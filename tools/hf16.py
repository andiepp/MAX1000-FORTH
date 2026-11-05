#!/usr/bin/env python3
"""HF16 cross-compiler and instruction-set simulator.

    python tools/hf16.py build forth/kernel.fs -o build/forth
        writes build/forth_lo.hex and build/forth_hi.hex (low and high bytes of each
        cell, for $readmemh into the two RAM halves), build/forth.hex (whole cells),
        build/forth_image.h (addresses for host tools) and build/forth.map

    python tools/hf16.py run forth/kernel.fs [-i "2 3 + ."] [--steps N]
        compiles and runs the image in the simulator, feeding the input text as
        terminal keystrokes (a CR is sent after each line) and printing the output

The machine model and encodings are described in docs/design.md.
"""

import argparse
import binascii
import os
import re
import sys

RAM_BYTES = 16384        # code and the dictionary (jumps and calls reach 16 KB)
MEM_BYTES = 24576        # the whole RAM: 0x4000..0x5FFF is data only

# ---------------------------------------------------------------------------
# Instruction encoding
# ---------------------------------------------------------------------------

OPS = {
    'T': 0, 'N': 1, 'N+T': 2, 'N&T': 3, 'N|T': 4, 'N^T': 5, '~T': 6, 'N=T': 7,
    'N<T': 8, 'Nu<T': 9, 'N>>T': 10, 'N<<T': 11, 'R': 12, '[T]': 13, 'c[T]': 14,
    'depth': 15, 'N-T': 16, 'T+1': 17, 'T-1': 18, 'T=0': 19, 'T<0': 20, 'N*T': 21,
    'T/2': 22, '-T': 23, 'N*Thi': 24,
}
DELTA = {0: 0, 1: 1, -1: 3, -2: 2}

F_IMMEDIATE, F_HIDDEN, F_INLINE = 0x80, 0x40, 0x20


def alu(op, r2p=False, t2n=False, n2m=False, byte=False, rd=0, dd=0):
    return (0x6000 | (r2p << 12) | (OPS[op] << 7) | (t2n << 6) | (n2m << 5)
            | (byte << 4) | (DELTA[rd] << 2) | DELTA[dd])


EXIT = alu('T', r2p=True, rd=-1)
INVERT = alu('~T')

PRIMS = {
    'dup':    [alu('T', dd=1)],
    'drop':   [alu('N', dd=-1)],
    'swap':   [alu('N', t2n=True)],
    'over':   [alu('N', dd=1)],
    'nip':    [alu('T', dd=-1)],
    '>r':     [alu('N', rd=1, dd=-1)],
    'r>':     [alu('R', rd=-1, dd=1)],
    'r@':     [alu('R', dd=1)],
    '+':      [alu('N+T', dd=-1)],
    '-':      [alu('N-T', dd=-1)],
    'and':    [alu('N&T', dd=-1)],
    'or':     [alu('N|T', dd=-1)],
    'xor':    [alu('N^T', dd=-1)],
    '=':      [alu('N=T', dd=-1)],
    '<':      [alu('N<T', dd=-1)],
    'u<':     [alu('Nu<T', dd=-1)],
    'rshift': [alu('N>>T', dd=-1)],
    'lshift': [alu('N<<T', dd=-1)],
    '*':      [alu('N*T', dd=-1)],
    '(um*)':  [alu('N*Thi', dd=-1)],     # high cell of the unsigned product
    'invert': [alu('~T')],
    '1+':     [alu('T+1')],
    '1-':     [alu('T-1')],
    '0=':     [alu('T=0')],
    '0<':     [alu('T<0')],
    '2/':     [alu('T/2')],
    'negate': [alu('-T')],
    '@':      [alu('[T]')],
    'c@':     [alu('c[T]')],
    # A store is always followed by a plain DROP (store hazard, see design.md)
    '!':      [alu('N', n2m=True, dd=-1), alu('N', dd=-1)],
    'c!':     [alu('N', n2m=True, byte=True, dd=-1), alu('N', dd=-1)],
    'depth':  [alu('depth', dd=1)],
    'exit':   [EXIT],
}


