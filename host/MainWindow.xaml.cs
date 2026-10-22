using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace MalodyV.ReworkOverlay;

public partial class MainWindow : Window
{
    private const int ToggleHotkeyId = 0x4D52;
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private static readonly IReadOnlyDictionary<string, (string Chinese, string English)> Texts =
        new Dictionary<string, (string, string)>
        {
            ["title"] = ("MalodyV 难度卡片", "MalodyV Rework Overlay"),
            ["subtitle"] = ("谱面卡片控制台", "Chart companion controls"),
            ["waiting"] = ("等待游戏连接", "Waiting for game"),
            ["connected"] = ("已连接游戏", "Connected to game"),
            ["reconnecting"] = ("正在重新连接", "Reconnecting"),
            ["selected"] = ("当前谱面", "Selected chart"),
            ["noChart"] = ("尚未选择谱面", "No chart selected"),
            ["loading"] = ("正在加载谱面…", "Loading chart..."),
            ["renderFailed"] = ("谱面卡片生成失败", "Could not render chart"),
            ["cardOptions"] = ("卡片显示", "Card display"),
            ["showCard"] = ("显示卡片", "Show card"),
            ["alwaysOnTop"] = ("始终置顶", "Keep on top"),
            ["dragCard"] = ("按住卡片拖动", "Drag card to move"),
            ["hideDuringPlay"] = ("游玩时隐藏，结算后恢复", "Hide during play, restore at results"),
            ["opacity"] = ("卡片透明度", "Card opacity"),
            ["save"] = ("保存设置", "Save settings"),
            ["ready"] = ("卡片资源已就绪", "Card resources are ready"),
            ["cardReady"] = ("卡片已更新", "Card updated"),
            ["botSourceCurrent"] = ("Bot 卡片目录与远程版本一致", "Bot card directory matches the remote version"),
            ["botSourceUpdated"] = ("Bot 卡片目录已从远程更新", "Bot card directory updated from remote"),
            ["settingsSaved"] = ("设置已保存", "Settings saved"),
            ["pressHotkey"] = ("请按下新快捷键", "Press a new shortcut"),
            ["hotkeyTooltip"] = ("点击后按下新的快捷键", "Click, then press a new shortcut"),
            ["hotkeyConflict"] = ("快捷键无法注册，可能已被占用", "Shortcut could not be registered; it may be in use"),
            ["hotkeyChanged"] = ("卡片快捷键已更新", "Card shortcut updated"),
            ["difficulty"] = ("难度", "Difficulty"),
            ["updateWaiting"] = ("已检测到更新，退出游戏后自动安装", "Update detected; installs automatically after the game exits"),
            ["updateDownloading"] = ("正在自动下载并校验更新…", "Downloading and verifying the update..."),
            ["updateInstalling"] = ("正在自动更新，控制台将重新启动…", "Installing update; the controls will restart..."),
            ["updateFailed"] = ("自动更新暂未完成，下次启动会重试", "Automatic update could not finish; retrying next launch"),
        };

