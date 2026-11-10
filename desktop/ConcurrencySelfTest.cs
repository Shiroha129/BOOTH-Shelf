using System.Text.Json.Nodes;

namespace BoothShelf;

internal static class ConcurrencySelfTest
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static async Task<JsonObject> RequestsAsync()
    {
        var gate = new RequestGate(8);
        var active = 0;
        var peak = 0;
        var observed = new object();
        async Task<int> Read()
        {
            var count = Interlocked.Increment(ref active);
            lock (observed) peak = Math.Max(peak, count);
            try { await Task.Delay(15); return count; }
            finally { Interlocked.Decrement(ref active); }
        }
        await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => gate.RunAsync(Read, CancellationToken.None)));
        Check(peak == 8, "请求总并发未限制为 8");
        gate.Backoff(0);
        peak = 0;
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => gate.RunAsync(Read, CancellationToken.None)));
        Check(peak == 4, "限流后没有降低并发");
        var instant = DateTimeOffset.UtcNow;
        var waits = new List<int>();
        var cooling = new RequestGate(8, (milliseconds, token) =>
        {
            token.ThrowIfCancellationRequested();
            waits.Add(milliseconds);
            instant = instant.AddMilliseconds(milliseconds);
            return Task.CompletedTask;
        }, () => instant);
        cooling.Backoff(12000);
        await cooling.RunAsync(() => Task.FromResult(true), CancellationToken.None);
        Check(waits.SequenceEqual(new[] { 12000 }), "未遵守共享 Retry-After 等待");
        var single = new RequestGate(1);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = single.RunAsync(() => release.Task, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var started = false;
        var queued = single.RunAsync(() => { started = true; return Task.FromResult(2); }, cancellation.Token);
        cancellation.Cancel();
        var canceled = false;
        try { await queued; } catch (OperationCanceledException) { canceled = true; }
        release.SetResult(1);
        await first;
        Check(canceled && !started, "排队取消仍发出了请求");
        try { await single.RunAsync<int>(() => throw new IOException("synthetic"), CancellationToken.None); } catch (IOException) { }
        Check(await single.RunAsync(() => Task.FromResult(3), CancellationToken.None) == 3, "失败后没有释放请求名额");
        return new JsonObject { ["peak"] = 8, ["throttledPeak"] = 4, ["sharedRetryAfter"] = true, ["queuedCancellation"] = true, ["failureReleasesSlot"] = true };
    }

    internal static async Task<JsonObject> PurchasesAsync(string root)
    {
        var pageActive = 0;
        var detailActive = 0;
        var pagePeak = 0;
        var detailPeak = 0;
        var detailCalls = new Dictionary<string, int>();
        var pages = new List<int>();
        var failOne = false;
        var gate = new object();
        async Task<JsonObject> Read(string url, string kind, CancellationToken token)
        {
            if (kind == "orders")
            {
                var number = int.Parse(new Uri(url).Query.Split('=').Last());
                var count = Interlocked.Increment(ref pageActive);
                lock (gate) { pagePeak = Math.Max(pagePeak, count); pages.Add(number); }
                try
                {
                    await Task.Delay(number % 2 == 0 ? 18 : 8, token);
                    return new JsonObject { ["page"] = number, ["maxPage"] = 12,
                        ["ids"] = new JsonArray((number * 10 + 1).ToString(), (number * 10 + 2).ToString(), "11") };
                }
                finally { Interlocked.Decrement(ref pageActive); }
            }
            var id = new Uri(url).Segments.Last();
            var details = Interlocked.Increment(ref detailActive);
            lock (gate) { detailPeak = Math.Max(detailPeak, details); detailCalls[id] = detailCalls.GetValueOrDefault(id) + 1; }
            try
            {
                await Task.Delay(int.Parse(id) % 2 == 0 ? 18 : 8, token);
                if (failOne && id == "32") { failOne = false; throw new IOException("synthetic receipt failure"); }
                return new JsonObject { ["id"] = id, ["paid"] = true, ["total"] = decimal.Parse(id),
                    ["lines"] = new JsonArray(new JsonObject { ["itemId"] = id, ["price"] = decimal.Parse(id), ["boost"] = 0m }) };
            }
            finally { Interlocked.Decrement(ref detailActive); }
        }
        var catalog = new PurchaseCatalog(root);
        await catalog.SyncAsync(Read, (_, _) => { }, CancellationToken.None);
        var snapshot = new JsonObject { ["items"] = new JsonArray() };
        catalog.Apply(snapshot);
        var total = Enumerable.Range(1, 12).Sum(number => number * 20m + 3);
        Check(pagePeak == 4 && detailPeak == 8 && pages.Order().SequenceEqual(Enumerable.Range(1, 12)), "订单页未并发读取或漏页、越界");
        Check(detailCalls.Count == 24 && detailCalls.Values.All(count => count == 1)
            && snapshot["spend"]!["orders"]!.GetValue<int>() == 24 && snapshot["spend"]!["totalJpy"]!.GetValue<decimal>() == total, "并发返回乱序或跨页重复导致金额错误");
        pages.Clear();
        detailCalls.Clear();
        await new PurchaseCatalog(root).SyncAsync(Read, (_, _) => { }, CancellationToken.None);
        Check(pages.Count == 12 && detailCalls.Count == 0, "并发扫描没有复用已支付订单缓存");
        var failureRoot = Path.Combine(root, "failure");
        var partial = new PurchaseCatalog(failureRoot);
        failOne = true;
        var failed = false;
        try { await partial.SyncAsync(Read, (_, _) => { }, CancellationToken.None); } catch (IOException) { failed = true; }
        Check(failed && Directory.GetFiles(Path.Combine(failureRoot, "purchase-cache"), "*.json").Length == 9, "订单批次失败时没有保存其他成功详情");
        var resumed = new PurchaseCatalog(failureRoot);
        await resumed.SyncAsync(Read, (_, _) => { }, CancellationToken.None);
        resumed.Apply(snapshot);
        Check(detailCalls.Count == 24 && detailCalls.All(pair => pair.Value == (pair.Key == "32" ? 2 : 1))
            && snapshot["spend"]!["totalJpy"]!.GetValue<decimal>() == total, "订单失败续传重读成功记录或重复计费");
        return new JsonObject { ["pageConcurrency"] = pagePeak, ["detailConcurrency"] = detailPeak,
            ["unorderedCompletionTotals"] = true, ["deduplicatedOrders"] = true, ["cachedDetailsReused"] = true, ["failedBatchResumes"] = true };
    }
}
