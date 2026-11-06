#!/usr/bin/env python3
"""Writes the test data of the app's decompiler (app/tests/.../Fixtures/see.json): the kernel
runs in the simulator, the definitions below are typed in, and the RAM image, the `words`
answer and the expected source of each definition are saved.

    python tools/make_see_fixture.py
"""

import json
import os
import sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..')
sys.path.insert(0, os.path.join(ROOT, 'tools'))
import hf16  # noqa: E402

# (typed definition, expected decompiled source)
CASES = [
    (': sq dup * ;', ': sq dup * ;'),
    (': tst 0< if ." neg" else ." pos" then cr ;', ': tst 0< if ." neg" else ." pos" then cr ;'),
    (': cnt 10 0 do i . loop ;', ': cnt 10 0 do i . loop ;'),
    (': cnt2 100 0 do i . 10 +loop ;', ': cnt2 100 0 do i . 10 +loop ;'),
    (': wt begin key? until ;', ': wt begin key? until ;'),
    (': rp begin dup while 1- repeat drop ;', ': rp begin dup while 1- repeat drop ;'),
    (': fv begin 1 drop again ;', ': fv begin 1 drop again ;'),
    (': st 5 $5000 ! 7 $5002 c! ;', ': st 5 $5000 ! 7 $5002 c! ;'),
    (': early dup 0= if drop exit then 1- ;', ': early dup 0= if drop exit then 1- ;'),
    (': big $8004 @ -300 + ;', ': big $8004 @ -300 + ;'),
    (": tk ['] sq execute ;", ": tk ['] sq execute ;"),
    (': nst 0< if 1 if 2 then then ;', ': nst 0< if 1 if 2 then then ;'),
    (': lp 3 0 do i 0= if 9 . then loop ;', ': lp 3 0 do i 0= if 9 . then loop ;'),
    (': rs >r r@ r> + ;', ': rs >r r@ r> + ;'),
    ('variable vv', 'variable vv   \\ now 0'),
    ('create cc 4 allot', 'create cc  4 allot'),
    ('42 constant ans', '42 constant ans'),
    (': imm 1 ; immediate', ': imm 1 ; immediate'),
]

# kernel words (compiled by the cross-compiler, which turns a final call into a jump and
# merges the return into the last instruction)
KERNEL = [
    ('led-on', ': led-on led-mask led@ or led! ;'),
    ('bset', ': bset dup @ rot or swap ! ;'),
    ('ms', ': ms ms@ + begin service? dup ms@ - 0< until drop ;'),
    ('pin!', ': pin! swap if high else low then ;'),
]


def main():
    comp = hf16.Compiler()
    with open(os.path.join(ROOT, 'forth', 'kernel.fs'), encoding='ascii') as f:
        comp.compile(f.read())
    m = hf16.Machine(comp.mem)
    m.rx += b''.join(line.encode('ascii') + b'\r' for line, _ in CASES)
    m.rx += b'\x01words\n'
    m.run(20_000_000)
    out = bytes(m.tx)
    i = out.index(b'\x01ok words ')
    head_end = out.index(b'\n', i)
    n = int(out[i:head_end].split()[2])
    words = out[head_end + 1:head_end + 1 + n].decode('ascii')
    here = int(words.split('\n')[0].split()[1], 16)
    data = {
        'words': words,
        'memory': m.mem[:here].hex(),
        'cases': [{'name': src.split()[1] if src.startswith(':') else src.split()[-1] if 'constant' in src
                   else src.split()[1], 'expect': exp} for src, exp in CASES]
                 + [{'name': name, 'expect': exp} for name, exp in KERNEL],
    }
    # names of the typed cases
    for case, (src, _) in zip(data['cases'], CASES):
        toks = src.split()
        case['name'] = toks[1] if toks[0] in (':', 'variable', 'create') else toks[2]
    path = os.path.join(ROOT, 'app', 'tests', 'ZynqForth.Core.Tests', 'Fixtures', 'see.json')
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        json.dump(data, f, indent=1)
    print(f'{path}: {len(data["cases"])} cases, HERE ${here:04X}')


if __name__ == '__main__':
    main()
