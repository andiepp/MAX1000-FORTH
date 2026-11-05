# MAX1000-FORTH design

The CPU, its instruction set and the kernel come from ZYNQ-FORTH; its `docs/design.md`
describes the machine model and the instruction encoding, which are the same here. This
document covers what is different on the MAX1000.

## System

| Part | Implementation |
|---|---|
| Clock | 12 MHz oscillator → PLL: c1 = 40 MHz CPU clock (12 × 10 / 3), c0 = 10 MHz for the ADC |
| Reset | held until the PLL locks, plus 255 clocks; **Ctrl-R** (0x12) from the host resets the CPU, FIFOs and I/O (the byte is not passed on) |
| RAM | 8K × 16 as two 8K × 8 halves in M9K blocks (byte stores without byte enables); port A fetches instructions, port B is the data port. Both halves are initialized from `build/forth_lo.hex` / `forth_hi.hex` |
| UART | 115200 8N1 on FTDI channel B (FPGA TX = B4, RX = A4), 1 KB RX and TX FIFOs in M9K |
| Stacks | Quartus maps the 32-entry data and return stacks into M9K blocks (the read address is a register) |

There is no ARM or soft processor: the kernel image is in the configuration (flash mode
"single image with ERAM", so the `.pof` carries the RAM contents), and the UART connects
straight to the CPU's FIFOs. The ZYNQ-FORTH control registers (load, pause, AXI) do not exist.

Timing: the longest path is stack RAM → 16×16 multiplier → ALU → next data address → I/O
decode, about 21.3 ns at the slow corner (85 °C). 48 MHz misses by 0.7 ns, so the CPU runs
at 40 MHz.

## I/O map

Address bit 15 selects I/O; only bits 7..1 are decoded.

| Address | Read | Write |
|---|---|---|
| 0x8000 | next received byte (pops the RX FIFO) | send byte (pushes the TX FIFO) |
| 0x8002 | bit 0 = RX byte available, bit 1 = TX FIFO has space | – |
| 0x8004 | user LEDs (8 bits, bit 0 = LED1) | same |
| 0x8006 | bit 0 = user button pressed (sampled every 10 ms) | – |
| 0x800A | millisecond counter (wraps) | – |
| 0x8010 + 8p | port p (0..1) pin levels, synchronized | – |
| 0x8012 + 8p | port p output latch | same |
| 0x8014 + 8p | port p direction, 1 = output (0 after reset) | same |
| 0x8040 + 2c | ADC channel c (0..8), 12-bit code | – |
| 0x8080 + 8c | PWM channel c (0..3), as in ZYNQ-FORTH (period counts 40 MHz clocks) | same |

### Pins

| Pin numbers | Port bits | Header | FPGA pins |
|---|---|---|---|
| 0..14 | port 0 bits 0..14 | D0..D14 | H8 K10 H5 H4 J1 J2 L12 J12 J13 K11 K12 J10 H10 H13 G12 |
| 16..23 | port 1 bits 0..7 | PIO_01..PIO_08 (Pmod) | M3 L3 M2 M1 N3 N2 K2 K1 |

Pin 15 and 24..31 do not exist (read 0, ignore writes); `pin` rejects them. No pull-ups are
enabled, so an open input reads at random. PWM channels can own any existing pin.

### ADC

`rtl/adc_scan.v` drives Intel's Modular ADC control core (`altera_modular_adc_control`,
used in place from the Quartus installation): 1 MSPS, internal reference (VCCA = 3.3 V),
channels CH8 down to CH0 in turn. Each channel is converted four times in a row and only
the last result is kept, because a high-impedance source does not settle in one
conversion after another channel.

| Header | AIN | AIN0 | AIN1 | AIN2 | AIN3 | AIN4 | AIN5 | AIN6 | AIN7 |
|---|---|---|---|---|---|---|---|---|---|
| FPGA pin | D2 (ANAIN1) | E1 | C2 | C1 | D1 | E3 | E4 | F1 | B1 |
| ADC channel | 0 | 8 | 2 | 5 | 1 | 3 | 4 | 7 | 6 |
| `adc@` input | 8 | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 |

The channel numbers come from the Quartus pin report; AIN3 = CH1 was confirmed with the
bench potentiometer. AIN7 = B1 is the remaining ADC pin and has not been checked with a
signal yet.

## Board facts

- The on-board **Reset** button is the MAX 10's nCONFIG (E7): it reloads the configuration
  from internal flash, so it is not available as an input.
- The LEDs and UART pins sit next to the ADC pins; Quartus warns about it (16248). They are
  fixed by the board.
- `analog_input_pin_mask` of the ADC control core must be an integer (65791 = 0x100FF).
