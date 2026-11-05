# MAX1000-FORTH — implementation details

HF16 is a small dual-stack machine whose instructions are Forth primitives. On the Trenz
MAX1000 (TEI0001-02, Intel MAX 10 10M08SAU169C8G) it runs alone: the Forth kernel,
cross-compiled from `forth/kernel.fs` by `tools/hf16.py`, is part of the FPGA configuration,
and the CPU talks to the host straight through the FTDI chip's UART. There is no soft
processor and no loader. The CPU, its instruction set and the kernel come from
[ZYNQ-FORTH](https://github.com/andiepp/ZYNQ-FORTH), where an ARM core loads the kernel and
answers the app; on the MAX1000 the kernel does both itself.

```
 PuTTY / ZYNQ-FORTH app / mforth.py
        │ USB
 FTDI FT2232H channel B ── UART 115200 ── RX FIFO 8 KB ─┐      ┌─ TX FIFO 1 KB ── UART
                            Ctrl-R → CPU reset           ▼      │
                                                    ┌────────────────┐
                                                    │ HF16 CPU 48 MHz │── RAM 24 KB (kernel preloaded)
                                                    └────────────────┘
                                                          │ I/O (address bit 15)
        8 user LEDs, user button, ms timer ───────────────┤
        23 digital pins: D0..D14, PIO1..PIO8 ─────────────┤
        PWM: 4 channels on any pin (rtl/pwm.v) ───────────┤
        ADC: 9 inputs + temperature diode (rtl/adc_scan.v)┤
        SPI master + CRC-32 (rtl/spi_flash.v) ────────────┴── W25Q64 serial flash (8 MB)
                                                         └── LIS3DH accelerometer
```

| File | Content |
|---|---|
| `rtl/hf16_cpu.v` | the CPU (from ZYNQ-FORTH, unchanged) |
| `rtl/max1000_forth.v` | top level: PLL, reset, RAM, UART, FIFOs, I/O registers, pins |
| `rtl/fifo_bram.v`, `rtl/uart.v` | FIFOs in block RAM, UART transmitter and receiver |
| `rtl/pwm.v` | four PWM channels (from ZYNQ-FORTH, separate read select) |
| `rtl/adc_scan.v` | ADC scanner around Intel's Modular ADC control core |
| `rtl/spi_flash.v` | SPI master for flash and accelerometer, CRC-32 unit |
| `constr/max1000_forth.sdc` | timing constraints |
| `scripts/build.tcl`, `scripts/build.ps1` | Quartus project, pins, build |
| `forth/kernel.fs` | the Forth kernel |
| `tools/hf16.py` | cross-compiler and instruction-set simulator (ISS) |
| `tools/mforth.py` | line sender, `--store`, `--skip` |
| `sim/run_flash_tests.ps1`, `sim/test_protocol.py` | ISS tests |

## Machine model

- 16-bit cells. Byte addresses; cells are little-endian and must be even-aligned.
- RAM 0x0000–0x5FFF (24 KB). Instructions are fetched from 0x0000–0x3FFF only: the PC and
  the targets of jumps, 0branch and calls are 13-bit word addresses (16 KB). 0x4000–0x5FFF
  is data RAM. Stores to 0x6000–0x7FFF are ignored; reads there return no defined value.
- I/O at 0x8000 and up (address bit 15 set); only bits 7..1 are decoded.
- Data stack: T register plus 32 entries (N is the top entry). Return stack: 32 entries.
  Both wrap silently.
- The return stack holds **byte** addresses; the PC is a word address (`pc = R >> 1`).
- One instruction per clock: 48 million instructions per second.

## Instruction encoding

| Bits 15..13 | Meaning |
|---|---|
| `1xx` | literal: push `insn[14:0]` (0..32767) |
| `000` | jump to word address `insn[12:0]` |
| `001` | 0branch: pop T, jump to `insn[12:0]` if it was zero |
| `010` | call `insn[12:0]`, pushing the byte address of the next instruction on R |
| `011` | ALU instruction, fields below |

ALU instruction fields:

| Bits | Field | Effect |
|---|---|---|
| 12 | R2P | return: `pc = R >> 1` (combine with rd = -1) |
| 11..7 | op | new T, table below |
| 6 | T2N | write old T into N (used by SWAP; implied on a data push) |
| 5 | N2M | store N at address T (RAM or I/O) |
| 4 | BYTE | the store writes one byte instead of a cell |
| 3..2 | rd | return stack delta: 00 = 0, 01 = +1 (pushes T), 11 = -1, 10 = -2 |
| 1..0 | dd | data stack delta: 00 = 0, 01 = +1 (pushes T into N), 11 = -1, 10 = -2 |

| op | new T | op | new T |
|---|---|---|---|
| 0 | T | 12 | R |
| 1 | N | 13 | cell at [T] |
| 2 | N + T | 14 | byte at [T] |
| 3 | N and T | 15 | data stack depth |
| 4 | N or T | 16 | N − T |
| 5 | N xor T | 17 | T + 1 |
| 6 | not T | 18 | T − 1 |
| 7 | N = T (−1/0) | 19 | T = 0 (−1/0) |
| 8 | N < T signed | 20 | T < 0 (−1/0) |
| 9 | N < T unsigned | 21 | N × T (low 16 bits) |
| 10 | N >> T logical | 22 | T >> 1 arithmetic |
| 11 | N << T | 23 | −T |
| | | 24 | N × T unsigned, high 16 bits (for `um*`) |

Literals above 32767 are compiled as the literal of their complement followed by `invert`.
A colon definition's last call becomes a jump (tail call); an ALU instruction at the end gets
the return folded in.

**Store hazard:** a store cycle uses the RAM data port for the write, so the instruction
directly after a store must not read memory (ops 13/14). The compilers always follow a store
with a plain DROP, which satisfies this.

**Memory timing.** RAM and I/O behave the same way: the CPU puts the *next* T on the data
address (`mem_addr`), and the value at that address is there during the following
instruction. The RAM does this with its address register; the I/O side registers the
address (`io_addr`) and selects the value combinationally in the next cycle. That keeps the
I/O decoding off the CPU's longest path (stack RAM → 16×16 multiplier → ALU → next
address), which is what allows 48 MHz.

