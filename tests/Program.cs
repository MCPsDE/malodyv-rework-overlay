using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using MalodyV.ReworkOverlay;

internal static class Program
{
    private static int _checks;

    [STAThread]
    private static void Main(string[] args)
    {
        var root = Path.GetFullPath(args[0]);
        var artifacts = Path.Combine(root, "release", "verification-1.3.17");
        Directory.CreateDirectory(artifacts);
        var work = Path.Combine(Path.GetTempPath(), "OverlayChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            CheckUpdates(work);
            CheckUi(root, artifacts);
            Console.WriteLine($"PASS: {_checks} checks. Rendered WPF captures: {artifacts}");
        }
        finally
        {
            Directory.Delete(work, true);
        }
    }

    private static void CheckUpdates(string work)
    {
        var package = Path.Combine(work, "package");
        var host = Path.Combine(package, AutomaticUpdater.HostDirectoryName);
        Directory.CreateDirectory(Path.Combine(host, "nonebot_plugin_rework", "tosu_plugin"));
        File.WriteAllText(Path.Combine(host, "nonebot_plugin_rework", "tosu_plugin", "index.html"), "card");
        File.WriteAllText(Path.Combine(host, "MalodyV.ReworkOverlay.exe"), "new exe");
        File.WriteAllText(Path.Combine(host, "MalodyV.ReworkOverlay.dll"), "new managed host");
        File.WriteAllText(Path.Combine(host, "MalodyV.ReworkOverlay.runtimeconfig.json"), "{}");
        File.WriteAllText(Path.Combine(package, AutomaticUpdater.PluginFileName), "new plugin");
        var manifest = new UpdateManifest
        {
            Version = "1.3.17",
            HostSha256 = UpdateChecker.ComputeSha256(Path.Combine(host, "MalodyV.ReworkOverlay.exe")),
            HostAssemblySha256 = UpdateChecker.ComputeSha256(Path.Combine(host, "MalodyV.ReworkOverlay.dll")),
            PluginSha256 = UpdateChecker.ComputeSha256(Path.Combine(package, AutomaticUpdater.PluginFileName)),
            PackageSha256 = new string('A', 64),
            PackageUrl = UpdateChecker.PackageUrlFor("1.3.17"),
        };
        File.WriteAllText(Path.Combine(package, "manifest.json"), JsonSerializer.Serialize(manifest));
        Assert(UpdateChecker.IsValidManifest(manifest), "valid release manifest");
        Assert(!UpdateChecker.IsValidManifest(new UpdateManifest { Version = "1.3.17" }), "missing hashes rejected");
        var target = Path.Combine(work, "game", "BepInEx", "plugins");
        var installedHost = Path.Combine(target, AutomaticUpdater.HostDirectoryName);
        Directory.CreateDirectory(installedHost);
        File.WriteAllText(Path.Combine(installedHost, "old.txt"), "old host");
        File.WriteAllText(Path.Combine(target, AutomaticUpdater.PluginFileName), "old plugin");
        File.WriteAllText(Path.Combine(target, "manifest.json"), "old manifest");
        Directory.CreateDirectory(Path.Combine(target, "OtherPlugin"));
        File.WriteAllText(Path.Combine(target, "OtherPlugin", "keep.dll"), "other plugin");
        using (File.Open(Path.Combine(target, "manifest.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Reject<IOException>(() => AutomaticUpdater.ApplyPackage(package, target, manifest), "locked file triggers rollback");
            Assert(File.ReadAllText(Path.Combine(installedHost, "old.txt")) == "old host", "host rolled back");
            Assert(File.ReadAllText(Path.Combine(target, AutomaticUpdater.PluginFileName)) == "old plugin", "plugin rolled back");
        }
        AutomaticUpdater.ApplyPackage(package, target, manifest);
        Assert(!File.Exists(Path.Combine(installedHost, "old.txt")), "old host replaced");
        Assert(File.ReadAllText(Path.Combine(target, "OtherPlugin", "keep.dll")) == "other plugin", "other plugins preserved");
        Assert(!UpdateChecker.NeedsUpdate(manifest, installedHost, new Version(1, 3, 17)), "matching install does not loop");
        Assert(!UpdateChecker.NeedsUpdate(manifest, installedHost, new Version(1, 3, 18)), "older remote cannot downgrade");
        Assert(UpdateChecker.NeedsUpdate(manifest, installedHost, new Version(1, 3, 16)), "new version detected");
        File.WriteAllText(Path.Combine(installedHost, "MalodyV.ReworkOverlay.dll"), "damaged managed host");
        Assert(UpdateChecker.NeedsUpdate(manifest, installedHost, new Version(1, 3, 17)), "managed DLL corruption detected");
        File.WriteAllText(Path.Combine(host, "MalodyV.ReworkOverlay.dll"), "corrupt download");
        Reject<InvalidDataException>(() => AutomaticUpdater.ApplyPackage(package, target, manifest), "corrupt download rejected before replacement");
        Reject<InvalidDataException>(() => AutomaticUpdater.ValidatePluginRoot(work), "unrelated destination rejected");
        foreach (var name in new[] { "../escape.dll", "MalodyVReworkOverlay/../escape.dll", "/absolute.dll", "OtherPlugin/hijack.dll", "MalodyVReworkOverlay/ads:payload" })
        {
            var archive = Path.Combine(work, Guid.NewGuid() + ".zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry(name).Open()))
                writer.Write("unsafe");
            Reject<InvalidDataException>(() => AutomaticUpdater.ExtractPackage(archive, Path.Combine(work, Guid.NewGuid().ToString("N"))), "ZIP path rejected: " + name);
        }
        var validZip = Path.Combine(work, "valid.zip");
        ZipFile.CreateFromDirectory(package, validZip);
        var extracted = Path.Combine(work, "extracted");
        AutomaticUpdater.ExtractPackage(validZip, extracted);
        Assert(File.Exists(Path.Combine(extracted, AutomaticUpdater.HostDirectoryName, "nonebot_plugin_rework", "tosu_plugin", "index.html")), "normal ZIP layout extracted");
        Console.WriteLine("Updater: versions, hashes, ZIP traversal, replacement, rollback, and other-plugin preservation passed.");
    }

    private static void CheckUi(string root, string artifacts)
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var appXml = XDocument.Load(Path.Combine(root, "host", "App.xaml"));
        var presentation = (XNamespace)"http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = new XElement(presentation + "ResourceDictionary",
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            appXml.Root!.Element(presentation + "Application.Resources")!.Elements());
        application.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
        foreach (var language in new[] { "zh", "en" })
        foreach (var fontScale in new[] { 1d, 1.25 })
        {
            var document = XDocument.Load(Path.Combine(root, "host", "MainWindow.xaml"));
            var eventNames = new HashSet<string> { "PreviewKeyDown", "MouseLeftButtonDown", "Click", "Checked", "Unchecked", "ValueChanged" };
            foreach (var element in document.Descendants())
            foreach (var attribute in element.Attributes().ToArray())
                if (attribute.Name.LocalName == "Class" || eventNames.Contains(attribute.Name.LocalName))
                    attribute.Remove();
            var window = (Window)XamlReader.Parse(document.ToString());
            var title = (TextBlock)window.FindName("WindowTitle");
            title.Text = language == "en" ? "MalodyV Rework Overlay" : "MalodyV 难度卡片";
            var labels = language == "en"
                ? new[] { "Show card", "Keep on top", "Drag card to move", "Hide during play, restore at results" }
                : new[] { "显示卡片", "始终置顶", "按住卡片拖动", "游玩时隐藏，结算后恢复" };
            var checks = new[] { "EnabledBox", "TopmostBox", "DragBox", "HideDuringPlayBox" }
                .Select(name => (CheckBox)window.FindName(name)).ToArray();
            for (var index = 0; index < checks.Length; index++)
            {
                checks[index].Content = labels[index];
                checks[index].FontSize *= fontScale;
            }
            var save = (Button)window.FindName("SaveButton");
            save.Content = language == "en" ? "Save settings" : "保存设置";
            save.FontSize *= fontScale;
            var hotkey = (Button)window.FindName("HotkeyButton");
            hotkey.Content = language == "en" ? "Press a new shortcut" : "请按下新快捷键";
            hotkey.FontSize *= fontScale;
            ((TextBlock)window.FindName("ConnectionState")).Text = language == "en" ? "Connected to game" : "已连接游戏";
            ((TextBlock)window.FindName("WindowSubtitle")).Text = language == "en" ? "Chart companion controls" : "谱面卡片控制台";
            ControlSizing.FitWindow(window,
                new FrameworkElement[] { title, (FrameworkElement)window.FindName("WindowActions") },
                new FrameworkElement[] { (TextBlock)window.FindName("WindowSubtitle"), (FrameworkElement)window.FindName("ConnectionBadge") },
                new FrameworkElement[] { checks[0], hotkey },
                new FrameworkElement[] { checks[1], checks[2] },
                new FrameworkElement[] { checks[3] });
            foreach (var requestedWidth in new[] { 460d, 520d })
            {
                var content = (FrameworkElement)window.Content;
                var width = Math.Max(window.MinWidth, requestedWidth);
                content.Measure(new Size(width, 700));
                content.Arrange(new Rect(0, 0, width, Math.Min(700, content.DesiredSize.Height)));
                content.UpdateLayout();
                foreach (var control in checks.Cast<Control>().Concat(new[] { save, hotkey }))
                {
                    var text = new FormattedText(control.ContentText(), System.Globalization.CultureInfo.CurrentUICulture,
                        FlowDirection.LeftToRight, new Typeface(control.FontFamily, control.FontStyle, control.FontWeight, control.FontStretch),
                        control.FontSize, Brushes.White, 1);
                    var minimum = text.WidthIncludingTrailingWhitespace + (control is CheckBox ? 37 : control.Padding.Left + control.Padding.Right + 2);
                    Assert(control.ActualWidth + 1 >= minimum, $"label fits: {language} {control.ContentText()}");
                }
                foreach (var scale in new[] { 1d, 1.25, 1.5, 2d })
                {
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * scale),
                        (int)Math.Ceiling(content.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(artifacts, $"controls-{language}-font{fontScale}-width{requestedWidth}-dpi{scale}.png"));
                    encoder.Save(output);
                }
            }
            var peer = new CheckBoxAutomationPeer(checks[0]);
            var toggle = (IToggleProvider)peer.GetPattern(PatternInterface.Toggle);
            var before = checks[0].IsChecked;
            toggle.Toggle();
            Assert(checks[0].IsChecked != before, "custom checkbox retains automation toggling");
            Assert(checks[0].Template.FindName("CheckBorder", checks[0]) is Border, "themed checkbox template applied");
            checks[1].IsChecked = false;
            checks[2].IsEnabled = false;
            checks[3].IsThreeState = true;
            checks[3].IsChecked = null;
            window.Close();
        }
        Console.WriteLine("UI: Chinese/English, minimum/normal width, enlarged labels, checkbox automation, synthetic 100/125/150/200% DPI passed.");
        application.Shutdown();
    }

    private static string ContentText(this Control control) => ((ContentControl)control).Content?.ToString() ?? "";

    private static void Assert(bool condition, string name)
    {
        if (!condition)
            throw new InvalidOperationException(name);
        _checks++;
    }

    private static void Reject<TException>(Action action, string name) where TException : Exception
    {
        try { action(); }
        catch (TException) { _checks++; return; }
        throw new InvalidOperationException(name);
    }
}
