using System.Text.Json;
using System.Text.Json.Nodes;

namespace BoothShelf;

internal sealed partial class ShelfWindow
{
    private async Task<JsonObject> SynchronizeAllAsync(string mode)
    {
        if (syncing) throw new InvalidOperationException("同步正在进行，请稍后重试。");
        if (mode is not ("full" or "quick")) throw new InvalidDataException("同步模式无效");
        syncing = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        syncCancellation = cancellation;
        var token = cancellation.Token;
        var baseline = ReadState()["snapshot"]?.DeepClone() as JsonObject;
        try
        {
            await PauseUpdateChecksAsync();
            Reply(new { type = "updates", state = NotificationState() });
            using var session = new AccountSession();
            await session.InitializeAsync(environment!, token);
            await ObserveSessionAuthenticationAsync(true);
            Reply(new { type = "auth", authenticated = true });
            var sections = await LibraryCoordinator.SynchronizeAsync(Program.DataDirectory, baseline, mode,
                async (url, number, pageToken) => (await session.ReadAsync(url + "?page=" + number, "library", pageToken)).Deserialize<LibraryPage>(SyncCache.JsonOptions)!,
                (items, pageToken) => metadata.EnrichAsync(items, pageToken),
                (label, page, total, count, status, seconds) =>
                    Reply(new { type = "booth-progress", page, total, count, status = label + " · " + status, remainingSeconds = seconds }), token);
            var resultSnapshot = LibraryCoordinator.Combine(sections, mode, true);
            var previousBought = LibraryCoordinator.Baseline(baseline, "bought")["items"]!.AsArray().Select(item => item!["id"]!.GetValue<string>()).ToHashSet();
            var addedBought = sections["bought"]!["items"]!.AsArray().Select(item => item!["id"]!.GetValue<string>()).Where(id => !previousBought.Contains(id)).ToHashSet();
            if (mode == "full" || addedBought.Count > 0)
            {
                Reply(new { type = "booth-progress", page = 0, total = 1, count = 0, status = mode == "full" ? "核对历史订单成交价" : "读取新增资产成交价" });
                await purchases.SyncAsync(session.ReadAsync, (page, total) =>
                    Reply(new { type = "booth-progress", page, total, count = 0, status = mode == "full" ? "并行核对订单成交价 · 已缓存订单直接复用" : "读取新增资产成交价" }), token,
                    mode == "quick" ? addedBought : null);
            }
            purchases.Apply(resultSnapshot);
            metadata.Merge(resultSnapshot);
            var rates = await currencyScheduler.RefreshAsync(false, token);
            Reply(new { type = "currency", rates });
            token.ThrowIfCancellationRequested();
            await storageLock.WaitAsync(token);
            try
            {
                var state = ReadState();
                state["snapshot"] = resultSnapshot.DeepClone();
                WriteState(state);
                updateMonitor.SetLibrary(resultSnapshot);
                foreach (var section in LibraryCoordinator.Sections) SyncCache.Load(Path.Combine(Program.DataDirectory, "sections", section.Id))?.Finish();
            }
            finally { storageLock.Release(); }
            await SaveLoginSessionAsync(token);
            return resultSnapshot;
        }
        finally { syncing = false; syncCancellation = null; Reply(new { type = "updates", state = NotificationState() }); }
    }

}
