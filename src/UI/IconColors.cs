using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Interface.Textures;
using Lumina.Data.Files;

namespace LootView.UI;

/// <summary>
/// The colour an item icon reads as at a glance, measured from the icon's own pixels. Used where
/// an item is drawn as a colour rather than a picture, such as a currency's sand in the loot jar,
/// so a patch's new currency gets a fitting colour without anyone picking one.
/// </summary>
public static class IconColors
{
    private static readonly Dictionary<uint, Vector4> Cache = new();

    public static Vector4 Dominant(uint iconId, Vector4 fallback)
    {
        if (iconId == 0) return fallback;
        if (Cache.TryGetValue(iconId, out var color)) return color;

        color = fallback;
        try
        {
            if (Plugin.TextureProvider.TryGetIconPath(new GameIconLookup(iconId), out var path) &&
                Plugin.DataManager.GetFile<TexFile>(path) is { } tex)
            {
                color = Measure(tex.ImageData) ?? fallback;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, $"Could not measure the colour of icon {iconId}");
        }

        Cache[iconId] = color;
        return color;
    }

    /// <summary>
    /// A weighted average over BGRA pixels. Vivid mid-tone pixels count most: outlines, shadows
    /// and specular highlights would otherwise pull every icon towards the same muddy grey.
    /// </summary>
    private static Vector4? Measure(byte[] bgra)
    {
        double r = 0, g = 0, b = 0, total = 0;

        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            var a = bgra[i + 3] / 255f;
            if (a < 0.5f) continue;

            var pb = bgra[i] / 255f;
            var pg = bgra[i + 1] / 255f;
            var pr = bgra[i + 2] / 255f;

            var max = MathF.Max(pr, MathF.Max(pg, pb));
            var min = MathF.Min(pr, MathF.Min(pg, pb));
            var saturation = max - min;
            var lightness = (max + min) * 0.5f;

            var weight = a * (0.08f + saturation * saturation * 4f) * MathF.Max(1f - MathF.Abs(lightness - 0.55f) * 1.6f, 0.05f);
            r += pr * weight;
            g += pg * weight;
            b += pb * weight;
            total += weight;
        }

        if (total <= 0) return null;

        var c = new Vector3((float)(r / total), (float)(g / total), (float)(b / total));

        // Averaging always washes colour out a little; give some of it back, and keep the result
        // light enough to read against the dark panels.
        var grey = (c.X + c.Y + c.Z) / 3f;
        c = Vector3.Clamp(new Vector3(grey) + (c - new Vector3(grey)) * 1.35f, Vector3.Zero, Vector3.One);
        var peak = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        if (peak < 0.62f && peak > 0) c *= 0.62f / peak;

        return new Vector4(c, 1f);
    }
}
