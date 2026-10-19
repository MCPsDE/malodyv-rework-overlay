using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace MalodyV.ReworkOverlay;

internal sealed class OverlayRuntime : IDisposable
{
    public const string PipeName = "MalodyV.ReworkOverlay.Selection";
    private readonly OverlaySettings _settings;
    private readonly CancellationTokenSource _stop = new();
    private readonly CardHttpServer _server;
    private MapPayload? _currentMap;

    public event Action<SelectionMessage?>? SelectionReceived;
    public event Action<string>? StatusChanged;
    public event Action<bool>? GameStateChanged;
    public event Action<SelectionMessage>? PluginIntegrityMismatch;
    public MapPayload? CurrentMap => Volatile.Read(ref _currentMap);
    public string PageUrl => _server.PageUrl;

    public OverlayRuntime(OverlaySettings settings)
    {
        _settings = settings;
        _server = new CardHttpServer();
    }

    public void Start()
    {
        _server.Start();
        _ = Task.Run(() => PipeLoopAsync(_stop.Token));
    }

    public void SetCurrentMap(MapPayload? payload)
    {
        Volatile.Write(ref _currentMap, payload);
        _server.SetCurrentMap(payload);
    }

    private async Task PipeLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                var line = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                var update = JsonSerializer.Deserialize<SelectionMessage>(line,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (update?.Type == "connected")
                {
                    StatusChanged?.Invoke("Connected to game");
                    continue;
                }
                if (update?.Type == "plugin-integrity-mismatch")
                {
                    PluginIntegrityMismatch?.Invoke(update);
                    continue;
                }
                if (update?.Type == "gameplay")
                {
                    GameStateChanged?.Invoke(true);
                    continue;
                }
                if (update?.Type == "result")
                {
                    GameStateChanged?.Invoke(false);
                    continue;
                }
                if (update?.Type != "selection")
                    continue;
                StatusChanged?.Invoke("Connected to game");
                SelectionReceived?.Invoke(string.IsNullOrWhiteSpace(update.Path) ? null : update);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                StatusChanged?.Invoke($"Pipe reconnecting: {exception.Message}");
                await Task.Delay(1000, cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _server.Dispose();
        _stop.Dispose();
    }
}
