using System.Globalization;

namespace GrblHost.Core.GCode;

/// <summary>
/// One block (line) of CNC G-code as grbl reads it: any number of G and M
/// words ("G21 G90 G0 X1 Y2") plus value words. Comments in parentheses
/// and after ';' are dropped, the first comment text is kept.
/// </summary>
public sealed class GCodeBlock
{
    private readonly double[] _values = new double[26];
    private uint _seen;

    /// <summary>G words as numbers: 1, 38.2, 90.1 ...</summary>
    public List<double> G { get; } = new(2);

    /// <summary>M words (M3, M8 ...).</summary>
    public List<int> M { get; } = new(1);

    /// <summary>First comment, without the brackets / ';'. Null if none.</summary>
    public string? Comment { get; private set; }

    /// <summary>A word letter appeared twice (grbl error 25).</summary>
    public bool RepeatedWord { get; private set; }

    /// <summary>Text that isn't G-code (letter without a number, junk).</summary>
    public bool BadFormat { get; private set; }

    public bool IsEmpty => G.Count == 0 && M.Count == 0 && _seen == 0;

    public bool Has(char letter) => (_seen & Bit(letter)) != 0;

    public double Get(char letter, double fallback = 0) =>
        Has(letter) ? _values[char.ToUpperInvariant(letter) - 'A'] : fallback;

    public bool TryGet(char letter, out double value)
    {
        if (Has(letter))
        {
            value = _values[char.ToUpperInvariant(letter) - 'A'];
            return true;
        }
        value = 0;
        return false;
    }

    public bool HasG(double code) => G.Exists(g => Math.Abs(g - code) < 1e-6);

    public bool HasM(int code) => M.Contains(code);

    /// <summary>Any of X Y Z (A B C ignored on a 3 axis machine).</summary>
    public bool HasAxisWords => Has('X') || Has('Y') || Has('Z');

    private static uint Bit(char letter)
    {
        int i = char.ToUpperInvariant(letter) - 'A';
        return i is >= 0 and < 26 ? 1u << i : 0;
    }

    public static GCodeBlock Parse(string line)
    {
        var b = new GCodeBlock();
        int i = 0, n = line.Length;
        while (i < n)
        {
            char c = line[i];
            if (c == ';')
            {
                b.Comment ??= line[(i + 1)..].Trim();
                break;
            }
            if (c == '(')
            {
                int end = line.IndexOf(')', i + 1);
                string text = end < 0 ? line[(i + 1)..] : line[(i + 1)..end];
                b.Comment ??= text.Trim();
                i = end < 0 ? n : end + 1;
                continue;
            }
            if (char.IsWhiteSpace(c) || c == '%')
            {
                i++;
                continue;
            }
            char letter = char.ToUpperInvariant(c);
            if (letter < 'A' || letter > 'Z')
            {
                b.BadFormat = true;
                i++;
                continue;
            }
            i++;
            while (i < n && line[i] == ' ')
                i++;
            int start = i;
            while (i < n && (char.IsDigit(line[i]) || line[i] is '.' or '-' or '+' || line[i] == ' ' &&
                             i + 1 < n && (char.IsDigit(line[i + 1]) || line[i + 1] == '.')))
                i++;
            var number = line.AsSpan(start, i - start).ToString().Replace(" ", "");
            if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            {
                b.BadFormat = true;
                continue;
            }
            switch (letter)
            {
                case 'G':
                    b.G.Add(Math.Round(v, 1));
                    break;
                case 'M':
                    b.M.Add((int)v);
                    break;
                case 'N':
                    break;                          // Line number.
                default:
                    if (b.Has(letter))
                        b.RepeatedWord = true;
                    b._values[letter - 'A'] = v;
                    b._seen |= Bit(letter);
                    break;
            }
        }
        return b;
    }
}
