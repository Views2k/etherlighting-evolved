"""Prepare application and installer images from the brand image in assets/logo.png.

Every build runs this script without arguments to draw the installer images, which are not tracked. After the
logo changes, run it with --icons to regenerate the tracked application and web icons, then commit them.
"""

import argparse
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
APP_ASSETS = ROOT / "src" / "etherlighting_evolved" / "assets"
WEB_ASSETS = ROOT / "assets"
PACKAGING = ROOT / "packaging"
# The workspace shows logo-256.png at up to 52 CSS pixels, which stays sharp on high-density screens without
# loading the full-size master on every page load.
WEB_ICONS = {"favicon.png": 128, "pwa-180.png": 180, "pwa-192.png": 192, "pwa-512.png": 512, "logo-256.png": 256}


def wizard_image(width: int, height: int, master: Image.Image) -> Image.Image:
    image = Image.new("RGBA", (width, height), (17, 20, 29, 255))
    draw = ImageDraw.Draw(image)
    for y in range(height):
        t = y / max(1, height - 1)
        draw.line((0, y, width, y), fill=(round(18 + 8 * t), round(22 + 3 * t), round(34 + 10 * t), 255))
    mark = master.resize((round(width * .88),) * 2, Image.Resampling.LANCZOS)
    image.alpha_composite(mark, ((width - mark.width) // 2, round(height * .18)))
    face = ImageFont.truetype("seguisb.ttf", round(width * .105))
    y = round(height * .18) + mark.height + round(height * .07)
    draw = ImageDraw.Draw(image)
    for line in ("Etherlighting", "Evolved"):
        line_width = draw.textlength(line, font=face)
        draw.text(((width - line_width) / 2, y), line, font=face, fill=(233, 235, 250))
        y += round(width * .15)
    stops = ((69, 147, 247), (137, 91, 238), (225, 93, 184))
    for x in range(width):
        position = x / max(1, width - 1) * 2
        index = min(1, int(position))
        fraction = position - index
        color = tuple(round(a + (b - a) * fraction) for a, b in zip(stops[index], stops[index + 1], strict=True))
        draw.line((x, height - max(2, round(width / 55)), x, height), fill=color)
    return image.convert("RGB")


def write_icons(master: Image.Image) -> None:
    master.resize((256, 256), Image.Resampling.LANCZOS).save(APP_ASSETS / "ee.png")
    master.save(APP_ASSETS / "ee.ico", sizes=[(s, s) for s in (16, 20, 24, 32, 40, 48, 64, 128, 256)])
    for name, size in WEB_ICONS.items():
        master.resize((size, size), Image.Resampling.LANCZOS).save(WEB_ASSETS / name, optimize=True)


def write_installer_images(master: Image.Image) -> None:
    for suffix, scale in (("", 1), ("-200", 2)):
        wizard_image(164 * scale, 314 * scale, master).save(PACKAGING / f"wizard{suffix}.bmp")
        badge = Image.new("RGBA", (55 * scale, 55 * scale), (255, 255, 255, 255))
        badge.alpha_composite(master.resize(badge.size, Image.Resampling.LANCZOS))
        badge.convert("RGB").save(PACKAGING / f"wizard-small{suffix}.bmp")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--icons", action="store_true", help="regenerate the tracked icons, not the installer images")
    master = Image.open(WEB_ASSETS / "logo.png").convert("RGBA")
    if parser.parse_args().icons:
        write_icons(master)
        print("Prepared application and web icons")
    else:
        write_installer_images(master)
        print("Prepared installer images")


if __name__ == "__main__":
    main()
