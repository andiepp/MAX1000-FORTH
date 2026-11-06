#!/usr/bin/env python3
"""Service-request tests (docs/Protocol.md of ZYNQ-FORTH) against the instruction-set
simulator: the kernel answers the app's requests itself.

    python sim/test_protocol.py
"""

import os
import sys
import zlib

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..')
sys.path.insert(0, os.path.join(ROOT, 'tools'))
import hf16  # noqa: E402

SOH = 1
failures = 0


def check(name, ok, detail=''):
    global failures
    print(('PASS  ' if ok else 'FAIL  ') + name + ('' if ok else f'\n      {detail}'))
    failures += 0 if ok else 1


class Board:
    def __init__(self):
        comp = hf16.Compiler()
        with open(os.path.join(ROOT, 'forth', 'kernel.fs'), encoding='ascii') as f:
            comp.compile(f.read())
        self.comp = comp
        self.m = hf16.Machine(comp.mem)
        self.taken = 0
        self.run()

    def run(self, steps=4_000_000, idle_limit=2000):
        self.m.idle_reads = 0
        self.m.run(self.m.steps + steps, idle_limit)

    def output(self):
        """New output since the last call, split into terminal text and answers."""
        data = bytes(self.m.tx[self.taken:])
        self.taken = len(self.m.tx)
        term, answers, events, i = bytearray(), [], [], 0
        while i < len(data):
            b = data[i]
            if b != SOH:
                term.append(b)
                i += 1
                continue
            if i + 1 < len(data) and data[i + 1] == SOH:
                term.append(SOH)
                i += 2
                continue
            end = data.index(b'\n', i)
            head = data[i + 1:end].decode('ascii').split(' ')
            i = end + 1
            payload = b''
            if head[0] == 'ok':
                n = int(head[2])
                payload = data[i:i + n]
                i += n
            answers.append((head, payload))
            events.append(('answer', head[1]))
            events.append(('term-pos', len(term)))
        return term.decode('ascii', 'replace'), answers, events

    def request(self, text, payload=b''):
        self.m.rx += bytes([SOH]) + text.encode('ascii') + b'\n' + payload
        self.run()
        return self.output()

    def type(self, line):
        self.m.rx += line.encode('ascii') + b'\r'
        self.run()
        return self.output()[0]


def one(answers):
    return answers[0] if len(answers) == 1 else (['?'], b'')


def lines(payload):
    return {l.split(' ')[0]: l.split(' ')[1:] for l in payload.decode('ascii').split('\n') if l}


b = Board()
term, _, _ = b.output()
check('banner', 'HF16 Forth on MAX1000' in term, term)

head, pl = one(b.request('echo hello')[1])
check('echo', head == ['ok', 'echo', '5'] and pl == b'hello', (head, pl))

head, pl = one(b.request('version')[1])
v = lines(pl)
image = b.comp.mem
kc = b.comp.lookup('kcheck')['xt'] + 4
total = (sum(image[a] | image[a + 1] << 8 for a in range(0, b.comp.dp, 2))
         - (image[kc] | image[kc + 1] << 8)) & 0xFFFF
