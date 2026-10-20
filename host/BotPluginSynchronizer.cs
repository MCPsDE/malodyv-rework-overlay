using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace MalodyV.ReworkOverlay;

internal sealed class BotPluginManifest
{
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("directorySha256")]
    public string? DirectorySha256 { get; init; }

    [JsonPropertyName("archiveUrl")]
    public string? ArchiveUrl { get; init; }
}

internal sealed record BotPluginSyncResult(bool Updated, string? Version, string? Error);

internal static class BotPluginSynchronizer
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".css", ".html", ".js", ".json", ".mjs", ".py", ".txt",
    };
    private const string DirectoryName = "nonebot_plugin_rework";
    private const string RepositoryArchiveUrl =
        "https://github.com/MCPsDE/nonebot-plugin-for-osumania4k/archive/refs/heads/main.zip";
    private const long MaximumArchiveBytes = 64 * 1024 * 1024;
    private const long MaximumExpandedBytes = 256 * 1024 * 1024;
    private const int MaximumArchiveEntries = 20_000;

    static BotPluginSynchronizer()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("MalodyV-Rework-Overlay");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public static async Task<BotPluginSyncResult> EnsureCurrentAsync(
        OverlaySettings settings,
        CancellationToken cancellationToken = default)
    {
        var localDirectory = Path.Combine(AppContext.BaseDirectory, DirectoryName);
        if (!settings.BotPluginSyncEnabled || string.IsNullOrWhiteSpace(settings.BotPluginManifestUrl))
            return new(false, null, null);

        BotPluginManifest? manifest;
        try
        {
            if (!Uri.TryCreate(settings.BotPluginManifestUrl, UriKind.Absolute, out var manifestUri)
                || manifestUri.Scheme != Uri.UriSchemeHttps
                || !string.Equals(manifestUri.Host, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
                || !manifestUri.AbsolutePath.Equals(
                    "/MCPsDE/nonebot-plugin-for-osumania4k/main/bepinex-bot-manifest.json",
                    StringComparison.Ordinal))
                return new(false, null, "Remote bot card manifest URL is not trusted.");

            manifest = await Http.GetFromJsonAsync<BotPluginManifest>(
                manifestUri, cancellationToken);
        }
        catch (Exception exception)
        {
            return new(false, null, $"Remote bot card manifest unavailable: {exception.Message}");
        }

        if (manifest is null
            || string.IsNullOrWhiteSpace(manifest.Version)
            || manifest.DirectorySha256 is not { Length: 64 } directoryHash
            || !directoryHash.All(Uri.IsHexDigit)
            || !Uri.TryCreate(manifest.ArchiveUrl, UriKind.Absolute, out var archiveUri)
            || !string.Equals(archiveUri.AbsoluteUri, RepositoryArchiveUrl, StringComparison.Ordinal))
            return new(false, manifest?.Version, "Remote bot card manifest is invalid.");

        var workDirectory = Path.Combine(AppContext.BaseDirectory, $".bot-card-sync-{Guid.NewGuid():N}");
        try
        {
            var localHash = ComputeDirectorySha256(localDirectory);
            if (string.Equals(localHash, manifest.DirectorySha256, StringComparison.OrdinalIgnoreCase))
                return new(false, manifest.Version, null);

            Directory.CreateDirectory(workDirectory);
            var archivePath = Path.Combine(workDirectory, "source.zip");
            using (var response = await Http.GetAsync(archiveUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                var finalUri = response.RequestMessage?.RequestUri;
                if (finalUri is null
                    || finalUri.Scheme != Uri.UriSchemeHttps
                    || !(string.Equals(finalUri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(finalUri.Host, "codeload.github.com", StringComparison.OrdinalIgnoreCase)))
                    return new(false, manifest.Version, "Remote archive redirected to an untrusted host.");
                if (response.Content.Headers.ContentLength is > MaximumArchiveBytes)
                    return new(false, manifest.Version, "Remote archive exceeds the download size limit.");

                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var target = File.Create(archivePath);
                var buffer = new byte[64 * 1024];
                long totalBytes = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    totalBytes += read;
                    if (totalBytes > MaximumArchiveBytes)
                        return new(false, manifest.Version, "Remote archive exceeds the download size limit.");
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            var extractedRoot = Path.Combine(workDirectory, "extracted");
            ExtractArchiveSafely(archivePath, extractedRoot);
            var repositoryRoot = Directory.EnumerateDirectories(extractedRoot, "*", SearchOption.TopDirectoryOnly)
                .SingleOrDefault(path => Path.GetFileName(path).StartsWith(
                    "nonebot-plugin-for-osumania4k-", StringComparison.Ordinal));
            var downloadedDirectory = repositoryRoot is null
                ? null
                : Path.Combine(repositoryRoot, DirectoryName);
            if (downloadedDirectory is null || !Directory.Exists(downloadedDirectory))
                return new(false, manifest.Version, "Remote archive does not contain nonebot_plugin_rework.");

            var downloadedHash = ComputeDirectorySha256(downloadedDirectory);
            if (!string.Equals(downloadedHash, manifest.DirectorySha256, StringComparison.OrdinalIgnoreCase))
                return new(false, manifest.Version, "Remote bot card archive failed SHA-256 verification.");

            var stagedDirectory = Path.Combine(workDirectory, DirectoryName);
            CopyDirectory(downloadedDirectory, stagedDirectory);
            ReplaceDirectoryAtomically(localDirectory, stagedDirectory, workDirectory);
            return new(true, manifest.Version, null);
        }
        catch (Exception exception)
        {
            return new(false, manifest.Version, exception.Message);
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
        }
    }

    public static string? ComputeDirectorySha256(string directory)
    {
        if (!Directory.Exists(directory))
            return null;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                     .Where(path => !path.Split(Path.DirectorySeparatorChar).Contains("__pycache__")
                         && !path.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(path => Path.GetRelativePath(directory, path)
                         .Replace(Path.DirectorySeparatorChar, '/'), StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData(new byte[] { 0 });
            if (TextExtensions.Contains(Path.GetExtension(file)))
            {
                var text = Encoding.UTF8.GetString(File.ReadAllBytes(file))
                    .Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Replace('\r', '\n');
                hash.AppendData(Encoding.UTF8.GetBytes(text));
            }
            else
            {
                using var stream = File.OpenRead(file);
                var buffer = new byte[128 * 1024];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    hash.AppendData(buffer, 0, read);
            }
            hash.AppendData(new byte[] { 0 });
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void ExtractArchiveSafely(string archivePath, string destination)
    {
        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > MaximumArchiveEntries)
            throw new InvalidDataException("Remote archive contains too many entries.");

        long expandedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.Length < 0 || entry.CompressedLength < 0 || entry.Length > MaximumExpandedBytes)
                throw new InvalidDataException("Remote archive contains an invalid entry.");
            if (expandedBytes > MaximumExpandedBytes - entry.Length)
                throw new InvalidDataException("Remote archive exceeds the extraction size limit.");
            expandedBytes += entry.Length;

            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Remote archive contains an unsafe path.");
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("Remote archive contains a symbolic link.");
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = File.Create(target);
            input.CopyTo(output);
        }
    }

    private static void ReplaceDirectoryAtomically(string current, string staged, string workDirectory)
    {
        var backup = Path.Combine(workDirectory, "previous");
        var movedCurrent = false;
        try
        {
            if (Directory.Exists(current))
            {
                Directory.Move(current, backup);
                movedCurrent = true;
            }
            Directory.Move(staged, current);
            if (movedCurrent)
                TryDeleteDirectory(backup);
        }
        catch
        {
            if (Directory.Exists(current))
                TryDeleteDirectory(current);
            if (movedCurrent && Directory.Exists(backup))
                Directory.Move(backup, current);
            throw;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch
        {
        }
    }
}
