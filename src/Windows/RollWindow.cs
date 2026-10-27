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
    // Measurements are given at 100% and follow Dalamud's global UI scale, like the text they hold.
    // Rows that carry text never get shorter than the text itself, whatever font size is set.
    private static float IconSize => Theme.Px(34f);
    private static float RowHeight => Math.Max(Theme.Px(21f), ImGui.GetTextLineHeight() + Theme.Px(4f));
    private static float TimerBarHeight => Theme.Px(3f);
    private static float ActionButtonHeight => Math.Max(Theme.Px(28f), ImGui.GetTextLineHeight() + Theme.Px(10f));

    /// <summary>The action bar's buttons plus the rule and breathing room above them.</summary>
    private static float ActionBarHeight => ActionButtonHeight + Theme.Px(13f);

    /// <summary>
    /// The card's top block: the icon beside the item name and the status badge under it, whichever
    /// is taller. Derived from the text height so the badge can never run into the buttons below.
    /// </summary>
    private static float CardHeaderHeight
    {
        get
        {
            var line = ImGui.GetTextLineHeight();
            var textBlock = NameTop + line + BadgeGap + line + Theme.Px(5f);
            return Math.Max(Theme.Px(9f) + IconSize, textBlock) + Theme.Px(8f);
        }
    }

    /// <summary>Top of the item name, from the top of the card.</summary>
    private static float NameTop => Theme.Px(10f);

    /// <summary>Space between the item name and the status badge under it.</summary>
    private static float BadgeGap => Theme.Px(3f);

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

    /// <summary>
    /// The race a resolved item's rolls are revealed as. Every contender's number counts up from
    /// zero together, each stopping at its roll, so the lowest settle first and the winner is the
    /// last one still climbing. The winner then takes the crown, and the rows slide into rank order.
    /// </summary>
    private sealed class RollReveal
    {
        public DateTime Start = DateTime.Now;

        /// <summary>Rows in the order players chose, so the top row doesn't give the winner away.</summary>
        public List<string> Order = [];

        /// <summary>Who can still win: the Need rolls if there are any, otherwise the Greed ones.</summary>
        public HashSet<string> Contenders = [];

        public int Top;
        public float RaceSeconds;

        public float Elapsed => (float)(DateTime.Now - Start).TotalSeconds;
        public DateTime RaceEnd => Start.AddSeconds(RaceSeconds);
        public bool IsOver => Elapsed >= RaceSeconds + RevealHold + RevealSlide + BarFade;

        /// <summary>When the counter reaches <paramref name="value"/>, inverting its ease-out.</summary>
        public float LockTime(int value)
            => value >= Top ? RaceSeconds : (1f - MathF.Cbrt(1f - value / (float)Top)) * RaceSeconds;
    }

    /// <summary>One row's state at this moment of a reveal.</summary>
    private readonly record struct RowReveal(int Shown, bool Rolling, float LockAge, bool Outranked, float Bar, float BarAlpha, float Settle, float CrownAge);

    /// <summary>The crown is held on the winner where it finished before the rows re-sort.</summary>
    private const float RevealHold = 0.55f;
    private const float RevealSlide = 0.4f;
    private const float BarFade = 0.6f;

    private readonly Dictionary<Services.RollInfo, RollReveal> reveals = [];

    /// <summary>Results already announced with the chime, or that never will be.</summary>
    private readonly HashSet<Services.RollInfo> announced = [];

    private readonly Services.RollChime chime = new();

    /// <summary>How long after the mouse leaves the window the layout stays put.</summary>
    private const float FreezeSeconds = 0.6f;

    private DateTime lastHovered = DateTime.MinValue;
    private List<Services.RollInfo> displayOrder = [];

    /// <summary>Items drawn as a full card last frame, which stay full while the layout is frozen.</summary>
    private HashSet<Services.RollInfo> fullCards = [];

    /// <summary>
    /// What still needs your choice comes first, the one closing soonest on top; then the results,
    /// newest first; then the items you've already chosen on, waiting for everyone else.
    /// </summary>
    private List<Services.RollInfo> DesiredOrder(IEnumerable<Services.RollInfo> rolls) => rolls
        .OrderBy(r => AwaitsYou(r) ? 0 : r.IsFinished ? 1 : 2)
        .ThenBy(r => r.IsFinished ? -(r.FinishedAt?.Ticks ?? 0) : r.Deadline.Ticks)
        .ToList();

    private bool AwaitsYou(Services.RollInfo roll)
        => !roll.IsFinished && !Services.LootTrackingService.IsRollPending(roll) && OptionsFor(roll, previewRolls.Contains(roll)).Any;

    /// <summary>The order on screen, kept as it was; items new since then join at the bottom.</summary>
    private List<Services.RollInfo> FrozenOrder(IReadOnlyList<Services.RollInfo> rolls)
    {
        var kept = displayOrder.Where(rolls.Contains).ToList();
        kept.AddRange(DesiredOrder(rolls.Where(r => !kept.Contains(r))));
        return kept;
    }

    /// <summary>
    /// Made-up items from the preview button in the settings, so the window can be checked without a
    /// duty. They live only here: the tracker never sees them and nothing is ever sent to the game.
    /// </summary>
    private readonly List<Services.RollInfo> previewRolls = [];

    private static readonly string[] PreviewParty = ["Y'shtola Rhul", "Thancred Waters", "Krile Mayer Baldesion"];

    /// <summary>How long a click on a preview button shows as in flight, like a real roll waiting on the game.</summary>
    private const double PreviewPendingSeconds = 0.8;

    private static string LocalPlayerName => Plugin.ObjectTable.LocalPlayer?.Name.TextValue ?? "You";

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
            Plugin.Log.Debug($"Roll window opened - {activeRolls.Count} active roll(s)");
        }
    }

    /// <summary>With our window gone, give the game's own back so the player can still roll.</summary>
    protected override void OnClosed()
    {
        previewRolls.Clear();
        plugin.NativeLootWindow.Refresh();
    }

    /// <summary>
    /// Opens the window on three sample items: one open and weekly-limited with your buttons, one open
    /// and about to time out, and one just won. Real rolls in progress stay where they are.
    /// </summary>
    public void ShowPreview()
    {
        previewRolls.Clear();

        var items = PickPreviewItems(3);
        var me = LocalPlayerName;
        var now = DateTime.Now;

        var open = NewPreviewRoll(items[0], now, 90);
        open.IsWeekly = true;
        open.PlayerRolls[PreviewParty[0]] = (Services.RollKind.Decided, 0);
        open.PlayerRolls[PreviewParty[1]] = (Services.RollKind.Decided, 0);

        var closing = NewPreviewRoll(items[1], now, 25);
        closing.PlayerRolls[PreviewParty[1]] = (Services.RollKind.Pass, 0);

        var won = NewPreviewRoll(items[2], now, 0);
        won.PlayerRolls[PreviewParty[2]] = (Services.RollKind.Need, 88);
        won.PlayerRolls[PreviewParty[0]] = (Services.RollKind.Greed, 42);
        won.PlayerRolls[me] = (Services.RollKind.Greed, 15);
        won.PlayerRolls[PreviewParty[1]] = (Services.RollKind.Pass, 0);
        won.WinnerName = PreviewParty[2];
        won.FinishedAt = now;
        won.CloseReason = Services.RollCloseReason.Finished;

        previewRolls.AddRange([open, closing, won]);
        IsOpen = true;
        Plugin.Log.Debug("Roll window preview opened");
    }

    private static Services.RollInfo NewPreviewRoll((uint Id, string Name, uint Icon, uint Rarity) item, DateTime now, double seconds) => new()
    {
        ItemId = item.Id,
        ItemName = item.Name,
        IconId = item.Icon,
        Rarity = item.Rarity,
        RollStartTime = now,
        Deadline = now.AddSeconds(seconds),
        TimerSeconds = Services.RollInfo.DefaultRollSeconds,
    };

    /// <summary>Real gear from the game data, so the preview has icons, colours and tooltips.</summary>
    private List<(uint Id, string Name, uint Icon, uint Rarity)> PickPreviewItems(int count)
    {
        var picks = new List<(uint Id, string Name, uint Icon, uint Rarity)>();
        try
        {
            var candidates = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>()
                .Where(i => i.EquipSlotCategory.RowId != 0 && i.Icon != 0 && i.Rarity >= 2 && i.Rarity <= 4)
                .OrderByDescending(i => i.LevelItem.RowId)
                .Take(150)
                .ToList();

            while (picks.Count < count && candidates.Count > 0)
            {
                var item = candidates[random.Next(candidates.Count)];
                candidates.Remove(item);
                picks.Add((item.RowId, item.Name.ExtractText(), item.Icon, item.Rarity));
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not pick items for the roll window preview");
        }

        while (picks.Count < count)
            picks.Add((0, $"Sample Item {picks.Count + 1}", 0, 3));

        return picks;
    }

    /// <summary>Everyone in the made-up party, you included.</summary>
    private static IEnumerable<string> PreviewMembers() => PreviewParty.Prepend(LocalPlayerName);

    /// <summary>
    /// Ends a preview item the way the game would: hidden choices are revealed, whoever never chose
    /// is marked as such, and the best Need (or failing that, Greed) wins.
    /// </summary>
    private void ResolvePreview(Services.RollInfo roll, Services.RollChoice? mine)
    {
        var me = LocalPlayerName;
        if (mine is { } choice)
        {
            roll.PlayerRolls[me] = choice switch
            {
                Services.RollChoice.Need => (Services.RollKind.Need, random.Next(1, 100)),
                Services.RollChoice.Greed => (Services.RollKind.Greed, random.Next(1, 100)),
                _ => (Services.RollKind.Pass, 0),
            };
        }

        foreach (var name in PreviewMembers())
        {
            if (roll.PlayerRolls.TryGetValue(name, out var existing) && existing.RollType != Services.RollKind.Decided)
                continue;

            roll.PlayerRolls[name] = name == me
                ? (Services.RollKind.NoRoll, 0)
                : random.Next(3) switch
                {
                    0 => (Services.RollKind.Need, random.Next(1, 100)),
                    1 => (Services.RollKind.Greed, random.Next(1, 100)),
                    _ => (Services.RollKind.Pass, 0),
                };
        }

        roll.WinnerName = roll.PlayerRolls
            .Where(r => Services.RollKind.IsRoll(r.Value.RollType))
            .OrderBy(r => Services.RollKind.Order(r.Value.RollType))
            .ThenByDescending(r => r.Value.RollValue)
            .Select(r => r.Key)
            .FirstOrDefault() ?? string.Empty;

        roll.PendingChoice = null;
        roll.FinishedAt = DateTime.Now;
        roll.CloseReason = Services.RollCloseReason.Finished;
    }

    /// <summary>Plays the preview forward: clicks land, timers run out, results leave on schedule.</summary>
    private void UpdatePreview()
    {
        foreach (var roll in previewRolls)
        {
            if (roll.IsFinished)
                continue;

            if (roll.PendingChoice is { } choice && (DateTime.Now - roll.PendingSince).TotalSeconds >= PreviewPendingSeconds)
                ResolvePreview(roll, choice);
            else if (roll.SecondsLeft <= 0)
                ResolvePreview(roll, null);
        }

        previewRolls.RemoveAll(r => SecondsUntilHidden(r) is <= 0d);
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
            if (previewRolls.Count > 0)
                UpdatePreview();

            var activeRolls = previewRolls.Count == 0
                ? plugin.LootTracker.ActiveRolls
                : plugin.LootTracker.ActiveRolls.Concat(previewRolls).ToList();

            if (bursts.Count > 0)
            {
                foreach (var gone in bursts.Keys.Where(r => !activeRolls.Contains(r)).ToList())
                    bursts.Remove(gone);
            }

            if (reveals.Count > 0)
            {
                foreach (var gone in reveals.Keys.Where(r => !activeRolls.Contains(r)).ToList())
                    reveals.Remove(gone);
            }

            announced.RemoveWhere(r => !activeRolls.Contains(r));

            if (activeRolls.Count == 0)
            {
                IsOpen = false;
                return;
            }

            DrawHeader(activeRolls);

            var localPlayerName = Plugin.ObjectTable.LocalPlayer?.Name.TextValue ?? "You";

            // Items waiting on you, then results, then items you've chosen on. While the mouse is over
            // the window nothing moves: the layout waits until you look away, so a result coming in
            // or your own click can't pull a button out from under the cursor.
            var frozen = (DateTime.Now - lastHovered).TotalSeconds < FreezeSeconds;
            var ordered = frozen ? FrozenOrder(activeRolls) : DesiredOrder(activeRolls);
            displayOrder = ordered;

            var drawnFull = new HashSet<Services.RollInfo>();
            foreach (var roll in ordered)
            {
                // A result stays a full card while it's revealed (or briefly, without the reveal),
                // then folds into a single line.
                var compact = roll.IsFinished && !(frozen && fullCards.Contains(roll));
                if (compact)
                {
                    var reveal = RevealFor(roll, SortedRows(roll, localPlayerName));
                    compact = reveal != null
                        ? reveal.IsOver
                        : roll.FinishedAt is not { } at || (DateTime.Now - at).TotalSeconds > 2.5;
                }

                if (compact)
                {
                    DrawResultRow(roll, localPlayerName);
                }
                else
                {
                    DrawRollCard(roll, localPlayerName);
                    drawnFull.Add(roll);
                }
            }
            fullCards = drawnFull;

            if (ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByPopup))
                lastHovered = DateTime.Now;
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
        var h = Math.Max(Theme.Px(28f), ImGui.GetTextLineHeight() + Theme.Px(8f));
        var width = Math.Max(ImGui.GetContentRegionAvail().X, Theme.Px(340f));
        var ring = Theme.Px(11f);

        // Once every item is resolved, the ring counts down to the last one disappearing.
        var center = new Vector2(origin.X + Theme.Px(12f), origin.Y + h * 0.5f);
        double? closingIn = activeRolls.All(r => r.IsFinished)
            ? activeRolls.Max(r => SecondsUntilHidden(r) ?? 0)
            : null;

        if (closingIn is { } closing)
        {
            var fraction = (float)(closing / ResultSeconds);

            dl.AddCircle(center, ring, Theme.U32(Theme.Line), 32, 2f);

            dl.PathClear();
            const int segs = 32;
            for (var i = 0; i <= segs; i++)
            {
                var a = -MathF.PI * 0.5f + MathF.PI * 2f * fraction * i / segs;
                dl.PathLineTo(new Vector2(center.X + MathF.Cos(a) * ring, center.Y + MathF.Sin(a) * ring));
            }
            dl.PathStroke(Theme.U32(fraction < 0.3f ? Theme.Bad : Theme.Gold, 0.95f), ImDrawFlags.None, 2f);
        }
        else
        {
            var pulse = 0.55f + 0.45f * MathF.Sin(Theme.Time * 3.2f);
            dl.AddCircleFilled(center, ring, Theme.U32(Theme.Gold, 0.10f + pulse * 0.10f), 32);
            dl.AddCircle(center, ring, Theme.U32(Theme.Gold, 0.5f + pulse * 0.4f), 32, 1.4f);
        }

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var glyph = FontAwesomeIcon.Dice.ToIconString();
            var gs = ImGui.CalcTextSize(glyph);
            dl.AddText(center - gs * 0.5f, Theme.U32(Theme.GoldBright), glyph);
        }

        using (new Theme.FontScale(1.08f))
        {
            ImGui.SetCursorScreenPos(new Vector2(origin.X + Theme.Px(30f), origin.Y + (h - ImGui.GetTextLineHeight()) * 0.5f));
            ImGui.TextColored(Theme.GoldBright, "Loot Rolls");
        }

        if (previewRolls.Count > 0)
        {
            ImGui.SameLine(0, Theme.Px(10));
            Theme.Badge("Preview", Theme.Crystal);
            if (ImGui.IsItemHovered())
                Theme.Tooltip("Sample items to check how the window looks. Nothing here is sent to the game.");
        }

        if (closingIn is { } remaining)
        {
            ImGui.SameLine(0, Theme.Px(10));
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.TextFaint, $"closing in {Math.Ceiling(remaining):F0}s");
        }

        // While the game's own loot window is kept hidden, offer a way back to it.
        const float headerButton = 24f; // IconButton scales it itself
        var button = Theme.Px(headerButton);
        if (plugin.NativeLootWindow.IsHidden)
        {
            ImGui.SetCursorScreenPos(new Vector2(origin.X + width - button * 2 - Theme.Px(4f), origin.Y + (h - button) * 0.5f));
            if (Theme.IconButton("##ShowNative", FontAwesomeIcon.WindowRestore, "Show the game's Need/Greed window", Theme.Crystal, false, headerButton))
                plugin.NativeLootWindow.Reveal();
        }

        // Close button, right aligned.
        ImGui.SetCursorScreenPos(new Vector2(origin.X + width - button, origin.Y + (h - button) * 0.5f));
        if (Theme.IconButton("##CloseRolls", FontAwesomeIcon.Times, "Close and clear all rolls", Theme.Bad, false, headerButton))
        {
            IsOpen = false;
            previewRolls.Clear();
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
        var width = Math.Max(ImGui.GetContentRegionAvail().X, Theme.Px(340f));
        var pad = Theme.Px(12f);

        var allRolls = rollInfo.GetSortedRolls().ToList();
        var rarityColor = Theme.RarityColor(rollInfo.Rarity);
        var won = !string.IsNullOrEmpty(rollInfo.WinnerName);
        var finished = rollInfo.IsFinished;
        var preview = previewRolls.Contains(rollInfo);

        var allianceSize = preview ? null : plugin.LootTracker.AllianceSize();
        var sortedRolls = SortedRows(rollInfo, localPlayerName);
        var others = allianceSize != null && finished ? allRolls.Except(sortedRolls).ToList() : [];

        // Party members who haven't chosen yet, while the item is still open.
        var waiting = finished || allianceSize != null
            ? []
            : (preview ? PreviewMembers().ToList() : plugin.LootTracker.PartyMemberNames())
                .Where(n => !rollInfo.PlayerRolls.Keys.Any(k => Services.LootTrackingService.SameName(k, n)))
                .ToList();

        var reveal = RevealFor(rollInfo, sortedRolls);
        var racing = reveal != null && reveal.Elapsed < reveal.RaceSeconds;
        Announce(rollInfo, reveal, localPlayerName);

        var placeholderRow = sortedRolls.Count == 0 && waiting.Count == 0 && others.Count == 0 ? 1 : 0;
        var rowsHeight = (sortedRolls.Count + placeholderRow + (waiting.Count > 0 ? 1 : 0) + (others.Count > 0 ? 1 : 0)) * RowHeight;
        var actionBar = !finished && plugin.Configuration.ShowRollButtons &&
                        (Services.LootTrackingService.IsRollPending(rollInfo) || OptionsFor(rollInfo, preview).Any);

        // In weekly-limited duties only one item can be yours, so rolling here puts your other rolls at stake.
        var weekly = rollInfo.IsWeekly && !finished;
        var atStake = weekly && actionBar && !preview ? plugin.LootTracker.OtherWeeklyRolls(rollInfo) : [];
        var stakeRow = atStake.Count > 0 ? RowHeight : 0;

        var headerHeight = CardHeaderHeight;
        var cardHeight = headerHeight + rowsHeight + stakeRow + (actionBar ? ActionBarHeight : 0) + (finished ? 0 : TimerBarHeight + Theme.Px(6f)) + Theme.Px(5f);

        var max = new Vector2(origin.X + width, origin.Y + cardHeight);

        // Card shell, tinted by the item's rarity.
        dl.AddRectFilled(origin, max, Theme.U32(Theme.Surface, 0.85f), Theme.Radius);
        dl.AddRectFilledMultiColor(origin, max,
            Theme.U32(rarityColor, 0.12f), Theme.U32(rarityColor, 0.03f),
            Theme.U32(rarityColor, 0f), Theme.U32(rarityColor, 0.05f));
        dl.AddRect(origin, max, Theme.U32(rarityColor, finished ? 0.5f : 0.3f), Theme.Radius, ImDrawFlags.None, 1f);
        dl.AddRectFilled(new Vector2(origin.X, origin.Y + Theme.Px(6)), new Vector2(origin.X + Theme.Px(2.5f), max.Y - Theme.Px(6)),
            Theme.U32(rarityColor, 0.9f), 1.5f);

        // Item icon.
        var iconPos = new Vector2(origin.X + pad, origin.Y + Theme.Px(9f));
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
                        var glow = new Vector2(Theme.Px(3), Theme.Px(3));
                        dl.AddRectFilled(iconPos - glow,
                            iconPos + new Vector2(IconSize, IconSize) + glow,
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
        var textX = origin.X + pad + IconSize + Theme.Px(11f);
        var nameY = origin.Y + NameTop;
        ImGui.SetCursorScreenPos(new Vector2(textX, nameY));
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
            dl.AddText(new Vector2(origin.X + width - Theme.Px(14f) - hw, nameY), Theme.U32(Theme.TextFaint), hideText);
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
                dl.AddText(new Vector2(origin.X + width - Theme.Px(14f) - tw - Theme.Px(6f) - gw, nameY + 1f), Theme.U32(timerColor, 0.9f), glyph);
            }
            dl.AddText(new Vector2(origin.X + width - Theme.Px(14f) - tw, nameY), Theme.U32(timerColor), timerText);
        }

        ImGui.SetCursorScreenPos(new Vector2(textX, nameY + ImGui.GetTextLineHeight() + BadgeGap));
        if (racing)
        {
            Theme.Badge("Rolling...", Theme.Crystal);
        }
        else if (won)
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
            Theme.Badge(allianceSize is { } size
                ? $"{Math.Min(rollInfo.PlayerRolls.Count, size)}/{size} decided"
                : $"{rollInfo.PlayerRolls.Count} decided", Theme.Crystal);
        }

        if (weekly)
        {
            ImGui.SameLine(0, Theme.Px(6));
            Theme.Badge("Weekly limit", Theme.Warn);
            if (ImGui.IsItemHovered())
                Theme.Tooltip(WeeklyLimitNote);
        }

        // Need / Greed / Pass sit right under the name, above the rolls: rows keep arriving
        // while you decide, and in a 24-player alliance they used to push the buttons away
        // from the cursor.
        var rowY = origin.Y + headerHeight;
        if (stakeRow > 0)
        {
            DrawStakeRow(dl, new Vector2(origin.X, rowY), width, atStake);
            rowY += stakeRow;
        }

        if (actionBar)
        {
            DrawActionBar(dl, rollInfo, preview, origin.X + pad, rowY, width - pad * 2, atStake);
            rowY += ActionBarHeight;
        }

        // Roll rows.
        if (placeholderRow > 0)
        {
            dl.AddText(new Vector2(origin.X + Theme.Px(18f), rowY + (RowHeight - ImGui.GetTextLineHeight()) * 0.5f),
                Theme.U32(Theme.TextFaint), "Waiting for rolls...");
            rowY += RowHeight;
        }

        var winnerRowY = -1f;
        for (var i = 0; i < sortedRolls.Count; i++)
        {
            var (playerName, rollType, rollValue, isWinner) = sortedRolls[i];
            var rowReveal = reveal is { IsOver: false } ? RowStateFor(reveal, playerName, rollType, rollValue) : (RowReveal?)null;

            // While revealing, rows keep the order players chose in, then slide into rank order.
            var slot = (float)i;
            if (rowReveal is { } rv)
            {
                var revealSlot = reveal!.Order.IndexOf(playerName);
                if (revealSlot >= 0) slot = revealSlot + (i - revealSlot) * rv.Settle;
            }

            var y = rowY + slot * RowHeight;
            var crowned = isWinner && !racing;
            if (crowned) winnerRowY = y - origin.Y;
            DrawRollRow(dl, new Vector2(origin.X, y), width, playerName, rollType, rollValue, crowned, localPlayerName, rowReveal);
        }
        rowY += sortedRolls.Count * RowHeight;

        if (others.Count > 0)
        {
            DrawOthersRow(dl, new Vector2(origin.X, rowY), width, others);
            rowY += RowHeight;
        }

        if (waiting.Count > 0)
        {
            var textY = rowY + (RowHeight - ImGui.GetTextLineHeight()) * 0.5f;
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                dl.AddText(new Vector2(origin.X + Theme.Px(22f), textY + 1f), Theme.U32(Theme.TextFaint),
                    FontAwesomeIcon.HourglassHalf.ToIconString());
            }
            Theme.ClipText(dl, new Vector2(origin.X + Theme.Px(40f), textY), width - Theme.Px(58f),
                $"Waiting on {string.Join(", ", waiting)}", Theme.U32(Theme.TextFaint));
            rowY += RowHeight;
        }

        // Timer bar along the bottom of open items.
        if (!finished)
        {
            var fraction = (float)Math.Clamp(rollInfo.SecondsLeft / Math.Max(rollInfo.TimerSeconds, 1f), 0, 1);
            var barMin = new Vector2(origin.X + pad, rowY + Theme.Px(3f));
            var barW = width - pad * 2;
            dl.AddRectFilled(barMin, barMin + new Vector2(barW, TimerBarHeight), Theme.U32(Theme.Line, 0.8f), 2f);
            dl.AddRectFilled(barMin, barMin + new Vector2(barW * fraction, TimerBarHeight),
                Theme.U32(TimerColor(rollInfo.SecondsLeft), 0.9f), 2f);
        }

        if (won && !racing)
        {
            var iconCenter = iconPos - origin + new Vector2(IconSize * 0.5f, IconSize * 0.5f);
            DrawWinnerBurst(dl, rollInfo, origin, max, iconCenter, winnerRowY, rarityColor, reveal?.RaceEnd ?? rollInfo.FinishedAt);
        }

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(width, cardHeight));
        ImGui.Dummy(new Vector2(0, Theme.Px(6)));
    }

    /// <summary>
    /// The rows a card lists, best roll first. With 24 players a row each would run off the screen,
    /// so an alliance gets a counter instead: only Need/Greed rolls and your own row are listed, and
    /// passes are summed up in one line.
    /// </summary>
    private List<(string PlayerName, string RollType, int RollValue, bool IsWinner)> SortedRows(Services.RollInfo roll, string localPlayerName)
    {
        var all = roll.GetSortedRolls().ToList();
        var allianceSize = previewRolls.Contains(roll) ? null : plugin.LootTracker.AllianceSize();
        return allianceSize == null
            ? all
            : all.Where(r => Services.RollKind.IsRoll(r.RollType) || Services.LootTrackingService.SameName(r.PlayerName, localPlayerName)).ToList();
    }

    /// <summary>
    /// A resolved item folded into one line once its reveal is over: the item, and who took it with
    /// what. Hovering the item shows its details, hovering the rest lists every roll.
    /// </summary>
    private void DrawResultRow(Services.RollInfo rollInfo, string localPlayerName)
    {
        using var id = ImRaii.PushId(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(rollInfo));
        Announce(rollInfo, reveals.GetValueOrDefault(rollInfo), localPlayerName);

        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = Math.Max(ImGui.GetContentRegionAvail().X, Theme.Px(340f));
        var height = Math.Max(Theme.Px(30f), ImGui.GetTextLineHeight() + Theme.Px(12f));
        var max = new Vector2(origin.X + width, origin.Y + height);
        var rarityColor = Theme.RarityColor(rollInfo.Rarity);
        var textY = origin.Y + (height - ImGui.GetTextLineHeight()) * 0.5f;

        var winner = rollInfo.WinnerName;
        var won = !string.IsNullOrEmpty(winner);
        var mine = won && Services.LootTrackingService.SameName(winner, localPlayerName);

        dl.AddRectFilled(origin, max, Theme.U32(Theme.Surface, 0.7f), Theme.Radius);
        if (mine)
        {
            dl.AddRectFilledMultiColor(origin, max,
                Theme.U32(Theme.Gold, 0.14f), Theme.U32(Theme.Gold, 0.02f),
                Theme.U32(Theme.Gold, 0.02f), Theme.U32(Theme.Gold, 0.14f));
        }
        dl.AddRect(origin, max, Theme.U32(mine ? Theme.Gold : rarityColor, mine ? 0.6f : 0.25f), Theme.Radius, ImDrawFlags.None, 1f);
        dl.AddRectFilled(new Vector2(origin.X, origin.Y + Theme.Px(5)), new Vector2(origin.X + Theme.Px(2.5f), max.Y - Theme.Px(5)),
            Theme.U32(rarityColor, 0.9f), 1.5f);

        // Icon, answering to hover and right-click like a full card's.
        var icon = height - Theme.Px(8f);
        var iconPos = new Vector2(origin.X + Theme.Px(10f), origin.Y + (height - icon) * 0.5f);
        if (rollInfo.IconId > 0)
        {
            try
            {
                var tex = Plugin.TextureProvider
                    .GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(rollInfo.IconId))
                    .GetWrapOrDefault();
                if (tex != null)
                {
                    dl.AddImage(tex.Handle, iconPos, iconPos + new Vector2(icon, icon));
                    dl.AddRect(iconPos, iconPos + new Vector2(icon, icon), Theme.U32(rarityColor, 0.6f), 3f, ImDrawFlags.None, 1f);
                    ItemUnlocks.DrawIconSeal(dl, iconPos + new Vector2(icon, icon), UnlockStatusOf(rollInfo), 11f);
                }
            }
            catch { /* Ignore icon errors */ }
        }

        // Right side, from the edge inwards: seconds left on screen, then the result.
        var right = max.X - Theme.Px(12f);
        if (SecondsUntilHidden(rollInfo) is { } hideIn)
        {
            var hideText = $"{Math.Ceiling(hideIn):F0}s";
            var hw = ImGui.CalcTextSize(hideText).X;
            dl.AddText(new Vector2(right - hw, textY), Theme.U32(Theme.TextFaint), hideText);
            right -= hw + Theme.Px(12f);
        }

        if (won && rollInfo.PlayerRolls.TryGetValue(winner, out var roll))
        {
            var value = roll.RollValue > 0 ? roll.RollValue.ToString() : "";
            var vw = ImGui.CalcTextSize(value).X;
            dl.AddText(new Vector2(right - vw, textY), Theme.U32(Theme.GoldBright), value);
            right -= vw + Theme.Px(6f);

            var rollColor = roll.RollType == Services.RollKind.Need ? Theme.Good : Theme.Crystal;
            var ts = ImGui.CalcTextSize(roll.RollType);
            var pillPad = Theme.Px(5f);
            var pillMin = new Vector2(right - ts.X - pillPad * 2, origin.Y + Theme.Px(6f));
            var pillMax = new Vector2(right, max.Y - Theme.Px(6f));
            dl.AddRectFilled(pillMin, pillMax, Theme.U32(rollColor, 0.16f), (pillMax.Y - pillMin.Y) * 0.5f);
            dl.AddText(new Vector2(pillMin.X + pillPad, textY), Theme.U32(rollColor), roll.RollType);
            right = pillMin.X - Theme.Px(8f);

            var name = mine ? "You" : winner;
            var nameBudget = Math.Clamp(width * 0.32f, Theme.Px(60f), Theme.Px(170f));
            var nw = Math.Min(ImGui.CalcTextSize(name).X, nameBudget);
            Theme.ClipText(dl, new Vector2(right - nw, textY), nameBudget, name, Theme.U32(mine ? Theme.Good : Theme.Text));
            right -= nw + Theme.Px(6f);

            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                var glyph = FontAwesomeIcon.Crown.ToIconString();
                var gw = ImGui.CalcTextSize(glyph).X;
                dl.AddText(new Vector2(right - gw, textY), Theme.U32(Theme.GoldBright), glyph);
                right -= gw;
            }
        }
        else
        {
            var note = rollInfo.CloseReason == Services.RollCloseReason.LeftDuty ? "You left before the result"
                : rollInfo.RolledCount > 0 ? "Closed" : "Nobody rolled";
            var nw = ImGui.CalcTextSize(note).X;
            dl.AddText(new Vector2(right - nw, textY), Theme.U32(Theme.TextMuted), note);
            right -= nw;
        }

        var nameX = iconPos.X + icon + Theme.Px(9f);
        Theme.ClipText(dl, new Vector2(nameX, textY), right - Theme.Px(10f) - nameX, rollInfo.ItemName, Theme.U32(rarityColor));

        ImGui.SetCursorScreenPos(iconPos);
        ImGui.InvisibleButton("##icon", new Vector2(icon, icon));
        DrawItemInteractions(rollInfo, drawMenu: true);

        ImGui.SetCursorScreenPos(new Vector2(iconPos.X + icon, origin.Y));
        ImGui.InvisibleButton("##result", new Vector2(Math.Max(max.X - iconPos.X - icon, 1f), height));
        if (ImGui.IsItemHovered())
        {
            var lines = rollInfo.GetSortedRolls()
                .Select(r => $"{(r.IsWinner ? "★ " : "   ")}{r.PlayerName}  -  {r.RollType}{(Services.RollKind.IsRoll(r.RollType) && r.RollValue > 0 ? $" {r.RollValue}" : "")}");
            Theme.Tooltip($"{rollInfo.ItemName}\n\n{string.Join("\n", lines)}");
        }

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(width, height));
        ImGui.Dummy(new Vector2(0, Theme.Px(4)));
    }

    /// <summary>
    /// The reveal for a just-resolved item, started the first time its card is drawn. Results that
    /// were already in when the window opened are shown as they are, without a race.
    /// </summary>
    private RollReveal? RevealFor(Services.RollInfo roll, List<(string PlayerName, string RollType, int RollValue, bool IsWinner)> rows)
    {
        if (reveals.TryGetValue(roll, out var existing))
            return existing;

        if (!plugin.Configuration.AnimateRollReveal || string.IsNullOrEmpty(roll.WinnerName) ||
            roll.FinishedAt is not { } at || (DateTime.Now - at).TotalSeconds > 1.0)
            return null;

        var rolled = rows.Where(r => Services.RollKind.IsRoll(r.RollType) && r.RollValue > 0).ToList();
        if (rolled.Count == 0)
            return null;

        // Need beats any Greed, so only one of the two groups races: the winner's. That is usually the
        // Need rolls, but not when the best Need couldn't receive the item, say a unique one they own.
        var winnerRow = rolled.FirstOrDefault(r => r.PlayerName == roll.WinnerName);
        var group = winnerRow.PlayerName != null
            ? winnerRow.RollType
            : rolled.Any(r => r.RollType == Services.RollKind.Need) ? Services.RollKind.Need : Services.RollKind.Greed;
        var contenders = rolled.Where(r => r.RollType == group).ToList();
        var top = contenders.Max(r => r.RollValue);

        var chosenOrder = roll.PlayerRolls.Keys.ToList();
        var reveal = new RollReveal
        {
            Order = rows.Select(r => r.PlayerName).OrderBy(n => chosenOrder.IndexOf(n)).ToList(),
            Contenders = contenders.Select(r => r.PlayerName).ToHashSet(),
            Top = top,
            // High rolls take longer to climb to, but never long enough to drag.
            RaceSeconds = 0.8f + 1.2f * top / 99f,
        };

        reveals[roll] = reveal;
        return reveal;
    }

    private static RowReveal RowStateFor(RollReveal reveal, string playerName, string rollType, int rollValue)
    {
        var t = reveal.Elapsed;
        var race = reveal.RaceSeconds;
        var settle = EaseInOut(Math.Clamp((t - race - RevealHold) / RevealSlide, 0f, 1f));
        var barAlpha = 1f - Math.Clamp((t - race - RevealHold - RevealSlide) / BarFade, 0f, 1f);
        var crownAge = t - race;

        if (!Services.RollKind.IsRoll(rollType) || rollValue <= 0)
            return new RowReveal(0, false, 99f, false, 0f, 0f, settle, crownAge);

        if (!reveal.Contenders.Contains(playerName))
        {
            // A Greed against someone's Need: shown straight away, dimmed, out of the race.
            return new RowReveal(rollValue, false, 99f, true, rollValue / 100f, barAlpha * 0.5f, settle, crownAge);
        }

        var lockAt = reveal.LockTime(rollValue);
        var counter = reveal.Top * EaseOut(Math.Clamp(t / race, 0f, 1f));
        var rolling = t < lockAt;
        var shown = rolling ? Math.Min((int)counter, rollValue - 1) : rollValue;
        return new RowReveal(Math.Max(shown, 0), rolling, t - lockAt, false, shown / 100f, barAlpha, settle, crownAge);
    }

    private static float EaseOut(float t) => 1f - (1f - t) * (1f - t) * (1f - t);

    private static float EaseInOut(float t) => t * t * (3f - 2f * t);

    /// <summary>
    /// Chimes once for an item that went to someone else, when its result lands: at the end of the
    /// reveal, or straight away without one. The game already sounds your own wins.
    /// </summary>
    private void Announce(Services.RollInfo roll, RollReveal? reveal, string localPlayerName)
    {
        if (!roll.IsFinished || announced.Contains(roll))
            return;

        var landsAt = reveal?.RaceEnd ?? roll.FinishedAt!.Value;
        if (DateTime.Now < landsAt)
            return;

        announced.Add(roll);

        var config = plugin.Configuration;
        var fresh = (DateTime.Now - landsAt).TotalSeconds < 3;
        var mine = !string.IsNullOrEmpty(roll.WinnerName) && Services.LootTrackingService.SameName(roll.WinnerName, localPlayerName);
        if (config.ChimeOnLostRoll && fresh && !mine && roll.CloseReason != Services.RollCloseReason.LeftDuty)
            chime.Play(config.ChimeVolume);
    }

    /// <summary>Plays the lost-roll chime at the current volume, for the button in the settings.</summary>
    public void TestChime() => chime.Play(plugin.Configuration.ChimeVolume);

    /// <summary>
    /// Plays once when a card's winner is announced: a light sweep across the card, a burst of
    /// sparks from the winner's crown and rings from the item icon. Clipped to the card.
    /// </summary>
    private void DrawWinnerBurst(ImDrawListPtr dl, Services.RollInfo rollInfo, Vector2 origin, Vector2 max,
        Vector2 iconCenter, float winnerRowY, Vector4 rarityColor, DateTime? announcedAt)
    {
        var config = plugin.Configuration;
        if (!config.EnableParticleEffects)
            return;

        if (!bursts.TryGetValue(rollInfo, out var burst))
        {
            // Only celebrate results as they come in, not ones already on screen when the window opened.
            // After a reveal, the result comes in when the race ends.
            if (announcedAt is not { } at || (DateTime.Now - at).TotalSeconds > 1.5)
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
                var bandW = Theme.Px(90f);
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
                    new Vector2(origin.X + Theme.Px(10f), origin.Y + winnerRowY + 1f),
                    new Vector2(max.X - Theme.Px(10f), origin.Y + winnerRowY + RowHeight - 1f),
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
        var source = winnerRowY >= 0 ? new Vector2(Theme.Px(24f), winnerRowY + RowHeight * 0.5f) : iconCenter;
        var count = (int)(26 * Math.Clamp(intensity, 0.2f, 2f));
        var scale = Theme.Px(1f);

        for (var i = 0; i < count; i++)
        {
            // Mostly upward, fanned out towards the right where the card has room.
            var angle = -MathF.PI * (0.08f + 0.84f * (float)random.NextDouble());
            var speed = (70f + 120f * (float)random.NextDouble()) * scale;
            var life = 0.9f + 0.8f * (float)random.NextDouble();
            var gold = random.NextDouble() < 0.6;

            burst.Particles.Add(new ParticleEffect
            {
                Position = source + new Vector2((float)random.NextDouble() * 10f - 5f, (float)random.NextDouble() * 6f - 3f) * scale,
                Velocity = new Vector2(MathF.Cos(angle) * speed * 1.6f, MathF.Sin(angle) * speed),
                Color = gold ? Theme.GoldBright : Theme.Lighten(rarityColor, 0.25f),
                Size = (gold ? 2.2f + 1.6f * (float)random.NextDouble() : 1.6f + 1.2f * (float)random.NextDouble()) * scale,
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
                Size = (10f + i * 8f) * scale,
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
    /// One line for everyone in an alliance who didn't roll Need or Greed, by what they did instead,
    /// with the names on hover.
    /// </summary>
    private static void DrawOthersRow(ImDrawListPtr dl, Vector2 origin, float width,
        List<(string PlayerName, string RollType, int RollValue, bool IsWinner)> others)
    {
        var groups = others.GroupBy(r => r.RollType).OrderBy(g => Services.RollKind.Order(g.Key)).ToList();
        var summary = string.Join(" · ", groups.Select(g => g.Key switch
        {
            Services.RollKind.Pass => $"{g.Count()} passed",
            Services.RollKind.NoRoll => $"{g.Count()} didn't roll",
            Services.RollKind.CantRoll => $"{g.Count()} couldn't roll",
            _ => $"{g.Count()} {g.Key.ToLowerInvariant()}",
        }));

        var textY = origin.Y + (RowHeight - ImGui.GetTextLineHeight()) * 0.5f;
        Theme.ClipText(dl, new Vector2(origin.X + Theme.Px(22f), textY), width - Theme.Px(40f), summary, Theme.U32(Theme.TextMuted));

        ImGui.SetCursorScreenPos(new Vector2(origin.X + Theme.Px(12f), origin.Y));
        ImGui.Dummy(new Vector2(width - Theme.Px(24f), RowHeight));
        if (ImGui.IsItemHovered())
            Theme.Tooltip(string.Join("\n", groups.Select(g => $"{g.Key}: {string.Join(", ", g.Select(r => r.PlayerName))}")));
    }

    private const string WeeklyLimitNote =
        "This item counts toward your weekly reward limit. The first such item you win, on Need or Greed alike, " +
        "uses it up: the others go to someone else until the weekly reset.";

    /// <summary>
    /// The warning above the buttons when you've already rolled on other weekly-limited items:
    /// whichever of them you win first, you lose the rest.
    /// </summary>
    private static void DrawStakeRow(ImDrawListPtr dl, Vector2 origin, float width, List<Services.RollInfo> atStake)
    {
        var min = new Vector2(origin.X + Theme.Px(12f), origin.Y + 1f);
        var max = new Vector2(origin.X + width - Theme.Px(12f), origin.Y + RowHeight - 2f);
        dl.AddRectFilled(min, max, Theme.U32(Theme.Warn, 0.10f), 4f);

        var textY = origin.Y + (RowHeight - ImGui.GetTextLineHeight()) * 0.5f;
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            dl.AddText(new Vector2(origin.X + Theme.Px(20f), textY + 1f), Theme.U32(Theme.Warn),
                FontAwesomeIcon.ExclamationTriangle.ToIconString());
        }

        var names = string.Join(", ", atStake.Select(r => r.ItemName));
        Theme.ClipText(dl, new Vector2(origin.X + Theme.Px(40f), textY), width - Theme.Px(58f),
            $"Also rolling on {names}", Theme.U32(Theme.Warn));

        ImGui.SetCursorScreenPos(min);
        ImGui.Dummy(max - min);
        if (ImGui.IsItemHovered())
            Theme.Tooltip(StakeNote(atStake));
    }

    private static string StakeNote(List<Services.RollInfo> atStake)
        => $"You've already rolled on {string.Join(", ", atStake.Select(r => r.ItemName))}. " +
           "Only one weekly item can be yours: if you roll here too, whichever you win first is the one you keep, " +
           "and the game decides that order, not you.";

    /// <summary>
    /// Your Need, Greed and Pass buttons, a full-width bar set apart from the results above it.
    /// Choices the game won't accept from you are greyed out, and the bar gives way to a note
    /// while a click is in flight.
    /// </summary>
    private void DrawActionBar(ImDrawListPtr dl, Services.RollInfo rollInfo, bool preview, float left, float y, float width,
        List<Services.RollInfo> atStake)
    {
        var tracker = plugin.LootTracker;

        dl.AddLine(new Vector2(left, y), new Vector2(left + width, y), Theme.U32(Theme.Line, 0.7f), 1f);
        y += Theme.Px(7f);

        if (Services.LootTrackingService.IsRollPending(rollInfo))
        {
            var note = $"Rolling {rollInfo.PendingChoice}...";
            var ns = ImGui.CalcTextSize(note);
            var center = left + width * 0.5f;
            dl.AddRectFilled(new Vector2(left, y), new Vector2(left + width, y + ActionButtonHeight), Theme.U32(Theme.Gold, 0.08f), Theme.Radius);
            Theme.DrawSpinner(dl, new Vector2(center - ns.X * 0.5f - Theme.Px(12f), y + ActionButtonHeight * 0.5f), Theme.Px(5f), 1.6f, Theme.Gold);
            dl.AddText(new Vector2(center - ns.X * 0.5f + Theme.Px(2f), y + (ActionButtonHeight - ns.Y) * 0.5f), Theme.U32(Theme.GoldBright), note);
            return;
        }

        var options = OptionsFor(rollInfo, preview);

        (Services.RollChoice Choice, FontAwesomeIcon Icon, bool Allowed, Vector4 Color, string? Blocked)[] buttons =
        [
            (Services.RollChoice.Need, FontAwesomeIcon.Dice, options.CanNeed, Theme.Good, options.NeedBlockedReason),
            (Services.RollChoice.Greed, FontAwesomeIcon.Coins, options.CanGreed, Theme.Crystal, "You can only pass on this item."),
            (Services.RollChoice.Pass, FontAwesomeIcon.Times, options.CanPass, Theme.Bad, null),
        ];

        var gap = Theme.Px(6f);
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
                    if (choice != Services.RollChoice.Pass && rollInfo.IsWeekly)
                        text += "\n\n" + (atStake.Count > 0 ? StakeNote(atStake) : WeeklyLimitNote);
                    Theme.Tooltip(text);
                }

                if (clicked && preview)
                {
                    rollInfo.PendingChoice = choice;
                    rollInfo.PendingSince = DateTime.Now;
                }
                else if (clicked)
                {
                    tracker.Roll(rollInfo, choice);
                }
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
            var glyphGap = Theme.Px(6f);
            var contentX = min.X + (buttonWidth - glyphWidth - glyphGap - ls.X) * 0.5f;
            var textY = min.Y + (ActionButtonHeight - ls.Y) * 0.5f;

            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                dl.AddText(new Vector2(contentX, textY + 1f), Theme.U32(textColor), glyph);
            }
            dl.AddText(new Vector2(contentX + glyphWidth + glyphGap, textY), Theme.U32(textColor), label);
        }
    }

    /// <summary>What the game accepts from you on the item; a preview item takes everything until you choose.</summary>
    private Services.RollOptions OptionsFor(Services.RollInfo rollInfo, bool preview)
    {
        if (!preview)
            return plugin.LootTracker.GetRollOptions(rollInfo);

        return rollInfo.IsFinished || rollInfo.PlayerRolls.ContainsKey(LocalPlayerName)
            ? default
            : new Services.RollOptions(true, true, true, null);
    }

    private static Vector4 TimerColor(double secondsLeft) => secondsLeft switch
    {
        <= 10 => Theme.Bad,
        <= 30 => Theme.Warn,
        _ => Theme.Crystal,
    };

    private static readonly FontAwesomeIcon[] DiceFaces =
    [
        FontAwesomeIcon.DiceOne, FontAwesomeIcon.DiceTwo, FontAwesomeIcon.DiceThree,
        FontAwesomeIcon.DiceFour, FontAwesomeIcon.DiceFive, FontAwesomeIcon.DiceSix,
    ];

    /// <summary>How long a number's pop lasts when it stops climbing.</summary>
    private const float LockPopSeconds = 0.28f;

    private static void DrawRollRow(ImDrawListPtr dl, Vector2 origin, float width, string playerName,
        string rollType, int rollValue, bool isWinner, string localPlayerName, RowReveal? reveal = null)
    {
        var rowHeight = RowHeight;
        var textY = origin.Y + (rowHeight - ImGui.GetTextLineHeight()) * 0.5f;
        var x = origin.X + Theme.Px(18f);
        var bandMin = new Vector2(origin.X + Theme.Px(10f), origin.Y + 1f);
        var bandMax = new Vector2(origin.X + width - Theme.Px(10f), origin.Y + rowHeight - 1f);

        var isRoll = Services.RollKind.IsRoll(rollType);
        var rollColor = rollType switch
        {
            Services.RollKind.Need => Theme.Good,
            Services.RollKind.Greed => Theme.Crystal,
            Services.RollKind.CantRoll => Theme.Warn,
            Services.RollKind.Decided => Theme.Gold,
            _ => Theme.TextMuted,
        };

        var racing = reveal is { } r0 && isRoll && rollValue > 0 && !r0.Outranked;
        var dim = reveal is { Outranked: true } ? 0.55f : 1f;

        // The race track: a bar filling behind the row as the number climbs, with a bright leading edge.
        if (reveal is { } rv && isRoll && rv.BarAlpha > 0 && rv.Bar > 0)
        {
            var trackW = bandMax.X - bandMin.X;
            var edgeX = bandMin.X + trackW * Math.Clamp(rv.Bar, 0f, 1f);
            var color = isWinner ? Theme.Mix(rollColor, Theme.GoldBright, 0.6f) : rollColor;
            dl.AddRectFilledMultiColor(bandMin, new Vector2(edgeX, bandMax.Y),
                Theme.U32(color, 0.02f * rv.BarAlpha), Theme.U32(color, 0.16f * rv.BarAlpha),
                Theme.U32(color, 0.16f * rv.BarAlpha), Theme.U32(color, 0.02f * rv.BarAlpha));
            dl.AddLine(new Vector2(edgeX, bandMin.Y + 2f), new Vector2(edgeX, bandMax.Y - 2f),
                Theme.U32(Theme.Lighten(color, 0.4f), (rv.Rolling ? 0.9f : 0.45f) * rv.BarAlpha), Math.Max(Theme.Px(1.5f), 1f));
        }

        // The slot left of the name holds the crown, or a tumbling die while the race is on. Rows
        // that raced keep the slot until they slide into place, so their names don't jump.
        float slotWidth;
        using (ImRaii.PushFont(UiBuilder.IconFont))
            slotWidth = ImGui.CalcTextSize(FontAwesomeIcon.Crown.ToIconString()).X + Theme.Px(7f);

        if (isWinner)
        {
            // Winners get a soft brass band behind the whole row.
            dl.AddRectFilled(bandMin, bandMax, Theme.U32(Theme.Gold, 0.13f), 4f);

            // The crown drops in with a little overshoot when the race is decided.
            var crownAge = reveal?.CrownAge ?? 99f;
            var drop = crownAge < 0.35f ? 1f - EaseOut(crownAge / 0.35f) : 0f;
            var scale = 1f + 0.5f * drop;
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                var glyph = FontAwesomeIcon.Crown.ToIconString();
                var font = ImGui.GetFont();
                var size = ImGui.GetFontSize() * scale;
                var gs = ImGui.CalcTextSize(glyph) * scale;
                var center = new Vector2(x + gs.X / scale * 0.5f, textY + ImGui.GetTextLineHeight() * 0.5f - Theme.Px(6f) * drop);
                dl.AddText(font, size, center - gs * 0.5f, Theme.U32(Theme.GoldBright, 1f - drop * 0.6f), glyph);
            }
            x += slotWidth;
        }
        else if (racing && reveal is { } rr)
        {
            var settle = rr.Settle;
            if (rr.Rolling || settle < 1f)
            {
                // Faces flick by while climbing, then the die rests and fades as the rows settle.
                var face = rr.Rolling ? (int)(Theme.Time * 14f + playerName.Length) % 6 : (rollValue - 1) % 6;
                var alpha = (rr.Rolling ? 0.95f : 0.5f) * (1f - settle);
                using (ImRaii.PushFont(UiBuilder.IconFont))
                    dl.AddText(new Vector2(x, textY), Theme.U32(rollColor, alpha), DiceFaces[face].ToIconString());
            }
            x += slotWidth + (Theme.Px(4f) - slotWidth) * settle;
        }
        else
        {
            x += Theme.Px(4f);
        }

        var isLocalPlayer = playerName.Equals(localPlayerName, StringComparison.OrdinalIgnoreCase) ||
                            playerName.StartsWith(localPlayerName, StringComparison.OrdinalIgnoreCase);
        var playerColor = isLocalPlayer ? Theme.Good : isRoll ? Theme.Text : Theme.TextMuted;

        // Roll type pill and value, right aligned. Passes and non-rolls have no value. During a
        // reveal the number counts up, and pops as it stops.
        var shown = racing ? reveal!.Value.Shown : rollValue;
        var valueText = !isRoll ? "-" : rollValue == Services.RollKind.PendingValue ? "..." : shown.ToString();

        // Room for the widest number the value can reach, so the pill doesn't creep left as it counts.
        var vw = ImGui.CalcTextSize(racing ? rollValue.ToString() : valueText).X;
        var valueX = origin.X + width - Theme.Px(18f) - vw;
        var valueColor = isWinner ? Theme.GoldBright : !isRoll ? Theme.TextFaint : racing && reveal!.Value.Rolling ? Theme.TextMuted : Theme.Text;

        var pop = racing && reveal!.Value.LockAge is >= 0 and < LockPopSeconds ? 1f - reveal.Value.LockAge / LockPopSeconds : 0f;
        if (pop > 0)
        {
            var scale = 1f + (isWinner ? 0.45f : 0.3f) * pop;
            var ts0 = ImGui.CalcTextSize(valueText);
            var center = new Vector2(valueX + vw - ts0.X * 0.5f, textY + ts0.Y * 0.5f);
            dl.AddText(ImGui.GetFont(), ImGui.GetFontSize() * scale, center - ts0 * scale * 0.5f,
                Theme.U32(Theme.Mix(valueColor, Theme.GoldBright, pop * 0.6f)), valueText);
        }
        else
        {
            var tw = ImGui.CalcTextSize(valueText).X;
            dl.AddText(new Vector2(valueX + vw - tw, textY), Theme.U32(valueColor, dim), valueText);
        }

        var ts = ImGui.CalcTextSize(rollType);
        var pillPad = Theme.Px(6f);
        var pillRight = valueX - Theme.Px(8f);
        var pillMin = new Vector2(pillRight - ts.X - pillPad * 2, origin.Y + Theme.Px(3.5f));
        var pillMax = new Vector2(pillRight, origin.Y + rowHeight - Theme.Px(3.5f));

        // The player's name gives way to the pill rather than running under it.
        Theme.ClipText(dl, new Vector2(x, textY), pillMin.X - Theme.Px(6f) - x, playerName, Theme.U32(playerColor, dim));

        dl.AddRectFilled(pillMin, pillMax, Theme.U32(rollColor, 0.16f * dim), (pillMax.Y - pillMin.Y) * 0.5f);
        dl.AddText(new Vector2(pillMin.X + pillPad, textY), Theme.U32(rollColor, dim), rollType);
    }

    public override void Dispose()
    {
        plugin.LootTracker.RollsUpdated -= OnRollsUpdated;
        chime.Dispose();
        base.Dispose();
    }
}
