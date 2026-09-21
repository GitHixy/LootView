using System;
using System.Linq;
using System.Numerics;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using LootView.Models;
using LootView.UI;

namespace LootView.Windows;

/// <summary>
/// Main loot display window - the live feed of everything that dropped.
/// </summary>
public class LootWindow : Window
{
    private const float RowHeight = 34f;
    private const float IconSize = 24f;
    private const float HighlightSeconds = 2.5f;

    /// <summary>Horizontal padding inside the quantity chip.</summary>
    private const float ChipPadding = 6f;

    /// <summary>Breathing room between the quantity chip and the player column.</summary>
    private const float QtyGutter = 14f;

    /// <summary>Ko-fi's brand blue, the one exception to the Eorzean palette.</summary>
    private static readonly Vector4 KofiBlue = new(0.13f, 0.59f, 0.95f, 1.0f);

    private readonly Plugin plugin;
    private readonly List<ParticleEffect> particles = new();
    private readonly Random random = new();
    private DateTime lastUpdate = DateTime.Now;
    private readonly Dictionary<Guid, bool> particlesSpawned = new(); // Track which items have spawned particles

    /// <summary>
    /// Everything looted since the counter was last reset, as quantity per
    /// (item, quality, owner). The displayed list is capped by MaxDisplayedItems and the
    /// tracker only keeps twice that in memory, so totalling the visible rows made the
    /// earned figure fall as older drops aged out. This accumulates independently.
    ///
    /// Only the identity and quantity are kept, not whole items, so a long session costs
    /// one entry per distinct item rather than one per drop. Both this and the draw loop
    /// run on the framework thread, so no locking is needed.
    /// </summary>
    private readonly Dictionary<(uint ItemId, bool IsHq, bool IsOwn), long> earned = new();

    public LootWindow(Plugin plugin) : base("LootView###LootViewMain")
    {
        this.plugin = plugin;

        // Set initial visibility from config
        IsOpen = plugin.ConfigService.Configuration.IsVisible;

        // Set window constraints
        SizeConstraintMin = new Vector2(430, 260);
        SizeConstraintMax = new Vector2(1200, 900);
        Size = new Vector2(620, 430);

        // Set initial window flags based on lock state
        UpdateWindowFlags();

        plugin.LootTracker.LootObtained += OnLootObtained;
    }

    private void OnLootObtained(LootItem item)
    {
        if (item.ItemId == 0) return;

        var key = (item.ItemId, item.IsHQ, item.IsOwnLoot);
        earned.TryGetValue(key, out var quantity);
        earned[key] = quantity + item.Quantity;
    }

    /// <summary>
    /// The overlay belongs to a character, so it stays hidden on the title and
    /// character-select screens and comes back once login completes.
    /// </summary>
    protected override bool ShouldDraw => Plugin.ClientState.IsLoggedIn;

    private void UpdateWindowFlags()
    {
        var config = plugin.ConfigService.Configuration;

        WindowFlags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

        if (config.LockWindowPosition)
        {
            WindowFlags |= ImGuiWindowFlags.NoMove;
        }

        if (config.LockWindowSize)
        {
            WindowFlags |= ImGuiWindowFlags.NoResize;
        }
    }

