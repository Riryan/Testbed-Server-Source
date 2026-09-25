using System.Windows;

namespace MMODashboard;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Handle command-line validation before normal WPF startup work.
        if (e.Args.Any(a => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = DashboardSelfTest.Run(e.Args);
            Shutdown(Environment.ExitCode);
            return;
        }

        base.OnStartup(e);

        var root = ServerRootResolver.Resolve(AppContext.BaseDirectory);
        if (root is null)
        {
            MessageBox.Show(
                "MMODashboard.exe must live in the main Server folder (the folder containing Source and the server folders).",
                "MMO Dashboard",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        var window = new MainWindow(root);
        MainWindow = window;
        window.Show();
    }
}
