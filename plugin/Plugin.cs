using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using System.Collections;
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
    public const string PluginVersion = "1.3.15";
    internal const string UpdateManifestUrl =
        "https://raw.githubusercontent.com/MCPsDE/malodyv-rework-overlay/main/manifest.json";
    private const string RuntimeChartTypeName = "Malody.Chart.iqy";
    private static readonly string HostPath = Path.Combine(Paths.PluginPath, "MalodyVReworkOverlay", "MalodyV.ReworkOverlay.exe");
    private static Harmony? _harmony;
    private static readonly HashSet<MethodBase> EditorCommandHooks = new();

    public override void Load()
    {
        RefreshScheduler.Initialize();
        try
        {
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
        TryPatch(() => FindMethod("Malody.UI.UISlider", "OnChange"), nameof(SelectionPatch.TurboSliderChanged), "Turbo music speed changes");
        TryPatch(() => FindMethod("Malody.Scene.SceneEditor", "Start"), nameof(SelectionPatch.EditorStarted), "editor initialization");
        TryPatch(() => FindMethod("Malody.Scene.SceneEditor", "OnDestroy"), nameof(SelectionPatch.EditorClosed), "editor exit");
        TryPatch(FindRoomConfigChangeMethod, nameof(SelectionPatch.ModSettingsChanged), "online room config sync");
        TryPatch(FindRoomChartPublisher, nameof(SelectionPatch.RoomChartResolved), "online room chart resolution");
        TryPatch(() => FindMethod("Malody.Scene.SceneChart", "OnClose"), nameof(SelectionPatch.SelectionCleared), "chart scene exit");
        TryPatch(() => FindMethod("Malody.Scene.SceneRoom", "OnBack"), nameof(SelectionPatch.SelectionCleared), "online room exit");
        TryPatch(() => FindMethod("Malody.Scene.SceneRoomWatch", "OnBack"), nameof(SelectionPatch.SelectionCleared), "room watch exit");
        TryPatch(() => FindMethod("Malody.Play.ChartPlayer", "Play"), nameof(SelectionPatch.GameplayStarted), "gameplay start");
        TryPatch(() => FindMethod("Malody.Scene.ScenePlay", "OnDestroy"), nameof(SelectionPatch.GameplayEnded), "gameplay exit");
        TryPatch(() => FindMethod("Malody.Scene.SceneResult", "OnEnable"), nameof(SelectionPatch.ResultShown), "result screen");
        TryPatch(() => FindMethod("Malody.Scene.SceneLoader", "ToScene"), nameof(SelectionPatch.SceneChanged), "scene state changes");
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

    internal static void InstallEditorCommandHook(object commandManager)
    {
        try
        {
            var managerType = FindLoadedType("Malody.Edit.Command.CommandManager");
            var commandType = FindLoadedType("Malody.Edit.Command.CommandBase");
            var method = managerType.GetMethod(
                "JustDoIt",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null,
                new[] { commandType, typeof(bool) },
                modifiers: null)
                ?? throw new MissingMethodException(
                    managerType.FullName,
                    "JustDoIt(CommandBase, Boolean)");
            if (!EditorCommandHooks.Add(method))
                return;
            _harmony!.Patch(method, postfix: new HarmonyMethod(typeof(SelectionPatch), nameof(SelectionPatch.EditorNoteChanged)));
            SelectionPipe.Diagnostic(
                $"Patched editor note changes: {method.DeclaringType?.FullName}.{method.Name}; " +
                $"manager instance={commandManager.GetType().FullName}.");
        }
        catch (Exception exception)
        {
            SelectionPipe.DiagnosticWarning($"Failed to patch editor note changes: {exception.Message}");
        }
    }

    private static Type FindLoadedType(string fullName) =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(fullName, throwOnError: false))
            .FirstOrDefault(type => type is not null)
        ?? throw new TypeLoadException($"Could not find {fullName} in loaded game assemblies.");

    private static MethodInfo FindRoomConfigChangeMethod()
    {
        var sceneRoomType = FindType("Assembly-CSharp", "Malody.Scene.SceneRoom");
        var method = sceneRoomType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .FirstOrDefault(candidate =>
            {
                var parameters = candidate.GetParameters();
                return parameters.Length == 1 &&
                    parameters[0].ParameterType.FullName?.Contains("RoomChangeConfig", StringComparison.Ordinal) == true;
            });
        return method ?? throw new MissingMethodException("Could not find the obfuscated SceneRoom config-change handler.");
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
                existing.Dispose();
                return;
            }

            Log.LogWarning($"Card host process {existing.Id} has no main window; stopping the stale instance.");
            try
            {
                existing.Kill(entireProcessTree: true);
                _ = RestartCompanionAfterExitAsync(existing);
                return;
            }
            catch (Exception exception)
            {
                existing.Dispose();
                throw new InvalidOperationException(
                    $"The existing card host process {existing.Id} could not be replaced.", exception);
            }
        }

        LaunchCompanion();
    }

    private async Task RestartCompanionAfterExitAsync(Process existing)
    {
        try
        {
            await existing.WaitForExitAsync().ConfigureAwait(false);
            existing.Dispose();
            LaunchCompanion();
        }
        catch (Exception exception)
        {
            Log.LogError($"Failed to restart the stale card host: {exception}");
        }
    }

    private void LaunchCompanion()
    {
        var started = Process.Start(new ProcessStartInfo(HostPath)
        {
            WorkingDirectory = Path.GetDirectoryName(HostPath)!,
            UseShellExecute = true,
        });
        if (started is null)
            throw new InvalidOperationException("The card host process could not be started.");

        Log.LogInfo($"Started card host process {started.Id}.");
        started.Dispose();
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
    int RandomSeed = 0,
    string? TurboSignature = null,
    string? EditorChartJson = null);

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
    private static int _clearPublished;
    private static bool? _lastGameState;
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
    private static PropertyInfo? _modProperty;
    private static PropertyInfo? _modForPlayProperty;
    private static PropertyInfo? _playModeProperty;
    private static FieldInfo? _playModeField;
    private static PropertyInfo? _customJudgeProperty;
    private static FieldInfo? _customJudgeField;
    private static PropertyInfo? _playSpeedProperty;
    private static PropertyInfo? _randomSeedBackingProperty;
    private static PropertyInfo? _customJudgeSpeedProperty;
    private static FieldInfo? _customJudgeSpeedField;
    private static PropertyInfo? _panelJudgeSceneInstanceProperty;
    private static FieldInfo? _panelJudgeTurboField;
    private static FieldInfo? _panelTurboSpeedField;
    private static FieldInfo? _panelJudgeTurboToggleField;
    private static PropertyInfo? _sliderCurrentProperty;
    private static PropertyInfo? _toggleCurrentProperty;
    private static bool _turboPanelMembersResolved;
    private static MethodInfo? _canPlayModMethod;
    private static bool _canPlayModResolved;
    private static int _turboMembersDiagnosticWritten;

    public static void PublishCurrentChart()
    {
        try
        {
            ResolvePlayInfoProperties();
            var instance = _localPlayInfoProperty?.GetValue(null);
            var chart = instance is null ? null : _chartProperty!.GetValue(instance);
            if (chart is null)
            {
                Clear();
                return;
            }
            PublishChart(chart);
        }
        catch (Exception exception)
        {
            DiagnosticWarning($"Initial current chart read failed: {exception}");
            Clear();
        }
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
                var typeProperties = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                var selfProperties = typeProperties.Where(property => property.GetMethod?.IsStatic == true && property.PropertyType == type).ToArray();
                if (selfProperties.Length < 2)
                    continue;
                var chartProperty = typeProperties.FirstOrDefault(property =>
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
        _localPlayInfoProperty = match.Instances.FirstOrDefault(property => property.SetMethod is null)
            ?? match.Instances.FirstOrDefault(property =>
                string.Equals(property.Name, "Local", StringComparison.OrdinalIgnoreCase))
            ?? match.Instances[0];
        _chartProperty = match.Chart;
        _runtimeChartType = match.Chart.PropertyType;
        _runtimeChartConstructor = _runtimeChartType.GetConstructor(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, new[] { typeof(IntPtr) }, modifiers: null);

        var properties = match.Type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static);
        var modMaskProperties = properties.Where(property =>
            string.Equals(property.PropertyType.FullName, "Malody.Play.ModMask", StringComparison.Ordinal)).ToArray();
        _modProperty = modMaskProperties.FirstOrDefault(property =>
            property.SetMethod is not null &&
            !properties.Any(candidate => candidate.Name.Equals($"_{property.Name}_k__BackingField",
                StringComparison.Ordinal))) ?? modMaskProperties.FirstOrDefault();
        _modForPlayProperty = modMaskProperties.FirstOrDefault(property =>
            property != _modProperty && properties.Any(candidate => candidate.Name.Equals(
                $"_{property.Name}_k__BackingField", StringComparison.Ordinal)));
        _playModeProperty = properties.FirstOrDefault(property =>
            string.Equals(property.PropertyType.FullName, "Malody.Play.PlayModeType", StringComparison.Ordinal));
        _playModeField = match.Type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(field => string.Equals(field.FieldType.FullName, "Malody.Play.PlayModeType", StringComparison.Ordinal))
            ?? match.Type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(field => string.Equals(field.Name, "rsk", StringComparison.Ordinal));
        _playSpeedProperty = properties.FirstOrDefault(property =>
            string.Equals(property.Name, "PlaySpeed", StringComparison.OrdinalIgnoreCase)
            && property.PropertyType == typeof(float));
        var fields = match.Type.GetFields(BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static);
        var customJudgeMembers = properties.Cast<MemberInfo>().Concat(fields).ToArray();
        var customJudgeMember = customJudgeMembers.FirstOrDefault(member =>
                IsCustomJudgeMemberType(GetMemberValueType(member)))
            ?? customJudgeMembers.FirstOrDefault(member =>
                string.Equals(member.Name, "customJudge", StringComparison.OrdinalIgnoreCase))
            ?? customJudgeMembers.FirstOrDefault(member =>
                GetMemberValueType(member) != _runtimeChartType &&
                HasFloatAudioSpeedMember(GetMemberValueType(member)));
        _customJudgeProperty = customJudgeMember as PropertyInfo;
        _customJudgeField = customJudgeMember as FieldInfo;
        _randomSeedBackingProperty = properties.FirstOrDefault(property =>
            property.PropertyType == typeof(int) && property.Name.Contains("BackingField", StringComparison.Ordinal));
        var customJudgeType = customJudgeMember is null
            ? null
            : GetMemberValueType(customJudgeMember);
        if (customJudgeType is not null)
        {
            _customJudgeSpeedProperty = FindProperty(customJudgeType, "audioSpeed") ??
                FindProperty(customJudgeType, "szr");
            _customJudgeSpeedField = customJudgeType
                .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(field => IsAudioSpeedName(field.Name) && IsNumericSpeedType(field.FieldType));
        }
        ResolveTurboPanelMembers();

        Diagnostic($"Resolved PlayInfo state members: Mod={_modProperty?.Name ?? "<missing>"}, " +
            $"ModForPlay={_modForPlayProperty?.Name ?? "<missing>"}, " +
            $"PlayMode={_playModeProperty?.Name ?? _playModeField?.Name ?? "<missing>"}, " +
                $"PlaySpeed={_playSpeedProperty?.Name ?? "<missing>"}, " +
                $"Turbo={customJudgeMember?.Name ?? "<missing>"}, " +
                $"TurboMusicSpeed={_customJudgeSpeedProperty?.Name ?? _customJudgeSpeedField?.Name ?? "<missing>"}, " +
                $"Seed={_randomSeedBackingProperty?.Name ?? "<missing>"}.");
    }

    private static Type GetMemberValueType(MemberInfo member) => member switch
    {
        PropertyInfo property => property.PropertyType,
        FieldInfo field => field.FieldType,
        _ => typeof(void),
    };

    private static void ResolveTurboPanelMembers()
    {
        if (_turboPanelMembersResolved)
            return;

        _turboPanelMembersResolved = true;
        try
        {
            var panelJudgeType = FindRuntimeType("Malody.Scene.Panel.PanelJudge");
            var panelTurboType = FindRuntimeType("Malody.Scene.Panel.PanelTurbo");
            var sliderType = FindRuntimeType("Malody.UI.UISlider");
            var toggleType = FindRuntimeType("Malody.UI.UIToggle");
            _panelJudgeSceneInstanceProperty = panelJudgeType.GetProperty("SceneInstance",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            _panelJudgeTurboField = FindField(panelJudgeType, "panelTurbo");
            _panelJudgeTurboToggleField = FindField(panelJudgeType, "turboToggle");
            _panelTurboSpeedField = FindField(panelTurboType, "slideSpeed");
            _sliderCurrentProperty = FindProperty(sliderType, "Current");
            _toggleCurrentProperty = FindProperty(toggleType, "Current");
        }
        catch (Exception exception)
        {
            DiagnosticWarning($"Turbo live slider inspection is unavailable: {exception.Message}");
        }
    }

    private static Type FindRuntimeType(string fullName) =>
        AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(GetLoadableTypes)
            .FirstOrDefault(type => string.Equals(type.FullName, fullName, StringComparison.Ordinal))
        ?? throw new TypeLoadException($"Could not find runtime type {fullName}.");

    private static FieldInfo? FindField(Type type, string name) =>
        type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(field => string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase));

    private static bool TryReadTurboPanel(out object? speedSlider, out bool enabled, out double speed)
    {
        ResolveTurboPanelMembers();
        speedSlider = null;
        enabled = false;
        speed = double.NaN;
        try
        {
            var panel = _panelJudgeSceneInstanceProperty?.GetValue(null);
            if (panel is null || IsNullNativeObject(panel))
                return false;
            var turboPanel = _panelJudgeTurboField?.GetValue(panel);
            speedSlider = turboPanel is null ? null : _panelTurboSpeedField?.GetValue(turboPanel);
            enabled = _panelJudgeTurboToggleField?.GetValue(panel) is { } toggle &&
                _toggleCurrentProperty?.GetValue(toggle) is true;
            if (enabled && speedSlider is not null && !IsNullNativeObject(speedSlider))
                speed = Convert.ToDouble(_sliderCurrentProperty?.GetValue(speedSlider),
                    System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            speedSlider = null;
            enabled = false;
            speed = double.NaN;
            return false;
        }
    }

    private static bool IsNullNativeObject(object value)
    {
        try
        {
            return FindProperty(value.GetType(), "Pointer")?.GetValue(value) is IntPtr pointer && pointer == IntPtr.Zero;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsTurboMusicSpeedSlider(object? slider)
    {
        if (slider is null || !TryReadTurboPanel(out var speedSlider, out _, out _ ) || speedSlider is null)
            return false;
        if (ReferenceEquals(slider, speedSlider))
            return true;
        try
        {
            return FindProperty(slider.GetType(), "Pointer")?.GetValue(slider) is IntPtr sliderPointer &&
                sliderPointer != IntPtr.Zero &&
                FindProperty(speedSlider.GetType(), "Pointer")?.GetValue(speedSlider) is IntPtr turboPointer &&
                sliderPointer == turboPointer;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCustomJudgeMemberType(Type type) =>
        type.FullName?.EndsWith(".CustomJudgeProfile", StringComparison.Ordinal) == true ||
        string.Equals(type.Name, "CustomJudgeProfile", StringComparison.Ordinal) ||
        string.Equals(type.FullName, "Malody.Play.blp", StringComparison.Ordinal);

    private static bool IsAudioSpeedName(string name) =>
        string.Equals(name, "audioSpeed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "szr", StringComparison.OrdinalIgnoreCase);

    private static bool IsNumericSpeedType(Type type) => type == typeof(float) || type == typeof(double);

    private static bool HasFloatAudioSpeedMember(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        return type.GetProperties(flags).Any(property =>
                   IsNumericSpeedType(property.PropertyType) && IsAudioSpeedName(property.Name)) ||
               type.GetFields(flags).Any(field =>
                   IsNumericSpeedType(field.FieldType) && IsAudioSpeedName(field.Name));
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
            var (mods, speedRate, playMode, randomSeed, turboSignature, valid) = ReadActiveModState();
            if (!valid)
            {
                DiagnosticWarning("Active Mod/Turbo state is unavailable or invalid; clearing stale card analysis.");
                Clear();
                return;
            }
            var identity = $"{Path.GetFullPath(path)}|{index}|{version}|{mods}|{speedRate.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture)}|{playMode}|{randomSeed}|{turboSignature}";
            Interlocked.Exchange(ref _clearPublished, 0);
            if (string.Equals(Interlocked.Exchange(ref _lastIdentity, identity), identity, StringComparison.OrdinalIgnoreCase))
                return;
            _log?.LogInfo($"Selected chart: {version} ({path}, difficulty {index + 1}); mods={mods}; speed={speedRate:0.##}x.");
            if (!Updates.Writer.TryWrite(new SelectionMessage("selection", path, index, identity, version,
                    Mods: mods, SpeedRate: speedRate, PlayMode: playMode, RandomSeed: randomSeed,
                    TurboSignature: turboSignature)))
                _log?.LogWarning("The selected chart update could not be queued for the overlay host.");
        }
        catch (Exception exception)
        {
            _log?.LogWarning($"Unable to read selected chart: {exception.Message}");
            Clear();
        }
    }

    private static (uint Mods, double SpeedRate, int PlayMode, int RandomSeed, string TurboSignature, bool Valid) ReadActiveModState()
    {
        try
        {
            ResolvePlayInfoProperties();
            var local = TryGetPropertyValue(_localPlayInfoProperty, null);
            if (local is null)
                return (0, 1, -1, 0, "unavailable-local", true);

            var localType = local.GetType();
            var rawMods = TryGetPropertyValue(_modProperty, local) ?? ReadMember(localType, local, "Mod");
            if (rawMods is null)
                return (0, 1, -1, 0, "unavailable-mods", true);
            var mods = Convert.ToUInt32(rawMods);
            var mode = TryGetPropertyValue(_playModeProperty, local) ?? TryGetFieldValue(_playModeField, local) ??
                ReadMember(localType, local, "playMode");
            mods = FilterPlayableMods(mods, rawMods?.GetType(), mode);
            var customJudge = TryGetPropertyValue(_customJudgeProperty, local)
                ?? TryGetFieldValue(_customJudgeField, local)
                ?? ReadMember(localType, local, "customJudge");
            var turboPanelOpen = TryReadTurboPanel(out _, out var turboEnabled, out var liveTurboSpeed);
            if (turboPanelOpen && !turboEnabled)
                customJudge = null;
            double speed;
            if (turboPanelOpen && turboEnabled && double.IsFinite(liveTurboSpeed))
            {
                speed = liveTurboSpeed;
            }
            else if (customJudge is not null)
            {
                speed = ReadCustomJudgeAudioSpeed(customJudge);
                if (!double.IsFinite(speed) || speed < 0.5 || speed > 2.0)
                {
                    DiagnosticWarning($"Turbo audioSpeed {speed:0.###} is invalid; using neutral 1.0x instead of regular speed mods.");
                    speed = 1d;
                }
            }
            else if ((mods & 0x20u) != 0) speed = 1.5;
            else if ((mods & 0x10u) != 0) speed = 1.2;
            else if ((mods & 0x100u) != 0) speed = 0.8;
            else speed = ReadDouble(TryGetPropertyValue(_playSpeedProperty, local), 1d);

            var randomSeed = TryGetPropertyValue(_randomSeedBackingProperty, local) ?? ReadMember(localType, local, "RandomSeed");
            return (mods, double.IsFinite(speed) && speed > 0 ? speed : 1d,
                mode is null ? -1 : Convert.ToInt32(mode), randomSeed is null ? 0 : Convert.ToInt32(randomSeed),
                turboPanelOpen && turboEnabled
                    ? $"live-audioSpeed={liveTurboSpeed.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture)}"
                    : BuildTurboSignature(customJudge), true);
        }
        catch (Exception exception)
        {
            _log?.LogWarning($"Unable to read active MalodyV mods: {exception.Message}");
            return (0, 1, -1, 0, "unavailable", true);
        }
    }

    private static double ReadCustomJudgeAudioSpeed(object? customJudge)
    {
        if (customJudge is null)
            return double.NaN;

        var namedValue = TryGetPropertyValue(_customJudgeSpeedProperty, customJudge)
            ?? TryGetFieldValue(_customJudgeSpeedField, customJudge)
            ?? ReadMember(customJudge.GetType(), customJudge, "audioSpeed")
            ?? ReadMember(customJudge.GetType(), customJudge, "szr");
        var speed = ReadDouble(namedValue, double.NaN);
        if (double.IsFinite(speed))
            return speed;

        if (Interlocked.Exchange(ref _turboMembersDiagnosticWritten, 1) == 0)
        {
            var type = customJudge.GetType();
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Select(field => $"field {field.Name}:{field.FieldType.FullName}={FormatSignatureValue(TryGetFieldValue(field, customJudge))}");
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(property => property.GetMethod?.IsStatic != true)
                .Select(property => $"property {property.Name}:{property.PropertyType.FullName}={FormatSignatureValue(TryGetPropertyValue(property, customJudge))}");
            DiagnosticWarning($"Turbo music speed (audioSpeed/szr) was not readable on {type.FullName}; members=[{string.Join(", ", fields.Concat(properties))}].");
        }

        DiagnosticWarning("Turbo music speed was not readable; using neutral 1.0x analysis speed.");
        return double.NaN;
    }

    private static string BuildTurboSignature(object? customJudge)
    {
        if (customJudge is null)
            return "none";

        var type = customJudge.GetType();
        var fieldValues = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(field => IsScalar(field.FieldType))
            .Select(field => $"{field.Name}={FormatSignatureValue(TryGetFieldValue(field, customJudge))}");
        var propertyValues = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(property => property.GetMethod?.IsStatic != true && IsScalar(property.PropertyType))
            .Select(property => $"{property.Name}={FormatSignatureValue(TryGetPropertyValue(property, customJudge))}");
        return string.Join(";", fieldValues.Concat(propertyValues));
    }

    private static bool IsScalar(Type type) =>
        type.IsPrimitive || type.IsEnum || type == typeof(decimal) || type == typeof(string);

    private static string FormatSignatureValue(object? value) => value switch
    {
        null => "<null>",
        IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "<null>",
    };

    public static void QueueCurrentChartRefresh()
    {
        RefreshScheduler.ScheduleChartRefresh();
    }

    public static void PublishEditorChart(string path, uint index, string version, string chartJson, int revision)
    {
        var fullPath = Path.GetFullPath(path);
        var identity = $"editor|{fullPath}|{index}|{version}|{revision}";
        Interlocked.Exchange(ref _lastIdentity, identity);
        Interlocked.Exchange(ref _clearPublished, 0);
        var update = new SelectionMessage("selection", fullPath, index, identity, version,
            Mods: 0, SpeedRate: 1, PlayMode: 0, RandomSeed: 0, TurboSignature: "editor",
            EditorChartJson: chartJson);
        if (!Updates.Writer.TryWrite(update))
            DiagnosticWarning("The editor snapshot could not be queued for the overlay host.");
    }

    private static uint FilterPlayableMods(uint mods, Type? modType, object? playMode)
    {
        if (mods == 0 || modType?.IsEnum != true || playMode is null || !playMode.GetType().IsEnum)
            return mods;

        try
        {
            if (!_canPlayModResolved)
            {
                var playUtil = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(assembly => assembly.GetType("Malody.Play.PlayUtil", throwOnError: false))
                    .FirstOrDefault(type => type is not null);
                _canPlayModMethod = playUtil?.GetMethod("CanPlayMod",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    binder: null, new[] { modType, playMode.GetType() }, modifiers: null);
                _canPlayModResolved = true;
            }
            var canPlayMod = _canPlayModMethod;
            if (canPlayMod is null || canPlayMod.ReturnType != typeof(bool))
                return mods;

            uint playableMods = 0;
            for (var bit = 0; bit < 16; bit++)
            {
                var flag = 1u << bit;
                if ((mods & flag) == 0)
                    continue;
                var result = canPlayMod.Invoke(null, new[] { Enum.ToObject(modType, flag), playMode });
                if (result is true)
                    playableMods |= flag;
            }
            return playableMods;
        }
        catch (Exception exception)
        {
            DiagnosticWarning($"Unable to filter mods for the selected mode: {exception.Message}");
            return mods;
        }
    }

    private static object? ReadMember(Type type, object instance, string name)
    {
        try
        {
            var property = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            if (property is not null)
                return property.GetValue(instance);
            var field = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            return field?.GetValue(instance);
        }
        catch
        {
            return null;
        }
    }

    private static object? TryGetPropertyValue(PropertyInfo? property, object? instance)
    {
        try
        {
            return property?.GetValue(instance);
        }
        catch
        {
            return null;
        }
    }

    private static object? TryGetFieldValue(FieldInfo? field, object? instance)
    {
        try
        {
            return field?.GetValue(instance);
        }
        catch
        {
            return null;
        }
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
        var hadSelection = Interlocked.Exchange(ref _lastIdentity, null) is not null;
        if (hadSelection || Interlocked.Exchange(ref _clearPublished, 1) == 0)
            Updates.Writer.TryWrite(new SelectionMessage("selection", null, 0, null, null));
    }

    public static void PublishGameState(bool isPlaying)
    {
        if (_lastGameState == isPlaying)
            return;
        _lastGameState = isPlaying;
        Updates.Writer.TryWrite(new SelectionMessage(isPlaying ? "gameplay" : "result", null, 0, null, null));
    }

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
    private static object? _editorChartManager;
    private static string? _editorPath;
    private static string? _editorVersion;
    private static uint _editorIndex;
    private static int _editorRevision;

    public static void EditorStarted(object __instance)
    {
        var manager = InvokeNoArgs(__instance, "GetChartManager");
        if (manager is null)
        {
            SelectionPipe.DiagnosticWarning("SceneEditor started before its chart manager was available.");
            return;
        }

        _editorChartManager = manager;
        var commandManager = InvokeNoArgs(__instance, "GetCommandManager")
            ?? ReadMember(__instance, "commandManager");
        if (commandManager is null)
            SelectionPipe.DiagnosticWarning("SceneEditor did not expose its CommandManager; note-change updates are unavailable.");
        else
            Plugin.InstallEditorCommandHook(commandManager);
        var chart = InvokeNoArgs(manager, "GetChart");
        if (chart is null)
        {
            SelectionPipe.DiagnosticWarning("Editor chart manager did not expose its working chart.");
            return;
        }

        _editorPath = ReadMember(chart, "FilePath")?.ToString();
        _editorVersion = ReadMember(chart, "DisplayVersion")?.ToString() ?? "Editor";
        _editorIndex = ReadUInt(ReadMember(chart, "IndexInFile"), 0);
        Interlocked.Exchange(ref _editorRevision, 0);
        RefreshScheduler.ScheduleEditorRefresh(0.5f);
    }

    public static void EditorNoteChanged(object[] __args)
    {
        if (_editorChartManager is null || __args.Length == 0 || !IsNoteCommand(__args[0]))
            return;

        RefreshScheduler.ScheduleEditorRefresh();
    }

    public static void EditorClosed()
    {
        _editorChartManager = null;
        _editorPath = null;
        _editorVersion = null;
        _editorRevision = 0;
        SelectionPipe.QueueCurrentChartRefresh();
    }

    public static void PublishEditorChart()
    {
        var manager = _editorChartManager;
        if (manager is null)
            return;

        try
        {
            var snapshot = BuildEditorChartJson(manager);
            var path = string.IsNullOrWhiteSpace(_editorPath)
                ? "editor-preview.mc"
                : _editorPath;
            var revision = Interlocked.Increment(ref _editorRevision);
            SelectionPipe.PublishEditorChart(path, _editorIndex, _editorVersion ?? "Editor", snapshot, revision);
        }
        catch (Exception exception)
        {
            SelectionPipe.DiagnosticWarning($"Unable to snapshot the editor chart: {exception.Message}");
        }
    }

    private static string BuildEditorChartJson(object manager)
    {
        var chart = InvokeNoArgs(manager, "GetChart")
            ?? throw new InvalidDataException("The editor working chart is unavailable.");
        var meta = ReadMember(chart, "meta") ?? throw new InvalidDataException("Editor chart metadata is unavailable.");
        var modeName = ReadMember(manager, "Mode")?.ToString() ?? "Key";
        var mcMode = modeName switch
        {
            "Catch" => 3,
            "Pad" => 4,
            "Taiko" => 5,
            "Ring" => 6,
            "Slide" => 7,
            "Live" => 8,
            "Cube" => 9,
            _ => 0,
        };
        var notes = new List<Dictionary<string, object?>>();
        var maxColumn = Math.Max(1, ReadInt(ReadMember(meta, "Column"), 1));
        var playableColumnCount = maxColumn;
        if (modeName is "Catch" or "Slide")
            maxColumn = 18;
        foreach (var note in EnumerateObjects(ReadMember(manager, "Notes")))
        {
            if (note is null)
                continue;
            var beat = ReadBeat(ReadMember(ReadMember(note, "start"), "Beat"));
            var columnValue = ReadMember(note, "X") ?? ReadMember(note, "column") ?? ReadMember(note, "index");
            if (beat is null || columnValue is null)
                continue;

            var column = ReadInt(columnValue, -1);
            if (modeName == "Key" && (column < 0 || column >= playableColumnCount))
                continue;
            if (modeName == "Catch")
                column = (int)Math.Round(Math.Clamp(column, 0, 512) * 17d / 512d);
            else if (modeName == "Slide")
                column = (int)Math.Round(Math.Clamp(column, 0, 255) * 17d / 255d);
            if (column < 0)
                continue;
            if (modeName is not ("Catch" or "Slide"))
                maxColumn = Math.Max(maxColumn, column + 1);
            var item = new Dictionary<string, object?>
            {
                ["beat"] = beat,
                ["column"] = column,
                ["type"] = ReadInt(ReadMember(note, "noteType"), 0),
            };
            var endBeat = ReadBeat(ReadMember(ReadMember(note, "end"), "Beat"));
            if (endBeat is not null)
                item["endbeat"] = endBeat;
            notes.Add(item);
        }

        if (maxColumn is < 1 or > 18)
            throw new InvalidDataException($"Editor key count {maxColumn} is outside the supported 1-18 range.");

        var times = new List<Dictionary<string, object?>>();
        foreach (var point in EnumerateObjects(InvokeNoArgs(manager, "GetTimes")))
        {
            var beat = ReadBeat(ReadMember(ReadMember(point, "start"), "Beat"));
            var bpm = ReadDouble(ReadMember(point, "BPM"), double.NaN);
            if (beat is null || !double.IsFinite(bpm) || bpm <= 0)
                continue;
            var signature = ReadDouble(ReadMember(point, "signature"), 4);
            times.Add(new Dictionary<string, object?> { ["beat"] = beat, ["bpm"] = bpm, ["signature"] = signature });
        }
        if (times.Count == 0)
            times.Add(new Dictionary<string, object?> { ["beat"] = new[] { 0, 0, 1 }, ["bpm"] = 120, ["signature"] = 4 });

        var songMeta = ReadMember(ReadMember(chart, "Song"), "meta");
        var snapshot = new Dictionary<string, object?>
        {
            ["meta"] = new Dictionary<string, object?>
            {
                ["mode"] = mcMode,
                ["creator"] = ReadMember(meta, "Creator")?.ToString() ?? "Unknown",
                ["version"] = ReadMember(chart, "DisplayVersion")?.ToString() ?? "Editor",
                ["background"] = ReadMember(meta, "Bgimage")?.ToString() ?? "",
                ["mode_ext"] = new Dictionary<string, object?> { ["column"] = maxColumn },
                ["song"] = new Dictionary<string, object?>
                {
                    ["title"] = ReadMember(songMeta, "Title")?.ToString() ?? ReadMember(meta, "Title")?.ToString() ?? "Editor Preview",
                    ["artist"] = ReadMember(songMeta, "Artist")?.ToString() ?? ReadMember(meta, "Artist")?.ToString() ?? "Unknown Artist",
                    ["file"] = ReadMember(meta, "MainAudio")?.ToString() ?? "audio.mp3",
                },
            },
            ["time"] = times,
            ["note"] = notes,
            ["effect"] = Array.Empty<object>(),
        };
        return JsonSerializer.Serialize(snapshot);
    }

    private static bool IsNoteCommand(object? command)
    {
        var commandType = ReadMember(command, "Type")?.ToString();
        if (commandType is "AddNote" or "DeleteNote" or "ChangeNoteType" or "MoveNoteStartTime" or
            "MoveNoteEndTime" or "MoveNoteX" or "ChangeNoteWidth" or "MoveSlideBody" or
            "AddSlideRear" or "DeleteSlideRear" or "DeleteSlideSeg" or "ChangeArrow" or
            "ChangeGroup" or "PadEndConfirm")
            return true;

        for (var type = command?.GetType(); type is not null; type = type.BaseType)
        {
            if (string.Equals(type.FullName, "Malody.Edit.Command.CommandBaseNote", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static object? InvokeNoArgs(object? instance, string name)
    {
        if (instance is null)
            return null;
        var method = instance.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, Type.EmptyTypes, modifiers: null);
        return method?.Invoke(instance, null);
    }

    private static object? ReadMember(object? instance, string name)
    {
        if (instance is null)
            return null;
        var type = instance.GetType();
        try
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property is not null)
                return property.GetValue(instance);
            return type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(instance);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<object?> EnumerateObjects(object? collection)
    {
        if (collection is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
                yield return item;
            yield break;
        }

        var iterator = InvokeNoArgs(collection, "GetEnumerator");
        var moveNext = iterator?.GetType().GetMethod("MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var current = iterator?.GetType().GetProperty("Current", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        while (iterator is not null && moveNext?.Invoke(iterator, null) is true)
            yield return current?.GetValue(iterator);
    }

    private static int[]? ReadBeat(object? beat)
    {
        if (beat is null)
            return null;
        var denominator = ReadInt(ReadMember(beat, "Denom"), 0);
        if (denominator <= 0)
            return null;
        return new[] { ReadInt(ReadMember(beat, "Int"), 0), ReadInt(ReadMember(beat, "Numer"), 0), denominator };
    }

    private static int ReadInt(object? value, int fallback)
    {
        try { return value is null ? fallback : Convert.ToInt32(value); }
        catch { return fallback; }
    }

    private static uint ReadUInt(object? value, uint fallback)
    {
        try { return value is null ? fallback : Convert.ToUInt32(value); }
        catch { return fallback; }
    }

    private static double ReadDouble(object? value, double fallback)
    {
        try { return value is null ? fallback : Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture); }
        catch { return fallback; }
    }

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
            SelectionPipe.QueueCurrentChartRefresh();
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

    public static void GameplayEnded() => SelectionPipe.PublishGameState(false);

    public static void ResultShown() => SelectionPipe.PublishGameState(false);

    public static void SceneChanged(object[] __args)
    {
        if (__args.Length == 0 || __args[0] is null)
            return;

        var scene = __args[0].ToString();
        if (string.Equals(scene, "Play", StringComparison.OrdinalIgnoreCase))
            SelectionPipe.PublishGameState(true);
        else if (string.Equals(scene, "Result", StringComparison.OrdinalIgnoreCase))
            SelectionPipe.PublishGameState(false);
    }

    public static void ModSettingsChanged() => SelectionPipe.QueueCurrentChartRefresh();

    public static void TurboSliderChanged(object __instance)
    {
        if (SelectionPipe.IsTurboMusicSpeedSlider(__instance))
            SelectionPipe.QueueCurrentChartRefresh();
    }

}

internal static class RefreshScheduler
{
    private static readonly object Gate = new();
    private static readonly Timer ChartTimer = new(DispatchChartRefresh, null, Timeout.Infinite, Timeout.Infinite);
    private static readonly Timer EditorTimer = new(DispatchEditorRefresh, null, Timeout.Infinite, Timeout.Infinite);
    private static SynchronizationContext? _unityContext;
    private static int _chartGeneration;
    private static int _editorGeneration;

    public static void Initialize()
    {
        lock (Gate)
            _unityContext = SynchronizationContext.Current;
    }

    public static void ScheduleChartRefresh()
    {
        Schedule(ChartTimer, editor: false, 0.15f);
    }

    public static void ScheduleEditorRefresh(float delay = 0.25f)
    {
        Schedule(EditorTimer, editor: true, delay);
    }

    private static void Schedule(Timer timer, bool editor, float delay)
    {
        SynchronizationContext? context;
        lock (Gate)
        {
            _unityContext ??= SynchronizationContext.Current;
            context = _unityContext;
            if (editor)
                _editorGeneration++;
            else
                _chartGeneration++;
        }

        if (context is null)
        {
            SelectionPipe.DiagnosticWarning(
                "Unity synchronization context is unavailable; refreshing on the Harmony callback thread.");
            if (editor)
                SelectionPatch.PublishEditorChart();
            else
                SelectionPipe.PublishCurrentChart();
            return;
        }

        timer.Change(TimeSpan.FromSeconds(Math.Clamp(delay, 0.05f, 2f)), Timeout.InfiniteTimeSpan);
    }

    private static void DispatchChartRefresh(object? state) => Dispatch(editor: false);

    private static void DispatchEditorRefresh(object? state) => Dispatch(editor: true);

    private static void Dispatch(bool editor)
    {
        SynchronizationContext? context;
        int generation;
        lock (Gate)
        {
            context = _unityContext;
            generation = editor ? _editorGeneration : _chartGeneration;
        }

        if (context is null)
            return;

        try
        {
            context.Post(_ =>
            {
                lock (Gate)
                {
                    if (generation != (editor ? _editorGeneration : _chartGeneration))
                        return;
                }

                if (editor)
                    SelectionPatch.PublishEditorChart();
                else
                    SelectionPipe.PublishCurrentChart();
            }, null);
        }
        catch (Exception exception)
        {
            SelectionPipe.DiagnosticWarning($"Unable to return a refresh event to Unity's main thread: {exception.Message}");
        }
    }
}
