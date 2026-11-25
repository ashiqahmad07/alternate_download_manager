# Makes the app's and the extension's icons from assets\logo-source.png, which has the
# logo on a black background and no transparency. Needs Pillow and numpy. Run by hand when
# the logo changes, and commit what it writes; the build only uses the results:
#   python scripts\make-icons.py
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "assets" / "logo-source.png"
LOGO = ROOT / "assets" / "logo.png"
APP_ICON = ROOT / "src" / "ADM.App" / "Assets" / "ADM.ico"
EXTENSION = ROOT / "extension"

# Windows asks for these at scales from 100% to 200%, and for 256 in Explorer's big views.
APP_SIZES = [16, 20, 24, 32, 40, 48, 64, 256]
EXTENSION_SIZES = [16, 32, 48, 128]

BLACK = 20  # Brightest a background pixel gets (the image has some noise).
EDGE = 3  # How far into the logo its soft edge can reach, in pixels of the source.


def cut_out(source):
    """The logo with a transparent background, undoing the black its edge pixels were blended with."""
    rgb = np.asarray(source.convert("RGB"), dtype=np.float32)
    brightest = rgb.max(axis=2)

    # The background is what's dark and connected to the corner, so nothing inside the logo goes.
    # (The copy because fromarray's image is read-only, and a fill on it does nothing, silently.)
    mask = Image.fromarray(np.where(brightest < BLACK, 255, 0).astype(np.uint8)).copy()
    ImageDraw.floodfill(mask, (0, 0), 128)
    outside = Image.fromarray(np.where(np.asarray(mask) == 128, 255, 0).astype(np.uint8))

    near = np.asarray(outside.filter(ImageFilter.MaxFilter(EDGE * 2 + 1))) > 0
    far = np.asarray(outside.filter(ImageFilter.MinFilter(EDGE * 2 + 1))) > 0

    # An edge pixel is the colour beside it, faded towards black by as much as it is darker.
    beside = Image.fromarray(brightest.astype(np.uint8)).filter(ImageFilter.MaxFilter((EDGE + 1) * 2 + 1))
    alpha = np.where(near, np.clip((brightest - BLACK / 2) / np.maximum(np.asarray(beside, dtype=np.float32) - BLACK / 2, 1), 0, 1), 1.0)
    alpha[far] = 0
    colour = np.where(alpha[..., None] > 0, np.clip(rgb / np.maximum(alpha[..., None], 1 / 255), 0, 255), 0)

    logo = np.dstack((colour, alpha * 255)).round().astype(np.uint8)
    return Image.fromarray(logo)


def square(logo):
    """The logo cropped to itself and centred on a square, with room for the edge to fade."""
    left, top, right, bottom = logo.getchannel("A").getbbox()
    side = max(right - left, bottom - top) + 8
    canvas = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    canvas.paste(logo.crop((left, top, right, bottom)), ((side - (right - left)) // 2, (side - (bottom - top)) // 2))
    return canvas


def at(logo, size):
    """The logo at a size, sharpened when it's small, where shrinking blurs the arrow most."""
    small = logo.resize((size, size), Image.LANCZOS)
    if size > 64:
        return small
    colour = small.convert("RGB").filter(ImageFilter.UnsharpMask(radius=1, percent=70, threshold=0))
    colour.putalpha(small.getchannel("A"))
    return colour


def main():
    logo = square(cut_out(Image.open(SOURCE)))
    logo.save(LOGO, optimize=True)

    APP_ICON.parent.mkdir(exist_ok=True)
    frames = [at(logo, size) for size in APP_SIZES]
    frames[-1].save(APP_ICON, format="ICO", sizes=[(size, size) for size in APP_SIZES], append_images=frames[:-1])

    for size in EXTENSION_SIZES:
        at(logo, size).save(EXTENSION / f"icon{size}.png", optimize=True)


if __name__ == "__main__":
    main()
