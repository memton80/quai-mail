"""Génère l'icône de Quai Mail (même fond que Quai et Quai PDF, enveloppe blanche) en .ico et .png.

Usage : python tools/make_icon.py src/QuaiMail.App/Assets/QuaiMail.ico src/QuaiMail.App/Assets/AppIcon.png [docs/images/logo.png]
"""
import sys
from PIL import Image, ImageDraw


def render(size: int) -> Image.Image:
    scale = 4
    s = size * scale
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    # Fond : carré arrondi, dégradé vertical bleu nuit -> bleu canard (celui de Quai).
    top, bottom = (15, 76, 129), (11, 132, 148)
    grad = Image.new("RGBA", (s, s))
    gd = ImageDraw.Draw(grad)
    for y in range(s):
        t = y / max(1, s - 1)
        gd.line([(0, y), (s, y)], fill=tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,))
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, s - 1, s - 1], radius=int(s * 0.22), fill=255)
    img.paste(grad, (0, 0), mask)
    d = ImageDraw.Draw(img)
    # Enveloppe blanche, coins arrondis.
    white = (255, 255, 255, 255)
    x0, y0, x1, y1 = s * 0.17, s * 0.27, s * 0.83, s * 0.73
    d.rounded_rectangle([x0, y0, x1, y1], radius=s * 0.05, fill=white)
    # Rabat : un V couleur du fond, de la largeur d'un trait.
    ink = (15, 76, 129, 255)
    w = max(1, int(s * 0.045))
    middle = ((x0 + x1) / 2, y0 + (y1 - y0) * 0.56)
    inset = s * 0.035
    d.line([(x0 + inset, y0 + inset), middle, (x1 - inset, y0 + inset)], fill=ink, width=w, joint="curve")
    r = w / 2
    for x, y in ((x0 + inset, y0 + inset), (x1 - inset, y0 + inset)):
        d.ellipse([x - r, y - r, x + r, y + r], fill=ink)
    return img.resize((size, size), Image.LANCZOS)


out_ico, out_png = sys.argv[1], sys.argv[2]
sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
big = render(256)
big.save(out_ico, format="ICO", sizes=[(n, n) for n in sizes], append_images=[render(n) for n in sizes[:-1]])
render(64).save(out_png, format="PNG")
if len(sys.argv) > 3:
    render(192).save(sys.argv[3], format="PNG")