## System

| Part | Implementation |
|---|---|
| Clock | 12 MHz oscillator (H6) → PLL: c1 = 48 MHz CPU clock (× 4), c0 = 10 MHz for the ADC (× 5 / 6) |
| Reset | held until the PLL locks plus 255 clocks. **Ctrl-R** (0x12) from the host, or a store to 0x8008, resets the CPU, the FIFOs, the SPI master, PWM and the I/O registers; the 0x12 is not passed on. The RAM keeps its contents |
| RAM | 12K × 16 as two 12K × 8 halves in M9K blocks (byte stores without byte enables); port A fetches instructions, port B is the data port. Both are initialized from `build/forth_lo.hex` / `forth_hi.hex` (`$readmemh`) |
| Configuration | the MAX 10 loads from its internal flash in mode "single image with ERAM", so the `.pof` carries the RAM contents (the kernel) |
| Stacks | Quartus maps the 32-entry data and return stacks into M9K blocks (their read addresses are registers) |
| UART | 115200 8N1 on FTDI channel B (FPGA TX = B4, RX = A4), 417 clocks per bit |
| FIFOs | RX 8 KB, TX 1 KB, first-word-fall-through in M9K (`rtl/fifo_bram.v`); a pushed byte is visible two clocks later |

### Start

`cold` (the reset vector jumps there) restores `dp` and `latest` from `dp0`/`latest0` (the
cross-compiler's values, so the dictionary is the kernel's again), clears the load and
answer state, counts the start (`starts`), sets the data RAM pointer, switches the
accelerometer on, prints the banner, lights LED1 and runs `autoload`, then `quit`.

## Memory map

| Address | Content |
|---|---|
| 0x0000 | reset vector: jump to `cold` |
| 0x0002.. | kernel: code, headers, variables (about 9.9 KB) |
| … `here` | user dictionary, grows up to 0x3FFF (`unused` shows what is left) |
| 0x4000–0x5BFF | data RAM, handed out by `buffer:` (`unused-data`) |
| 0x5C00 | `pbuf`, 256 bytes: one flash page for `store` |
| 0x5D00 | `tib`, 82 bytes: the input line |
| 0x5D60 | `rq`, 64 bytes: a service request |
| 0x5DA0 | `sbuf`, 64 bytes: `.s` parks the stack here |
| 0x5DE0 | `ssnap`, 40 bytes: the data stack at the last prompt (the `stack` request) |
| 0x5E10 | `hdr`, 16 bytes: the saved source's header |
| 0x5E20–0x5EFF | free (not handed out) |
| 0x5F00 | `lbuf`, 256 bytes: the piece of saved source being interpreted |

Dictionary entries: link (cell, address of the previous header, 0 ends the list), count
byte (bits 4..0 = name length, bit 7 = IMMEDIATE, bit 6 = HIDDEN, bit 5 = INLINE), the
name, padding to an even address, then the code (the execution token). Inline words are
copied into new definitions by the compiler up to their EXIT.

