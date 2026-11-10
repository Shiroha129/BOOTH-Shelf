using System.Text.Json;
using System.Text.Json.Nodes;

namespace BoothShelf;

internal sealed partial class UpdateMonitor
{
    internal async Task<bool> CheckBatchAsync(Func<string, CancellationToken, Task<JsonObject>> readLibrary, CancellationToken token)
    {
        if (Busy || targets.Count == 0 || RetryAfter > now()) return false;
        var due = LibraryCoordinator.Sections.Where(section => Date(sections[section.Id]?["nextAt"]) <= now()).ToArray();
        if (due.Length == 0) return false;
        Busy = true;
        var before = notificationRevision;
        var metadata = new Dictionary<string, MetadataResult>();
        Progress?.Invoke();
        try
        {
            foreach (var section in due)
            {
                token.ThrowIfCancellationRequested();
                sections[section.Id] ??= new JsonObject();
                var state = sections[section.Id]!.AsObject();
                state["pages"] = 0;
                state["checked"] = 0;
                state["unavailable"] = 0;
                state["ownership"] = 0;
                try { await ScanSectionAsync(section.Url, state, readLibrary, metadata, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is SyncReadException or IOException or InvalidDataException or InvalidOperationException or JsonException or FormatException)
                {
                    state["error"] = true;
                    state["nextAt"] = now().AddMinutes(5).ToString("O");
                    if (error is SyncReadException { Retryable: true } readError)
                    {
                        var retry = now().AddMilliseconds(Math.Max(300000, readError.RetryAfterMs));
                        if (retry > RetryAfter) schedule["retryAt"] = retry.ToString("O");
                    }
                }
                SaveSchedule();
                Progress?.Invoke();
                if (RetryAfter > now()) break;
            }
            if (journalEntries > Math.Max(2000, records.Count * 2) && new FileInfo(historyPath).Length > 64 * 1024 * 1024)
            {
                SyncCache.AtomicWrite(historyPath, string.Join("\n", records.Values.Select(record => record.ToJsonString())) + "\n");
                journalEntries = records.Count;
            }
        }
        finally
        {
            Busy = false;
            schedule["manualAfter"] = now().AddSeconds(30).ToString("O");
            SaveSchedule();
            if (notificationRevision > before) NewNotifications?.Invoke(notificationRevision - before);
        }
        return true;
    }

    private async Task ScanSectionAsync(string url, JsonObject state, Func<string, CancellationToken, Task<JsonObject>> readLibrary,
        Dictionary<string, MetadataResult> metadata, CancellationToken token)
    {
        var previous = Date(state["through"] ?? state["baselineAt"]);
        var cutoff = DateTimeOffset.MinValue;
        var started = DateTimeOffset.MinValue;
        var seenPages = new HashSet<string>();
        var checkedIds = new HashSet<string>();
        var failed = new HashSet<string>();
        for (var number = 1; ; number++)
        {
            token.ThrowIfCancellationRequested();
            var data = await readLibrary(url + "?sort=file_updated_at_desc&page=" + number, token);
            var page = data.Deserialize<LibraryPage>(SyncCache.JsonOptions) ?? throw new InvalidDataException("资料库页面无效");
            page.Validate(number);
            if (!Uri.TryCreate(data["sourceUrl"]?.ToString(), UriKind.Absolute, out var source) || source.GetLeftPart(UriPartial.Path) != url
                || data["sort"]?.ToString() != "file_updated_at_desc" || data["searchPage"]?.GetValue<int>() != number
                || data["unfiltered"]?.GetValue<bool>() != true || !seenPages.Add(page.Fingerprint))
                throw new InvalidDataException("更新排序或分页未确认");
            if (number == 1)
            {
                started = Date(data["serverDate"]);
                if (started == DateTimeOffset.MinValue) throw new InvalidDataException("无法确认网站时间");
                state["baselineAt"] ??= JsonValue.Create(started.ToString("O"));
                cutoff = (previous == DateTimeOffset.MinValue ? started : previous).AddMinutes(-10);
                foreach (var member in members.Select(entry => entry.Value).OfType<JsonObject>())
                {
                    member["since"] ??= JsonValue.Create(started.ToString("O"));
                    foreach (var version in member["versions"]!.AsObject().ToArray())
                        if (version.Value == null) member["versions"]![version.Key] = started.ToString("O");
                }
                SaveSchedule();
            }
            state["pages"] = number;
            var rows = page.Items.OfType<JsonObject>().GroupBy(item => item["id"]!.ToString()).Select(group =>
            {
                var row = (JsonObject)group.First().DeepClone();
                foreach (var item in group.Skip(1)) LibraryOwnership.Merge(row, item);
                return row;
            }).ToArray();
            if (rows.Any(row => !ValidId(row["id"]!.ToString()))) throw new InvalidDataException("商品 ID 无效");
            var reachedBoundary = false;
            var dated = false;
            foreach (var batch in rows.Chunk(4))
            {
                var pending = batch.Where(row => !metadata.TryGetValue(row["id"]!.ToString(), out var cached)
                    || cached.Unchanged && row["section"]?.ToString() == "free").ToArray();
                var responses = await Task.WhenAll(pending.Select(row => FetchAsync(row, token)));
                foreach (var response in responses) metadata[response.Id] = response;
                foreach (var row in batch)
                {
                    var id = row["id"]!.ToString();
                    var result = metadata[id];
                    var latest = ApplyMetadata(result, row, started);
                    checkedIds.Add(id);
                    if (result.Failed || records.GetValueOrDefault(id)?["status"]?.ToString() is "unavailable" or "ownership") failed.Add(id);
                    if (latest != DateTimeOffset.MinValue) dated = true;
                    if (latest != DateTimeOffset.MinValue && latest < cutoff) reachedBoundary = true;
                }
                state["checked"] = checkedIds.Count;
                state["unavailable"] = failed.Count;
                state["ownership"] = failed.Count(id => records.GetValueOrDefault(id)?["status"]?.ToString() == "ownership");
                Progress?.Invoke();
                if (RetryAfter > now()) throw new InvalidOperationException("暂时限流");
            }
            if (reachedBoundary || (page.MaxPage <= number && page.Next == null)) break;
            if (!dated || page.Next == null) throw new InvalidDataException("更新排序分页不完整");
        }
        state["error"] = failed.Count > 0;
        state["nextAt"] = now().AddMinutes(failed.Count > 0 ? 5 : 15).ToString("O");
        if (failed.Count == 0)
        {
            state["through"] = (started > previous ? started : previous).ToString("O");
            state["lastAt"] = now().ToString("O");
        }
    }
}
