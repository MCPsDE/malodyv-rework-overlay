using System.Threading;
using System.Windows;

namespace MalodyV.ReworkOverlay;

public partial class App : Application
{
    private Mutex? _singleInstance;
    internal static bool IsUpdateRecovery { get; private set; }

    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        if (e.Args.Length == 2 && e.Args[0] == "--apply-update")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var result = await Task.Run(() => AutomaticUpdater.RunInstallerAsync(e.Args[1]));
            Shutdown(result);
            return;
        }
        IsUpdateRecovery = e.Args.Contains("--update-recovery");
        _singleInstance = new Mutex(true, @"Local\MalodyV.ReworkOverlay", out var created);
        if (!created)
        {
            Shutdown();
            return;
        }
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        _singleInstance?.Dispose();
    }
}
