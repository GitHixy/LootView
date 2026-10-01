using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using LootView.Services;
using LootView.UI;

namespace LootView.Windows;

/// <summary>
/// The currency drawer: a narrow panel that slides out from behind one side of the loot window,
/// listing every currency the player holds with its cap and weekly limit. It is its own ImGui
/// window, placed against the loot window every frame, so it follows it when moved or resized.
/// </summary>
public sealed class CurrencyDrawer
{
    // Measurements follow the loot window's scale, which is still current when the drawer draws.
    private static float DrawerWidth => Theme.Px(210f);
    private static float Gap => Theme.Px(4f);
    private static float RowHeight => Theme.Px(24f);
    private static float IconSize => Theme.Px(18f);
    private static float Pad => Theme.Px(8f);
    private static float HeaderHeight => Theme.Px(26f);

    /// <summary>Seconds a full open or close takes.</summary>
    private const float SlideSeconds = 0.28f;

    /// <summary>How long a currency gain stays highlighted.</summary>
    public const float GainSeconds = 4f;

    private readonly Plugin plugin;
    private readonly CurrencyService currencies = new();

    /// <summary>0 closed, 1 open; eased when drawn.</summary>
    private float progress;

    public CurrencyDrawer(Plugin plugin)
    {
        this.plugin = plugin;
        progress = plugin.ConfigService.Configuration.CurrencyPanelOpen ? 1f : 0f;
    }

    public void RequestRefresh() => currencies.RequestRefresh();

    /// <summary>A currency went up in the last few seconds; the toolbar button uses it to hint at a closed drawer.</summary>
    public bool HasRecentGain
    {
        get
        {
            var now = DateTime.Now;
            foreach (var c in currencies.Entries)
                if ((now - c.GainedAt).TotalSeconds < GainSeconds) return true;
            return false;
        }
    }

    /// <summary>
    /// Keeps the readings fresh even while the drawer is shut, so a gain made with it closed
    /// is not flashed again the moment it opens.
    /// </summary>
    public void Update() => currencies.Update();

    /// <param name="hostPos">Top-left of the loot window, in screen space.</param>
    /// <param name="hostSize">Size of the loot window.</param>
    public void Draw(Vector2 hostPos, Vector2 hostSize)
    {
        var config = plugin.ConfigService.Configuration;
        if (!config.ShowCurrencyPanel) return;

        var target = config.CurrencyPanelOpen ? 1f : 0f;
        var step = ImGui.GetIO().DeltaTime / SlideSeconds;
        progress = progress < target ? Math.Min(progress + step, target) : Math.Max(progress - step, target);

        // Ease-out-cubic on the way out, ease-in on the way back, so it settles softly when
        // opening and tucks away decisively when closing.
        var eased = config.CurrencyPanelOpen
            ? 1f - MathF.Pow(1f - progress, 3f)
            : progress * progress * progress;

        var visible = DrawerWidth * eased;
        if (visible < 1f) return;

        var onLeft = config.CurrencyPanelOnLeft;
        var top = hostPos.Y + 6f;
        var height = Math.Max(hostSize.Y - 12f, HeaderHeight + RowHeight);
        var x = onLeft ? hostPos.X - Gap - visible : hostPos.X + hostSize.X + Gap;

        ImGui.SetNextWindowPos(new Vector2(x, top));
        ImGui.SetNextWindowSize(new Vector2(visible, height));
        ImGui.SetNextWindowBgAlpha(config.BackgroundAlpha);

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove |
                                       ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings |
                                       ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav |
                                       ImGuiWindowFlags.NoScrollWithMouse;

        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (ImGui.Begin("##LootViewCurrencyDrawer", flags))
        {
            try
            {
                ImGui.SetWindowFontScale(Theme.UiScale);
                Theme.DrawWindowBackdrop();

                // The contents are laid out at full width and pinned to the outer edge, so the
                // drawer looks pulled out from behind the loot window rather than unrolled.
                var winPos = ImGui.GetWindowPos();
                var originX = onLeft ? winPos.X : winPos.X + visible - DrawerWidth;
                DrawContents(new Vector2(originX, winPos.Y), height, eased);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Error drawing the currency drawer");
            }
        }
        ImGui.End();
    }

