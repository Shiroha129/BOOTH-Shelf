using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BoothShelf;

internal sealed partial class UpdateMonitor : IDisposable
{
    private readonly string historyPath;
    private readonly string noticesPath;
    private readonly string schedulePath;
    private readonly HttpClient client;
    private readonly Func<DateTimeOffset> now;
    private readonly Dictionary<string, JsonObject> records = new();
    private Dictionary<string, JsonObject> targets = new();
    private readonly JsonObject schedule;
    private readonly JsonObject sections;
    private readonly JsonObject members;
    private JsonArray notices;
    private int notificationRevision;
    private int journalEntries;
    internal bool Busy { get; private set; }
    internal event Action<int>? NewNotifications;
    internal event Action? Progress;

    internal UpdateMonitor(string root, HttpMessageHandler? handler = null, Func<DateTimeOffset>? clock = null)
    {
        now = clock ?? (() => DateTimeOffset.UtcNow);
        historyPath = Path.Combine(root, "update-monitor.jsonl");
        noticesPath = Path.Combine(root, "notifications.json");
        schedulePath = Path.Combine(root, "update-monitor-state.json");
        client = handler == null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) : new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(25);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BOOTH-Shelf/0.5.10");
        if (File.Exists(historyPath)) foreach (var line in File.ReadLines(historyPath))
        {
            journalEntries++;
            try
            {
                var record = JsonNode.Parse(line)!.AsObject();
                if (record["id"]?.ToString() is { } id && ValidId(id)) records[id] = record;
            }
            catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException) { }
        }
        notices = File.Exists(noticesPath) ? JsonNode.Parse(File.ReadAllText(noticesPath))!.AsArray() : new JsonArray();
        schedule = File.Exists(schedulePath) ? JsonNode.Parse(File.ReadAllText(schedulePath))!.AsObject() : new JsonObject();
        schedule["sections"] ??= new JsonObject();
        schedule["members"] ??= new JsonObject();
        sections = schedule["sections"]!.AsObject();
        members = schedule["members"]!.AsObject();
    }

    private static bool ValidId(string id) => Regex.IsMatch(id, @"^\d{1,20}$");
    private static DateTimeOffset Date(JsonNode? value) => DateTimeOffset.TryParse(value?.ToString(), out var date) ? date : DateTimeOffset.MinValue;
    private DateTimeOffset RetryAfter => Date(schedule["retryAt"]);
    private void SaveSchedule() => SyncCache.AtomicWrite(schedulePath, schedule.ToJsonString());

    internal void SetLibrary(JsonObject? snapshot)
    {
        targets = (snapshot?["items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Where(item => ValidId(item["id"]?.ToString() ?? ""))
            .GroupBy(item => item["id"]!.ToString()).ToDictionary(group => group.Key, group => (JsonObject)group.First().DeepClone());
        foreach (var entry in targets)
        {
            if (members[entry.Key] is not JsonObject) members[entry.Key] = new JsonObject { ["since"] = null, ["versions"] = new JsonObject() };
            var versions = members[entry.Key]!["versions"]!.AsObject();
            foreach (var version in LibraryOwnership.Versions(entry.Value))
                if (!versions.ContainsKey(version)) versions[version] = null;
        }
        foreach (var id in members.Select(entry => entry.Key).Where(id => !targets.ContainsKey(id)).ToArray()) members.Remove(id);
        SaveSchedule();
    }

    internal JsonObject State()
    {
        var states = LibraryCoordinator.Sections.Select(section => sections[section.Id] as JsonObject).ToArray();
        var last = states.Select(state => Date(state?["lastAt"])).Min();
        var next = states.Select(state => Date(state?["nextAt"])).Min();
        return new JsonObject
        {
            ["items"] = notices.DeepClone(), ["total"] = targets.Count, ["busy"] = Busy, ["mode"] = "incremental",
            ["sourcesReady"] = states.Count(state => Date(state?["through"]) != DateTimeOffset.MinValue),
            ["checked"] = states.Sum(state => state?["checked"]?.GetValue<int>() ?? 0),
            ["pages"] = states.Sum(state => state?["pages"]?.GetValue<int>() ?? 0),
            ["unavailable"] = states.Sum(state => state?["unavailable"]?.GetValue<int>() ?? 0),
            ["issue"] = states.Any(state => state?["error"]?.GetValue<bool>() == true),
            ["sources"] = new JsonArray(LibraryCoordinator.Sections.Select(section => (JsonNode)new JsonObject
            {
                ["id"] = section.Id, ["error"] = sections[section.Id]?["error"]?.DeepClone() ?? JsonValue.Create(false),
                ["ownership"] = sections[section.Id]?["ownership"]?.DeepClone() ?? JsonValue.Create(0)
            }).ToArray()),
            ["lastCheckedAt"] = last == DateTimeOffset.MinValue ? null : last.ToString("O"),
            ["nextAt"] = next > now() ? next.ToString("O") : null,
            ["retryAt"] = RetryAfter > now() ? RetryAfter.ToString("O") : null
        };
    }

    internal JsonObject MarkRead(string? id)
    {
        var updated = (JsonArray)notices.DeepClone();
        foreach (var notice in updated.OfType<JsonObject>()) if (id == null || notice["id"]?.ToString() == id) notice["read"] = true;
        SyncCache.AtomicWrite(noticesPath, updated.ToJsonString());
        notices = updated;
        return State();
    }

    internal void RequestCheck()
    {
        if (Busy || Date(schedule["manualAfter"]) > now() || RetryAfter > now()) return;
        foreach (var state in sections.Select(entry => entry.Value).OfType<JsonObject>()) state["nextAt"] = null;
        SaveSchedule();
    }

    private sealed record MetadataResult(string Id, JsonObject? Data = null, string? Etag = null, bool Unchanged = false,
        DateTimeOffset RetryAt = default, bool Failed = false);

    private async Task<MetadataResult> FetchAsync(JsonObject row, CancellationToken token)
    {
        var id = row["id"]!.ToString();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.booth.pm/vroid/items/" + id);
            var record = records.GetValueOrDefault(id);
            var owned = targets.TryGetValue(id, out var item) ? LibraryOwnership.Versions(item) : [];
            var scopeUnchanged = !targets.ContainsKey(id) || owned.Length > 0 && owned.SequenceEqual((record?["owned"] as JsonArray ?? new JsonArray()).Select(value => value!.ToString()));
            if (row["section"]?.ToString() != "free" && scopeUnchanged && record?["latestFileAt"] != null && record["etag"]?.ToString() is { Length: > 0 } etag)
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            using var response = await client.SendAsync(request, token);
            if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? response.Headers.RetryAfter?.Date - now() ?? TimeSpan.FromMinutes(5);
                return new(id, RetryAt: now() + (wait < TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : wait), Failed: true);
            }
            if (response.StatusCode == HttpStatusCode.NotModified && record?["latestFileAt"] != null) return new(id, Unchanged: true);
            response.EnsureSuccessStatusCode();
            var data = JsonNode.Parse(await response.Content.ReadAsStringAsync(token))!.AsObject();
            if (data["id"]?.ToString() != id || data["variations"] is not JsonArray) throw new InvalidDataException("文件信息无效");
            return new(id, Data: data, Etag: response.Headers.ETag?.ToString());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or TaskCanceledException)
        { return new(id, Failed: true); }
    }

    private DateTimeOffset ApplyMetadata(MetadataResult result, JsonObject row, DateTimeOffset observedAt)
    {
        var id = result.Id;
        var record = (JsonObject)(records.GetValueOrDefault(id)?.DeepClone() ?? new JsonObject { ["id"] = id });
        if (result.RetryAt > RetryAfter) { schedule["retryAt"] = result.RetryAt.ToString("O"); SaveSchedule(); }
        if (result.Failed)
        {
            record["status"] = "unavailable";
            record["etag"] = null;
            SaveRecord(record);
            return DateTimeOffset.MinValue;
        }
        if (result.Unchanged) return Date(record["latestFileAt"]);
        var allFiles = result.Data!["variations"]!.AsArray().OfType<JsonObject>()
            .SelectMany(variant => (variant["downloadables"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()).ToArray();
        var timestamps = allFiles.Select(file => Date(file["updated_at"])).ToArray();
        var latest = timestamps.Length > 0 && timestamps.All(date => date != DateTimeOffset.MinValue) ? timestamps.Max() : DateTimeOffset.MinValue;
        record["latestFileAt"] = latest == DateTimeOffset.MinValue ? null : latest.ToString("O");
        record["etag"] = null;
        record["status"] = "ok";
        if (targets.TryGetValue(id, out var item))
        {
            var owned = LibraryOwnership.Versions(item).Union(LibraryOwnership.Versions(row))
                .Union(members[id]!["versions"]!.AsObject().Select(entry => entry.Key))
                .Union((record["owned"] as JsonArray ?? new JsonArray()).Select(value => value?.ToString() ?? "").Where(ValidId)).Order().ToArray();
            var free = LibraryOwnership.FreeVersions(result.Data, row);
            owned = owned.Union(free).Order().ToArray();
            if (owned.Length == 0 || row["section"]?.ToString() == "free" && LibraryOwnership.Versions(row).Length == 0 && free.Length == 0) record["status"] = "ownership";
            else try
            {
                var versions = FileVersions(result.Data, id, owned);
                var previous = record["versions"] as JsonObject;
                var member = members[id]!.AsObject();
                var changed = new HashSet<string>();
                foreach (var variant in versions)
                {
                    var oldFiles = previous?[variant.Key] as JsonObject;
                    var since = Date(member["versions"]![variant.Key] ?? member["since"]);
                    foreach (var file in variant.Value!.AsObject())
                        if (oldFiles != null ? !JsonNode.DeepEquals(oldFiles[file.Key], file.Value) : Date(file.Value?["updatedAt"]) > since)
                            changed.Add(file.Key);
                }
                if (changed.Count > 0) AddNotice(item, versions, changed);
                record["versions"] = versions;
                record["owned"] = new JsonArray(owned.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray());
                foreach (var version in owned)
                    if (!member["versions"]!.AsObject().ContainsKey(version)) member["versions"]![version] = member["since"]?.DeepClone() ?? JsonValue.Create(observedAt.ToString("O"));
                record["etag"] = result.Etag;
            }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException or FormatException) { record["status"] = "unavailable"; }
        }
        else record["etag"] = result.Etag;
        SaveRecord(record);
        return latest;
    }

    private void AddNotice(JsonObject item, JsonObject versions, HashSet<string> changed)
    {
        var id = item["id"]!.ToString();
        var noticeId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id + versions.ToJsonString())));
        if (notices.OfType<JsonObject>().Any(notice => notice["id"]?.ToString() == noticeId)) return;
        var updated = (JsonArray)notices.DeepClone();
        updated.Insert(0, new JsonObject { ["id"] = noticeId, ["itemId"] = id, ["title"] = item["title"]?.DeepClone(),
            ["files"] = new JsonArray(changed.Order().Select(name => (JsonNode)JsonValue.Create(name)!).ToArray()), ["createdAt"] = now().ToString("O"), ["read"] = false });
        while (updated.Count > 1000) updated.RemoveAt(updated.Count - 1);
        SyncCache.AtomicWrite(noticesPath, updated.ToJsonString());
        notices = updated;
        notificationRevision++;
    }

    private void SaveRecord(JsonObject record)
    {
        record["checkedAt"] = now().ToString("O");
        File.AppendAllText(historyPath, record.ToJsonString() + "\n", new UTF8Encoding(false));
        journalEntries++;
        records[record["id"]!.ToString()] = record;
    }

    internal static JsonObject FileVersions(JsonObject data, string id, string[] owned)
    {
        if (data["id"]?.ToString() != id || data["variations"] is not JsonArray all) throw new InvalidDataException("文件信息无效");
        var versions = new JsonObject();
        foreach (var versionId in owned.Order())
        {
            var variant = all.OfType<JsonObject>().FirstOrDefault(value => value["id"]?.ToString() == versionId);
            if (variant?["downloadables"] is not JsonArray { Count: > 0 } downloads) throw new InvalidDataException("版本文件暂不可用");
            var files = new JsonObject();
            foreach (var file in downloads.OfType<JsonObject>().OrderBy(file => file["name"]?.ToString()))
            {
                var name = file["name"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name) || !DateTimeOffset.TryParse(file["updated_at"]?.ToString(), out var updated) || file["file_size"] == null)
                    throw new InvalidDataException("文件信息无效");
                var signature = new JsonObject { ["size"] = file["file_size"]!.ToString(), ["updatedAt"] = updated.ToUniversalTime().ToString("O"),
                    ["createdAt"] = DateTimeOffset.TryParse(file["created_at"]?.ToString(), out var created) ? created.ToUniversalTime().ToString("O") : null };
                if (files.ContainsKey(name) && !JsonNode.DeepEquals(files[name], signature)) throw new InvalidDataException("文件信息无效");
                files[name] = signature;
            }
            if (files.Count == 0) throw new InvalidDataException("版本文件暂不可用");
            versions[versionId] = files;
        }
        return versions;
    }

    public void Dispose() => client.Dispose();
}
