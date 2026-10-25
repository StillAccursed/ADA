using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Overlord.Platform.Storage;
using Overlord.ViewModels;
using Overlord.Views;
using Serilog;
using Serilog.Events;

namespace Overlord;

public partial class App : Application
{
    private ServiceProvider? _services;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Show the window at once; folders, the database and services are set up behind it.
            var window = new MainWindow();
            desktop.MainWindow = window;
            desktop.Exit += (_, _) =>
            {
                _services?.Dispose();
                Log.CloseAndFlush();
            };
            LogUnhandledExceptions();
            _ = StartAsync(window, desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartAsync(MainWindow window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            var main = await Task.Run(() =>
            {
                var paths = AppPaths.ForCurrentOS().EnsureCreated();
                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Information()
                    // IHttpClientFactory logs every request; only failures are worth keeping.
                    .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
                    .WriteTo.File(Path.Combine(paths.Logs, "overlord-.log"),
                        rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
                    .CreateLogger();
                Log.Information("Overlord starting on {OS}", Environment.OSVersion);

                _services = Services.Build(paths);
                return _services.GetRequiredService<MainViewModel>();
            });

            window.DataContext = main;
            main.Start();
        }
        catch (Exception e)
        {
            Log.Fatal(e, "Overlord failed to start");
            await Log.CloseAndFlushAsync();
            desktop.Shutdown(1);
        }
    }

    private static void LogUnhandledExceptions()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
        Dispatcher.UIThread.UnhandledException += (_, e) =>
            Log.Error(e.Exception, "Unhandled exception on the UI thread");
    }
}
