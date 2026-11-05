# MAX1000-FORTH design

The CPU, its instruction set and the kernel come from ZYNQ-FORTH; its `docs/design.md`
describes the machine model and the instruction encoding, which are the same here. This
document covers what is different on the MAX1000.

## System

| Part | Implementation |
|---|---|
| Clock | 12 MHz oscillator → PLL: c1 = 40 MHz CPU clock (12 × 10 / 3), c0 = 10 MHz for the ADC |
| Reset | held until the PLL locks, plus 255 clocks; **Ctrl-R** (0x12) from the host resets the CPU, FIFOs and I/O (the byte is not passed on). The RAM keeps its contents; `cold` restores `dp`/`latest` from `dp0`/`latest0` (set by the cross-compiler), so the dictionary is the kernel's again |
| RAM | 8K × 16 as two 8K × 8 halves in M9K blocks (byte stores without byte enables); port A fetches instructions, port B is the data port. Both halves are initialized from `build/forth_lo.hex` / `forth_hi.hex` |
| UART | 115200 8N1 on FTDI channel B (FPGA TX = B4, RX = A4); RX FIFO 16 KB, TX FIFO 1 KB, in M9K. The large RX FIFO takes a whole `store` payload while the flash erases |
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
| 0x8002 | bit 0 = RX byte available, bit 1 = TX FIFO has space, bit 2 = TX idle (FIFO empty, last byte sent) | – |
| 0x8004 | user LEDs (8 bits, bit 0 = LED1) | same |
| 0x8006 | bit 0 = user button pressed (sampled every 10 ms) | – |
| 0x8008 | – | any value: reset the CPU (as Ctrl-R) |
| 0x800A | millisecond counter (wraps) | – |
| 0x800C | next received byte without taking it (peek) | – |
| 0x800E | bytes waiting in the RX FIFO | – |
| 0x8010 + 8p | port p (0..1) pin levels, synchronized | – |
| 0x8012 + 8p | port p output latch | same |
| 0x8014 + 8p | port p direction, 1 = output (0 after reset) | same |
| 0x8020 | SPI: byte received last | SPI: send a byte (starts an exchange) |
| 0x8022 | bit 0 = flash selected, bit 1 = SPI busy | bit 0 = select the flash (F_S low) |
| 0x8024 | – | reset the CRC |
| 0x8026 | – | add the low byte to the CRC |
| 0x8028, 0x802A | CRC-32 bits 15..0, 31..16 | – |
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

## Serial flash and saved source

The MAX1000 carries a Winbond W25Q64 (8 MB, JEDEC ID EF 40 17) on F_S = B3, F_CLK = A3,
F_DI = A2, F_DO = B2. The FPGA does not configure from it (the MAX 10 has its own flash), so
all of it is available. `rtl/spi_flash.v` is a byte-wide SPI master: mode 0, SCK = 10 MHz
(CPU clock / 4). Forth drives the chip select and the commands (03 read, 02 page program,
06 write enable, 05 status, 20 4 KB erase, D8 64 KB erase, 9F ID). The same block has a
CRC-32 unit (standard reflected CRC-32, as zlib computes it) that Forth feeds byte by byte.

Saved source, in the ZYNQ-FORTH format:

| Flash address | Content |
|---|---|
| 0x000000 (4 KB sector) | header, 16 bytes, little-endian: magic `HF16`, length (32 bits), CRC-32 of the text, flags (bit 0 = autostart) |
| 0x010000 (64 KB block) | the text, at most 65,535 bytes, lines end in CR |

`store ( len flags -- )` first erases the header (so an interrupted store leaves no valid
source), then the text block, prints `ready`, and reads `len` raw bytes from the UART into a
256-byte buffer, programming one page at a time. The 1 KB RX FIFO covers programming time
easily: a page arrives in 22 ms and takes about 1 ms to program. At the end it writes the
header and checks the whole text against its CRC (`source?`). A 49 KB file takes 4.9 s, which
is mostly UART time.

**Boot.** `cold` resets the dictionary and prints the banner. Then `autoload` checks the
header and the CRC; if autostart is on, it waits up to 200 ms for an ESC, then looks at the
user button, and either skips or calls `load-source`. The button has to be read after that
wait: it is sampled every 10 ms and reads "not pressed" until the first sample. `load-source` starts a flash read and
sets `fsrc` to the length. From then on `key` takes its bytes from the flash, and
`accept-line` and `quit` print neither echo nor `ok`. When the last byte is read, the flash
is deselected; if that byte was not a CR, `key` supplies one. `abort` stops a running load
and says `(in the saved source)`. The flash stays selected while the source is read, so the
saved source must not use the flash words itself, except on its last line.

Tests: `sim/run_flash_tests.ps1` runs 21 checks in the instruction-set simulator, which
models the W25Q64 commands and the CRC unit (`tools/hf16.py run --flash image --button`).

## Service requests (the app)

The ZYNQ-FORTH app talks protocol 1 (ZYNQ-FORTH `docs/Protocol.md`): a request is SOH, a
line, LF; the answer is `SOH ok <name> <length> LF <payload>` or `SOH err <name> <reason>
LF`; a 0x01 in Forth's own output is sent twice. On the Zynq the ARM answers; here the
kernel does.

**Where.** Requests arrive in the RX FIFO between terminal bytes. `service?` answers every
request at the head of the FIFO. `key?` calls it and decides on the head byte itself (peek
at 0x800C), so a SOH can never be taken as terminal input. `key`, `ms` and the wait in
`emit` go through `key?`/`service?`, so answers come at the prompt and whenever a program
waits, polls `key?` or prints. A request queued behind terminal input waits until Forth has
read that input. `serving` keeps an answer from starting another one.

**Answers.** `answer ( xt -- )` runs the payload word twice: once with `counting` set,
where `emit` only counts bytes, then for real after the header with the length. `source`
uses the stored length directly (`answer-n`). The request line is read with a 2 s timeout
per byte; an unfinished one is dropped.

| Request | MAX1000-FORTH |
|---|---|
| `echo`, `words`, `stack`, `source`, `erase`, `autostart on\|off` | as ZYNQ-FORTH |
| `version` | `firmware max1000-forth 1.0`, `protocol 1`, `kernel <cells> <sum hex>` (KCHECK, set by the cross-compiler) |
| `status` | as ZYNQ-FORTH; `pc` is always 0000; `input` = RX FIFO bytes plus the rest of a saved source being read; the source fields come from the state cached at the last start/store/erase/autostart |
| `board` | `board` (bits 7..0 LEDs, bit 16 user button), `ports` (2 words: pins 0–15, 16–31; levels, driven), `pwm` (as ZYNQ-FORTH), `adc` (9 codes, channels 0..8) |
| `store <len> <crc> on\|off` | erases the header, then 4 KB sectors as the payload arrives; refuses `args`, `timeout` (2 s without a byte), `crc`, `flash` |
| `restart [skip]` | answers, waits for TX idle, sets `skip1` and resets the CPU via 0x8008. The app uses Ctrl-R (+ESC) instead, which also works during a tight loop |

There is no HASE mode. Tests: `sim/test_protocol.py` (34 checks against the ISS, which
models the RX count, the TX idle bit and the CPU reset).

## Board facts

- The on-board **Reset** button is the MAX 10's nCONFIG (E7): it reloads the configuration
  from internal flash, so it is not available as an input.
- The LEDs and UART pins sit next to the ADC pins; Quartus warns about it (16248). They are
  fixed by the board.
- `analog_input_pin_mask` of the ADC control core must be an integer (65791 = 0x100FF).
