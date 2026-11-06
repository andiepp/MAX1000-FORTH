using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ZynqForth.Board;
using ZynqForth.Themes;

namespace ZynqForth.Controls;

/// <summary>
/// The Trenz MAX1000 as MAX1000-FORTH sees it: the 8 user LEDs, the user button, the digital
/// pins D0..D14 and PIO_01..PIO_08 (level, direction, PWM) and the analog inputs AIN0..AIN7
/// and AIN. Same size and skin colours as <see cref="BoardView"/>; put it in a Viewbox.
/// </summary>
public sealed class Max1000View : FrameworkElement
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(Max1000State), typeof(Max1000View),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double Width0 = 960;
    private const double Height0 = 210;
    private const double Pitch = 23;

    public Max1000View()
    {
        Loaded += (_, _) => Skins.Changed += InvalidateVisual;
        Unloaded += (_, _) => Skins.Changed -= InvalidateVisual;
    }

    public Max1000State? State
    {
        get => (Max1000State?)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Width0, Height0);

    protected override void OnRender(DrawingContext dc)
    {
        Brush pcb = Skins.Brush(this, "Zf.BoardPcb", Brushes.DarkGreen);
        Brush text = Skins.Brush(this, "Zf.BoardText", Brushes.White);
        Brush border = Skins.Brush(this, "Zf.Border", Brushes.Black);
        var outline = new Pen(border, 1);
        Max1000State? s = State;

        dc.DrawRoundedRectangle(pcb, outline, new Rect(0.5, 0.5, Width0 - 1, Height0 - 1), 10, 10);
        Label(dc, "MAX1000 · HF16 FORTH", 16, 12, text, 13, bold: true);
        if (s is null)
        {
            Label(dc, "not connected", Width0 - 16, 12, Skins.Brush(this, "Zf.DimText", Brushes.Gray), 12, align: TextAlignment.Right);
        }

        DrawLedsAndButton(dc, s, text, outline);
        DrawPins(dc, s, text, outline);
        DrawAnalog(dc, s, text, outline);
    }

    private void DrawLedsAndButton(DrawingContext dc, Max1000State? s, Brush text, Pen outline)
    {
        Brush ledOn = Skins.Brush(this, "Zf.LedOn", Brushes.Red);
        Brush ledOff = Skins.Brush(this, "Zf.LedOff", Brushes.DimGray);
        Brush cap = Skins.Brush(this, "Zf.ButtonCap", Brushes.Gray);
        Brush capPressed = Skins.Brush(this, "Zf.ButtonCapPressed", Brushes.DodgerBlue);

        // LED1..LED8 (led! bit 0..7)
        double x = 20, y = 52;
        Label(dc, "LEDS", x, y - 18, text, 10);
        for (int i = 0; i < 8; i++)
        {
            bool on = s is not null && (s.Leds & (1 << i)) != 0;
            var rect = new Rect(x, y, 18, 10);
            if (on)
            {
                dc.DrawRoundedRectangle(null, new Pen(WithOpacity(ledOn, 0.4), 5), Inflate(rect, 1), 3, 3);
            }

            dc.DrawRoundedRectangle(on ? ledOn : ledOff, outline, rect, 2, 2);
            Label(dc, (i + 1).ToString(CultureInfo.InvariantCulture), x + 9, y + 14, text, 10, align: TextAlignment.Center);
            x += 24;
        }

        // the user button beside Reset (btn@)
        x = 20;
        y = 120;
        Label(dc, "USER BUTTON", x, y - 22, text, 10);
        bool pressed = s?.Button == true;
        dc.DrawRectangle(Skins.Brush(this, "Zf.LedOff", Brushes.DimGray), outline, new Rect(x, y - 4, 24, 24));
        dc.DrawEllipse(pressed ? capPressed : cap, outline, new Point(x + 12, y + 8), 8, 8);
        Label(dc, pressed ? "pressed" : "", x + 34, y + 2, text, 10);

        // chip temperature and the accelerometer (MAX1000-FORTH 1.0 and later)
        string chip = s?.Temperature is double t
            ? string.Create(CultureInfo.InvariantCulture, $"10M08  {t:0.0} °C")
            : "10M08";
        Label(dc, chip, 20, 146, text, 11);
        if (s?.Accel is var (ax, ay, az))
        {
            Label(dc, string.Create(CultureInfo.InvariantCulture,
                      $"tilt  x {ax / 1000.0:0.00}  y {ay / 1000.0:0.00}  z {az / 1000.0:0.00} g"), 20, 164, text, 11);
        }

        if (s is not null)
        {
            var channels = new List<string>();
            for (int c = 0; c < 4; c++)
            {
                if ((s.PwmEnabled & (1 << c)) != 0)
                {
                    channels.Add($"PWM{c} → {Max1000State.Label(s.PwmPins[c])}");
                }
            }

            Label(dc, channels.Count == 0 ? "PWM off" : string.Join("   ", channels), 20, 182, text, 11);
        }
    }

    private void DrawPins(DrawingContext dc, Max1000State? s, Brush text, Pen outline)
    {
        Brush high = Skins.Brush(this, "Zf.PinHigh", Brushes.SkyBlue);
        Brush low = Skins.Brush(this, "Zf.PinLow", Brushes.DimGray);
        Brush driven = Skins.Brush(this, "Zf.PinDriven", Brushes.OrangeRed);
        Brush drivenLow = Skins.Brush(this, "Zf.PinDrivenLow", Brushes.Maroon);
        Brush pwm = Skins.Brush(this, "Zf.PinPwm", Brushes.Orchid);

        const double left = 240;
        Label(dc, "DIGITAL", left, 34, text, 10);
        Legend(dc, left + 60, 34, [(high, "in 1"), (low, "in 0"), (driven, "out 1"), (drivenLow, "out 0"), (pwm, "PWM")], text, outline);

        DrawPinRow(dc, s, [.. Enumerable.Range(0, 15)], left, 70, text, outline, high, low, driven, drivenLow, pwm);
        Label(dc, "PMOD", left, 108, text, 10);
        DrawPinRow(dc, s, [.. Enumerable.Range(16, 8)], left, 128, text, outline, high, low, driven, drivenLow, pwm);
        Label(dc, "pins 0–14 = D0–D14, 16–23 = PIO1–PIO8", left, 182, text, 11);
    }

    private void DrawPinRow(DrawingContext dc, Max1000State? s, int[] pins, double left, double y, Brush text, Pen outline,
                            Brush high, Brush low, Brush driven, Brush drivenLow, Brush pwm)
    {
        for (int i = 0; i < pins.Length; i++)
        {
            int number = pins[i];
            ShieldPin? pin = s?.Pins.FirstOrDefault(p => p.Number == number);
            double x = left + i * Pitch;
            Brush fill = pin is null ? low
                : pin.PwmChannel is not null ? pwm
                : pin.Driven ? (pin.Level ? driven : drivenLow)
                : pin.Level ? high : low;
            var rect = new Rect(x, y, 16, 16);
            dc.DrawRectangle(fill, outline, rect);
            dc.DrawEllipse(Skins.Brush(this, "Zf.BoardPcb", Brushes.DarkGreen), null, new Point(x + 8, y + 8), 3, 3);
            if (pin?.PwmChannel is int channel)
            {
                Label(dc, "~" + channel, x + 8, y - 13, text, 9, align: TextAlignment.Center);
            }

            Label(dc, Max1000State.Label(number), x + 8, y + 19, text, 9, align: TextAlignment.Center);
        }
    }

    /// <summary>AIN0..AIN7 and AIN as bars with their voltage (0..3.3 V).</summary>
    private void DrawAnalog(DrawingContext dc, Max1000State? s, Brush text, Pen outline)
    {
        Brush bar = Skins.Brush(this, "Zf.PinHigh", Brushes.SkyBlue);
        Brush slot = Skins.Brush(this, "Zf.LedOff", Brushes.DimGray);

        const double left = 610, top = 56, height = 92;
        Label(dc, "ANALOG (V)", left, 34, text, 10);
        for (int input = 0; input < 9; input++)
        {
            double x = left + input * 37;
            dc.DrawRectangle(slot, outline, new Rect(x, top, 22, height));
            if (s is not null)
            {
                double volts = s.Input(input);
                double h = Math.Clamp(volts / 3.3, 0, 1) * height;
                dc.DrawRectangle(bar, null, new Rect(x + 1, top + height - h, 20, h));
                Label(dc, volts.ToString("0.00", CultureInfo.InvariantCulture), x + 11, top + height + 22, text, 9,
                      align: TextAlignment.Center);
            }

            Label(dc, Max1000State.InputLabel(input), x + 11, top + height + 6, text, 9, align: TextAlignment.Center);
        }
    }

    private void Legend(DrawingContext dc, double x, double y, (Brush Brush, string Text)[] items, Brush text, Pen outline)
    {
        foreach ((Brush brush, string label) in items)
        {
            dc.DrawRectangle(brush, outline, new Rect(x, y + 2, 10, 10));
            FormattedText formatted = Format(label, text, 10, false);
            dc.DrawText(formatted, new Point(x + 14, y));
            x += 22 + formatted.Width;
        }
    }

    private void Label(DrawingContext dc, string value, double x, double y, Brush brush, double size,
                       bool bold = false, TextAlignment align = TextAlignment.Left)
    {
        FormattedText formatted = Format(value, brush, size, bold);
        formatted.TextAlignment = align;
        dc.DrawText(formatted, new Point(x, y));
    }

    private FormattedText Format(string value, Brush brush, double size, bool bold)
    {
        var family = Skins.Value(this, "Zf.UiFont", new FontFamily("Segoe UI"));
        var typeface = new Typeface(family, FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal);
        return new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, brush,
                                 VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    private static Rect Inflate(Rect rect, double by)
    {
        rect.Inflate(by, by);
        return rect;
    }

    private static Brush WithOpacity(Brush brush, double opacity)
    {
        Brush copy = brush.Clone();
        copy.Opacity = opacity;
        copy.Freeze();
        return copy;
    }
}
