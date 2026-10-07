using System.IO;
using System.Text.Json;

namespace GrblHost.Services;

public sealed class MacroSettings
{
    public string Name { get; set; } = "";
    public string Script { get; set; } = "";
}

/// <summary>User settings, stored as JSON in %APPDATA%\GrblHost\settings.json.</summary>
public sealed class AppSettings
{
    public string? Port { get; set; }
    public int BaudRate { get; set; } = 115200;
    public double VirtualTimeScale { get; set; } = 10;
    /// <summary>Telnet / TCP serial bridge, "host" or "host:port".</summary>
    public string NetworkAddress { get; set; } = "192.168.1.100:23";
    public bool AutoReconnect { get; set; } = true;

    // Work area of the CNC 3018 ($130 / $131 / $132), replaced by the controller's values.
    public double TableWidth { get; set; } = 300;
    public double TableDepth { get; set; } = 180;
    public double TableHeight { get; set; } = 45;

    public double JogFeedXY { get; set; } = 1000;
    public double JogFeedZ { get; set; } = 300;

    public double SpindleRpm { get; set; } = 10000;
    /// <summary>Probe: touch plate thickness, search distance and feed.</summary>
    public double ProbePlate { get; set; } = 20;
    public double ProbeDistance { get; set; } = 30;
    public double ProbeFeed { get; set; } = 100;
    /// <summary>Safe Z for starting in the middle of a file, work coordinates.</summary>
    public double SafeZ { get; set; } = 5;
    /// <summary>Spindle spin-up before cutting when starting in the middle, s.</summary>
    public double SpindleDelay { get; set; } = 3;

    public bool ShowRapids { get; set; } = true;
    public bool HideStatusLines { get; set; } = true;
    public bool ShowJobLines { get; set; }
    public string? LastFolder { get; set; }

    /// <summary>"ru" or "en"; null: the system language.</summary>
    public string? Language { get; set; }
    public bool DarkTheme { get; set; } = true;

    /// <summary>Null until saved once: the program fills in defaults in the current language.</summary>
    public List<MacroSettings>? Macros { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GrblHost", "settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch
        {
            // Broken file: start with the defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // Settings are a convenience, never fail because of them.
        }
    }
}
