#!/usr/bin/env python3
"""Render docs/Manual.md to docs/Manual.pdf (Markdown -> HTML -> headless Edge).

    python tools/manual_pdf.py [--keep-html]

Needs the 'markdown' package (pip install --user markdown) and Microsoft Edge.
"""

import argparse
import datetime
import os
import pathlib
import re
import subprocess
import sys

import markdown

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(HERE, 'docs', 'Manual.md')
PDF = os.path.join(HERE, 'docs', 'Manual.pdf')
HTML = os.path.join(HERE, 'build', 'Manual.html')

EDGE_PATHS = [
    r'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe',
    r'C:\Program Files\Microsoft\Edge\Application\msedge.exe',
]

CSS = """
@page {
  size: A4;
  margin: 18mm 16mm 18mm 16mm;
  @bottom-left  { content: "MAX1000-FORTH \\2014  User Manual"; font: 8pt 'Segoe UI', sans-serif; color: #777; }
  @bottom-right { content: "Page " counter(page) " of " counter(pages); font: 8pt 'Segoe UI', sans-serif; color: #777; }
}
@page :first { @bottom-left { content: none; } @bottom-right { content: none; } }

body { font: 10pt/1.45 'Segoe UI', Arial, sans-serif; color: #1a1a1a; }
.cover { height: 240mm; display: flex; flex-direction: column; justify-content: center;
         border-left: 6px solid #1f5f99; padding-left: 14mm; page-break-after: always; }
.cover h1 { font-size: 30pt; margin: 0 0 4mm 0; border: none; }
.cover .sub { font-size: 14pt; color: #444; margin-bottom: 14mm; }
.cover .meta { font-size: 10pt; color: #666; line-height: 1.7; }

h1 { font-size: 20pt; color: #1f5f99; border-bottom: 2px solid #1f5f99; padding-bottom: 2mm; }
h2 { font-size: 15pt; color: #1f5f99; border-bottom: 1px solid #c8d6e5; padding-bottom: 1mm;
     margin-top: 9mm; page-break-before: always; }
h3 { font-size: 12pt; color: #23476b; margin-top: 6mm; }
h2, h3 { page-break-after: avoid; break-after: avoid; }
h1 + p, h2 + p, h3 + p { break-before: avoid; }

a { color: #1f5f99; text-decoration: none; }
code { font-family: Consolas, 'Courier New', monospace; font-size: 9pt;
       background: #f1f4f8; padding: 0 2px; border-radius: 2px; }
pre { font-family: Consolas, 'Courier New', monospace; font-size: 8.6pt; line-height: 1.35;
      background: #f6f8fa; border: 1px solid #dde3ea; border-left: 3px solid #1f5f99;
      padding: 2.5mm 3mm; white-space: pre-wrap; break-inside: avoid; }
pre code { background: none; padding: 0; font-size: inherit; }

table { border-collapse: collapse; width: 100%; margin: 3mm 0 4mm 0; font-size: 9pt; }
th { background: #e8eef5; text-align: left; font-weight: 600; }
th, td { border: 1px solid #ccd5df; padding: 1.2mm 2mm; vertical-align: top; }
tr { break-inside: avoid; }
td:first-child code { white-space: nowrap; }
hr { display: none; }
img { max-width: 100%; border: 1px solid #ccd5df; break-inside: avoid; }
ul, ol { padding-left: 6mm; }
li { margin: 0.6mm 0; }
"""


def build_html(md_text):
    # The Markdown's own title becomes the cover page
    lines = md_text.splitlines()
    if lines and lines[0].startswith('# '):
        lines = lines[1:]
    body = markdown.markdown('\n'.join(lines),
                             extensions=['tables', 'fenced_code', 'toc', 'sane_lists'])
    today = datetime.date.today().isoformat()
    # The HTML is written to build/, so images referenced relative to docs/ get absolute paths
    body = re.sub(r'src="(?!\w+:)([^"]+)"',
                  lambda m: f'src="{pathlib.Path(HERE, "docs", m.group(1)).resolve().as_uri()}"', body)
    cover = f"""
<div class="cover">
  <h1>MAX1000-FORTH</h1>
  <div class="sub">User Manual &mdash; a 16-bit Forth CPU on the Trenz MAX1000 (Intel MAX 10)</div>
  <div class="meta">MAX1000-FORTH 1.0<br>Generated {today} from docs/Manual.md</div>
</div>"""
    return (f'<!doctype html><html><head><meta charset="utf-8">'
            f'<title>MAX1000-FORTH User Manual</title><style>{CSS}</style></head>'
            f'<body>{cover}{body}</body></html>')


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--keep-html', action='store_true', help='keep build/Manual.html')
    args = ap.parse_args()

    edge = next((p for p in EDGE_PATHS if os.path.exists(p)), None)
    if not edge:
        sys.exit('Microsoft Edge not found')

    with open(SRC, encoding='utf-8') as f:
        html = build_html(f.read())
    os.makedirs(os.path.dirname(HTML), exist_ok=True)
    with open(HTML, 'w', encoding='utf-8') as f:
        f.write(html)

    url = 'file:///' + HTML.replace('\\', '/')
    subprocess.run([edge, '--headless=new', '--disable-gpu', '--no-pdf-header-footer',
                    '--no-first-run', f'--print-to-pdf={PDF}', url],
                   check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=120)
    if not args.keep_html:
        os.remove(HTML)
    print(f'wrote {PDF} ({os.path.getsize(PDF) // 1024} KB)')


if __name__ == '__main__':
    main()
