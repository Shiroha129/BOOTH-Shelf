using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;

namespace BoothShelf;

internal sealed class LocalLibrary
{
    private readonly string path;
    private readonly JsonArray records;

    internal LocalLibrary(string root, JsonArray history)
    {
        path = Path.Combine(root, "local-files.json");
        records = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsArray() : new JsonArray();
        foreach (var record in history.OfType<JsonObject>().Where(record => record["state"]?.GetValue<string>() == "completed"))
            Add(record, false);
        Save();
    }

    internal void Add(JsonObject record, bool save = true)
    {
        var target = record["path"]?.GetValue<string>();
        if (target == null || !File.Exists(target)) return;
        var existing = records.FirstOrDefault(item => string.Equals(item?["path"]?.GetValue<string>(), target, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            if (existing["id"]?.GetValue<string>() == record["id"]?.GetValue<string>() || existing["state"]?.GetValue<string>() != "deleted") return;
            records.Remove(existing);
        }
        records.Add(record.DeepClone());
        if (save) Save();
    }

    internal JsonArray List(string? itemId = null) => new(records.OfType<JsonObject>()
        .Where(record => record["state"]?.GetValue<string>() != "deleted" && (itemId == null || record["itemId"]?.GetValue<string>() == itemId) && File.Exists(record["path"]!.GetValue<string>()))
        .Select(record =>
        {
            var copy = (JsonObject)record.DeepClone();
            var file = new FileInfo(record["path"]!.GetValue<string>());
            copy["size"] = file.Length;
            copy["modifiedAt"] = file.LastWriteTimeUtc.ToString("O");
            return (JsonNode)copy;
        }).ToArray());

    internal string Resolve(string id)
    {
        var record = records.FirstOrDefault(record => record?["id"]?.GetValue<string>() == id)
            ?? throw new InvalidOperationException("文件不在本地库中。");
        var target = Path.GetFullPath(record["path"]!.GetValue<string>());
        if (record["state"]?.GetValue<string>() == "deleted") throw new FileNotFoundException("文件已经移到回收站。");
        if (!File.Exists(target)) throw new FileNotFoundException("文件已移动或删除，请刷新本地库。");
        return target;
    }

    internal bool Act(string id, string action)
    {
        var target = Resolve(id);
        switch (action)
        {
            case "open":
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                break;
            case "folder":
                var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                start.ArgumentList.Add("/select,");
                start.ArgumentList.Add(target);
                Process.Start(start);
                break;
            case "delete":
                FileSystem.DeleteFile(target, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                var record = records.First(record => record?["id"]?.GetValue<string>() == id)!;
                record["state"] = "deleted";
                Save();
                break;
            default: throw new InvalidOperationException("未知文件操作");
        }
        return true;
    }

    internal string[] AssetDirectories(string itemId)
    {
        if (!Regex.IsMatch(itemId, @"^\d{1,20}$")) throw new InvalidDataException("商品 ID 无效");
        var owned = records.OfType<JsonObject>().Where(record => record["state"]?.GetValue<string>() != "deleted"
            && record["itemId"]?.GetValue<string>() == itemId).ToArray();
        if (owned.Length == 0) throw new InvalidOperationException("该资产没有可用的本地文件");
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in owned)
        {
            var target = Path.GetFullPath(record["path"]!.GetValue<string>());
            var directory = Path.GetDirectoryName(target);
            var title = record["title"]?.GetValue<string>();
            if (directory == null || string.IsNullOrWhiteSpace(title)
                || !string.Equals(Path.GetFileName(directory), DownloadService.SafeName(itemId + "_" + title), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("无法确认资产文件夹，请使用打开所在目录手动处理。");
            if (record["assetDirectory"] is JsonValue assetDirectory && !SamePath(assetDirectory.GetValue<string>(), directory)
                || record["downloadRoot"] is JsonValue downloadRoot && !SamePath(downloadRoot.GetValue<string>(), Path.GetDirectoryName(directory)!))
                throw new InvalidOperationException("无法确认资产文件夹，请使用打开所在目录手动处理。");
            if (Directory.Exists(directory)) directories.Add(directory);
        }
        if (directories.Count == 0) throw new DirectoryNotFoundException("文件已移动或删除，请刷新本地库。");
        foreach (var directory in directories)
        {
            for (var ancestor = new DirectoryInfo(directory); ancestor != null; ancestor = ancestor.Parent)
                RejectLink(ancestor.FullName);
            foreach (var other in records.OfType<JsonObject>().Where(record => record["state"]?.GetValue<string>() != "deleted"
                && record["itemId"]?.GetValue<string>() != itemId))
                if (Within(other["path"]!.GetValue<string>(), directory))
                    throw new InvalidOperationException("文件夹包含其他资产，无法整目录删除。");
            ValidateTree(directory);
        }
        return directories.OrderBy(directory => directory, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal void DeleteAsset(string itemId, string[] expectedDirectories, Action<string>? recycle = null)
    {
        var directories = AssetDirectories(itemId);
        if (!directories.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(expectedDirectories))
            throw new InvalidOperationException("资产目录已变化，请重新打开删除菜单。");
        foreach (var directory in directories)
        {
            if (recycle != null) recycle(directory);
            else FileSystem.DeleteDirectory(directory, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
            foreach (var record in records.OfType<JsonObject>().Where(record => record["itemId"]?.GetValue<string>() == itemId
                && Within(record["path"]!.GetValue<string>(), directory))) record["state"] = "deleted";
            Save();
        }
    }

    private static bool SamePath(string first, string second) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);

    private static bool Within(string file, string directory) => Path.GetFullPath(file).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void RejectLink(string target)
    {
        if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("资产目录包含链接，无法安全删除，请打开所在目录手动处理。");
    }

    private static void ValidateTree(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                RejectLink(entry);
                if (Directory.Exists(entry)) pending.Push(entry);
            }
    }

    private void Save() => SyncCache.AtomicWrite(path, records.ToJsonString());
}
