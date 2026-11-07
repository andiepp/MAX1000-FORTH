# Changelog

## 1.4.1 — 2026-11-07

- **Help** in the app: a window with every Forth word of the kernel (381 entries), opened with
  the Help button beside Manual or F2 (on the word at the cursor in the editor or the input
  line). It stays open while you work; the search covers names, stack effects and
  descriptions; an area list limits it to one section; each word shows its stack effect,
  description, manual section, related words and examples. Insert, Copy, Keep on top.
- The help is taken from the manual's word reference when the app is built
  (`tools/make_word_help.py`, which also checks that every kernel word is described).
- Manual: section 2.8 (Help). Firmware unchanged (1.4).

## 1.4 — 2026-11-06

- **See** in the app: the Dictionary's new See button shows the selected word as Forth
  source, decompiled from the board's memory (calls, numbers, `if`/`else`/`then`,
  `begin`/`until`/`again`, `while`/`repeat`, `do`/`loop`, `."`, `variable`, `constant`,
  `create`), with a listing of every cell, Copy and To editor.
- Kernel: the service request `mem <addr> <cells>` returns RAM cells in hex (RAM only, at
  most 256 cells). Firmware 1.4; 380 words, 12,930 bytes, 3,454 bytes of code space left.
- Tests: `mem` in `sim/test_protocol.py`; the decompiler against the real kernel
  (`tools/make_see_fixture.py`, 24 cases plus every kernel word) in the app's tests.
- `program_flash.ps1` names its steps and the time it took; the repository carries the
  firmware and the app, so GitHub's Code > Download ZIP works.

## 1.3.1 — 2026-11-06

- **The MAX1000-FORTH app** comes with the release: `app/Max1000Forth.exe`, one program for
  64-bit Windows without installation (terminal, editor, data stack, dictionary, saved
  source, board view). Its source is in `app/` (C#, WPF, .NET 10); F1 opens this release's
  manual; settings in `%LocalAppData%/MAX1000-FORTH`.
- The README and the manual describe the bundled app and no longer refer to other projects;
  the manual has the board's pinout (section 5.11) and a new screenshot.
- Firmware unchanged (1.3).

## 1.3 — 2026-11-06

- **Serial interfaces on any pin** (after ZYNQ-FPGA-FORTH32 3.1): two UARTs (8N1, 3 Bd to
  6 MBd, 1 KB receive and send buffers), two SPI masters (modes 0..3, 8 or 16 bits, MSB or
  LSB first, up to 12 MHz) and an I2C master (1..1000 kHz, open drain, clock stretching).
  Words: `uart-open` `uart-close` `uart-emit` `uart-type` `uart-key?` `uart-key`
  `uart-status`, `spi-open` `spi-close` `spi-khz` `spi-mode` `spi-lsb` `spi-bits` `spi-xfer`
  `spi-write` `spi-read`, `i2c-open` `i2c-close` `i2c!` `i2c@` `i2c-probe` `i2c-scan`
  `i2c-start` `i2c-stop` `i2c-addr` `i2c-write` `i2c-read`. Registers at 0x80A0..0x80E4.
- **Double numbers** can be typed with a trailing dot (`115200.`, unsigned); the baud rate of
  `uart-open` is one. SPI and I2C take their clock in kHz.
- **Pin owners:** a pin belongs to one PWM channel, the DDS or one serial interface at a time;
  another one gets ` pin taken` (in 1.2 two PWM channels, or PWM and DDS, on one pin were
  ORed). `owner ( pin -- id )` tells who has it. The open words check all their pins first, so
  a refused one changes nothing.
- `cold` typed at the prompt now switches PWM, DDS and the serial interfaces off (Ctrl-R and a
  restart always did); the pins keep their state.
- Port register +6 (`$8016`, `$801E`) reads the pins driven now; the `board` answer takes the
  driven pins from it and has a new last line, `fn`, with the pins of the serial interfaces.
- The CPU's stacks are kept in M9K explicitly (a `ramstyle` attribute): without it, Quartus
  built them from logic once the UART buffers took four more M9K blocks, and the CPU clock
  lost almost all its timing margin.
- The simulator models the serial interfaces per transfer (`--wire A-B` joins pins,
  `--i2c-dev ADDR` adds an I2C register memory); new tests `sim/test_serial.py` (43 checks)
  and the Verilog testbench `sim/tb_serial.v` (`sim/run_serial.ps1`, 2,016 checks).
- The FPGA uses 6,451 of 8,064 logic elements (1.2: 4,259) and 40 of 42 M9K blocks; the
  kernel has 376 words, 12,646 bytes, and leaves 3,738 bytes of code space for your words
  (1.2: 5,870).
- **Docs:** manual section 5.15 (serial interfaces) and examples in 6.5; the implementation
  details describe the new units, the pin router and the tests.

## 1.2 — 2026-11-06

- **Single LEDs:** `led-on`, `led-off` and `led-toggle ( n -- )` switch LED n (1..8) and
  leave the others as they are; `led-mask ( n -- mask )` gives its bit; ` bad led` for other
  numbers.
- `key?` no longer waits for good when an app request arrives while a word keeps the flash or
  the accelerometer selected: it answers false, and the request waits until the device is
  released.
- The `input` count of the `status` answer is taken once, so a byte arriving during the
  answer can no longer make the announced length wrong.
- `store` requests take a decimal length of at most 65535; a bigger one is refused (`err store
  args`) instead of wrapping around.
- The simulator clears the RX FIFO on a restart, as the hardware does.
- **Docs:** the manual's contents list every section as a link (chapters and their
  sub-chapters), and has the DDS section (5.14) and the LED words; the release contains
  `docs/Implementation-Details.pdf`, a detailed description of the implementation (CPU,
  memory and I/O, peripherals, kernel, service protocol, tools, design decisions).

