using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace MalodyV.ReworkOverlay.Plugin;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "com.baka666.malodyv.reworkoverlay";
    public const string PluginName = "MalodyV Rework Overlay";
    public const string PluginVersion = "1.3.0";
    internal const string UpdateManifestUrl =
        "https://raw.githubusercontent.com/MCPsDE/malodyv-rework-overlay/main/manifest.json";
    private const string RuntimeChartTypeName = "Malody.Chart.iqy";
    private static readonly string HostPath = Path.Combine(Paths.PluginPath, "MalodyVReworkOverlay", "MalodyV.ReworkOverlay.exe");
    private static Harmony? _harmony;

    public override void Load()
    {
        try
        {
            MainThreadDispatcher.Initialize();
            StartCompanion();
        }
        catch (Exception exception)
        {
            Log.LogError($"Failed to launch the card host: {exception}");
        }

        SelectionPipe.Start(Log);
        _harmony = new Harmony(PluginGuid);
        TryPatch(FindChartNotificationMethod, nameof(SelectionPatch.NotificationPosted), "chart notification");
        TryPatch(() => FindMethod("Malody.Scene.Item.ItemChart", "OnPointerClick"), nameof(SelectionPatch.ChartItemChanged), "hall chart click");
        TryPatch(() => FindMethod("Malody.Scene.Item.ItemChart", "SetSelected"), nameof(SelectionPatch.ChartItemChanged), "hall chart focus");
        TryPatch(() => FindMethod("Malody.Scene.Panel.PanelMod", "OnDisable"), nameof(SelectionPatch.ModSettingsChanged), "hall and room mod selection");
        TryPatch(() => FindMethod("Malody.Scene.Panel.PanelJudge", "OnDisable"), nameof(SelectionPatch.ModSettingsChanged), "Turbo settings");
        TryPatch(() => FindMethod("Malody.Scene.SceneRoom", "HandleConfigChange"), nameof(SelectionPatch.ModSettingsChanged), "online room mod sync");
        TryPatch(FindRoomChartPublisher, nameof(SelectionPatch.RoomChartResolved), "online room chart resolution");
        TryPatch(() => FindMethod("Malody.Scene.SceneChart", "OnClose"), nameof(SelectionPatch.SelectionCleared), "chart scene exit");
        TryPatch(() => FindMethod("Malody.Scene.SceneRoom", "OnBack"), nameof(SelectionPatch.SelectionCleared), "online room exit");
        TryPatch(() => FindMethod("Malody.Scene.SceneRoomWatch", "OnBack"), nameof(SelectionPatch.SelectionCleared), "room watch exit");
        TryPatch(() => FindMethod("Malody.Play.ChartPlayer", "Play"), nameof(SelectionPatch.GameplayStarted), "gameplay start");
        TryPatch(() => FindMethod("Malody.Scene.SceneResult", "OnEnable"), nameof(SelectionPatch.ResultShown), "result screen");
        try
        {
            var playInfo = FindPlayInfoMembers();
            Log.LogInfo($"Resolved current chart owner {playInfo.ChartProperty.DeclaringType?.FullName}.{playInfo.ChartProperty.Name}.");
            TryPatch(() => playInfo.ChartProperty.GetSetMethod(true)
                ?? throw new MissingMethodException(playInfo.ChartProperty.DeclaringType?.FullName, "set_Chart"),
                nameof(SelectionPatch.CurrentChartChanged), "current chart state change");
            TryPatch(() => playInfo.ChartProperty.GetGetMethod(true)
                ?? throw new MissingMethodException(playInfo.ChartProperty.DeclaringType?.FullName, "get_Chart"),
                nameof(SelectionPatch.CurrentChartRead), "current chart state read");
        }
        catch (Exception exception)
        {
            Log.LogError($"Failed to locate the current chart state: {exception.Message}");
        }
        SelectionPipe.PublishCurrentChart();
    }

    private void TryPatch(Func<MethodBase> findTarget, string postfixName, string description)
    {
        try
        {
            var targetMethod = findTarget();
            var postfix = new HarmonyMethod(typeof(SelectionPatch), postfixName);
            _harmony!.Patch(targetMethod, postfix: postfix);
            Log.LogInfo($"Patched {description}: {targetMethod.DeclaringType?.FullName}.{targetMethod.Name}.");
        }
        catch (Exception exception)
        {
            Log.LogError($"Failed to patch {description}: {exception.Message}");
        }
    }

    private static MethodInfo FindChartNotificationMethod()
    {
        var framework = FindAssembly("Framework");
        foreach (var type in GetLoadableTypes(framework))
        {
            MethodInfo[] methods;
            try
            {
                methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
            }
            catch
            {
                continue;
            }

            foreach (var method in methods)
            {
                var parameters = method.GetParameters();
                if (method.Name != "Notify" || parameters.Length != 2 || !parameters[0].ParameterType.IsEnum)
                    continue;
                if (Enum.GetNames(parameters[0].ParameterType).Contains("ChangeChart", StringComparer.Ordinal))
                    return method;
            }
        }

        throw new MissingMethodException("Could not find the chart-change notification dispatcher in Framework.dll.");
    }

    private static MethodInfo FindRoomChartPublisher()
    {
        var chartItemType = FindType("Assembly-CSharp", "Malody.Scene.Item.ItemChart");
        var chartType = FindChartProperty(chartItemType).PropertyType;
        var panelType = FindType("Assembly-CSharp", "Malody.Scene.MP.PanelRoomSong");
        foreach (var method in panelType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            var parameters = method.GetParameters();
            if (parameters.Length == 1 && parameters[0].ParameterType == chartType)
                return method;
        }

        throw new MissingMethodException("Could not find the room chart resolver on PanelRoomSong.");
    }

    private static PropertyInfo FindChartProperty(Type itemType)
    {
        return itemType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(property => property.PropertyType.FullName == "Malody.Chart.iqy")
            ?? throw new MissingMemberException($"A chart property was not found on {itemType.FullName}.");
    }

    private static MethodInfo FindMethod(string typeName, string methodName)
    {
        var type = FindType("Assembly-CSharp", typeName);
        return AccessTools.Method(type, methodName)
            ?? throw new MissingMethodException(typeName, methodName);
    }

    private sealed record PlayInfoMembers(Type Type, PropertyInfo[] Instances, PropertyInfo ChartProperty);

    private static PlayInfoMembers FindPlayInfoMembers()
    {
        var candidates = new List<PlayInfoMembers>();
        foreach (var type in AppDomain.CurrentDomain.GetAssemblies().SelectMany(GetLoadableTypes))
        {
            try
            {
                var properties = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                var selfProperties = properties.Where(property => property.GetMethod?.IsStatic == true && property.PropertyType == type).ToArray();
                if (selfProperties.Length < 2)
                    continue;

                var chartProperty = properties.FirstOrDefault(property =>
                    property.GetMethod?.IsStatic != true &&
                    string.Equals(property.PropertyType.FullName, RuntimeChartTypeName, StringComparison.Ordinal));
                if (chartProperty is not null)
                    candidates.Add(new PlayInfoMembers(type, selfProperties, chartProperty));
            }
            catch
            {
            }
        }

        return candidates.OrderByDescending(candidate => candidate.Instances.Length).FirstOrDefault()
            ?? throw new TypeLoadException("Could not identify the obfuscated PlayInfo type by its chart and singleton properties.");
    }

    private static Type FindType(string assemblyName, string typeName)
    {
        return FindAssembly(assemblyName).GetType(typeName, throwOnError: false)
            ?? throw new TypeLoadException($"Could not find {typeName} in {assemblyName}.dll.");
    }

    private static Assembly FindAssembly(string assemblyName)
    {
        return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly =>
            string.Equals(assembly.GetName().Name, assemblyName, StringComparison.Ordinal))
            ?? throw new FileNotFoundException($"Could not find loaded {assemblyName}.dll.");
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
    }

    private void StartCompanion()
    {
        if (!File.Exists(HostPath))
            throw new FileNotFoundException("The overlay host executable is missing from the plugin package.", HostPath);

        var processName = Path.GetFileNameWithoutExtension(HostPath);
        var existing = Process.GetProcessesByName(processName)
            .FirstOrDefault(process => !process.HasExited);
        if (existing is not null)
        {
            existing.Refresh();
            if (existing.MainWindowHandle != IntPtr.Zero)
            {
                Log.LogInfo($"Reusing card host process {existing.Id}.");
                return;
            }

            Log.LogWarning($"Card host process {existing.Id} has no main window; stopping the stale instance.");
            try
            {
                existing.Kill(entireProcessTree: true);
                existing.WaitForExit(3000);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"The existing card host process {existing.Id} could not be replaced.", exception);
            }
        }

        var started = Process.Start(new ProcessStartInfo(HostPath)
        {
            WorkingDirectory = Path.GetDirectoryName(HostPath)!,
            UseShellExecute = true,
        });
        if (started is null)
            throw new InvalidOperationException("The card host process could not be started.");

        Log.LogInfo($"Started card host process {started.Id}.");
        for (var attempt = 0; attempt < 20; attempt++)
        {
            Thread.Sleep(100);
            started.Refresh();
            if (started.HasExited)
                throw new InvalidOperationException(
                    $"The card host exited during startup with code {started.ExitCode}.");
            if (started.MainWindowHandle != IntPtr.Zero)
            {
                Log.LogInfo($"Card host window is ready in process {started.Id}.");
                return;
            }
        }

        Log.LogWarning($"Card host process {started.Id} is running but has not exposed a main window yet.");
    }
}

