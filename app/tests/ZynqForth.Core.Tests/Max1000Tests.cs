using ZynqForth.Board;

namespace ZynqForth.Tests;

/// <summary>The MAX1000-FORTH answers (Trenz MAX1000), as the board sends them.</summary>
public class Max1000Tests
{
    [Fact]
    public void VersionNamesTheBoard()
    {
        BoardVersion max = BoardVersion.Parse("firmware max1000-forth 1.0\nprotocol 1\nkernel 4875 A7AF\n");
        Assert.Equal(BoardKind.Max1000, max.Kind);
        Assert.Equal("MAX1000-FORTH", max.Product);
        Assert.Equal("1.0", max.Firmware);
        Assert.Equal(4875, max.KernelWords);

        BoardVersion zynq = BoardVersion.Parse("firmware zynq-forth 2.3\nprotocol 1\nkernel 2643 1234\n");
        Assert.Equal(BoardKind.Zynq, zynq.Kind);
        Assert.Equal("ZYNQ-FORTH", zynq.Product);
    }

    [Fact]
    public void Board()
    {
        // LEDs $A5, button pressed; D3 driven high, D5 driven by PWM channel 0, D6 high input;
        // PIO1..PIO8 high inputs; AIN3 (channel 1) at half scale, AIN0 (channel 8) at full scale
        Max1000State s = Max1000State.Parse(
            "board 000100A5\nports 00280048 000000FF\npwm 00000051\n" +
            "adc 0000 0800 0000 0000 0000 0000 0000 0000 0FFF\n");

        Assert.Equal(0xA5, s.Leds);
        Assert.True(s.Button);
        Assert.Equal(23, s.Pins.Count);

        ShieldPin d3 = s.Pins.Single(p => p.Number == 3);
        Assert.True(d3.Driven);
        Assert.True(d3.Level);
        Assert.Equal("D3", d3.Label);
        Assert.Equal(0, s.Pins.Single(p => p.Number == 5).PwmChannel);
        ShieldPin d6 = s.Pins.Single(p => p.Number == 6);
        Assert.False(d6.Driven);
        Assert.True(d6.Level);
        Assert.Equal("PIO1", s.Pins.Single(p => p.Number == 16).Label);
        Assert.True(s.Pins.Single(p => p.Number == 23).Level);

        Assert.Equal(1, s.PwmEnabled);
        Assert.Equal(5, s.PwmPins[0]);

        Assert.InRange(s.Input(3), 1.64, 1.66);    // AIN3
        Assert.InRange(s.Input(0), 3.29, 3.30);    // AIN0
        Assert.Equal(0, s.Input(8));               // AIN (dedicated)
        Assert.Equal("AIN", Max1000State.InputLabel(8));
    }

    [Fact]
    public void TemperatureAndAcceleration()
    {
        const string basic = "board 00000001\nports 00000000 00000000\npwm 00000000\n" +
                             "adc 0000 0000 0000 0000 0000 0000 0000 0000 0000\n";
        Max1000State s = Max1000State.Parse(basic + "temp 0E3C\naccel 001F FFF2 03F8\n");
        Assert.Equal(0xE3C, s.TempCode);
        Assert.InRange(s.Temperature!.Value, 32.6, 32.8);           // code 3644: Forth's .temp says 32.7 C
        Assert.Equal((31, -14, 1016), s.Accel);

        Max1000State old = Max1000State.Parse(basic);                // before MAX1000-FORTH 1.0
        Assert.Null(old.Temperature);
        Assert.Null(old.Accel);
    }

    [Fact]
    public void IncompleteBoardAnswerIsRefused() =>
        Assert.Throws<FormatException>(() => Max1000State.Parse("board 0\nports 0 0\npwm 0\nadc 0 0 0\n"));
}