## I/O map

| Address | Read | Write |
|---|---|---|
| 0x8000 | next received byte (pops the RX FIFO) | send byte (pushes the TX FIFO) |
| 0x8002 | bit 0 = RX byte available, bit 1 = TX FIFO has space, bit 2 = TX idle | – |
| 0x8004 | user LEDs (bit 0 = LED1) | same |
| 0x8006 | bit 0 = user button pressed (sampled every 10 ms) | – |
| 0x8008 | – | any value: reset the CPU (as Ctrl-R) |
| 0x800A | millisecond counter (wraps) | – |
| 0x800C | next received byte without taking it (peek) | – |
| 0x800E | bytes waiting in the RX FIFO | – |
| 0x8010 + 8p | port p (0..1): pin levels, synchronized | – |
| 0x8012 + 8p | port p: output latch | same |
| 0x8014 + 8p | port p: direction, 1 = output (0 after reset) | same |
| 0x8020 | SPI: the byte received last | SPI: send a byte (starts an 8-bit exchange) |
| 0x8022 | bit 0 = flash selected, bit 1 = busy, bit 2 = accelerometer selected | bit 0 = select the flash, bit 1 = select the accelerometer |
| 0x8024 | – | reset the CRC |
| 0x8026 | – | add the low byte to the CRC |
| 0x8028, 0x802A | CRC-32 bits 15..0, 31..16 | – |
| 0x8040 + 2c | ADC channel c (0..8), 12-bit code | – |
| 0x8052 | temperature diode, 12-bit code (64-sample average) | – |
| 0x8080 + 8c | PWM channel c: period low 16 bits | same (held until the high half is written) |
| 0x8082 + 8c | PWM channel c: period high 16 bits (48 MHz clocks per cycle, at least 2) | same (takes both halves) |
| 0x8084 + 8c | PWM channel c: duty in 1/65536 of the period, 0xFFFF = always high | same |
| 0x8086 + 8c | PWM channel c: bit 15 on, bits 5..0 pin number | same |

### Pins

| Pin numbers | Port bits | Header | FPGA pins |
|---|---|---|---|
| 0..14 | port 0 bits 0..14 | D0..D14 | H8 K10 H5 H4 J1 J2 L12 J12 J13 K11 K12 J10 H10 H13 G12 |
| 16..23 | port 1 bits 0..7 | PIO_01..PIO_08 (Pmod) | M3 L3 M2 M1 N3 N2 K2 K1 |

Pin 15 and 24..31 do not exist (read 0, ignore writes); the word `pin` rejects them. Inputs
pass two flip-flops. No pull-ups by default, so an open input reads at random;
`scripts/build.ps1 -PullUps` builds weak pull-ups into D0..D14 and PIO1..PIO8 (the MAX 10
sets them in the configuration, so Forth cannot switch them). With LEDs to ground on a pin
(as on the bench: 1 kΩ on D3..D5) a pull-up keeps them faintly lit while the pin is an
input, which is why the option is off by default.

Other board pins: LEDs A8 A9 A11 A10 B10 C9 C10 D8 (LED1..LED8), user button E6 (low while
pressed), UART B4/A4, flash B3/A3/A2/B2, accelerometer L5/J6/J7/K5.

### PWM

`rtl/pwm.v` has four channels. Each counts 48 MHz clocks from 0 to period − 1 and drives its
output high for the first (period × duty) / 65536 clocks; the 32 × 16 product is pipelined
in two stages (multiplier blocks). New period and duty values are taken at the end of the
running cycle, so no pulse is cut short; enabling starts a fresh cycle. An enabled channel
owns its pin: the pin becomes an output with the channel's level (channels on the same pin
are ORed); disabled, the pin returns to its latch and direction. A CPU reset turns every
channel off. The ISS keeps the registers but has no time base: a PWM pin reads high there
only at full duty.

### ADC and temperature

`rtl/adc_scan.v` drives Intel's Modular ADC control core (`altera_modular_adc_control`, used
in place from the Quartus installation, no Platform Designer): 1 MSPS from a 10 MHz clock,
internal reference (VCCA = 3.3 V), channels CH8 down to CH0, then the temperature sensing
diode (TSD), again and again. Each analog channel is converted four times in a row and only
the last result (flagged with end-of-packet) is kept, because a high-impedance source does
not settle in one conversion after another channel; floating inputs show the charge left by
the channel before them. For the TSD the core switches the ADC to temperature mode (50 kSPS)
and back by itself and keeps a running average of the last 64 samples.