    private void DrawContents(Vector2 origin, float height, float eased)
    {
        var dl = ImGui.GetWindowDrawList();
        var list = currencies.Entries;
        var now = DateTime.Now;

        // --- Header ---------------------------------------------------
        var headerY = origin.Y + (HeaderHeight - ImGui.GetTextLineHeight()) * 0.5f + 2f;
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            dl.AddText(new Vector2(origin.X + Pad, headerY), Theme.U32(Theme.Gold, eased),
                FontAwesomeIcon.Coins.ToIconString());
        }

        using (new Theme.FontScale(0.85f))
        {
            var labelY = origin.Y + (HeaderHeight - ImGui.GetTextLineHeight()) * 0.5f + 2f;
            dl.AddText(new Vector2(origin.X + Pad + Theme.Px(20f), labelY), Theme.U32(Theme.TextMuted), "CURRENCIES");

            var count = list.Count.ToString();
            var cw = ImGui.CalcTextSize(count).X;
            dl.AddText(new Vector2(origin.X + DrawerWidth - Pad - cw, labelY), Theme.U32(Theme.TextFaint), count);
        }

        dl.AddLine(new Vector2(origin.X + Pad, origin.Y + HeaderHeight + 2f),
            new Vector2(origin.X + DrawerWidth - Pad, origin.Y + HeaderHeight + 2f), Theme.U32(Theme.Line, 0.7f));

        // --- List -----------------------------------------------------
        ImGui.SetCursorScreenPos(new Vector2(origin.X + Theme.Px(4f), origin.Y + HeaderHeight + Theme.Px(6f)));
        var listSize = new Vector2(DrawerWidth - Theme.Px(8f), height - HeaderHeight - Theme.Px(10f));

        using var child = ImRaii.Child("##CurrencyList", listSize, false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoBackground);
        if (!child) return;

        // A child window starts at font scale 1; carry the window's scale into it.
        Theme.SetFontScale(1f);

        if (list.Count == 0)
        {
            ImGui.TextColored(Theme.TextFaint, "  Nothing yet");
            return;
        }

