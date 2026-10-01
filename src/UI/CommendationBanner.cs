using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using LootView.Services;

namespace LootView.UI;

/// <summary>
/// A large banner across the upper middle of the screen when the player receives a player
/// commendation. The count is read from the game rather than the chat, so it works in every
/// client language.
///
/// Commendations land as you leave a duty, during the loading screen, often several at once or a
/// few seconds apart. They are held until the zone has loaded, and there is only ever one banner:
/// further commendations tick its counter up instead of stacking a second one.
/// </summary>
public sealed unsafe class CommendationBanner : IDisposable
{
    public const float MinScale = 0.5f;
    public const float MaxScale = 2f;

    private const float FadeInSeconds = 0.25f;
    private const float HoldSeconds = 6f;
    private const float FadeOutSeconds = 0.9f;
    private const float TotalSeconds = FadeInSeconds + HoldSeconds + FadeOutSeconds;

    /// <summary>Time between counter steps when several arrive at once, so each one registers.</summary>
    private const float TickSeconds = 0.15f;

    /// <summary>How long the counter's pop lasts after each step.</summary>
    private const float PopSeconds = 0.3f;

    /// <summary>Reading one field is cheap, so the count is checked every frame.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.Zero;

    /// <summary>Grace after a loading screen, so the banner doesn't play over the fade-in.</summary>
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(0.5);

    private readonly ConfigurationService configService;
    private readonly IFontHandle titleFont;

    private DateTime lastPoll = DateTime.MinValue;
    private ulong contentId;
    private int lastCount = -1;

    /// <summary>Received but not shown yet, because a loading screen was up.</summary>
    private int pending;
    private DateTime readySince = DateTime.MinValue;

    private DateTime shownAt = DateTime.MinValue;
    private int gained;     // everything this banner is announcing
    private int counted;    // how far its counter has ticked
    private DateTime lastTick = DateTime.MinValue;
    private int total;

    public CommendationBanner(ConfigurationService configService)
    {
        this.configService = configService;
        titleFont = Plugin.PluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(
            new GameFontStyle(GameFontFamilyAndSize.TrumpGothic68));
    }

    /// <summary>
    /// Plays the banner as if a commendation had just arrived, for the settings preview.
    /// Pressing it again while it is up steps the counter, the same as a real second one.
    /// </summary>
    public void Preview() => Show(1, Math.Max(Math.Max(lastCount, 0), total) + 1);

    private bool IsVisible => (DateTime.Now - shownAt).TotalSeconds < TotalSeconds;

    public void Draw()
    {
        Poll();
        Tick();

        var age = (float)(DateTime.Now - shownAt).TotalSeconds;
        if (age >= TotalSeconds) return;

        try
        {
            DrawBanner(age);
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Failed to draw the commendation banner");
        }
    }

    private void Poll()
    {
        var now = DateTime.Now;
        if (now - lastPoll < PollInterval) return;
        lastPoll = now;

        var state = PlayerState.Instance();
        if (!Plugin.ClientState.IsLoggedIn || state == null || !state->IsLoaded)
        {
            lastCount = -1;
            return;
        }

        // A different character, or the first reading after login, only sets the baseline.
        if (state->ContentId != contentId)
        {
            contentId = state->ContentId;
            lastCount = -1;
        }

        int count = state->PlayerCommendations;
        if (lastCount >= 0 && count > lastCount && configService.Configuration.ShowCommendationBanner)
        {
            pending += count - lastCount;
            total = count;
        }

        lastCount = count;

        // Hold everything while the screen is loading, then give the zone a moment to fade in.
        var busy = Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51];
        if (busy)
        {
            readySince = DateTime.MaxValue;
            return;
        }