def literal(n):
    n &= 0xFFFF
    if n < 0x8000:
        return [0x8000 | n]
    return [0x8000 | (~n & 0x7FFF), INVERT]


# ---------------------------------------------------------------------------
# Cross-compiler
# ---------------------------------------------------------------------------

class CompileError(Exception):
    pass


class Compiler:
    def __init__(self):
        self.mem = bytearray(RAM_BYTES)
        self.dp = 0
        self.latest = 0            # header address of the newest word
        self.words = {}            # lower-case name -> dict(xt, header)
        self.stack = []            # host data stack (interpret mode)
        self.cstack = []           # control-flow stack (compile mode)
        self.targets = set()       # byte addresses that are branch targets
        self.fixups = []           # (cell address, name, opcode bits) for forward calls
        self.current = None        # word being compiled
        self.last_call = None      # address of the last emitted call
        self.src = ''
        self.pos = 0
        self.line = 1

    # --- memory ---------------------------------------------------------
    def c_comma(self, b):
        if self.dp >= RAM_BYTES:
            raise CompileError('image larger than RAM')
        self.mem[self.dp] = b & 0xFF
        self.dp += 1

    def align(self):
        if self.dp & 1:
            self.c_comma(0)

    def comma(self, w):
        self.align()
        self.c_comma(w)
        self.c_comma(w >> 8)

    def cell(self, addr):
        return self.mem[addr] | (self.mem[addr + 1] << 8)

    def set_cell(self, addr, w):
        self.mem[addr] = w & 0xFF
        self.mem[addr + 1] = (w >> 8) & 0xFF

    def emit(self, insns):
        for w in insns:
            self.last_call = None
            self.comma(w)

    def emit_call(self, xt):
        self.comma(0x4000 | (xt >> 1))
        self.last_call = self.dp - 2

    # --- source scanning ------------------------------------------------
    def token(self):
        s = self.src
        while self.pos < len(s) and s[self.pos].isspace():
            if s[self.pos] == '\n':
                self.line += 1
            self.pos += 1
        start = self.pos
        while self.pos < len(s) and not s[self.pos].isspace():
            self.pos += 1
        return s[start:self.pos] if self.pos > start else None

    def parse(self, delim):
        """Text up to delim (like Forth PARSE); skips one leading blank."""
        if self.pos < len(self.src) and self.src[self.pos] in ' \t':
            self.pos += 1
        end = self.src.find(delim, self.pos)
        if end < 0:
            raise CompileError(f'missing {delim!r}')
        text = self.src[self.pos:end]
        self.line += text.count('\n')
        self.pos = end + 1
        return text

    def need(self, what):
        t = self.token()
        if t is None:
            raise CompileError(f'{what} expected')
        return t

    # --- dictionary -----------------------------------------------------
    def header(self, name, flags=0):
        if len(name) > 31:
            raise CompileError(f'name too long: {name}')
        self.align()
        h = self.dp
        self.comma(self.latest)
        self.c_comma(len(name) | flags)
        for ch in name.encode('ascii'):
            self.c_comma(ch)
        self.align()
        self.latest = h
        xt = self.dp
        self.words[name.lower()] = {'xt': xt, 'header': h, 'name': name}
        return xt

    def lookup(self, name):
        return self.words.get(name.lower())

    @staticmethod
    def number(tok):
        m = re.fullmatch(r'(-?)(\$[0-9a-fA-F]+|[0-9]+)', tok)
        if not m:
            return None
        digits = m.group(2)
        val = int(digits[1:], 16) if digits.startswith('$') else int(digits)
        return -val if m.group(1) else val

    # --- compile mode ---------------------------------------------------
    def mark_target(self):
        self.targets.add(self.dp)

    def finish_definition(self):
        inline = self.current['inline']
        here = self.dp
        if not inline and here not in self.targets and here > self.current['xt']:
            last = self.cell(here - 2)
            if self.last_call == here - 2:
                # tail call: call -> jump
                self.set_cell(here - 2, last & 0x1FFF)
                self.last_call = None
                return
            if (last & 0xE000) == 0x6000 and not (last & 0x1000) and (last & 0x000C) == 0:
                self.set_cell(here - 2, last | 0x1000 | (DELTA[-1] << 2))
                return
        self.emit([EXIT])

    def compile_token(self, tok):
        low = tok.lower()
        cs = self.cstack

        if low == ';':
            if cs:
                raise CompileError(f'unbalanced control structure in {self.current["name"]}')
            self.finish_definition()
            self.current = None
        elif low in ('\\', '('):
            self.comment(low)
        elif low == 'if':
            cs.append(('orig', self.dp))
            self.emit([0x2000])
        elif low == 'else':
            kind, orig = cs.pop()
            cs.append(('orig', self.dp))
            self.emit([0x0000])
            self.resolve(orig)
        elif low == 'then':
            kind, orig = cs.pop()
            self.resolve(orig)
        elif low == 'begin':
            self.mark_target()
            cs.append(('dest', self.dp))
        elif low == 'until':
            kind, dest = cs.pop()
            self.emit([0x2000 | (dest >> 1)])
        elif low == 'again':
            kind, dest = cs.pop()
            self.emit([dest >> 1])
        elif low == 'while':
            dest = cs.pop()
            cs.append(('orig', self.dp))
            cs.append(dest)
            self.emit([0x2000])
        elif low == 'repeat':
            kind, dest = cs.pop()
            self.emit([dest >> 1])
            kind, orig = cs.pop()
            self.resolve(orig)
        elif low == 'do':
            self.emit(PRIMS['swap'] + PRIMS['>r'] + PRIMS['>r'])
            self.mark_target()
            cs.append(('do', self.dp))
        elif low in ('loop', '+loop'):
            kind, dest = cs.pop()
            self.call_word('(loop)' if low == 'loop' else '(+loop)')
            self.emit([0x2000 | (dest >> 1)])
            self.emit(PRIMS['r>'] + PRIMS['drop'] + PRIMS['r>'] + PRIMS['drop'])
        elif low == 'i':
            self.emit(PRIMS['r@'])
        elif low == 'unloop':
            self.emit(PRIMS['r>'] + PRIMS['drop'] + PRIMS['r>'] + PRIMS['drop'])
        elif low == 'recurse':
            self.emit_call(self.current['xt'])
        elif low == '."':
            text = self.parse('"')
            self.call_word('(.")')
            self.c_comma(len(text))
            for ch in text.encode('ascii'):
                self.c_comma(ch)
            self.align()
        elif low == 's"':
            text = self.parse('"')
            self.call_word('(s")')
            self.c_comma(len(text))
            for ch in text.encode('ascii'):
                self.c_comma(ch)
            self.align()
        elif low == '[char]':
            self.emit(literal(ord(self.need('character'))))
        elif low == "[']":
            name = self.need('name')
            w = self.lookup(name)
            if not w:
                raise CompileError(f"['] of unknown word {name}")
            self.emit(literal(w['xt']))
        elif low == '[op]':
            name = self.need('primitive').lower()
            if name not in PRIMS:
                raise CompileError(f'[op] of non-primitive {name}')
            self.emit(literal(PRIMS[name][0]))
        elif low in PRIMS:
            self.emit(PRIMS[low])
        elif self.number(tok) is not None:
            self.emit(literal(self.number(tok)))
        else:
            self.call_word(tok)

    def resolve(self, orig):
        self.set_cell(orig, self.cell(orig) | (self.dp >> 1))
        self.mark_target()

    def call_word(self, name):
        w = self.lookup(name)
        if w:
            self.emit_call(w['xt'])
        else:
            self.fixups.append((self.dp, name, self.line))
            self.emit_call(0)

    def comment(self, low):
        if low == '\\':
            end = self.src.find('\n', self.pos)
            self.pos = len(self.src) if end < 0 else end
        else:
            self.parse(')')

    # --- interpret mode -------------------------------------------------
    def interpret_token(self, tok):
        low = tok.lower()
        st = self.stack
        if low in (':', ':inline'):
            name = self.need('name')
            flags = F_INLINE if low == ':inline' else 0
            xt = self.header(name, flags)
            self.current = {'name': name, 'xt': xt, 'inline': bool(flags)}
            self.last_call = None
        elif low in ('\\', '('):
            self.comment(low)
        elif low == 'immediate':
            self.mem[self.latest + 2] |= F_IMMEDIATE
        elif low in ('variable', 'create'):
            xt = self.header(self.need('name'))
            self.emit(literal(xt + 4) + [EXIT])
            if low == 'variable':
                self.comma(0)
        elif low == 'constant':
            self.header(self.need('name'))
            self.emit(literal(st.pop()) + [EXIT])
        elif low == 'allot':
            for _ in range(st.pop()):
                self.c_comma(0)
        elif low == ',':
            self.comma(st.pop())
        elif low == 'c,':
            self.c_comma(st.pop())
        elif self.number(tok) is not None:
            st.append(self.number(tok))
        else:
            raise CompileError(f'unknown word outside a definition: {tok}')

    # --- driver ---------------------------------------------------------
    def compile(self, src):
        self.src, self.pos, self.line = src, 0, 1
        # Reset vector: jump to COLD, patched at the end
        self.emit([0x0000])
        while True:
            tok = self.token()
            if tok is None:
                break
            try:
                if self.current:
                    self.compile_token(tok)
                else:
                    self.interpret_token(tok)
            except (CompileError, IndexError) as e:
                raise CompileError(f'line {self.line}: {tok}: {e}') from None
        if self.current:
            raise CompileError(f'unterminated definition {self.current["name"]}')

        for addr, name, line in self.fixups:
            w = self.lookup(name)
            if not w:
                raise CompileError(f'line {line}: undefined word {name}')
            self.set_cell(addr, self.cell(addr) | (w['xt'] >> 1))

        cold = self.lookup('cold')
        if not cold:
            raise CompileError('no COLD word')
        self.set_cell(0, cold['xt'] >> 1)

        # Initial values of the dictionary pointers
        self.align()
        for var, value in (('dp', self.dp), ('latest', self.latest),
                           ('dp0', self.dp), ('latest0', self.latest)):
            w = self.lookup(var)
            if not w:
                raise CompileError(f'kernel must define variable {var}')
            self.set_cell(w['xt'] + 4, value)

        # KCHECK: the sum of the image's cells (with KCHECK itself 0), for the version answer
        w = self.lookup('kcheck')
        if w:
            self.set_cell(w['xt'] + 4, 0)
            total = sum(self.cell(a) for a in range(0, self.dp, 2)) & 0xFFFF
            self.set_cell(w['xt'] + 4, total)
        return self.dp


