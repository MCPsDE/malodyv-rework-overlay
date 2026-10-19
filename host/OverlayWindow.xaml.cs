using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace MalodyV.ReworkOverlay;

public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x20;
    private const long WsExLayered = 0x80000;
    private const double ResizeGripSize = 44;
    private readonly OverlayRuntime _runtime;
    private readonly OverlaySettings _settings;
    private bool _browserReady;
    private PointerOperation _pointerOperation;
    private Point _lastPointerScreen;

    private enum PointerOperation
    {
        None,
        Drag,
        Resize,
    }

    private const string OverlayLayoutScript = """
        (() => {
            const install = () => {
                if (!document.body || document.getElementById('overlay-resize-grip')) return;
                const style = document.createElement('style');
                style.textContent = `
                    .dashboard { width:100% !important; min-width:0 !important; max-width:none !important;
                        height:100% !important; min-height:0 !important; align-content:stretch !important;
                        grid-template-rows:minmax(0,1fr) !important; gap:0 !important; }
                    .main-card { width:100% !important; height:100% !important; min-height:0 !important;
                        max-height:none !important; overflow:auto !important; scrollbar-width:thin;
                        scrollbar-color:rgba(132,215,232,.42) transparent; }
                    body { user-select:none !important; -webkit-user-select:none !important; }
                    #overlay-resize-grip { position:fixed; right:4px; bottom:4px; z-index:2147483647;
                        width:40px; height:40px; display:flex; flex-direction:column; align-items:flex-end;
                        justify-content:flex-end; gap:4px; padding:0 3px 3px; pointer-events:none; }
                    #overlay-resize-grip span { display:block; height:2px; background:rgba(159,224,239,.82);
                        transform:rotate(-45deg); transform-origin:right center; border-radius:2px; }
                    #overlay-resize-grip span:nth-child(1) { width:9px; }
                    #overlay-resize-grip span:nth-child(2) { width:15px; }
                    #overlay-resize-grip span:nth-child(3) { width:21px; }
                `;
                const grip = document.createElement('div');
                grip.id = 'overlay-resize-grip';
                grip.setAttribute('aria-label', 'Resize card');
                grip.innerHTML = '<span></span><span></span><span></span>';
                document.head.appendChild(style);
                document.body.appendChild(grip);
            };
            if (document.readyState === 'loading')
                document.addEventListener('DOMContentLoaded', install, { once:true });
            else install();
        })();
        """;

    internal OverlayWindow(OverlayRuntime runtime, OverlaySettings settings)
    {
        InitializeComponent();
        _runtime = runtime;
        _settings = settings;
        Loaded += OverlayWindow_Loaded;
    }

    private async void OverlayWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await CardBrowser.EnsureCoreWebView2Async();
            await CardBrowser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync("window.REWORK_OFFLINE_MODE = true;");
            await CardBrowser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(OverlayLayoutScript);
            CardBrowser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            CardBrowser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            CardBrowser.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
            CardBrowser.CoreWebView2.Navigate(_runtime.PageUrl);
            ApplySettings();
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Overlay WebView2 initialization failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public async Task ShowMapAsync()
    {
        if (_browserReady)
            CardBrowser.CoreWebView2.Reload();
        if (_settings.Enabled)
        {
            ApplySettings();
            Show();
        }
        await Task.CompletedTask;
    }

    public void ApplySettings()
    {
        if (_pointerOperation == PointerOperation.None)
        {
            Left = _settings.Left - 18;
            Top = _settings.Top - 18;
            Width = Math.Max(MinWidth, _settings.Width);
            Height = Math.Max(MinHeight, _settings.Height);
        }
        Opacity = 1;
        Topmost = _settings.AlwaysOnTop;
        ApplyHitTesting();
        ApplyCardOpacity();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyHitTesting();
    }

    private void ApplyHitTesting()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        style |= WsExLayered;
        style &= ~WsExTransparent;
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style));
    }

    private void CoreWebView2_NavigationCompleted(object? sender,
        Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
    {
        _browserReady = e.IsSuccess;
        ApplyCardOpacity();
    }

    private async void ApplyCardOpacity()
    {
        if (!_browserReady || CardBrowser.CoreWebView2 is null)
            return;
        var opacity = Math.Clamp(_settings.Opacity, 0.3, 1.0).ToString(CultureInfo.InvariantCulture);
        var script = $"(() => {{ const card = document.querySelector('.main-card'); " +
                     $"if (card) card.style.setProperty('--card-opacity', '{opacity}', 'important'); }})()";
        try
        {
            await CardBrowser.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch
        {
        }
    }

    private void OverlayWindow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        var point = e.GetPosition(this);
        if (point.X >= ActualWidth - ResizeGripSize && point.Y >= ActualHeight - ResizeGripSize)
            _pointerOperation = PointerOperation.Resize;
        else if (_settings.DragEnabled)
            _pointerOperation = PointerOperation.Drag;
        else
            return;

        _lastPointerScreen = PointToScreen(point);
        Mouse.Capture(this, CaptureMode.SubTree);
        e.Handled = true;
    }

    private void OverlayWindow_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pointerOperation == PointerOperation.None || e.LeftButton != MouseButtonState.Pressed)
            return;

        var pointerScreen = PointToScreen(e.GetPosition(this));
        var dpi = VisualTreeHelper.GetDpi(this);
        var deltaX = (pointerScreen.X - _lastPointerScreen.X) / dpi.DpiScaleX;
        var deltaY = (pointerScreen.Y - _lastPointerScreen.Y) / dpi.DpiScaleY;
        _lastPointerScreen = pointerScreen;

        if (_pointerOperation == PointerOperation.Drag)
        {
            Left += deltaX;
            Top += deltaY;
            UpdateStoredPosition();
        }
        else
        {
            Width = Math.Clamp(Width + deltaX, MinWidth, 1600);
            Height = Math.Clamp(Height + deltaY, MinHeight, 1600);
            _settings.Width = Width;
            _settings.Height = Height;
        }

        e.Handled = true;
    }

    private void OverlayWindow_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pointerOperation == PointerOperation.None)
            return;
        _pointerOperation = PointerOperation.None;
        Mouse.Capture(null);
        PersistGeometry();
        e.Handled = true;
    }

    private void UpdateStoredPosition()
    {
        _settings.Left = Left + 18;
        _settings.Top = Top + 18;
    }

    private void PersistGeometry()
    {
        UpdateStoredPosition();
        _settings.Width = Width;
        _settings.Height = Height;
        try
        {
            SettingsStore.Save(_settings);
        }
        catch
        {
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value);
}
