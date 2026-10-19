using System.Threading;
using System.Windows;

namespace MalodyV.ReworkOverlay;

public partial class App : Application
{
    private Mutex? _singleInstance;

    private void Application_Startup(object sender, StartupEventArgs e)
    {
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
