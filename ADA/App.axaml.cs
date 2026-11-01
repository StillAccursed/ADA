using System.Diagnostics;
using ADA.Infrastructure.Activity;
using ADA.Infrastructure.Backups;
using ADA.Infrastructure.Logging;
using ADA.Infrastructure.Settings;
using ADA.Platform.Storage;
using ADA.ViewModels;
using ADA.Views;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;

namespace ADA;

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
            // Scale and theme first so the first frame is already right; Load never throws.
            Appearance.Apply(new SettingsStore(AppPaths.ForCurrentOS().SettingsFile).Load());
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
                // Before any store reads Config, so restored files aren't overwritten by cached ones; and before the
                // logger, so a restored log limit applies this start.
                var restored = PendingAdaConfig.Apply(paths.PendingAdaConfig, paths.Config);
                var logLimit = new SettingsStore(paths.SettingsFile).Load().MaxLogFolderMegabytes * 1024L * 1024;
                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Information()
                    // IHttpClientFactory logs every request; only failures are worth keeping.
                    .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
                    // FILES_KEPT files of a seventh of the limit each can't exceed the limit; a full day stops logging.
                    .WriteTo.File(Path.Combine(paths.Logs, "ada-.log"),
                        rollingInterval: RollingInterval.Day, retainedFileCountLimit: LogFiles.FILES_KEPT,
                        fileSizeLimitBytes: LogFiles.FileLimitFor(logLimit), rollOnFileSizeLimit: false)
                    .CreateLogger();
                Log.Information("ADA starting on {OS}", Environment.OSVersion);
                if (restored > 0)
                {
                    Log.Information("Applied {Count} restored ADA config files", restored);
                }

                _services = Services.Build(paths);
                return _services.GetRequiredService<MainViewModel>();
            });

            var services = _services!;
            Appearance.Follow(services.GetRequiredService<SettingsStore>());
            services.GetRequiredService<BackupsViewModel>().RestartRequested += (_, _) => Restart(desktop);
            _ = services.GetRequiredService<ActivityLog>().TrimAsync();
            _ = services.GetRequiredService<LogFiles>().TrimToAsync(
                services.GetRequiredService<SettingsStore>().Current.MaxLogFolderMegabytes * 1024L * 1024);

            window.DataContext = main;
            main.Start();
        }
        catch (Exception e)
        {
            Log.Fatal(e, "ADA failed to start");
            await Log.CloseAndFlushAsync();
            desktop.Shutdown(1);
        }
    }

    /// <summary>
    /// Starts a new ADA with the same arguments, then closes this one; used after restoring ADA's own settings,
    /// which the new one applies at startup. Run through the <c>dotnet</c> host, the app's own <c>.dll</c> is
    /// the first argument and is passed on too.
    /// </summary>
    private static void Restart(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (Environment.ProcessPath is { } executable)
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false };
            var viaHost = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
            foreach (var argument in Environment.GetCommandLineArgs().Skip(viaHost ? 0 : 1))
            {
                start.ArgumentList.Add(argument);
            }
            Log.Information("Restarting ADA to apply restored settings");
            Process.Start(start);
        }
        desktop.Shutdown();
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
