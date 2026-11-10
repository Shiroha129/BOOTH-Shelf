using System.Diagnostics;
using System.Text.Json.Nodes;

namespace BoothShelf;

internal static class LocalAssetSelfTest
{
    internal static JsonObject Run(string root)
    {
        var downloads = Path.Combine(root, "downloads");
        var directory = Path.Combine(downloads, "42_Test asset");
        var second = Path.Combine(root, "new-downloads", "42_Test asset");
        var neighbor = Path.Combine(downloads, "43_Other asset");
        foreach (var target in new[] { directory, second, neighbor }) Directory.CreateDirectory(target);
        var firstFile = Path.Combine(directory, "material.zip");
        var secondFile = Path.Combine(second, "outfit.zip");
        var neighborFile = Path.Combine(neighbor, "keep.zip");
        foreach (var target in new[] { firstFile, secondFile, neighborFile }) File.WriteAllText(target, "fixture");
        Directory.CreateDirectory(Path.Combine(directory, "extracted"));
        File.WriteAllText(Path.Combine(directory, "extracted", "material.mat"), "fixture");
        var history = new JsonArray(Record("first", "42", "Test asset", firstFile), Record("second", "42", "Test asset", secondFile),
            Record("neighbor", "43", "Other asset", neighborFile));
        var library = new LocalLibrary(root, history);
        var planned = library.AssetDirectories("42");
        Check(planned.Length == 2, "未包含修改下载目录前后的资产文件夹");
        Reject(() => library.DeleteAsset("42", [downloads], _ => throw new Exception("unsafe delete")), "允许替换为下载根目录");
        Reject(() => library.AssetDirectories("../42"), "允许非法商品 ID");
        Reject(() => library.AssetDirectories("999"), "允许删除未登记资产");
        library.Add(Record("shared", "43", "Other asset", firstFile + ".unused"));
        var shared = Record("shared", "43", "Other asset", Path.Combine(directory, "extracted", "material.mat"));
        library.Add(shared);
        Reject(() => library.AssetDirectories("42"), "允许删除其他资产的文件");
        var isolatedRoot = Path.Combine(root, "isolated");
        Directory.CreateDirectory(isolatedRoot);
        library = new LocalLibrary(isolatedRoot, history);
        var junction = Path.Combine(directory, "linked");
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(junction);
        start.ArgumentList.Add(neighbor);
        using (var process = Process.Start(start)!)
        {
            process.WaitForExit();
            Check(process.ExitCode == 0, "隔离测试目录的 junction 创建失败");
        }
        try { Reject(() => library.AssetDirectories("42"), "允许递归删除链接目录"); }
        finally { Directory.Delete(junction); }
        var mismatchedRoot = Path.Combine(root, "mismatched");
        Directory.CreateDirectory(mismatchedRoot);
        var mismatched = Record("mismatch", "42", "Test asset", firstFile);
        mismatched["downloadRoot"] = neighbor;
        var invalidLibrary = new LocalLibrary(mismatchedRoot, new JsonArray(mismatched));
        Reject(() => invalidLibrary.AssetDirectories("42"), "忽略下载根目录不匹配");
        var removed = new List<string>();
        library.DeleteAsset("42", planned, target =>
        {
            Check(Path.GetFullPath(target).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "测试越出隔离目录");
            Directory.Delete(target, true);
            removed.Add(target);
        });
        Check(removed.Count == 2 && !Directory.Exists(directory) && !Directory.Exists(second) && File.Exists(neighborFile), "删除范围不正确");
        Check(Directory.Exists(downloads) && library.List().Count == 1, "误删下载根目录或本地库未刷新");
        Directory.CreateDirectory(directory);
        File.WriteAllText(firstFile, "restored by user");
        var restarted = new LocalLibrary(isolatedRoot, history);
        Check(restarted.List("42").Count == 0, "历史记录使已删除资产重新出现");
        var redownload = Record("new", "42", "Test asset", firstFile);
        restarted.Add(redownload);
        Check(restarted.List("42").Count == 1, "重新下载后未恢复显示");
        return new JsonObject { ["multipleFolders"] = true, ["unpackedFilesIncluded"] = true, ["neighborPreserved"] = true,
            ["rootRejected"] = true, ["unregisteredRejected"] = true, ["sharedFolderRejected"] = true, ["junctionRejected"] = true,
            ["changedRootRejected"] = true, ["noHistoryResurrection"] = true, ["redownload"] = true };
    }

    private static JsonObject Record(string id, string itemId, string title, string target) => new()
    {
        ["id"] = id, ["itemId"] = itemId, ["title"] = title, ["name"] = Path.GetFileName(target), ["path"] = target, ["state"] = "completed"
    };

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or DirectoryNotFoundException) { return; }
        throw new Exception(message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
