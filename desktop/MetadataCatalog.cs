using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BoothShelf;

internal sealed class MetadataCatalog : IDisposable
{
    private readonly string cachePath;
    private readonly HttpClient client;
    private readonly Dictionary<string, JsonObject> records = new();
    private readonly Dictionary<string, Task<JsonObject>> requests = new();
    private readonly RequestGate requestsGate = new(12);
    private readonly BoothRequestPolicy? access;
    private readonly object gate = new();

    internal MetadataCatalog(string root, HttpMessageHandler? handler = null, BoothRequestPolicy? access = null)
    {
        this.access = access;
        cachePath = Path.Combine(root, "product-metadata.jsonl");
        client = handler == null ? new HttpClient() : new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BOOTH-Shelf/" + Program.AppVersion.ToString(3));
        if (File.Exists(cachePath))
            foreach (var line in File.ReadLines(cachePath))
                try
                {
                    var record = JsonNode.Parse(line)?.AsObject();
                    if (record?["id"]?.GetValue<string>() is string id) records[id] = record;
                }
                catch (System.Text.Json.JsonException) { }
    }

    internal static JsonObject Normalize(JsonObject data, string id)
    {
        if (data["id"]?.ToString() != id || data["tags"] is not JsonArray)
            throw new InvalidDataException("商品详情不符合预期格式");
        var category = data["category"];
        var tags = data["tags"]!.AsArray().Select(tag => tag?["name"]?.GetValue<string>()?.Trim())
            .Where(tag => !string.IsNullOrEmpty(tag)).Distinct().Select(tag => (JsonNode?)JsonValue.Create(tag)).ToArray();
        var forms = data["variations"]?.AsArray().Select(variation => variation?["type"]?.GetValue<string>())
            .Where(form => !string.IsNullOrEmpty(form)).Distinct().Select(form => (JsonNode?)JsonValue.Create(form)).ToArray() ?? [];
        var events = data["events"] is JsonArray eventList
            ? eventList.Select(item => item?["name"]?.GetValue<string>()).Where(name => !string.IsNullOrEmpty(name)).Distinct().Select(name => (JsonNode?)JsonValue.Create(name)).ToArray() : [];
        return new JsonObject
        {
            ["id"] = id, ["tags"] = new JsonArray(tags), ["category"] = category?["parent"]?["name"]?.DeepClone() ?? category?["name"]?.DeepClone(),
            ["subcategory"] = category?["parent"] != null ? category?["name"]?.DeepClone() : null,
            ["events"] = new JsonArray(events), ["forms"] = new JsonArray(forms), ["adult"] = data["is_adult"]?.DeepClone(),
            ["shopUrl"] = data["shop"]?["url"]?.DeepClone(), ["shopImage"] = data["shop"]?["thumbnail_url"]?.DeepClone(),
            ["metadataStatus"] = "ok", ["metadataAt"] = DateTimeOffset.UtcNow.ToString("O")
        };
    }

    internal void Merge(JsonObject? snapshot)
    {
        if (snapshot?["items"] is not JsonArray items) return;
        lock (gate)
            foreach (var item in items.OfType<JsonObject>())
                if (records.TryGetValue(item["id"]!.GetValue<string>(), out var data)) Apply(item, data);
    }

    private static void Apply(JsonObject item, JsonObject data)
    {
        foreach (var field in data)
            if (field.Key != "id" && field.Value != null) item[field.Key] = field.Value.DeepClone();
    }

    internal async Task EnrichAsync(JsonArray items, CancellationToken token)
    {
        await Task.WhenAll(items.OfType<JsonObject>().Select(async item =>
        {
            var id = item["id"]!.GetValue<string>();
            if (!Regex.IsMatch(id, @"^\d{1,20}$")) throw new InvalidDataException("商品 ID 无效");
            Task<JsonObject> task;
            lock (gate)
            {
                if (records.TryGetValue(id, out var cached)
                    && DateTimeOffset.TryParse(cached["metadataAt"]?.GetValue<string>(), out var cachedAt)
                    && DateTimeOffset.UtcNow - cachedAt < TimeSpan.FromDays(7))
                { Apply(item, cached); return; }
                if (!requests.TryGetValue(id, out task!)) requests[id] = task = FetchCachedAsync(id, token);
            }
            try { Apply(item, await task); }
            finally { lock (gate) { if (task.IsCompleted && requests.GetValueOrDefault(id) == task) requests.Remove(id); } }
        }));
    }

    private async Task<JsonObject> FetchCachedAsync(string id, CancellationToken token)
    {
        var record = await ReadAsync(id, token);
        lock (gate)
        {
            if (record["metadataStatus"]?.GetValue<string>() == "unavailable" && records.TryGetValue(id, out var prior))
            {
                var retained = (JsonObject)prior.DeepClone();
                retained["metadataAt"] = record["metadataAt"]?.DeepClone();
                retained["metadataStatus"] = "unavailable";
                record = retained;
            }
            File.AppendAllText(cachePath, record.ToJsonString() + "\n", new UTF8Encoding(false));
            records[id] = record;
        }
        return record;
    }

    private async Task<JsonObject> ReadAsync(string id, CancellationToken token)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            HttpResponseMessage response;
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://booth.pm/ja/items/{id}.json");
            try { response = await requestsGate.RunAsync(() => access == null ? client.SendAsync(request, token) : access.SendAsync(client, request, token), token); }
            catch (Exception error) when (attempt < 3 && !token.IsCancellationRequested && error is HttpRequestException or TaskCanceledException)
            { requestsGate.Backoff(2000 * (attempt + 1)); continue; }
            using (response)
            {
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.Forbidden)
                    return new JsonObject { ["id"] = id, ["tags"] = new JsonArray(), ["metadataStatus"] = "unavailable", ["metadataAt"] = DateTimeOffset.UtcNow.ToString("O") };
                if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500)
                {
                    var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow)
                        ?? TimeSpan.FromSeconds(5 * (1 << attempt));
                    requestsGate.Backoff((int)Math.Clamp(Math.Ceiling(delay.TotalMilliseconds), 1000, int.MaxValue));
                    continue;
                }
                if (!response.IsSuccessStatusCode) throw new IOException($"商品标签返回 HTTP {(int)response.StatusCode}，请稍后继续同步。");
                return Normalize(JsonNode.Parse(await response.Content.ReadAsStringAsync(token))!.AsObject(), id);
            }
        }
        throw new IOException("BOOTH 暂时限流，标签进度已缓存，请稍后继续同步。");
    }

    public void Dispose() => client.Dispose();
}
