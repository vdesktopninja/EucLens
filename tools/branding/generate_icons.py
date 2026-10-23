#!/usr/bin/env python3
"""Generates the EUC Lens application icon, tray icons, SVG master and previews.

Run from the repository root:  python3 tools/branding/generate_icons.py
Needs Pillow only. Output is deterministic; commit the result.

Artwork: a computer monitor and a circular magnifier with a small bar chart inside it, white on deep blue, bars in cyan.
Each size class has its own hand-tuned layout (pixel-aligned strokes for the tiny sizes) instead of downscaling one
large drawing:

    grid 16   used for 16 px      (1 px strokes on whole pixels)
    grid 24   used for 20, 24 px
    grid 32   used for 32, 48 px
    grid 256  used for 64, 128, 256 px and the SVG master

Tray icons add a status badge in the lower right (shape and colour both encode the state):
    idle       none                  (brand icon)
    monitoring green play triangle
    recording  red filled circle
    warning    amber triangle
    paused     grey two bars
    recording-warning  red circle plus an amber triangle in the upper right
"""
import io
import struct
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[2]
ASSETS = ROOT / 'src/EucLens.App/Assets'
BRAND = ROOT / 'assets/branding'

BLUE = (11, 77, 162, 255)
WHITE = (255, 255, 255, 255)
CYAN = (41, 208, 240, 255)
GREEN = (46, 184, 92, 255)
RED = (226, 58, 48, 255)
AMBER = (245, 166, 35, 255)
GREY = (150, 156, 166, 255)
SS = 8  # supersampling factor

# Per grid: coordinates in grid units. 'r' radius of the background tile, 'mon' monitor outline box and stroke,
# 'neck' / 'base' stand boxes, 'lens' centre, outer radius and ring width, 'handle' line and width, 'bars' (x, width, height) on 'base_y'.
LAYOUTS = {
    16: dict(r=3, mon=(1.5, 2.5, 10.5, 9.5, 1), neck=(5, 10, 7, 11), base=(3, 11, 9, 12),
             lens=(10.5, 10, 4.5, 1.5), handle=(13.6, 13.1, 15.0, 14.5, 2), base_y=12, bars=[(8, 1.2, 1.5), (10, 1.2, 3), (12, 1.2, 4.5)]),
    24: dict(r=4.5, mon=(2.2, 3.3, 15.5, 14.2, 1.6), neck=(7.8, 14.2, 10.2, 16.6), base=(5.4, 16.6, 12.6, 18.2),
             lens=(15.6, 15.2, 6.6, 2.1), handle=(20.4, 20, 22.4, 22, 2.8), base_y=18.4, bars=[(12.6, 1.7, 2.5), (15.1, 1.7, 4.6), (17.6, 1.7, 6.8)]),
    32: dict(r=6, mon=(3, 4.5, 21, 19, 2.2), neck=(10.5, 19, 13.5, 22.5), base=(7.5, 22.5, 16.5, 24.7),
             lens=(21, 20.5, 8.8, 2.8), handle=(27.2, 26.7, 30, 29.5, 3.6), base_y=24.4, bars=[(16.7, 2.4, 3.5), (20.2, 2.4, 6.2), (23.7, 2.4, 9.2)]),
    256: dict(r=48, mon=(26, 44, 188, 156, 12), neck=(92, 156, 120, 180), base=(66, 180, 146, 192),
              lens=(166, 150, 56, 14), handle=(206, 190, 236, 220, 20), base_y=172, bars=[(141, 14, 20), (159, 14, 34), (177, 14, 50)]),
}


def _pixels(points, colour, image):
    px = image.load()
    for x, y in points:
        px[x, y] = colour


def ring_points(left, top):
    """Hand-drawn 9 x 9 pixel ring (symmetric) and its interior, offset to (left, top)."""
    rows = {0: (2, 3, 4, 5, 6), 1: (1, 7), 2: (0, 8), 3: (0, 8), 4: (0, 8), 5: (0, 8), 6: (0, 8), 7: (1, 7), 8: (2, 3, 4, 5, 6)}
    ring = {(left + x, top + y) for y, xs in rows.items() for x in xs}
    inner = {(left + x, top + y) for y in range(1, 8) for x in range(1, 8)} - ring
    return ring, inner


