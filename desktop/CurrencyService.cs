using System.Net.Http;
using System.Text.Json.Nodes;

namespace BoothShelf;

internal sealed class CurrencyService : IDisposable
{
    internal static readonly string[] Currencies = ["JPY", "CNY", "USD", "EUR", "HKD", "KRW", "GBP", "AUD", "CAD", "CHF", "SGD"];
    private readonly string path;
    private readonly HttpClient client;
    internal JsonObject Rates { get; private set; }

    internal CurrencyService(string root, HttpMessageHandler? handler = null)
    {
        client = handler == null ? new HttpClient() : new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(25);
        path = Path.Combine(root, "exchange-rates.json");
        Rates = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new JsonObject();
    }

    internal async Task<JsonObject> RefreshAsync(bool force, CancellationToken token)
    {
        if (!force && DateTimeOffset.TryParse(Rates["fetchedAt"]?.GetValue<string>(), out var fetched)
            && DateTimeOffset.UtcNow - fetched < TimeSpan.FromHours(24)) return (JsonObject)Rates.DeepClone();
        try
        {
            var response = JsonNode.Parse(await client.GetStringAsync("https://api.frankfurter.dev/v1/latest?base=JPY", token))!.AsObject();
            if (response["base"]?.GetValue<string>() != "JPY" || response["rates"] is not JsonObject values
                || !DateTime.TryParse(response["date"]?.GetValue<string>(), out _))
                throw new InvalidDataException("汇率服务返回无效数据");
            var selected = new JsonObject { ["JPY"] = 1m };
            foreach (var currency in Currencies.Where(value => value != "JPY"))
            {
                var rate = values[currency]?.GetValue<decimal>();
                if (rate is > 0 and < 100000) selected[currency] = rate;
            }
            var updated = new JsonObject { ["base"] = "JPY", ["date"] = response["date"]!.DeepClone(),
                ["rates"] = selected, ["source"] = "Frankfurter / ECB", ["fetchedAt"] = DateTimeOffset.UtcNow.ToString("O") };
            SyncCache.AtomicWrite(path, updated.ToJsonString());
            Rates = updated;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            var failed = (JsonObject)Rates.DeepClone();
            failed["error"] = "汇率更新失败，已保留缓存：" + error.Message;
            return failed;
        }
        return (JsonObject)Rates.DeepClone();
    }

    public void Dispose() => client.Dispose();
}
