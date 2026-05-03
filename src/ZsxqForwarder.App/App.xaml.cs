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
            e.Handled = true;
        };
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var settingsService = new SettingsService();
        settingsService.Load();

        var loginWindow = new LoginWindow();
        loginWindow.ShowDialog();

        if (loginWindow.LoginSucceeded)
        {
            var mainWindow = new MainWindow(loginWindow.AccessToken!, settingsService);
            mainWindow.Show();
        }
        else
        {
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
