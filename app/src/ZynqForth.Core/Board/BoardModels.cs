using System.Globalization;

namespace ZynqForth.Board;

/// <summary>Which board answers: the Arty Z7-20 (ZYNQ-FORTH) or the Trenz MAX1000 (MAX1000-FORTH).</summary>
public enum BoardKind { Zynq, Max1000 }

/// <summary>
/// The <c>version</c> answer. <paramref name="Name"/> is the firmware's name from the
/// <c>firmware</c> line: <c>zynq-forth</c> or <c>max1000-forth</c>.
/// </summary>
public sealed record BoardVersion(string Firmware, int Protocol, int KernelWords, ushort KernelChecksum,
                                  string Name = "zynq-forth")
{
    public BoardKind Kind => Name == "max1000-forth" ? BoardKind.Max1000 : BoardKind.Zynq;

    /// <summary>"ZYNQ-FORTH" or "MAX1000-FORTH", for messages.</summary>
    public string Product => Kind == BoardKind.Max1000 ? "MAX1000-FORTH" : "ZYNQ-FORTH";

    public static BoardVersion Parse(string text)
    {
        Dictionary<string, string[]> lines = Lines(text);
        string[] firmware = lines.GetValueOrDefault("firmware", ["?", "?"]);
        string[] kernel = lines.GetValueOrDefault("kernel", ["0", "0"]);
        return new BoardVersion(
            firmware.Length > 1 ? firmware[1] : "?",
            int.TryParse(lines.GetValueOrDefault("protocol", ["0"])[0], out int protocol) ? protocol : 0,
            int.TryParse(kernel[0], out int words) ? words : 0,
            kernel.Length > 1 && ushort.TryParse(kernel[1], NumberStyles.HexNumber, null, out ushort sum) ? sum : (ushort)0,
            firmware.Length > 0 ? firmware[0] : "?");
    }

    internal static Dictionary<string, string[]> Lines(string text)
    {
        var lines = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] words = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 0)
            {
                lines[words[0]] = words[1..];
            }
        }

        return lines;
    }
}

public enum SourceState { None, Stored, Damaged }

/// <summary>The <c>status</c> answer.</summary>
public sealed record BoardStatus(
    bool Running, int ProgramCounter, int InputPending, int Starts,
    SourceState Source, int SourceLength, uint SourceCrc, bool Autostart, string LastStart)
{
    public static BoardStatus Parse(string text)
    {
        Dictionary<string, string[]> lines = BoardVersion.Lines(text);
        string Value(string key) => lines.TryGetValue(key, out string[]? v) && v.Length > 0 ? v[0] : "";
        int Number(string key) => int.TryParse(Value(key), out int n) ? n : 0;

        return new BoardStatus(
            Value("state") == "running",
            int.TryParse(Value("pc"), NumberStyles.HexNumber, null, out int pc) ? pc : 0,
            Number("input"),
            Number("starts"),
            Value("source") switch { "stored" => SourceState.Stored, "damaged" => SourceState.Damaged, _ => SourceState.None },
            Number("length"),
            uint.TryParse(Value("crc"), NumberStyles.HexNumber, null, out uint crc) ? crc : 0,
            Value("autostart") == "on",
            Value("last"));
    }
}

/// <summary>One dictionary entry: code address, flags and name.</summary>
public sealed record ForthWord(int CodeAddress, bool Immediate, bool Hidden, bool Inline, string Name)
{
    public string Flags => (Immediate ? "immediate " : "") + (Inline ? "inline " : "") + (Hidden ? "hidden" : "");
}

/// <summary>The <c>words</c> answer: HERE and the entries, newest first.</summary>
public sealed record WordList(int Here, IReadOnlyList<ForthWord> Words)
{
    public static WordList Parse(string text)
    {
        int here = 0;
        var words = new List<ForthWord>();
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split(' ', 3);
            if (parts is ["here", var value])
            {
                here = int.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
            else if (parts.Length == 3 && int.TryParse(parts[0], NumberStyles.HexNumber, null, out int address))
            {
                string flags = parts[1];
                words.Add(new ForthWord(address, flags.Contains('i'), flags.Contains('h'), flags.Contains('n'), parts[2]));
            }
        }

        return new WordList(here, words);
    }
}

/// <summary>The <c>stack</c> answer: the data stack at the last prompt, bottom first.</summary>
public sealed record DataStack(IReadOnlyList<ushort> Items)
{
    public static DataStack Parse(string text)
    {
        string[] words = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int depth = words.Length > 0 && int.TryParse(words[0], out int d) ? d : 0;
        var items = new List<ushort>(depth);
        for (int i = 1; i <= depth && i < words.Length; i++)
        {
            items.Add(ushort.Parse(words[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }

        return new DataStack(items);
    }
}
