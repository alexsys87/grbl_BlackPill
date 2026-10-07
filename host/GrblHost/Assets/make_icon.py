"""Draws the Grbl Host icon: AppIcon.ico (16…256 px) and AppIcon.png.

Usage: python3 make_icon.py [output directory]   (needs Pillow)
"""
import math, os, sys
from PIL import Image, ImageDraw, ImageFilter

N = 1024
out = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.abspath(__file__))

def lerp(a, b, t): return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(len(a)))
def hexc(h, a=255): h = h.lstrip('#'); return (int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16), a)

img = Image.new('RGBA', (N, N), (0, 0, 0, 0))

# Background: rounded square, diagonal gradient blue → violet.
grad = Image.new('RGBA', (N, N))
gp = grad.load()
c0, c1 = hexc('#1E88E5'), hexc('#6D28D9')
for y in range(N):
    for x in range(N):
        t = (x * 0.35 + y * 0.65) / N
        gp[x, y] = lerp(c0, c1, min(1, max(0, t)))
mask = Image.new('L', (N, N), 0)
m = 40
ImageDraw.Draw(mask).rounded_rectangle((m, m, N - m, N - m), radius=210, fill=255)
img.paste(grad, (0, 0), mask)

# Soft light in the upper left.
glow = Image.new('RGBA', (N, N), (0, 0, 0, 0))
ImageDraw.Draw(glow).ellipse((-200, -300, 700, 500), fill=(255, 255, 255, 60))
glow = glow.filter(ImageFilter.GaussianBlur(120))
img = Image.alpha_composite(img, Image.composite(glow, Image.new('RGBA', (N, N), (0, 0, 0, 0)), mask))

d = ImageDraw.Draw(img)

# Isometric block of stock with a milled pocket, an end mill above it.
cx, cy, s = 512, 600, 300
c30 = math.cos(math.radians(30))
def P(x, y, z): return (cx + (x - y) * s * c30, cy + (x + y) * s * 0.5 - z * s)
def poly(pts, fill): d.polygon([P(*p) for p in pts], fill=fill)

cy += int(s * 0.18)        # Room for the tool above the block.
H = 0.32                   # Block height.

# Shadow on the table.
sh = Image.new('RGBA', (N, N), (0, 0, 0, 0))
sd = ImageDraw.Draw(sh)
sd.polygon([P(0, 0, 0), P(1.12, 0, 0), P(1.12, 1.12, 0), P(0, 1.12, 0)], fill=(0, 0, 0, 90))
sh = sh.filter(ImageFilter.GaussianBlur(18))
sh.putalpha(Image.composite(sh.getchannel('A'), Image.new('L', (N, N), 0), mask))
img = Image.alpha_composite(img, sh)
d = ImageDraw.Draw(img)

top, left, right = hexc('#FFCC80'), hexc('#F59E0B'), hexc('#D97706')
poly([(1, 0, 0), (1, 1, 0), (1, 1, H), (1, 0, H)], right)
poly([(0, 1, 0), (1, 1, 0), (1, 1, H), (0, 1, H)], left)
poly([(0, 0, H), (1, 0, H), (1, 1, H), (0, 1, H)], top)

# Pocket: a darker floor and its two visible walls.
a, b, depth = 0.22, 0.78, 0.12
poly([(a, a, H - depth), (b, a, H - depth), (b, b, H - depth), (a, b, H - depth)], hexc('#B45309'))
poly([(a, a, H), (b, a, H), (b, a, H - depth), (a, a, H - depth)], hexc('#92400E'))
poly([(a, a, H), (a, b, H), (a, b, H - depth), (a, a, H - depth)], hexc('#78350F'))
# Tool marks on the floor.
for i in range(1, 6):
    t = a + (b - a) * i / 6
    d.line([P(a, t, H - depth), P(b, t, H - depth)], fill=hexc('#FBBF24', 110), width=5)
# Edge highlight.
d.line([P(0, 1, H), P(1, 1, H), P(1, 0, H)], fill=hexc('#FFF3E0', 230), width=8)

# End mill: shank, fluted part, tip in the pocket corner.
tip = P(b, b, H - depth)
tx, ty = tip[0], tip[1]
r, flute_h, shank_h = 46, 190, 170
d.rounded_rectangle((tx - r - 14, ty - flute_h - shank_h - 60, tx + r + 14, ty - flute_h - shank_h + 10),
                    radius=14, fill=hexc('#475569'))                                            # collet nut
d.rectangle((tx - r, ty - flute_h - shank_h, tx + r, ty - flute_h), fill=hexc('#CBD5E1'))         # shank
d.rectangle((tx - r, ty - flute_h, tx + r, ty), fill=hexc('#E2E8F0'))                             # flutes
for k in range(5):
    y0 = ty - flute_h + k * flute_h / 5
    d.polygon([(tx - r, y0 + 10), (tx + r, y0 - 26), (tx + r, y0 + 4), (tx - r, y0 + 40)], fill=hexc('#94A3B8'))
d.ellipse((tx - r, ty - 14, tx + r, ty + 14), fill=hexc('#E2E8F0'))
# Chips.
for (dx, dy, rr) in ((-90, -10, 14), (-120, 30, 10), (80, 20, 12), (110, -20, 9)):
    d.ellipse((tx + dx - rr, ty + dy - rr, tx + dx + rr, ty + dy + rr), fill=hexc('#FDE68A'))

sizes = [16, 24, 32, 48, 64, 128, 256]
img.resize((256, 256), Image.LANCZOS).save(os.path.join(out, 'AppIcon.ico'), sizes=[(z, z) for z in sizes])
img.resize((64, 64), Image.LANCZOS).save(os.path.join(out, 'AppIcon.png'))