def pixel16(with_lens=True):
    """Hand-placed 16 x 16 artwork: whole-pixel 1 px strokes, no resampling."""
    image = Image.new('RGBA', (16, 16), (0, 0, 0, 0))
    tile = [(x, y) for y in range(16) for x in range(16) if not ((x in (0, 15)) and (y in (0, 15)))]
    _pixels(tile, BLUE, image)
    if with_lens:
        mon_right, mon_bottom, stand_y = 9, 9, 10
        monitor = [(x, 2) for x in range(1, mon_right + 1)] + [(x, mon_bottom) for x in range(1, mon_right + 1)] \
            + [(1, y) for y in range(2, mon_bottom + 1)] + [(mon_right, y) for y in range(2, mon_bottom + 1)]
        _pixels(monitor, WHITE, image)
        _pixels([(4, 10), (5, 10), (4, 11), (5, 11)] + [(x, 12) for x in range(2, 8)], WHITE, image)
        ring, inner = ring_points(5, 4)
        _pixels(inner, BLUE, image)
        _pixels(ring, WHITE, image)
        _pixels([(13, 12), (14, 13)], WHITE, image)
        bars = [(7, 10, 11), (9, 8, 11), (11, 7, 11)]
    else:
        # Tray base: the badge takes the lens' place, so the monitor carries the bar chart.
        monitor = [(x, 2) for x in range(1, 12)] + [(x, 10) for x in range(1, 12)] + [(1, y) for y in range(2, 11)] + [(11, y) for y in range(2, 11)]
        _pixels(monitor, WHITE, image)
        _pixels([(5, 11), (6, 11), (5, 12), (6, 12)] + [(x, 13) for x in range(3, 9)], WHITE, image)
        bars = [(3, 7, 9), (5, 5, 9), (7, 4, 9)]
    for x, top, bottom in bars:
        _pixels([(x, y) for y in range(top, bottom + 1)], CYAN, image)
    return image


def grid_for(size):
    return 16 if size <= 16 else 24 if size <= 24 else 32 if size <= 48 else 256


def draw_art(size, background=True):
    """The brand icon at exactly size x size pixels."""
    if size == 16:
        return pixel16()
    grid = grid_for(size)
    lay = LAYOUTS[grid]
    k = size / grid * SS
    big = Image.new('RGBA', (size * SS, size * SS), (0, 0, 0, 0))
    d = ImageDraw.Draw(big)

    def sc(v):
        return v * k

    if background:
        d.rounded_rectangle((0, 0, size * SS - 1, size * SS - 1), radius=sc(lay['r']), fill=BLUE)
    x0, y0, x1, y1, w = lay['mon']
    d.rounded_rectangle((sc(x0 - w / 2), sc(y0 - w / 2), sc(x1 + w / 2), sc(y1 + w / 2)), radius=sc(max(w * .9, 1)), fill=WHITE)
    d.rounded_rectangle((sc(x0 + w / 2), sc(y0 + w / 2), sc(x1 - w / 2), sc(y1 - w / 2)), radius=sc(max(w * .3, 0.3)), fill=BLUE)
    d.rectangle([sc(v) for v in lay['neck']], fill=WHITE)
    d.rounded_rectangle([sc(v) for v in lay['base']], radius=sc(.4 if grid < 256 else 5), fill=WHITE)
    cx, cy, r, rw = lay['lens']
    d.ellipse((sc(cx - r), sc(cy - r), sc(cx + r), sc(cy + r)), fill=BLUE)  # masks the monitor behind the lens
    hx0, hy0, hx1, hy1, hw = lay['handle']
    d.line((sc(hx0), sc(hy0), sc(hx1), sc(hy1)), fill=WHITE, width=round(sc(hw)))
    for ex, ey in ((hx1, hy1),):
        d.ellipse((sc(ex - hw / 2), sc(ey - hw / 2), sc(ex + hw / 2), sc(ey + hw / 2)), fill=WHITE)
    d.ellipse((sc(cx - r), sc(cy - r), sc(cx + r), sc(cy + r)), fill=WHITE)
    d.ellipse((sc(cx - r + rw), sc(cy - r + rw), sc(cx + r - rw), sc(cy + r - rw)), fill=BLUE)
    for bx, bw, bh in lay['bars']:
        d.rectangle((sc(bx), sc(lay['base_y'] - bh), sc(bx + bw), sc(lay['base_y'])), fill=CYAN)
    return big.resize((size, size), Image.LANCZOS)


