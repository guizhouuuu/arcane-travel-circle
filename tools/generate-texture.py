"""Rebuild the original 48x48 transparent pixel-art circle (requires Pillow)."""
from pathlib import Path
from math import cos, sin, pi
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parents[1]
image = Image.new('RGBA', (48, 48), (0, 0, 0, 0))
draw = ImageDraw.Draw(image)
dark = (45, 28, 83, 220)
stone = (77, 60, 119, 235)
edge = (150, 116, 191, 255)
gold = (240, 209, 127, 255)
light = (149, 237, 228, 255)

# Integer coordinates and a deliberately small palette preserve pixel edges.
draw.ellipse((1, 1, 46, 46), fill=dark, outline=edge, width=1)
draw.ellipse((3, 3, 44, 44), outline=stone, width=2)
draw.ellipse((6, 6, 41, 41), outline=gold, width=1)
draw.ellipse((10, 10, 37, 37), outline=edge, width=1)
points = [(round(23.5 + 12*cos(-pi/2 + 2*pi*i/6)),
           round(23.5 + 12*sin(-pi/2 + 2*pi*i/6))) for i in range(6)]
for triangle in ((0, 2, 4, 0), (1, 3, 5, 1)):
    draw.line([points[i] for i in triangle], fill=gold, width=1)
draw.ellipse((19, 19, 28, 28), fill=dark, outline=light, width=1)
draw.line((23, 20, 23, 27), fill=light, width=1)
draw.line((20, 23, 27, 23), fill=light, width=1)
for i in range(12):
    angle = 2*pi*i/12
    x, y = round(23.5 + 19*cos(angle)), round(23.5 + 19*sin(angle))
    dx, dy = round(2*cos(angle)), round(2*sin(angle))
    draw.line((x-dx, y-dy, x+dx, y+dy), fill=light if i % 3 == 0 else gold)
    if i % 3:
        draw.point((x-dy, y+dx), fill=gold)
for x, y in ((3, 3), (44, 3), (3, 44), (44, 44)):
    draw.line((x-1, y, x+1, y), fill=gold)
    draw.line((x, y-1, x, y+1), fill=gold)

(root / 'assets').mkdir(exist_ok=True)
image.save(root / 'assets' / 'circle.png')
mini = Image.new('RGBA', (16, 16), (0, 0, 0, 0))
small = ImageDraw.Draw(mini)
small.ellipse((0, 0, 15, 15), fill=(35, 52, 78, 225), outline=light)
small.ellipse((2, 2, 13, 13), outline=gold)
small.line((7, 3, 12, 11, 3, 11, 7, 3), fill=light)
small.line((7, 12, 3, 5, 12, 5, 7, 12), fill=edge)
small.point((7, 7), fill=gold)
mini.save(root / 'assets' / 'mini-circle.png')
print(root / 'assets' / 'circle.png')