# ---------------------------------------------------------------------------
# Instruction-set simulator (mirrors rtl/hf16_cpu.v)
# ---------------------------------------------------------------------------

# MAX1000 pins 0..14 = D0..D14, 16..23 = PIO_01..PIO_08. Open inputs read low here,
# except D6, which has the bench button's pull-up.
PIN_EXISTS = 0x00FF_7FFF
PIN_IDLE = 0x0000_0040

# Simulated ADC codes by ADC channel (12 bits, 3.3 V full scale): CH1 = AIN3 = the
# bench potentiometer at 1.65 V, CH8 = AIN0 at 3.3 V, the rest 0 V.
ADC_SIM = {1: 2048, 8: 4095, 9: 3640}     # slot 9: the temperature diode, about 34.6 C

# LIS3DH model: WHO_AM_I and the output registers (X = 0, Y = 0, Z = 1000 mg, 12 bits
# left-justified); other registers keep what is written
ACC_SIM = {0x0F: 0x33, 0x28: 0x00, 0x29: 0x00, 0x2A: 0x00, 0x2B: 0x00, 0x2C: 0x80, 0x2D: 0x3E}

# Serial flash model (W25Q64): JEDEC ID, status, write enable, read, page program,
# 4 KB / 64 KB erase. FLASH_SAVE bytes are kept in the --flash image file.
FLASH_BYTES = 0x800000
FLASH_SAVE = 0x20000