| Header | AIN | AIN0 | AIN1 | AIN2 | AIN3 | AIN4 | AIN5 | AIN6 | AIN7 |
|---|---|---|---|---|---|---|---|---|---|
| FPGA pin | D2 (ANAIN1) | E1 | C2 | C1 | D1 | E3 | E4 | F1 | B1 |
| ADC channel | 0 | 8 | 2 | 5 | 1 | 3 | 4 | 7 | 6 |
| `adc@` input | 8 | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 |

The channel numbers come from the Quartus pin report; AIN3 = CH1 was confirmed with a
potentiometer. The TSD code falls as the chip warms. `temp@` interpolates linearly between
two points of Intel's conversion table (−40 °C = 3798, 96 °C = 3510), so it is approximate;
`temp-code@` gives the raw code. The control core needs `analog_input_pin_mask` as an
integer (65791 = 0x100FF), not as a sized vector.

### SPI master, serial flash, accelerometer, CRC

`rtl/spi_flash.v` is a byte-wide SPI master: mode 0, SCK = CPU clock / 6 = 8 MHz (the LIS3DH
allows 10 MHz). A store to 0x8020 shifts a byte out and one in (busy for 48 clocks); Forth
drives the select bits and the commands. Two devices share the shift engine, each on its own pins: clock and data out
go to both, data in comes from the selected one.

- **Winbond W25Q64** serial flash, 8 MB, JEDEC ID EF 40 17 (F_S B3, F_CLK A3, F_DI A2,
  F_DO B2). The FPGA does not configure from it (the MAX 10 has internal flash), so all of
  it is available. Commands used: 03 read, 02 page program, 06 write enable, 05 status,
  20 4 KB erase, D8 64 KB erase, 9F ID.
- **LIS3DH** accelerometer (SEN_CS L5, SEN_SPC J6, SEN_SDI J7, SEN_SDO K5): the first byte
  is the register address with bit 7 = read and bit 6 = auto-increment. `accel-on` (run by
  `cold`) writes CTRL_REG1 = $57 (100 Hz, X/Y/Z) and CTRL_REG4 = $88 (block data update,
  high resolution, ±2 g); `accel@` reads OUT_X_L..OUT_Z_H in one burst ($E8) and shifts the
  left-justified 12-bit values right by 4, giving 1 mg per digit.
- **CRC-32** unit: standard reflected CRC-32 (polynomial 0xEDB88320, as zlib), one byte per
  clock; Forth feeds it byte by byte.

## Saved source

One Forth source lives in the serial flash, in the ZYNQ-FORTH format:

| Flash address | Content |
|---|---|
| 0x000000 (4 KB sector) | header, 16 bytes, little-endian: magic `HF16`, length (32 bits), CRC-32 of the text, flags (bit 0 = autostart) |
| 0x010000 (64 KB block) | the text, at most 65,535 bytes, lines end in CR |

**Saving.** `store ( len flags -- )` erases the header first (so an interrupted store leaves
no valid source), prints `ready`, and reads `len` raw bytes from the UART into `pbuf`,
erasing each 4 KB sector when it reaches it and programming one 256-byte page at a time.
A byte must arrive within 2 s. The CRC unit runs along. At the end it writes the header and
checks the text against its CRC (`source?`). The RX FIFO absorbs the erase and programming
times: 4 KB arrive in 355 ms, a sector takes about 45 ms to erase and 16 pages about 12 ms
to program. A 49 KB file takes about 5 s, which is UART time. `mforth.py --store` and the
app's `store` request use the same path.

**Boot.** `autoload` (in `cold`) reads the header and checks the text's CRC (`scan-source`,
which also caches the state for the `status` request). If autostart is on, it waits up to
200 ms for an ESC (peeking at 0x800C, so other bytes stay), then reads the user button —
after the wait, because the button is sampled every 10 ms and reads "not pressed" until the
first sample — and either skips or starts the load. `load-source` sets `fsrc` to the length.
From then on `key` takes its bytes from the saved source, and `accept-line` and `quit` print
neither echo nor `ok`. `fkey` copies the text into `lbuf` (0x5F00) 256 bytes at a time
(`lfill`: select the flash, read, deselect), so the flash is selected only while a piece is
copied and the source itself may use the flash and the accelerometer on any line. If the
last byte was not a CR, `key` supplies one. `abort` stops a running load and says
`(in the saved source)`.

## Service requests (the app)

The ZYNQ-FORTH app talks protocol 1 (ZYNQ-FORTH `docs/Protocol.md`): a request is SOH, a
line, LF; the answer is `SOH ok <name> <length> LF <payload>` or `SOH err <name> <reason> LF`;
a 0x01 in Forth's own output is sent twice. On the Zynq the ARM answers; here the kernel does.

