namespace BatomonHelper.Overlay;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (MainWindow is not null) return;
        if (e.Args.Length > 0 && e.Args[0] is "--install-bridge" or "--uninstall-bridge")
        {
            try
            {
                if (e.Args.Length is not (2 or 4) ||
                    e.Args[0] is not ("--install-bridge" or "--uninstall-bridge") ||
                    (e.Args.Length == 4 && (e.Args[0] != "--install-bridge" || e.Args[2] != "--loader")))
                    throw new ArgumentException("Usage: --install-bridge <game-folder> [--loader <extracted-folder>] or --uninstall-bridge <game-folder>");
                var installation = new BatomonHelper.Core.GameInstallation(AppContext.BaseDirectory);
                if (e.Args[0] == "--uninstall-bridge") installation.Uninstall(e.Args[1]);
                else installation.Install(e.Args[1], e.Args.Length == 4 ? e.Args[3] : null);
                Shutdown(0);
            }
            catch (Exception error)
            {
                MessageBox.Show(error.Message, "Batomon Helper mod setup", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
            return;
        }
        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }
}
