using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BoothShelf;

internal sealed record AppRelease(Version Version, string Tag, string ReleaseUrl, Uri DownloadUrl, Uri ChecksumUrl, long Size, string? Digest);
internal sealed record PreparedAppUpdate(string Directory, string Executable, Version Version, string Sha256);
internal sealed record AppDownloadProgress(long Received, long Total);

internal sealed class AppUpdateClient : IDisposable
{
    internal const string ReleasesUrl = "https://api.github.com/repos/Shiroha129/BOOTH-Shelf/releases?per_page=30";
    internal const string ReleaseError = "更新发布信息无效，请前往 GitHub 发布页手动下载。";
    internal const string SourceError = "更新下载地址不受信任，已停止下载。";
    internal const string SizeError = "更新文件大小无效或下载不完整，请重试。";
    internal const string ChecksumError = "更新包校验失败，已停止更新。";
    internal const string ArchiveError = "更新包结构无效，已停止更新。";
    internal const string VersionError = "更新程序版本不匹配，已停止更新。";
    internal const string TimeoutError = "检查或下载更新超时，请稍后重试。";
    internal const string RequestError = "无法从 GitHub 获取更新，请稍后重试。";
    internal const string StorageError = "无法写入或清理更新文件，请检查磁盘空间和目录权限。";
    private const string RepositoryUrl = "https://github.com/Shiroha129/BOOTH-Shelf";
    private const int MaximumMetadataBytes = 2 * 1024 * 1024;
    private const int MaximumChecksumBytes = 64 * 1024;
    private const long MaximumPackageBytes = 512L * 1024 * 1024;
    private const long MaximumExpandedBytes = 768L * 1024 * 1024;
    private const int MaximumEntries = 512;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private readonly HttpClient client;

