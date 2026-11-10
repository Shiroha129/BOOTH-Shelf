using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BoothShelf;

internal sealed class BoothAccessPausedException() : IOException("BOOTH 自动访问已暂停，请到设置中查看并手动继续。");

internal sealed class BoothRequestPolicy
{
    private readonly RequestGate requests;
    private readonly Func<DateTimeOffset> now;
    private readonly string? statePath;
    private readonly object gate = new();
    private string? reason;
    private DateTimeOffset retryAt;
    internal event Action? Changed;
    internal bool Paused { get { lock (gate) return reason != null; } }
    internal const string ChallengeScript = "Boolean(/just a moment|attention required|security verification|checking your browser/i.test(document.title) || document.querySelector('#challenge-form, #cf-challenge-running, script[src*=\"/cdn-cgi/challenge-platform/\"]'))";

    internal BoothRequestPolicy(string? root = null, int intervalMilliseconds = 250, Func<DateTimeOffset>? clock = null,
        Func<int, CancellationToken, Task>? delay = null)
    {
        now = clock ?? (() => DateTimeOffset.UtcNow);
        requests = new RequestGate(4, delay, now, intervalMilliseconds);
        statePath = root == null ? null : Path.Combine(root, "booth-access.json");
        if (statePath != null && File.Exists(statePath))
        {
            var state = JsonNode.Parse(File.ReadAllText(statePath));
            reason = state?["reason"]?.ToString();
            if (DateTimeOffset.TryParse(state?["retryAt"]?.ToString(), out var saved)) retryAt = saved;
        }
    }

    internal JsonObject State()
    {
        lock (gate) return new JsonObject
        {
            ["paused"] = reason != null, ["reason"] = reason,
            ["retryAt"] = reason != null && retryAt > now() ? retryAt.ToString("O") : null
        };
    }

    internal void ThrowIfPaused()
    {
        if (Paused) throw new BoothAccessPausedException();
    }

    internal Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken token)
    {
        ThrowIfPaused();
        return requests.RunAsync(() => { ThrowIfPaused(); return action(); }, token);
    }

    internal void Inspect(int status, bool challenge = false, TimeSpan? retry = null)
    {
        if (status is not (403 or 429) && !challenge) return;
        lock (gate)
        {
            reason = status == 429 ? "rate-limit" : "verification";
            var minimum = TimeSpan.FromMinutes(status == 429 ? 10 : 5);
            var until = now() + (retry > minimum ? retry.Value : minimum);
            if (until > retryAt) retryAt = until;
            Save();
        }
        Changed?.Invoke();
        throw new BoothAccessPausedException();
    }

    internal JsonObject Resume()
    {
        lock (gate)
        {
            if (reason != null && retryAt > now()) throw new InvalidOperationException("请等待访问暂停提示中的时间结束后再继续。");
            reason = null;
            retryAt = default;
            Save();
        }
        Changed?.Invoke();
        return State();
    }

    private void Save()
    {
        if (statePath != null) SyncCache.AtomicWrite(statePath, new JsonObject
        {
            ["reason"] = reason, ["retryAt"] = reason == null ? null : retryAt.ToString("O")
        }.ToJsonString());
    }

    internal static bool IsChallenge(string html) => Regex.IsMatch(html,
        "<title[^>]*>\\s*(?:just a moment|attention required|security verification|checking your browser)|(?:id=[\"'](?:challenge-form|cf-challenge-running)[\"'])|<script[^>]+/cdn-cgi/challenge-platform/",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken token) => RunAsync(async () =>
    {
        var response = await client.SendAsync(request, token);
        try
        {
            var challenge = response.Headers.TryGetValues("cf-mitigated", out var mitigation) && mitigation.Contains("challenge");
            if (response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true)
                challenge |= IsChallenge(await response.Content.ReadAsStringAsync(token));
            Inspect((int)response.StatusCode, challenge, response.Headers.RetryAfter?.Delta ?? response.Headers.RetryAfter?.Date - now());
            return response;
        }
        catch { response.Dispose(); throw; }
    }, token);
}