        var cdl = ImGui.GetWindowDrawList();
        foreach (var c in list)
            DrawRow(cdl, c, listSize.X, now);
    }

    private static void DrawRow(ImDrawListPtr dl, CurrencyEntry c, float width, DateTime now)
    {
        var rowMin = ImGui.GetCursorScreenPos();
        var rowMax = rowMin + new Vector2(width, RowHeight);

        ImGui.InvisibleButton($"##cur_{c.ItemId}", new Vector2(width, RowHeight));
        var hovered = ImGui.IsItemHovered();

        if (hovered)
            dl.AddRectFilled(rowMin, rowMax, Theme.U32(Theme.SurfaceHover, 0.7f), Theme.Radius);

        var age = (float)(now - c.GainedAt).TotalSeconds;
        var gaining = age < GainSeconds;
        var fade = gaining ? 1f - age / GainSeconds : 0f;
        if (gaining)
        {
            dl.AddRectFilled(rowMin, rowMax, Theme.U32(Theme.Gold, 0.16f * fade), Theme.Radius);
            dl.AddRectFilled(new Vector2(rowMin.X, rowMin.Y + 4f), new Vector2(rowMin.X + 2.5f, rowMax.Y - 4f),
                Theme.U32(Theme.Gold, 0.9f * fade), 1.5f);
        }

        var iconPos = new Vector2(rowMin.X + Theme.Px(5f), rowMin.Y + (RowHeight - IconSize) * 0.5f);
        if (c.IsCommendation)
            DrawGlyph(dl, FontAwesomeIcon.Medal, iconPos, IconSize, Theme.Gold);
        else
            DrawGameIcon(dl, c.IconId, iconPos, IconSize);

        using var font = new Theme.FontScale(0.9f);
        var textY = rowMin.Y + (RowHeight - ImGui.GetTextLineHeight()) * 0.5f;

        // Amount and cap, right aligned.
        var countText = FormatAmount(c.Count);
        var capText = c.Cap > 0 ? $"/{FormatAmount(c.Cap)}" : string.Empty;
        var countW = ImGui.CalcTextSize(countText).X;
        var capW = capText.Length > 0 ? ImGui.CalcTextSize(capText).X : 0f;
        var right = rowMax.X - Theme.Px(5f);

        var fraction = c.Cap > 0 ? (float)c.Count / c.Cap : 0f;
        var amountColor = fraction >= 1f ? Theme.Bad : fraction >= 0.9f ? Theme.Warn : Theme.Text;

        if (capW > 0)
            dl.AddText(new Vector2(right - capW, textY), Theme.U32(Theme.TextFaint), capText);
        dl.AddText(new Vector2(right - capW - countW, textY), Theme.U32(amountColor), countText);

        var nameRight = right - capW - countW - 6f;

        // A fresh gain sits just left of the amount and drifts toward it as it fades.
        if (gaining)
        {
            var gain = $"+{FormatAmount(c.LastGain)}";
            var gw = ImGui.CalcTextSize(gain).X;
            var t = age / GainSeconds;
            var gx = nameRight - gw + 4f * t;
            dl.AddText(new Vector2(gx, textY), Theme.U32(Theme.GoldBright, t < 0.6f ? 1f : fade / 0.4f), gain);
            nameRight = gx - 6f;
        }

        var nameX = iconPos.X + IconSize + Theme.Px(6f);
        Theme.ClipText(dl, new Vector2(nameX, textY), nameRight - nameX, c.Name, Theme.U32(Theme.TextMuted));

        // Weekly progress along the bottom edge, for the tomestone with a weekly limit.
        if (c.WeeklyCap > 0)
        {
            var weekly = Math.Clamp((float)c.Weekly / c.WeeklyCap, 0f, 1f);
            var barMin = new Vector2(nameX, rowMax.Y - 3f);
            var barW = rowMax.X - 5f - nameX;
            dl.AddRectFilled(barMin, new Vector2(barMin.X + barW, rowMax.Y - 1.5f), Theme.U32(Theme.Line, 0.8f), 1f);
            dl.AddRectFilled(barMin, new Vector2(barMin.X + barW * weekly, rowMax.Y - 1.5f),
                Theme.U32(weekly >= 1f ? Theme.Good : Theme.Crystal, 0.9f), 1f);
        }

        if (hovered)
            ShowTooltip(c, now);
    }

    private static void ShowTooltip(CurrencyEntry c, DateTime now)
    {
        var lines = new List<string> { c.Name, string.Empty };

        lines.Add(c.Cap > 0
            ? $"Held: {c.Count:N0} / {c.Cap:N0}" + (c.Count >= c.Cap ? "  (capped)" : $"  ({c.Cap - c.Count:N0} to cap)")
            : $"Held: {c.Count:N0}");

        if (c.WeeklyCap > 0)
            lines.Add($"This week: {c.Weekly:N0} / {c.WeeklyCap:N0}");

        if (c.Remaining >= 0)
            lines.Add($"Can still earn: {c.Remaining:N0}");

        var sinceGain = now - c.GainedAt;
        if (sinceGain.TotalMinutes < 10)
            lines.Add($"Last gain: +{c.LastGain:N0}, {(sinceGain.TotalMinutes < 1 ? $"{sinceGain.Seconds}s" : $"{(int)sinceGain.TotalMinutes}m")} ago");

        Theme.Tooltip(string.Join("\n", lines));
    }

    /// <summary>Currency amount: in full below 100k, so tomestones and seals stay exact, then 512k / 12.3m.</summary>
    private static string FormatAmount(long amount)
    {
        if (amount >= 1_000_000) return $"{amount / 1_000_000d:0.##}m";
        if (amount >= 100_000) return $"{amount / 1_000d:0.#}k";
        return amount.ToString("N0");
    }

    /// <summary>A FontAwesome glyph centred in an icon-sized square, for entries without a game icon.</summary>
    private static void DrawGlyph(ImDrawListPtr dl, FontAwesomeIcon icon, Vector2 pos, float size, Vector4 color)
    {
        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        var glyph = icon.ToIconString();
        var gs = ImGui.CalcTextSize(glyph);
        dl.AddText(pos + (new Vector2(size, size) - gs) * 0.5f, Theme.U32(color), glyph);
    }

    private static void DrawGameIcon(ImDrawListPtr dl, uint iconId, Vector2 pos, float size)
    {
        if (iconId == 0) return;

        try
        {
            var tex = Plugin.TextureProvider
                .GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(iconId))
                .GetWrapOrDefault();
            if (tex != null)
                dl.AddImage(tex.Handle, pos, pos + new Vector2(size, size));
        }
        catch { /* Ignore icon loading errors */ }
    }
}