    private readonly OverlaySettings _settings = SettingsStore.Load();
    private readonly OverlayRuntime _runtime;
    private readonly OverlayWindow _overlay;
    private HwndSource? _windowSource;
    private bool _loadingSettings;
    private bool _capturingHotkey;
    private bool _hotkeyRegistered;
    private string? _registeredHotkey;
    private bool _connected;
    private bool _isPlaying;
    private string? _selectedStateKey = "noChart";
    private string? _runtimeStatusKey;
    private string? _runtimeStatusDetail;
    private int _selectionGeneration;
    private readonly CancellationTokenSource _stopUpdate = new();
    private Task? _updateTask;
    private bool _updateScheduled;
    private string? _updateStatusKey;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr handle, int id);

    public MainWindow()
    {
        InitializeComponent();
        _settings.Language = string.Equals(_settings.Language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh-CN";
        _settings.Opacity = Math.Clamp(_settings.Opacity, 0.3, 1.0);
        _settings.Width = Math.Clamp(_settings.Width, 320, 1600);
        _settings.Height = Math.Clamp(_settings.Height, 300, 1600);
        _runtime = new OverlayRuntime(_settings);
        _overlay = new OverlayWindow(_runtime, _settings);
        _runtime.StatusChanged += Runtime_StatusChanged;
        _runtime.SelectionReceived += Runtime_SelectionReceived;
        _runtime.GameStateChanged += Runtime_GameStateChanged;
        _runtime.PluginIntegrityMismatch += Runtime_PluginIntegrityMismatch;
        _loadingSettings = true;
        try
        {
            LoadSettingsIntoControls();
            ApplyLanguage();
        }
        finally
        {
            _loadingSettings = false;
        }
        Loaded += MainWindow_Loaded;
        Closed += (_, _) =>
        {
            _stopUpdate.Cancel();
            UnregisterToggleHotkey();
            _overlay.Close();
            _runtime.Dispose();
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        FitControls();
        if (App.IsUpdateRecovery)
        {
            _updateStatusKey = "updateFailed";
            SetRuntimeStatus(_updateStatusKey);
        }
        BeginAutomaticUpdate();
        var cardIndex = Path.Combine(AppContext.BaseDirectory,
            "nonebot_plugin_rework", "tosu_plugin", "index.html");
        var runtimeStarted = false;
        if (File.Exists(cardIndex))
        {
            try
            {
                _runtime.Start();
                runtimeStarted = true;
            }
            catch (Exception exception)
            {
                SetRuntimeStatus(null, exception.Message);
                return;
            }
        }

        var sync = await BotPluginSynchronizer.EnsureCurrentAsync(_settings);
        if (!runtimeStarted)
        {
            if (!File.Exists(cardIndex))
            {
                var detail = _settings.Language == "en"
                    ? $"Bot card assets are missing. Remote sync failed: {sync.Error ?? "unknown error"}"
                    : $"Bot 卡片资源缺失，远程同步失败：{sync.Error ?? "未知错误"}";
                SetRuntimeStatus(null, detail);
                return;
            }
            try
            {
                _runtime.Start();
            }
            catch (Exception exception)
            {
                SetRuntimeStatus(null, exception.Message);
                return;
            }
        }
        if (sync.Updated)
            SetRuntimeStatus("botSourceUpdated");
        else if (sync.UsedBundledFallback)
            SetRuntimeStatus(null, _settings.Language == "en"
                ? $"Using verified bundled Bot card v{sync.Version}; remote update check is unavailable."
                : $"正在使用已验证的随包 Bot 卡片 v{sync.Version}；当前无法检查远程更新。");
        else if (sync.Error is not null)
            SetRuntimeStatus(null, _settings.Language == "en"
                ? $"Using bundled bot card; remote sync failed: {sync.Error}"
                : $"当前使用随包 Bot 卡片；远程同步失败：{sync.Error}");
        else if (_settings.BotPluginSyncEnabled && !string.IsNullOrWhiteSpace(_settings.BotPluginManifestUrl))
            SetRuntimeStatus("botSourceCurrent");
    }

    private void BeginAutomaticUpdate()
    {
        if (!App.IsUpdateRecovery && !_updateScheduled && (_updateTask is null || _updateTask.IsCompleted))
            _updateTask = RunAutomaticUpdateAsync();
    }

    private async Task RunAutomaticUpdateAsync()
    {
        try
        {
            var cancellationToken = _stopUpdate.Token;
            var manifest = await Task.Run(() => UpdateChecker.CheckAsync(cancellationToken), cancellationToken);
            if (manifest is null)
                return;
            var pluginRoot = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
            AutomaticUpdater.ValidatePluginRoot(pluginRoot);
            var gameExe = Path.Combine(Directory.GetParent(Directory.GetParent(pluginRoot)!.FullName)!.FullName, "Malody V.exe");
            _updateStatusKey = "updateWaiting";
            SetRuntimeStatus(_updateStatusKey);
            foreach (var game in AutomaticUpdater.FindProcesses("Malody V", gameExe))
            {
                using (game)
                    await game.WaitForExitAsync(cancellationToken);
            }
            _updateStatusKey = "updateDownloading";
            SetRuntimeStatus(_updateStatusKey);
            var update = await Task.Run(() => AutomaticUpdater.PrepareAsync(manifest, cancellationToken), cancellationToken);
            ReadSettingsFromControls();
            SettingsStore.Save(_settings);
            AutomaticUpdater.StartInstaller(update);
            _updateScheduled = true;
            _updateStatusKey = "updateInstalling";
            SetRuntimeStatus(_updateStatusKey);
        }
        catch (OperationCanceledException) when (_stopUpdate.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _updateStatusKey = "updateFailed";
            SetRuntimeStatus(_updateStatusKey);
            RuntimeStatus.ToolTip = exception.Message;
        }
    }

    private void FitControls()
    {
        ControlSizing.FitWindow(this,
            new FrameworkElement[] { WindowTitle, WindowActions },
            new FrameworkElement[] { WindowSubtitle, ConnectionBadge },
            new FrameworkElement[] { EnabledBox, HotkeyButton },
            new FrameworkElement[] { TopmostBox, DragBox },
            new FrameworkElement[] { HideDuringPlayBox },
            new FrameworkElement[] { SaveButton });
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        FitControls();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource?.AddHook(WindowMessageHook);
        if (!UpdateToggleHotkey())
            SetRuntimeStatus("hotkeyConflict");
    }

    private void LoadSettingsIntoControls()
    {
        EnabledBox.IsChecked = _settings.Enabled;
        TopmostBox.IsChecked = _settings.AlwaysOnTop;
        DragBox.IsChecked = _settings.DragEnabled;
        HideDuringPlayBox.IsChecked = _settings.HideDuringPlay;
        OpacitySlider.Value = _settings.Opacity * 100;
        HotkeyButton.Content = _settings.ToggleHotkey;
        _overlay.ApplySettings();
    }

    private string T(string key)
    {
        var value = Texts[key];
        return _settings.Language == "en" ? value.English : value.Chinese;
    }

    private void ApplyLanguage()
    {
        Title = T("title");
        WindowTitle.Text = T("title");
        WindowSubtitle.Text = T("subtitle");
        SponsorHyperlink.Inlines.Clear();
        SponsorHyperlink.Inlines.Add(new Run(_settings.Language == "en"
            ? "Support"
            : "赞助支持"));
        LanguageButton.Content = _settings.Language == "en" ? "中文" : "EN";
        SelectedSectionTitle.Text = T("selected");
        OptionsTitle.Text = T("cardOptions");
        EnabledBox.Content = T("showCard");
        TopmostBox.Content = T("alwaysOnTop");
        DragBox.Content = T("dragCard");
        HideDuringPlayBox.Content = T("hideDuringPlay");
        OpacityLabel.Text = T("opacity");
        SaveButton.Content = T("save");
        HotkeyButton.Content = _capturingHotkey ? T("pressHotkey") : _settings.ToggleHotkey;
        HotkeyButton.ToolTip = T("hotkeyTooltip");
        OpacityValue.Text = $"{Math.Round(OpacitySlider.Value):0}%";
        if (_selectedStateKey is not null)
            SelectedMap.Text = T(_selectedStateKey);
        if (_runtime.CurrentMap is { } map)
            SelectedPath.Text = $"{map.SourcePath}  [{T("difficulty")} {map.Index + 1}]";
        ConnectionState.Text = _connected ? T("connected") : T("waiting");
        ConnectionDot.Fill = _connected ? (Brush)FindResource("Accent") : (Brush)FindResource("Muted");
        RuntimeStatus.Text = _runtimeStatusDetail ?? (_runtimeStatusKey is null ? T("ready") : T(_runtimeStatusKey));
        Dispatcher.BeginInvoke(FitControls);
    }

    private void Runtime_StatusChanged(string status)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _connected = status == "Connected to game";
            ConnectionState.Text = _connected
                ? T("connected")
                : status.StartsWith("Pipe reconnecting", StringComparison.Ordinal)
                    ? T("reconnecting")
                    : T("waiting");
            ConnectionDot.Fill = _connected ? (Brush)FindResource("Accent") : (Brush)FindResource("Muted");
        });
    }

    private async void Runtime_SelectionReceived(SelectionMessage? selection)
    {
        var generation = Interlocked.Increment(ref _selectionGeneration);
        if (selection is null || string.IsNullOrWhiteSpace(selection.Path))
        {
            await Dispatcher.InvokeAsync(() =>
            {
                _selectedStateKey = "noChart";
                SelectedMap.Text = T(_selectedStateKey);
                SelectedPath.Text = string.Empty;
                _overlay.Hide();
            });
            _runtime.SetCurrentMap(null);
            return;
        }

        await Dispatcher.InvokeAsync(() =>
        {
            _selectedStateKey = "loading";
            SelectedMap.Text = T(_selectedStateKey);
            SelectedPath.Text = selection.Path;
        });
        try
        {
            var payload = await MapLoader.LoadAsync(selection, _settings);
            if (generation != Volatile.Read(ref _selectionGeneration))
                return;
            _runtime.SetCurrentMap(payload);
            await Dispatcher.InvokeAsync(async () =>
            {
                _selectedStateKey = null;
                SelectedMap.Text = payload.Title;
                SelectedPath.Text = $"{payload.SourcePath}  [{T("difficulty")} {payload.Index + 1}]";
                SetRuntimeStatus("cardReady");
                await _overlay.ShowMapAsync();
                ApplyVisibility();
            });
        }
        catch (Exception exception)
        {
            if (generation != Volatile.Read(ref _selectionGeneration))
                return;
            _runtime.SetCurrentMap(null);
            await Dispatcher.InvokeAsync(() =>
            {
                _selectedStateKey = "renderFailed";
                SelectedMap.Text = T(_selectedStateKey);
                SetRuntimeStatus(null, exception.Message);
                _overlay.Hide();
            });
        }
    }

    private void Runtime_GameStateChanged(bool isPlaying)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _isPlaying = isPlaying;
            ApplyVisibility();
        });
    }

    private void Runtime_PluginIntegrityMismatch(SelectionMessage message)
    {
        Dispatcher.BeginInvoke(BeginAutomaticUpdate);
    }

    private void Settings_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _loadingSettings)
            return;
        ReadSettingsFromControls();
        _overlay.ApplySettings();
        ApplyVisibility();
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized)
            return;
        _settings.Opacity = Math.Clamp(OpacitySlider.Value / 100.0, 0.3, 1.0);
        OpacityValue.Text = $"{Math.Round(OpacitySlider.Value):0}%";
        _overlay.ApplySettings();
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        ReadSettingsFromControls();
        _overlay.ApplySettings();
        ApplyVisibility();
        try
        {
            SettingsStore.Save(_settings);
            SetRuntimeStatus("settingsSaved");
        }
        catch (Exception exception)
        {
            SetRuntimeStatus(null, exception.Message);
        }
    }

    private void ReadSettingsFromControls()
    {
        _settings.Enabled = EnabledBox.IsChecked == true;
        _settings.AlwaysOnTop = TopmostBox.IsChecked == true;
        _settings.DragEnabled = DragBox.IsChecked == true;
        _settings.HideDuringPlay = HideDuringPlayBox.IsChecked == true;
        _settings.Opacity = Math.Clamp(OpacitySlider.Value / 100.0, 0.3, 1.0);
    }

    private void ApplyVisibility()
    {
        if (_settings.Enabled && _runtime.CurrentMap is not null && !(_settings.HideDuringPlay && _isPlaying))
            _overlay.Show();
        else
            _overlay.Hide();
    }

    private void LanguageButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.Language = _settings.Language == "en" ? "zh-CN" : "en";
        ApplyLanguage();
        try
        {
            SettingsStore.Save(_settings);
        }
        catch (Exception exception)
        {
            SetRuntimeStatus(null, exception.Message);
        }
    }

    private void HotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _capturingHotkey = true;
        HotkeyButton.Content = T("pressHotkey");
        FitControls();
        HotkeyButton.Focus();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingHotkey)
            return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            _capturingHotkey = false;
            HotkeyButton.Content = _settings.ToggleHotkey;
            e.Handled = true;
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or
            Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            return;
        }

        var previousHotkey = _settings.ToggleHotkey;
        _settings.ToggleHotkey = FormatHotkey(key, Keyboard.Modifiers);
        _capturingHotkey = false;
        var registered = UpdateToggleHotkey();
        if (!registered)
            _settings.ToggleHotkey = previousHotkey;
        HotkeyButton.Content = _settings.ToggleHotkey;
        try
        {
            SettingsStore.Save(_settings);
        }
        catch (Exception exception)
        {
            SetRuntimeStatus(null, exception.Message);
        }
        SetRuntimeStatus(registered ? "hotkeyChanged" : "hotkeyConflict");
        e.Handled = true;
    }

    private static string FormatHotkey(Key key, ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if ((modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    private bool UpdateToggleHotkey()
    {
        var previousHotkey = _registeredHotkey;
        UnregisterToggleHotkey();
        if (_windowSource is null)
            return false;
        var canRegister = TryParseHotkey(_settings.ToggleHotkey, out var modifiers, out var virtualKey);
        _hotkeyRegistered = canRegister && RegisterHotKey(_windowSource.Handle, ToggleHotkeyId,
            modifiers | ModNoRepeat, virtualKey);
        if (_hotkeyRegistered)
        {
            _registeredHotkey = _settings.ToggleHotkey;
            return true;
        }
        if (previousHotkey is not null && TryParseHotkey(previousHotkey, out modifiers, out virtualKey))
        {
            _hotkeyRegistered = RegisterHotKey(_windowSource.Handle, ToggleHotkeyId,
                modifiers | ModNoRepeat, virtualKey);
            if (_hotkeyRegistered)
                _registeredHotkey = previousHotkey;
        }
        return false;
    }

    private static bool TryParseHotkey(string value, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        var parts = value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;
        foreach (var part in parts[..^1])
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control": modifiers |= ModControl; break;
                case "shift": modifiers |= ModShift; break;
                case "alt": modifiers |= ModAlt; break;
                case "win":
                case "windows": modifiers |= ModWin; break;
                default: return false;
            }
        }
        if (!Enum.TryParse<Key>(parts[^1], true, out var key) || key is Key.None or Key.System)
            return false;
        virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        return virtualKey != 0;
    }

    private void UnregisterToggleHotkey()
    {
        if (_hotkeyRegistered && _windowSource is not null)
            UnregisterHotKey(_windowSource.Handle, ToggleHotkeyId);
        _hotkeyRegistered = false;
        _registeredHotkey = null;
    }

    private IntPtr WindowMessageHook(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == ToggleHotkeyId && !_capturingHotkey)
        {
            ToggleCardFromHotkey();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void ToggleCardFromHotkey()
    {
        _settings.Enabled = !_settings.Enabled;
        _loadingSettings = true;
        EnabledBox.IsChecked = _settings.Enabled;
        _loadingSettings = false;
        _overlay.ApplySettings();
        ApplyVisibility();
        try
        {
            SettingsStore.Save(_settings);
        }
        catch (Exception exception)
        {
            SetRuntimeStatus(null, exception.Message);
        }
    }

    private void WindowHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void SponsorLink_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var target = new UriBuilder(Uri.UriSchemeHttps, "ifdian.net") { Path = "/a/mcpsde" }.Uri;
            Process.Start(new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            SetRuntimeStatus(null, exception.Message);
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void SetRuntimeStatus(string? key, string? detail = null)
    {
        if (_updateStatusKey is not null && key != _updateStatusKey)
            return;
        _runtimeStatusKey = key;
        _runtimeStatusDetail = detail;
        RuntimeStatus.Text = detail ?? (key is null ? T("ready") : T(key));
    }
}
