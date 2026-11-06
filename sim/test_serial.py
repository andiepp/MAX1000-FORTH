#!/usr/bin/env python3
"""Tests of the serial interfaces (UART, SPI, I2C), pin ownership and double numbers
against the instruction-set simulator, with jumper wires and an I2C register memory
modelled at the transfer level (tools/hf16.py).

    python sim/test_serial.py
"""

import os
import sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..')
sys.path.insert(0, os.path.join(ROOT, 'tools'))
import hf16  # noqa: E402

failures = 0


def check(name, ok, detail=''):
    global failures
    print(('PASS  ' if ok else 'FAIL  ') + name + ('' if ok else f'\n      {detail!r}'))
    failures += 0 if ok else 1


class Board:
    def __init__(self, wires=(), i2c=()):
        comp = hf16.Compiler()
        with open(os.path.join(ROOT, 'forth', 'kernel.fs'), encoding='ascii') as f:
            comp.compile(f.read())
        self.m = hf16.Machine(comp.mem)
        self.m.wires = list(wires)
        for a in i2c:
            self.m.i2c_devs[a] = bytearray(256)
        self.taken = 0
        self.run()
        self.out()

    def run(self, steps=4_000_000):
        self.m.idle_reads = 0
        self.m.run(self.m.steps + steps, 2000)

    def out(self):
        data = bytes(self.m.tx[self.taken:])
        self.taken = len(self.m.tx)
        return data.decode('ascii', 'replace')

    def type(self, line):
        assert len(line) <= 80, line
        self.m.rx += line.encode('ascii') + b'\r'
        self.run()
        return self.out()


# --- double numbers ----------------------------------------------------------
b = Board()
t = b.type('115200. . . 65535. . . $10000. . . 1. . .')
check('double literals', '1 -15872 0 -1 1 0 0 1' in t, t)
t = b.type(': br 115200. ; br swap u. u.')
check('double literal compiled', 'br swap u. u. 49664 1' in t, t)
t = b.type('1x. .')
check('bad double literal', '1x. ?' in t, t)
t = b.type('12 . -3 . $1F . .s')
check('single numbers and . still work', '12 -3 31 <0>' in t, t)

# --- pin ownership -----------------------------------------------------------
t = b.type('5 0 pwm-pin 5 1 pwm-pin')
check('PWM pin taken', 'pin taken' in t, t)
t = b.type('5 owner . 0 pwm-off 5 owner . 5 1 pwm-pin 5 owner .')
check('PWM owner, released, retaken', 'owner . 1 0 2' in t, t)
t = b.type('115200. 6 5 0 uart-open')
check('UART refuses a PWM pin', 'pin taken' in t, t)
t = b.type('6 owner . 1 pwm-off 5 dds-pin 5 owner . 6 dds-pin 5 owner . 6 owner .')
check('nothing half claimed; DDS moves', 'owner . 0 10 0 10' in t, t)
t = b.type('dds-off 6 owner . 15 owner')
check('DDS released; no pin 15', 'owner 0  bad pin' in t, t)

# --- UART ---------------------------------------------------------------------
b = Board(wires=[(0, 1)])
t = b.type('115200. 0 1 0 uart-open $80A4 @ . $80A6 @ . $80A8 @ .')
check('UART divider 417, pins', '417 0 -32512' in t, t)
t = b.type('0 uart-key? . 65 0 uart-emit 0 uart-key? . 0 uart-key . 0 uart-key? .')
check('UART loopback D0-D1', 'uart-key? . 0 -1 65 0' in t, t)
t = b.type('create m 104 c, 105 c,  m 2 0 uart-type 0 uart-key emit 0 uart-key emit')
check('uart-type', t.rstrip().endswith('hi ok') and bytes(b.m.uart_log[0]).endswith(b'Ahi'), t)
t = b.type('0 uart-status . 0 owner . 1 owner .')
check('UART status idle, owners', 'owner . 4 5 5' in t, t)
t = b.type('9600. 2 1 1 uart-open')
check('UART 1 refused on a UART 0 pin', 'pin taken' in t, t)
# UART 0 sends on D0, UART 1 receives on D1 (joined to D0)
t = b.type('0 uart-close 115200. 0 3 0 uart-open 9600. 2 1 1 uart-open $80B4 @ .')
check('9600 Bd divider', 'uart-open $80B4 @ . 5000' in t, t)
t = b.type('66 0 uart-emit 1 uart-key? . 1 uart-status .')
check('other rate: framing error', 'uart-status . 0 20' in t, t)
t = b.type('1 uart-status . 115200. 2 1 1 uart-open 67 0 uart-emit 1 uart-key .')
check('status cleared; UART 0 to UART 1', t.rstrip().endswith('uart-key . 4 67  ok'), t)
t = b.type('1 uart-close 0. 0 3 0 uart-open')
t += b.type('2. 0 3 0 uart-open')
t += b.type('115200. 0 3 2 uart-open')
check('bad baud, bad uart', t.count('bad baud') == 2 and 'bad uart' in t, t)
for baud, div in ((300, 160000), (921600, 52), (3000000, 16), (1000000, 48), (57600, 833)):
    t = b.type(f'{baud}. 0 3 0 uart-open $80A4 @ u. $80A6 @ u.')
    check(f'divider at {baud} Bd', f'{div & 0xFFFF} {div >> 16}' in t, t)
