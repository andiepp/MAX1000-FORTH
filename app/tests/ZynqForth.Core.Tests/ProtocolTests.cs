using System.Text;
using ZynqForth.Board;
using ZynqForth.Protocol;

namespace ZynqForth.Tests;

public class StreamSplitterTests
{
    private readonly StreamSplitter splitter = new();
    private readonly StringBuilder terminal = new();
    private readonly List<Answer> answers = [];
    private readonly List<string> garbled = [];

    public StreamSplitterTests()
    {
        splitter.TerminalBytes += b => terminal.Append(Encoding.Latin1.GetString(b.Span));
        splitter.AnswerReceived += answers.Add;
        splitter.Garbled += garbled.Add;
    }

    private void Feed(string text) => splitter.Feed(Encoding.Latin1.GetBytes(text));

    [Fact]
    public void PlainBytesAreTerminalOutput()
    {
        Feed("2 3 + . 5  ok\r\n");
        Assert.Equal("2 3 + . 5  ok\r\n", terminal.ToString());
        Assert.Empty(answers);
    }

    [Fact]
    public void AnswerBetweenTerminalBytesIsTakenOut()
    {
        Feed("ab\u0001ok echo 3\nxyzcd");
        Assert.Equal("abcd", terminal.ToString());
        Answer answer = Assert.Single(answers);
        Assert.True(answer.Ok);
        Assert.Equal("echo", answer.Name);
        Assert.Equal("xyz", answer.Text);
    }

    [Fact]
    public void AnswerSplitAcrossChunksIsReassembled()
    {
        string all = "x\u0001ok words 12\nhere 0F00\nab!y";
        foreach (char c in all)
        {
            Feed(c.ToString());
        }

        Assert.Equal("x!y", terminal.ToString());
        Assert.Equal("here 0F00\nab", Assert.Single(answers).Text);
    }

    [Fact]
    public void DoubledSohIsForthsOwnByte()
    {
        Feed("a\u0001\u0001b");
        Assert.Equal("a\u0001b", terminal.ToString());
        Assert.Empty(answers);
    }

    [Fact]
    public void RefusalCarriesTheReason()
    {
        Feed("\u0001err store crc\n");
        Answer answer = Assert.Single(answers);
        Assert.False(answer.Ok);
        Assert.Equal("store", answer.Name);
        Assert.Equal("crc", answer.Reason);
    }

    [Fact]
    public void EmptyPayloadCompletesAtTheHeader()
    {
        Feed("\u0001ok restart 0\n[zynq-forth] 1.0");
        Assert.Equal("restart", Assert.Single(answers).Name);
        Assert.Equal("[zynq-forth] 1.0", terminal.ToString());
    }

    [Fact]
    public void GarbledHeaderFallsBackToTheTerminal()
    {
        Feed("\u0001what\nok");
        Assert.Single(garbled);
        Assert.Equal("ok", terminal.ToString());
    }
}

public class ParserTests
{
    [Fact]
    public void Status()
    {
        BoardStatus status = BoardStatus.Parse(
            "state running\npc 01A2\ninput 12\nstarts 3\nsource stored\nlength 195\ncrc AA4BEAA3\nautostart on\nlast fed\n");
        Assert.True(status.Running);
        Assert.Equal(0x1A2, status.ProgramCounter);
        Assert.Equal(12, status.InputPending);
        Assert.Equal(SourceState.Stored, status.Source);
        Assert.Equal(195, status.SourceLength);
        Assert.Equal(0xAA4BEAA3u, status.SourceCrc);
        Assert.True(status.Autostart);
        Assert.Equal("fed", status.LastStart);
    }

    [Fact]
    public void Version()
    {
        BoardVersion version = BoardVersion.Parse("firmware zynq-forth 1.0\nprotocol 1\nkernel 2398 BEEF\n");
        Assert.Equal("1.0", version.Firmware);
        Assert.Equal(1, version.Protocol);
        Assert.Equal(2398, version.KernelWords);
        Assert.Equal(0xBEEF, version.KernelChecksum);
    }

    [Fact]
    public void Words()
    {
        WordList list = WordList.Parse("here 12F0\n1200 - sq\n0A3C i if\n0040 n dup\n");
        Assert.Equal(0x12F0, list.Here);
        Assert.Equal(["sq", "if", "dup"], list.Words.Select(w => w.Name));
        Assert.True(list.Words[1].Immediate);
        Assert.True(list.Words[2].Inline);
        Assert.Equal(0x1200, list.Words[0].CodeAddress);
    }

    [Fact]
    public void Stack()
    {
        DataStack stack = DataStack.Parse("3 0001 FFFE 012C\n");
        Assert.Equal([1, 0xFFFE, 300], stack.Items.Select(i => (int)i));
        Assert.Empty(DataStack.Parse("0\n").Items);
    }

    [Fact]
    public void Board()
    {
        // LD0+LD2, LD4 red + LD5 green (0x11), BTN1, SW0; pin 13 driven high, pin 2 low input;
        // PWM channel 1 on pin 27; temperature code 0x9C0 (~33.9 C), A0 code 0x800
        string xadc = string.Join(' ', Enumerable.Range(0, 32).Select(r => r switch
        {
            0 => "9C00",
            1 => "5550",
            0x11 => "8000",
            _ => "0000",
        }));
        uint pwm = 0x2u | (27u << 10);
        BoardState board = BoardState.Parse(
            $"board 00120115\nports 2000FFFB 08000000 00000000\npwm {pwm:X8}\nxadc {xadc}\n");

        Assert.Equal(0b0101, board.Leds);
        Assert.Equal(0x11, board.Rgb);
        Assert.Equal(0b0010, board.Buttons);
        Assert.Equal(0b01, board.Switches);

        ShieldPin io13 = board.Pins.Single(p => p.Number == 13);
        Assert.True(io13.Driven);
        Assert.True(io13.Level);
        Assert.False(board.Pins.Single(p => p.Number == 2).Level);
        Assert.Equal("A0", board.Pins.Single(p => p.Number == 14).Label);
        Assert.Equal(1, board.Pins.Single(p => p.Number == 27).PwmChannel);
        Assert.True(board.Pins.Single(p => p.Number == 27).Driven);
        Assert.Equal(36, board.Pins.Count);

        Assert.InRange(board.Temperature, 33.5, 34.5);
        Assert.InRange(board.VccInt, 0.99, 1.01);
        Assert.InRange(board.Analog(0), 1.65, 1.67);
    }

    [Fact]
    public void Crc32MatchesZlib() =>
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));

    [Fact]
    public void ForthTextUsesCrLineEnds()
    {
        Assert.Equal("a\rb\rc\r d"u8.ToArray(), BoardClient.ToForthText("a\r\nb\nc\r\td"));
        Assert.Equal("x?y"u8.ToArray(), BoardClient.ToForthText("xäy"));
        Assert.Equal($"a{Environment.NewLine}b{Environment.NewLine}", BoardClient.FromForthText("a\rb\r"u8.ToArray()));
    }
}
