using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using LootView.Models;
using LootView.UI;

namespace LootView.Windows;

/// <summary>
/// Real-time window showing active roll sessions. Each resolved item disappears after the configured
/// time, and the window closes once none are left.
/// </summary>
public class RollWindow : Window
{
    private const float IconSize = 34f;
    private const float RowHeight = 21f;
    private const float TimerBarHeight = 3f;
    private const float ActionButtonHeight = 28f;

    /// <summary>The action bar's buttons plus the rule and breathing room above them.</summary>
    private const float ActionBarHeight = ActionButtonHeight + 13f;

    private readonly Plugin plugin;

    /// <summary>The celebration played on a card when its winner is announced, in card-local coordinates.</summary>
    private sealed class WinnerBurst
    {
        public DateTime Start = DateTime.Now;
        public DateTime LastUpdate = DateTime.Now;
        public readonly List<ParticleEffect> Particles = [];
    }

    private const float SweepSeconds = 0.9f;
    private const float BurstSeconds = 2.2f;

    private readonly Dictionary<Services.RollInfo, WinnerBurst> bursts = [];
    private readonly Random random = new();

    public RollWindow(Plugin plugin) : base("Loot Rolls###LootViewRolls")
    {
        this.plugin = plugin;

        IsOpen = false;

        WindowFlags = ImGuiWindowFlags.NoTitleBar |
                      ImGuiWindowFlags.NoScrollbar |
                      ImGuiWindowFlags.AlwaysAutoResize |
                      ImGuiWindowFlags.NoResize;

        SizeConstraintMin = new Vector2(360, 50);
        SizeConstraintMax = new Vector2(700, 640);

        plugin.LootTracker.RollsUpdated += OnRollsUpdated;
    }

    /// <summary>Rolls only happen in-game, so never draw this outside a session.</summary>
    protected override bool ShouldDraw => Plugin.ClientState.IsLoggedIn;

    private void OnRollsUpdated()
    {
        var activeRolls = plugin.LootTracker.ActiveRolls;

        if (activeRolls.Count > 0 && !IsOpen)
        {
            IsOpen = true;
            Plugin.Log.Info($"Roll window opened - {activeRolls.Count} active roll(s)");
        }
    }

    private double ResultSeconds => Math.Max(plugin.Configuration.RollResultSeconds, 1);

    /// <summary>Seconds until a resolved item leaves the window, or null while it is still open.</summary>
    private double? SecondsUntilHidden(Services.RollInfo roll)
        => roll.FinishedAt is { } at ? Math.Max(ResultSeconds - (DateTime.Now - at).TotalSeconds, 0) : null;

