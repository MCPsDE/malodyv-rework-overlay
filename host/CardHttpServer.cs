using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace MalodyV.ReworkOverlay;

internal sealed class CardHttpServer : IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> SettingsOverrides = new Dictionary<string, string>
    {
        ["contentBar"] = "\"Pattern\"",
        ["srText"] = "\"Fixed Density v1\"",
        ["diffText"] = "\"Difficulty\"",
        ["showModeTagCapsule"] = "true",
        ["enableOsuTheme"] = "true",
        ["useOsuFont"] = "true",
        ["enableFloatingTriangles"] = "true",
        ["enableCoverArt"] = "true",
        ["customBackgroundColor"] = "\"#000000\"",
        ["enableEtternaRainbowBars"] = "false",
        ["enableStatusMarquee"] = "true",
        ["enableNumericDifficulty"] = "true",
        ["enableLNDifficulty"] = "true",
        ["reverseCardExtendDirection"] = "false",
        ["cardVisibility"] = "\"Always\"",
        ["cardOpacity"] = "\"95%\"",
        ["cardBgBlur"] = "\"4px\"",
        ["cardRadius"] = "\"Medium\"",
        ["enablePauseDetection"] = "true",
        ["VibroDetection"] = "true",
        ["useSvDetection"] = "false",
        ["display6kLevel"] = "true",
        ["extendedEstimationRange"] = "false",
        ["forceSunnyWindow"] = "true",
        ["enableAnalyzeLN"] = "true",
        ["pauseDetectionThreshold"] = "\"500\"",
        ["estimatorAlgorithm"] = "\"Mixed\"",
        ["etternaVersion"] = "\"0.72.3\"",
        ["companellaEtternaVersion"] = "\"0.74.0\"",
        ["enableUpdateCheck"] = "false",
        ["enableTelemetry"] = "false",
        ["preset"] = "\"LastSavedPreset\"",
    };

    private string WebRoot => Path.Combine(
        AppContext.BaseDirectory, "nonebot_plugin_rework", "tosu_plugin");
    private readonly CancellationTokenSource _stop = new();
    private readonly object _stateLock = new();
    private readonly TcpListener _listener;
    private Task? _acceptTask;
    private MapPayload? _currentMap;
    private int _port;
    private string _settingsJson = "[]";

    public string PageUrl => $"http://127.0.0.1:{_port}/index.html";

    public CardHttpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
    }

    public void Start()
    {
        if (!File.Exists(Path.Combine(WebRoot, "index.html")))
            throw new FileNotFoundException("The bot plugin card assets are missing nonebot_plugin_rework/tosu_plugin/index.html.");
        _listener.Start(64);
        _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _settingsJson = BuildSettingsJson();
        _acceptTask = Task.Run(() => AcceptLoopAsync(_stop.Token));
    }

    public void SetCurrentMap(MapPayload? payload)
    {
        lock (_stateLock)
            _currentMap = payload;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(() => HandleClientAsync(client, cancellationToken), CancellationToken.None);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                if (!cancellationToken.IsCancellationRequested)
                    await Task.Delay(200, cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(requestLine))
                    return;
                var parts = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    await WriteResponseAsync(stream, 400, "text/plain", Encoding.UTF8.GetBytes("Bad request"), cancellationToken);
                    return;
                }
                var isHead = parts[0].Equals("HEAD", StringComparison.OrdinalIgnoreCase);
                if (!isHead && !parts[0].Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteResponseAsync(stream, 405, "text/plain", [], cancellationToken);
                    return;
                }
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken)))
                {
                }

                var requestPath = parts[1].Split('?', 2)[0];
                var response = ResolveRequest(requestPath);
                await WriteResponseAsync(stream, response.Status, response.ContentType,
                    isHead ? [] : response.Body, cancellationToken, response.Body.Length);
            }
            catch
            {
            }
        }
    }

    private (int Status, string ContentType, byte[] Body) ResolveRequest(string requestPath)
    {
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(requestPath);
        }
        catch
        {
            return (400, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Bad path"));
        }
        var route = decoded.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        lock (_stateLock)
        {
            if (decoded.Equals("/settings.json", StringComparison.OrdinalIgnoreCase))
                return (200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(_settingsJson));
            if (decoded.Equals("/files/beatmap/file", StringComparison.OrdinalIgnoreCase))
                return _currentMap is null
                    ? (503, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("No selected chart"))
                    : (200, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(_currentMap.OsuText));
            if (decoded.Equals("/files/beatmap/background", StringComparison.OrdinalIgnoreCase))
                return _currentMap?.Background is { Length: > 0 } image
                    ? (200, DetectImageType(image), image)
                    : (404, "text/plain; charset=utf-8", []);
            if (decoded.Equals("/rework/reference-metrics", StringComparison.OrdinalIgnoreCase))
                return (200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(_currentMap?.ReferenceMetricsJson ?? "{}"));
            if (decoded.Equals("/rework/analysis-options", StringComparison.OrdinalIgnoreCase))
            {
                var options = new JsonObject
                {
                    ["speedRate"] = _currentMap?.SpeedRate ?? 1d,
                    ["odFlag"] = null,
                    ["cvtFlag"] = null,
                    ["modCodes"] = new JsonArray(),
                    ["gameModMask"] = _currentMap?.Mods ?? 0u,
                    ["gameModSignature"] = _currentMap?.AnalysisSignature ?? "none",
                    ["offline"] = true,
                };
                return (200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(options.ToJsonString()));
            }
        }

        if (string.IsNullOrWhiteSpace(route) || route.Contains(':') || route.Split(Path.DirectorySeparatorChar).Any(part => part == ".."))
            return (400, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Bad path"));
        var fullPath = Path.GetFullPath(Path.Combine(WebRoot, route));
        var rootPrefix = Path.GetFullPath(WebRoot) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            return (404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not found"));
        return (200, ContentTypeFor(fullPath), File.ReadAllBytes(fullPath));
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status, string contentType,
        byte[] body, CancellationToken cancellationToken, int? contentLength = null)
    {
        var description = status switch { 200 => "OK", 400 => "Bad Request", 404 => "Not Found", 405 => "Method Not Allowed", _ => "Service Unavailable" };
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {description}\r\nContent-Type: {contentType}\r\nContent-Length: {contentLength ?? body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken);
        if (body.Length > 0)
            await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private string BuildSettingsJson()
    {
        var path = Path.Combine(WebRoot, "settings.json");
        var settings = JsonNode.Parse(File.ReadAllText(path)) as JsonArray ?? [];
        foreach (var item in settings.OfType<JsonObject>())
        {
            if (item["uniqueID"]?.GetValue<string>() is { } id && SettingsOverrides.TryGetValue(id, out var raw))
                item["value"] = JsonNode.Parse(raw);
            if (item["uniqueID"]?.GetValue<string>() == "wsEndpoint")
                item["value"] = $"127.0.0.1:{_port}";
        }
        return settings.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
    }

    private static string DetectImageType(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff ? "image/jpeg" :
        bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 ? "image/png" : "application/octet-stream";

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".woff2" => "font/woff2",
        ".wasm" => "application/wasm",
        ".onnx" => "application/octet-stream",
        ".webp" => "image/webp",
        _ => "application/octet-stream",
    };

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }
}