    private bool IsInDuty()
    {
        try
        {
            var territoryId = Plugin.ClientState.TerritoryType;
            if (territoryId == 0) return false;

            var territorySheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>();
            if (territorySheet == null) return false;

            if (territorySheet.TryGetRow(territoryId, out var territory))
            {
                return territory.ContentFinderCondition.RowId > 0;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    protected override void DrawContents()
    {
        try
        {
            var config = plugin.ConfigService.Configuration;

            BgAlpha = config.BackgroundAlpha;

            if (config.EnableParticleEffects)
            {
                UpdateParticles();
            }

            var lootItems = plugin.LootTracker.GetFilteredLoot().ToList();

            DrawToolbar(config);
            DrawValueStrip(config);

            if (lootItems.Count == 0)
            {
                Theme.EmptyState(
                    FontAwesomeIcon.Gem,
                    "No loot yet",
                    "Items you and your party obtain will appear here.");
            }
            else
            {
                DrawLootList(lootItems);
            }

            // Draw particles on top of everything
            if (config.EnableParticleEffects)
            {
                DrawParticles();
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error drawing loot window");
            ImGui.TextColored(Theme.Bad, "Error displaying loot!");
        }
    }

    // ============================================================================
    // HEADER
    // ============================================================================

    private void DrawToolbar(Configuration config)
    {
        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        const float barHeight = 30f;

        // --- Wordmark -------------------------------------------------
        var crest = new Vector2(origin.X + 11f, origin.Y + barHeight * 0.5f);
        DrawCrest(dl, crest, 11f);

        // Drawn rather than laid out so the byline can sit on the title's baseline.
        ImGui.SetWindowFontScale(1.1f);
        var titleSize = ImGui.CalcTextSize("LootView");
        var titleTop = origin.Y + (barHeight - titleSize.Y) * 0.5f;
        dl.AddText(new Vector2(origin.X + 29f, titleTop), Theme.U32(Theme.GoldBright), "LootView");

        ImGui.SetWindowFontScale(0.85f);
        var bylineSize = ImGui.CalcTextSize("by GitHixy");
        dl.AddText(
            new Vector2(origin.X + 29f + titleSize.X + 7f, titleTop + titleSize.Y - bylineSize.Y - 1f),
            Theme.U32(Theme.TextFaint), "by GitHixy");
        ImGui.SetWindowFontScale(1f);

        // --- Action cluster, right aligned ----------------------------
        var isInDuty = IsInDuty();
        const float btn = 28f;
        const float gap = 4f;
        var buttonCount = isInDuty ? 7 : 6;
        var clusterWidth = buttonCount * btn + (buttonCount - 1) * gap;

        var right = origin.X + ImGui.GetContentRegionAvail().X;
        var x = right - clusterWidth;
        var y = origin.Y + (barHeight - btn) * 0.5f;

        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(gap, gap)))
        {
            ImGui.SetCursorScreenPos(new Vector2(x, y));

            // Filter: only my loot
            var onlyMine = config.ShowOnlyOwnLoot;
            if (Theme.IconButton("##FilterOwn", onlyMine ? FontAwesomeIcon.User : FontAwesomeIcon.Users,
                    onlyMine ? "Showing only your loot - click to show everyone's" : "Showing all party loot - click to show only yours",
                    Theme.Gold, onlyMine, btn))
            {
                config.ShowOnlyOwnLoot = !onlyMine;
                config.ShowOnlyMyLoot = config.ShowOnlyOwnLoot; // Sync both properties
                plugin.ConfigService.Save();
            }

            ImGui.SameLine();
            if (Theme.IconButton("##ClearAll", FontAwesomeIcon.Broom, "Clear the current list", Theme.Bad, false, btn))
            {
                plugin.LootTracker.ClearLoot();
            }

            ImGui.SameLine();
            if (Theme.IconButton("##Stats", FontAwesomeIcon.ChartLine, "Statistics & history", Theme.Crystal, false, btn))
            {
                plugin.StatisticsWindow.IsOpen = true;
            }

            if (isInDuty)
            {
                ImGui.SameLine();
                if (Theme.IconButton("##LootTable", FontAwesomeIcon.Table, "Loot table for this duty", Theme.Crystal, false, btn))
                {
                    plugin.LootTableWindow.IsOpen = true;
                    plugin.LootTableWindow.LoadCurrentZone();
                }
            }

            ImGui.SameLine();
            var isLocked = config.LockWindowPosition;
            if (Theme.IconButton("##Lock", isLocked ? FontAwesomeIcon.Lock : FontAwesomeIcon.LockOpen,
                    isLocked ? "Unlock window" : "Lock position and size", Theme.Gold, isLocked, btn))
            {
                config.LockWindowPosition = !config.LockWindowPosition;
                config.LockWindowSize = config.LockWindowPosition;
                plugin.ConfigService.Save();
                UpdateWindowFlags();
            }

            ImGui.SameLine();
            if (Theme.IconButton("##Config", FontAwesomeIcon.Cog, "Settings", Theme.Crystal, false, btn))
            {
                plugin.ConfigWindow.IsOpen = true;
            }

            ImGui.SameLine();
            if (Theme.IconButton("##Kofi", FontAwesomeIcon.Coffee, "Support development on Ko-fi", KofiBlue, false, btn))
            {
                OpenUrl("https://ko-fi.com/hixyllian");
            }
        }

        ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + barHeight));
        ImGui.Dummy(new Vector2(ImGui.GetContentRegionAvail().X, 0));
        Theme.Rule(4f);
    }

    /// <summary>The rotated, aetherially-lit diamond used as the plugin's mark.</summary>
    private static void DrawCrest(ImDrawListPtr dl, Vector2 center, float r)
    {
        var pulse = 0.5f + 0.5f * MathF.Sin(Theme.Time * 1.1f);

        dl.AddCircleFilled(center, r * 1.5f, Theme.U32(Theme.Gold, 0.06f + pulse * 0.04f), 24);

        var outer = new[]
        {
            new Vector2(center.X, center.Y - r),
            new Vector2(center.X + r * 0.78f, center.Y),
            new Vector2(center.X, center.Y + r),
            new Vector2(center.X - r * 0.78f, center.Y),
        };
        dl.AddQuadFilled(outer[0], outer[1], outer[2], outer[3], Theme.U32(Theme.Gold, 0.22f));
        dl.AddQuad(outer[0], outer[1], outer[2], outer[3], Theme.U32(Theme.Gold, 0.85f), 1.3f);

        var inner = r * 0.42f;
        dl.AddQuadFilled(
            new Vector2(center.X, center.Y - inner),
            new Vector2(center.X + inner * 0.78f, center.Y),
            new Vector2(center.X, center.Y + inner),
            new Vector2(center.X - inner * 0.78f, center.Y),
            Theme.U32(Theme.GoldBright, 0.55f + pulse * 0.45f));
    }

