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
                                                                       ├─ ADC: AIN, AIN0..AIN7, 12 bit, 0..3.3 V
                                                                       └─ SPI flash (8 MB): saved source + autostart
```

Status: phases 1 and 2 work on the board (2026-11-05). You get an interactive Forth with
digital I/O, PWM and ADC words, and a source saved in the serial flash that runs at power-up.
Not there yet: the ZYNQ-FORTH app's service requests (phase 3).

## Using it

Serial port: the MAX1000's second COM port (FTDI channel B), **115200 8N1**, no flow control.
Lines end in CR and hold at most 80 characters. **Ctrl-R** restarts Forth: the dictionary goes
back to the kernel's, then the saved source runs if autostart is on.

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

### Saved source and autostart

One source file of up to 65,535 bytes can live in the board's serial flash. At power-up,
after Reset and after Ctrl-R, Forth runs it as if it were typed, but without echo. Words it
defines are there when the `ok` prompt comes, and its last line can start an application.

```powershell
python tools/mforth.py -p COM5 --store examples/bench.fs                 # save, autostart on
python tools/mforth.py -p COM5 --store examples/bench.fs --no-autostart  # save only
python tools/mforth.py -p COM5 --skip                                    # restart without it
```

To skip the saved source at start, hold the **user button** during power-up or Reset, or send
**ESC** within 200 ms of the start. `--skip` sends Ctrl-R + ESC, which recovers from a source
that hangs. If a line of the saved source fails, the rest is not run, and the error ends in
`(in the saved source)`.

| Word | Stack | Meaning |
|---|---|---|
| `.source` | `( -- )` | list the saved source |
| `load-source` | `( -- )` | run the saved source now (after the current line) |
| `autostart` | `( f -- )` | switch autostart on (true) or off (false) |
| `erase-source` | `( -- )` | delete the saved source |
| `source?` | `( -- len \| 0 )` | length of a valid saved source (header and CRC checked) |
| `store` | `( len flags -- )` | receive and save a source (used by `mforth.py --store`) |
| `flash-id` | `( -- mfr type size )` | the flash chip's JEDEC ID (EF 40 17 = W25Q64) |

Lower-level flash words for your own data: `f-read ( lo hi -- )` then `0 spi` per byte and
`f-end`; `f-erase ( lo hi -- )` for 4 KB; `f-erase64` for 64 KB; `f-prog ( a n lo hi -- )`
within one 256-byte page. Addresses have 24 bits as `( lo hi )`. The saved source uses
0x000000..0x000FFF and 0x010000..0x01FFFF; the rest of the 8 MB is free.

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
sim/run_flash_tests.ps1                                                      # saved-source tests (simulator)
```

`build.ps1` finds Quartus via `-Quartus`, `QUARTUS_ROOTDIR` or `<drive>:\Quartus\18.1\quartus`.
Resources: about 4.0k of 8,064 logic elements, 149 kbit of RAM, 18 multiplier elements;
40 MHz with about 2.7 ns of slack at the slow corner. The kernel takes 6.4 KB of the 16 KB
RAM.

## Layout

| Path | Content |
|---|---|
| `rtl/hf16_cpu.v` | the CPU (from ZYNQ-FORTH, unchanged) |
| `rtl/max1000_forth.v` | top level: PLL, RAM, UART, I/O registers |
| `rtl/fifo_bram.v`, `rtl/uart.v` | UART FIFOs in block RAM, UART |
| `rtl/pwm.v` | PWM (from ZYNQ-FORTH) |
| `rtl/adc_scan.v` | ADC scanner around Intel's Modular ADC control core |
| `rtl/spi_flash.v` | SPI master for the serial flash, CRC-32 unit |
| `sim/run_flash_tests.ps1` | simulator tests of the flash words and the saved source |
| `forth/kernel.fs` | the Forth kernel (ZYNQ-FORTH kernel with MAX1000 board words) |
| `tools/hf16.py` | cross-compiler and instruction-set simulator |
| `tools/mforth.py` | serial line sender |
| `docs/design.md` | machine model, I/O map, pins, ADC |
| `examples/bench.fs` | demo for the pot / button / LED bench wiring |

License: MIT (see LICENSE). HF16, its kernel and tools come from ZYNQ-FORTH.
