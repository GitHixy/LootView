using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace LootView.UI;

/// <summary>
/// Small drawn charts for the statistics views. Everything renders through the
/// window draw list so it inherits the theme and stays crisp at any window size.
/// </summary>
public static class Charts
{
    public readonly record struct Bar(string Label, double Value, string? Tooltip = null);

    /// <summary>
    /// A column chart with a baseline, hover highlight and optional rotated-free labels.
    /// Labels are drawn only when they fit; otherwise every n-th one is kept.
    /// </summary>
    public static void Columns(string id, IReadOnlyList<Bar> data, float height, Vector4 color, bool showLabels = true)
    {
        if (data.Count == 0)
        {
            ImGui.TextColored(Theme.TextFaint, "No data for this range");
            return;
        }

        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = Math.Max(ImGui.GetContentRegionAvail().X, 8f);
        var labelHeight = showLabels ? ImGui.GetTextLineHeight() + 4f : 0f;
        var plotHeight = height - labelHeight;

        var max = 0d;
        foreach (var d in data) max = Math.Max(max, d.Value);
        if (max <= 0) max = 1;

        // Hit area covering the whole plot, so we can resolve the hovered column ourselves.
        ImGui.InvisibleButton(id, new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();
        var mouse = ImGui.GetIO().MousePos;

        var slot = width / data.Count;
        var barWidth = Math.Max(Math.Min(slot - 3f, 34f), 2f);
        var hoverIndex = -1;

        if (hovered)
        {
            var rel = mouse.X - origin.X;
            hoverIndex = Math.Clamp((int)(rel / slot), 0, data.Count - 1);
        }

        // Three faint gridlines give the eye something to measure against.
        for (var g = 1; g <= 3; g++)
        {
            var y = origin.Y + plotHeight * (1f - g / 4f);
            dl.AddLine(new Vector2(origin.X, y), new Vector2(origin.X + width, y), Theme.U32(Theme.LineSoft, 0.8f), 1f);
        }

        for (var i = 0; i < data.Count; i++)
        {
            var d = data[i];
            var frac = (float)(d.Value / max);
            var barHeight = Math.Max(plotHeight * frac, d.Value > 0 ? 2f : 0f);

            var cx = origin.X + slot * i + slot * 0.5f;
            var min = new Vector2(cx - barWidth * 0.5f, origin.Y + plotHeight - barHeight);
            var maxPt = new Vector2(cx + barWidth * 0.5f, origin.Y + plotHeight);

            var isHover = i == hoverIndex;
            var c = isHover ? Theme.Lighten(color, 0.35f) : color;

            if (barHeight > 0.5f)
            {
                dl.AddRectFilledMultiColor(min, maxPt,
                    Theme.U32(Theme.Lighten(c, 0.2f), isHover ? 1f : 0.9f),
                    Theme.U32(Theme.Lighten(c, 0.2f), isHover ? 1f : 0.9f),
                    Theme.U32(c, 0.35f), Theme.U32(c, 0.35f));
                dl.AddRectFilled(min, new Vector2(maxPt.X, min.Y + 2f), Theme.U32(Theme.Lighten(c, 0.5f)), 1f);
            }

            if (isHover)
            {
                dl.AddRectFilled(
                    new Vector2(min.X - 2, origin.Y),
                    new Vector2(maxPt.X + 2, origin.Y + plotHeight),
                    Theme.U32(color, 0.10f), 3f);
            }
        }

        // Baseline.
        dl.AddLine(
            new Vector2(origin.X, origin.Y + plotHeight),
            new Vector2(origin.X + width, origin.Y + plotHeight),
            Theme.U32(Theme.Line), 1f);

        if (showLabels)
        {
            // Keep every n-th label so they never collide.
            var sample = ImGui.CalcTextSize(data[0].Label).X + 8f;
            var step = Math.Max((int)Math.Ceiling(sample / slot), 1);

            for (var i = 0; i < data.Count; i += step)
            {
                var ts = ImGui.CalcTextSize(data[i].Label);
                var cx = origin.X + slot * i + slot * 0.5f;
                dl.AddText(new Vector2(cx - ts.X * 0.5f, origin.Y + plotHeight + 4f),
                    Theme.U32(i == hoverIndex ? Theme.Text : Theme.TextFaint), data[i].Label);
            }
        }

        if (hoverIndex >= 0)
        {
            var d = data[hoverIndex];
            Theme.Tooltip(d.Tooltip ?? $"{d.Label}\n{d.Value:N0}");
        }
    }

    /// <summary>
    /// A labelled horizontal bar - the shape used for rankings like top zones or rarity splits.
    /// </summary>
    public static void RankedBar(string label, double value, double max, Vector4 color, float labelWidth, string? valueText = null)
    {
        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = Math.Max(ImGui.GetContentRegionAvail().X, 8f);
        var height = ImGui.GetTextLineHeight() + 6f;

        ImGui.InvisibleButton($"##rank_{label}", new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();

        var textY = origin.Y + (height - ImGui.GetTextLineHeight()) * 0.5f;

        var trackX = origin.X + labelWidth;
        var suffix = valueText ?? value.ToString("N0");
        var suffixWidth = ImGui.CalcTextSize(suffix).X + 10f;
        var trackWidth = Math.Max(width - labelWidth - suffixWidth, 20f);

        dl.AddRectFilled(
            new Vector2(trackX, origin.Y + 3),
            new Vector2(trackX + trackWidth, origin.Y + height - 3),
            Theme.U32(Theme.Panel, 0.85f), 3f);

        var frac = max > 0 ? (float)Math.Clamp(value / max, 0, 1) : 0f;
        if (frac > 0.001f)
        {
            var fillMax = new Vector2(trackX + Math.Max(trackWidth * frac, 3f), origin.Y + height - 3);
            dl.AddRectFilledMultiColor(
                new Vector2(trackX, origin.Y + 3), fillMax,
                Theme.U32(color, hovered ? 0.85f : 0.6f), Theme.U32(Theme.Lighten(color, 0.3f), hovered ? 1f : 0.9f),
                Theme.U32(Theme.Lighten(color, 0.3f), hovered ? 1f : 0.9f), Theme.U32(color, hovered ? 0.85f : 0.6f));
        }

        // Label sits over the track start, value is pinned to the right.
        Theme.ClipText(dl, new Vector2(origin.X, textY), labelWidth - 8f, label,
            Theme.U32(hovered ? Theme.Text : Theme.TextMuted));

        dl.AddText(new Vector2(origin.X + width - ImGui.CalcTextSize(suffix).X, textY),
            Theme.U32(hovered ? Theme.Lighten(color, 0.3f) : Theme.Text), suffix);
    }

    /// <summary>A row of intensity blocks - the activity heatmap on the analytics tab.</summary>
    public static void HeatStrip(IReadOnlyList<(string Label, double Value)> blocks, float height, Vector4 color)
    {
        if (blocks.Count == 0) return;

        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = Math.Max(ImGui.GetContentRegionAvail().X, 8f);
        var gap = 5f;
        var cell = (width - gap * (blocks.Count - 1)) / blocks.Count;

        var max = 0d;
        foreach (var b in blocks) max = Math.Max(max, b.Value);
        if (max <= 0) max = 1;

        ImGui.InvisibleButton("##heatstrip", new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();
        var mouse = ImGui.GetIO().MousePos;
        var hoverIndex = hovered ? Math.Clamp((int)((mouse.X - origin.X) / (cell + gap)), 0, blocks.Count - 1) : -1;

        for (var i = 0; i < blocks.Count; i++)
        {
            var (label, value) = blocks[i];
            var intensity = (float)(value / max);

            var min = new Vector2(origin.X + i * (cell + gap), origin.Y);
            var maxPt = new Vector2(min.X + cell, origin.Y + height);

            dl.AddRectFilled(min, maxPt, Theme.U32(Theme.Panel, 0.85f), Theme.Radius);
            dl.AddRectFilled(min, maxPt, Theme.U32(color, 0.10f + intensity * 0.55f), Theme.Radius);
            dl.AddRect(min, maxPt, Theme.U32(color, i == hoverIndex ? 0.8f : 0.2f), Theme.Radius, ImDrawFlags.None, 1f);

            var ts = ImGui.CalcTextSize(label);
            dl.AddText(new Vector2(min.X + (cell - ts.X) * 0.5f, min.Y + 8f), Theme.U32(Theme.TextMuted), label);

            var v = value.ToString("N0");
            var vs = ImGui.CalcTextSize(v);
            dl.AddText(new Vector2(min.X + (cell - vs.X) * 0.5f, maxPt.Y - vs.Y - 8f),
                Theme.U32(intensity > 0.5f ? Theme.Lighten(color, 0.5f) : Theme.Text), v);
        }

        if (hoverIndex >= 0)
        {
            var (label, value) = blocks[hoverIndex];
            Theme.Tooltip($"{label}\n{value:N0} items");
        }
    }

    /// <summary>A donut showing how a total splits across categories.</summary>
    public static void Donut(IReadOnlyList<(string Label, double Value, Vector4 Color)> slices, float radius, string? centerLabel = null, string? centerValue = null)
    {
        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var size = radius * 2;
        var center = new Vector2(origin.X + radius, origin.Y + radius);

        var total = 0d;
        foreach (var s in slices) total += s.Value;

        ImGui.InvisibleButton("##donut", new Vector2(size, size));
        var hovered = ImGui.IsItemHovered();
        var mouse = ImGui.GetIO().MousePos;

        var inner = radius * 0.62f;
        var hoverIndex = -1;

        if (hovered && total > 0)
        {
            var d = mouse - center;
            var dist = d.Length();
            if (dist >= inner && dist <= radius)
            {
                var angle = MathF.Atan2(d.Y, d.X) + MathF.PI * 0.5f;
                if (angle < 0) angle += MathF.PI * 2;

                var acc = 0d;
                for (var i = 0; i < slices.Count; i++)
                {
                    acc += slices[i].Value;
                    if (angle <= (float)(acc / total) * MathF.PI * 2)
                    {
                        hoverIndex = i;
                        break;
                    }
                }
            }
        }

        if (total <= 0)
        {
            dl.AddCircle(center, radius * 0.85f, Theme.U32(Theme.Line), 48, 8f);
        }
        else
        {
            var start = -MathF.PI * 0.5f;
            for (var i = 0; i < slices.Count; i++)
            {
                var sweep = (float)(slices[i].Value / total) * MathF.PI * 2;
                if (sweep <= 0.0001f) continue;

                var r = i == hoverIndex ? radius + 3f : radius;
                var thickness = r - inner;

                dl.PathClear();
                dl.PathArcTo(center, (r + inner) * 0.5f, start, start + sweep, Math.Max((int)(sweep * 24), 4));
                dl.PathStroke(Theme.U32(slices[i].Color, i == hoverIndex ? 1f : 0.85f), ImDrawFlags.None, thickness);

                start += sweep;
            }
        }

        if (centerValue is not null)
        {
            using var s = new Theme.FontScale(1.25f);
            var ts = ImGui.CalcTextSize(centerValue);
            dl.AddText(new Vector2(center.X - ts.X * 0.5f, center.Y - ts.Y * 0.5f - 4), Theme.U32(Theme.Text), centerValue);
        }

        if (centerLabel is not null)
        {
            var ts = ImGui.CalcTextSize(centerLabel);
            dl.AddText(new Vector2(center.X - ts.X * 0.5f, center.Y + 8), Theme.U32(Theme.TextFaint), centerLabel);
        }

        if (hoverIndex >= 0)
        {
            var s = slices[hoverIndex];
            Theme.Tooltip($"{s.Label}\n{s.Value:N0}  ({s.Value / total:P1})");
        }
    }
}
