"""
Generates LootView's icon as both SVG and PNG from one set of constants, so the
two can never drift apart.

The design is the plugin's own visual language: the aetheryte-diamond crest from
the overlay toolbar, cut into gem facets, on the same deep-navy glass panel with a
brass frame that every LootView window uses.
"""
import numpy as np
from PIL import Image, ImageDraw

# ---------------------------------------------------------------- palette ------
# Straight from src/UI/Theme.cs.
INK_TOP = (0x18, 0x21, 0x33)
INK_BOTTOM = (0x06, 0x09, 0x10)
CRYSTAL = (0x6F, 0xB8, 0xE0)
GOLD = (0xD6, 0xB0, 0x68)
GOLD_BRIGHT = (0xF2, 0xD7, 0x96)
SEAM = (0x5A, 0x46, 0x1E)

# The eight cut faces, lit from the upper left.
FACETS = {
    'crown_l_outer': (0xE6, 0xC8, 0x82),
    'crown_l_table': (0xFF, 0xF3, 0xD2),
    'crown_r_table': (0xDF, 0xB9, 0x6E),
    'crown_r_outer': (0xBE, 0x96, 0x50),
    'pav_l_outer':   (0xAD, 0x88, 0x45),
    'pav_l_table':   (0xCD, 0xA8, 0x60),
    'pav_r_table':   (0x9E, 0x7F, 0x3C),
    'pav_r_outer':   (0x6B, 0x53, 0x26),
}

# ---------------------------------------------------------------- geometry -----
SIZE = 512          # final icon size
CORNER = 104        # panel corner radius
FRAME_INSET = 36    # brass frame inset from the edge
FRAME_RADIUS = 72
FRAME_WIDTH = 3.5
BRACKET = 34        # corner filigree arm length

CX = CY = SIZE / 2
GEM_H = 158         # half height
GEM_W = 121         # half width
GIRDLE = 0.46       # where the inner facet edges meet the girdle

SPARKLES = [        # (x, y, radius, alpha)
    (116, 150, 14, 0.92),
    (398, 186, 9, 0.70),
    (140, 366, 8, 0.58),
    (392, 350, 12, 0.80),
]


def gem_points(cx=CX, cy=CY, w=GEM_W, h=GEM_H):
    """The gem's key points: apexes, girdle corners, inner girdle edges and centre."""
    return {
        'top': (cx, cy - h),
        'bottom': (cx, cy + h),
        'left': (cx - w, cy),
        'right': (cx + w, cy),
        'ml': (cx - w * GIRDLE, cy),
        'mr': (cx + w * GIRDLE, cy),
        'mid': (cx, cy),
    }


def facet_polygons(p):
    """The cut faces, in draw order. The centre split gives the stone its axis."""
    return [
        ('crown_l_outer', [p['top'], p['left'], p['ml']]),
        ('crown_l_table', [p['top'], p['ml'], p['mid']]),
        ('crown_r_table', [p['top'], p['mid'], p['mr']]),
        ('crown_r_outer', [p['top'], p['mr'], p['right']]),
        ('pav_l_outer',   [p['bottom'], p['left'], p['ml']]),
        ('pav_l_table',   [p['bottom'], p['ml'], p['mid']]),
        ('pav_r_table',   [p['bottom'], p['mid'], p['mr']]),
        ('pav_r_outer',   [p['bottom'], p['mr'], p['right']]),
    ]


def seam_pairs():
    """Facet edges drawn as fine seams over the fills."""
    return [('top', 'ml'), ('top', 'mid'), ('top', 'mr'),
            ('bottom', 'ml'), ('bottom', 'mid'), ('bottom', 'mr'),
            ('left', 'right')]


def glint_polygon(p):
    """Specular streak across the upper-left crown."""
    tx, ty = p['top']
    return [(tx - 9, ty + 26), (tx - 44, ty + 74), (tx - 26, ty + 86), (tx + 3, ty + 42)]


def corner_brackets():
    """L-shaped brass corner ornaments, hugging the frame's rounded corners."""
    lo, hi = FRAME_INSET, SIZE - FRAME_INSET
    out = []
    for (x, y, dx, dy) in [(lo, lo, 1, 1), (hi, lo, -1, 1), (lo, hi, 1, -1), (hi, hi, -1, -1)]:
        ox = x + dx * FRAME_RADIUS * 0.30
        oy = y + dy * FRAME_RADIUS * 0.30
        out.append(((ox, oy + dy * BRACKET), (ox, oy), (ox + dx * BRACKET, oy)))
    return out


# ================================================================== PNG ========
def layer(n):
    return Image.new('RGBA', (n, n), (0, 0, 0, 0))


