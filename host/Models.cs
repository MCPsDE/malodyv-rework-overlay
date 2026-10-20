using System.Text.Json.Serialization;

namespace MalodyV.ReworkOverlay;

internal sealed class SelectionMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "selection";

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("index")]
    public uint Index { get; init; }

    [JsonPropertyName("identity")]
    public string? Identity { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("mods")]
    public uint Mods { get; init; }

    [JsonPropertyName("speedRate")]
    public double SpeedRate { get; init; } = 1;

    [JsonPropertyName("playMode")]
    public int PlayMode { get; init; } = -1;

    [JsonPropertyName("randomSeed")]
    public int RandomSeed { get; init; }

    [JsonPropertyName("turboSignature")]
    public string? TurboSignature { get; init; }

    [JsonPropertyName("pluginVersion")]
    public string? PluginVersion { get; init; }

    [JsonPropertyName("pluginSha256")]
    public string? PluginSha256 { get; init; }

    [JsonPropertyName("remotePluginVersion")]
    public string? RemotePluginVersion { get; init; }

    [JsonPropertyName("remotePluginSha256")]
    public string? RemotePluginSha256 { get; init; }
}

internal sealed record MapPayload(
    string Identity,
    string SourcePath,
    uint Index,
    string OsuText,
    byte[]? Background,
    string ReferenceMetricsJson,
    string Title,
    uint Mods,
    double SpeedRate,
    string AnalysisSignature);

internal sealed class OverlaySettings
{
    public bool Enabled { get; set; } = true;
    public bool AlwaysOnTop { get; set; } = true;
    public bool DragEnabled { get; set; } = true;
    public bool HideDuringPlay { get; set; } = true;
    public double Opacity { get; set; } = 0.95;
    public double Left { get; set; } = 40;
    public double Top { get; set; } = 80;
    public double Width { get; set; } = 515;
    public double Height { get; set; } = 610;
    public string ToggleHotkey { get; set; } = "Ctrl+Shift+F8";
    public string Language { get; set; } = "zh-CN";
    public bool UpdateCheckEnabled { get; set; } = true;
    public string UpdateManifestUrl { get; set; } =
        "https://raw.githubusercontent.com/MCPsDE/malodyv-rework-overlay/main/manifest.json";
    public bool BotPluginSyncEnabled { get; set; } = true;
    public string BotPluginManifestUrl { get; set; } =
        "https://raw.githubusercontent.com/MCPsDE/nonebot-plugin-for-osumania4k/main/bepinex-bot-manifest.json";
}

internal static class SettingsStore
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MalodyVReworkOverlay", "settings.json");

    public static OverlaySettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return System.Text.Json.JsonSerializer.Deserialize<OverlaySettings>(File.ReadAllText(SettingsPath)) ?? new();
        }
        catch
        {
        }
        return new OverlaySettings();
    }

    public static void Save(OverlaySettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, System.Text.Json.JsonSerializer.Serialize(settings,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
}
