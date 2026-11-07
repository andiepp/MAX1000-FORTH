using ZynqForth.Board;

namespace ZynqForth.Tests;

/// <summary>The word help built from the manual (app/src/ZynqForth/Help/words.json) and its search.</summary>
public class HelpTests
{
    private static readonly Lazy<WordHelp> Help = new(() =>
        WordHelp.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Help", "words.json"))));

    [Fact]
    public void EveryKernelWordHasHelp()
    {
        // the decompiler's test data holds the kernel's dictionary
        string words = System.Text.Json.JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "see.json"))).RootElement.GetProperty("words").GetString()!;
        WordList list = WordList.Parse(words);
        string[] typed = ["sq", "tst", "cnt", "cnt2", "wt", "rp", "fv", "st", "early", "big", "tk", "nst", "lp", "rs",
                          "vv", "cc", "ans", "imm"];
        string[] missing = [.. list.Words.Select(w => w.Name).Where(n => !typed.Contains(n) && Help.Value.Find(n) is null)];
        Assert.True(missing.Length == 0, string.Join(" ", missing));
    }

    [Fact]
    public void EntriesCarryTheManualsText()
    {
        HelpEntry e = Help.Value.Find("uart-open")!;
        Assert.Equal("( ud-baud tx rx u -- )", e.Stack);
        Assert.StartsWith("5.15", e.Section);
        Assert.Contains("baud rate", e.Text);
        Assert.Contains("uart-close", e.Related);
        Assert.Contains(e.Examples, x => x.Contains("uart-open"));
        Assert.NotNull(Help.Value.Find("DUP"));          // names are case-insensitive, as in Forth
    }

    [Fact]
    public void NameMatchesComeFirst()
    {
        IReadOnlyList<HelpEntry> hits = Help.Value.Search("dup");
        Assert.Equal("dup", hits[0].Name);
        Assert.Contains(hits, h => h.Name == "?dup");
        Assert.Contains(hits, h => h.Name == "2dup");
        Assert.True(hits.ToList().FindIndex(h => h.Name == "2dup") < hits.ToList().FindIndex(h => !h.Name.Contains("dup")) ||
                    hits.All(h => h.Name.Contains("dup")));
    }

    [Fact]
    public void DescriptionsAreSearchedToo()
    {
        Assert.Contains(Help.Value.Search("baud"), h => h.Name == "uart-open");
        Assert.Contains(Help.Value.Search("negative"), h => h.Name == "negate" || h.Text.Contains("negative"));
        Assert.Equal("uart-open", Help.Value.Search("uart baud")[0].Name);     // all terms must match
        Assert.Empty(Help.Value.Search("uart xyzzy"));
    }

    [Fact]
    public void AreasFilterTheSearch()
    {
        string serial = Help.Value.Areas.First(a => a.StartsWith("Serial interfaces"));
        Assert.Equal(WordHelp.AllAreas, Help.Value.Areas[0]);
        Assert.True(Help.Value.Areas.ToList().IndexOf("Data stack") < Help.Value.Areas.ToList().IndexOf(serial));
        IReadOnlyList<HelpEntry> hits = Help.Value.Search("", serial);
        Assert.All(hits, h => Assert.Equal(serial, h.Area));
        Assert.Contains(hits, h => h.Name == "i2c-scan");
    }
}
