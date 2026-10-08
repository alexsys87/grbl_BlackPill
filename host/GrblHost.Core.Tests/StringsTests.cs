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
    public void EveryKeyTheProgramUsesExists()
    {
        var en = Load("Strings.en.xaml");
        string app = Path.GetDirectoryName(ResourcesDir())!;
        // {DynamicResource S.Key} in XAML, Loc.T("S.Key") / Loc.F("S.Key", …) in code;
        // keys put together at run time ("S.Conn." + msg) end with a dot and are skipped.
        var xaml = new System.Text.RegularExpressions.Regex(@"DynamicResource (S\.[A-Za-z0-9.]+)\}");
        var code = new System.Text.RegularExpressions.Regex(@"Loc\.[TF]\(""(S\.[A-Za-z0-9.]*[A-Za-z0-9])""");
        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(app, "*.*", SearchOption.AllDirectories))
        {
            string sep = Path.DirectorySeparatorChar.ToString();
            if (file.Contains(sep + "obj" + sep) || file.Contains(sep + "bin" + sep))
                continue;
            var re = file.EndsWith(".xaml") ? xaml : file.EndsWith(".cs") ? code : null;
            if (re == null)
                continue;
            foreach (System.Text.RegularExpressions.Match m in re.Matches(File.ReadAllText(file)))
                if (!en.ContainsKey(m.Groups[1].Value))
                    missing.Add(Path.GetFileName(file) + ": " + m.Groups[1].Value);
        }
        Assert.Empty(missing);
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