        if (readySince == DateTime.MaxValue) readySince = now;
        if (pending > 0 && now - readySince >= SettleTime)
        {
            Show(pending, total);
            pending = 0;
        }
    }

    private void Show(int amount, int newTotal)
    {
        total = newTotal;

        // Only one banner at a time: more arriving while it is up add to its counter.
        if (IsVisible)
        {
            gained += amount;
            Hold();
            return;
        }

        gained = amount;
        counted = 1;
        lastTick = DateTime.Now;
        shownAt = DateTime.Now;
    }

    /// <summary>Steps the counter toward the total, one commendation at a time.</summary>
    private void Tick()
    {
        if (!IsVisible || counted >= gained) return;
        if ((DateTime.Now - lastTick).TotalSeconds < TickSeconds) return;

        counted++;
        lastTick = DateTime.Now;
        Hold();
    }

    /// <summary>Keeps the banner fully visible for another full hold, bringing it back if it was fading.</summary>
    private void Hold()
    {
        var age = (DateTime.Now - shownAt).TotalSeconds;
        if (age > FadeInSeconds)
            shownAt = DateTime.Now.AddSeconds(-FadeInSeconds);
    }

    private void DrawBanner(float age)
    {
        // Ease in rising into place, hold, then drift up while fading out.
        float alpha, rise;
        if (age < FadeInSeconds)
        {
            var t = age / FadeInSeconds;
            var eased = 1f - MathF.Pow(1f - t, 3f);
            alpha = eased;
            rise = (1f - eased) * 14f;
        }
        else if (age < FadeInSeconds + HoldSeconds)
        {
            alpha = 1f;
            rise = 0f;
        }
        else
        {
            var t = (age - FadeInSeconds - HoldSeconds) / FadeOutSeconds;
            alpha = 1f - t * t;
            rise = -t * 10f;
        }

        var size = Math.Clamp(configService.Configuration.CommendationBannerScale, MinScale, MaxScale);
        var viewport = ImGui.GetMainViewport();
        var dl = ImGui.GetForegroundDrawList(viewport);
        var scale = ImGuiHelpers.GlobalScale * size;
        var centerX = viewport.Pos.X + viewport.Size.X * 0.5f;
        var top = viewport.Pos.Y + viewport.Size.Y * 0.2f + rise * scale;

        var title = counted > 1 ? "Player Commendations" : "Player Commendation!";
        var counter = counted > 1 ? $"x{counted}" : string.Empty;

        // The total follows the counter, so it never runs ahead of what has been announced.
        var detail = $"You have {total - (gained - counted):N0} in total";

        // --- Measure, in the game's own headline font --------------------
        var titleSize = 52f * scale;
        var counterSize = titleSize * 1.2f;
        var counterGap = 16f * scale;
        Vector2 titleExtent, counterExtent = Vector2.Zero;
        ImFontPtr font;
        using (titleFont.Available ? titleFont.Push() : null)
        {
            font = ImGui.GetFont();
            titleExtent = ImGui.CalcTextSize(title) * (titleSize / ImGui.GetFontSize());
            if (counter.Length > 0)
                counterExtent = ImGui.CalcTextSize(counter) * (counterSize / ImGui.GetFontSize());
        }

        // Title and counter are centred as one line.
        var lineWidth = titleExtent.X + (counter.Length > 0 ? counterGap + counterExtent.X : 0f);

        // --- Backing band -------------------------------------------------
        // A soft dark band behind the text keeps it legible over any scene.
        var bandHalfW = Math.Max(lineWidth * 0.5f + 90f * scale, 240f * scale);
        var bandTop = top - 16f * scale;
        var bandBottom = top + titleExtent.Y + 44f * scale;
        var shade = ImGui.GetColorU32(Theme.Alpha(Theme.Ink, 0.55f * alpha));
        var clear = ImGui.GetColorU32(Theme.Alpha(Theme.Ink, 0f));
        dl.AddRectFilledMultiColor(new Vector2(centerX - bandHalfW, bandTop), new Vector2(centerX, bandBottom),
            clear, shade, shade, clear);
        dl.AddRectFilledMultiColor(new Vector2(centerX, bandTop), new Vector2(centerX + bandHalfW, bandBottom),
            shade, clear, clear, shade);

        // Brass hairlines above and below, fading out toward the edges.
        var line = Theme.U32(Theme.Gold, 0.8f * alpha);
        foreach (var y in new[] { bandTop, bandBottom })
        {
            dl.AddRectFilledMultiColor(new Vector2(centerX - bandHalfW, y), new Vector2(centerX, y + 1.5f * scale),
                clear, line, line, clear);
            dl.AddRectFilledMultiColor(new Vector2(centerX, y), new Vector2(centerX + bandHalfW, y + 1.5f * scale),
                line, clear, clear, line);
        }

        // Medal glyph centred on the top hairline.
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var glyph = FontAwesomeIcon.Medal.ToIconString();
            var glyphSize = ImGui.GetFontSize() * size;
            var gs = ImGui.CalcTextSize(glyph) * size;
            dl.AddCircleFilled(new Vector2(centerX, bandTop), gs.Y * 0.85f, Theme.U32(Theme.Ink, 0.9f * alpha), 24);
            dl.AddText(ImGui.GetFont(), glyphSize, new Vector2(centerX - gs.X * 0.5f, bandTop - gs.Y * 0.5f),
                Theme.U32(Theme.GoldBright, alpha), glyph);
        }

        // --- Title and counter --------------------------------------------
        var titlePos = new Vector2(centerX - lineWidth * 0.5f, top);
        var outline = Theme.U32(Theme.Ink, 0.9f * alpha);
        DrawOutlined(dl, font, titleSize, titlePos, title, Theme.U32(Theme.GoldBright, alpha), outline, scale);

        // The counter pops each time it steps up, growing from its own centre.
        if (counter.Length > 0)
        {
            var popT = (float)(DateTime.Now - lastTick).TotalSeconds / PopSeconds;
            var pop = popT < 1f ? 1f + 0.35f * (1f - popT) * (1f - popT) : 1f;
            var extent = counterExtent * pop;
            var anchor = new Vector2(titlePos.X + titleExtent.X + counterGap + counterExtent.X * 0.5f,
                top + titleExtent.Y - counterExtent.Y * 0.5f);

            if (popT < 1f)
                dl.AddCircleFilled(anchor, extent.Y * 0.55f, Theme.U32(Theme.Gold, 0.25f * (1f - popT) * alpha), 32);

            DrawOutlined(dl, font, counterSize * pop, anchor - extent * 0.5f, counter,
                Theme.U32(Theme.Lighten(Theme.GoldBright, 0.3f), alpha), outline, scale);
        }

        // --- Running total, in the regular UI font ----------------------
        var detailSize = ImGui.GetFontSize() * 1.15f * size;
        var detailExtent = ImGui.CalcTextSize(detail) * 1.15f * size;
        var detailPos = new Vector2(centerX - detailExtent.X * 0.5f, top + titleExtent.Y + 8f * scale);
        dl.AddText(ImGui.GetFont(), detailSize, detailPos + new Vector2(1, 1) * scale, outline, detail);
        dl.AddText(ImGui.GetFont(), detailSize, detailPos, Theme.U32(Theme.Text, alpha), detail);
    }

    private static void DrawOutlined(ImDrawListPtr dl, ImFontPtr font, float size, Vector2 pos, string text, uint color, uint outline, float scale)
    {
        for (var dx = -2; dx <= 2; dx += 2)
        for (var dy = -2; dy <= 2; dy += 2)
        {
            if (dx == 0 && dy == 0) continue;
            dl.AddText(font, size, pos + new Vector2(dx, dy) * scale * 0.75f, outline, text);
        }
        dl.AddText(font, size, pos, color, text);
    }

    public void Dispose() => titleFont.Dispose();
}
