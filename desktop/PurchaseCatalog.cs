using System.Text.Json.Nodes;

namespace BoothShelf;

internal sealed class PurchaseCatalog
{
    private readonly string root;
    private readonly string statePath;
    private readonly Dictionary<string, JsonObject> receipts = new();
    private bool complete;

    internal PurchaseCatalog(string dataRoot)
    {
        root = Path.Combine(dataRoot, "purchase-cache");
        statePath = Path.Combine(dataRoot, "purchase-state.json");
        complete = File.Exists(statePath) && JsonNode.Parse(File.ReadAllText(statePath))?["complete"]?.GetValue<bool>() == true;
        Directory.CreateDirectory(root);
        foreach (var file in Directory.EnumerateFiles(root, "*.json"))
        {
            var receipt = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            receipts[receipt["id"]!.GetValue<string>()] = receipt;
        }
    }

    internal async Task SyncAsync(Func<string, string, CancellationToken, Task<JsonObject>> read, Action<int, int> progress,
        CancellationToken token, IReadOnlySet<string>? requiredItems = null)
    {
        var remaining = requiredItems?.ToHashSet();
        void Resolve(JsonObject receipt)
        {
            if (receipt["paid"]?.GetValue<bool>() == true)
                foreach (var line in receipt["lines"]!.AsArray()) remaining?.Remove(line!["itemId"]!.GetValue<string>());
        }
        foreach (var receipt in receipts.Values) Resolve(receipt);
        token.ThrowIfCancellationRequested();
        if (remaining?.Count == 0) return;
        var page = 1;
        var totalPages = 1;
        var seen = new HashSet<string>();
        var fingerprints = new HashSet<string>();
        do
        {
            token.ThrowIfCancellationRequested();
            var numbers = Enumerable.Range(page, Math.Min(requiredItems == null ? 4 : 1, totalPages - page + 1)).ToArray();
            var batches = await Task.WhenAll(numbers.Select(async number =>
            {
                var batch = await read("https://accounts.booth.pm/orders?page=" + number, "orders", token);
                if (batch["page"]!.GetValue<int>() != number) throw new InvalidDataException("订单分页返回了其他页。");
                return batch;
            }));
            var ids = new List<string>();
            foreach (var batch in batches)
            {
                if (!fingerprints.Add(batch["ids"]!.ToJsonString())) throw new InvalidDataException("订单列表重复返回同一页，已暂停。");
                totalPages = Math.Max(totalPages, batch["maxPage"]!.GetValue<int>());
                ids.AddRange(batch["ids"]!.AsArray().Select(value => value!.GetValue<string>()).Where(seen.Add));
            }
            foreach (var group in ids.Where(id => !receipts.TryGetValue(id, out var record) || record["paid"]?.GetValue<bool>() != true
                || !DateTimeOffset.TryParse(record["checkedAt"]?.GetValue<string>(), out var checkedAt) || DateTimeOffset.UtcNow - checkedAt > TimeSpan.FromDays(30)).Chunk(8))
            {
                var results = await Task.WhenAll(group.Select(async id =>
                {
                    try
                    {
                        var record = await read("https://accounts.booth.pm/orders/" + id, "receipt", token);
                        if (record["id"]?.GetValue<string>() != id) throw new InvalidDataException("订单详情返回了其他订单。");
                        record["checkedAt"] = DateTimeOffset.UtcNow.ToString("O");
                        return (Record: record, Error: (Exception?)null);
                    }
                    catch (Exception error) { return (Record: (JsonObject?)null, Error: error); }
                }));
                foreach (var record in results.Where(result => result.Record != null).Select(result => result.Record!))
                {
                    var id = record["id"]!.GetValue<string>();
                    SyncCache.AtomicWrite(Path.Combine(root, id + ".json"), record.ToJsonString());
                    receipts[id] = record;
                    Resolve(record);
                }
                var failure = results.FirstOrDefault(result => result.Error != null).Error;
                if (failure != null) throw failure;
                token.ThrowIfCancellationRequested();
                progress(page - 1, totalPages);
                if (remaining?.Count == 0) return;
            }
            page += numbers.Length;
            progress(page - 1, totalPages);
        } while (page <= totalPages);
        token.ThrowIfCancellationRequested();
        complete = true;
        SyncCache.AtomicWrite(statePath, new JsonObject { ["complete"] = true, ["checkedAt"] = DateTimeOffset.UtcNow.ToString("O") }.ToJsonString());
    }

    internal void Apply(JsonObject snapshot)
    {
        var items = snapshot["items"]!.AsArray().OfType<JsonObject>().ToDictionary(item => item["id"]!.GetValue<string>());
        var recordedIds = receipts.Values.SelectMany(receipt => receipt["lines"]!.AsArray()).Select(line => line!["itemId"]!.GetValue<string>()).ToHashSet();
        foreach (var item in items.Values.Where(item => recordedIds.Contains(item["id"]!.GetValue<string>())))
        {
            item.Remove("purchaseJpy");
            item.Remove("purchaseCount");
            item.Remove("purchasedAt");
        }
        decimal total = 0;
        var count = 0;
        foreach (var receipt in receipts.Values.Where(record => record["paid"]?.GetValue<bool>() == true))
        {
            total += receipt["total"]!.GetValue<decimal>();
            count++;
            foreach (var line in receipt["lines"]!.AsArray())
            {
                if (!items.TryGetValue(line!["itemId"]!.GetValue<string>(), out var item)) continue;
                item["purchaseJpy"] = (item["purchaseJpy"]?.GetValue<decimal>() ?? 0) + line["price"]!.GetValue<decimal>() + (line["boost"]?.GetValue<decimal>() ?? 0);
                item["purchaseCount"] = (item["purchaseCount"]?.GetValue<int>() ?? 0) + 1;
                var date = receipt["createdAt"]?.GetValue<string>();
                if (string.CompareOrdinal(date, item["purchasedAt"]?.GetValue<string>()) > 0) item["purchasedAt"] = date;
            }
        }
        snapshot["spend"] = new JsonObject { ["totalJpy"] = total, ["orders"] = count, ["complete"] = complete,
            ["pricedAssets"] = items.Values.Count(item => item["purchaseJpy"] != null), ["checkedAt"] = DateTimeOffset.UtcNow.ToString("O") };
    }
}