class Machine:
    def __init__(self, mem, rx=b''):
        self.mem = bytearray(mem) + bytearray(MEM_BYTES - len(mem))
        self.pc = 0
        self.t = 0
        self.dsp = 0
        self.rsp = 0
        self.ds = [0] * 32
        self.rs = [0] * 32
        self.rx = bytearray(rx)
        self.tx = bytearray()
        self.leds = 0
        self.buttons = 0
        self.ms = 0
        self.steps = 0
        self.idle_reads = 0
        self.restart_pending = False
        self.starts = 1
        self.pin_out = 0
        self.pin_dir = 0
        self.adc = [0] * 32
        for ch, value in ADC_SIM.items():
            self.adc[ch] = value
        self.flash = bytearray(b'\xff' * FLASH_BYTES)
        self.spi_cs = False
        self.acc_cs = False
        self.acc = dict(ACC_SIM)
        self.acc_n = 0
        self.acc_addr = 0
        self.spi_rx = 0
        self.wel = False
        self.fl = {'n': 0, 'cmd': 0, 'addr': 0}
        self.crc = 0
        # PWM channel c: [period low as written, period (32 bits), duty, control]
        self.pwm = [[0, 0, 0, 0] for _ in range(4)]

    def pwm_pins(self):
        """Pins owned by an enabled PWM channel, and their level: the ISS has no time
        base for the waveform, so a pin reads high only at full duty (0xFFFF)."""
        owns = level = 0
        for _, _, duty, control in self.pwm:
            if control & 0x8000:
                bit = 1 << (control & 0x3F)
                owns |= bit
                if duty == 0xFFFF:
                    level |= bit
        return owns, level

    def pins(self):
        """Pin levels: driven pins read back their latch (or their PWM channel), inputs
        see PIN_IDLE."""
        owns, level = self.pwm_pins()
        out = (self.pin_out & ~owns) | (level & owns)
        drive = self.pin_dir | owns
        return ((out & drive) | (PIN_IDLE & ~drive)) & PIN_EXISTS

    def spi_select(self, on):
        if on and not self.spi_cs:
            self.fl = {'n': 0, 'cmd': 0, 'addr': 0}
        elif not on and self.spi_cs:
            cmd, n, a = self.fl['cmd'], self.fl['n'], self.fl['addr'] & 0x7FFFFF
            if cmd in (0x20, 0xD8) and n >= 4 and self.wel:
                size = 0x1000 if cmd == 0x20 else 0x10000
                a &= ~(size - 1)
                self.flash[a:a + size] = b'\xff' * size
            if cmd in (0x02, 0x20, 0xD8) and n >= 4:
                self.wel = False
        self.spi_cs = on

    def spi_xfer(self, b):
        if not self.spi_cs:
            return 0xFF
        st = self.fl
        n = st['n']
        st['n'] += 1
        if n == 0:
            st['cmd'] = b
            if b == 0x06:
                self.wel = True
            return 0xFF
        cmd = st['cmd']
        if cmd == 0x9F:
            return (0xEF, 0x40, 0x17)[min(n - 1, 2)]
        if cmd == 0x05:
            return 0x02 if self.wel else 0x00
        if cmd in (0x03, 0x02, 0x20, 0xD8):
            if n <= 3:
                st['addr'] = (st['addr'] << 8) | b
                return 0xFF
            a = st['addr'] & 0x7FFFFF
            k = n - 4
            if cmd == 0x03:
                return self.flash[(a + k) & 0x7FFFFF]
            if cmd == 0x02 and self.wel:
                a = (a & 0x7FFF00) | ((a + k) & 0xFF)     # wraps within the page
                self.flash[a] &= b
        return 0xFF

    def acc_xfer(self, b):
        """LIS3DH: the first byte is the register address (bit 7 read, bit 6 auto-increment)."""
        n = self.acc_n
        self.acc_n += 1
        if n == 0:
            self.acc_addr = b
            return 0xFF
        reg = (self.acc_addr & 0x3F) + ((n - 1) if self.acc_addr & 0x40 else 0)
        if self.acc_addr & 0x80:
            return self.acc.get(reg, 0)
        self.acc[reg] = b
        return 0xFF

    def rd16(self, a):
        if a & 0x8000:
            return self.io_read(a)
        a &= 0x7FFE
        if a >= MEM_BYTES:
            return 0
        return self.mem[a] | (self.mem[a + 1] << 8)

    def io_read(self, a):
        if a & 0xC0 == 0x40:
            return self.adc[(a >> 1) & 31]
        if a & 0xE0 == 0x80:
            _, period, duty, control = self.pwm[(a >> 3) & 3]
            return [period & 0xFFFF, period >> 16, duty, control][(a >> 1) & 3]
        port = ((a >> 3) & 7) - 2
        if a & 0xC0 == 0 and 0 <= port <= 1:
            shift, sel = 16 * port, (a >> 1) & 3
            vec = [self.pins(), self.pin_out, self.pin_dir, 0][sel]
            return ((vec & PIN_EXISTS) >> shift) & 0xFFFF
        if a & 0xF0 == 0x20:
            reg = (a >> 1) & 7
            return {0: self.spi_rx, 1: int(self.spi_cs) | (4 if self.acc_cs else 0), 4: self.crc & 0xFFFF,
                    5: self.crc >> 16}.get(reg, 0)
        if a & 0xF0:
            return 0
        reg = (a >> 1) & 7
        if reg == 0 or reg == 6:
            return self.rx[0] if self.rx else 0
        if reg == 1:
            if not self.rx:
                self.idle_reads += 1
            return (1 if self.rx else 0) | 2 | 4
        if reg == 2:
            return self.leds
        if reg == 3:
            return self.buttons
        if reg == 5:
            return self.ms & 0xFFFF
        if reg == 7:
            return min(len(self.rx), 0x7FFF)
        return 0

    def store(self, a, v, byte):
        if a & 0x8000:
            if a & 0xE0 == 0x80:
                ch, sel, v = self.pwm[(a >> 3) & 3], (a >> 1) & 3, v & 0xFFFF
                if sel == 0:
                    ch[0] = v
                elif sel == 1:
                    ch[1] = (v << 16) | ch[0]
                else:
                    ch[sel] = v
                return
            port = ((a >> 3) & 7) - 2
            if a & 0xC0 == 0 and 0 <= port <= 1:
                shift, sel, mask = 16 * port, (a >> 1) & 3, 0xFFFF << (16 * port)
                if sel == 1:
                    self.pin_out = (self.pin_out & ~mask) | ((v & 0xFFFF) << shift)
                elif sel == 2:
                    self.pin_dir = (self.pin_dir & ~mask) | ((v & 0xFFFF) << shift)
                return
            if a & 0xF0 == 0x20:
                reg = (a >> 1) & 7
                if reg == 0:
                    self.spi_rx = self.acc_xfer(v & 0xFF) if self.acc_cs else self.spi_xfer(v & 0xFF)
                elif reg == 1:
                    self.spi_select(bool(v & 1))
                    if (v & 2) and not self.acc_cs:
                        self.acc_n = 0
                    self.acc_cs = bool(v & 2)
                elif reg == 2:
                    self.crc = 0
                elif reg == 3:
                    self.crc = binascii.crc32(bytes([v & 0xFF]), self.crc)
                return
            if a & 0xF0:
                return
            reg = (a >> 1) & 7
            if reg == 0:
                self.tx.append(v & 0xFF)
                self.idle_reads = 0          # printing is not waiting for input
            elif reg == 2:
                self.leds = v & 0xFF
            elif reg == 4:
                self.restart_pending = True
            return
        a &= 0x7FFF
        if a >= MEM_BYTES:
            return
        if byte:
            self.mem[a] = v & 0xFF
        else:
            a &= 0x7FFE
            self.mem[a] = v & 0xFF
            self.mem[a + 1] = (v >> 8) & 0xFF

    def step(self):
        a = (self.pc << 1) & 0x3FFE
        insn = self.mem[a] | (self.mem[a + 1] << 8)
        t, n, r = self.t, self.ds[self.dsp], self.rs[self.rsp]
        pc1 = (self.pc + 1) & 0x1FFF
        self.steps += 1
        if self.steps % 100 == 0:          # nominal timer: 1 ms per 100 instructions
            self.ms += 1

        if insn & 0x8000:
            self.dsp = (self.dsp + 1) & 31
            self.ds[self.dsp] = t
            self.t = insn & 0x7FFF
            self.pc = pc1
            return
        kind = (insn >> 13) & 3
        target = insn & 0x1FFF
        if kind == 0:
            self.pc = target
        elif kind == 1:
            self.t = n
            self.dsp = (self.dsp - 1) & 31
            self.pc = target if t == 0 else pc1
        elif kind == 2:
            self.rsp = (self.rsp + 1) & 31
            self.rs[self.rsp] = (pc1 << 1) & 0xFFFF
            self.pc = target
        else:
            op = (insn >> 7) & 31
            sgn = lambda x: x - 0x10000 if x & 0x8000 else x
            flag = lambda c: 0xFFFF if c else 0
            if op == 0:    v = t
            elif op == 1:  v = n
            elif op == 2:  v = n + t
            elif op == 3:  v = n & t
            elif op == 4:  v = n | t
            elif op == 5:  v = n ^ t
            elif op == 6:  v = ~t
            elif op == 7:  v = flag(n == t)
            elif op == 8:  v = flag(sgn(n) < sgn(t))
            elif op == 9:  v = flag(n < t)
            elif op == 10: v = 0 if t > 15 else n >> t
            elif op == 11: v = 0 if t > 15 else n << t
            elif op == 12: v = r
            elif op == 13:
                v = self.rd16(t & 0xFFFE)
                if t == 0x8000 and self.rx:
                    self.rx.pop(0)
            elif op == 14:
                w = self.rd16(t & 0xFFFE)
                v = (w >> 8) if t & 1 else (w & 0xFF)
                if t == 0x8000 and self.rx:
                    self.rx.pop(0)
            elif op == 15: v = self.dsp
            elif op == 16: v = n - t
            elif op == 17: v = t + 1
            elif op == 18: v = t - 1
            elif op == 19: v = flag(t == 0)
            elif op == 20: v = flag(t & 0x8000)
            elif op == 21: v = n * t
            elif op == 22: v = (t >> 1) | (t & 0x8000)
            elif op == 23: v = -t
            elif op == 24: v = (n * t) >> 16
            else:          v = t
            v &= 0xFFFF

            if insn & 0x20:
                self.store(t, n, bool(insn & 0x10))
            dd = {0: 0, 1: 1, 3: -1, 2: -2}[insn & 3]
            rd = {0: 0, 1: 1, 3: -1, 2: -2}[(insn >> 2) & 3]
            new_dsp = (self.dsp + dd) & 31
            new_rsp = (self.rsp + rd) & 31
            if (insn & 0x40) or dd == 1:
                self.ds[new_dsp] = t
            if rd == 1:
                self.rs[new_rsp] = t
            self.pc = (r >> 1) & 0x1FFF if insn & 0x1000 else pc1
            self.dsp, self.rsp, self.t = new_dsp, new_rsp, v

    def restart(self):
        """What a CPU reset does (Ctrl-R or a store to 0x8008); the RAM keeps its contents."""
        self.pc = self.t = self.dsp = self.rsp = 0
        self.leds = self.pin_out = self.pin_dir = 0
        self.pwm = [[0, 0, 0, 0] for _ in range(4)]
        self.spi_select(False)
        self.restart_pending = False
        self.starts += 1

    def run(self, max_steps, idle_limit=2000):
        """Run until max_steps, or until all input is consumed and the program
        has polled an empty RX FIFO idle_limit times (waiting at KEY)."""
        while self.steps < max_steps:
            self.step()
            if self.restart_pending:
                self.restart()
            if not self.rx and self.idle_reads > idle_limit:
                break


