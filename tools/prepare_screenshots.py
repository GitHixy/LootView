"""
Fits in-game screenshots into the size DalamudPluginsD17 accepts for the plugin
installer, and writes them out as image1.png, image2.png, ...

D17's rule: images in the `images/` folder next to manifest.toml must be PNG and
no larger than 730x380. Anything bigger is rejected, so this scales down (never
up) while preserving aspect ratio.

    python tools/prepare_screenshots.py shot-a.png shot-b.png

The game's own screenshot key does not capture the Dalamud overlay - it grabs the
back buffer before ImGui is drawn on top. Use Win+Shift+S to snip the plugin
window instead, then pull it straight off the clipboard:

    python tools/prepare_screenshots.py --from-clipboard --start 1

By default it writes into D17_Submission/testing/live/LootView/images/. Pass
--out to target the stable folder instead.
"""
import argparse
import os
import sys

from PIL import Image, ImageGrab

MAX_W, MAX_H = 730, 380
DEFAULT_OUT = os.path.join('D17_Submission', 'testing', 'live', 'LootView', 'images')


def fit(path, out_path):
    with Image.open(path) as im:
        im = im.convert('RGBA')
        w, h = im.size

        # Only ever shrink: upscaling a screenshot just makes it blurry.
        scale = min(MAX_W / w, MAX_H / h, 1.0)
        if scale < 1.0:
            im = im.resize((max(int(w * scale), 1), max(int(h * scale), 1)), Image.LANCZOS)

        im.save(out_path, 'PNG', optimize=True)
        return (w, h), im.size, os.path.getsize(out_path)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('images', nargs='*', help='screenshots, in the order they should appear')
    parser.add_argument('--from-clipboard', action='store_true',
                        help='take the image from the clipboard (after a Win+Shift+S snip)')
    parser.add_argument('--out', default=DEFAULT_OUT, help='destination images/ folder')
    parser.add_argument('--start', type=int, default=1, help='first image number (default 1)')
    args = parser.parse_args()

    if not args.images and not args.from_clipboard:
        parser.error('give one or more image files, or --from-clipboard')

    os.makedirs(args.out, exist_ok=True)

    if args.from_clipboard:
        grabbed = ImageGrab.grabclipboard()
        if not isinstance(grabbed, Image.Image):
            print('No image on the clipboard. Snip one with Win+Shift+S first.', file=sys.stderr)
            return 1

        name = f'image{args.start}.png'
        dest = os.path.join(args.out, name)
        before = grabbed.size
        scale = min(MAX_W / before[0], MAX_H / before[1], 1.0)
        out = grabbed.convert('RGBA')
        if scale < 1.0:
            out = out.resize((max(int(before[0] * scale), 1), max(int(before[1] * scale), 1)), Image.LANCZOS)
        out.save(dest, 'PNG', optimize=True)

        note = '' if before == out.size else f'  (scaled from {before[0]}x{before[1]})'
        print(f'{name}: {out.size[0]}x{out.size[1]}, {os.path.getsize(dest) / 1024:.0f} KB{note}')
        print(f'\nwritten to {args.out}')
        return 0

    for offset, src in enumerate(args.images):
        if not os.path.isfile(src):
            print(f'missing: {src}', file=sys.stderr)
            return 1

        name = f'image{args.start + offset}.png'
        dest = os.path.join(args.out, name)
        before, after, size = fit(src, dest)

        note = '' if before == after else f'  (scaled from {before[0]}x{before[1]})'
        print(f'{name}: {after[0]}x{after[1]}, {size / 1024:.0f} KB{note}')

    print(f'\nwritten to {args.out}')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
