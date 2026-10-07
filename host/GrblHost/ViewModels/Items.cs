using GrblHost.Infrastructure;

namespace GrblHost.ViewModels;

public enum LogKind
{
    Sent,
    Received,
    Info,
    Warning,
    Error,
}

public sealed class LogEntry
{
    public LogEntry(LogKind kind, string text)
    {
        Kind = kind;
        Text = text;
        Time = DateTime.Now.ToString("HH:mm:ss");
    }

    public LogKind Kind { get; }
    public string Text { get; }
    public string Time { get; }
    public string Prefix => Kind switch
    {
        LogKind.Sent => ">>",
        LogKind.Received => "<<",
        LogKind.Error => "!!",
        LogKind.Warning => "!",
        _ => "--",
    };
}

/// <summary>A line of the G-code listing.</summary>
public sealed class GCodeLineItem
{
    public GCodeLineItem(int index, string text)
    {
        Index = index;
        Text = text;
    }

    public int Index { get; }
    public int Number => Index + 1;
    public string Text { get; }
    public bool IsComment => Text.TrimStart() is var t && (t.StartsWith(';') || t.StartsWith('(') && t.EndsWith(')'));
}

/// <summary>A grbl setting ($n=value) in the settings editor.</summary>
public sealed class SettingItem : ObservableObject
{
    private string _value;
    private string _original;

    public SettingItem(int id, string value)
    {
        Id = id;
        _value = value;
        _original = value;
    }

    public int Id { get; }
    public string Code => "$" + Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string Name => GrblHost.Core.Machine.GrblCodes.SettingName(Id, Services.Loc.Language == Services.Loc.Russian);
    public string Unit => GrblHost.Core.Machine.GrblCodes.SettingUnit(Id);

    public string Value
    {
        get => _value;
        set
        {
            if (Set(ref _value, value.Trim()))
                OnPropertyChanged(nameof(IsChanged));
        }
    }

    public bool IsChanged => _value != _original;

    /// <summary>The controller reported this value.</summary>
    public void Reported(string value)
    {
        _original = value;
        _value = value;
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(IsChanged));
    }

    public void Refresh() => OnPropertyChanged(nameof(Name));
}

public sealed class MacroItem : ObservableObject
{
    private string _name;
    private string _script;

    public MacroItem(string name, string script)
    {
        _name = name;
        _script = script;
    }

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public string Script
    {
        get => _script;
        set => Set(ref _script, value);
    }
}

public enum ViewerMode
{
    /// <summary>The whole file, the depth slider picks the deepest level shown.</summary>
    Preview,
    /// <summary>Simulated job, time runs at the chosen speed.</summary>
    Simulation,
    /// <summary>Follows the real job.</summary>
    Live,
}
