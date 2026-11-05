#!/usr/bin/env python3
"""Send Forth lines to MAX1000-FORTH over the serial port and print the answers.

    python tools/mforth.py -p COM5 "2 3 + ."            one or more lines
    python tools/mforth.py -p COM5 -f examples/x.fs     a source file, line by line
    python tools/mforth.py -p COM5 --restart ...        Ctrl-R first (fresh Forth)

Each line is sent with a CR; the tool waits until Forth has answered it (the
answer ends in CR LF) before sending the next one, so long files do not overrun
the input FIFO. Needs pyserial.
"""

import argparse
import sys
import time

import serial


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


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('lines', nargs='*')
    ap.add_argument('-p', '--port', default='COM5')
    ap.add_argument('-f', '--file')
    ap.add_argument('--restart', action='store_true', help='send Ctrl-R first')
    ap.add_argument('-t', '--timeout', type=float, default=1.0,
                    help='seconds of silence that end an answer (default 1)')
    args = ap.parse_args()

    lines = list(args.lines)
    if args.file:
        with open(args.file, encoding='ascii') as f:
            lines += [l.rstrip('\r\n') for l in f]

    with serial.Serial(args.port, 115200, timeout=0.05) as port:
        port.reset_input_buffer()
        if args.restart:
            port.write(b'\x12')
            sys.stdout.write(read_answer(port, 0.5))
            sys.stdout.write(read_answer(port, 0.5))
        for line in lines:
            if len(line) > 80:
                sys.exit(f'line longer than 80 characters: {line}')
            port.write(line.encode('ascii') + b'\r')
            sys.stdout.write(read_answer(port, args.timeout))
            sys.stdout.flush()


if __name__ == '__main__':
    main()