def badge_geometry(size):
    """Badge box (x0, y0, x1, y1) in pixels, lower right; tuned per tray size."""
    edge = {16: 8, 20: 10, 24: 12, 32: 16}[size]
    return (size - edge, size - edge, size, size)


def draw_badge(image, kind, size, corner='lower'):
    x0, y0, x1, y1 = badge_geometry(size)
    if corner == 'upper':
        x0, y0, x1, y1 = size - (x1 - x0), 0, size, y1 - y0
    big = Image.new('RGBA', (size * SS, size * SS), (0, 0, 0, 0))
    d = ImageDraw.Draw(big)
    bx0, by0, bx1, by1 = (v * SS for v in (x0, y0, x1, y1))
    w, h = bx1 - bx0, by1 - by0
    keyline = max(1, round(size / 16)) * SS * 0.9

    def shape(color, grow=0.0):
        g = grow
        if kind in ('monitoring',):
            d.polygon([(bx0 + w * .12 - g, by0 + h * .05 - g * 1.2), (bx1 - w * .02 + g * 1.4, by0 + h * .5), (bx0 + w * .12 - g, by1 - h * .05 + g * 1.2)], fill=color)
        elif kind in ('recording',):
            d.ellipse((bx0 + w * .05 - g, by0 + h * .05 - g, bx1 - w * .05 + g, by1 - h * .05 + g), fill=color)
        elif kind in ('warning',):
            d.polygon([((bx0 + bx1) / 2, by0 - g * 1.3), (bx1 + g * 1.2, by1 - h * .05 + g), (bx0 - g * 1.2, by1 - h * .05 + g)], fill=color)
        elif kind in ('paused',):
            bw = w * .26
            d.rectangle((bx0 + w * .12 - g, by0 + h * .08 - g, bx0 + w * .12 + bw + g, by1 - h * .08 + g), fill=color)
            d.rectangle((bx1 - w * .12 - bw - g, by0 + h * .08 - g, bx1 - w * .12 + g, by1 - h * .08 + g), fill=color)

    colour = {'monitoring': GREEN, 'recording': RED, 'warning': AMBER, 'paused': GREY}[kind]
    shape(BLUE, grow=keyline)   # keyline in the tile colour separates the badge from the artwork
    shape(colour)
    layer = big.resize((size, size), Image.LANCZOS)
    image.alpha_composite(layer)


TRAY_STATES = {
    'idle': [],
    'monitoring': [('monitoring', 'lower')],
    'recording': [('recording', 'lower')],
    'recording-warning': [('recording', 'lower'), ('warning', 'upper')],
    'warning': [('warning', 'lower')],
    'paused': [('paused', 'lower')],
}
TRAY_SIZES = (16, 20, 24, 32)
APP_SIZES = (16, 20, 24, 32, 48, 64, 128, 256)


def tray_image(state, size):
    image = pixel16(with_lens=False) if size == 16 else draw_art(size)
    tile = image.getchannel('A')
    for kind, corner in TRAY_STATES[state]:
        draw_badge(image, kind, size, corner)
    # The badge never leaves the rounded tile.
    image.putalpha(Image.composite(image.getchannel('A'), Image.new('L', image.size, 0), tile))
    return image


def png_bytes(image):
    out = io.BytesIO()
    image.save(out, 'PNG', optimize=True)
    return out.getvalue()