def render_png(path, size=SIZE, ss=4):
    """Renders at `ss` times the target size, then downsamples for clean edges."""
    n = size * ss

    def s(v):
        return v * (n / SIZE)

    def sp(xy):
        return (s(xy[0]), s(xy[1]))

    # --- panel: vertical navy gradient plus the window backdrop's glows ----
    ramp = np.linspace(0.0, 1.0, n, dtype=np.float32)[:, None]
    base = np.zeros((n, n, 3), dtype=np.float32)
    for c in range(3):
        base[:, :, c] = INK_TOP[c] * (1 - ramp) + INK_BOTTOM[c] * ramp

    ys, xs = np.mgrid[0:n, 0:n].astype(np.float32)

    top_glow = np.clip(1.0 - ys / (n * 0.52), 0, 1) ** 2 * 0.42
    dist = np.sqrt((xs - n * 0.28) ** 2 + (ys - n * 0.96) ** 2) / (n * 0.66)
    gold_pool = np.clip(1.0 - dist, 0, 1) ** 2 * 0.34
    gdist = np.sqrt((xs - n / 2) ** 2 + (ys - n / 2) ** 2) / (n * 0.46)
    halo = np.clip(1.0 - gdist, 0, 1) ** 2.0 * 0.50

    for c in range(3):
        base[:, :, c] += CRYSTAL[c] * top_glow
        base[:, :, c] += GOLD[c] * gold_pool
        base[:, :, c] += GOLD[c] * halo

    panel = Image.fromarray(np.clip(base, 0, 255).astype(np.uint8), 'RGB').convert('RGBA')

    mask = Image.new('L', (n, n), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, n - 1, n - 1], radius=s(CORNER), fill=255)
    panel.putalpha(mask)

    # Each translucent pass gets its own layer: ImageDraw *replaces* pixels rather
    # than blending, so drawing them straight onto one overlay punches holes.
    out = panel

    # --- brass frame -------------------------------------------------------
    frame = layer(n)
    ImageDraw.Draw(frame).rounded_rectangle(
        [s(FRAME_INSET), s(FRAME_INSET), s(SIZE - FRAME_INSET), s(SIZE - FRAME_INSET)],
        radius=s(FRAME_RADIUS), outline=GOLD + (255,), width=int(s(FRAME_WIDTH)))
    out = Image.alpha_composite(out, fade(frame, 0.50))

    brackets = layer(n)
    bd = ImageDraw.Draw(brackets)
    for arm in corner_brackets():
        bd.line([sp(arm[0]), sp(arm[1]), sp(arm[2])],
                fill=GOLD_BRIGHT + (255,), width=int(s(FRAME_WIDTH + 1.5)), joint='curve')
    out = Image.alpha_composite(out, fade(brackets, 0.78))

    # --- gem ---------------------------------------------------------------
    p = gem_points()

    gem = layer(n)
    gd = ImageDraw.Draw(gem)
    for name, poly in facet_polygons(p):
        gd.polygon([sp(xy) for xy in poly], fill=FACETS[name] + (255,))
    out = Image.alpha_composite(out, gem)

    seams = layer(n)
    sd = ImageDraw.Draw(seams)
    for a, b in seam_pairs():
        sd.line([sp(p[a]), sp(p[b])], fill=SEAM + (255,), width=max(int(s(1.8)), 1))
    out = Image.alpha_composite(out, fade(seams, 0.45))

    glint = layer(n)
    ImageDraw.Draw(glint).polygon([sp(xy) for xy in glint_polygon(p)], fill=(255, 255, 255, 255))
    out = Image.alpha_composite(out, fade(glint, 0.30))

    edge = layer(n)
    ImageDraw.Draw(edge).polygon(
        [sp(p['top']), sp(p['right']), sp(p['bottom']), sp(p['left'])],
        outline=GOLD_BRIGHT + (255,), width=int(s(4)))
    out = Image.alpha_composite(out, edge)

    # --- motes -------------------------------------------------------------
    for (x, y, r, a) in SPARKLES:
        star = layer(n)
        draw_sparkle(ImageDraw.Draw(star), s(x), s(y), s(r))
        out = Image.alpha_composite(out, fade(star, a))

    icon = out.resize((size, size), Image.LANCZOS)
    icon.save(path)
    return icon


def fade(img, alpha):
    """Scales a layer's alpha channel, so it composites at the intended opacity."""
    r, g, b, a = img.split()
    return Image.merge('RGBA', (r, g, b, a.point(lambda v: int(v * alpha))))


def draw_sparkle(d, x, y, r):
    """A four-point star: the mote shape the overlay's particle effects use."""
    waist = r * 0.16
    d.polygon([
        (x, y - r), (x + waist, y - waist), (x + r, y), (x + waist, y + waist),
        (x, y + r), (x - waist, y + waist), (x - r, y), (x - waist, y - waist),
    ], fill=GOLD_BRIGHT + (255,))
    d.ellipse([x - r * 0.2, y - r * 0.2, x + r * 0.2, y + r * 0.2], fill=(255, 255, 255, 255))


# ================================================================== SVG ========
def hexc(rgb):
    return '#%02X%02X%02X' % rgb


def poly_attr(points):
    return ' '.join('%.1f,%.1f' % xy for xy in points)


