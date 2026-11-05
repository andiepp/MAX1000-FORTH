#!/usr/bin/env python3
"""Talk to MAX1000-FORTH over the serial port.

    python tools/mforth.py -p COM5 "2 3 + ."              send lines, print the answers
    python tools/mforth.py -p COM5 -f examples/x.fs       send a source file line by line
    python tools/mforth.py -p COM5 --store examples/x.fs  save a file in the board's flash
                                                          (runs at power-up; --no-autostart)
    python tools/mforth.py -p COM5 --restart ...          Ctrl-R first (fresh Forth)
    python tools/mforth.py -p COM5 --skip ...             Ctrl-R + ESC: fresh Forth without
                                                          running the saved source

Each line is sent with a CR; the tool waits until Forth has answered it (the answer
ends in CR LF) before sending the next one, so long files do not overrun the input
FIFO. Lines may hold at most 80 characters. Needs pyserial.
"""

import argparse
import sys
import time

import serial


def out(text):
    """Print board output; CR LF becomes the console's own line end."""
    sys.stdout.write(text.replace('\r\n', '\n'))
    sys.stdout.flush()


def read_answer(port, timeout):
    """Read until a CR LF that ends Forth's answer to one line, or timeout."""
    buf = bytearray()
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        chunk = port.read(port.in_waiting or 1)
        if chunk:
            buf += chunk
            end = time.monotonic() + timeout
            if buf.endswith(b'\r\n'):
                break
    return buf.decode('ascii', 'replace')


def read_until(port, marks, timeout):
    """Read until one of the marks appears (or timeout); returns the text."""
    buf = bytearray()
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        chunk = port.read(port.in_waiting or 1)
        if chunk:
            buf += chunk
            text = buf.decode('ascii', 'replace')
            if any(m in text for m in marks) and buf.endswith(b'\r\n'):
                return text
    return buf.decode('ascii', 'replace')


def source_lines(path):
    with open(path, encoding='ascii') as f:
        lines = [l.rstrip('\r\n') for l in f]
    for n, l in enumerate(lines, 1):
        if len(l) > 80:
            sys.exit(f'{path}:{n}: line longer than 80 characters')
    return lines


def store(port, path, autostart):
    text = ''.join(l + '\r' for l in source_lines(path)).encode('ascii')
    if len(text) > 65535:
        sys.exit(f'{path}: {len(text)} bytes, at most 65535 fit')
    port.write(f'{len(text)} {1 if autostart else 0} store\r'.encode('ascii'))
    answer = read_until(port, ['ready'], 5.0)
    out(answer)
    if 'ready' not in answer:
        sys.exit('no "ready" from the board')
    port.write(text)
    answer = read_until(port, ['stored', 'failed'], 10.0)
    out(answer)
    if 'stored' not in answer:
        sys.exit('store failed')


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('lines', nargs='*')
    ap.add_argument('-p', '--port', default='COM5')
    ap.add_argument('-f', '--file', help='send this source file line by line')
    ap.add_argument('--store', metavar='FILE', help='save this source file in the flash')
    ap.add_argument('--no-autostart', action='store_true',
                    help='with --store: do not run it at power-up')
    ap.add_argument('--restart', action='store_true', help='send Ctrl-R first')
    ap.add_argument('--skip', action='store_true',
                    help='send Ctrl-R and ESC first (restart without the saved source)')
    ap.add_argument('-t', '--timeout', type=float, default=1.0,
                    help='seconds of silence that end an answer (default 1)')
    args = ap.parse_args()

    lines = list(args.lines)
    if args.file:
        lines = source_lines(args.file) + lines
    for l in lines:
        if len(l) > 80:
            sys.exit(f'line longer than 80 characters: {l}')

    with serial.Serial(args.port, 115200, timeout=0.05) as port:
        port.reset_input_buffer()
        if args.restart or args.skip:
            port.write(b'\x12\x1b' if args.skip else b'\x12')
            out(read_until(port, ['ok', 'skipped', 'MAX1000'], 1.5))
            out(read_answer(port, 0.5))
        if args.store:
            store(port, args.store, not args.no_autostart)
        for line in lines:
            port.write(line.encode('ascii') + b'\r')
            out(read_answer(port, args.timeout))


if __name__ == '__main__':
    main()