check('version', head[:2] == ['ok', 'version'] and v['firmware'] == ['max1000-forth', '1.3']
      and v['protocol'] == ['1'] and v['kernel'][0] == str(b.comp.dp // 2)
      and int(v['kernel'][1], 16) == total, pl)

head, pl = one(b.request('status')[1])
st = lines(pl)
check('status at start', st['state'] == ['running'] and st['starts'] == ['1'] and st['source'] == ['none']
      and st['last'] == ['none'] and st['input'] == ['0'] and st['autostart'] == ['off'], pl)

b.type('1 2 -3')
head, pl = one(b.request('stack')[1])
check('stack', pl == b'3 0001 0002 FFFD\n', pl)
b.type('drop drop drop')
head, pl = one(b.request('stack')[1])
check('empty stack', pl == b'0\n', pl)

head, pl = one(b.request('words')[1])
wl = pl.decode().split('\n')
names = {l.split(' ')[2]: l.split(' ')[1] for l in wl[1:] if l}
check('words', wl[0] == f"here {b.comp.dp:04X}" and names.get('dup') == 'n' and names.get('if') == 'i'
      and names.get('cold') == '-' and len(names) == len(b.comp.words), wl[:3])

b.type(': sq dup * ;')
head, pl = one(b.request('words')[1])
check('new word first', pl.decode().split('\n')[1].endswith(' - sq'), pl[:60])

head, pl = one(b.request('board')[1])
bd = lines(pl)
check('board at start', bd['board'] == ['00000001'] and bd['pwm'] == ['00000000']
      and len(bd['ports']) == 2 and len(bd['adc']) == 9 and bd['adc'][1] == '0800'
      and bd['adc'][8] == '0FFF', pl)

check('board: temperature and acceleration', bd.get('temp') == ['0E38']
      and bd.get('accel') == ['0000', '0000', '03E8'], pl)
check('board: no pin functions at start', bd.get('fn') == ['0000'] * 12, pl)

# 24 KB RAM: data RAM up to 0x5FFF, stores above it are ignored; BUFFER: hands out data RAM
term = b.type('$5BFE @ . 1234 $5BFE ! $5BFE @ . 99 $6000 ! $6000 @ .')
check('data RAM and its end', '1234 0' in term, term)
term = b.type('20 buffer: buf  buf . unused-data .')
check('buffer:', '16384 7148' in term, term)
term = b.type('3 buffer: odd  4 buffer: next  next odd - .')
check('buffer: keeps cells aligned', 'next odd - . 4' in term, term)

# a request arriving while a word holds the flash selected waits until it lets go
b.m.rx += b': rd 0 0 f-read 300 ms 0 spi f-end . ;\r'
b.run()
b.output()
b.m.rx += b'rd\r' + bytes([SOH]) + b'board\n'
b.run()
term, answers, events = b.output()
pos = dict(e for e in events if e[0] == 'term-pos')
check('no answer while the flash is selected', len(answers) == 1 and
      term[:pos.get('term-pos', 0)].strip().startswith('rd 255'), (term, events))

# ... and KEY? answers false meanwhile instead of looping (it used to hang there)
b.m.rx += b': kq 0 0 f-read 0 begin 1+ key? over 50 = or until f-end . ;\r'
b.run()
b.output()
b.m.rx += b'kq\r' + bytes([SOH]) + b'board\n'
b.run()
term, answers, events = b.output()
pos = dict(e for e in events if e[0] == 'term-pos')
check('KEY? false while a request waits for the flash', len(answers) == 1 and
      term[:pos.get('term-pos', 0)].strip().startswith('kq 50'), (term, events))

# single LEDs: LED1..LED8 = bits 0..7 of LED!
term = b.type('0 led! 1 led-on 8 led-on 3 led-on led@ .')
term += b.type('1 led-off 3 led-toggle 2 led-toggle led@ .')
check('led-on led-off led-toggle', 'led@ . 133' in term and 'led@ . 130' in term, term)
check('bad led', 'bad led' in b.type('0 led-on') and 'bad led' in b.type('9 led-off'))
check('led words keep the others', '130' in b.type('led@ .'))

# status: input counts the terminal bytes behind the request
b.m.rx += bytes([SOH]) + b'status\n' + b'7 .\r'
b.run()
term, answers, _ = b.output()
st = lines(one(answers)[1])
check('status input', st['input'] == ['4'] and '7 . 7' in term, (st, term))

# store lengths: decimal 1..65535 only (a bigger one used to wrap)
for arg in ('65536', '70000', '99999', '1x', '-5', '$100'):
    check(f'store {arg} refused', one(b.request(f'store {arg} 00000000 on')[1])[0] == ['err', 'store', 'args'])

b.type('3 output 3 high 1000 0 pwm-hz 500 0 pwm-duty 5 0 pwm-pin $a5 led!')
head, pl = one(b.request('board')[1])
bd = lines(pl)
check('board pins/pwm/leds', bd['board'] == ['000000A5'] and bd['ports'][0] == '00280048'
      and bd['pwm'] == ['00000051'], pl)

# DDS: tuning words as Python computes them; its pin shows as driven; off again
term = b.type('1000 dds-khz 9 dds-pin')
head, pl = one(b.request('board')[1])
bd = lines(pl)
check('board: DDS on pin 9 at 1 MHz', bd.get('dds') == ['8009', f'{(10**6 * 2**32 + 96 * 10**6) // (192 * 10**6):08X}']
      and int(bd['ports'][0], 16) >> 16 & 0x0200, pl)
term = b.type('dds-off 1 dds-hz $8060 @ . 65535 dds-khz $8062 @ . $8060 @ .')
t1, t2 = (2**32 + 96 * 10**6) // (192 * 10**6), (65535000 * 2**32 + 96 * 10**6) // (192 * 10**6)
lo = t2 & 0xFFFF
lo = lo - 0x10000 if lo & 0x8000 else lo          # `.` prints cells signed
check('DDS tuning at 1 Hz and 65535 kHz', f'{t1} {t2 >> 16} {lo}' in term, (term, t1, t2))

# pin functions of the serial interfaces; their output pins show as driven
b.type('115200. 0 1 1 uart-open 13 11 12 0 spi-open 100 14 16 i2c-open')
head, pl = one(b.request('board')[1])
bd = lines(pl)
drv = int(bd['ports'][0], 16) >> 16
check('board: pin functions', bd.get('fn') == ['0000', '0000', '8000', '8001', '800D', '800B',
      '800C', '0000', '0000', '0000', '800E', '8010']
      and drv & 0x2801 == 0x2801 and drv & 0x5002 == 0, pl)
b.type('1 uart-close 0 spi-close i2c-close')
head, pl = one(b.request('board')[1])
check('board: pin functions closed', lines(pl).get('fn') == ['0000'] * 12, pl)

text = b': hi 42 ;\r'
crc = zlib.crc32(text)
head, pl = one(b.request(f'store {len(text)} {crc:08X} on', text)[1])
check('store', head == ['ok', 'store', '0'], head)
head, pl = one(b.request('status')[1])
st = lines(pl)
check('status after store', st['source'] == ['stored'] and st['length'] == [str(len(text))]
      and st['crc'] == [f'{crc:08X}'] and st['autostart'] == ['on'], pl)
head, pl = one(b.request('source')[1])
check('source', head == ['ok', 'source', str(len(text))] and pl == text, (head, pl))

head, pl = one(b.request(f'store {len(text)} {crc ^ 1:08X} on', text)[1])
check('store with a wrong CRC', head == ['err', 'store', 'crc'], head)
st = lines(one(b.request('status')[1])[1])
check('no source after a bad store', st['source'] == ['none'], st)
head, pl = one(b.request('source')[1])
check('source refused', head == ['err', 'source', 'none'], head)

one(b.request(f'store {len(text)} {crc:08X} on', text)[1])
term, answers, _ = b.request('restart')
check('restart', one(answers)[0] == ['ok', 'restart', '0'] and 'HF16 Forth on MAX1000' in term, (answers, term))
st = lines(one(b.request('status')[1])[1])
check('after restart: source ran', st['starts'] == ['2'] and st['last'] == ['fed'], st)
check('word from the saved source', '42' in b.type('hi .'))
check('dictionary fresh after restart', 'sq ?' in b.type('sq'))

# a restart clears the RX FIFO: what was queued behind the request is gone
b.m.rx += bytes([SOH]) + b'restart\n' + b'nosuchword\r'
b.run()
term, answers, _ = b.output()
check('restart clears the input', one(answers)[0] == ['ok', 'restart', '0'] and 'nosuchword' not in term, (answers, term))

term, answers, _ = b.request('restart skip')
st = lines(one(b.request('status')[1])[1])
check('restart skip', st['starts'] == ['4'] and st['last'] == ['skipped'], st)
check('skipped source not run', 'hi ?' in b.type('hi'))

check('autostart off', one(b.request('autostart off')[1])[0] == ['ok', 'autostart', '0'])
b.request('restart')
st = lines(one(b.request('status')[1])[1])
check('no autostart', st['autostart'] == ['off'] and st['last'] == ['none'] and st['source'] == ['stored'], st)
check('autostart bad args', one(b.request('autostart maybe')[1])[0] == ['err', 'autostart', 'args'])

check('erase', one(b.request('erase')[1])[0] == ['ok', 'erase', '0'])
st = lines(one(b.request('status')[1])[1])
check('erased', st['source'] == ['none'] and st['length'] == ['0'], st)
check('autostart without source', one(b.request('autostart on')[1])[0] == ['err', 'autostart', 'none'])

check('unknown request', one(b.request('frobnicate now')[1])[0] == ['err', 'frobnicate', 'unknown'])

# an answer while Forth waits in MS: it comes before the line's " ok"
b.m.rx += b'300 ms\r' + bytes([SOH]) + b'status\n'
b.run()
term, answers, events = b.output()
pos = dict(e for e in events if e[0] == 'term-pos')
check('answer during ms', len(answers) == 1 and ' ok' not in term[:pos.get('term-pos', 0)]
      and ' ok' in term, (term, events))

# a SOH printed by Forth is doubled and arrives as terminal text
term = b.type('1 emit 2 emit')
check('SOH doubled', '\x01\x02' in term, repr(term))

# an unfinished request is dropped after 2 s; the terminal goes on
b.m.rx += bytes([SOH]) + b'status'
b.run(400_000, idle_limit=10**9)      # 2 s of simulated time (1 ms per 100 instructions)
term = b.type('7 .')
check('unfinished request dropped', '7 . 7' in term and not b.output()[1], repr(term))

# a big store, several 4 KB sectors
big = b''.join(b'\\ filler %04d %s\r' % (i, b'x' * 60) for i in range(200)) + b': big 99 ;\r'
head, _ = one(b.request(f'store {len(big)} {zlib.crc32(big):08X} on', big)[1])
check('store 15 KB', head == ['ok', 'store', '0'], head)
b.request('restart')
check('15 KB source ran', 'big . 99' in b.type('big .'))

print('all protocol checks passed' if failures == 0 else f'{failures} check(s) failed')
sys.exit(1 if failures else 0)
