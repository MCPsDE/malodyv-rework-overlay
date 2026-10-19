using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
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

    [JsonPropertyName("downloadUrl")]
    public string? DownloadUrl { get; init; }
}

internal sealed record UpdateCheckResult(
    string LocalVersion,
    string RemoteVersion,
    string? LocalPluginSha256,
    string? RemotePluginSha256,
    string? LocalHostSha256,
    string? RemoteHostSha256,
    string? DownloadUrl);

internal static class UpdateChecker
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public static string LocalVersion =>
        typeof(UpdateChecker).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public static async Task<UpdateCheckResult?> CheckAsync(
        OverlaySettings settings,
        CancellationToken cancellationToken = default)
    {
        if (!settings.UpdateCheckEnabled || string.IsNullOrWhiteSpace(settings.UpdateManifestUrl))
            return null;

        UpdateManifest? manifest;
        try
        {
            if (!Uri.TryCreate(settings.UpdateManifestUrl, UriKind.Absolute, out var manifestUri)
                || manifestUri.Scheme != Uri.UriSchemeHttps
                || !string.Equals(manifestUri.Host, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
                || !manifestUri.AbsolutePath.Equals(
                    "/MCPsDE/malodyv-rework-overlay/main/manifest.json", StringComparison.Ordinal))
                return null;
            manifest = await Http.GetFromJsonAsync<UpdateManifest>(manifestUri, cancellationToken);
        }
        catch
        {
            return null;
        }

        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version))
            return null;

        var pluginPath = Path.Combine(AppContext.BaseDirectory, "MalodyV.ReworkOverlay.Plugin.dll");
        var hostPath = Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "MalodyV.ReworkOverlay.exe");
        var localPlugin = ComputeSha256(pluginPath);
        var localHost = ComputeSha256(hostPath);
        var mismatch = !string.Equals(localHost, manifest.HostSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(LocalVersion, manifest.Version, StringComparison.OrdinalIgnoreCase);
        return mismatch
            ? new UpdateCheckResult(
                LocalVersion,
                manifest.Version,
                localPlugin,
                manifest.PluginSha256,
                localHost,
                manifest.HostSha256,
                manifest.DownloadUrl)
            : null;
    }

    public static string? ComputeSha256(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch
        {
            return null;
        }
    }
}
