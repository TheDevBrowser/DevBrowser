using System.Windows;
using System.IO;
using DeveloperBrowser.Core.Bookmarks;
using DeveloperBrowser.Core.Collections;
using DeveloperBrowser.Core.History;
using DeveloperBrowser.Core.Updates;
using DeveloperBrowser.Infrastructure;
using DeveloperBrowser.App.Updates;
using DeveloperBrowser.App.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace DeveloperBrowser.App;

public partial class App : Application
{
    private ServiceProvider? _provider;
    private CrashReportingService? _crashReporting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevBrowser");
        ConfigureLocalLogging(dataDirectory);
        RegisterGlobalExceptionHandlers();

        try
        {
            var services = new ServiceCollection();
            services.AddDeveloperBrowserInfrastructure(dataDirectory);
            services.AddSingleton<PageMetadataService>();
            services.AddSingleton<IAppUpdateService, AppInstallerUpdateService>();
            services.AddSingleton(new DiagnosticSettings(dataDirectory));
            services.AddSingleton<CrashReportingService>();
            _provider = services.BuildServiceProvider();
            _crashReporting = _provider.GetRequiredService<CrashReportingService>();
            var bookmarks = _provider.GetRequiredService<IBookmarkService>();
            await bookmarks.InitializeAsync();
            var window = new MainWindow(bookmarks, _provider.GetRequiredService<IBrowsingHistoryService>(), _provider.GetRequiredService<PageMetadataService>(), _provider.GetRequiredService<ICollectionService>(), _provider.GetRequiredService<IEnvironmentService>(), _provider.GetRequiredService<IVariableResolver>(), _provider.GetRequiredService<ICollectionImportExportService>(), _provider.GetRequiredService<IAppUpdateService>(), _crashReporting, dataDirectory);
            MainWindow = window;
            window.Show();
            PromptForCrashReportingConsent(window, _crashReporting);
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "DevBrowser failed during startup.");
            _crashReporting?.Capture(exception, "startup");
            _crashReporting?.Flush(TimeSpan.FromSeconds(2));
            MessageBox.Show("DevBrowser could not start. Details were written to the local log.", "DevBrowser", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _crashReporting?.Flush(TimeSpan.FromSeconds(2));
        _provider?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private static void ConfigureLocalLogging(string dataDirectory)
    {
        var logDirectory = Path.Combine(dataDirectory, "Logs");
        Directory.CreateDirectory(logDirectory);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(logDirectory, "devbrowser-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14, fileSizeLimitBytes: 10 * 1024 * 1024, rollOnFileSizeLimit: true)
            .CreateLogger();
        Log.Information("DevBrowser is starting.");
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Fatal(args.Exception, "Unhandled dispatcher exception.");
            _crashReporting?.Capture(args.Exception, "dispatcher");
            _crashReporting?.Flush(TimeSpan.FromSeconds(2));
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is not Exception exception) return;
            Log.Fatal(exception, "Unhandled application exception.");
            _crashReporting?.Capture(exception, "app-domain");
            _crashReporting?.Flush(TimeSpan.FromSeconds(2));
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception.");
            _crashReporting?.Capture(args.Exception, "background-task");
            args.SetObserved();
        };
    }

    private static void PromptForCrashReportingConsent(Window owner, CrashReportingService crashReporting)
    {
        if (crashReporting.HasRecordedChoice) return;
        var dialog = new CrashReportingConsentDialog(null) { Owner = owner };
        if (dialog.ShowDialog() == true) crashReporting.SetEnabled(dialog.ReportingEnabled);
    }
}