def bmp_entry(image):
    """ICO BMP entry: BITMAPINFOHEADER (height doubled), bottom-up BGRA pixels and a 1 bpp AND mask."""
    w, h = image.size
    pixels = image.load()
    header = struct.pack('<IiiHHIIiiII', 40, w, h * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    xor = bytearray()
    for y in range(h - 1, -1, -1):
        for x in range(w):
            r, g, b, a = pixels[x, y]
            xor += bytes((b, g, r, a))
    stride = ((w + 31) // 32) * 4
    mask = bytearray(stride * h)
    for y in range(h - 1, -1, -1):
        row = (h - 1 - y) * stride
        for x in range(w):
            if pixels[x, y][3] < 128:
                mask[row + x // 8] |= 0x80 >> (x % 8)
    return header + bytes(xor) + bytes(mask)


def write_ico(path, images):
    """images: list of (size, Image). Sizes up to 48 are stored as BMP, larger ones as PNG (classic Windows layout)."""
    entries = []
    for size, image in images:
        data = bmp_entry(image) if size <= 48 else png_bytes(image)
        entries.append((size, data))
    out = bytearray(struct.pack('<HHH', 0, 1, len(entries)))
    offset = 6 + 16 * len(entries)
    for size, data in entries:
        out += struct.pack('<BBBBHHII', size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    for _, data in entries:
        out += data
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(bytes(out))


SVG = '''<?xml version="1.0" encoding="UTF-8"?>
<!-- EUC Lens application icon, vector master. Generated by tools/branding/generate_icons.py (grid 256). -->
<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256" role="img" aria-label="EUC Lens">
  <rect width="256" height="256" rx="48" fill="#0B4DA2"/>
  <rect x="26" y="44" width="162" height="112" rx="11" fill="none" stroke="#FFFFFF" stroke-width="12"/>
  <rect x="92" y="156" width="28" height="24" fill="#FFFFFF"/>
  <rect x="66" y="180" width="80" height="12" rx="5" fill="#FFFFFF"/>
  <circle cx="166" cy="150" r="56" fill="#0B4DA2"/>
  <line x1="206" y1="190" x2="236" y2="220" stroke="#FFFFFF" stroke-width="20" stroke-linecap="round"/>
  <circle cx="166" cy="150" r="49" fill="#0B4DA2" stroke="#FFFFFF" stroke-width="14"/>
  <rect x="141" y="152" width="14" height="20" fill="#29D0F0"/>
  <rect x="159" y="138" width="14" height="34" fill="#29D0F0"/>
  <rect x="177" y="122" width="14" height="50" fill="#29D0F0"/>
</svg>
'''


def main():
    # Application icon
    write_ico(ASSETS / 'EucLens.ico', [(s, draw_art(s)) for s in APP_SIZES])
    # Tray icons (all six states, four tray sizes)
    for state in TRAY_STATES:
        write_ico(ASSETS / 'Tray' / f'tray-{state}.ico', [(s, tray_image(state, s)) for s in TRAY_SIZES])
    # Vector master and previews
    BRAND.mkdir(parents=True, exist_ok=True)
    (BRAND / 'euclens-icon.svg').write_text(SVG, encoding='utf-8')
    previews = BRAND / 'previews'
    previews.mkdir(exist_ok=True)
    draw_art(256).save(previews / 'euclens-icon-256.png')
    draw_art(256).resize((512, 512), Image.LANCZOS).save(previews / 'euclens-icon-512.png')
    for name, background in (('light', (243, 243, 243, 255)), ('dark', (32, 32, 32, 255))):
        # Application sizes: true size on top, enlarged (nearest neighbour, pixels visible) below.
        sheet = Image.new('RGBA', (1000, 360), background)
        x = 20
        for size in APP_SIZES:
            image = draw_art(size)
            sheet.alpha_composite(image, (x, 20))
            zoom = 8 if size <= 24 else 4 if size <= 32 else 2 if size <= 48 else 0
            if zoom:
                sheet.alpha_composite(image.resize((size * zoom, size * zoom), Image.NEAREST), (x, 80))
            x += max(size, size * zoom if zoom else size) + 16
            if x > 900:
                break
        sheet.convert('RGB').save(previews / f'application-icon-sizes-{name}.png')
        # Tray states: true sizes on top, 32 px enlarged 4x in the middle, 16 px enlarged 8x at the bottom.
        states = list(TRAY_STATES)
        sheet = Image.new('RGBA', (20 + len(states) * 200, 20 + 40 + 144 + 20 + 128 + 20), background)
        for i, state in enumerate(states):
            x = 20 + i * 200
            cx = x
            for size in TRAY_SIZES:
                sheet.alpha_composite(tray_image(state, size), (cx, 20))
                cx += size + 8
            sheet.alpha_composite(tray_image(state, 32).resize((128, 128), Image.NEAREST), (x, 70))
            sheet.alpha_composite(tray_image(state, 16).resize((128, 128), Image.NEAREST), (x, 70 + 128 + 20))
        sheet.convert('RGB').save(previews / f'tray-states-{name}.png')
    for stale in ('tray-16px-enlarged-light.png', 'tray-16px-enlarged-dark.png'):
        (previews / stale).unlink(missing_ok=True)
    print('generated', ASSETS / 'EucLens.ico')


if __name__ == '__main__':
    main()
