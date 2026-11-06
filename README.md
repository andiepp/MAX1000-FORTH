# MAX1000-FORTH

An interactive 16-bit Forth on its own CPU, in the FPGA of a Trenz **MAX1000** (TEI0001-02,
Intel MAX 10 10M08SAU169C8G). The CPU is **HF16**, the Forth processor of
[ZYNQ-FORTH](https://github.com/andiepp/ZYNQ-FORTH); here it runs alone, with no soft
processor and no loader: the Forth kernel is part of the FPGA configuration, and Forth talks
straight to the board's USB serial port.

![The ZYNQ-FORTH app with the MAX1000 running examples/bench.fs](docs/images/app-max1000.png)

- **HF16** at 48 MHz, one Forth primitive per clock, with 24 KB of RAM: 16 KB for code and
  the dictionary, 8 KB data RAM.
- A **Forth kernel** of about 300 words, written in Forth and cross-compiled on the PC: the
  interpreter and compiler, 32-bit mixed arithmetic, and the board's I/O.
- **Board I/O:** 8 user LEDs and the user button; 23 digital pins (D0..D14 and the Pmod's
  PIO1..PIO8) with four PWM channels and a 192 MHz DDS square-wave generator on any pin; 9 analog inputs (12 bits, 0..3.3 V); the
  MAX 10's temperature sensor; the on-board LIS3DH accelerometer.
- **Saved source:** one Forth source in the board's 8 MB serial flash runs at every start;
  hold the user button (or send ESC) to skip it.
- **Any terminal** (PuTTY, Tera Term) talks to Forth at 115200 baud. The
  [ZYNQ-FORTH app](https://github.com/andiepp/ZYNQ-FORTH/releases) (2.4 and later) adds an
  editor, the data stack, the dictionary, the saved source and a live view of the board: the
  kernel answers its service requests itself.

## Get started

1. Download `MAX1000-FORTH-<version>.zip` from the
   [releases](https://github.com/andiepp/MAX1000-FORTH/releases) and unpack it.
2. Install the **Arrow USB Blaster** driver and the **Quartus Prime Programmer** (or Quartus
   Prime Lite, which contains it).
3. Plug in the MAX1000 and write MAX1000-FORTH into its flash:

   ```powershell
   .\firmware\program_flash.ps1          # internal flash: starts at every power-up
   .\firmware\program_flash.ps1 -Sram    # or only until power-off
   ```

4. Open the board's second serial port (the FTDI's channel B) at **115200 8N1** in a
   terminal or the ZYNQ-FORTH app, press Enter, and type Forth:

   ```
   HF16 Forth on MAX1000
   2 3 + .  5  ok
   : blink 10 0 do 0 output 0 toggle 200 ms loop ;  ok
   accel@ . . .  1024 -10 34  ok
   .temp  31.7 C  ok
   ```

**Ctrl-R** restarts Forth at any time, even out of an endless loop. The board's **Reset**
button reloads the whole FPGA.

The [user manual](docs/Manual.md) (PDF in the release) covers the app, the interpreter,
every word, examples and troubleshooting; [docs/design.md](docs/design.md) the
implementation: instruction set, memory and I/O maps, the peripherals, the saved source and
the service protocol.

## Words at a glance

| Area | Words |
|---|---|
| LEDs, button, time | `led!` `led@` `btn@` `ms@` `ms` |
| Digital pins (0..14 = D0..D14, 16..23 = PIO1..PIO8) | `output` `input` `high` `low` `toggle` `pin!` `pin@` |
| PWM | `pwm-hz` `pwm-khz` `pwm-duty` `pwm-pin` `pwm-off` |
| DDS square wave (0.045 Hz steps) | `dds-hz` `dds-khz` `dds-tuning` `dds-pin` `dds-off` |
| Analog (0..7 = AIN0..AIN7, 8 = AIN) | `adc@` `mv@` `adc-ch@` |
| Temperature, accelerometer | `temp@` `.temp` `temp-code@` `accel@` `accel?` `acc@` `acc!` |
| Data RAM | `buffer:` `unused` `unused-data` |
| Saved source | `.source` `load-source` `autostart` `erase-source` `source?` |
| Serial flash | `flash-id` `f-read` `f-prog` `f-erase` `f-erase64` `spi` `crc@` |

`tools/mforth.py` sends lines or files from the PC and saves a source in the flash (needs
pyserial):

```powershell
python tools/mforth.py -p COMx -f examples/bench.fs        # send a file
python tools/mforth.py -p COMx --store examples/bench.fs   # save it; runs at every start
python tools/mforth.py -p COMx --skip                      # restart without it
```

## Building

Needs Quartus Prime Lite 18.1 (MAX 10 support) and Python 3.

```powershell
scripts\build.ps1                      # kernel + FPGA (build\output_files\max1000_forth.pof/.sof)
scripts\build.ps1 -PullUps             # the same with weak pull-ups on the digital pins
scripts\program_flash.ps1 [-Sram]      # write it to the board
sim\run_flash_tests.ps1                # simulator tests: flash and saved source
python sim\test_protocol.py            # simulator tests: the app's service requests
python tools\hf16.py run forth\kernel.fs -i "2 3 + ."      # the kernel in the simulator
scripts\Build-Release.ps1 -Version 1.0                      # release zip in dist\
```

`build.ps1` finds Quartus via `-Quartus`, `QUARTUS_ROOTDIR` or `<drive>:\Quartus\18.1\quartus`.
The design uses about 4.3k of 8,064 logic elements, 70% of the M9K RAM and 18 multiplier
elements, with about 1.5 ns of slack at 48 MHz (CPU) and 1.2 ns at 192 MHz (DDS).

## Repository

| Path | Content |
|---|---|
| `rtl/` | Verilog: `hf16_cpu.v` (from ZYNQ-FORTH), `max1000_forth.v` (top), FIFOs, UART, PWM, ADC scanner, SPI master |
| `forth/kernel.fs` | the Forth kernel |
| `tools/hf16.py` | cross-compiler and instruction-set simulator |
| `tools/mforth.py` | line sender and source store |
| `tools/manual_pdf.py` | builds `docs/Manual.pdf` |
| `scripts/` | build, flash and release scripts |
| `sim/` | simulator tests |
| `docs/` | user manual, implementation details, images |
| `examples/` | example programs |

## License

MIT, see [LICENSE](LICENSE). HF16, the kernel's core and the tools come from
[ZYNQ-FORTH](https://github.com/andiepp/ZYNQ-FORTH).
