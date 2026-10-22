using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MalodyV.ReworkOverlay;

internal sealed class UpdateManifest
{
    [JsonPropertyName("version")]
    public string? Version { get; init; }
    [JsonPropertyName("pluginSha256")]
    public string? PluginSha256 { get; init; }
    [JsonPropertyName("hostSha256")]
    public string? HostSha256 { get; init; }
    [JsonPropertyName("hostAssemblySha256")]
    public string? HostAssemblySha256 { get; init; }
    [JsonPropertyName("packageUrl")]
    public string? PackageUrl { get; init; }
    [JsonPropertyName("packageSha256")]
    public string? PackageSha256 { get; init; }
    [JsonPropertyName("botDirectorySha256")]
    public string? BotDirectorySha256 { get; init; }
    [JsonPropertyName("downloadUrl")]
    public string? DownloadUrl { get; init; }
}

internal static class UpdateChecker
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false });
    private static readonly string[] ManifestUrls =
    {
        "https://raw.githubusercontent.com/MCPsDE/malodyv-rework-overlay/main/manifest.json",
        "https://cdn.jsdelivr.net/gh/MCPsDE/malodyv-rework-overlay@main/manifest.json",
    };

    public static string LocalVersion =>
        typeof(UpdateChecker).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public static async Task<UpdateManifest?> CheckAsync(CancellationToken cancellationToken = default)
    {
        foreach (var url in ManifestUrls)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                using var response = await Http.GetAsync(url, timeout.Token);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(timeout.Token);
                if (json.Length > 64 * 1024)
                    continue;
                var manifest = JsonSerializer.Deserialize<UpdateManifest>(json);
                if (manifest is not null && IsValidManifest(manifest))
                    return NeedsUpdate(manifest, AppContext.BaseDirectory, Version.Parse(LocalVersion)) ? manifest : null;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested
                && exception is HttpRequestException or OperationCanceledException or JsonException)
            {
            }
        }
        return null;
    }

    internal static bool IsValidManifest(UpdateManifest manifest)
    {
        return Version.TryParse(manifest.Version, out var version) && version.Build >= 0 && version.Revision < 0
            && manifest.Version == version.ToString(3)
            && IsHash(manifest.PluginSha256) && IsHash(manifest.HostSha256)
            && IsHash(manifest.HostAssemblySha256) && IsHash(manifest.PackageSha256)
            && manifest.PackageUrl == PackageUrlFor(manifest.Version);
    }

    internal static string PackageUrlFor(string? version) =>
        $"https://github.com/MCPsDE/malodyv-rework-overlay/releases/download/v{version}/MalodyVReworkOverlay-{version}.zip";

    internal static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    internal static bool NeedsUpdate(UpdateManifest manifest, string hostDirectory, Version localVersion)
    {
        var remoteVersion = Version.Parse(manifest.Version!);
        if (remoteVersion < localVersion)
            return false;
        if (remoteVersion > localVersion)
            return true;
        var pluginRoot = Directory.GetParent(Path.GetFullPath(hostDirectory).TrimEnd(Path.DirectorySeparatorChar))!.FullName;
        return !Matches(Path.Combine(hostDirectory, "MalodyV.ReworkOverlay.exe"), manifest.HostSha256)
            || !Matches(Path.Combine(hostDirectory, "MalodyV.ReworkOverlay.dll"), manifest.HostAssemblySha256)
            || !Matches(Path.Combine(pluginRoot, "MalodyV.ReworkOverlay.Plugin.dll"), manifest.PluginSha256);
    }

    internal static bool Matches(string path, string? expected) => IsHash(expected)
        && string.Equals(ComputeSha256(path), expected, StringComparison.OrdinalIgnoreCase);

    public static string? ComputeSha256(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
