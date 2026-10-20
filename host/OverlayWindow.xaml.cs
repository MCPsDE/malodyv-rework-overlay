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
    private const double ResizeBorderSize = 12;
    private const double CardAspectRatio = 515d / 610d;
    private const double MaximumCardWidth = 1400;
    private readonly OverlayRuntime _runtime;
    private readonly OverlaySettings _settings;
    private bool _browserReady;
    private PointerOperation _pointerOperation;
    private ResizeEdges _resizeEdges;
    private Point _resizeStartPointerScreen;
    private Rect _resizeStartBounds;

    private enum PointerOperation
    {
        None,
        PendingDrag,
        Drag,
        Resize,
    }

    [Flags]
    private enum ResizeEdges
    {
        None = 0,
        Left = 1,
        Right = 2,
        Top = 4,
        Bottom = 8,
    }

    private const string OverlayLayoutScript = """
        (() => {
            const install = () => {
                if (!document.body || document.getElementById('overlay-layout-style')) return;
                const style = document.createElement('style');
                style.id = 'overlay-layout-style';
                style.textContent = `
                    html, body { width:100% !important; height:100% !important; overflow:hidden !important; }
                    .dashboard { position:absolute !important; left:0 !important; top:0 !important;
                        width:515px !important; min-width:515px !important; max-width:515px !important;
                        height:610px !important; min-height:610px !important; max-height:610px !important;
                        margin:0 !important; align-content:stretch !important;
                        grid-template-rows:minmax(0,1fr) !important; gap:0 !important;
                        transform:scale(var(--overlay-scale, 1)) !important;
                        transform-origin:top left !important; }
                    .main-card { width:100% !important; height:100% !important; min-height:0 !important;
                        max-height:none !important; overflow:auto !important; scrollbar-width:thin;
                        scrollbar-color:rgba(132,215,232,.42) transparent; }
                    body { padding:0 !important; user-select:none !important; -webkit-user-select:none !important; }
                    .main-card, .card.main-card { border:0 !important; outline:0 !important; box-shadow:none !important; }
                    .main-card::after { border:0 !important; box-shadow:none !important; }
                `;
                document.head.appendChild(style);
                const updateScale = () => {
                    const scale = Math.min(window.innerWidth / 515, window.innerHeight / 610);
                    document.documentElement.style.setProperty('--overlay-scale', String(scale));
                };
                window.addEventListener('resize', updateScale, { passive:true });
                updateScale();
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
            Left = _settings.Left;
            Top = _settings.Top;
            var configuredWidth = double.IsFinite(_settings.Width) ? _settings.Width : 515;
            Width = Math.Clamp(configuredWidth, MinWidth, MaximumCardWidth);
            Height = Width / CardAspectRatio;
            var boundsRecovered = EnsureVisibleBounds();
            _settings.Width = Width;
            _settings.Height = Height;
            _settings.Left = Left;
            _settings.Top = Top;
            if (boundsRecovered)
                PersistGeometry();
        }
        Opacity = 1;
        Topmost = _settings.AlwaysOnTop;
        ApplyHitTesting();
        ApplyCardOpacity();
    }

    private bool EnsureVisibleBounds()
    {
        var originalLeft = Left;
        var originalTop = Top;
        if (!double.IsFinite(originalLeft) || !double.IsFinite(originalTop))
        {
            Left = 40;
            Top = 80;
            return true;
        }

        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;
        var maxLeft = Math.Max(virtualLeft, virtualRight - Width);
        var maxTop = Math.Max(virtualTop, virtualBottom - Height);

        Left = Math.Clamp(originalLeft, virtualLeft, maxLeft);
        Top = Math.Clamp(originalTop, virtualTop, maxTop);
        return Left != originalLeft || Top != originalTop;
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
        _resizeEdges = GetResizeEdges(point);
        if (_resizeEdges != ResizeEdges.None)
            _pointerOperation = PointerOperation.Resize;
        else if (_settings.DragEnabled)
            _pointerOperation = PointerOperation.PendingDrag;
        else
            return;

        _resizeStartPointerScreen = PointToScreen(point);
        _resizeStartBounds = new Rect(Left, Top, ActualWidth, ActualHeight);
        if (_pointerOperation == PointerOperation.Resize)
        {
            Mouse.Capture(this, CaptureMode.SubTree);
            e.Handled = true;
        }
    }

    private void OverlayWindow_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pointerOperation == PointerOperation.None)
        {
            UpdateCursor(e.GetPosition(this));
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        var pointerScreen = PointToScreen(e.GetPosition(this));
        var dpi = VisualTreeHelper.GetDpi(this);
        var deltaX = (pointerScreen.X - _resizeStartPointerScreen.X) / dpi.DpiScaleX;
        var deltaY = (pointerScreen.Y - _resizeStartPointerScreen.Y) / dpi.DpiScaleY;

        if (_pointerOperation == PointerOperation.PendingDrag)
        {
            if (Math.Abs(deltaX) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(deltaY) < SystemParameters.MinimumVerticalDragDistance)
                return;

            _pointerOperation = PointerOperation.Drag;
            if (!Mouse.Capture(this, CaptureMode.SubTree))
            {
                _pointerOperation = PointerOperation.None;
                return;
            }
        }

        if (_pointerOperation == PointerOperation.Drag)
        {
            Left = _resizeStartBounds.Left + deltaX;
            Top = _resizeStartBounds.Top + deltaY;
            UpdateStoredPosition();
        }
        else
        {
            ResizeProportionally(deltaX, deltaY);
            _settings.Width = Width;
            _settings.Height = Height;
            UpdateStoredPosition();
        }

        e.Handled = true;
    }

    private void ResizeProportionally(double deltaX, double deltaY)
    {
        var hasHorizontalEdge = _resizeEdges.HasFlag(ResizeEdges.Left) ||
                                _resizeEdges.HasFlag(ResizeEdges.Right);
        var hasVerticalEdge = _resizeEdges.HasFlag(ResizeEdges.Top) ||
                              _resizeEdges.HasFlag(ResizeEdges.Bottom);
        var widthChange = _resizeEdges.HasFlag(ResizeEdges.Left) ? -deltaX : deltaX;
        var heightChange = _resizeEdges.HasFlag(ResizeEdges.Top) ? -deltaY : deltaY;
        var widthFactor = 1 + widthChange / _resizeStartBounds.Width;
        var heightFactor = 1 + heightChange / _resizeStartBounds.Height;
        var factor = hasHorizontalEdge && hasVerticalEdge
            ? Math.Abs(widthFactor - 1) >= Math.Abs(heightFactor - 1) ? widthFactor : heightFactor
            : hasHorizontalEdge ? widthFactor : heightFactor;
        var width = Math.Clamp(_resizeStartBounds.Width * Math.Max(0.05, factor), MinWidth, MaximumCardWidth);
        var height = width / CardAspectRatio;

        if (_resizeEdges.HasFlag(ResizeEdges.Left))
            Left = _resizeStartBounds.Right - width;
        else if (!hasHorizontalEdge)
            Left = _resizeStartBounds.Left + (_resizeStartBounds.Width - width) / 2;
        else
            Left = _resizeStartBounds.Left;

        if (_resizeEdges.HasFlag(ResizeEdges.Top))
            Top = _resizeStartBounds.Bottom - height;
        else if (!hasVerticalEdge)
            Top = _resizeStartBounds.Top + (_resizeStartBounds.Height - height) / 2;
        else
            Top = _resizeStartBounds.Top;

        Width = width;
        Height = height;
    }

    private void OverlayWindow_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pointerOperation == PointerOperation.None)
            return;
        if (_pointerOperation == PointerOperation.PendingDrag)
        {
            _pointerOperation = PointerOperation.None;
            return;
        }
        _pointerOperation = PointerOperation.None;
        _resizeEdges = ResizeEdges.None;
        Mouse.Capture(null);
        PersistGeometry();
        e.Handled = true;
    }

    private void UpdateCursor(Point point)
    {
        var edges = GetResizeEdges(point);
        Cursor = edges switch
        {
            ResizeEdges.Left or ResizeEdges.Right => Cursors.SizeWE,
            ResizeEdges.Top or ResizeEdges.Bottom => Cursors.SizeNS,
            ResizeEdges.Left | ResizeEdges.Top or ResizeEdges.Right | ResizeEdges.Bottom => Cursors.SizeNWSE,
            ResizeEdges.Right | ResizeEdges.Top or ResizeEdges.Left | ResizeEdges.Bottom => Cursors.SizeNESW,
            _ => Cursors.Arrow,
        };
    }

    private ResizeEdges GetResizeEdges(Point point)
    {
        var edges = ResizeEdges.None;
        if (point.X <= ResizeBorderSize)
            edges |= ResizeEdges.Left;
        else if (point.X >= ActualWidth - ResizeBorderSize)
            edges |= ResizeEdges.Right;
        if (point.Y <= ResizeBorderSize)
            edges |= ResizeEdges.Top;
        else if (point.Y >= ActualHeight - ResizeBorderSize)
            edges |= ResizeEdges.Bottom;
        return edges;
    }

    private void UpdateStoredPosition()
    {
        _settings.Left = Left;
        _settings.Top = Top;
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
