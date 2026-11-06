"""Draws the app icon (also the FPGA-FORTH16 desktop shortcut's): an FPGA chip with pins, "16" in blue and "FORTH" above it,
the 16-bit sibling of the ZYNQ-FORTH32 / DualCore icons (orange "32" / "2x32").
Small sizes (16..32 px) get their own simplified drawing: fewer pins, only a big "16".
    python app/tools/make_icon.py app/src/ZynqForth/ZynqForth.ico"""
import sys
from PIL import Image, ImageDraw, ImageFont

PIN = (176, 184, 196, 255)
BODY = (24, 32, 46, 255)
EDGE = (40, 150, 255, 255)        # ZYNQ-FORTH blue (the FORTH32 icons use orange)
EDGE_DARK = (22, 96, 180, 255)
TEXT_MAIN = (88, 182, 255, 255)
TEXT_DIM = (210, 218, 230, 255)
DOT = (90, 100, 118, 255)


def font(size):
    for name in ('consolab.ttf', 'arialbd.ttf', 'arial.ttf'):
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            pass
    return ImageFont.load_default()


def centered(d, S, text, f, y, fill):
    box = d.textbbox((0, 0), text, font=f)
    d.text(((S - (box[2] - box[0])) / 2 - box[0], y - box[1]), text, font=f, fill=fill)


def chip(S, n_pins, small):
    """The chip at S x S pixels (drawn large, then scaled down)."""
    img = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    k = S / 1024
    pw, pl = (54 if not small else 80) * k, 180 * k
    lo, hi = (250 if not small else 300) * k, S - (250 if not small else 300) * k
    for i in range(n_pins):
        c = lo + (hi - lo) * i / (n_pins - 1)
        r = 14 * k
        d.rounded_rectangle([c - pw / 2, 40 * k, c + pw / 2, 40 * k + pl], r, fill=PIN)
        d.rounded_rectangle([c - pw / 2, S - 40 * k - pl, c + pw / 2, S - 40 * k], r, fill=PIN)
        d.rounded_rectangle([40 * k, c - pw / 2, 40 * k + pl, c + pw / 2], r, fill=PIN)
        d.rounded_rectangle([S - 40 * k - pl, c - pw / 2, S - 40 * k, c + pw / 2], r, fill=PIN)

    m = 150 * k
    ew = (26 if not small else 44) * k
    d.rounded_rectangle([m, m, S - m, S - m], 90 * k, fill=EDGE)
    d.rounded_rectangle([m + ew, m + ew, S - m - ew, S - m - ew], 70 * k, fill=BODY)
    if not small:
        # pin-1 dot and a thin inner line: the "stack" of a stack machine
        d.ellipse([m + 70 * k, m + 70 * k, m + 130 * k, m + 130 * k], fill=DOT)
        d.line([(m + 120 * k, 488 * k), (S - m - 120 * k, 488 * k)], fill=EDGE_DARK, width=int(10 * k))
        centered(d, S, 'FORTH', font(int(150 * k)), 300 * k, TEXT_DIM)
        centered(d, S, '16', font(int(330 * k)), 530 * k, TEXT_MAIN)
    else:
        centered(d, S, '16', font(int(470 * k)), 300 * k, TEXT_MAIN)
    return img


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else 'FPGA-FORTH16.ico'
    big = chip(1024, 6, small=False)
    little = chip(1024, 4, small=True)
    frames = {s: (big if s >= 48 else little).resize((s, s), Image.LANCZOS)
              for s in (256, 128, 64, 48, 32, 24, 16)}
    frames[256].save(out, sizes=[(s, s) for s in frames], append_images=[frames[s] for s in frames if s != 256])
    frames[256].save(out.replace('.ico', '.png'))
    # a preview sheet with every size
    sheet = Image.new('RGBA', (256 + 128 + 64 + 48 + 32 + 24 + 16 + 8 * 20, 276), (37, 37, 38, 255))
    x = 10
    for s in (256, 128, 64, 48, 32, 24, 16):
        sheet.paste(frames[s], (x, 10 + (256 - s)), frames[s])
        x += s + 20
    sheet.save(out.replace('.ico', '-sizes.png'))
    print('wrote', out)


if __name__ == '__main__':
    main()