    internal AppUpdateClient(HttpMessageHandler? handler = null)
    {
        client = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            MaxResponseHeadersLength = 32
        });
        client.Timeout = Timeout.InfiniteTimeSpan;
        var version = typeof(AppUpdateClient).Assembly.GetName().Version ?? new Version(0, 0, 0);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BOOTH-Shelf/" + version.ToString(3));
        client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("identity");
    }

    internal async Task<AppRelease?> CheckAsync(Version current, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var response = await GetAsync(new Uri(ReleasesUrl), false, timeout.Token).ConfigureAwait(false);
            var data = await ReadLimitedAsync(response, MaximumMetadataBytes, timeout.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > 30)
                throw new InvalidDataException(ReleaseError);
            var candidates = new List<(Version Version, string Tag, JsonElement Data)>();
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("draft", out var draft)
                    || draft.ValueKind != JsonValueKind.False || !TryParseTag(Text(entry, "tag_name"), out var version)
                    || version <= current || !DateTimeOffset.TryParse(Text(entry, "published_at"), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out _)) continue;
                candidates.Add((version, Text(entry, "tag_name")!, entry));
            }
            foreach (var candidate in candidates.OrderByDescending(entry => entry.Version))
            {
                if (!candidate.Data.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;
                var filename = PackageName(candidate.Tag) + ".zip";
                var packages = assets.EnumerateArray().Where(asset => Text(asset, "name") == filename).ToArray();
                var checksums = assets.EnumerateArray().Where(asset => Text(asset, "name") == "SHA256SUMS.txt").ToArray();
                if (packages.Length > 1 || checksums.Length > 1) throw new InvalidDataException(ReleaseError);
                if (packages.Length == 0 || checksums.Length == 0) continue;
                if (!packages[0].TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out var bytes)
                    || !checksums[0].TryGetProperty("size", out var checksumSize) || checksumSize.ValueKind != JsonValueKind.Number
                    || !checksumSize.TryGetInt64(out var checksumBytes)
                    || checksumBytes <= 0 || checksumBytes > MaximumChecksumBytes) throw new InvalidDataException(SizeError);
                string? digest = null;
                if (packages[0].TryGetProperty("digest", out var digestValue) && digestValue.ValueKind != JsonValueKind.Null)
                {
                    if (digestValue.ValueKind != JsonValueKind.String) throw new InvalidDataException(ChecksumError);
                    digest = digestValue.GetString();
                }
                var release = new AppRelease(candidate.Version, candidate.Tag, Text(candidate.Data, "html_url") ?? "",
                    AssetUri(Text(packages[0], "browser_download_url")), AssetUri(Text(checksums[0], "browser_download_url")), bytes, digest);
                ValidateRelease(release);
                timeout.Token.ThrowIfCancellationRequested();
                return release;
            }
            timeout.Token.ThrowIfCancellationRequested();
            return null;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException(TimeoutError); }
        catch (JsonException error) { throw new InvalidDataException(ReleaseError, error); }
        catch (HttpRequestException error) { throw new HttpRequestException(RequestError, error, error.StatusCode); }
    }

    internal async Task<PreparedAppUpdate> DownloadAsync(AppRelease release, string stagingRoot,
        IProgress<AppDownloadProgress> progress, CancellationToken token)
    {
        ValidateRelease(release);
        token.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(stagingRoot);
        var directory = Path.Combine(root, "app-update-" + Guid.NewGuid().ToString("N"));
        var executable = Path.Combine(directory, "BOOTH-Shelf.exe");
        var archivePath = Path.Combine(directory, "package.zip");
        var created = false;
        var prepared = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        var downloadToken = timeout.Token;
        try
        {
            Directory.CreateDirectory(root);
            RejectReparsePoints(root);
            if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException(StorageError);
            Directory.CreateDirectory(directory);
            created = true;
            RejectReparsePoints(directory);
            byte[] expectedHash;
            using (var response = await GetAsync(release.ChecksumUrl, true, downloadToken).ConfigureAwait(false))
            {
                var bytes = await ReadLimitedAsync(response, MaximumChecksumBytes, downloadToken).ConfigureAwait(false);
                expectedHash = ParseChecksum(bytes, PackageName(release.Tag) + ".zip");
            }
            using (var response = await GetAsync(release.DownloadUrl, true, downloadToken).ConfigureAwait(false))
            {
                ValidateLength(response, MaximumPackageBytes, release.Size);
                await using var input = await response.Content.ReadAsStreamAsync(downloadToken).ConfigureAwait(false);
                await using var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long received = 0;
                progress.Report(new AppDownloadProgress(0, release.Size));
                int count;
                while ((count = await ReadAsync(input, buffer, downloadToken).ConfigureAwait(false)) > 0)
                {
                    received += count;
                    if (received > release.Size) throw new InvalidDataException(SizeError);
                    await output.WriteAsync(buffer.AsMemory(0, count), downloadToken).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, count);
                    progress.Report(new AppDownloadProgress(received, release.Size));
                }
                if (received != release.Size) throw new InvalidDataException(SizeError);
                var actualHash = hash.GetHashAndReset();
                if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash)
                    || release.Digest != null && !CryptographicOperations.FixedTimeEquals(ParseDigest(release.Digest), actualHash))
                    throw new InvalidDataException(ChecksumError);
            }
            downloadToken.ThrowIfCancellationRequested();
            var executableHash = await ExtractExecutableAsync(archivePath, executable, release, downloadToken).ConfigureAwait(false);
            await StampOriginAsync(executable, release.ReleaseUrl, downloadToken).ConfigureAwait(false);
            File.Delete(archivePath);
            downloadToken.ThrowIfCancellationRequested();
            prepared = true;
            return new PreparedAppUpdate(directory, executable, release.Version, executableHash);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException(TimeoutError); }
        catch (HttpRequestException error) { throw new HttpRequestException(RequestError, error, error.StatusCode); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new IOException(StorageError, error); }
        finally
        {
            if (created && !prepared)
            {
                try { Directory.Delete(directory, true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { throw new IOException(StorageError, error); }
            }
        }
    }

    private async Task<HttpResponseMessage> GetAsync(Uri initial, bool asset, CancellationToken token)
    {
        var target = initial;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var redirects = 0; ; redirects++)
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(target.AbsoluteUri)) throw new InvalidDataException(SourceError);
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            if (!asset)
            {
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(RequestTimeout);
            HttpResponseMessage response;
            try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException(TimeoutError); }
            try
            {
                if (response.RequestMessage?.RequestUri is { } actual && actual != target)
                    throw new InvalidDataException(SourceError);
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther
                    or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if (!asset || redirects >= 3 || response.Headers.Location is not { } location)
                        throw new InvalidDataException(SourceError);
                    var next = location.IsAbsoluteUri ? location : new Uri(target, location);
                    if (!IsHttps(next) || next.Host is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                        throw new InvalidDataException(SourceError);
                    target = next;
                    response.Dispose();
                    continue;
                }
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new HttpRequestException(RequestError, null, response.StatusCode);
                if (response.Content.Headers.ContentEncoding.Any(encoding => !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase))
                    || response.Content.Headers.ContentRange != null) throw new InvalidDataException(SizeError);
                return response;
            }
            catch { response.Dispose(); throw; }
        }
    }

    private static async Task<int> ReadAsync(Stream input, Memory<byte> buffer, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(RequestTimeout);
        try { return await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException(TimeoutError); }
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, int maximum, CancellationToken token)
    {
        ValidateLength(response, maximum);
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await ReadAsync(input, buffer, token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > maximum) throw new InvalidDataException(SizeError);
            output.Write(buffer, 0, count);
        }
        if (output.Length == 0 || response.Content.Headers.ContentLength is long expected && output.Length != expected)
            throw new InvalidDataException(SizeError);
        return output.ToArray();
    }

    private static void ValidateLength(HttpResponseMessage response, long maximum, long? exact = null)
    {
        if (response.Content.Headers.ContentLength is long length && (length <= 0 || length > maximum || exact != null && length != exact))
            throw new InvalidDataException(SizeError);
    }

    private static string? Text(JsonElement element, string property) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static bool TryParseTag(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (tag == null || tag.Length is < 6 or > 32 || tag[0] != 'v') return false;
        var value = tag[1..];
        if (value.Any(character => character != '.' && !char.IsAsciiDigit(character))
            || !Version.TryParse(value, out var parsed) || parsed.Build < 0 || parsed.Revision != -1 || parsed.ToString(3) != value) return false;
        version = parsed;
        return true;
    }

    private static string PackageName(string tag) => "BOOTH-Shelf-" + tag + "-win-x64";
    private static Uri AssetUri(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : throw new InvalidDataException(SourceError);
    private static bool IsHttps(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443
        && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && uri.OriginalString.Length <= 8192;

    private static void ValidateRelease(AppRelease release)
    {
        if (!TryParseTag(release.Tag, out var version) || version != release.Version) throw new InvalidDataException(ReleaseError);
        var downloadRoot = RepositoryUrl + "/releases/download/" + release.Tag + "/";
        if (release.ReleaseUrl != RepositoryUrl + "/releases/tag/" + release.Tag || !IsHttps(release.DownloadUrl)
            || !IsHttps(release.ChecksumUrl) || release.DownloadUrl.OriginalString != downloadRoot + PackageName(release.Tag) + ".zip"
            || release.ChecksumUrl.OriginalString != downloadRoot + "SHA256SUMS.txt") throw new InvalidDataException(SourceError);
        if (release.Size <= 0 || release.Size > MaximumPackageBytes) throw new InvalidDataException(SizeError);
        if (release.Digest != null) ParseDigest(release.Digest);
    }

    private static byte[] ParseDigest(string digest)
    {
        if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || !IsSha256(digest[7..])) throw new InvalidDataException(ChecksumError);
        return Convert.FromHexString(digest[7..]);
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);

    private static byte[] ParseChecksum(byte[] data, string filename)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(data).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException error) { throw new InvalidDataException(ChecksumError, error); }
        byte[]? expected = null;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.Length < 67 || !IsSha256(line[..64]) || line[64] != ' ' || line[65] is not (' ' or '*'))
                throw new InvalidDataException(ChecksumError);
            var name = line[66..];
            if (!names.Add(name)) throw new InvalidDataException(ChecksumError);
            if (name == filename) expected = Convert.FromHexString(line[..64]);
        }
        return expected ?? throw new InvalidDataException(ChecksumError);
    }

    private static async Task<string> ExtractExecutableAsync(string archivePath, string executable, AppRelease release, CancellationToken token)
    {
        try
        {
            await using var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            var entryCount = ValidateArchiveDirectory(input);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, true);
            if (archive.Entries.Count != entryCount) throw new InvalidDataException(ArchiveError);
            var root = PackageName(release.Tag);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ZipArchiveEntry? executableEntry = null;
            long expanded = 0;
            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                var name = entry.FullName.Replace('\\', '/');
                var directory = name.EndsWith('/');
                var path = directory ? name[..^1] : name;
                var parts = path.Split('/');
                var mode = (entry.ExternalAttributes >> 16) & 0xf000;
                if (name.Length > 1024 || parts.Any(InvalidPathPart) || parts[0] != root || parts.Length == 1 && !directory
                    || !names.Add(path) || (entry.ExternalAttributes & 0x400) != 0 || mode is not (0 or 0x8000 or 0x4000)
                    || mode == 0x4000 && !directory || mode == 0x8000 && directory
                    || (entry.ExternalAttributes & 0x10) != 0 && !directory || directory && entry.Length != 0)
                    throw new InvalidDataException(ArchiveError);
                if (directory)
                {
                    if (files.Contains(path)) throw new InvalidDataException(ArchiveError);
                    directories.Add(path);
                }
                else
                {
                    if (directories.Contains(path)) throw new InvalidDataException(ArchiveError);
                    files.Add(path);
                }
                for (var separator = path.IndexOf('/'); separator >= 0; separator = path.IndexOf('/', separator + 1))
                {
                    var parent = path[..separator];
                    if (files.Contains(parent)) throw new InvalidDataException(ArchiveError);
                    directories.Add(parent);
                }
                if (entry.Length < 0 || entry.Length > MaximumPackageBytes || entry.CompressedLength < 0
                    || entry.CompressedLength > input.Length || entry.Length > 0 && (entry.CompressedLength == 0
                        || entry.Length > entry.CompressedLength * 200)) throw new InvalidDataException(ArchiveError);
                expanded += entry.Length;
                if (expanded > MaximumExpandedBytes) throw new InvalidDataException(ArchiveError);
                if (name == root + "/BOOTH-Shelf.exe" && !directory) executableEntry = entry;
            }
            if (executableEntry == null || executableEntry.Length == 0) throw new InvalidDataException(ArchiveError);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var compressed = executableEntry.Open())
            await using (var output = new FileStream(executable, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long written = 0;
                int count;
                while ((count = await compressed.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    written += count;
                    if (written > executableEntry.Length) throw new InvalidDataException(ArchiveError);
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, count);
                }
                if (written != executableEntry.Length) throw new InvalidDataException(ArchiveError);
            }
            token.ThrowIfCancellationRequested();
            ValidateExecutable(executable, release.Version);
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        catch (InvalidDataException error) when (error.Message != ArchiveError && error.Message != VersionError)
        { throw new InvalidDataException(ArchiveError, error); }
    }

    private static int ValidateArchiveDirectory(Stream input)
    {
        if (input.Length < 22) throw new InvalidDataException(ArchiveError);
        var tail = new byte[(int)Math.Min(input.Length, 65557)];
        input.Position = input.Length - tail.Length;
        input.ReadExactly(tail);
        for (var offset = tail.Length - 22; offset >= 0; offset--)
        {
            var record = tail.AsSpan(offset);
            if (BinaryPrimitives.ReadUInt32LittleEndian(record) != 0x06054b50
                || BinaryPrimitives.ReadUInt16LittleEndian(record[20..]) != record.Length - 22) continue;
            var entries = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
            var start = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);
            if (entries is 0 or > MaximumEntries || BinaryPrimitives.ReadUInt32LittleEndian(record[4..]) != 0
                || BinaryPrimitives.ReadUInt16LittleEndian(record[8..]) != entries || length > MaximumMetadataBytes
                || (long)start + length != input.Length - tail.Length + offset) throw new InvalidDataException(ArchiveError);
            input.Position = start;
            var header = new byte[46];
            for (var index = 0; index < entries; index++)
            {
                if (input.Position + header.Length > (long)start + length) throw new InvalidDataException(ArchiveError);
                input.ReadExactly(header);
                if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x02014b50
                    || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(34)) != 0) throw new InvalidDataException(ArchiveError);
                input.Position += BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28))
                    + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30)) + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32));
            }
            if (input.Position != (long)start + length) throw new InvalidDataException(ArchiveError);
            input.Position = 0;
            return entries;
        }
        throw new InvalidDataException(ArchiveError);
    }

    private static bool InvalidPathPart(string part)
    {
        if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
            || part.Any(character => character < 32 || "<>:\"|?*".Contains(character))) return true;
        var stem = part.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                && "123456789¹²³".Contains(stem[3]);
    }

    internal static void ValidateExecutable(string executable, Version version)
    {
        try
        {
            using var input = File.OpenRead(executable);
            using var reader = new PEReader(input);
            var headers = reader.PEHeaders;
            if (headers.CoffHeader.Machine != Machine.Amd64 || headers.PEHeader?.Magic != PEMagic.PE32Plus
                || !headers.CoffHeader.Characteristics.HasFlag(Characteristics.ExecutableImage)
                || headers.CoffHeader.Characteristics.HasFlag(Characteristics.Dll)
                || headers.PEHeader.Subsystem is not (Subsystem.WindowsGui or Subsystem.WindowsCui))
                throw new InvalidDataException(VersionError);
            var info = ReadExecutableVersion(executable);
            if (!MatchesVersion(info.FileVersion, version, false) || !MatchesVersion(info.ProductVersion, version, true)
                || info.FileMajorPart != version.Major || info.FileMinorPart != version.Minor || info.FileBuildPart != version.Build
                || info.FilePrivatePart != 0 || info.ProductMajorPart != version.Major || info.ProductMinorPart != version.Minor
                || info.ProductBuildPart != version.Build || info.ProductPrivatePart != 0) throw new InvalidDataException(VersionError);
        }
        catch (BadImageFormatException error) { throw new InvalidDataException(VersionError, error); }
    }

    internal static FileVersionInfo ReadExecutableVersion(string executable)
    {
        var path = Path.GetFullPath(executable);
        if (OperatingSystem.IsWindows() && !path.StartsWith(@"\\?\", StringComparison.Ordinal))
            path = path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
        return FileVersionInfo.GetVersionInfo(path);
    }

    private static bool MatchesVersion(string? text, Version expected, bool metadata)
    {
        if (text == null) return false;
        if (metadata && text.IndexOf('+') is var suffix && suffix >= 0)
        {
            if (suffix == text.Length - 1 || text[(suffix + 1)..].Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-')))
                return false;
            text = text[..suffix];
        }
        return Version.TryParse(text, out var parsed) && parsed.Major == expected.Major && parsed.Minor == expected.Minor
            && parsed.Build == expected.Build && parsed.Revision is -1 or 0;
    }

    private static void RejectReparsePoints(string path)
    {
        for (var directory = new DirectoryInfo(path); directory != null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException(StorageError);
    }

    private static async Task StampOriginAsync(string executable, string releaseUrl, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) return;
        var format = new DriveInfo(Path.GetPathRoot(executable)!).DriveFormat;
        if (format is not ("NTFS" or "ReFS")) return;
        await File.WriteAllTextAsync(executable + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=" + releaseUrl + "\r\n",
            new UTF8Encoding(false), token).ConfigureAwait(false);
    }

    public void Dispose() => client.Dispose();
}