## 1.1 — 2026-11-06

- **DDS square wave** on any pin (after ZYNQ-FPGA-FORTH32): a 32-bit phase accumulator on
  its own 192 MHz clock, 0.0447 Hz steps, edges on a 5.2 ns grid; `dds-hz`, `dds-khz`,
  `dds-tuning`, `dds-pin`, `dds-off` (registers at 0x8060). The `board` answer gets a
  `dds` line and counts the DDS pin as driven. About 200 logic elements, no RAM.
- The ADC now decodes only 0x8040..0x805F.

## 1.0 — 2026-11-05

The first release: the HF16 Forth of [ZYNQ-FORTH](https://github.com/andiepp/ZYNQ-FORTH),
alone on a Trenz MAX1000 (Intel MAX 10 10M08).

- **CPU and kernel.** HF16 at 48 MHz, one instruction per clock; the kernel (about 300
  words, 9.9 KB) is part of the FPGA configuration. RAM 24 KB: 16 KB for code and the
  dictionary, 8 KB data RAM (`buffer:`).
- **Terminal.** 115200 8N1 on the board's USB serial port; Ctrl-R restarts Forth in
  hardware, even in an endless loop.
- **Board words.** 8 user LEDs, the user button, a millisecond timer; 23 digital pins
  (D0..D14, PIO1..PIO8) with four PWM channels on any pin; 9 analog inputs (AIN0..AIN7, AIN,
  12 bits, 0..3.3 V); the MAX 10's temperature diode (`temp@`, approximate); the LIS3DH
  accelerometer (`accel@`, mg).
- **Saved source.** One source of up to 64 KB in the 8 MB serial flash runs at every start
  (skip it with the user button or ESC); `tools/mforth.py --store`, `.source`, `autostart`,
  `erase-source`; the rest of the flash is free for your own data (`f-read`, `f-prog`,
  `f-erase`).
- **App.** The kernel answers the ZYNQ-FORTH app's service requests; the app (2.4 and later)
  shows the MAX1000's board view.
- **Build.** Quartus Prime Lite 18.1; `scripts/build.ps1` (`-PullUps` builds weak pull-ups
  into the digital pins); simulator tests for the saved source and the app protocol.
- **Docs.** User manual (`docs/Manual.md`, PDF in the release) and implementation details
  (`docs/design.md`).