internal sealed record SelectionMessage(
    string Type,
    string? Path,
    uint Index,
    string? Identity,
    string? Version,
    string? PluginVersion = null,
    string? PluginSha256 = null,
    string? RemotePluginVersion = null,
    string? RemotePluginSha256 = null,
    uint Mods = 0,
    double SpeedRate = 1,
    int PlayMode = -1,
    int RandomSeed = 0);

internal sealed class PluginUpdateManifest
{
    public string? Version { get; init; }
    public string? PluginSha256 { get; init; }
}

internal static class SelectionPipe
{
    private const string RuntimeChartTypeName = "Malody.Chart.iqy";
    private const string PipeName = "MalodyV.ReworkOverlay.Selection";
    private static readonly Channel<SelectionMessage> Updates = Channel.CreateBounded<SelectionMessage>(
        new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
    private static string? _lastIdentity;
    private static BepInEx.Logging.ManualLogSource? _log;
    private static readonly HttpClient UpdateHttp = new() { Timeout = TimeSpan.FromSeconds(5) };

    public static void Start(BepInEx.Logging.ManualLogSource logger)
    {
        _log = logger;
        _ = Task.Run(WriteLoopAsync);
        Updates.Writer.TryWrite(new SelectionMessage(
            "connected", null, 0, null, null, Plugin.PluginVersion, ComputeSelfSha256()));
        _ = CheckRemotePluginAsync();
    }

    private static async Task CheckRemotePluginAsync()
    {
        try
        {
            var manifest = await UpdateHttp.GetFromJsonAsync<PluginUpdateManifest>(Plugin.UpdateManifestUrl);
            var localHash = ComputeSelfSha256();
            if (manifest is null
                || string.IsNullOrWhiteSpace(manifest.Version)
                || manifest.PluginSha256 is not { Length: 64 } remoteHash
                || !remoteHash.All(Uri.IsHexDigit)
                || string.IsNullOrWhiteSpace(localHash))
                return;

            if (string.Equals(localHash, remoteHash, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Plugin.PluginVersion, manifest.Version, StringComparison.OrdinalIgnoreCase))
                return;

            Updates.Writer.TryWrite(new SelectionMessage(
                "plugin-integrity-mismatch", null, 0, null, null,
                Plugin.PluginVersion, localHash, manifest.Version, remoteHash));
        }
        catch (Exception exception)
        {
            _log?.LogWarning($"Plugin update check failed: {exception.Message}");
        }
    }

    private static string? ComputeSelfSha256()
    {
        try
        {
            var path = typeof(Plugin).Assembly.Location;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;
            using var stream = File.OpenRead(path);
            using var sha256 = SHA256.Create();
            return Convert.ToHexString(sha256.ComputeHash(stream));
        }
        catch
        {
            return null;
        }
    }

    public static void Diagnostic(string message) => _log?.LogInfo(message);

    public static void DiagnosticWarning(string message) => _log?.LogWarning(message);

    private static PropertyInfo? _chartProperty;
    private static PropertyInfo? _localPlayInfoProperty;
    private static PropertyInfo[] _playInfoInstances = Array.Empty<PropertyInfo>();
    private static Type? _runtimeChartType;
    private static ConstructorInfo? _runtimeChartConstructor;
    private static long _lastPollAt;

    public static void PublishCurrentChart()
    {
        try
        {
            ResolvePlayInfoProperties();
            foreach (var instanceProperty in _playInfoInstances)
            {
                var instance = instanceProperty.GetValue(null);
                var chart = instance is null ? null : _chartProperty!.GetValue(instance);
                if (chart is not null)
                {
                    Diagnostic($"Initial current chart read from {instanceProperty.Name}: {chart.GetType().FullName}.");
                    PublishChart(chart);
                }
            }
        }
        catch (Exception exception)
        {
            DiagnosticWarning($"Initial current chart read failed: {exception}");
        }
    }

    public static void PublishCurrentChart(object? chart) => PublishChart(chart);

    public static void PublishCurrentChartIfChanged(object? chart)
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastPollAt) < 500)
            return;
        Interlocked.Exchange(ref _lastPollAt, now);
        PublishChart(chart);
    }

    public static void PublishCurrentChartDelayed()
    {
        MainThreadDispatcher.PostDelayed(PublishCurrentChart);
    }

    private static void ResolvePlayInfoProperties()
    {
        if (_chartProperty is not null && _playInfoInstances.Length > 0)
            return;

        var candidates = new List<(Type Type, PropertyInfo[] Instances, PropertyInfo Chart)>();
        foreach (var type in AppDomain.CurrentDomain.GetAssemblies().SelectMany(GetLoadableTypes))
        {
            try
            {
                var properties = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                var selfProperties = properties.Where(property => property.GetMethod?.IsStatic == true && property.PropertyType == type).ToArray();
                if (selfProperties.Length < 2)
                    continue;
                var chartProperty = properties.FirstOrDefault(property =>
                    property.GetMethod?.IsStatic != true &&
                    string.Equals(property.PropertyType.FullName, RuntimeChartTypeName, StringComparison.Ordinal));
                if (chartProperty is not null)
                    candidates.Add((type, selfProperties, chartProperty));
            }
            catch
            {
            }
        }

        var match = candidates.OrderByDescending(candidate => candidate.Instances.Length).FirstOrDefault();
        if (match.Type is null)
            throw new MissingMemberException("Could not identify the obfuscated PlayInfo chart state.");
        _playInfoInstances = match.Instances;
        _localPlayInfoProperty = match.Instances.FirstOrDefault(property =>
            string.Equals(property.Name, "Local", StringComparison.OrdinalIgnoreCase)) ?? match.Instances[0];
        _chartProperty = match.Chart;
        _runtimeChartType = match.Chart.PropertyType;
        _runtimeChartConstructor = _runtimeChartType.GetConstructor(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, new[] { typeof(IntPtr) }, modifiers: null);
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
    }

    public static void PublishChart(object? chart)
    {
        try
        {
            chart = CastChartObject(chart);
            if (chart is null)
            {
                Clear();
                return;
            }
            var chartType = chart.GetType();
            var pathProperty = FindProperty(chartType, "FilePath");
            var indexProperty = FindProperty(chartType, "IndexInFile");
            var versionProperty = FindProperty(chartType, "DisplayVersion");
            var path = pathProperty?.GetValue(chart)?.ToString();
            var rawIndex = indexProperty?.GetValue(chart);
            var version = versionProperty?.GetValue(chart)?.ToString();
            if (string.IsNullOrWhiteSpace(path) || rawIndex is null)
            {
                var metaProperty = chartType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(property => string.Equals(property.PropertyType.FullName, "Malody.DB.ChartRecord", StringComparison.Ordinal));
                var metadata = metaProperty?.GetValue(chart);
                if (metadata is not null)
                {
                    var metadataType = metadata.GetType();
                    path ??= FindProperty(metadataType, "Path")?.GetValue(metadata)?.ToString();
                    rawIndex ??= FindProperty(metadataType, "Index")?.GetValue(metadata);
                    version ??= FindProperty(metadataType, "Version")?.GetValue(metadata)?.ToString();
                }
            }
            if (string.IsNullOrWhiteSpace(path) || rawIndex is null)
            {
                var metadataProperty = chartType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(property => string.Equals(property.PropertyType.FullName, "Malody.DB.ChartRecord", StringComparison.Ordinal));
                var metadata = metadataProperty?.GetValue(chart);
                var metadataProperties = metadata is null ? "<missing>" : string.Join(", ", metadata.GetType()
                    .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Select(property => $"{property.Name}:{property.PropertyType.FullName}"));
                var properties = string.Join(", ", chartType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Select(property => $"{property.Name}:{property.PropertyType.FullName}"));
                throw new InvalidDataException(
                    $"Selected chart did not expose FilePath and IndexInFile (type {chartType.FullName}; " +
                    $"FilePath={pathProperty?.Name ?? "<missing>"}; IndexInFile={indexProperty?.Name ?? "<missing>"}; " +
                    $"properties=[{properties}]; metadata=[{metadataProperties}]).");
            }
            var index = Convert.ToUInt32(rawIndex);
            var (mods, speedRate, playMode, randomSeed) = ReadActiveModState();
            var identity = $"{Path.GetFullPath(path)}|{index}|{version}|{mods}|{speedRate.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture)}|{playMode}|{randomSeed}";
            if (string.Equals(Interlocked.Exchange(ref _lastIdentity, identity), identity, StringComparison.OrdinalIgnoreCase))
                return;
            _log?.LogInfo($"Selected chart: {version} ({path}, difficulty {index + 1}); mods={mods}; speed={speedRate:0.##}x.");
            if (!Updates.Writer.TryWrite(new SelectionMessage("selection", path, index, identity, version,
                    Mods: mods, SpeedRate: speedRate, PlayMode: playMode, RandomSeed: randomSeed)))
                _log?.LogWarning("The selected chart update could not be queued for the overlay host.");
        }
        catch (Exception exception)
        {
            _log?.LogWarning($"Unable to read selected chart: {exception.Message}");
        }
    }

    private static (uint Mods, double SpeedRate, int PlayMode, int RandomSeed) ReadActiveModState()
    {
        try
        {
            ResolvePlayInfoProperties();
            var local = _localPlayInfoProperty?.GetValue(null);
            if (local is null)
                return (0, 1, -1, 0);

            var localType = local.GetType();
            var rawMods = ReadMember(localType, local, "Mod");
            var mods = rawMods is null ? 0u : Convert.ToUInt32(rawMods);
            var customJudge = ReadMember(localType, local, "customJudge");
            var speed = customJudge is null ? 1d : ReadDouble(ReadMember(customJudge.GetType(), customJudge, "audioSpeed"), 1d);
            if (customJudge is null || (mods & 0x8000u) != 0)
            {
                if ((mods & 0x20u) != 0) speed = 1.5;
                else if ((mods & 0x10u) != 0) speed = 1.2;
                else if ((mods & 0x100u) != 0) speed = 0.8;
            }

            var mode = ReadMember(localType, local, "playMode");
            var randomSeed = ReadMember(localType, local, "RandomSeed");
            return (mods, double.IsFinite(speed) && speed > 0 ? speed : 1d,
                mode is null ? -1 : Convert.ToInt32(mode), randomSeed is null ? 0 : Convert.ToInt32(randomSeed));
        }
        catch (Exception exception)
        {
            _log?.LogWarning($"Unable to read active MalodyV mods: {exception.Message}");
            return (0, 1, -1, 0);
        }
    }

    private static object? ReadMember(Type type, object instance, string name)
    {
        var property = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
        if (property is not null)
            return property.GetValue(instance);
        var field = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
        return field?.GetValue(instance);
    }

    private static double ReadDouble(object? value, double fallback) =>
        value is null ? fallback : Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);

    private static PropertyInfo? FindProperty(Type type, string name) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase));

    private static object? CastChartObject(object? value)
    {
        if (value is null || string.Equals(value.GetType().FullName, RuntimeChartTypeName, StringComparison.Ordinal))
            return value;

        var pointer = FindProperty(value.GetType(), "Pointer")?.GetValue(value);
        if (pointer is not IntPtr nativePointer || nativePointer == IntPtr.Zero)
            return value;

        _runtimeChartType ??= AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(GetLoadableTypes)
            .FirstOrDefault(type => string.Equals(type.FullName, RuntimeChartTypeName, StringComparison.Ordinal));
        _runtimeChartConstructor ??= _runtimeChartType?.GetConstructor(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, new[] { typeof(IntPtr) }, modifiers: null);
        return _runtimeChartConstructor?.Invoke(new object[] { nativePointer }) ?? value;
    }

    public static void Clear()
    {
        if (Interlocked.Exchange(ref _lastIdentity, null) is not null)
            Updates.Writer.TryWrite(new SelectionMessage("selection", null, 0, null, null));
    }

    public static void PublishGameState(bool isPlaying) =>
        Updates.Writer.TryWrite(new SelectionMessage(isPlaying ? "gameplay" : "result", null, 0, null, null));

    private static async Task WriteLoopAsync()
    {
        await foreach (var update in Updates.Reader.ReadAllAsync())
        {
            var json = JsonSerializer.Serialize(update) + "\n";
            var sent = false;
            for (var attempt = 0; attempt < 20 && !sent; attempt++)
            {
                try
                {
                    await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                    await pipe.ConnectAsync(1000);
                    var bytes = Encoding.UTF8.GetBytes(json);
                    await pipe.WriteAsync(bytes);
                    await pipe.FlushAsync();
                    sent = true;
                }
                catch (TimeoutException)
                {
                    await Task.Delay(250);
                }
                catch (IOException)
                {
                    await Task.Delay(250);
                }
            }
            if (!sent)
                _log?.LogWarning("Overlay host is not accepting map updates; the selection will retry on its next change.");
            else
                _log?.LogInfo($"Sent {update.Type} update to the overlay host.");
        }
    }
}

