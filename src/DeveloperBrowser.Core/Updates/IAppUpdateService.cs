namespace DeveloperBrowser.Core.Updates;

public enum AppUpdateState
{
    Unsupported,
    Checking,
    Current,
    Available,
    Applying,
    Failed
}

public sealed record AppUpdateStatus(AppUpdateState State, string Message, Exception? Error = null)
{
    public bool IsAvailable => State == AppUpdateState.Available;
}

public interface IAppUpdateService
{
    AppUpdateStatus Status { get; }
    event EventHandler<AppUpdateStatus>? StatusChanged;

    Task CheckForUpdatesAsync(CancellationToken cancellationToken = default);
    Task RelaunchToUpdateAsync(CancellationToken cancellationToken = default);
}
