using System.IO;
using System.Windows;
using Serilog;
using ZsxqForwarder.App.Views;
using ZsxqForwarder.Core.Services;

namespace ZsxqForwarder.App;

public partial class App : Application
{
    public static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ZsxqForwarder");

    public App()
    {
        // Use Windows native HTTP handler for proper SSL/TLS support
        AppContext.SetSwitch("System.Net.Http.UseSocketsHttpHandler", false);

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Directory.CreateDirectory(AppDataDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(AppDataDir, "logs", "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30)
            .WriteTo.Debug()
            .CreateLogger();

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception");

        DispatcherUnhandledException += (s, e) =>
        {
            Log.Error(e.Exception, "Dispatcher unhandled exception");
            MessageBox.Show($"发生错误: {e.Exception.Message}\n\n{e.Exception.StackTrace}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            Log.Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            var db = new DatabaseService();
            db.Init();

            var loginWindow = new LoginWindow();
            loginWindow.ShowDialog();

            Log.Information("Login finished. Succeeded={Succeeded}", loginWindow.LoginSucceeded);

            if (loginWindow.LoginSucceeded)
            {
                var mainWindow = new MainWindow(loginWindow.AccessToken!, db);
                mainWindow.Closed += (s, args) =>
                {
                    Log.Information("MainWindow closed, shutting down");
                    Shutdown();
                };
                mainWindow.Show();
                Log.Information("MainWindow shown successfully");
            }
            else
            {
                Shutdown();
            }
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Fatal error during startup");
            MessageBox.Show($"启动失败: {ex.Message}\n\n{ex.StackTrace}", "致命错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
