using System.Text.Json;
using System.IO;

namespace DeveloperBrowser.App.Diagnostics;

public sealed class DiagnosticSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _filePath;

    public DiagnosticSettings(string dataDirectory)
    {
        _filePath = Path.Combine(dataDirectory, "settings.json");
        Load();
    }

    public bool? CrashReportingEnabled { get; private set; }
    public bool FirstLaunchReported { get; private set; }

    public void SetCrashReporting(bool enabled)
    {
        CrashReportingEnabled = enabled;
        Save();
    }

    public void MarkFirstLaunchReported()
    {
        FirstLaunchReported = true;
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var document = JsonSerializer.Deserialize<SettingsDocument>(File.ReadAllText(_filePath), JsonOptions);
            CrashReportingEnabled = document?.CrashReportingEnabled;
            FirstLaunchReported = document?.FirstLaunchReported ?? false;
        }
        catch (Exception exception)
        {
            Serilog.Log.Warning(exception, "Could not read diagnostic settings.");
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(new SettingsDocument(CrashReportingEnabled, FirstLaunchReported), JsonOptions));
        }
        catch (Exception exception)
        {
            Serilog.Log.Warning(exception, "Could not save diagnostic settings.");
        }
    }

    private sealed record SettingsDocument(bool? CrashReportingEnabled, bool FirstLaunchReported = false);
}
