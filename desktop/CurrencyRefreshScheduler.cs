using System.Text.Json.Nodes;

namespace BoothShelf;

internal sealed class CurrencyRefreshScheduler(CurrencyService currency, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.Now);
    private DateOnly? attemptedDate;
    private DateTimeOffset? retryAt;
    private Task<JsonObject>? refresh;
    internal JsonObject Current { get; private set; } = (JsonObject)currency.Rates.DeepClone();
    internal event Action<JsonObject>? Refreshed;

    internal bool IsDue => attemptedDate != DateOnly.FromDateTime(now().DateTime) || retryAt is { } retry && now() >= retry;

    internal int NextDelayMilliseconds
    {
        get
        {
            if (IsDue) return 100;
            var current = now();
            var delay = current.Date.AddDays(1) - current.DateTime;
            if (retryAt is { } retry && retry - current < delay) delay = retry - current;
            return (int)Math.Clamp(delay.TotalMilliseconds, 100, 60000);
        }
    }

    internal Task<JsonObject> RefreshAsync(bool force, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (refresh is { IsCompleted: false }) return refresh;
        if (!force && !IsDue) return Task.FromResult((JsonObject)Current.DeepClone());
        return refresh = FetchAsync(token);
    }

    private async Task<JsonObject> FetchAsync(CancellationToken token)
    {
        var started = now();
        var result = await currency.RefreshAsync(true, token);
        token.ThrowIfCancellationRequested();
        attemptedDate = DateOnly.FromDateTime(started.DateTime);
        retryAt = result["error"] == null ? null : now().AddMinutes(15);
        Current = (JsonObject)result.DeepClone();
        Refreshed?.Invoke((JsonObject)result.DeepClone());
        return result;
    }
}
