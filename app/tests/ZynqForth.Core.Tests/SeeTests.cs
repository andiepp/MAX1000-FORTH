using System.Text.Json;
using ZynqForth.Board;

namespace ZynqForth.Tests;

/// <summary>
/// The decompiler against the real kernel: tools/make_see_fixture.py ran it in the simulator,
/// typed definitions in and saved the RAM, the <c>words</c> answer and the expected source.
/// </summary>
public class SeeTests
{
    private sealed record Fixture(WordList Words, byte[] Memory, (string Name, string Expect)[] Cases);

    private static readonly Lazy<Fixture> Data = new(() =>
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "see.json")));
        JsonElement root = doc.RootElement;
        return new Fixture(WordList.Parse(root.GetProperty("words").GetString()!),
                           Convert.FromHexString(root.GetProperty("memory").GetString()!),
                           [.. root.GetProperty("cases").EnumerateArray()
                               .Select(c => (c.GetProperty("name").GetString()!, c.GetProperty("expect").GetString()!))]);
    });

    private static Decompiled See(string name)
    {
        Fixture f = Data.Value;
        ForthWord word = f.Words.Words.First(w => w.Name == name);
        int end = Decompiler.CodeEnd(f.Words, word);
        var code = new List<ushort>();
        for (int a = word.CodeAddress; a < end; a += 2)
        {
            code.Add((ushort)(f.Memory[a] | f.Memory[a + 1] << 8));
        }

        return Decompiler.Decompile(f.Words, word, code);
    }

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach ((string name, string expect) in Data.Value.Cases)
        {
            data.Add(name, expect);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void DecompilesToItsSource(string name, string expect)
    {
        string source = See(name).Source;
        string flat = string.Join(' ', source.Split((char[])['\n', ' '], StringSplitOptions.RemoveEmptyEntries));
        string want = string.Join(' ', expect.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.True(flat.StartsWith(want, StringComparison.Ordinal), $"{name}:\n{source}\nexpected:\n{expect}");
    }

    [Fact]
    public void EveryKernelWordDecompiles()
    {
        var odd = new List<string>();
        foreach (ForthWord word in Data.Value.Words.Words)
        {
            Decompiled d = See(word.Name);
            if (d.Source.Contains("[alu") || d.Source.Contains("[0branch") || d.Source.Contains("[call"))
            {
                odd.Add(d.Source);
            }
        }

        Assert.True(odd.Count == 0, string.Join("\n\n", odd));
    }

    [Fact]
    public void ListingShowsEveryCell()
    {
        Decompiled d = See("sq");
        Assert.Equal(3, d.Listing.Trim().Split('\n').Length);
        Assert.Contains("exit", d.Listing);
    }
}