def render_svg(path, size=SIZE):
    p = gem_points()

    facets = '\n'.join(
        '    <polygon points="%s" fill="%s"/>' % (poly_attr(poly), hexc(FACETS[name]))
        for name, poly in facet_polygons(p))

    seams = '\n'.join(
        '      <line x1="%.1f" y1="%.1f" x2="%.1f" y2="%.1f"/>' % (p[a] + p[b])
        for a, b in seam_pairs())

    brackets = '\n'.join(
        '    <path d="M %.1f %.1f L %.1f %.1f L %.1f %.1f"/>' % (arm[0] + arm[1] + arm[2])
        for arm in corner_brackets())

    sparkles = '\n'.join(
        '  <g opacity="%.2f"><polygon points="%s" fill="%s"/>'
        '<circle cx="%.1f" cy="%.1f" r="%.2f" fill="#FFFFFF"/></g>' % (
            a,
            poly_attr([(x, y - r), (x + r * 0.16, y - r * 0.16), (x + r, y), (x + r * 0.16, y + r * 0.16),
                       (x, y + r), (x - r * 0.16, y + r * 0.16), (x - r, y), (x - r * 0.16, y - r * 0.16)]),
            hexc(GOLD_BRIGHT), x, y, r * 0.2)
        for (x, y, r, a) in SPARKLES)

    svg = f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {size} {size}" width="{size}" height="{size}">
  <title>LootView</title>
  <defs>
    <linearGradient id="panel" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="{hexc(INK_TOP)}"/>
      <stop offset="1" stop-color="{hexc(INK_BOTTOM)}"/>
    </linearGradient>
    <linearGradient id="topGlow" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="{hexc(CRYSTAL)}" stop-opacity="0.42"/>
      <stop offset="1" stop-color="{hexc(CRYSTAL)}" stop-opacity="0"/>
    </linearGradient>
    <radialGradient id="goldPool" cx="0.28" cy="0.96" r="0.66">
      <stop offset="0" stop-color="{hexc(GOLD)}" stop-opacity="0.34"/>
      <stop offset="1" stop-color="{hexc(GOLD)}" stop-opacity="0"/>
    </radialGradient>
    <radialGradient id="halo" cx="0.5" cy="0.5" r="0.46">
      <stop offset="0" stop-color="{hexc(GOLD)}" stop-opacity="0.50"/>
      <stop offset="1" stop-color="{hexc(GOLD)}" stop-opacity="0"/>
    </radialGradient>
    <clipPath id="panelClip">
      <rect width="{size}" height="{size}" rx="{CORNER}"/>
    </clipPath>
  </defs>

  <!-- Panel: the deep-navy glass every LootView window is built from -->
  <g clip-path="url(#panelClip)">
    <rect width="{size}" height="{size}" fill="url(#panel)"/>
    <rect width="{size}" height="{size * 0.52:.0f}" fill="url(#topGlow)"/>
    <rect width="{size}" height="{size}" fill="url(#goldPool)"/>
    <rect width="{size}" height="{size}" fill="url(#halo)"/>
  </g>

  <!-- Brass frame and corner filigree -->
  <rect x="{FRAME_INSET}" y="{FRAME_INSET}" width="{SIZE - FRAME_INSET * 2}" height="{SIZE - FRAME_INSET * 2}"
        rx="{FRAME_RADIUS}" fill="none" stroke="{hexc(GOLD)}" stroke-opacity="0.50" stroke-width="{FRAME_WIDTH}"/>
  <g fill="none" stroke="{hexc(GOLD_BRIGHT)}" stroke-opacity="0.78" stroke-width="{FRAME_WIDTH + 1.5}"
     stroke-linecap="round" stroke-linejoin="round">
{brackets}
  </g>

  <!-- The aetheryte crest, cut into gem facets -->
  <g>
{facets}
    <g stroke="{hexc(SEAM)}" stroke-opacity="0.45" stroke-width="1.8">
{seams}
    </g>
    <polygon points="{poly_attr([p['top'], p['right'], p['bottom'], p['left']])}"
             fill="#FFFFFF" opacity="0.0"/>
    <polygon points="{poly_attr(glint_polygon(p))}" fill="#FFFFFF" opacity="0.30"/>
    <polygon points="{poly_attr([p['top'], p['right'], p['bottom'], p['left']])}"
             fill="none" stroke="{hexc(GOLD_BRIGHT)}" stroke-width="4" stroke-linejoin="round"/>
  </g>

  <!-- Motes, echoing the overlay's drop particles -->
{sparkles}
</svg>
'''
    with open(path, 'w', encoding='utf-8') as f:
        f.write(svg)


if __name__ == '__main__':
    import sys
    out = sys.argv[1] if len(sys.argv) > 1 else '.'
    render_svg(f'{out}/icon.svg')
    img = render_png(f'{out}/icon.png')
    print('wrote icon.svg and icon.png', img.size)