    /// <summary>
    /// Running market value of everything in the list, priced on the player's home world.
    /// Both figures are shown because they answer different questions: the average is what
    /// items have been selling for, the minimum is what you would have to list at today.
    /// </summary>
    private void DrawValueStrip(Configuration config)
    {
        if (!config.EnableMarketPrices) return;

        var market = plugin.MarketPriceService;

        // Respect the same toggles the list does, so the total always matches what the
        // window claims to be showing. Blacklisted items were never meant to be tracked.
        var counted = earned
            .Where(e => !config.ShowOnlyOwnLoot || e.Key.IsOwn)
            .Where(e => !(config.BlacklistedItemIds?.Contains(e.Key.ItemId) ?? false))
            .Where(e => market.IsMarketable(e.Key.ItemId))
            .ToList();

        if (counted.Count == 0) return;

        market.RequestPrices(counted.Select(e => e.Key.ItemId).Distinct());

        double averageTotal = 0, minimumTotal = 0;
        long totalUnits = 0;
        var priced = 0;    // has sale history, counts toward Avg
        var listed = 0;    // has a live listing, counts toward Now
        var unpriced = 0;  // Universalis knows nothing about it

        foreach (var (key, quantity) in counted)
        {
            totalUnits += quantity;

            if (market.TryGetPrice(key.ItemId, out var price) && price.HasData)
            {
                var avg = price.Average(key.IsHq);
                var min = price.Minimum(key.IsHq);

                // An item can have sale history but nothing listed right now. Counting
                // that as zero would quietly understate the second figure, so each total
                // only sums the items it actually has a price for.
                if (avg > 0)
                {
                    averageTotal += avg * quantity;
                    priced++;
                }

                if (min > 0)
                {
                    minimumTotal += min * quantity;
                    listed++;
                }

                if (avg > 0 || min > 0) continue;
            }

            unpriced++;
        }

        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        const float h = 30f;
        var max = new Vector2(origin.X + width, origin.Y + h);

        dl.AddRectFilled(origin, max, Theme.U32(Theme.Surface, 0.55f), Theme.Radius);
        dl.AddRectFilledMultiColor(origin, max,
            Theme.U32(Theme.Gold, 0.10f), Theme.U32(Theme.Gold, 0.02f),
            Theme.U32(Theme.Gold, 0f), Theme.U32(Theme.Gold, 0.04f));
        dl.AddRectFilled(new Vector2(origin.X, origin.Y + 5), new Vector2(origin.X + 2.5f, max.Y - 5),
            Theme.U32(Theme.Gold, 0.9f), 1.5f);

        var textY = origin.Y + (h - ImGui.GetTextLineHeight()) * 0.5f;
        var x = origin.X + 13f;

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var glyph = FontAwesomeIcon.Coins.ToIconString();
            dl.AddText(new Vector2(x, textY), Theme.U32(Theme.Gold), glyph);
            x += ImGui.CalcTextSize(glyph).X + 9f;
        }

        var busy = market.IsFetching || market.HasWork;

        if (priced == 0)
        {
            if (busy || !market.HasWorld)
            {
                // A spinner rather than a line of text: the wait is short, and swapping
                // messages in and out on consecutive frames reads as a flicker.
                var r = ImGui.GetTextLineHeight() * 0.38f;
                Theme.DrawSpinner(dl, new Vector2(x + r, textY + ImGui.GetTextLineHeight() * 0.5f), r, 2f, Theme.Gold);
                dl.AddText(new Vector2(x + r * 2 + 9f, textY), Theme.U32(Theme.TextFaint), "Checking prices");
            }
            else
            {
                var idle = market.IsPaused
                    ? "Universalis unavailable, retrying shortly"
                    : "No market data for these items";
                dl.AddText(new Vector2(x, textY), Theme.U32(Theme.TextFaint), idle);
            }
        }
        else
        {
            dl.AddText(new Vector2(x, textY), Theme.U32(Theme.TextMuted), "Avg");
            x += ImGui.CalcTextSize("Avg").X + 7f;

            var avgText = FormatGil(averageTotal);
            dl.AddText(new Vector2(x, textY), Theme.U32(Theme.GoldBright), avgText);
            x += ImGui.CalcTextSize(avgText).X + 14f;

            dl.AddText(new Vector2(x, textY), Theme.U32(Theme.TextFaint), "|");
            x += ImGui.CalcTextSize("|").X + 14f;

            dl.AddText(new Vector2(x, textY), Theme.U32(Theme.TextMuted), "Now");
            x += ImGui.CalcTextSize("Now").X + 7f;

            var minText = listed > 0 ? FormatGil(minimumTotal) : "nothing listed";
            dl.AddText(new Vector2(x, textY),
                Theme.U32(listed > 0 ? Theme.Crystal : Theme.TextFaint), minText);
            x += ImGui.CalcTextSize(minText).X;
        }

        // Right side: the world being priced, and anything we could not price.
        // Totals are already on screen; a refresh for newly dropped items spins quietly
        // on the right rather than replacing them.
        if (priced > 0 && busy)
        {
            var r = ImGui.GetTextLineHeight() * 0.36f;
            Theme.DrawSpinner(dl, new Vector2(x + 14f + r, textY + ImGui.GetTextLineHeight() * 0.5f), r, 1.8f, Theme.Crystal);
        }

        var notes = new List<string>();
        if (priced > listed) notes.Add($"{priced - listed} unlisted");
        if (unpriced > 0) notes.Add($"{unpriced} unpriced");
        if (!string.IsNullOrEmpty(market.WorldName)) notes.Add(market.WorldName);

        const float resetSize = 20f;
        var resetMin = new Vector2(max.X - 13f - resetSize, origin.Y + (h - resetSize) * 0.5f);
        var overReset = ImGui.IsMouseHoveringRect(resetMin, resetMin + new Vector2(resetSize, resetSize));
        var notesRight = resetMin.X - 8f;

