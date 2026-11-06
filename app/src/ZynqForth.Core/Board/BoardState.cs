using System.Globalization;

namespace ZynqForth.Board;

/// <summary>One shield pin as the <c>board</c> answer shows it.</summary>
public sealed record ShieldPin(int Number, string Label, bool Level, bool Driven, int? PwmChannel);

/// <summary>
/// The pattern generator in the <c>board</c> answer's <c>pg</c> line (firmware 2.2): its current
/// step, status bits, channel enable mask and output levels (docs/Protocol.md).
/// </summary>
public sealed record PatternStatus(int Step, int Flags, ushort Enabled, ushort Levels)
{
    public bool Running => (Flags & 0x01) != 0;

    public bool Waiting => (Flags & 0x02) != 0;

    public bool Ended => (Flags & 0x04) != 0;

    /// <summary>A step ran late: the generator could not fetch the next one in time.</summary>
    public bool Late => (Flags & 0x10) != 0;

    /// <summary>Loops were nested more than four deep.</summary>
    public bool TooDeep => (Flags & 0x20) != 0;

    /// <summary>"stopped", "running · step 12", "waiting for the trigger · step 0", "ended", plus flags.</summary>
    public string Summary
    {
        get
        {
            string state = Waiting ? $"waiting for the trigger · step {Step}"
                : Running ? $"running · step {Step}"
                : Ended ? "ended"
                : "stopped";
            return state + (Late ? " · late" : "") + (TooDeep ? " · loops too deep" : "");
        }
    }

    public static PatternStatus FromRegisters(uint status, uint output) =>
        new((int)((status >> 16) & 0xFFF), (int)(status & 0x7F), (ushort)(output >> 16), (ushort)output);
}

/// <summary>
/// The <c>board</c> answer decoded: LEDs, RGB LEDs, buttons, switches, shield pins, PWM
/// channels and the XADC (register layout in docs/design.md).
/// </summary>
public sealed class BoardState
{
    /// <summary>Shield pins in board-label order: IO0..IO13, A0..A5 (14..19), IO26..IO41.</summary>
    public static readonly IReadOnlyList<int> PinNumbers =
        [.. Enumerable.Range(0, 20), .. Enumerable.Range(26, 16)];

    private static readonly int[] AnalogRegisters = [0x11, 0x19, 0x16, 0x1F, 0x15, 0x1D, 0x1C, 0x10, 0x18];

    private readonly ushort[] xadc;

    private BoardState(uint board, uint[] ports, uint pwm, ushort[] xadc, PatternStatus? pattern)
    {
        this.xadc = xadc;
        Pattern = pattern;
        Leds = (int)(board & 0xF);
        Rgb = (int)((board >> 4) & 0x3F);
        Buttons = (int)((board >> 16) & 0xF);
        Switches = (int)((board >> 20) & 0x3);

        PwmEnabled = (int)(pwm & 0xF);
        var pwmPins = new int[4];
        for (int c = 0; c < 4; c++)
        {
            pwmPins[c] = (int)((pwm >> (4 + 6 * c)) & 0x3F);
        }

        PwmPins = pwmPins;
        Pins = [.. PinNumbers.Select(n =>
        {
            uint port = ports[n / 16];
            int bit = n % 16;
            int channel = Array.FindIndex(pwmPins, (p) => p == n);
            bool pwmOn = channel >= 0 && (PwmEnabled & (1 << channel)) != 0;
            return new ShieldPin(n, Label(n), ((port >> bit) & 1) != 0, ((port >> (16 + bit)) & 1) != 0,
                                 pwmOn ? channel : null);
        })];
    }

    /// <summary>LD0..LD3, bit n = LDn.</summary>
    public int Leds { get; }

    /// <summary>Bits 0/1/2 = LD4 red/green/blue, bits 3/4/5 = LD5 red/green/blue.</summary>
    public int Rgb { get; }

    /// <summary>BTN0..BTN3, debounced.</summary>
    public int Buttons { get; }

    public int Switches { get; }

    public int PwmEnabled { get; }

    public IReadOnlyList<int> PwmPins { get; }

    public IReadOnlyList<ShieldPin> Pins { get; }

    /// <summary>The pattern generator; null with firmware before 2.2.</summary>
    public PatternStatus? Pattern { get; }

    /// <summary>Chip temperature in °C.</summary>
    public double Temperature => Code(0) * 503.975 / 4096 - 273.15;

    public double VccInt => Code(1) * 3.0 / 4096;

    public double VccAux => Code(2) * 3.0 / 4096;

    public double VccBram => Code(6) * 3.0 / 4096;

    /// <summary>Analog channel 0..8 in volts: A0..A5 (0..3.32 V), then the pairs A6/A7, A8/A9, A10/A11 (0..1 V).</summary>
    public double Analog(int channel) =>
        Code(AnalogRegisters[channel]) * (channel < 6 ? 3.32 : 1.0) / 4096;

    public static string Label(int pin) => pin switch
    {
        < 14 => "IO" + pin,
        < 20 => "A" + (pin - 14),
        _ => "IO" + pin,
    };

    public static BoardState Parse(string text)
    {
        Dictionary<string, string[]> lines = BoardVersion.Lines(text);
        uint Hex(string s) => uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        uint board = Hex(lines["board"][0]);
        uint[] ports = [.. lines["ports"].Take(3).Select(Hex)];
        uint pwm = Hex(lines["pwm"][0]);
        ushort[] xadc = [.. lines["xadc"].Take(32).Select(s => (ushort)Hex(s))];
        if (ports.Length != 3 || xadc.Length != 32)
        {
            throw new FormatException("incomplete board answer");
        }

        PatternStatus? pattern = lines.TryGetValue("pg", out string[]? pg) && pg.Length >= 2
            ? PatternStatus.FromRegisters(Hex(pg[0]), Hex(pg[1]))
            : null;
        return new BoardState(board, ports, pwm, xadc, pattern);
    }

    /// <summary>The 12-bit result of an XADC status register.</summary>
    private int Code(int register) => xadc[register] >> 4;
}
