using System.Text;
using ZynqForth.Protocol;

namespace ZynqForth.Board;

/// <summary>A service request the board refused (<c>err &lt;name&gt; &lt;reason&gt;</c>).</summary>
public sealed class BoardRefusedException(string request, string reason)
    : Exception($"The board refused '{request}': {reason}.")
{
    public string Reason { get; } = reason;
}

/// <summary>The service requests of docs/Protocol.md as typed calls, plus Forth input.</summary>
public sealed class BoardClient(ForthLink link)
{
    /// <summary>The largest source the flash sector holds.</summary>
    public const int SourceMax = 65536 - 16;

    private static readonly TimeSpan FlashTimeout = TimeSpan.FromSeconds(30);

    public ForthLink Link { get; } = link ?? throw new ArgumentNullException(nameof(link));

    /// <summary>
    /// What may be queued in the board's input before waiting for Forth. The Zynq's ARM keeps
    /// a 64 KB ring; on the MAX1000 the kernel answers a request only after the input queued
    /// before it, so smaller pieces keep each status answer quick.
    /// </summary>
    public int InputWindow { get; set; } = 16 * 1024;

    /// <summary>
    /// Restart with Ctrl-R (and ESC to skip the saved source) instead of the restart request:
    /// the MAX1000 resets its CPU in hardware on Ctrl-R, even while Forth runs a tight loop.
    /// </summary>
    public bool RestartByCtrlR { get; set; }

    /// <summary>Lines as Forth expects them: LF, CR LF and CR all become CR; tabs become spaces.</summary>
    public static byte[] ToForthText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string normal = text.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r').Replace('\t', ' ');
        byte[] bytes = new byte[normal.Length];
        for (int i = 0; i < normal.Length; i++)
        {
            char c = normal[i];
            bytes[i] = c is '\r' or (>= ' ' and <= '~') ? (byte)c : (byte)'?';
        }

        return bytes;
    }

    /// <summary>The stored form back as editor text, CR line ends become CR LF.</summary>
    public static string FromForthText(byte[] bytes) =>
        Encoding.ASCII.GetString(bytes).Replace("\r\n", "\r", StringComparison.Ordinal)
                                        .Replace("\n", "\r", StringComparison.Ordinal)
                                        .Replace("\r", Environment.NewLine, StringComparison.Ordinal);

    public async Task<BoardVersion> VersionAsync() => BoardVersion.Parse(await TextAsync("version"));

    public async Task<BoardStatus> StatusAsync() => BoardStatus.Parse(await TextAsync("status"));

    public async Task<WordList> WordsAsync() => WordList.Parse(await TextAsync("words"));

    public async Task<DataStack> StackAsync() => DataStack.Parse(await TextAsync("stack"));

    public async Task<BoardState> BoardAsync() => BoardState.Parse(await TextAsync("board"));

    public async Task<bool> EchoAsync(string word) => await TextAsync("echo " + word) == word;

    public async Task<Max1000State> Max1000BoardAsync() => Max1000State.Parse(await TextAsync("board"));

    public Task RestartAsync(bool skipAutostart)
    {
        if (RestartByCtrlR)
        {
            byte[] keys = skipAutostart ? [0x12, 0x1B] : [0x12];
            Link.SendTerminal(keys);
            return Task.CompletedTask;
        }

        return TextAsync(skipAutostart ? "restart skip" : "restart");
    }

    /// <summary>The saved source, or null when none is stored.</summary>
    public async Task<string?> SourceAsync()
    {
        Answer answer = await Link.RequestAsync("source", timeout: TimeSpan.FromSeconds(15));
        if (!answer.Ok)
        {
            return answer.Reason == "none" ? null : throw new BoardRefusedException("source", answer.Reason);
        }

        return FromForthText(answer.Payload);
    }

    /// <summary>Saves the source in the board's flash (CR line ends, a final CR added).</summary>
    public async Task StoreAsync(string source, bool autostart)
    {
        byte[] text = ToForthText(source);
        if (text.Length > 0 && text[^1] != '\r')
        {
            text = [.. text, (byte)'\r'];
        }

        if (text.Length > SourceMax)
        {
            throw new ArgumentException($"The source has {text.Length} bytes; the board keeps at most {SourceMax}.");
        }

        await TextAsync($"store {text.Length} {Crc32.Compute(text):X8} {(autostart ? "on" : "off")}", text, FlashTimeout);
    }

    public Task EraseAsync() => TextAsync("erase", timeout: FlashTimeout);

    public Task AutostartAsync(bool on) => TextAsync(on ? "autostart on" : "autostart off", timeout: FlashTimeout);

    /// <summary>One terminal line; CR ends it.</summary>
    public void SendLine(string line) => Link.SendTerminal([.. ToForthText(line), (byte)'\r']);

    /// <summary>
    /// Feeds a whole text to Forth as if typed. Large texts go in pieces, each sent once the
    /// board's input ring has room, so nothing is lost while Forth compiles.
    /// </summary>
    public async Task SendTextAsync(string text, IProgress<double>? progress = null, CancellationToken cancellation = default)
    {
        byte[] bytes = ToForthText(text);
        if (bytes.Length > 0 && bytes[^1] != '\r')
        {
            bytes = [.. bytes, (byte)'\r'];
        }

        int sent = 0;
        while (sent < bytes.Length)
        {
            cancellation.ThrowIfCancellationRequested();
            BoardStatus status = await StatusAsync();
            int room = InputWindow - status.InputPending;
            if (room < 1024)
            {
                await Task.Delay(100, cancellation);
                continue;
            }

            int count = Math.Min(room, bytes.Length - sent);
            Link.SendTerminal(bytes.AsSpan(sent, count));
            sent += count;
            progress?.Report((double)sent / bytes.Length);
        }
    }

    private async Task<string> TextAsync(string request, byte[]? payload = null, TimeSpan? timeout = null)
    {
        Answer answer = await Link.RequestAsync(request, payload, timeout);
        return answer.Ok ? answer.Text : throw new BoardRefusedException(request.Split(' ')[0], answer.Reason);
    }
}

/// <summary>CRC-32 (IEEE, reflected, as zlib), the one the firmware checks.</summary>
public static class Crc32
{
    private static readonly uint[] Table = [.. Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++)
        {
            c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        }

        return c;
    })];

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }
}
