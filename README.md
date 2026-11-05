# MAX1000-FORTH

An interactive 16-bit Forth on the Trenz **MAX1000** (TEI0001-02, Intel MAX 10 10M08SAU169C8G).
The CPU is **HF16**, the Forth processor from [ZYNQ-FORTH](https://github.com/andiepp/ZYNQ-FORTH),
unchanged; its Forth kernel runs on the FPGA alone. No soft processor and no host-side
loader are involved: the kernel image is part of the FPGA configuration, and Forth talks
directly to the board's USB serial port.

```
 PuTTY / tools/mforth.py ── USB ── FTDI ch. B ── UART + 1 KB FIFOs ── HF16 CPU (40 MHz)
                                                                       ├─ 16 KB RAM (kernel preloaded)
                                                                       ├─ 8 user LEDs, user button, ms timer
                                                                       ├─ 23 digital pins: D0..D14, PIO_01..PIO_08
                                                                       ├─ PWM: 4 channels on any pin
                                                                       └─ ADC: AIN, AIN0..AIN7, 12 bit, 0..3.3 V
```

Status: phase 1. Interactive Forth with digital I/O, PWM and ADC words works on the board
(2026-11-05). Not there yet: saving source to flash plus autostart (phase 2), and the
ZYNQ-FORTH app's service requests (phase 3).

## Using it

Serial port: the MAX1000's second COM port (FTDI channel B), **115200 8N1**, no flow control.
Lines end in CR and hold at most 80 characters. **Ctrl-R** restarts Forth (the RAM goes back
to the kernel only).

```
HF16 Forth on MAX1000
3 mv@ .  1842  ok
3 output 3 high  ok
: blink 10 0 do 3 toggle 200 ms loop ;  blink  ok
```

`tools/mforth.py` sends lines or a file and waits for each answer (needs pyserial):

```powershell
python tools/mforth.py -p COM5 --restart -f examples/bench.fs
python tools/mforth.py -p COM5 "bench"
```

### Board words

| Word | Stack | Meaning |
|---|---|---|
| `led!` `led@` | `( n -- )` `( -- n )` | the 8 user LEDs, bit 0 = LED1 |
| `btn@` | `( -- f )` | user button, true while pressed |
| `ms@` `ms` | `( -- n )` `( n -- )` | free-running millisecond counter; wait n ms |
| `output` `input` | `( pin -- )` | pin direction (all pins are inputs after reset, no pull-ups) |
| `high` `low` `toggle` `pin!` | `( pin -- )` `( f pin -- )` | drive an output |
| `pin@` | `( pin -- f )` | pin level |
| `pwm-hz` `pwm-khz` | `( u ch -- )` | PWM channel 0..3 frequency (1 Hz .. 20 MHz) |
| `pwm-duty` | `( permille ch -- )` | duty 0..1000 |
| `pwm-pin` `pwm-off` | `( pin ch -- )` `( ch -- )` | put channel on a pin, switch it off |
| `adc@` | `( input -- u )` | 12-bit ADC code of input 0..7 = AIN0..AIN7, 8 = AIN |
| `mv@` | `( input -- mv )` | the same in millivolts (3.3 V full scale) |
| `adc-ch@` | `( ch -- u )` | raw ADC channel 0..8 |

Pin numbers: **0..14 = D0..D14**, **16..23 = PIO_01..PIO_08** (Pmod).

The rest is the ZYNQ-FORTH kernel: `: ; if else then begin until again while repeat do loop
+loop i j`, `variable constant create allot , c,`, `. u. .s ." type emit cr`, `+ - * / mod */
um* um/mod`, `words`, `hex decimal`, and so on (see `forth/kernel.fs`).

### Inputs that float

Open digital inputs read at random (no pull-ups). Open analog inputs show the charge left by
the channel converted before them; only driven inputs give meaningful values.

## Building

Needs Quartus Prime Lite 18.1 (MAX 10 support) and Python 3.

```powershell
scripts/build.ps1                                                            # kernel + FPGA
quartus_pgm -c 1 -m JTAG -o "p;build/output_files/max1000_forth.sof"         # SRAM (until power-off)
quartus_pgm -c 1 -m JTAG -o "p;build/output_files/max1000_forth.pof"         # internal flash
python tools/hf16.py run forth/kernel.fs -i "2 3 + ."                       # kernel in the simulator
```

`build.ps1` finds Quartus via `-Quartus`, `QUARTUS_ROOTDIR` or `<drive>:\Quartus\18.1\quartus`.
Resources: about 3.9k of 8,064 logic elements, 149 kbit of RAM, 18 multiplier elements;
40 MHz with about 2 ns of slack at the slow corner.

## Layout

| Path | Content |
|---|---|
| `rtl/hf16_cpu.v` | the CPU (from ZYNQ-FORTH, unchanged) |
| `rtl/max1000_forth.v` | top level: PLL, RAM, UART, I/O registers |
| `rtl/fifo_bram.v`, `rtl/uart.v` | UART FIFOs in block RAM, UART |
| `rtl/pwm.v` | PWM (from ZYNQ-FORTH) |
| `rtl/adc_scan.v` | ADC scanner around Intel's Modular ADC control core |
| `forth/kernel.fs` | the Forth kernel (ZYNQ-FORTH kernel with MAX1000 board words) |
| `tools/hf16.py` | cross-compiler and instruction-set simulator |
| `tools/mforth.py` | serial line sender |
| `docs/design.md` | machine model, I/O map, pins, ADC |
| `examples/bench.fs` | demo for the pot / button / LED bench wiring |

License: MIT (see LICENSE). HF16, its kernel and tools come from ZYNQ-FORTH.