        if (notes.Count > 0)
        {
            var note = string.Join("  -  ", notes);
            var nw = ImGui.CalcTextSize(note).X;
            dl.AddText(new Vector2(notesRight - nw, textY), Theme.U32(Theme.TextFaint), note);
        }

        // Hover target for the whole strip, submitted before the button so the button wins
        // the click.
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(width, h));

        if (ImGui.IsItemHovered() && !overReset)
        {
            Theme.Tooltip(
                $"Everything looted since the counter was last reset, valued on " +
                $"{market.WorldName ?? "your home world"} via Universalis.\n\n" +
                $"Avg - what these items have been selling for recently.\n" +
                $"Now - the cheapest listings currently up.\n\n" +
                $"{totalUnits:N0} units across {counted.Count} sellable items. " +
                $"{priced} have sale history, {listed} have something listed right now.\n" +
                "This total is independent of the list, which only keeps the most recent " +
                "items. Prices are crowd-sourced and exclude the 5% market board tax.");
        }

        // Resets only the earned total - clearing the list leaves it alone, and vice versa.
        ImGui.SetCursorScreenPos(resetMin);
        if (Theme.IconButton("##ResetEarned", FontAwesomeIcon.Undo,
                "Reset the earned total\nThe loot list is left untouched", Theme.Gold, false, resetSize))
        {
            earned.Clear();
        }

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(width, h));
        ImGui.Dummy(new Vector2(0, 4));
    }

    /// <summary>
    /// Compact gil figure: 840 / 7.9k / 3.51m.
    /// Both totals sit on one line, so they have to compact at the same threshold - with a
    /// 10k cutoff and a locale that groups with dots, "14.3k" next to "7.852" read as two
    /// different kinds of number.
    /// </summary>
    private static string FormatGil(double gil)
    {
        if (gil >= 1_000_000) return $"{gil / 1_000_000:0.##}m";
        if (gil >= 1_000) return $"{gil / 1_000:0.#}k";
        return $"{gil:0}";
    }

    // ============================================================================
    // LOOT LIST
    // ============================================================================

    private void DrawLootList(List<LootItem> lootItems)
    {
        var avail = ImGui.GetContentRegionAvail();

        // The header labels sit outside the scrolling child, so they have to account for
        // the scrollbar up front or the columns drift by its width once the list overflows.
        var labelRowHeight = ImGui.GetTextLineHeight() + 4f;
        var contentHeight = lootItems.Count * (RowHeight + 2f);
        var needsScrollbar = contentHeight > avail.Y - labelRowHeight;
        var listWidth = Math.Max(avail.X - (needsScrollbar ? ImGui.GetStyle().ScrollbarSize : 0f), 80f);

        // Column geometry, resolved once per frame from the available width.
        const float accentW = 3f;
        const float iconX = 12f;
        const float nameX = iconX + IconSize + 10f;
        var timeW = 60f;

        // The quantity chip is sized to the widest stack on screen plus a fixed gutter,
        // so four-digit counts never run into the player column.
        var widestQty = 0f;
        foreach (var item in lootItems)
            widestQty = Math.Max(widestQty, ImGui.CalcTextSize($"x{item.Quantity}").X);
        var qtyW = Math.Clamp(widestQty + ChipPadding * 2f + QtyGutter, 46f, 110f);

        var playerW = Math.Clamp(listWidth * 0.26f, 70f, 150f);
        var nameW = Math.Max(listWidth - nameX - timeW - qtyW - playerW - 30f, 60f);

        DrawColumnLabels(nameX, nameW, qtyW, playerW, timeW, listWidth);

        using var child = Theme.Region("LootItemsChild", new Vector2(avail.X, ImGui.GetContentRegionAvail().Y));
        if (!child) return;

        var dl = ImGui.GetWindowDrawList();
        var config = plugin.ConfigService.Configuration;

        foreach (var item in lootItems)
        {
            var rowOrigin = ImGui.GetCursorScreenPos();
            var rowWidth = listWidth;
            var rowMax = new Vector2(rowOrigin.X + rowWidth, rowOrigin.Y + RowHeight);

            var age = (DateTime.Now - item.Timestamp).TotalSeconds;
            var isNew = age < HighlightSeconds;
            var rarityColor = Theme.RarityColor(item.Rarity);

            // Hit area first so the visuals below never steal hover from the row.
            var id = $"##row_{item.Id}";
            ImGui.InvisibleButton(id, new Vector2(rowWidth, RowHeight));
            var hovered = ImGui.IsItemHovered();

            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                ImGui.OpenPopup($"##ctx_{item.Id}");

            // --- Row background ---------------------------------------
            if (hovered)
            {
                dl.AddRectFilled(rowOrigin, rowMax, Theme.U32(Theme.SurfaceHover, 0.75f), Theme.Radius);
            }
            else
            {
                dl.AddRectFilled(rowOrigin, rowMax, Theme.U32(Theme.Surface, 0.42f), Theme.Radius);
            }

            if (isNew)
            {
                // A brass sweep travels across the row once, then settles into a fade.
                var t = (float)(age / HighlightSeconds);
                var fade = 1f - t;

                dl.AddRectFilled(rowOrigin, rowMax, Theme.U32(Theme.Gold, 0.13f * fade), Theme.Radius);

                var sweep = rowOrigin.X + rowWidth * Math.Min(t * 1.8f, 1f);
                var tail = Math.Max(sweep - rowWidth * 0.32f, rowOrigin.X);
                dl.AddRectFilledMultiColor(
                    new Vector2(tail, rowOrigin.Y), new Vector2(sweep, rowMax.Y),
                    Theme.U32(Theme.GoldBright, 0f), Theme.U32(Theme.GoldBright, 0.18f * fade),
                    Theme.U32(Theme.GoldBright, 0.18f * fade), Theme.U32(Theme.GoldBright, 0f));

                dl.AddRect(rowOrigin, rowMax, Theme.U32(Theme.Gold, 0.55f * fade), Theme.Radius, ImDrawFlags.None, 1f);
            }

            // Rarity spine on the leading edge.
            dl.AddRectFilled(
                new Vector2(rowOrigin.X, rowOrigin.Y + 5),
                new Vector2(rowOrigin.X + accentW, rowMax.Y - 5),
                Theme.U32(rarityColor, isNew ? 1f : 0.8f), 1.5f);

            // --- Icon --------------------------------------------------
            var iconPos = new Vector2(rowOrigin.X + iconX, rowOrigin.Y + (RowHeight - IconSize) * 0.5f);
            DrawItemIcon(dl, item, iconPos, rarityColor, isNew);

            if (age < 0.1 && config.EnableParticleEffects)
            {
                SpawnParticlesForItem(item, iconPos + new Vector2(IconSize * 0.5f, IconSize * 0.5f));
            }

            // --- Name + HQ --------------------------------------------
            var textY = rowOrigin.Y + (RowHeight - ImGui.GetTextLineHeight()) * 0.5f;
            var namePos = new Vector2(rowOrigin.X + nameX, textY);
            var name = ToTitleCase(item.ItemName);

            var hqWidth = item.IsHQ ? 20f : 0f;
            DrawClipped(dl, namePos, nameW - hqWidth, name, Theme.U32(rarityColor));

            if (item.IsHQ)
            {
                var nameWidth = Math.Min(ImGui.CalcTextSize(name).X, nameW - hqWidth);
                DrawHqMark(dl, new Vector2(namePos.X + nameWidth + 6, textY));
            }

            // --- Quantity ----------------------------------------------
            var qtyX = rowOrigin.X + nameX + nameW + 8;
            if (item.Quantity > 1)
            {
                var qty = $"x{item.Quantity}";
                var qs = ImGui.CalcTextSize(qty);
                var chipMin = new Vector2(qtyX, rowOrigin.Y + (RowHeight - qs.Y - 5) * 0.5f);
                var chipMax = new Vector2(chipMin.X + qs.X + ChipPadding * 2f, chipMin.Y + qs.Y + 5);
                dl.AddRectFilled(chipMin, chipMax, Theme.U32(Theme.Crystal, 0.16f), (chipMax.Y - chipMin.Y) * 0.5f);
                dl.AddText(new Vector2(chipMin.X + ChipPadding, chipMin.Y + 2.5f), Theme.U32(Theme.CrystalBright), qty);
            }
            else
            {
                dl.AddText(new Vector2(qtyX + ChipPadding, textY), Theme.U32(Theme.TextFaint), "x1");
            }

            // --- Player -------------------------------------------------
            var playerX = qtyX + qtyW;
            var playerColor = item.IsOwnLoot ? Theme.Good : Theme.TextMuted;
            DrawClipped(dl, new Vector2(playerX, textY), playerW - 8, item.PlayerName, Theme.U32(playerColor));

            // --- Time ---------------------------------------------------
            var timeText = FormatTimeAgo(item.Timestamp);
            var tw = ImGui.CalcTextSize(timeText).X;
            dl.AddText(new Vector2(rowMax.X - tw - 8, textY), Theme.U32(Theme.TextFaint), timeText);

            if (hovered)
                ShowItemTooltip(item);

            DrawItemContextMenu(item);

            ImGui.Dummy(new Vector2(0, 2));
        }
    }

    private static void DrawColumnLabels(float nameX, float nameW, float qtyW, float playerW, float timeW, float listWidth)
    {
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();

        using var s = new Theme.FontScale(0.85f);
        var col = Theme.U32(Theme.TextFaint, 0.9f);

        // These offsets mirror the row layout exactly: qtyX = nameX + nameW + 8.
        dl.AddText(new Vector2(p.X + nameX, p.Y), col, "ITEM");
        dl.AddText(new Vector2(p.X + nameX + nameW + 8 + ChipPadding, p.Y), col, "QTY");
        dl.AddText(new Vector2(p.X + nameX + nameW + 8 + qtyW, p.Y), col, "PLAYER");

        var tw = ImGui.CalcTextSize("TIME").X;
        dl.AddText(new Vector2(p.X + listWidth - tw - 8, p.Y), col, "TIME");

        ImGui.Dummy(new Vector2(listWidth, ImGui.GetTextLineHeight() + 4));
    }

    /// <summary>Item icon in a beveled, rarity-tinted frame.</summary>
    private void DrawItemIcon(ImDrawListPtr dl, LootItem item, Vector2 pos, Vector4 rarityColor, bool isNew)
    {
        var max = pos + new Vector2(IconSize, IconSize);

        // Rarity halo for anything above uncommon, brighter while the drop is fresh.
        if (item.Rarity >= 3 || isNew)
        {
            var glow = isNew ? 0.5f : 0.22f;
            dl.AddRectFilled(pos - new Vector2(2, 2), max + new Vector2(2, 2), Theme.U32(rarityColor, glow * 0.35f), 5f);
        }

        dl.AddRectFilled(pos, max, Theme.U32(Theme.Panel, 0.9f), 4f);

        var drawn = false;
        if (item.IconId > 0)
        {
            try
            {
                var tex = Plugin.TextureProvider
                    .GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(item.IconId))
                    .GetWrapOrDefault();
                if (tex != null)
                {
                    dl.AddImage(tex.Handle, pos, max);
                    drawn = true;
                }
            }
            catch { /* Ignore icon loading errors */ }
        }

        if (!drawn)
        {
            var q = "?";
            var qs = ImGui.CalcTextSize(q);
            dl.AddText(new Vector2(pos.X + (IconSize - qs.X) * 0.5f, pos.Y + (IconSize - qs.Y) * 0.5f),
                Theme.U32(Theme.TextFaint), q);
        }

        dl.AddRect(pos, max, Theme.U32(rarityColor, item.Rarity >= 2 ? 0.7f : 0.28f), 4f, ImDrawFlags.None, 1f);
    }

    /// <summary>The small gold "HQ" seal the game puts beside high-quality items.</summary>
    private static void DrawHqMark(ImDrawListPtr dl, Vector2 pos)
    {
        var h = ImGui.GetTextLineHeight();
        var size = new Vector2(18, h);
        var max = pos + size;
        dl.AddRectFilled(pos, max, Theme.U32(Theme.Warn, 0.2f), 3f);
        dl.AddRect(pos, max, Theme.U32(Theme.Warn, 0.6f), 3f, ImDrawFlags.None, 1f);

        using var s = new Theme.FontScale(0.8f);
        var ts = ImGui.CalcTextSize("HQ");
        dl.AddText(new Vector2(pos.X + (size.X - ts.X) * 0.5f, pos.Y + (size.Y - ts.Y) * 0.5f),
            Theme.U32(Theme.Warn), "HQ");
    }

    /// <summary>Draws text, trimming with an ellipsis when it would overrun its column.</summary>
    private static void DrawClipped(ImDrawListPtr dl, Vector2 pos, float maxWidth, string text, uint color)
    {
        if (maxWidth <= 8f) return;

        if (ImGui.CalcTextSize(text).X <= maxWidth)
        {
            dl.AddText(pos, color, text);
            return;
        }

        var ellipsisWidth = ImGui.CalcTextSize("...").X;
        var budget = maxWidth - ellipsisWidth;
        var length = text.Length;

        while (length > 1 && ImGui.CalcTextSize(text[..length]).X > budget)
            length--;

        dl.AddText(pos, color, text[..length] + "...");
    }

    private void DrawItemContextMenu(LootItem item)
    {
        using var popup = ImRaii.Popup($"##ctx_{item.Id}");
        if (!popup) return;

        var config = plugin.ConfigService.Configuration;
        var isBlacklisted = config.BlacklistedItemIds?.Contains(item.ItemId) ?? false;

        ImGui.TextColored(Theme.RarityColor(item.Rarity), ToTitleCase(item.ItemName));
        ImGui.Separator();

        if (isBlacklisted)
        {
            Theme.Icon(FontAwesomeIcon.EyeSlash, Theme.Good);
            ImGui.SameLine(0, 8);
            if (ImGui.MenuItem("Remove from blacklist"))
            {
                config.BlacklistedItemIds?.Remove(item.ItemId);
                plugin.ConfigService.Save();
            }
        }
        else
        {
            Theme.Icon(FontAwesomeIcon.Ban, Theme.Bad);
            ImGui.SameLine(0, 8);
            if (ImGui.MenuItem("Add to blacklist"))
            {
                config.BlacklistedItemIds ??= new List<uint>();
                if (!config.BlacklistedItemIds.Contains(item.ItemId))
                {
                    config.BlacklistedItemIds.Add(item.ItemId);
                    plugin.ConfigService.Save();
                }
            }
        }

        Theme.Icon(FontAwesomeIcon.Copy, Theme.Crystal);
        ImGui.SameLine(0, 8);
        if (ImGui.MenuItem("Copy item name"))
        {
            ImGui.SetClipboardText(ToTitleCase(item.ItemName));
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to open {Url}", url);
        }
    }

    private static string FormatTimeAgo(DateTime timestamp)
    {
        var elapsed = DateTime.Now - timestamp;
        return elapsed.TotalMinutes < 1
            ? $"{elapsed.Seconds}s ago"
            : elapsed.TotalHours < 1
                ? $"{(int)elapsed.TotalMinutes}m ago"
                : $"{(int)elapsed.TotalHours}h ago";
    }

    private static string ToTitleCase(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var textInfo = System.Globalization.CultureInfo.CurrentCulture.TextInfo;
        var titleCased = textInfo.ToTitleCase(text.ToLower());

        // Fix common words that should stay lowercase
        var wordsToLower = new[] { " Of ", " The ", " A ", " An ", " And ", " Or ", " In ", " On ", " At ", " To ", " For ", " With " };
        foreach (var word in wordsToLower)
        {
            titleCased = titleCased.Replace(word, word.ToLower());
        }

        return titleCased;
    }

    // ============================================================================
    // TOOLTIP SYSTEM
    // ============================================================================

    private void ShowItemTooltip(LootItem item)
    {
        if (!plugin.ConfigService.Configuration.ShowTooltips)
            return;

        using var s = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(13, 11))
            .Push(ImGuiStyleVar.WindowRounding, Theme.Radius);
        using var c = ImRaii.PushColor(ImGuiCol.PopupBg, new Vector4(0.055f, 0.078f, 0.122f, 0.98f))
            .Push(ImGuiCol.Border, Theme.Alpha(Theme.RarityColor(item.Rarity), 0.55f));

        ImGui.BeginTooltip();

        var rarityColor = Theme.RarityColor(item.Rarity);

        // Header: icon, name, rarity.
        if (item.IconId > 0)
        {
            try
            {
                var tex = Plugin.TextureProvider
                    .GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(item.IconId))
                    .GetWrapOrDefault();
                if (tex != null)
                {
                    ImGui.Image(tex.Handle, new Vector2(36, 36));
                    ImGui.SameLine(0, 10);
                }
            }
            catch { /* Ignore icon loading errors */ }
        }

        ImGui.BeginGroup();
        using (new Theme.FontScale(1.08f))
        {
            ImGui.TextColored(rarityColor, ToTitleCase(item.ItemName));
        }

        Theme.RarityGem(item.Rarity, 9f);
        ImGui.SameLine(0, 5);
        ImGui.TextColored(Theme.Alpha(rarityColor, 0.8f), Theme.RarityName(item.Rarity));
        if (item.IsHQ)
        {
            ImGui.SameLine(0, 8);
            Theme.Badge("HQ", Theme.Warn);
        }
        ImGui.EndGroup();

        Theme.Rule(5f);

        TooltipRow(FontAwesomeIcon.LayerGroup, "Quantity", $"x{item.Quantity}");
        TooltipRow(FontAwesomeIcon.Hashtag, "Item ID", item.ItemId.ToString());
        TooltipRow(FontAwesomeIcon.Bullseye, "Source", item.Source.ToString());

        if (!string.IsNullOrEmpty(item.ZoneName))
            TooltipRow(FontAwesomeIcon.MapMarkerAlt, "Zone", item.ZoneName);

        if (!string.IsNullOrEmpty(item.RollType))
        {
            var rollColor = item.RollType == "Need" ? Theme.Good : Theme.Crystal;
            TooltipRow(FontAwesomeIcon.Dice, item.RollType, item.RollValue.ToString(), rollColor);
        }

        Theme.Rule(5f);

        var playerColor = item.IsOwnLoot ? Theme.Good : Theme.Text;
        Theme.IconText(item.IsOwnLoot ? FontAwesomeIcon.Star : FontAwesomeIcon.User,
            item.IsOwnLoot ? "You obtained this" : item.PlayerName, playerColor);

        ImGui.TextColored(Theme.TextFaint, $"{FormatTimeAgo(item.Timestamp)}  ·  {item.Timestamp:HH:mm:ss}");

        ImGui.EndTooltip();
    }

    private static void TooltipRow(FontAwesomeIcon icon, string label, string value, Vector4? valueColor = null)
    {
        Theme.Icon(icon, Theme.TextFaint);
        ImGui.SameLine(0, 8);
        ImGui.TextColored(Theme.TextMuted, label);
        ImGui.SameLine(115);
        ImGui.TextColored(valueColor ?? Theme.Text, value);
    }

    // ============================================================================
    // PARTICLE SYSTEM
    // ============================================================================

    private void UpdateParticles()
    {
        var now = DateTime.Now;
        var deltaTime = (float)(now - lastUpdate).TotalSeconds;
        lastUpdate = now;

        // Update existing particles
        for (int i = particles.Count - 1; i >= 0; i--)
        {
            particles[i].Update(deltaTime);
            if (!particles[i].IsAlive)
            {
                particles.RemoveAt(i);
            }
        }
    }

    private void SpawnParticlesForItem(LootItem item, Vector2 position)
    {
        // Check if we already spawned particles for this item
        if (particlesSpawned.ContainsKey(item.Id))
            return;

        particlesSpawned[item.Id] = true;

        var config = plugin.ConfigService.Configuration;
        if (!config.EnableParticleEffects)
            return;

        // Get rarity-specific particle configuration
        var particleConfig = GetParticleConfigForRarity(item.Rarity);
        var particleCount = (int)(particleConfig.Count * config.ParticleIntensity);

        for (int i = 0; i < particleCount; i++)
        {
            var angle = random.NextDouble() * Math.PI * 2;
            var speed = particleConfig.Speed * (0.5f + (float)random.NextDouble() * 0.5f);
            var velocity = new Vector2(
                (float)Math.Cos(angle) * speed,
                (float)Math.Sin(angle) * speed - particleConfig.InitialYVelocity
            );

            var particle = new ParticleEffect
            {
                Position = position + new Vector2((float)random.NextDouble() * 20 - 10, (float)random.NextDouble() * 20 - 10),
                Velocity = velocity,
                Color = particleConfig.Color,
                Size = particleConfig.Size * (0.7f + (float)random.NextDouble() * 0.6f),
                Life = particleConfig.Life * (0.8f + (float)random.NextDouble() * 0.4f),
                MaxLife = particleConfig.Life,
                Type = particleConfig.Type,
                Rotation = (float)(random.NextDouble() * Math.PI * 2),
                RotationSpeed = ((float)random.NextDouble() - 0.5f) * 4f
            };

            particles.Add(particle);
        }

        // Add special effect rings for rare items
        if (item.Rarity >= 3)
        {
            for (int i = 0; i < 3; i++)
            {
                particles.Add(new ParticleEffect
                {
                    Position = position,
                    Velocity = Vector2.Zero,
                    Color = new Vector4(particleConfig.Color.X, particleConfig.Color.Y, particleConfig.Color.Z, 0.6f),
                    Size = 10f + i * 25f,
                    Life = 1.5f + i * 0.4f,
                    MaxLife = 1.5f + i * 0.4f,
                    Type = ParticleType.Ring,
                    Rotation = 0,
                    RotationSpeed = 0
                });
            }
        }
    }

    private void DrawParticles()
    {
        if (particles.Count == 0)
            return;

        var drawList = ImGui.GetWindowDrawList();

        foreach (var particle in particles)
        {
            // Particle.Position is already in screen coordinates
            var screenPos = particle.Position;

            switch (particle.Type)
            {
                case ParticleType.Spark:
                    // Bright small point
                    drawList.AddCircleFilled(screenPos, particle.Size, ImGui.GetColorU32(particle.Color), 8);
                    break;

                case ParticleType.Glow:
                    // Soft glowing orb with gradient
                    drawList.AddCircleFilled(screenPos, particle.Size, ImGui.GetColorU32(particle.Color), 16);
                    var glowColor = new Vector4(particle.Color.X, particle.Color.Y, particle.Color.Z, particle.Color.W * 0.3f);
                    drawList.AddCircleFilled(screenPos, particle.Size * 1.5f, ImGui.GetColorU32(glowColor), 16);
                    break;

                case ParticleType.Star:
                    // Star shape using lines
                    for (int i = 0; i < 4; i++)
                    {
                        var angle = particle.Rotation + i * (float)Math.PI / 2;
                        var offset = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * particle.Size;
                        drawList.AddLine(screenPos - offset, screenPos + offset, ImGui.GetColorU32(particle.Color), 3f);
                    }
                    break;

                case ParticleType.Ring:
                    // Expanding ring
                    var ringSize = particle.Size * (1f - particle.Life / particle.MaxLife) * 3f;
                    drawList.AddCircle(screenPos, ringSize, ImGui.GetColorU32(particle.Color), 32, 3f);
                    break;

                case ParticleType.Trail:
                    // Motion trail
                    var trailEnd = screenPos - particle.Velocity * 0.1f;
                    drawList.AddLine(screenPos, trailEnd, ImGui.GetColorU32(particle.Color), particle.Size);
                    break;

                case ParticleType.Shimmer:
                    // Twinkling star
                    var shimmerSize = particle.Size * (0.5f + 0.5f * (float)Math.Sin(particle.Life * 10));
                    drawList.AddCircleFilled(screenPos, shimmerSize, ImGui.GetColorU32(particle.Color), 8);
                    break;
            }
        }
    }

    private (int Count, float Speed, float InitialYVelocity, Vector4 Color, float Size, float Life, ParticleType Type) GetParticleConfigForRarity(uint rarity)
    {
        return rarity switch
        {
            // Common (White) - Simple sparks
            1 => (
                Count: 15,
                Speed: 120f,
                InitialYVelocity: 60f,
                Color: Theme.RarityCommon,
                Size: 3f,
                Life: 1.2f,
                Type: ParticleType.Spark
            ),

            // Uncommon (Green) - Glowing orbs
            2 => (
                Count: 20,
                Speed: 130f,
                InitialYVelocity: 70f,
                Color: Theme.RarityUncommon,
                Size: 4f,
                Life: 1.5f,
                Type: ParticleType.Glow
            ),

            // Rare (Blue) - Stars with shimmer
            3 => (
                Count: 30,
                Speed: 150f,
                InitialYVelocity: 80f,
                Color: Theme.RarityRare,
                Size: 6f,
                Life: 2.0f,
                Type: ParticleType.Star
            ),

            // Relic (Purple) - Multiple effects
            4 => (
                Count: 45,
                Speed: 180f,
                InitialYVelocity: 100f,
                Color: Theme.RarityRelic,
                Size: 7f,
                Life: 2.5f,
                Type: ParticleType.Shimmer
            ),

            // Aetherial (Pink) - Trails and sparkles
            7 => (
                Count: 35,
                Speed: 160f,
                InitialYVelocity: 90f,
                Color: Theme.RarityAetherial,
                Size: 6f,
                Life: 2.2f,
                Type: ParticleType.Trail
            ),

            // Default - Basic sparks
            _ => (
                Count: 15,
                Speed: 120f,
                InitialYVelocity: 60f,
                Color: new Vector4(0.8f, 0.8f, 0.8f, 1.0f),
                Size: 3f,
                Life: 1.2f,
                Type: ParticleType.Spark
            )
        };
    }

    public override void Dispose()
    {
        plugin.LootTracker.LootObtained -= OnLootObtained;

        // Save window visibility state
        plugin.ConfigService.Configuration.IsVisible = IsOpen;
        plugin.ConfigService.Save();

        base.Dispose();
    }
}