    protected override void DrawContents()
    {
        try
        {
            plugin.LootTracker.RemoveExpiredRolls();
            var activeRolls = plugin.LootTracker.ActiveRolls;

            if (bursts.Count > 0)
            {
                foreach (var gone in bursts.Keys.Where(r => !activeRolls.Contains(r)).ToList())
                    bursts.Remove(gone);
            }

            if (activeRolls.Count == 0)
            {
                IsOpen = false;
                return;
            }

            DrawHeader(activeRolls);

            var localPlayerName = Plugin.ObjectTable.LocalPlayer?.Name.TextValue ?? "You";

            // Items still waiting for your choice come first, newest on top; resolved ones sink below.
            var ordered = activeRolls
                .OrderBy(r => r.IsFinished)
                .ThenByDescending(r => r.IsFinished ? r.FinishedAt : r.RollStartTime)
                .ToList();

            foreach (var roll in ordered)
            {
                DrawRollCard(roll, localPlayerName);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error drawing roll window");
        }
    }

    private void DrawHeader(IReadOnlyList<Services.RollInfo> activeRolls)
    {
        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        const float h = 28f;
        var width = Math.Max(ImGui.GetContentRegionAvail().X, 340f);

        // Once every item is resolved, the ring counts down to the last one disappearing.
        var center = new Vector2(origin.X + 12f, origin.Y + h * 0.5f);
        double? closingIn = activeRolls.All(r => r.IsFinished)
            ? activeRolls.Max(r => SecondsUntilHidden(r) ?? 0)
            : null;

        if (closingIn is { } closing)
        {
            var fraction = (float)(closing / ResultSeconds);

            dl.AddCircle(center, 11f, Theme.U32(Theme.Line), 32, 2f);

            dl.PathClear();
            const int segs = 32;
            for (var i = 0; i <= segs; i++)
            {
                var a = -MathF.PI * 0.5f + MathF.PI * 2f * fraction * i / segs;
                dl.PathLineTo(new Vector2(center.X + MathF.Cos(a) * 11f, center.Y + MathF.Sin(a) * 11f));
            }
            dl.PathStroke(Theme.U32(fraction < 0.3f ? Theme.Bad : Theme.Gold, 0.95f), ImDrawFlags.None, 2f);
        }
        else
        {
            var pulse = 0.55f + 0.45f * MathF.Sin(Theme.Time * 3.2f);
            dl.AddCircleFilled(center, 11f, Theme.U32(Theme.Gold, 0.10f + pulse * 0.10f), 32);
            dl.AddCircle(center, 11f, Theme.U32(Theme.Gold, 0.5f + pulse * 0.4f), 32, 1.4f);
        }

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var glyph = FontAwesomeIcon.Dice.ToIconString();
            var gs = ImGui.CalcTextSize(glyph);
            dl.AddText(center - gs * 0.5f, Theme.U32(Theme.GoldBright), glyph);
        }

        ImGui.SetCursorScreenPos(new Vector2(origin.X + 30f, origin.Y + 4f));
        using (new Theme.FontScale(1.08f))
        {
            ImGui.TextColored(Theme.GoldBright, "Loot Rolls");
        }

        if (closingIn is { } remaining)
        {
            ImGui.SameLine(0, 10);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.TextFaint, $"closing in {Math.Ceiling(remaining):F0}s");
        }

        // While the game's own loot window is kept hidden, offer a way back to it.
        if (plugin.NativeLootWindow.IsHidden)
        {
            ImGui.SetCursorScreenPos(new Vector2(origin.X + width - 52f, origin.Y + (h - 24f) * 0.5f));
            if (Theme.IconButton("##ShowNative", FontAwesomeIcon.WindowRestore, "Show the game's Need/Greed window", Theme.Crystal, false, 24f))
                plugin.NativeLootWindow.Reveal();
        }