internal static class SelectionPatch
{
    private static int _notificationDiagnosticWritten;
    private static int _hallDiagnosticWritten;
    private static int _roomDiagnosticWritten;

    public static void NotificationPosted(object[] __args)
    {
        if (Interlocked.Exchange(ref _notificationDiagnosticWritten, 1) == 0)
        {
            var details = string.Join(", ", __args.Select((value, index) =>
                $"arg{index}={(value is null ? "<null>" : value.GetType().FullName + "=" + value)}"));
            SelectionPipe.Diagnostic($"Notification hook invoked: args={__args.Length}; {details}");
        }
        if (__args.Length >= 2 && string.Equals(__args[0]?.ToString(), "ChangeChart", StringComparison.Ordinal))
            SelectionPipe.PublishChart(__args[1]);
        else if (__args.Length > 0 && string.Equals(__args[0]?.ToString(), "ChangeMod", StringComparison.Ordinal))
            SelectionPipe.PublishCurrentChartDelayed();
    }

    public static void ChartItemChanged(object __instance)
    {
        if (Interlocked.Exchange(ref _hallDiagnosticWritten, 1) == 0)
        {
            var type = __instance?.GetType();
            var properties = type is null
                ? "<no instance>"
                : string.Join(", ", type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Select(property => $"{property.Name}:{property.PropertyType.FullName}"));
            SelectionPipe.Diagnostic($"Hall chart hook invoked: type={type?.FullName ?? "<null>"}; properties=[{properties}]");
        }

        var chart = __instance is null
            ? null
            : FindChartProperty(__instance.GetType())?.GetValue(__instance);
        SelectionPipe.PublishChart(chart);
    }

