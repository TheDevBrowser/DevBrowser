using System.Reflection;
using System.Text.RegularExpressions;
using Sentry;
using Serilog;

namespace DeveloperBrowser.App.Diagnostics;

public sealed partial class CrashReportingService : IDisposable
{
    private const string Dsn = "https://9b12c0b0e3df0d648546ce7e37721a4d@o4512040643330048.ingest.de.sentry.io/4512040971599952";
    private readonly DiagnosticSettings _settings;
    private IDisposable? _sentry;

    public CrashReportingService(DiagnosticSettings settings)
    {
        _settings = settings;
        if (_settings.CrashReportingEnabled == true) Enable();
    }

    public bool IsEnabled => _sentry is not null;
    public bool HasRecordedChoice => _settings.CrashReportingEnabled.HasValue;
    public bool? RecordedChoice => _settings.CrashReportingEnabled;

    public void SetEnabled(bool enabled)
    {
        _settings.SetCrashReporting(enabled);
        if (enabled) Enable();
        else Disable();
    }

    public void Capture(Exception exception, string feature)
    {
        if (!IsEnabled) return;

        using var scope = SentrySdk.PushScope();
        SentrySdk.ConfigureScope(current =>
        {
            current.SetTag("devbrowser.error_feature", SafeTag(feature));
            current.SetTag("devbrowser.exception_type", exception.GetType().FullName ?? exception.GetType().Name);
        });
        var eventId = SentrySdk.CaptureMessage(BuildSanitizedReport(exception), SentryLevel.Error);
        Log.Information("Submitted anonymous crash report {SentryEventId} for {ExceptionType}.", eventId, exception.GetType().Name);
    }

    public void Flush(TimeSpan timeout)
    {
        if (!IsEnabled) return;
        SentrySdk.FlushAsync(timeout).GetAwaiter().GetResult();
    }

    private void Enable()
    {
        if (_sentry is not null) return;

        var version = typeof(CrashReportingService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion?.Split('+')[0] ?? "development";

        _sentry = SentrySdk.Init(options =>
        {
            options.Dsn = Dsn;
            options.Release = $"devbrowser@{version}";
            options.Environment = version.Contains("dev", StringComparison.OrdinalIgnoreCase) ? "development" : "production";
            options.SendDefaultPii = false;
            options.IsGlobalModeEnabled = true;
            options.AutoSessionTracking = false;
            options.TracesSampleRate = 0;
            options.AttachStacktrace = false;
            options.MaxBreadcrumbs = 0;
        });
        Log.Information("Anonymous diagnostics were enabled by the user.");
        CaptureFirstLaunch();
    }

    private void Disable()
    {
        _sentry?.Dispose();
        _sentry = null;
        Log.Information("Anonymous diagnostics were disabled by the user.");
    }

    private void CaptureFirstLaunch()
    {
        if (_settings.FirstLaunchReported) return;

        using var scope = SentrySdk.PushScope();
        SentrySdk.ConfigureScope(current => current.SetTag("devbrowser.event", "first_launch"));
        var eventId = SentrySdk.CaptureMessage("devbrowser.first_launch", SentryLevel.Info);
        _settings.MarkFirstLaunchReported();
        Log.Information("Submitted anonymous first-launch diagnostic {SentryEventId}.", eventId);
    }

    private static string BuildSanitizedReport(Exception exception)
    {
        var stack = exception.StackTrace ?? "No managed stack trace was available.";
        stack = UrlPattern().Replace(stack, "[url removed]");
        stack = UserProfilePattern().Replace(stack, @"C:\Users\[user]");
        stack = SecretPattern().Replace(stack, "$1=[secret removed]");
        return $"{exception.GetType().FullName}\n{stack}";
    }

    private static string SafeTag(string value)
    {
        var sanitized = Regex.Replace(value, "[^a-zA-Z0-9_.-]", "_");
        return sanitized[..Math.Min(sanitized.Length, 80)];
    }

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"[A-Za-z]:\\Users\\[^\\\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex UserProfilePattern();

    [GeneratedRegex(@"(?i)\b(token|password|secret|api[_-]?key|authorization)\s*[=:]\s*\S+")]
    private static partial Regex SecretPattern();

    public void Dispose() => Disable();
}
