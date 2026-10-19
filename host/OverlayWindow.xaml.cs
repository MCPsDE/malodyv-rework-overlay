using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;

namespace MalodyV.ReworkOverlay;

public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExLayered = 0x80000;
    private readonly OverlayRuntime _runtime;
    private readonly OverlaySettings _settings;
    private bool _browserReady;
    private bool _dragging;
    private bool _resizing;

    private const string OverlayInteractionScript = """
        (() => {
            const bridge = window.chrome && window.chrome.webview;
            if (!bridge) return;
            let dragEnabled = true;
            let activeGesture = null;
            bridge.addEventListener('message', event => {
                if (event.data && event.data.type === 'settings')
                    dragEnabled = Boolean(event.data.dragEnabled);
            });
            const install = () => {
                if (!document.body || document.getElementById('overlay-resize-grip')) return;
                const style = document.createElement('style');
                style.textContent = `
                    .dashboard { width:100% !important; min-width:0 !important; max-width:none !important;
                        height:100% !important; min-height:0 !important; align-content:stretch !important;
                        grid-template-rows:minmax(0,1fr) !important; gap:0 !important; }
                    html { --card-opacity:1 !important; }
                    .main-card { width:100% !important; height:100% !important; min-height:0 !important;
                        max-height:none !important; overflow:auto !important; scrollbar-width:thin;
                        scrollbar-color:rgba(132,215,232,.42) transparent; }
                    body { user-select:none !important; -webkit-user-select:none !important; }
                    #overlay-resize-grip { position:fixed; right:18px; bottom:18px; z-index:2147483647;
                        width:28px; height:28px; display:flex; flex-direction:column; align-items:flex-end;
                        justify-content:flex-end; gap:3px; padding:0 4px 4px; cursor:nwse-resize;
                        touch-action:none; }
                    #overlay-resize-grip span { display:block; height:2px; background:rgba(159,224,239,.78);
                        transform:rotate(-45deg); transform-origin:right center; border-radius:2px; }
                    #overlay-resize-grip span:nth-child(1) { width:7px; }
                    #overlay-resize-grip span:nth-child(2) { width:11px; }
                    #overlay-resize-grip span:nth-child(3) { width:15px; }
                    #overlay-resize-grip:hover span { background:#eafaff; }
                `;
                const grip = document.createElement('div');
                grip.id = 'overlay-resize-grip';
                grip.setAttribute('aria-label', 'Resize card');
                grip.innerHTML = '<span></span><span></span><span></span>';
                document.head.appendChild(style);
                document.body.appendChild(grip);
                document.addEventListener('pointerdown', event => {
                    if (event.button !== 0 || !dragEnabled ||
                        (event.target instanceof Element && event.target.closest('#overlay-resize-grip'))) return;
                    activeGesture = 'drag';
                    document.body.setPointerCapture(event.pointerId);
                    bridge.postMessage({ type:'drag-start' });
                }, true);
                grip.addEventListener('pointerdown', event => {
                    if (event.button !== 0) return;
                    event.preventDefault();
                    event.stopPropagation();
                    activeGesture = 'resize';
                    grip.setPointerCapture(event.pointerId);
                    bridge.postMessage({ type:'resize-start' });
                });
                document.addEventListener('pointermove', event => {
                    if (!activeGesture || !(event.buttons & 1)) return;
                    bridge.postMessage({ type:activeGesture + '-move', dx:event.movementX, dy:event.movementY });
                }, true);
                document.addEventListener('pointerup', () => {
                    if (!activeGesture) return;
                    bridge.postMessage({ type:activeGesture + '-end' });
                    activeGesture = null;
                }, true);
                document.addEventListener('pointercancel', () => {
                    if (!activeGesture) return;
                    bridge.postMessage({ type:activeGesture + '-end' });
                    activeGesture = null;
                }, true);
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
            await CardBrowser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(OverlayInteractionScript);
            CardBrowser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            CardBrowser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            CardBrowser.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
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
        Left = _settings.Left - 18;
        Top = _settings.Top - 18;
        Width = Math.Max(MinWidth, _settings.Width);
        Height = Math.Max(MinHeight, _settings.Height);
        Opacity = Math.Clamp(_settings.Opacity, 0.3, 1.0);
        Topmost = _settings.AlwaysOnTop;
        if (IsLoaded)
        {
            ApplyClickThrough();
            SendInteractionSettings();
        }
    }

    public event Action? GeometryChanged;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyClickThrough();
    }

    private void ApplyClickThrough()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        style |= WsExLayered;
        style &= ~((long)WsExTransparent);
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style));
    }

    private void CoreWebView2_NavigationCompleted(object? sender,
        Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
    {
        _browserReady = e.IsSuccess;
        SendInteractionSettings();
    }

    private void SendInteractionSettings()
    {
        if (!_browserReady || CardBrowser.CoreWebView2 is null)
            return;
        CardBrowser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            type = "settings",
            dragEnabled = _settings.DragEnabled,
        }));
    }

    private void CoreWebView2_WebMessageReceived(object? sender,
        Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            var root = message.RootElement;
            var type = root.GetProperty("type").GetString();
            switch (type)
            {
                case "drag-start":
                    _dragging = _settings.DragEnabled;
                    break;
                case "drag-move" when _dragging:
                    Left += root.GetProperty("dx").GetDouble();
                    Top += root.GetProperty("dy").GetDouble();
                    UpdateStoredPosition();
                    GeometryChanged?.Invoke();
                    break;
                case "drag-end":
                    _dragging = false;
                    PersistGeometry();
                    break;
                case "resize-start":
                    _resizing = true;
                    break;
                case "resize-move" when _resizing:
                    Width = Math.Clamp(Width + root.GetProperty("dx").GetDouble(), MinWidth, 1600);
                    Height = Math.Clamp(Height + root.GetProperty("dy").GetDouble(), MinHeight, 1600);
                    _settings.Width = Width;
                    _settings.Height = Height;
                    GeometryChanged?.Invoke();
                    break;
                case "resize-end":
                    _resizing = false;
                    PersistGeometry();
                    break;
            }
        }
        catch
        {
        }
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
