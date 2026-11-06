# Changelog

## 1.1 — unreleased

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
