using System.Runtime.InteropServices;
using DeveloperBrowser.Core.Updates;
using Serilog;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace DeveloperBrowser.App.Updates;

public sealed class AppInstallerUpdateService : IAppUpdateService
{
    private static readonly Uri AppInstallerUri = new("https://github.com/RezaHoque/DevBrowser/releases/latest/download/DevBrowser.appinstaller");
    private readonly ILogger _logger = Log.ForContext<AppInstallerUpdateService>();
    private AppUpdateStatus _status = new(AppUpdateState.Unsupported, "Updates are available in installed releases.");

    public AppUpdateStatus Status => _status;
    public event EventHandler<AppUpdateStatus>? StatusChanged;

    public async Task CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        if (!TryGetCurrentPackage(out var package))
        {
            Publish(new AppUpdateStatus(AppUpdateState.Unsupported, "Updates are available in installed releases."));
            return;
        }

        try
        {
            Publish(new AppUpdateStatus(AppUpdateState.Checking, "Checking for DevBrowser updates…"));
            var result = await package.CheckUpdateAvailabilityAsync();
            cancellationToken.ThrowIfCancellationRequested();
            switch (result.Availability)
            {
                case PackageUpdateAvailability.Available:
                case PackageUpdateAvailability.Required:
                    Publish(new AppUpdateStatus(AppUpdateState.Available, "A DevBrowser update is available."));
                    break;
                case PackageUpdateAvailability.NoUpdates:
                    Publish(new AppUpdateStatus(AppUpdateState.Current, "DevBrowser is up to date."));
                    break;
                default:
                    _logger.Information("DevBrowser update availability was {Availability}.", result.Availability);
                    Publish(new AppUpdateStatus(AppUpdateState.Current, "No update information is available."));
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.Warning(exception, "DevBrowser update check failed.");
            Publish(new AppUpdateStatus(AppUpdateState.Failed, "Could not check for updates.", exception));
        }
    }

    public async Task RelaunchToUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (!TryGetCurrentPackage(out var package))
        {
            Publish(new AppUpdateStatus(AppUpdateState.Unsupported, "Updates are available in installed releases."));
            return;
        }

        try
        {
            Publish(new AppUpdateStatus(AppUpdateState.Applying, "Downloading update and restarting DevBrowser…"));
            var restartResult = RegisterApplicationRestart(null, RestartFlags.None);
            if (restartResult != 0) _logger.Warning("RegisterApplicationRestart returned {Result}.", restartResult);

            var packageManager = new PackageManager();
            await packageManager.AddPackageByAppInstallerFileAsync(
                AppInstallerUri,
                AddPackageByAppInstallerOptions.ForceTargetAppShutdown,
                packageManager.GetDefaultPackageVolume());

            cancellationToken.ThrowIfCancellationRequested();
            _logger.Information("DevBrowser update was applied for package {PackageFullName}.", package.Id.FullName);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.Warning(exception, "DevBrowser update install failed.");
            Publish(new AppUpdateStatus(AppUpdateState.Failed, "Could not install the update. Try again later.", exception));
        }
    }

    private bool TryGetCurrentPackage(out Package package)
    {
        try
        {
            package = Package.Current;
            return package is not null && !string.IsNullOrWhiteSpace(package.Id.FullName);
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException)
        {
            _logger.Debug("DevBrowser is running without package identity.");
            package = null!;
            return false;
        }
    }

    private void Publish(AppUpdateStatus status)
    {
        _status = status;
        StatusChanged?.Invoke(this, status);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterApplicationRestart(string? commandLine, RestartFlags flags);

    [Flags]
    private enum RestartFlags
    {
        None = 0
    }
}