# ---------------------------------------------------------------------------
# Outputs
# ---------------------------------------------------------------------------

def write_outputs(comp, size, base):
    os.makedirs(os.path.dirname(base) or '.', exist_ok=True)
    words = [comp.cell(a) for a in range(0, RAM_BYTES, 2)] + [0] * ((MEM_BYTES - RAM_BYTES) // 2)
    with open(base + '.hex', 'w') as f:
        for w in words:
            f.write(f'{w:04x}\n')
    for suffix, shift in (('_lo.hex', 0), ('_hi.hex', 8)):
        with open(base + suffix, 'w') as f:
            for w in words:
                f.write(f'{(w >> shift) & 0xFF:02x}\n')
    used = (size + 1) // 2
    with open(base + '_image.h', 'w') as f:
        f.write('/* Generated by tools/hf16.py from forth/kernel.fs. Do not edit. */\n')
        f.write('#define FORTH_IMAGE_WORDS %d\n' % used)
        # byte addresses of the cells holding the newest header and HERE, for the ARM's
        # dictionary walk
        f.write('#define FORTH_LATEST_ADDR 0x%04x\n' % (comp.lookup('latest')['xt'] + 4))
        f.write('#define FORTH_DP_ADDR 0x%04x\n' % (comp.lookup('dp')['xt'] + 4))
        # and of the data stack copy QUIT makes before each line, for the stack view
        f.write('#define FORTH_SDEPTH_ADDR 0x%04x\n' % (comp.lookup('sdepth')['xt'] + 4))
        f.write('#define FORTH_SSNAP_ADDR 0x%04x\n' % (comp.lookup('ssnap')['xt'] + 4))
        f.write('static const unsigned short forth_image[FORTH_IMAGE_WORDS] = {\n')
        for i in range(0, used, 8):
            f.write('    ' + ', '.join(f'0x{w:04x}' for w in words[i:min(i + 8, used)]) + ',\n')
        f.write('};\n')
    with open(base + '.map', 'w') as f:
        for w in sorted(comp.words.values(), key=lambda w: w['xt']):
            f.write(f"{w['xt']:04x}  {w['name']}\n")


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest='cmd', required=True)
    b = sub.add_parser('build')
    b.add_argument('source')
    b.add_argument('-o', '--output', default='build/forth')
    r = sub.add_parser('run')
    r.add_argument('source')
    r.add_argument('-i', '--input', action='append', default=[],
                   help='a line of terminal input (repeatable)')
    r.add_argument('-f', '--input-file', help='file with lines of terminal input')
    r.add_argument('--steps', type=int, default=20_000_000)
    r.add_argument('--flash', help='flash image file (loaded if present, saved after the run)')
    r.add_argument('--button', action='store_true', help='hold the user button')
    args = ap.parse_args()

    comp = Compiler()
    with open(args.source, encoding='ascii') as f:
        src = f.read()
    try:
        size = comp.compile(src)
    except CompileError as e:
        sys.exit(f'{args.source}: {e}')

    if args.cmd == 'build':
        write_outputs(comp, size, args.output)
        print(f'HF16: {size} bytes ({size * 100 // RAM_BYTES}% of RAM), '
              f'{len(comp.words)} words -> {args.output}_lo/_hi.hex, .hex, _image.h, .map')
    else:
        lines = list(args.input)
        if args.input_file:
            with open(args.input_file, encoding='ascii') as f:
                lines += f.read().splitlines()
        rx = ''.join(line + '\r' for line in lines).encode('ascii')
        m = Machine(comp.mem, rx)
        if args.button:
            m.buttons = 1
        if args.flash and os.path.exists(args.flash):
            with open(args.flash, 'rb') as f:
                data = f.read()
            m.flash[:len(data)] = data
        m.run(args.steps)
        if args.flash:
            with open(args.flash, 'wb') as f:
                f.write(m.flash[:FLASH_SAVE])
        sys.stdout.write(m.tx.decode('ascii', 'replace'))
        print(f'\n[{m.steps} instructions, dsp={m.dsp}, rsp={m.rsp}, leds={m.leds:08b}]')


if __name__ == '__main__':
    main()