**Where.** Requests arrive in the RX FIFO between terminal bytes. `service?` answers every
request at the head of the FIFO. `key?` calls it and decides on the head byte itself (peek
at 0x800C): a SOH there is served, anything else is terminal input, so a request can never
slip into the input line. `key`, `ms` and the wait in `emit` go through `key?`/`service?`,
so answers come at the prompt and whenever a program waits, polls `key?` or prints. A word
that only computes answers nothing until it ends; Ctrl-R always works because it is
hardware. A request queued behind terminal input waits until Forth has read that input (the
app therefore sends text in 4 KB pieces). `serving` keeps an answer from starting another,
and no request is answered while a word has the flash or the accelerometer selected (bit 0
or 2 of 0x8022): `board` reads the accelerometer and `source` reads the flash, which would
break a transfer that word has open. Such a word's requests wait until it deselects.

**Answers.** `answer ( xt -- )` runs the payload word twice: once with `counting` set,
where `emit` only counts bytes, then for real after the header with the length. `source`
uses the stored length directly (`answer-n`). The request line is read with a 2 s timeout
per byte; an unfinished one is dropped.

| Request | MAX1000-FORTH |
|---|---|
| `echo`, `words`, `stack`, `source`, `erase`, `autostart on\|off` | as ZYNQ-FORTH |
| `version` | `firmware max1000-forth 1.0`, `protocol 1`, `kernel <cells> <sum hex>` (KCHECK: the image's cell sum, set by the cross-compiler) |
| `status` | as ZYNQ-FORTH; `pc` is always 0000; `input` = RX FIFO bytes plus the rest of a saved source being read; the source fields come from the cached state |
| `board` | `board` (bits 7..0 LEDs, bit 16 user button), `ports` (2 words: pins 0–15 and 16–31; bits 15..0 levels, 31..16 driven), `pwm` (bits 3..0 enabled, 6 bits per channel's pin), `adc` (9 codes, channels 0..8), `temp` (TSD code), `accel` (x y z in mg, hex) |
| `store <len> <crc> on\|off` | erases the header, then 4 KB sectors as the payload arrives; refuses `args`, `timeout`, `crc`, `flash` |
| `restart [skip]` | answers, waits for TX idle, sets `skip1` and resets the CPU via 0x8008. The app sends Ctrl-R (+ESC) instead, which also works during a tight loop |

There is no HASE mode.

## Tools and tests

`tools/hf16.py build` cross-compiles the kernel: outside definitions it understands numbers,
`:`, `:inline`, `variable`, `constant`, `create`, `allot`, `,`, `c,`, `immediate` and
comments; inside definitions it compiles primitives inline and handles the control
structures, `."`, `s"`, `[char]`, `[']` and `[op]` itself; calls to words defined later are
allowed. It fills in the reset vector, `dp`/`latest` and their copies `dp0`/`latest0`, and
KCHECK, and writes `build/forth_lo.hex` and `forth_hi.hex` (24 KB, for the two RAM halves),
`forth.hex`, `forth_image.h` and `forth.map`.

`tools/hf16.py run` runs the kernel in an instruction-set simulator that mirrors the CPU
and models the I/O: UART (input lines or a file), LEDs, button (`--button`), ms timer
(1 ms per 100 instructions), pins, PWM registers, ADC codes (AIN3 = 1.65 V, AIN0 = 3.3 V,
temperature ≈ 34.6 °C), the W25Q64 (`--flash image`), the LIS3DH (z = 1000 mg), the CRC unit,
the RX count and the CPU reset.

| Test | Checks |
|---|---|
| `sim/run_flash_tests.ps1` | 23: flash ID, store, autostart, skips (button, ESC), listing, errors in the source, a source without final CR, flash and accelerometer words inside a saved source, 41 KB sources, erase |
| `python sim/test_protocol.py` | 39: every request, answers during `ms`, SOH doubling, dropped requests, store/restart cycles, the `board` lines, data RAM limits, `buffer:` alignment, no answer while the flash is selected |

## Timing and resources

48 MHz CPU clock; slack about 2 ns at the slow corner (1200 mV, 85 °C). The design uses about
4.1k of 8,064 logic elements, 272 kbit of the 378 kbit M9K RAM (24 KB RAM, 8 KB RX FIFO,
1 KB TX FIFO, the stacks) and 18 of 48 9-bit multiplier elements (CPU and PWM). Quartus
warns (16248) that the LED and UART pins sit next to ADC pins; they are fixed by the board.
