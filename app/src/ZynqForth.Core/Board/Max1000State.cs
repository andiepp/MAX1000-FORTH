using System.Globalization;

namespace ZynqForth.Board;

/// <summary>
/// The MAX1000-FORTH <c>board</c> answer decoded (Trenz MAX1000, see MAX1000-FORTH's
/// docs/design.md): the 8 user LEDs, the user button, the digital pins D0..D14 and
/// PIO_01..PIO_08, the PWM channels and the 9 ADC channels.
/// </summary>
/// <remarks>
/// <code>
/// board 000100A5                 bits 7..0 = LED1..LED8, bit 16 = user button pressed
/// ports 00280048 000000FF        pins 0-15, 16-31: bits 15..0 levels, 31..16 driven
/// pwm 00000051                   as ZYNQ-FORTH: bits 3..0 enabled, 6 bits per channel's pin
/// adc 0000 0800 ...              12-bit codes of ADC channels 0..8 (3.3 V full scale)
/// temp 0E3C                      temperature diode code (MAX1000-FORTH 1.0 and later)
/// accel 001F FFF2 03F8           LIS3DH x y z in mg, 16-bit two's complement (same)
/// </code>
/// </remarks>
public sealed class Max1000State
{
    /// <summary>Pin numbers as in Forth: 0..14 = D0..D14, 16..23 = PIO_01..PIO_08.</summary>
    public static readonly IReadOnlyList<int> PinNumbers = [.. Enumerable.Range(0, 15), .. Enumerable.Range(16, 8)];

    /// <summary>The ADC channel of each header input: AIN0..AIN7, then the dedicated AIN.</summary>
    private static readonly int[] InputChannels = [8, 2, 5, 1, 3, 4, 7, 6, 0];

    private readonly ushort[] adc;

    private Max1000State(uint board, uint[] ports, uint pwm, ushort[] adc, int? tempCode, (int X, int Y, int Z)? accel)
    {
        this.adc = adc;
        TempCode = tempCode;
        Accel = accel;
        Leds = (int)(board & 0xFF);
        Button = ((board >> 16) & 1) != 0;

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
            int channel = Array.FindIndex(pwmPins, p => p == n);
            bool pwmOn = channel >= 0 && (PwmEnabled & (1 << channel)) != 0;
            return new ShieldPin(n, Label(n), ((port >> bit) & 1) != 0, ((port >> (16 + bit)) & 1) != 0,
                                 pwmOn ? channel : null);
        })];
    }

    /// <summary>LED1..LED8, bit 0 = LED1.</summary>
    public int Leds { get; }

    /// <summary>The user button (beside Reset) is pressed.</summary>
    public bool Button { get; }

    public int PwmEnabled { get; }

    public IReadOnlyList<int> PwmPins { get; }

    public IReadOnlyList<ShieldPin> Pins { get; }

    /// <summary>The MAX 10's temperature diode code; null when the firmware does not send it.</summary>
    public int? TempCode { get; }

    /// <summary>
    /// Chip temperature in °C, as Forth's <c>temp@</c>: linear between Intel's table points
    /// −40 °C = 3798 and 96 °C = 3510, so approximate.
    /// </summary>
    public double? Temperature => TempCode is int c ? -40 + (3798 - c) * 136.0 / 288 : null;

    /// <summary>The LIS3DH's acceleration in mg (x, y, z); null when the firmware does not send it.</summary>
    public (int X, int Y, int Z)? Accel { get; }

    /// <summary>The 12-bit code of ADC channel 0..8.</summary>
    public int AdcCode(int channel) => adc[channel] & 0xFFF;

    /// <summary>Header input in volts: 0..7 = AIN0..AIN7, 8 = the dedicated AIN (as Forth's <c>mv@</c>).</summary>
    public double Input(int input) => AdcCode(InputChannels[input]) * 3.3 / 4096;

    /// <summary>"AIN0".."AIN7", "AIN" for input 8.</summary>
    public static string InputLabel(int input) => input < 8 ? "AIN" + input : "AIN";

    public static string Label(int pin) => pin switch
    {
        < 15 => "D" + pin,
        >= 16 and < 24 => "PIO" + (pin - 15),
        _ => "?" + pin,
    };

    public static Max1000State Parse(string text)
    {
        Dictionary<string, string[]> lines = BoardVersion.Lines(text);
        uint Hex(string s) => uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        uint board = Hex(lines["board"][0]);
        uint[] ports = [.. lines["ports"].Take(2).Select(Hex)];
        uint pwm = Hex(lines["pwm"][0]);
        ushort[] adc = [.. lines["adc"].Take(9).Select(s => (ushort)Hex(s))];
        if (ports.Length != 2 || adc.Length != 9)
        {
            throw new FormatException("incomplete board answer");
        }

        int? temp = lines.TryGetValue("temp", out string[]? t) && t.Length > 0 ? (int)Hex(t[0]) : null;
        (int, int, int)? accel = lines.TryGetValue("accel", out string[]? a) && a.Length >= 3
            ? ((short)Hex(a[0]), (short)Hex(a[1]), (short)Hex(a[2]))
            : null;
        return new Max1000State(board, ports, pwm, adc, temp, accel);
    }
}