        // Close button, right aligned.
        ImGui.SetCursorScreenPos(new Vector2(origin.X + width - 24f, origin.Y + (h - 24f) * 0.5f));
        if (Theme.IconButton("##CloseRolls", FontAwesomeIcon.Times, "Close and clear all rolls", Theme.Bad, false, 24f))
        {
            IsOpen = false;
            plugin.LootTracker.ClearAllRolls();
        }

        ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + h));
        ImGui.Dummy(new Vector2(width, 0));
        Theme.Rule(4f);
    }

    private void DrawRollCard(Services.RollInfo rollInfo, string localPlayerName)
    {
        // Keyed by the session rather than its position, so an open menu survives the list reordering.
        using var id = ImRaii.PushId(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(rollInfo));

        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = Math.Max(ImGui.GetContentRegionAvail().X, 340f);

        var sortedRolls = rollInfo.GetSortedRolls().ToList();
        var rarityColor = Theme.RarityColor(rollInfo.Rarity);
        var won = !string.IsNullOrEmpty(rollInfo.WinnerName);
        var finished = rollInfo.IsFinished;

        // Party members who haven't chosen yet, while the item is still open.
        var waiting = finished
            ? []
            : plugin.LootTracker.PartyMemberNames()
                .Where(n => !rollInfo.PlayerRolls.Keys.Any(k => Services.LootTrackingService.SameName(k, n)))
                .ToList();

        var placeholderRow = sortedRolls.Count == 0 && waiting.Count == 0 ? 1 : 0;
        var rowsHeight = (sortedRolls.Count + placeholderRow + (waiting.Count > 0 ? 1 : 0)) * RowHeight;
        var actionBar = !finished && plugin.Configuration.ShowRollButtons &&
                        (Services.LootTrackingService.IsRollPending(rollInfo) || plugin.LootTracker.GetRollOptions(rollInfo).Any);
        var cardHeight = 14f + IconSize + 8f + rowsHeight + (actionBar ? ActionBarHeight : 0) + (finished ? 0 : TimerBarHeight + 6f);

        var max = new Vector2(origin.X + width, origin.Y + cardHeight);

        // Card shell, tinted by the item's rarity.
        dl.AddRectFilled(origin, max, Theme.U32(Theme.Surface, 0.85f), Theme.Radius);
        dl.AddRectFilledMultiColor(origin, max,
            Theme.U32(rarityColor, 0.12f), Theme.U32(rarityColor, 0.03f),
            Theme.U32(rarityColor, 0f), Theme.U32(rarityColor, 0.05f));
        dl.AddRect(origin, max, Theme.U32(rarityColor, finished ? 0.5f : 0.3f), Theme.Radius, ImDrawFlags.None, 1f);
        dl.AddRectFilled(new Vector2(origin.X, origin.Y + 6), new Vector2(origin.X + 2.5f, max.Y - 6),
            Theme.U32(rarityColor, 0.9f), 1.5f);

        // Item icon.
        var iconPos = new Vector2(origin.X + 12f, origin.Y + 9f);
        if (rollInfo.IconId > 0)
        {
            try
            {
                var tex = Plugin.TextureProvider
                    .GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(rollInfo.IconId))
                    .GetWrapOrDefault();
                if (tex != null)
                {
                    if (!finished)
                    {
                        // Undecided items breathe, so your eye goes to what's still in play.
                        var pulse = 0.5f + 0.5f * MathF.Sin(Theme.Time * 3f);
                        dl.AddRectFilled(iconPos - new Vector2(3, 3),
                            iconPos + new Vector2(IconSize + 3, IconSize + 3),
                            Theme.U32(rarityColor, 0.12f + pulse * 0.18f), 6f);
                    }

                    dl.AddImage(tex.Handle, iconPos, iconPos + new Vector2(IconSize, IconSize));
                    dl.AddRect(iconPos, iconPos + new Vector2(IconSize, IconSize),
                        Theme.U32(rarityColor, 0.7f), 4f, ImDrawFlags.None, 1f);
                    ItemUnlocks.DrawIconSeal(dl, iconPos + new Vector2(IconSize, IconSize), UnlockStatusOf(rollInfo), 14f);
                }
            }
            catch { /* Ignore icon errors */ }
        }

        // Item name + state.
        var textX = origin.X + 12f + IconSize + 11f;
        ImGui.SetCursorScreenPos(new Vector2(textX, origin.Y + 10f));
        ImGui.TextColored(rarityColor, rollInfo.ItemName);
        DrawItemInteractions(rollInfo, drawMenu: false);

        // The icon answers to hover and right-click just like the name.
        ImGui.SetCursorScreenPos(iconPos);
        ImGui.InvisibleButton("##icon", new Vector2(IconSize, IconSize));
        DrawItemInteractions(rollInfo, drawMenu: true);

        // Time left to roll, or until a resolved item leaves the window, right aligned on the name line.
        if (SecondsUntilHidden(rollInfo) is { } hideIn)
        {
            var hideText = $"{Math.Ceiling(hideIn):F0}s";
            var hw = ImGui.CalcTextSize(hideText).X;
            dl.AddText(new Vector2(origin.X + width - 14f - hw, origin.Y + 10f), Theme.U32(Theme.TextFaint), hideText);
        }
        else
        {
            var left = (int)Math.Ceiling(rollInfo.SecondsLeft);
            var timerText = $"{left / 60}:{left % 60:00}";
            var timerColor = TimerColor(left);
            var tw = ImGui.CalcTextSize(timerText).X;

            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                var glyph = FontAwesomeIcon.Stopwatch.ToIconString();
                var gw = ImGui.CalcTextSize(glyph).X;
                dl.AddText(new Vector2(origin.X + width - 14f - tw - 6f - gw, origin.Y + 11f), Theme.U32(timerColor, 0.9f), glyph);
            }
            dl.AddText(new Vector2(origin.X + width - 14f - tw, origin.Y + 10f), Theme.U32(timerColor), timerText);
        }

        ImGui.SetCursorScreenPos(new Vector2(textX, origin.Y + 10f + ImGui.GetTextLineHeight() + 3f));
        if (won)
        {
            Theme.Badge($"Won by {rollInfo.WinnerName}", Theme.Gold);
        }
        else if (rollInfo.CloseReason == Services.RollCloseReason.LeftDuty)
        {
            Theme.Badge("You left before the result", Theme.TextMuted);
        }
        else if (finished)
        {
            Theme.Badge(rollInfo.RolledCount > 0 ? "Closed" : "Nobody rolled", Theme.TextMuted);
        }
        else
        {
            Theme.Badge($"{rollInfo.PlayerRolls.Count} decided", Theme.Crystal);
        }

        // Roll rows.
        var rowY = origin.Y + 9f + IconSize + 8f;
        if (placeholderRow > 0)
        {
            dl.AddText(new Vector2(origin.X + 18f, rowY), Theme.U32(Theme.TextFaint), "Waiting for rolls...");
            rowY += RowHeight;
        }

        var winnerRowY = -1f;
        foreach (var (playerName, rollType, rollValue, isWinner) in sortedRolls)
        {
            if (isWinner) winnerRowY = rowY - origin.Y;
            DrawRollRow(dl, new Vector2(origin.X, rowY), width, playerName, rollType, rollValue, isWinner, localPlayerName);
            rowY += RowHeight;
        }

        if (waiting.Count > 0)
        {
            var textY = rowY + (RowHeight - ImGui.GetTextLineHeight()) * 0.5f;
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                dl.AddText(new Vector2(origin.X + 22f, textY + 1f), Theme.U32(Theme.TextFaint),
                    FontAwesomeIcon.HourglassHalf.ToIconString());
            }
            Theme.ClipText(dl, new Vector2(origin.X + 40f, textY), width - 58f,
                $"Waiting on {string.Join(", ", waiting)}", Theme.U32(Theme.TextFaint));
            rowY += RowHeight;
        }

        if (actionBar)
        {
            DrawActionBar(dl, rollInfo, origin.X + 12f, rowY + 6f, width - 24f);
            rowY += ActionBarHeight;
        }

        // Timer bar along the bottom of open items.
        if (!finished)
        {
            var fraction = (float)Math.Clamp(rollInfo.SecondsLeft / Math.Max(rollInfo.TimerSeconds, 1f), 0, 1);
            var barMin = new Vector2(origin.X + 12f, rowY + 3f);
            var barW = width - 24f;
            dl.AddRectFilled(barMin, barMin + new Vector2(barW, TimerBarHeight), Theme.U32(Theme.Line, 0.8f), 2f);
            dl.AddRectFilled(barMin, barMin + new Vector2(barW * fraction, TimerBarHeight),
                Theme.U32(TimerColor(rollInfo.SecondsLeft), 0.9f), 2f);
        }

        if (won)
        {
            var iconCenter = iconPos - origin + new Vector2(IconSize * 0.5f, IconSize * 0.5f);
            DrawWinnerBurst(dl, rollInfo, origin, max, iconCenter, winnerRowY, rarityColor);
        }

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(width, cardHeight));
        ImGui.Dummy(new Vector2(0, 6));
    }

    /// <summary>
    /// Plays once when a card's winner is announced: a light sweep across the card, a burst of
    /// sparks from the winner's crown and rings from the item icon. Clipped to the card.
    /// </summary>
    private void DrawWinnerBurst(ImDrawListPtr dl, Services.RollInfo rollInfo, Vector2 origin, Vector2 max,
        Vector2 iconCenter, float winnerRowY, Vector4 rarityColor)
    {
        var config = plugin.Configuration;
        if (!config.EnableParticleEffects)
            return;

        if (!bursts.TryGetValue(rollInfo, out var burst))
        {
            // Only celebrate results as they come in, not ones already on screen when the window opened.
            if (rollInfo.FinishedAt is not { } at || (DateTime.Now - at).TotalSeconds > 1.5)
                return;

            burst = new WinnerBurst();
            bursts[rollInfo] = burst;
            SpawnWinnerParticles(burst, iconCenter, winnerRowY, rarityColor, config.ParticleIntensity);
        }

        var now = DateTime.Now;
        var age = (float)(now - burst.Start).TotalSeconds;
        var dt = Math.Min((float)(now - burst.LastUpdate).TotalSeconds, 0.1f);
        burst.LastUpdate = now;

        for (var i = burst.Particles.Count - 1; i >= 0; i--)
        {
            burst.Particles[i].Update(dt);
            if (!burst.Particles[i].IsAlive)
                burst.Particles.RemoveAt(i);
        }

        if (age > BurstSeconds && burst.Particles.Count == 0)
            return;

        dl.PushClipRect(origin, max, true);
        try
        {
            // A band of light sweeping left to right across the card.
            if (age < SweepSeconds)
            {
                var t = age / SweepSeconds;
                var eased = 1f - (1f - t) * (1f - t);
                var bandW = 90f;
                var x = origin.X - bandW + (max.X - origin.X + bandW * 2) * eased;
                var alpha = 0.28f * (1f - t * 0.6f);
                var glow = Theme.Mix(Theme.GoldBright, rarityColor, 0.35f);

                dl.AddRectFilledMultiColor(new Vector2(x - bandW, origin.Y), new Vector2(x, max.Y),
                    Theme.U32(glow, 0f), Theme.U32(glow, alpha), Theme.U32(glow, alpha), Theme.U32(glow, 0f));
                dl.AddRectFilledMultiColor(new Vector2(x, origin.Y), new Vector2(x + bandW, max.Y),
                    Theme.U32(glow, alpha), Theme.U32(glow, 0f), Theme.U32(glow, 0f), Theme.U32(glow, alpha));
            }

            // The winner's row flares, then settles back to its usual band.
            if (winnerRowY >= 0 && age < 1.6f)
            {
                var flare = 0.35f * (1f - age / 1.6f);
                dl.AddRectFilled(
                    new Vector2(origin.X + 10f, origin.Y + winnerRowY + 1f),
                    new Vector2(max.X - 10f, origin.Y + winnerRowY + RowHeight - 1f),
                    Theme.U32(Theme.GoldBright, flare), 4f);
            }

            foreach (var particle in burst.Particles)
                DrawParticle(dl, particle, origin);
        }
        finally
        {
            dl.PopClipRect();
        }
    }

    private void SpawnWinnerParticles(WinnerBurst burst, Vector2 iconCenter, float winnerRowY, Vector4 rarityColor, float intensity)
    {
        // Sparks rise from the crown on the winner's row; fall back to the icon if the row isn't shown.
        var source = winnerRowY >= 0 ? new Vector2(24f, winnerRowY + RowHeight * 0.5f) : iconCenter;
        var count = (int)(26 * Math.Clamp(intensity, 0.2f, 2f));

        for (var i = 0; i < count; i++)
        {
            // Mostly upward, fanned out towards the right where the card has room.
            var angle = -MathF.PI * (0.08f + 0.84f * (float)random.NextDouble());
            var speed = 70f + 120f * (float)random.NextDouble();
            var life = 0.9f + 0.8f * (float)random.NextDouble();
            var gold = random.NextDouble() < 0.6;

            burst.Particles.Add(new ParticleEffect
            {
                Position = source + new Vector2((float)random.NextDouble() * 10f - 5f, (float)random.NextDouble() * 6f - 3f),
                Velocity = new Vector2(MathF.Cos(angle) * speed * 1.6f, MathF.Sin(angle) * speed),
                Color = gold ? Theme.GoldBright : Theme.Lighten(rarityColor, 0.25f),
                Size = gold ? 2.2f + 1.6f * (float)random.NextDouble() : 1.6f + 1.2f * (float)random.NextDouble(),
                Life = life,
                MaxLife = life,
                Type = random.NextDouble() < 0.35 ? ParticleType.Star : ParticleType.Spark,
                Rotation = (float)(random.NextDouble() * Math.PI * 2),
                RotationSpeed = ((float)random.NextDouble() - 0.5f) * 6f,
            });
        }

        for (var i = 0; i < 2; i++)
        {
            var life = 1.0f + i * 0.35f;
            burst.Particles.Add(new ParticleEffect
            {
                Position = iconCenter,
                Velocity = Vector2.Zero,
                Color = i == 0 ? Theme.GoldBright : rarityColor,
                Size = 10f + i * 8f,
                Life = life,
                MaxLife = life,
                Type = ParticleType.Ring,
            });
        }
    }

    private static void DrawParticle(ImDrawListPtr dl, ParticleEffect particle, Vector2 origin)
    {
        var pos = origin + particle.Position;
        var color = ImGui.GetColorU32(particle.Color);

        switch (particle.Type)
        {
            case ParticleType.Star:
                for (var i = 0; i < 2; i++)
                {
                    var a = particle.Rotation + i * MathF.PI / 2;
                    var offset = new Vector2(MathF.Cos(a), MathF.Sin(a)) * particle.Size * 1.6f;
                    dl.AddLine(pos - offset, pos + offset, color, 1.6f);
                }
                break;

            case ParticleType.Ring:
                var radius = particle.Size * (1f - particle.Life / particle.MaxLife) * 3f;
                dl.AddCircle(pos, radius, color, 32, 2f);
                break;

            default:
                dl.AddCircleFilled(pos, particle.Size, color, 8);
                dl.AddCircleFilled(pos, particle.Size * 2f,
                    ImGui.GetColorU32(new Vector4(particle.Color.X, particle.Color.Y, particle.Color.Z, particle.Color.W * 0.25f)), 12);
                break;
        }
    }

    private UnlockStatus UnlockStatusOf(Services.RollInfo rollInfo)
        => plugin.Configuration.ShowUnlockStatus ? ItemUnlocks.Get(rollInfo.ItemId) : UnlockStatus.NotCollectible;

    /// <summary>Item tooltip on hover and the item menu on right-click, for the item just drawn.</summary>
    private void DrawItemInteractions(Services.RollInfo rollInfo, bool drawMenu)
    {
        if (ImGui.IsItemHovered() && plugin.Configuration.ShowTooltips)
        {
            ItemTooltip.Show(rollInfo.ItemId, rollInfo.IsHq,
                () => ItemTooltip.MarketRow(plugin, rollInfo.ItemId, rollInfo.IsHq),
                "Right-click for Try On and more", UnlockStatusOf(rollInfo));
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            ImGui.OpenPopup("##rollctx");

        if (!drawMenu)
            return;

        using var popup = ImRaii.Popup("##rollctx");
        if (!popup) return;

        ImGui.TextColored(Theme.RarityColor(rollInfo.Rarity), rollInfo.ItemName);
        ImGui.Separator();
        ItemActions.DrawMenuItems(rollInfo.ItemId, rollInfo.ItemName, plugin.MarketPriceService.IsMarketable(rollInfo.ItemId));
    }

    /// <summary>
    /// Your Need, Greed and Pass buttons, a full-width bar set apart from the results above it.
    /// Choices the game won't accept from you are greyed out, and the bar gives way to a note
    /// while a click is in flight.
    /// </summary>
    private void DrawActionBar(ImDrawListPtr dl, Services.RollInfo rollInfo, float left, float y, float width)
    {
        var tracker = plugin.LootTracker;

        dl.AddLine(new Vector2(left, y), new Vector2(left + width, y), Theme.U32(Theme.Line, 0.7f), 1f);
        y += 7f;

        if (Services.LootTrackingService.IsRollPending(rollInfo))
        {
            var note = $"Rolling {rollInfo.PendingChoice}...";
            var ns = ImGui.CalcTextSize(note);
            var center = left + width * 0.5f;
            dl.AddRectFilled(new Vector2(left, y), new Vector2(left + width, y + ActionButtonHeight), Theme.U32(Theme.Gold, 0.08f), Theme.Radius);
            Theme.DrawSpinner(dl, new Vector2(center - ns.X * 0.5f - 12f, y + ActionButtonHeight * 0.5f), 5f, 1.6f, Theme.Gold);
            dl.AddText(new Vector2(center - ns.X * 0.5f + 2f, y + (ActionButtonHeight - ns.Y) * 0.5f), Theme.U32(Theme.GoldBright), note);
            return;
        }

        var options = tracker.GetRollOptions(rollInfo);

        (Services.RollChoice Choice, FontAwesomeIcon Icon, bool Allowed, Vector4 Color, string? Blocked)[] buttons =
        [
            (Services.RollChoice.Need, FontAwesomeIcon.Dice, options.CanNeed, Theme.Good, options.NeedBlockedReason),
            (Services.RollChoice.Greed, FontAwesomeIcon.Coins, options.CanGreed, Theme.Crystal, "You can only pass on this item."),
            (Services.RollChoice.Pass, FontAwesomeIcon.Times, options.CanPass, Theme.Bad, null),
        ];

        const float gap = 6f;
        var buttonWidth = (width - gap * (buttons.Length - 1)) / buttons.Length;

        for (var i = 0; i < buttons.Length; i++)
        {
            var (choice, icon, allowed, color, blocked) = buttons[i];
            var label = choice.ToString();
            var min = new Vector2(left + i * (buttonWidth + gap), y);
            var max = min + new Vector2(buttonWidth, ActionButtonHeight);

            ImGui.SetCursorScreenPos(min);
            var clicked = ImGui.InvisibleButton($"##roll_{label}", max - min);
            var hovered = ImGui.IsItemHovered();
            var held = ImGui.IsItemActive();

            Vector4 textColor;
            if (allowed)
            {
                // Solid, bevelled buttons: a real control, not another tag like the result pills.
                var fill = held ? 0.55f : hovered ? 0.42f : 0.26f;
                dl.AddRectFilled(min, max, Theme.U32(Theme.Mix(Theme.Panel, color, fill)), Theme.Radius);
                dl.AddRectFilledMultiColor(min, new Vector2(max.X, min.Y + ActionButtonHeight * 0.5f),
                    Theme.U32(Theme.Lighten(color, 0.4f), 0.14f), Theme.U32(Theme.Lighten(color, 0.4f), 0.14f),
                    Theme.U32(color, 0f), Theme.U32(color, 0f));
                dl.AddRect(min, max, Theme.U32(color, hovered ? 1f : 0.7f), Theme.Radius, ImDrawFlags.None, hovered ? 1.6f : 1.2f);
                textColor = hovered ? Theme.Lighten(color, 0.55f) : Theme.Lighten(color, 0.3f);

                if (hovered)
                {
                    var text = choice == Services.RollChoice.Pass ? $"Pass on {rollInfo.ItemName}" : $"Roll {label} on {rollInfo.ItemName}";
                    if (choice == Services.RollChoice.Need && UnlockStatusOf(rollInfo) == UnlockStatus.Unlocked)
                        text += "\nYou already have this unlocked.";
                    Theme.Tooltip(text);
                }

                if (clicked)
                    tracker.Roll(rollInfo, choice);
            }
            else
            {
                dl.AddRectFilled(min, max, Theme.U32(Theme.Surface, 0.5f), Theme.Radius);
                dl.AddRect(min, max, Theme.U32(Theme.Line, 0.6f), Theme.Radius, ImDrawFlags.None, 1f);
                textColor = Theme.Alpha(Theme.TextFaint, 0.7f);

                if (hovered && blocked != null)
                    Theme.Tooltip(blocked);
            }

            // Icon and label, centred together.
            string glyph;
            float glyphWidth;
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                glyph = icon.ToIconString();
                glyphWidth = ImGui.CalcTextSize(glyph).X;
            }

            var ls = ImGui.CalcTextSize(label);
            var contentX = min.X + (buttonWidth - glyphWidth - 6f - ls.X) * 0.5f;
            var textY = min.Y + (ActionButtonHeight - ls.Y) * 0.5f;

            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                dl.AddText(new Vector2(contentX, textY + 1f), Theme.U32(textColor), glyph);
            }
            dl.AddText(new Vector2(contentX + glyphWidth + 6f, textY), Theme.U32(textColor), label);
        }
    }

    private static Vector4 TimerColor(double secondsLeft) => secondsLeft switch
    {
        <= 10 => Theme.Bad,
        <= 30 => Theme.Warn,
        _ => Theme.Crystal,
    };

    private static void DrawRollRow(ImDrawListPtr dl, Vector2 origin, float width, string playerName,
        string rollType, int rollValue, bool isWinner, string localPlayerName)
    {
        var textY = origin.Y + (21f - ImGui.GetTextLineHeight()) * 0.5f;
        var x = origin.X + 18f;

        if (isWinner)
        {
            // Winners get a soft brass band behind the whole row.
            dl.AddRectFilled(
                new Vector2(origin.X + 10f, origin.Y + 1f),
                new Vector2(origin.X + width - 10f, origin.Y + 20f),
                Theme.U32(Theme.Gold, 0.13f), 4f);

            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                var glyph = FontAwesomeIcon.Crown.ToIconString();
                dl.AddText(new Vector2(x, textY), Theme.U32(Theme.GoldBright), glyph);
                x += ImGui.CalcTextSize(glyph).X + 7f;
            }
        }
        else
        {
            x += 4f;
        }

        var isLocalPlayer = playerName.Equals(localPlayerName, StringComparison.OrdinalIgnoreCase) ||
                            playerName.StartsWith(localPlayerName, StringComparison.OrdinalIgnoreCase);
        var playerColor = isLocalPlayer ? Theme.Good : Services.RollKind.IsRoll(rollType) ? Theme.Text : Theme.TextMuted;
        dl.AddText(new Vector2(x, textY), Theme.U32(playerColor), playerName);

        // Roll type pill and value, right aligned. Passes and non-rolls have no value.
        var isRoll = Services.RollKind.IsRoll(rollType);
        var valueText = !isRoll ? "-" : rollValue == Services.RollKind.PendingValue ? "..." : rollValue.ToString();
        var vw = ImGui.CalcTextSize(valueText).X;
        var valueX = origin.X + width - 18f - vw;
        dl.AddText(new Vector2(valueX, textY), Theme.U32(isWinner ? Theme.GoldBright : isRoll ? Theme.Text : Theme.TextFaint), valueText);

        var rollColor = rollType switch
        {
            Services.RollKind.Need => Theme.Good,
            Services.RollKind.Greed => Theme.Crystal,
            Services.RollKind.CantRoll => Theme.Warn,
            Services.RollKind.Decided => Theme.Gold,
            _ => Theme.TextMuted,
        };
        var ts = ImGui.CalcTextSize(rollType);
        var pillMax = new Vector2(valueX - 8f, origin.Y + 3.5f);
        var pillMin = new Vector2(pillMax.X - ts.X - 12f, pillMax.Y);
        var pillBottom = new Vector2(pillMax.X, origin.Y + 17.5f);

        dl.AddRectFilled(pillMin, pillBottom, Theme.U32(rollColor, 0.16f), 7f);
        dl.AddText(new Vector2(pillMin.X + 6f, textY), Theme.U32(rollColor), rollType);
    }

    public override void Dispose()
    {
        plugin.LootTracker.RollsUpdated -= OnRollsUpdated;
        base.Dispose();
    }
}
