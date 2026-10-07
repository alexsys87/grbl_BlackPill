using System.Xml.Linq;

namespace GrblHost.Core.Tests;

/// <summary>The Russian and English string dictionaries of the UI have the same keys.</summary>
public class StringsTests
{
    private static string ResourcesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "GrblHost.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "GrblHost", "Resources");
    }

    private static Dictionary<string, string> Load(string file)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return XDocument.Load(Path.Combine(ResourcesDir(), file)).Root!.Elements()
            .ToDictionary(e => (string)e.Attribute(x + "Key")!, e => e.Value);
    }

    [Fact]
    public void RussianAndEnglishHaveTheSameKeys()
    {
        var ru = Load("Strings.ru.xaml");
        var en = Load("Strings.en.xaml");
        Assert.True(ru.Count > 200);
        Assert.Empty(ru.Keys.Except(en.Keys));
        Assert.Empty(en.Keys.Except(ru.Keys));
    }

    [Fact]
    public void FormatStringsTakeTheSameArguments()
    {
        var ru = Load("Strings.ru.xaml");
        var en = Load("Strings.en.xaml");
        var placeholder = new System.Text.RegularExpressions.Regex(@"\{(\d+)");
        foreach (var (key, text) in ru)
        {
            var a = placeholder.Matches(text).Select(m => m.Groups[1].Value).Distinct().Order();
            var b = placeholder.Matches(en[key]).Select(m => m.Groups[1].Value).Distinct().Order();
            Assert.True(a.SequenceEqual(b), $"{key}: {{…}} differ");
        }
    }

    [Fact]
    public void EveryEnumValueHasAText()
    {
        var en = Load("Strings.en.xaml");
        foreach (var m in Enum.GetNames<Machine.ConnectionMessage>())
            Assert.True(en.ContainsKey("S.Conn." + m), m);
        foreach (var m in Enum.GetNames<Machine.MachineState>())
            Assert.True(en.ContainsKey("S.Machine." + m), m);
        foreach (var r in Enum.GetNames<Machine.JobResult>())
            Assert.True(en.ContainsKey("S.Result." + r), r);
        foreach (var f in Enum.GetNames<GCode.FeatureType>())
            Assert.True(en.ContainsKey("S.Feature." + f), f);
    }

    [Fact]
    public void EveryErrorAndAlarmHasATextInBothLanguages()
    {
        // The fallback for an unknown code is "Error n" / "Ошибка n".
        for (int e = 1; e <= 38; e++)
        {
            Assert.NotEqual($"Error {e}", Machine.GrblCodes.Error(e, false));
            Assert.NotEqual($"Ошибка {e}", Machine.GrblCodes.Error(e, true));
        }
        for (int a = 1; a <= 14; a++)
        {
            Assert.NotEqual($"Alarm {a}", Machine.GrblCodes.Alarm(a, false));
            Assert.NotEqual($"Тревога {a}", Machine.GrblCodes.Alarm(a, true));
        }
    }
}