b.type('115200. 0 3 0 uart-open')
b.m.uart_feed(3, b'xyz')
t = b.type('0 uart-key emit 0 uart-key emit 0 uart-key emit')
check('bytes from outside', t.rstrip().endswith('xyz ok'), t)

# --- SPI ----------------------------------------------------------------------
b = Board(wires=[(11, 12)])
t = b.type('13 11 12 0 spi-open $A5 0 spi-xfer . $80C6 @ .')
check('SPI loopback D11-D12, 1 MHz', '165 24  ok' in t, t)
t = b.type('16 0 spi-bits $1234 0 spi-xfer . 12 0 spi-bits')
check('16 bits; bad bits', '4660' in t and 'bad bits' in t, t)
t = b.type('8 0 spi-bits 3 0 spi-mode -1 0 spi-lsb $80C4 @ . 0 0 spi-lsb $80C4 @ .')
check('mode and LSB first in control', 'spi-lsb $80C4 @ . 7 3' in t, t)
t = b.type('7000 0 spi-khz $80C6 @ . 12000 0 spi-khz $80C6 @ . 1 0 spi-khz $80C6 @ .')
check('spi-khz rounds the clock down', '4 2 24000' in t, t)
t = b.type('20000 0 spi-khz')
check('spi-khz range', 'bad frequency' in t, t)
t = b.type('create sb 97 c, 98 c, 99 c, 100 c,  sb 4 0 spi-write')
t += b.type('sb 4 0 spi-read sb c@ . sb 3 + c@ .')
check('spi-write, spi-read', b.m.spi_log[0][-8:] == [97, 98, 99, 100, 0, 0, 0, 0]
      and t.rstrip().endswith('0 0  ok'), (b.m.spi_log[0], t))
t = b.type('1 2 6 1 spi-open $3C 1 spi-xfer .')
check('SPI 1: open MISO reads its idle level', 'spi-xfer . 255' in t, t)
t = b.type('7 12 3 1 spi-open')
check('SPI 1 refused on SPI 0 pin', 'pin taken' in t, t)
t = b.type('1 owner . 6 owner . 7 owner . $80D8 @ .')
check('refused re-open keeps the old pins', '8 8 0 -32255' in t, t)
t = b.type('0 spi-close 13 owner . 1 owner . 2 spi-xfer')
check('SPI closed; bad spi', 'spi-xfer 0 8  bad spi' in t, t)

# --- I2C ----------------------------------------------------------------------
b = Board(i2c=[0x50])
t = b.type('100 14 16 i2c-open $80E2 @ . $80E4 @ . 14 owner . 16 owner .')
check('I2C open: 100 kHz, pins', '120 -28658 9 9' in t, t)
t = b.type('$50 i2c-probe . $51 i2c-probe .')
check('i2c-probe', 'probe . -1 0' in t, t)
t = b.type('$5A 3 $50 i2c! $C3 4 $50 i2c! 3 $50 i2c@ . 4 $50 i2c@ .')
check('i2c! i2c@', 'i2c@ . 90 195' in t and b.m.i2c_devs[0x50][3:5] == b'\x5a\xc3', t)
t = b.type('i2c-scan')
check('i2c-scan', 'i2c-scan 50' in t, t)
t = b.type('3 $33 i2c@')
check('no ack', 'no ack' in t, t)
t = b.type('i2c-close 400 0 1 i2c-open $80E2 @ . 2000 0 1 i2c-open')
check('400 kHz; range', ' 30  bad frequency' in t, t)
t = b.type('14 owner . 0 owner .')
check('I2C moved pins', 'owner . 0 9' in t, t)

# --- a restart frees every pin ---------------------------------------------------
b.m.rx += b'\x12'
b.m.restart_pending = True
b.run()
b.out()
t = b.type('0 owner . 1 owner . $80E4 @ .')
check('restart clears owners and units', '@ . 0 0 0' in t, t)

t = b.type('3 0 pwm-pin 5 dds-pin 115200. 0 1 0 uart-open 13 11 12 1 spi-open')
t = b.type('cold')
t = b.type('3 owner . 0 owner . 13 owner . $8086 @ . $8064 @ . $80A8 @ . $80D8 @ .')
check('typed cold switches the units off', '@ . 0 0 0 0 0 0 0' in t, t)

print('all serial checks passed' if failures == 0 else f'{failures} check(s) failed')
sys.exit(1 if failures else 0)
