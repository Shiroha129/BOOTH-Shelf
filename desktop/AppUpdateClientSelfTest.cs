using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace BoothShelf;

internal static class AppUpdateClientSelfTest
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal readonly List<Uri> Requests = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Check(request.Method == HttpMethod.Get && request.Headers.Authorization == null && !request.Headers.Contains("Cookie")
                && request.Headers.UserAgent.Any(value => value.Product?.Name == "BOOTH-Shelf" && value.Product.Version != null),
                "更新请求缺少产品版本或携带了用户凭据");
            Requests.Add(request.RequestUri!);
            var response = respond(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private sealed class Progress(Action<AppDownloadProgress> update) : IProgress<AppDownloadProgress>
    {
        public void Report(AppDownloadProgress value) => update(value);
    }

    private sealed class ChunkStream(byte[] bytes, int chunk, CancellationTokenSource? cancellation = null) : Stream
    {
        private int position;
        internal bool Disposed;
        internal bool CancellationObserved;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("下载未使用异步读取");
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (cancellation != null && position > 0)
            {
                cancellation.Cancel();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { CancellationObserved = true; throw; }
            }
            var count = Math.Min(Math.Min(buffer.Length, chunk), bytes.Length - position);
            bytes.AsMemory(position, count).CopyTo(buffer);
            position += count;
            return count;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed record Entry(string Name, byte[] Bytes, int Attributes = 0, CompressionLevel Compression = CompressionLevel.NoCompression);

    internal static Task<JsonObject> RunAsync(string root) => RunAsync(root,
        Environment.ProcessPath ?? throw new InvalidOperationException("无法定位自测程序"));

    internal static async Task<JsonObject> RunAsync(string root, string fixtureExecutable)
    {
        var run = Path.Combine(Path.GetFullPath(root), "app-update-tests-" + Guid.NewGuid().ToString("N"));
        var nested = run;
        while (nested.Length < 260) nested = Path.Combine(nested, "long-path-" + new string('x', 40));
        var staging = Path.Combine(nested, "app-updates");
        Directory.CreateDirectory(Path.Combine(staging, "existing"));
        var sentinel = Path.Combine(staging, "existing", "keep.txt");
        var outside = Path.Combine(run, "outside.exe");
        await File.WriteAllTextAsync(sentinel, "preserved existing stage");
        await File.WriteAllTextAsync(outside, "preserved outside stage");
        var baseline = Directory.GetFileSystemEntries(staging).Order().ToArray();
        var quiet = new Progress(_ => { });
        var report = new JsonObject();
        try
        {
            await CheckReleasesAsync(report);
            var version = new Version(1, 2, 3);
            var tag = "v" + version;
            var bundle = "BOOTH-Shelf-" + tag + "-win-x64";
            var package = Package(new Entry(bundle + "/BOOTH-Shelf.exe", [77, 90, 0, 0]));
            var release = Release(version, package);

            void CheckClean()
            {
                Check(Directory.GetFileSystemEntries(staging).Order().SequenceEqual(baseline), "失败下载残留目录或移除了其他暂存目录");
                Check(File.ReadAllText(sentinel) == "preserved existing stage" && File.ReadAllText(outside) == "preserved outside stage",
                    "下载失败修改了暂存范围外的数据");
            }

            async Task RejectDownloadAsync(AppRelease candidate, Handler handler, string error)
            {
                using var client = new AppUpdateClient(handler);
                await RejectAsync<InvalidDataException>(() => client.DownloadAsync(candidate, staging, quiet, CancellationToken.None), error);
                CheckClean();
            }

            await RejectDownloadAsync(release, DownloadHandler(release, package, new string('0', 64) + "  " + AssetName(release)), AppUpdateClient.ChecksumError);
            await RejectDownloadAsync(release with { Digest = "sha256:" + new string('0', 64) }, DownloadHandler(release, package), AppUpdateClient.ChecksumError);
            foreach (var checksum in new[]
            {
                Hash(package) + "  other.zip\n",
                Hash(package) + "  nested/" + AssetName(release) + "\n",
                Hash(package) + "  " + AssetName(release) + ".extra\n",
                Hash(package) + "  " + AssetName(release) + "\n" + Hash(package) + " *" + AssetName(release) + "\n",
                "not-a-checksum  " + AssetName(release) + "\n"
            }) await RejectDownloadAsync(release, DownloadHandler(release, package, checksum), AppUpdateClient.ChecksumError);
            report["tamperedChecksumRejected"] = true;
            report["apiDigestRequiredWhenPresent"] = true;
            report["exactUniqueChecksumEntry"] = true;

            var wrongSource = new[]
            {
                release with { DownloadUrl = new Uri("https://example.invalid/payload.exe") },
                release with { ChecksumUrl = new Uri("https://github.com/another/repo/releases/download/v1.2.3/SHA256SUMS.txt") },
                release with { ReleaseUrl = "https://github.com/Shiroha129/BOOTH-Shelf/releases/tag/v1.2.4" },
                release with { DownloadUrl = new Uri(release.DownloadUrl + "?unexpected=true") },
                release with { DownloadUrl = new Uri("http://github.com/Shiroha129/BOOTH-Shelf/releases/download/v1.2.3/" + AssetName(release)) }
            };
            foreach (var candidate in wrongSource)
            {
                var handler = new Handler(_ => throw new InvalidOperationException("不可信来源产生了 HTTP 请求"));
                await RejectDownloadAsync(candidate, handler, AppUpdateClient.SourceError);
                Check(handler.Requests.Count == 0, "验证来源前已下载");
            }
            report["releaseOriginsPinned"] = true;

            foreach (var target in new[]
            {
                "https://example.invalid/payload.exe", "http://release-assets.githubusercontent.com/payload",
                "https://release-assets.githubusercontent.com.evil.invalid/payload", "https://objects.githubusercontent.com:444/payload",
                "https://user@objects.githubusercontent.com/payload", "https://objects.githubusercontent.com/payload#fragment",
                "https://github.com/another/repo/releases/download/v1.2.3/payload.exe", "/login"
            })
            {
                var handler = new Handler(_ => Redirect(target));
                await RejectDownloadAsync(release, handler, AppUpdateClient.SourceError);
                Check(handler.Requests.Count == 1, "拒绝重定向后仍访问了目标");
            }
            var redirectLoop = new Handler(_ => Redirect("https://release-assets.githubusercontent.com/loop"));
            await RejectDownloadAsync(release, redirectLoop, AppUpdateClient.SourceError);
            Check(redirectLoop.Requests.Count == 2, "未及时终止重定向循环");
            var redirectCount = 0;
            var redirectChain = new Handler(_ => Redirect("https://objects.githubusercontent.com/asset/" + ++redirectCount));
            await RejectDownloadAsync(release, redirectChain, AppUpdateClient.SourceError);
            Check(redirectChain.Requests.Count == 4, "重定向次数没有上限");
            var implicitRedirect = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/hidden"),
                Content = new ByteArrayContent([1])
            });
            await RejectDownloadAsync(release, implicitRedirect, AppUpdateClient.SourceError);
            report["redirectRejection"] = true;
            report["redirectLoopsAndLimit"] = true;

            var truncated = new ChunkStream(package[..^7], 19);
            await RejectDownloadAsync(release, DownloadHandler(release, package, content: () =>
            {
                var content = new StreamContent(truncated);
                content.Headers.ContentLength = package.Length;
                return content;
            }), AppUpdateClient.SizeError);
            Check(truncated.Disposed, "截断下载没有释放流");
            var extra = new ChunkStream([.. package, 1], 23);
            await RejectDownloadAsync(release, DownloadHandler(release, package, content: () => new StreamContent(extra)), AppUpdateClient.SizeError);
            await RejectDownloadAsync(release, DownloadHandler(release, package, content: () =>
            {
                var content = new ByteArrayContent(package);
                content.Headers.ContentLength = package.Length + 1;
                return content;
            }), AppUpdateClient.SizeError);
            await RejectDownloadAsync(release with { Size = 512L * 1024 * 1024 + 1 }, new Handler(_ => throw new Exception("超大更新包发起请求")), AppUpdateClient.SizeError);
            var largeChecksums = new ChunkStream(new byte[64 * 1024 + 1], 8192);
            await RejectDownloadAsync(release, new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(largeChecksums) }), AppUpdateClient.SizeError);
            Check(largeChecksums.Disposed, "超大校验文件没有释放流");
            report["truncatedAndOversizedDownloads"] = true;
            report["checksumSizeLimited"] = true;

            using (var cancellation = new CancellationTokenSource())
            {
                var stream = new ChunkStream(package, 17, cancellation);
                using var client = new AppUpdateClient(DownloadHandler(release, package, content: () => new StreamContent(stream)));
                await RejectAsync<OperationCanceledException>(() => client.DownloadAsync(release, staging, quiet, cancellation.Token));
                Check(stream.CancellationObserved && stream.Disposed, "取消未传递给下载流或未关闭文件");
            }
            CheckClean();
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                var handler = new Handler(_ => throw new Exception("已取消的请求仍访问网络"));
                using var client = new AppUpdateClient(handler);
                await RejectAsync<OperationCanceledException>(() => client.DownloadAsync(release, staging, quiet, cancellation.Token));
                Check(handler.Requests.Count == 0, "开始前取消仍发起请求");
            }
            CheckClean();
            report["cancellationDuringStreaming"] = true;
            report["onlyGeneratedStageRemoved"] = true;

            foreach (var path in new[]
            {
                "../outside.exe", bundle + "/../../outside.exe", bundle + "\\..\\..\\outside.exe", "/outside.exe",
                "C:\\outside.exe", bundle + "/BOOTH-Shelf.exe:Zone.Identifier", bundle + "/./BOOTH-Shelf.exe",
                bundle + "//BOOTH-Shelf.exe", bundle + "/NUL.txt", bundle + "/trailing. ", "wrong-package/BOOTH-Shelf.exe"
            })
            {
                var invalid = Package(new Entry(path, [1]));
                var candidate = Release(version, invalid);
                await RejectDownloadAsync(candidate, DownloadHandler(candidate, invalid), AppUpdateClient.ArchiveError);
            }
            foreach (var entries in new[]
            {
                new[] { new Entry(bundle + "/BOOTH-Shelf.exe", [1]), new Entry(bundle + "\\booth-shelf.EXE", [2]) },
                new[] { new Entry(bundle + "/BOOTH-Shelf.exe", [1], unchecked((int)0xa1ff0000)) },
                new[] { new Entry(bundle + "/BOOTH-Shelf.exe", [1], 0x400) },
                new[] { new Entry(bundle + "/licenses", [1]), new Entry(bundle + "/licenses/notice.txt", [2]) },
                new[] { new Entry(bundle + "/BOOTH-Shelf.exe", new byte[1024 * 1024], Compression: CompressionLevel.SmallestSize) },
                new[] { new Entry(bundle + "/readme.txt", [1]) },
                Enumerable.Range(0, 513).Select(index => new Entry(bundle + "/file-" + index, [])).ToArray()
            })
            {
                var invalid = Package(entries);
                var candidate = Release(version, invalid);
                await RejectDownloadAsync(candidate, DownloadHandler(candidate, invalid), AppUpdateClient.ArchiveError);
            }
            var corruptArchive = new byte[64];
            var corruptRelease = Release(version, corruptArchive);
            await RejectDownloadAsync(corruptRelease, DownloadHandler(corruptRelease, corruptArchive), AppUpdateClient.ArchiveError);
            await RejectDownloadAsync(release, DownloadHandler(release, package), AppUpdateClient.VersionError);
            report["traversalAndWindowsPathsRejected"] = true;
            report["duplicateEntriesRejected"] = true;
            report["linksAndArchiveBombsRejected"] = true;
            report["missingExecutableRejected"] = true;
            report["invalidExecutableRejected"] = true;

            var runningExecutable = Path.GetFullPath(fixtureExecutable);
            var info = AppUpdateClient.ReadExecutableVersion(runningExecutable);
            var installed = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
            AppUpdateClient.ValidateExecutable(runningExecutable, installed);
            await RejectAsync<InvalidDataException>(() =>
            {
                AppUpdateClient.ValidateExecutable(runningExecutable, new Version(installed.Major + 1, installed.Minor, installed.Build));
                return Task.CompletedTask;
            }, AppUpdateClient.VersionError);
            var installedTag = "v" + installed;
            var installedBundle = "BOOTH-Shelf-" + installedTag + "-win-x64";
            var executableBytes = await File.ReadAllBytesAsync(runningExecutable);
            var validPackage = Package(new Entry(installedBundle + "\\BOOTH-Shelf.exe", executableBytes),
                new Entry(installedBundle + "\\licenses\\", [], 0x10), new Entry(installedBundle + "\\licenses\\notice.txt", [1, 2, 3]));
            var validRelease = Release(installed, validPackage);
            var updates = new List<AppDownloadProgress>();
            var allowedHandler = new Handler(request =>
            {
                if (request.RequestUri == validRelease.ChecksumUrl) return Redirect("https://objects.githubusercontent.com/checksums?token=offline");
                if (request.RequestUri == validRelease.DownloadUrl) return Redirect("https://release-assets.githubusercontent.com/package?token=offline");
                if (request.RequestUri!.Host == "objects.githubusercontent.com") return Response(Encoding.UTF8.GetBytes(Hash(validPackage) + " *" + AssetName(validRelease) + "\r\n"));
                Check(request.RequestUri.Host == "release-assets.githubusercontent.com", "下载访问了非预期地址");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ChunkStream(validPackage, 32768)) };
            });
            PreparedAppUpdate prepared;
            using (var client = new AppUpdateClient(allowedHandler))
                prepared = await client.DownloadAsync(validRelease, staging, new Progress(updates.Add), CancellationToken.None);
            Check(allowedHandler.Requests.Count == 4 && updates.Count > 2 && updates[0] == new AppDownloadProgress(0, validPackage.Length)
                && updates[^1] == new AppDownloadProgress(validPackage.Length, validPackage.Length)
                && updates.All(update => update.Total == validPackage.Length)
                && updates.Zip(updates.Skip(1)).All(pair => pair.First.Received < pair.Second.Received), "下载进度不连续或未跟随允许的跳转");
            Check(Path.GetDirectoryName(prepared.Directory) == staging && prepared.Executable == Path.Combine(prepared.Directory, "BOOTH-Shelf.exe")
                && prepared.Version == installed && prepared.Sha256 == Hash(executableBytes)
                && File.ReadAllBytes(prepared.Executable).SequenceEqual(executableBytes), "成功暂存布局、版本或 EXE 哈希错误");
            Check(prepared.Executable.Length > 260, "长路径版本校验回归用例未超过 Windows 传统路径上限");
            AppUpdateClient.ValidateExecutable(prepared.Executable, installed);
            if (OperatingSystem.IsWindows())
                AppUpdateClient.ValidateExecutable(AppUpdateClient.ReadExecutableVersion(prepared.Executable).FileName, installed);
            await RejectAsync<InvalidDataException>(() =>
            {
                AppUpdateClient.ValidateExecutable(prepared.Executable, new Version(installed.Major + 1, installed.Minor, installed.Build));
                return Task.CompletedTask;
            }, AppUpdateClient.VersionError);
            Check(Directory.GetFileSystemEntries(prepared.Directory).SequenceEqual([prepared.Executable]), "提取了主程序之外的 ZIP 条目或保留了下载包");
            var format = new DriveInfo(Path.GetPathRoot(prepared.Executable)!).DriveFormat;
            if (OperatingSystem.IsWindows() && format is "NTFS" or "ReFS")
            {
                var mark = await File.ReadAllTextAsync(prepared.Executable + ":Zone.Identifier");
                Check(mark == "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=" + validRelease.ReleaseUrl + "\r\n", "下载来源标记缺失或来源错误");
                report["windowsInternetZonePreserved"] = true;
            }
            else report["windowsInternetZonePreserved"] = "filesystem does not support Windows alternate streams";
            baseline = Directory.GetFileSystemEntries(staging).Order().ToArray();
            await RejectDownloadAsync(release, DownloadHandler(release, package, new string('0', 64) + "  " + AssetName(release)), AppUpdateClient.ChecksumError);
            Check(File.Exists(prepared.Executable), "失败下载移除了之前成功的暂存程序");
            using (var client = new AppUpdateClient(DownloadHandler(validRelease, validPackage)))
            {
                var legacy = await client.DownloadAsync(validRelease with { Digest = null }, staging, quiet, CancellationToken.None);
                Check(legacy.Directory != prepared.Directory && File.Exists(prepared.Executable) && legacy.Sha256 == prepared.Sha256,
                    "重复下载覆盖了成功目录或无 API 摘要时跳过了校验");
            }
            report["allowedAssetRedirects"] = true;
            report["streamingProgress"] = true;
            report["backslashPackagePath"] = true;
            report["onlyExpectedExecutableExtracted"] = true;
            report["executableVersionAndHashVerified"] = true;
            report["longPathExecutableVersionAndMismatch"] = true;
            report["successfulUniqueStagesRetained"] = true;
            report["checksumWorksWithoutApiDigest"] = true;
            report["noDownloadedCodeExecuted"] = true;
            report["passed"] = true;
            return report;
        }
        finally { Directory.Delete(run, true); }
    }

    private static async Task CheckReleasesAsync(JsonObject report)
    {
        var draft = ReleaseNode("v9.0.0");
        draft["draft"] = true;
        var unpublished = ReleaseNode("v8.0.0");
        unpublished["published_at"] = null;
        var releases = new JsonArray(ReleaseNode("v1.9.9", false), draft, ReleaseNode("v1.10.0"), unpublished,
            ReleaseNode("v1.10.0-rc.1"), ReleaseNode("v1.11.0.0"), ReleaseNode("v1.02.0"), ReleaseNode("v0.9.0"));
        var handler = new Handler(request =>
        {
            Check(request.RequestUri!.AbsoluteUri == AppUpdateClient.ReleasesUrl && request.Headers.Contains("X-GitHub-Api-Version"),
                "版本检查未固定到匿名公开 releases 接口");
            return Response(Encoding.UTF8.GetBytes(releases.ToJsonString()));
        });
        using (var client = new AppUpdateClient(handler))
        {
            var release = await client.CheckAsync(new Version(1, 9, 0), CancellationToken.None);
            Check(release?.Version == new Version(1, 10, 0) && release.Tag == "v1.10.0", "未按数字版本选取最高已发布预发行版");
            Check(await client.CheckAsync(new Version(1, 10, 0), CancellationToken.None) == null, "重复提供当前版本");
            Check(await client.CheckAsync(new Version(2, 0, 0), CancellationToken.None) == null, "更新检查允许降级");
        }
        Check(handler.Requests.Count == 3, "检查版本访问了资产或其他接口");
        report["anonymousPinnedReleaseQuery"] = true;
        report["highestNumericPrerelease"] = true;
        report["draftAndUnpublishedExcluded"] = true;
        report["noDowngradeOrReinstall"] = true;

        foreach (var tag in new[] { "1.2.3", "V1.2.3", "v1.2", "v1.2.3.4", "v1.2.3-beta", "v1.2.3+build", "v01.2.3", "v-1.2.3", "v1.2.2147483648", "v１.2.3", "v1.2.3\n" })
            Check(!AppUpdateClient.TryParseTag(tag, out _), "接受了无效版本标签：" + tag);
        using (var client = new AppUpdateClient(new Handler(_ => Response(Encoding.UTF8.GetBytes(new JsonArray(ReleaseNode("v1.2.3-preview")).ToJsonString())))))
            Check(await client.CheckAsync(new Version(0, 0, 0), CancellationToken.None) == null, "畸形版本标签产生了更新");
        report["malformedVersionsRejected"] = true;

        async Task RejectMetadataAsync(JsonObject data, string error)
        {
            using var client = new AppUpdateClient(new Handler(_ => Response(Encoding.UTF8.GetBytes(new JsonArray(data).ToJsonString()))));
            await RejectAsync<InvalidDataException>(() => client.CheckAsync(new Version(0, 0, 0), CancellationToken.None), error);
        }
        var wrongPage = ReleaseNode("v1.2.3");
        wrongPage["html_url"] = "https://github.com/other/repo/releases/tag/v1.2.3";
        await RejectMetadataAsync(wrongPage, AppUpdateClient.SourceError);
        var wrongTag = ReleaseNode("v1.2.3");
        wrongTag["assets"]![0]!["browser_download_url"] = "https://github.com/Shiroha129/BOOTH-Shelf/releases/download/v1.2.4/BOOTH-Shelf-v1.2.3-win-x64.zip";
        await RejectMetadataAsync(wrongTag, AppUpdateClient.SourceError);
        var wrongDigest = ReleaseNode("v1.2.3");
        wrongDigest["assets"]![0]!["digest"] = "sha256:invalid";
        await RejectMetadataAsync(wrongDigest, AppUpdateClient.ChecksumError);
        var wrongSize = ReleaseNode("v1.2.3");
        wrongSize["assets"]![0]!["size"] = "not-a-number";
        await RejectMetadataAsync(wrongSize, AppUpdateClient.SizeError);
        var duplicateAsset = ReleaseNode("v1.2.3");
        duplicateAsset["assets"]!.AsArray().Add(duplicateAsset["assets"]![0]!.DeepClone());
        await RejectMetadataAsync(duplicateAsset, AppUpdateClient.ReleaseError);
        using (var client = new AppUpdateClient(new Handler(_ => Redirect("https://api.github.com/other"))))
            await RejectAsync<InvalidDataException>(() => client.CheckAsync(new Version(0, 0, 0), CancellationToken.None), AppUpdateClient.SourceError);
        using (var client = new AppUpdateClient(new Handler(_ => Response(Encoding.UTF8.GetBytes("{broken")))))
            await RejectAsync<InvalidDataException>(() => client.CheckAsync(new Version(0, 0, 0), CancellationToken.None), AppUpdateClient.ReleaseError);
        using (var client = new AppUpdateClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests))))
            await RejectAsync<HttpRequestException>(() => client.CheckAsync(new Version(0, 0, 0), CancellationToken.None), AppUpdateClient.RequestError);
        var oversized = new ChunkStream(new byte[2 * 1024 * 1024 + 1], 8192);
        using (var client = new AppUpdateClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(oversized) })))
            await RejectAsync<InvalidDataException>(() => client.CheckAsync(new Version(0, 0, 0), CancellationToken.None), AppUpdateClient.SizeError);
        Check(oversized.Disposed, "超大版本响应未释放");
        report["metadataValidationAndSizeLimit"] = true;
        report["apiRedirectAndRateLimitRejected"] = true;
    }

    private static AppRelease Release(Version version, byte[] package)
    {
        var tag = "v" + version;
        var prefix = "https://github.com/Shiroha129/BOOTH-Shelf/releases/";
        return new AppRelease(version, tag, prefix + "tag/" + tag,
            new Uri(prefix + "download/" + tag + "/BOOTH-Shelf-" + tag + "-win-x64.zip"),
            new Uri(prefix + "download/" + tag + "/SHA256SUMS.txt"), package.LongLength, "sha256:" + Hash(package));
    }

    private static JsonObject ReleaseNode(string tag, bool prerelease = true)
    {
        var prefix = "https://github.com/Shiroha129/BOOTH-Shelf/releases/";
        var filename = "BOOTH-Shelf-" + tag + "-win-x64.zip";
        return new JsonObject
        {
            ["tag_name"] = tag, ["draft"] = false, ["prerelease"] = prerelease, ["published_at"] = "2026-11-09T00:00:00Z",
            ["html_url"] = prefix + "tag/" + tag,
            ["assets"] = new JsonArray(
                new JsonObject { ["name"] = filename, ["size"] = 256, ["browser_download_url"] = prefix + "download/" + tag + "/" + filename, ["digest"] = "sha256:" + new string('a', 64) },
                new JsonObject { ["name"] = "SHA256SUMS.txt", ["size"] = 128, ["browser_download_url"] = prefix + "download/" + tag + "/SHA256SUMS.txt" })
        };
    }

    private static Handler DownloadHandler(AppRelease release, byte[] package, string? checksum = null, Func<HttpContent>? content = null) => new(request =>
    {
        if (request.RequestUri == release.ChecksumUrl) return Response(Encoding.UTF8.GetBytes(checksum ?? Hash(package) + "  " + AssetName(release) + "\n"));
        Check(request.RequestUri == release.DownloadUrl, "下载请求超出了固定发布资产");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content?.Invoke() ?? new ByteArrayContent(package) };
    });

    private static HttpResponseMessage Response(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static HttpResponseMessage Redirect(string target) => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri(target, UriKind.RelativeOrAbsolute) } };
    private static string AssetName(AppRelease release) => "BOOTH-Shelf-" + release.Tag + "-win-x64.zip";
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[] Package(params Entry[] entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var fixture in entries)
            {
                var entry = archive.CreateEntry(fixture.Name, fixture.Compression);
                entry.ExternalAttributes = fixture.Attributes;
                using var stream = entry.Open();
                stream.Write(fixture.Bytes);
            }
        return output.ToArray();
    }

    private static async Task RejectAsync<TException>(Func<Task> action, string? message = null) where TException : Exception
    {
        try { await action(); }
        catch (TException error)
        {
            Check(message == null || error.Message == message, "拒绝原因不符合预期：" + error);
            return;
        }
        throw new InvalidOperationException("未拒绝无效更新：" + typeof(TException).Name);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