    public static void RoomChartResolved(object[] __args)
    {
        if (Interlocked.Exchange(ref _roomDiagnosticWritten, 1) == 0)
        {
            var details = string.Join(", ", __args.Select((value, index) =>
                $"arg{index}={(value is null ? "<null>" : value.GetType().FullName + "=" + value)}"));
            SelectionPipe.Diagnostic($"Room chart hook invoked: args={__args.Length}; {details}");
        }
        if (__args.Length > 0)
            SelectionPipe.PublishChart(__args[0]);
    }

    private static PropertyInfo? FindChartProperty(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(property => string.Equals(property.PropertyType.FullName, "Malody.Chart.iqy", StringComparison.Ordinal));

    public static void SelectionCleared() => SelectionPipe.Clear();

    public static void GameplayStarted() => SelectionPipe.PublishGameState(true);

    public static void ResultShown() => SelectionPipe.PublishGameState(false);

    public static void ModSettingsChanged() => SelectionPipe.PublishCurrentChartDelayed();

    public static void CurrentChartChanged(object[] __args)
    {
        if (__args.Length > 0)
            SelectionPipe.PublishCurrentChart(__args[0]);
    }

    public static void CurrentChartRead(object? __result) =>
        SelectionPipe.PublishCurrentChartIfChanged(__result);
}
