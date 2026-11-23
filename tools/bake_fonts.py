"""
Печёт растровые глифы (Inter, Regular + SemiBold) в файл fonts.bin для RollWorld.
Нужен только если хочешь поменять набор символов или размеры. Обычно НЕ нужен.

  pip install pillow
  python tools/bake_fonts.py Inter-Regular.otf Inter-SemiBold.otf fonts.bin

Шрифты Inter (лицензия OFL): https://github.com/rsms/inter/releases
"""
import struct, sys
from PIL import Image, ImageDraw, ImageFont

SIZES = [20, 22, 24, 26, 28, 32, 44]
CHARS = ([chr(c) for c in range(32, 127)] +
         [chr(c) for c in range(0x410, 0x450)] + ['Ё', 'ё'] +
         list('«»—–…×°№·•'))

def bake(path, size):
    font = ImageFont.truetype(path, size)
    asc, desc = font.getmetrics()
    glyphs, blob = [], bytearray()
    for ch in CHARS:
        adv = font.getlength(ch)
        l, t, r, b = font.getbbox(ch, anchor='ls')
        w, h = max(0, r - l), max(0, b - t)
        data = b''
        if w and h:
            im = Image.new('L', (w, h), 0)
            d = ImageDraw.Draw(im)
            d.fontmode = 'L'
            d.text((-l, -t), ch, font=font, fill=255, anchor='ls')
            data = im.tobytes()
        glyphs.append((ord(ch), adv, l, t, w, h, len(blob)))
        blob += data
    return asc, desc, glyphs, bytes(blob)

def main():
    reg, semi, out = sys.argv[1:4]
    faces = []
    for bold, path in ((0, reg), (1, semi)):
        for size in SIZES:
            faces.append((bold, size) + bake(path, size))
    with open(out, 'wb') as f:
        f.write(b'RWF1')
        f.write(struct.pack('<i', len(faces)))
        for bold, size, asc, desc, glyphs, blob in faces:
            f.write(struct.pack('<iiiii', bold, size, asc, desc, len(glyphs)))
            for cp, adv, ox, oy, w, h, off in glyphs:
                f.write(struct.pack('<ifhhHHi', cp, adv, ox, oy, w, h, off))
            f.write(struct.pack('<i', len(blob)))
            f.write(blob)

main()
