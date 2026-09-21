using System;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using LootView.Models;
using LootView.UI;

namespace LootView.Windows;

public class StatisticsWindow : Window
{
    private readonly Plugin plugin;

    private int tab;

    /// <summary>History size this frame; every cache key includes it.</summary>
    private int itemCount;

    // Everything expensive is computed on the thread pool. The window keeps drawing the
    // previous result while a new one is in flight, so opening this window - or changing a
    // filter - never blocks the game.
    private readonly AsyncValue<LootStatistics> statsAsync = new("overview statistics");
    private readonly AsyncValue<List<LootItem>> historyAsync = new("history filter");
    private readonly AsyncValue<LootStatistics> currentPeriodAsync = new("current period");
    private readonly AsyncValue<LootStatistics> previousPeriodAsync = new("previous period");
    private readonly AsyncValue<Dictionary<uint, DutyStatistics>> dutyStatsAsync = new("duty statistics");
    private readonly AsyncValue<List<DutyRun>> recentRunsAsync = new("recent duty runs");
    private readonly AsyncValue<List<DutyRun>> bestRunsAsync = new("best runs");
    private readonly AsyncValue<List<DutyRun>> fastestRunsAsync = new("fastest runs");

    private DateTime statsStartDate = DateTime.Now.AddDays(-30);
    private DateTime statsEndDate = DateTime.Now;
    private int dateRangeOption = 3; // 0=Today, 1=Week, 2=Month, 3=All Time

    // For history browser
    private string searchQuery = "";
    private uint filterRarity = 999; // 999 = all
    private string filterZone = "";
    private bool filterOwnLootOnly = false;
    private bool filterHQOnly = false;
    private int historyPage = 0;
    private const int ItemsPerPage = 50;
    private readonly string sortColumn = "Timestamp";
    private readonly bool sortDescending = true;

    // Icon texture cache to prevent loading the same icon multiple times
    private readonly Dictionary<uint, Dalamud.Interface.Textures.ISharedImmediateTexture> iconCache = new();

    // For analytics
    private int comparisonDays = 7;

    // For duty tracker
    private int dutyTypeFilter;
    private int dutyView; // 0 = leaderboard, 1 = recent runs, 2 = best runs
    private uint selectedDutyId = 0;

    private static readonly string[] DutyTypes = ["All", "Dungeon", "Trial", "Raid", "Alliance Raid"];

    // For zone finder
    private string zoneSearchQuery = "";
    private List<ZoneSearchResult> zoneSearchResults = new();
    private bool zoneSearchPerformed = false;

    private static readonly (FontAwesomeIcon, string)[] Tabs =
    [
        (FontAwesomeIcon.ChartPie, "Overview"),
        (FontAwesomeIcon.Book, "History"),
        (FontAwesomeIcon.ChartLine, "Trends"),
        (FontAwesomeIcon.Calculator, "Analytics"),
        (FontAwesomeIcon.Flag, "Duties"),
        (FontAwesomeIcon.Search, "Zone Finder"),
        (FontAwesomeIcon.Ban, "Blacklist"),
        (FontAwesomeIcon.Download, "Export"),
    ];

    public StatisticsWindow(Plugin plugin) : base("Loot Statistics###LootView_Statistics")
    {
        this.plugin = plugin;

        Size = new Vector2(940, 660);
        SizeConstraintMin = new Vector2(760, 520);
        SizeConstraintMax = new Vector2(1900, 1300);
        WindowFlags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
    }

    protected override void DrawContents()
    {
        BgAlpha = Math.Max(plugin.Configuration.BackgroundAlpha, 0.85f);

        // Read once per frame: every cache key below is keyed on it, so a new drop
        // invalidates exactly the views that depend on the history.
        itemCount = plugin.HistoryService.ItemCount;

        RequestOverviewStatistics();

        var history = plugin.HistoryService.GetHistory();

        Theme.WindowHeader(FontAwesomeIcon.ChartPie, "Statistics & History",
            $"{history.TotalItemsObtained:N0} items recorded across {history.DailyStatistics.Count:N0} days",
            () =>
            {
                var right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
                ImGui.SetCursorPosX(right - 30f);
                if (Theme.IconButton("##RefreshStats", FontAwesomeIcon.Sync, "Recalculate statistics", Theme.Crystal))
                {
                    InvalidateAll();
                }
            });

        Theme.TabStrip("##StatsTabs", ref tab, Tabs);
        ImGui.Dummy(new Vector2(0, 6));

        using var content = Theme.Region("##StatsContent", ImGui.GetContentRegionAvail());
        if (!content) return;

        switch (tab)
        {
            case 0: DrawOverviewTab(); break;
            case 1: DrawHistoryTab(); break;
            case 2: DrawTrendsTab(); break;
            case 3: DrawAnalyticsTab(); break;
            case 4: DrawDutyTrackerTab(); break;
            case 5: DrawZoneFinderTab(); break;
            case 6: DrawBlacklistTab(); break;
            case 7: DrawExportTab(); break;
        }
    }

    // ==================================================================
    // OVERVIEW
    // ==================================================================

    private void DrawOverviewTab()
    {
        if (statsAsync.IsFirstLoad)
        {
            DrawLoading("Crunching your loot history...");
            return;
        }

        var cachedStats = statsAsync.Value;
        if (cachedStats == null) return;

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextMuted, "Range");
        ImGui.SameLine(0, 10);
        if (Theme.SegmentedControl("##DateRange", ref dateRangeOption, "Today", "This week", "This month", "All time"))
        {
            UpdateDateRange();
        }

        if (statsAsync.IsLoading)
        {
            ImGui.SameLine(0, 12);
            DrawWorkingBadge();
        }

        ImGui.Dummy(new Vector2(0, 10));

        // --- Headline metrics -----------------------------------------
        var cardWidth = (ImGui.GetContentRegionAvail().X - 30) / 4f;
        Theme.StatCard(FontAwesomeIcon.Gem, "Total items", cachedStats.TotalItems.ToString("N0"), Theme.Gold, cardWidth);
        ImGui.SameLine(0, 10);
        Theme.StatCard(FontAwesomeIcon.Fingerprint, "Unique items", cachedStats.TotalUnique.ToString("N0"), Theme.Crystal, cardWidth);
        ImGui.SameLine(0, 10);
        Theme.StatCard(FontAwesomeIcon.Star, "High quality", $"{cachedStats.TotalHQ:N0}", Theme.Warn, cardWidth,
            $"{cachedStats.HQPercentage:F1}% of everything in this range");
        ImGui.SameLine(0, 10);
        Theme.StatCard(FontAwesomeIcon.TachometerAlt, "Items / day", cachedStats.ItemsPerDay.ToString("F1"), Theme.Good, cardWidth);

        ImGui.Dummy(new Vector2(0, 14));

        // --- Rarity split + streaks -----------------------------------
        var half = (ImGui.GetContentRegionAvail().X - 12) / 2f;

        using (Theme.Card("##RarityPanel", new Vector2(half, 210), true, Theme.Crystal))
        {
            Theme.SectionHeader("Rarity split", FontAwesomeIcon.Gem);

            if (cachedStats.ByRarity.Any())
            {
                var slices = cachedStats.ByRarity
                    .OrderByDescending(kv => kv.Key)
                    .Select(kv => (Theme.RarityName(kv.Key), (double)kv.Value.Count, Theme.RarityColor(kv.Key)))
                    .ToList();

                Charts.Donut(slices, 55f, "items", cachedStats.TotalItems.ToString("N0"));

                ImGui.SameLine(0, 16);
                ImGui.BeginGroup();
                foreach (var kv in cachedStats.ByRarity.OrderByDescending(k => k.Key))
                {
                    Theme.RarityGem(kv.Key, 9f);
                    ImGui.SameLine(0, 6);
                    ImGui.TextColored(Theme.RarityColor(kv.Key), Theme.RarityName(kv.Key));
                    ImGui.SameLine(112);
                    ImGui.TextColored(Theme.Text, $"{kv.Value.Count:N0}");
                    ImGui.SameLine(168);
                    ImGui.TextColored(Theme.TextFaint, $"{kv.Value.Percentage:F1}%");
                }
                ImGui.EndGroup();
            }
            else
            {
                ImGui.TextColored(Theme.TextFaint, "No items in this range");
            }

        }

        ImGui.SameLine(0, 12);

        using (Theme.Card("##StreakPanel", new Vector2(half, 210), true, Theme.Gold))
        {
            Theme.SectionHeader("Play streaks", FontAwesomeIcon.Fire);

            MetricLine(FontAwesomeIcon.Fire, "Current streak", Days(cachedStats.CurrentStreak), Theme.Gold);
            MetricLine(FontAwesomeIcon.Trophy, "Longest streak", Days(cachedStats.LongestStreak), Theme.Warn);
            MetricLine(FontAwesomeIcon.CalendarCheck, "Days played", $"{cachedStats.DaysPlayed}", Theme.Crystal);

            if (cachedStats.LongestStreak > 0)
            {
                ImGui.Dummy(new Vector2(0, 8));
                Theme.Meter(cachedStats.CurrentStreak / (float)Math.Max(cachedStats.LongestStreak, 1),
                    ImGui.GetContentRegionAvail().X, 8f, Theme.Gold);
            }

            if (cachedStats.FirstItemDate.HasValue && cachedStats.LastItemDate.HasValue)
            {
                ImGui.Dummy(new Vector2(0, 10));
                Theme.HairLine();
                ImGui.Dummy(new Vector2(0, 6));
                ImGui.TextColored(Theme.TextFaint, $"First item   {cachedStats.FirstItemDate.Value:yyyy-MM-dd}");
                ImGui.TextColored(Theme.TextFaint, $"Latest item  {cachedStats.LastItemDate.Value:yyyy-MM-dd HH:mm}");
            }

        }

        ImGui.Dummy(new Vector2(0, 12));

        // --- Top zones -------------------------------------------------
        Theme.SectionHeader("Top zones", FontAwesomeIcon.MapMarkedAlt);

        var topZones = cachedStats.ByZone.OrderByDescending(z => z.Value.TotalItems).Take(8).ToList();
        if (topZones.Count > 0)
        {
            var maxZone = topZones.Max(z => z.Value.TotalItems);
            foreach (var zone in topZones)
            {
                Charts.RankedBar(zone.Value.ZoneName, zone.Value.TotalItems, maxZone, Theme.Crystal, 220f,
                    $"{zone.Value.TotalItems:N0}");
            }
        }
        else
        {
            ImGui.TextColored(Theme.TextFaint, "No zone data available");
        }

        ImGui.Dummy(new Vector2(0, 12));

        // --- Most common items ----------------------------------------
        Theme.SectionHeader("Most common items", FontAwesomeIcon.ListOl);

        if (cachedStats.MostCommonItems.Any())
        {
            using var table = ImRaii.Table("CommonItems", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV);
            if (table)
            {
                ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Count", ImGuiTableColumnFlags.WidthFixed, 80);
                ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthFixed, 60);
                ImGui.TableSetupColumn("Last obtained", ImGuiTableColumnFlags.WidthFixed, 150);
                ImGui.TableHeadersRow();

                foreach (var item in cachedStats.MostCommonItems.Take(10))
                {
                    ImGui.TableNextRow(ImGuiTableRowFlags.None, 32f);
                    ImGui.TableNextColumn();

                    DrawItemIcon(item.IconId);
                    ImGui.SameLine(0, 8);
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(Theme.RarityColor(item.Rarity), item.ItemName);

                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(Theme.Text, item.Count.ToString("N0"));

                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    if (item.HQCount > 0)
                        ImGui.TextColored(Theme.Warn, item.HQCount.ToString());
                    else
                        ImGui.TextColored(Theme.TextFaint, "-");

                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(Theme.TextMuted, item.LastObtained.ToString("MM/dd HH:mm"));
                }
            }
        }
        else
        {
            ImGui.TextColored(Theme.TextFaint, "No items to display");
        }

        ImGui.Dummy(new Vector2(0, 10));
    }

    /// <summary>"1 day" rather than "1 days".</summary>
    private static string Days(int count) => count == 1 ? "1 day" : $"{count} days";

    private static void MetricLine(FontAwesomeIcon icon, string label, string value, Vector4 accent)
    {
        Theme.Icon(icon, accent);
        ImGui.SameLine(0, 9);
        ImGui.TextColored(Theme.TextMuted, label);
        ImGui.SameLine(150);
        ImGui.TextColored(Theme.Text, value);
    }

    // ==================================================================
    // HISTORY
    // ==================================================================

    private void DrawHistoryTab()
    {
        DrawHistoryFilters();

        RequestFilteredHistory();

        if (historyAsync.IsFirstLoad)
        {
            DrawLoading("Searching your history...");
            return;
        }

        var filteredItems = historyAsync.Value ?? new List<LootItem>();
        var totalPages = Math.Max((int)Math.Ceiling(filteredItems.Count / (double)ItemsPerPage), 1);
        historyPage = Math.Clamp(historyPage, 0, totalPages - 1);

        // --- Result bar + pagination ----------------------------------
        ImGui.AlignTextToFramePadding();
        Theme.Badge($"{filteredItems.Count:N0} results", Theme.Crystal);

        if (historyAsync.IsLoading)
        {
            ImGui.SameLine(0, 10);
            DrawWorkingBadge();
        }

        if (totalPages > 1)
        {
            const float navWidth = 214f;
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(ImGui.GetContentRegionAvail().X - navWidth, 0));

            if (Theme.IconButton("##FirstPage", FontAwesomeIcon.AngleDoubleLeft, "First page", Theme.Crystal, false, 26f))
                historyPage = 0;
            ImGui.SameLine(0, 4);
            if (Theme.IconButton("##PrevPage", FontAwesomeIcon.AngleLeft, "Previous page", Theme.Crystal, false, 26f) && historyPage > 0)
                historyPage--;

            ImGui.SameLine(0, 8);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.TextMuted, $"{historyPage + 1} / {totalPages}");

            ImGui.SameLine(0, 8);
            if (Theme.IconButton("##NextPage", FontAwesomeIcon.AngleRight, "Next page", Theme.Crystal, false, 26f) && historyPage < totalPages - 1)
                historyPage++;
            ImGui.SameLine(0, 4);
            if (Theme.IconButton("##LastPage", FontAwesomeIcon.AngleDoubleRight, "Last page", Theme.Crystal, false, 26f))
                historyPage = totalPages - 1;
        }

        ImGui.Dummy(new Vector2(0, 6));

        if (filteredItems.Count == 0)
        {
            Theme.EmptyState(FontAwesomeIcon.Search, "Nothing matches these filters",
                "Widen your search or clear the filters above.");
            return;
        }

        var tableHeight = ImGui.GetContentRegionAvail().Y - 4;
        using var table = ImRaii.Table("HistoryTable", 6,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable,
            new Vector2(0, tableHeight));
        if (!table) return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 50);
        ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthFixed, 130);
        ImGui.TableSetupColumn("Zone", ImGuiTableColumnFlags.WidthFixed, 190);
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthFixed, 100);
        ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.WidthFixed, 150);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        foreach (var item in filteredItems.Skip(historyPage * ItemsPerPage).Take(ItemsPerPage))
        {
            ImGui.TableNextRow(ImGuiTableRowFlags.None, 32f);
            ImGui.TableNextColumn();

            DrawItemIcon(item.IconId);
            ImGui.SameLine(0, 8);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.RarityColor(item.Rarity), item.ItemName);
            if (item.IsHQ)
            {
                ImGui.SameLine(0, 6);
                Theme.Badge("HQ", Theme.Warn);
            }

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(item.Quantity > 1 ? Theme.CrystalBright : Theme.TextMuted, $"x{item.Quantity}");

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(item.IsOwnLoot ? Theme.Good : Theme.Text, item.PlayerName);

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.TextMuted, item.ZoneName);

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.TextFaint, item.Source.ToString());

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.TextMuted, item.Timestamp.ToString("MM/dd HH:mm:ss"));
        }
    }

    private void DrawHistoryFilters()
    {
        ImGui.AlignTextToFramePadding();
        Theme.Icon(FontAwesomeIcon.Search, Theme.TextFaint);
        ImGui.SameLine(0, 8);
        ImGui.SetNextItemWidth(230);
        if (ImGui.InputTextWithHint("##search", "Search item names", ref searchQuery, 100))
            historyPage = 0;

        ImGui.SameLine(0, 8);
        ImGui.SetNextItemWidth(170);
        if (ImGui.BeginCombo("##rarityFilter", filterRarity == 999 ? "All rarities" : Theme.RarityName(filterRarity)))
        {
            if (ImGui.Selectable("All rarities", filterRarity == 999)) { filterRarity = 999; historyPage = 0; }
            ImGui.Separator();
            foreach (uint r in (uint[])[1, 2, 3, 4, 7])
            {
                Theme.RarityGem(r, 9f);
                ImGui.SameLine(0, 6);
                if (ImGui.Selectable(Theme.RarityName(r), filterRarity == r)) { filterRarity = r; historyPage = 0; }
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine(0, 8);
        ImGui.SetNextItemWidth(180);
        if (ImGui.InputTextWithHint("##zoneFilter", "Filter by zone", ref filterZone, 100))
            historyPage = 0;

        ImGui.Dummy(new Vector2(0, 4));

        var hq = filterHQOnly;
        if (Theme.Toggle("##HQOnly", ref hq)) { filterHQOnly = hq; historyPage = 0; }
        ImGui.SameLine(0, 8);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(filterHQOnly ? Theme.Warn : Theme.TextMuted, "HQ only");

        ImGui.SameLine(0, 22);
        var own = filterOwnLootOnly;
        if (Theme.Toggle("##OwnOnly", ref own)) { filterOwnLootOnly = own; historyPage = 0; }
        ImGui.SameLine(0, 8);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(filterOwnLootOnly ? Theme.Good : Theme.TextMuted, "My loot only");

        ImGui.SameLine(0, 22);
        if (Theme.GhostButton("Reset filters", new Vector2(120, 0), Theme.Bad))
        {
            searchQuery = "";
            filterZone = "";
            filterRarity = 999;
            filterHQOnly = false;
            filterOwnLootOnly = false;
            historyPage = 0;
        }

        Theme.Rule(6f);
    }

    // ==================================================================
    // TRENDS
    // ==================================================================

    private void DrawTrendsTab()
    {
        if (statsAsync.IsFirstLoad)
        {
            DrawLoading("Crunching your loot history...");
            return;
        }

        var cachedStats = statsAsync.Value;
        if (cachedStats == null) return;

        Theme.SectionHeader("Daily activity", FontAwesomeIcon.CalendarAlt);
        ImGui.TextColored(Theme.TextFaint, "Items obtained over the last two weeks");
        ImGui.Dummy(new Vector2(0, 8));

        var recentDays = cachedStats.DailyItems
            .OrderByDescending(d => d.Key)
            .Take(14)
            .Reverse()
            .Select(d => new Charts.Bar(d.Key.ToString("MM/dd"), d.Value, $"{d.Key:dddd, dd MMM}\n{d.Value:N0} items"))
            .ToList();

        Charts.Columns("##DailyChart", recentDays, 150f, Theme.Gold);

        ImGui.Dummy(new Vector2(0, 16));
        Theme.SectionHeader("Hourly pattern", FontAwesomeIcon.Clock);
        ImGui.TextColored(Theme.TextFaint, "When during the day you tend to pick things up");
        ImGui.Dummy(new Vector2(0, 8));

        var hourly = Enumerable.Range(0, 24)
            .Select(h => new Charts.Bar(
                h % 3 == 0 ? $"{h:D2}" : " ",
                cachedStats.HourlyItems.TryGetValue(h, out var v) ? v : 0,
                $"{h:D2}:00 - {h:D2}:59\n{(cachedStats.HourlyItems.TryGetValue(h, out var c) ? c : 0):N0} items"))
            .ToList();

        Charts.Columns("##HourlyChart", hourly, 130f, Theme.Crystal);

        ImGui.Dummy(new Vector2(0, 16));
        Theme.SectionHeader("Rarest finds", FontAwesomeIcon.Award);
        ImGui.Dummy(new Vector2(0, 4));

        if (cachedStats.RarestItems.Any())
        {
            using var table = ImRaii.Table("RarestItems", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV);
            if (table)
            {
                ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Rarity", ImGuiTableColumnFlags.WidthFixed, 110);
                ImGui.TableSetupColumn("Zone", ImGuiTableColumnFlags.WidthFixed, 190);
                ImGui.TableSetupColumn("Obtained", ImGuiTableColumnFlags.WidthFixed, 140);
                ImGui.TableHeadersRow();

                foreach (var item in cachedStats.RarestItems.Take(15))
                {
                    ImGui.TableNextRow(ImGuiTableRowFlags.None, 32f);
                    ImGui.TableNextColumn();

                    DrawItemIcon(item.IconId);
                    ImGui.SameLine(0, 8);
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(Theme.RarityColor(item.Rarity), item.ItemName);
                    if (item.IsHQ)
                    {
                        ImGui.SameLine(0, 6);
                        Theme.Badge("HQ", Theme.Warn);
                    }

                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    Theme.Badge(Theme.RarityName(item.Rarity), Theme.RarityColor(item.Rarity));

                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(Theme.TextMuted, item.ZoneName);

                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(Theme.TextFaint, item.Timestamp.ToString("MM/dd HH:mm"));
                }
            }
        }
        else
        {
            ImGui.TextColored(Theme.TextFaint, "Nothing notable yet");
        }

        ImGui.Dummy(new Vector2(0, 10));
    }

    // ==================================================================
    // ANALYTICS
    // ==================================================================

    private void DrawAnalyticsTab()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextMuted, "Compare the last");
        ImGui.SameLine(0, 10);
        Theme.SliderInt("##comparisonDays", ref comparisonDays, 1, 30, 170f);
        ImGui.SameLine(0, 10);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextMuted, "days against the period before it");

        if (currentPeriodAsync.IsLoading || previousPeriodAsync.IsLoading)
        {
            ImGui.SameLine(0, 12);
            DrawWorkingBadge();
        }

        ImGui.Dummy(new Vector2(0, 10));

        RequestComparisonStatistics();

        if (currentPeriodAsync.IsFirstLoad || previousPeriodAsync.IsFirstLoad)
        {
            DrawLoading("Comparing the two periods...");
            return;
        }

        var current = currentPeriodAsync.Value;
        var previous = previousPeriodAsync.Value;
        if (current == null || previous == null) return;

        Theme.SectionHeader("Items obtained", FontAwesomeIcon.ExchangeAlt);
        ImGui.Dummy(new Vector2(0, 4));

        ComparisonRow("Total items", current.TotalItems, previous.TotalItems);
        ComparisonRow("Unique items", current.TotalUnique, previous.TotalUnique);
        ComparisonRow("HQ items", current.TotalHQ, previous.TotalHQ);
        ComparisonRow("Items per day", (int)current.ItemsPerDay, (int)previous.ItemsPerDay);

        ImGui.Dummy(new Vector2(0, 14));
        Theme.SectionHeader("Rarity distribution", FontAwesomeIcon.Gem);
        ImGui.Dummy(new Vector2(0, 4));

        using (var table = ImRaii.Table("RarityComparison", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            if (table)
            {
                ImGui.TableSetupColumn("Rarity", ImGuiTableColumnFlags.WidthFixed, 140);
                ImGui.TableSetupColumn("Current", ImGuiTableColumnFlags.WidthFixed, 100);
                ImGui.TableSetupColumn("Previous", ImGuiTableColumnFlags.WidthFixed, 100);
                ImGui.TableSetupColumn("Change", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableHeadersRow();

                var allRarities = current.ByRarity.Keys.Union(previous.ByRarity.Keys).OrderByDescending(r => r);
                foreach (var rarity in allRarities)
                {
                    ImGui.TableNextRow(ImGuiTableRowFlags.None, 28f);
                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    Theme.RarityGem(rarity, 9f);
                    ImGui.SameLine(0, 6);
                    ImGui.TextColored(Theme.RarityColor(rarity), Theme.RarityName(rarity));

                    var currentCount = current.ByRarity.TryGetValue(rarity, out var cv) ? cv.Count : 0;
                    var previousCount = previous.ByRarity.TryGetValue(rarity, out var pv) ? pv.Count : 0;

                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(Theme.Text, currentCount.ToString("N0"));

                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(Theme.TextMuted, previousCount.ToString("N0"));

                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    DrawDelta(currentCount, previousCount);
                }
            }
        }

        ImGui.Dummy(new Vector2(0, 14));
        Theme.SectionHeader("Most active zones", FontAwesomeIcon.MapMarkedAlt);
        ImGui.Dummy(new Vector2(0, 4));

        var half = (ImGui.GetContentRegionAvail().X - 12) / 2f;

        using (Theme.Card("##ZonesNow", new Vector2(half, 170), true, Theme.Good))
        {
            ImGui.TextColored(Theme.Good, $"LAST {comparisonDays} DAYS");
            ImGui.Dummy(new Vector2(0, 6));
            var top = current.ByZone.OrderByDescending(z => z.Value.TotalItems).Take(5).ToList();
            if (top.Count > 0)
            {
                var max = top.Max(z => z.Value.TotalItems);
                foreach (var zone in top)
                    Charts.RankedBar(zone.Value.ZoneName, zone.Value.TotalItems, max, Theme.Good, 150f);
            }
            else ImGui.TextColored(Theme.TextFaint, "No activity");
        }

        ImGui.SameLine(0, 12);

        using (Theme.Card("##ZonesBefore", new Vector2(half, 170), true, Theme.TextFaint))
        {
            ImGui.TextColored(Theme.TextMuted, $"PREVIOUS {comparisonDays} DAYS");
            ImGui.Dummy(new Vector2(0, 6));
            var top = previous.ByZone.OrderByDescending(z => z.Value.TotalItems).Take(5).ToList();
            if (top.Count > 0)
            {
                var max = top.Max(z => z.Value.TotalItems);
                foreach (var zone in top)
                    Charts.RankedBar(zone.Value.ZoneName, zone.Value.TotalItems, max, Theme.TextMuted, 150f);
            }
            else ImGui.TextColored(Theme.TextFaint, "No activity");
        }

        ImGui.Dummy(new Vector2(0, 14));
        Theme.SectionHeader("Peak activity", FontAwesomeIcon.Clock);
        ImGui.Dummy(new Vector2(0, 4));

        if (current.HourlyItems.Any())
        {
            var peakHour = current.HourlyItems.OrderByDescending(h => h.Value).First();
            var avgPerHour = current.HourlyItems.Values.Average();

            MetricLine(FontAwesomeIcon.ArrowUp, "Peak hour", $"{peakHour.Key:D2}:00  ·  {peakHour.Value:N0} items", Theme.Gold);
            MetricLine(FontAwesomeIcon.Equals, "Average per hour", $"{avgPerHour:F1} items", Theme.Crystal);

            ImGui.Dummy(new Vector2(0, 10));

            var blocks = Enumerable.Range(0, 6).Select(block =>
            {
                var startHour = block * 4;
                var total = 0;
                for (var h = startHour; h <= startHour + 3; h++)
                    total += current.HourlyItems.TryGetValue(h, out var v) ? v : 0;
                return ($"{startHour:D2}-{startHour + 3:D2}", (double)total);
            }).ToList();

            Charts.HeatStrip(blocks, 62f, Theme.Gold);
        }
        else
        {
            ImGui.TextColored(Theme.TextFaint, "Not enough data in this window");
        }

        ImGui.Dummy(new Vector2(0, 10));
    }

    private static void ComparisonRow(string label, int currentValue, int previousValue)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextMuted, label);
        ImGui.SameLine(190);
        ImGui.TextColored(Theme.Text, currentValue.ToString("N0"));
        ImGui.SameLine(290);
        DrawDelta(currentValue, previousValue);
    }

    private static void DrawDelta(int currentValue, int previousValue)
    {
        if (previousValue <= 0)
        {
            ImGui.TextColored(Theme.TextFaint, currentValue > 0 ? "new" : "no data");
            return;
        }

        var change = currentValue - previousValue;
        var percent = change * 100.0 / previousValue;

        if (change > 0)
            Theme.IconText(FontAwesomeIcon.CaretUp, $"+{change:N0}  ({percent:F1}%)", Theme.Good);
        else if (change < 0)
            Theme.IconText(FontAwesomeIcon.CaretDown, $"{change:N0}  ({percent:F1}%)", Theme.Bad);
        else
            Theme.IconText(FontAwesomeIcon.Minus, "no change", Theme.TextFaint);
    }

    // ==================================================================
    // DUTY TRACKER
    // ==================================================================

    private void DrawDutyTrackerTab()
    {
        RequestDutyStatistics();

        if (dutyStatsAsync.IsFirstLoad || recentRunsAsync.IsFirstLoad)
        {
            DrawLoading("Reviewing your duty runs...");
            return;
        }

        var dutyStats = dutyStatsAsync.Value;
        var recentRuns = recentRunsAsync.Value;
        if (dutyStats == null || recentRuns == null) return;

        if (!dutyStats.Any())
        {
            Theme.EmptyState(FontAwesomeIcon.Flag, "No duties recorded yet",
                "Run a dungeon, trial or raid and LootView will start keeping score.");
            return;
        }

        // --- Summary cards ---------------------------------------------
        var cardWidth = (ImGui.GetContentRegionAvail().X - 30) / 4f;
        Theme.StatCard(FontAwesomeIcon.Flag, "Duties tracked", dutyStats.Count.ToString("N0"), Theme.Crystal, cardWidth);
        ImGui.SameLine(0, 10);
        Theme.StatCard(FontAwesomeIcon.Redo, "Total runs", dutyStats.Sum(d => d.Value.TotalAttempts).ToString("N0"), Theme.Gold, cardWidth);
        ImGui.SameLine(0, 10);
        Theme.StatCard(FontAwesomeIcon.CheckCircle, "Completions", dutyStats.Sum(d => d.Value.Completions).ToString("N0"), Theme.Good, cardWidth);
        ImGui.SameLine(0, 10);
        Theme.StatCard(FontAwesomeIcon.Gem, "Items looted", dutyStats.Sum(d => d.Value.TotalItemsObtained).ToString("N0"), Theme.Warn, cardWidth);

        ImGui.Dummy(new Vector2(0, 12));

        // --- View + type filter -----------------------------------------
        Theme.SegmentedControl("##DutyView", ref dutyView, "Leaderboard", "Recent runs", "Best runs");
        ImGui.SameLine(0, 14);
        Theme.SegmentedControl("##DutyType", ref dutyTypeFilter, DutyTypes);

        ImGui.Dummy(new Vector2(0, 8));

        switch (dutyView)
        {
            case 0: DrawDutyLeaderboard(dutyStats); break;
            case 1: DrawRecentRuns(recentRuns); break;
            case 2: DrawBestRuns(dutyStats); break;
        }
    }

    private void DrawDutyLeaderboard(Dictionary<uint, DutyStatistics> dutyStats)
    {
        var selectedType = DutyTypes[Math.Clamp(dutyTypeFilter, 0, DutyTypes.Length - 1)];
        var filtered = selectedType == "All"
            ? dutyStats
            : dutyStats.Where(d => d.Value.ContentType == selectedType).ToDictionary(k => k.Key, v => v.Value);

        if (filtered.Count == 0)
        {
            Theme.EmptyState(FontAwesomeIcon.Filter, $"No {selectedType.ToLower()} runs recorded",
                "Pick a different content type above.");
            return;
        }

        ImGui.TextColored(Theme.TextFaint, "Click a duty to inspect its best runs");
        ImGui.Dummy(new Vector2(0, 4));

        var tableHeight = ImGui.GetContentRegionAvail().Y - 6;
        using var table = ImRaii.Table("DutyStatsTable", 8,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable,
            new Vector2(0, tableHeight));
        if (!table) return;

        ImGui.TableSetupColumn("Duty", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableSetupColumn("Runs", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("Items", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("Per run", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("Avg time", ImGuiTableColumnFlags.WidthFixed, 85);
        ImGui.TableSetupColumn("Best time", ImGuiTableColumnFlags.WidthFixed, 85);
        ImGui.TableSetupColumn("Last run", ImGuiTableColumnFlags.WidthFixed, 140);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        foreach (var stat in dutyStats.Where(s => selectedType == "All" || s.Value.ContentType == selectedType)
                     .OrderByDescending(s => s.Value.TotalAttempts))
        {
            ImGui.TableNextRow(ImGuiTableRowFlags.None, 30f);
            ImGui.TableNextColumn();

            var isSelected = selectedDutyId == stat.Key;
            ImGui.AlignTextToFramePadding();
            if (ImGui.Selectable($"##duty_{stat.Key}", isSelected, ImGuiSelectableFlags.SpanAllColumns))
            {
                selectedDutyId = stat.Key;
                dutyView = 2;
            }
            ImGui.SameLine(0, 0);
            ImGui.TextColored(isSelected ? Theme.GoldBright : Theme.Text, stat.Value.DutyName);

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.TextMuted, stat.Value.ContentType);

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Text, stat.Value.TotalAttempts.ToString("N0"));

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Good, stat.Value.TotalItemsObtained.ToString("N0"));

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Text, $"{stat.Value.AverageItemsPerRun:F1}");

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.TextMuted, $"{stat.Value.AverageTimeMinutes:F1}m");

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            if (stat.Value.FastestTimeMinutes < double.MaxValue)
                ImGui.TextColored(Theme.Crystal, $"{stat.Value.FastestTimeMinutes:F1}m");
            else
                ImGui.TextColored(Theme.TextFaint, "-");

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.TextFaint, stat.Value.LastAttempt.ToString("MM/dd HH:mm"));
        }
    }

    private void DrawRecentRuns(List<DutyRun> recentRuns)
    {
        var selectedType = DutyTypes[Math.Clamp(dutyTypeFilter, 0, DutyTypes.Length - 1)];
        var runs = selectedType == "All" ? recentRuns : recentRuns.Where(r => r.ContentType == selectedType).ToList();

        if (runs.Count == 0)
        {
            Theme.EmptyState(FontAwesomeIcon.History, "No recent runs", "Nothing matching this content type yet.");
            return;
        }

        var tableHeight = ImGui.GetContentRegionAvail().Y - 6;
        using var table = ImRaii.Table("RecentRunsTable", 6,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable,
            new Vector2(0, tableHeight));
        if (!table) return;

        ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.WidthFixed, 150);
        ImGui.TableSetupColumn("Duty", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableSetupColumn("Duration", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("Items", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        foreach (var run in runs)
        {
            ImGui.TableNextRow(ImGuiTableRowFlags.None, 30f);
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.TextMuted, run.StartedAt.ToString("MM/dd HH:mm:ss"));

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Text, run.DutyName);

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.TextMuted, run.ContentType);

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            if (run.CompletedAt.HasValue)
                ImGui.TextColored(Theme.Crystal, $"{run.DurationMinutes:F1}m");
            else
                ImGui.TextColored(Theme.TextFaint, "abandoned");

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Text, run.ItemsObtained.ToString("N0"));

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            if (run.HQItemsObtained > 0)
                ImGui.TextColored(Theme.Warn, run.HQItemsObtained.ToString());
            else
                ImGui.TextColored(Theme.TextFaint, "-");
        }
    }

    private void DrawBestRuns(Dictionary<uint, DutyStatistics> dutyStats)
    {
        if (selectedDutyId == 0 || !dutyStats.TryGetValue(selectedDutyId, out var selectedStats))
        {
            Theme.EmptyState(FontAwesomeIcon.HandPointUp, "Pick a duty first",
                "Choose one from the leaderboard to see its records.");
            return;
        }

        Theme.SectionHeader(selectedStats.DutyName, FontAwesomeIcon.Trophy);

        // Previously re-queried on every frame; now computed once per duty, off-thread.
        var runsKey = (selectedDutyId, itemCount);
        var dutyId = selectedDutyId;
        bestRunsAsync.Ensure(runsKey, () => plugin.HistoryService.GetBestRuns(dutyId, 20));
        fastestRunsAsync.Ensure(runsKey, () => plugin.HistoryService.GetFastestRuns(dutyId, 20));

        if (bestRunsAsync.IsFirstLoad || fastestRunsAsync.IsFirstLoad)
        {
            DrawLoading("Ranking your runs...");
            return;
        }

        var bestRuns = bestRunsAsync.Value ?? new List<DutyRun>();
        var fastestRuns = fastestRunsAsync.Value ?? new List<DutyRun>();

        var half = (ImGui.GetContentRegionAvail().X - 12) / 2f;
        var height = ImGui.GetContentRegionAvail().Y - 10;

        using (Theme.Card("##MostItems", new Vector2(half, height), true, Theme.Gold))
        {
            Theme.IconText(FontAwesomeIcon.Gem, "MOST ITEMS", Theme.Gold);
            ImGui.Dummy(new Vector2(0, 6));
            DrawRunRanking(bestRuns, run => run.ItemsObtained.ToString("N0"), run => $"{run.DurationMinutes:F1}m");
        }

        ImGui.SameLine(0, 12);

        using (Theme.Card("##Fastest", new Vector2(half, height), true, Theme.Crystal))
        {
            Theme.IconText(FontAwesomeIcon.Bolt, "FASTEST CLEARS", Theme.Crystal);
            ImGui.Dummy(new Vector2(0, 6));
            DrawRunRanking(fastestRuns, run => $"{run.DurationMinutes:F1}m", run => $"{run.ItemsObtained} items");
        }
    }

    private static void DrawRunRanking(List<DutyRun> runs, Func<DutyRun, string> primary, Func<DutyRun, string> secondary)
    {
        if (runs.Count == 0)
        {
            ImGui.TextColored(Theme.TextFaint, "No completed runs yet");
            return;
        }

        var rank = 1;
        foreach (var run in runs)
        {
            var medal = rank switch
            {
                1 => new Vector4(1.0f, 0.84f, 0.0f, 1f),
                2 => new Vector4(0.78f, 0.80f, 0.84f, 1f),
                3 => new Vector4(0.80f, 0.53f, 0.27f, 1f),
                _ => Theme.TextFaint,
            };

            ImGui.TextColored(medal, $"#{rank}");
            ImGui.SameLine(40);
            ImGui.TextColored(Theme.Text, primary(run));
            ImGui.SameLine(110);
            ImGui.TextColored(Theme.TextMuted, secondary(run));
            ImGui.SameLine(200);
            ImGui.TextColored(Theme.TextFaint, run.StartedAt.ToString("MM/dd HH:mm"));

            rank++;
        }
    }

    // ==================================================================
    // ZONE FINDER
    // ==================================================================

    private void DrawZoneFinderTab()
    {
        Theme.Callout(FontAwesomeIcon.InfoCircle, "Instanced battle content",
            "Dungeons, trials and raids (normal, savage and ultimate) are supported. Overworld zones and some special content have no drop table on record.",
            Theme.Crystal);

        ImGui.Dummy(new Vector2(0, 10));

        ImGui.AlignTextToFramePadding();
        Theme.Icon(FontAwesomeIcon.Search, Theme.TextFaint);
        ImGui.SameLine(0, 8);
        ImGui.SetNextItemWidth(320);
        if (ImGui.InputTextWithHint("##ZoneSearch", "Sastasha, Titan, Alexander...", ref zoneSearchQuery, 100,
                ImGuiInputTextFlags.EnterReturnsTrue))
        {
            PerformZoneSearch();
        }

        ImGui.SameLine(0, 10);
        if (Theme.PrimaryButton("Search", new Vector2(110, 0)))
        {
            PerformZoneSearch();
        }

        ImGui.Dummy(new Vector2(0, 8));

        if (zoneSearchPerformed && zoneSearchResults.Count == 0)
        {
            Theme.EmptyState(FontAwesomeIcon.MapSigns, "No zones found",
                "Try a shorter or differently spelled name.");
            return;
        }

        if (zoneSearchResults.Count == 0)
        {
            Theme.EmptyState(FontAwesomeIcon.Compass, "Look up any duty's loot table",
                "Type a dungeon, trial or raid name and press Enter.");
            return;
        }

        ImGui.AlignTextToFramePadding();
        Theme.Badge($"{zoneSearchResults.Count} matches", Theme.Crystal);
        ImGui.Dummy(new Vector2(0, 6));

        var tableHeight = ImGui.GetContentRegionAvail().Y - 6;
        using var table = ImRaii.Table("ZoneSearchResultsTable", 4,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV,
            new Vector2(0, tableHeight));
        if (!table) return;

        ImGui.TableSetupColumn("Zone", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 140);
        ImGui.TableSetupColumn("iLvl", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 130);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        foreach (var result in zoneSearchResults)
        {
            ImGui.TableNextRow(ImGuiTableRowFlags.None, 32f);

            ImGui.TableSetColumnIndex(0);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Text, result.Name);

            ImGui.TableSetColumnIndex(1);
            ImGui.AlignTextToFramePadding();
            Theme.Badge(result.ContentType, Theme.Crystal);

            ImGui.TableSetColumnIndex(2);
            ImGui.AlignTextToFramePadding();
            if (result.ItemLevel > 0)
                ImGui.TextColored(Theme.Gold, $"i{result.ItemLevel}");
            else
                ImGui.TextColored(Theme.TextFaint, "-");

            ImGui.TableSetColumnIndex(3);
            if (Theme.GhostButton($"View loot##{result.ContentFinderConditionId}", new Vector2(120, 0)))
            {
                plugin.LootTableWindow.IsOpen = true;
                LoadZoneLootTable(result.ContentFinderConditionId);
            }
        }
    }

    // ==================================================================
    // BLACKLIST
    // ==================================================================

    private void DrawBlacklistTab()
    {
        var config = plugin.ConfigService.Configuration;
        var blacklistedIds = config.BlacklistedItemIds ?? new List<uint>();

        if (blacklistedIds.Count == 0)
        {
            Theme.EmptyState(FontAwesomeIcon.EyeSlash, "Nothing is blacklisted",
                "Right-click an item in the loot list to stop tracking it.");
            return;
        }

        ImGui.AlignTextToFramePadding();
        Theme.Badge($"{blacklistedIds.Count} hidden items", Theme.Bad);

        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(ImGui.GetContentRegionAvail().X - 160, 0));
        if (Theme.DangerButton("Clear blacklist", new Vector2(160, 0)))
        {
            ImGui.OpenPopup("ConfirmClearBlacklist");
        }

        DrawConfirmPopup("ConfirmClearBlacklist",
            "Remove every item from the blacklist?",
            "They will start appearing in the loot list again.",
            "Yes, clear it",
            () =>
            {
                config.BlacklistedItemIds?.Clear();
                plugin.ConfigService.Save();
            });

        ImGui.Dummy(new Vector2(0, 8));

        var tableHeight = ImGui.GetContentRegionAvail().Y - 6;
        using var table = ImRaii.Table("BlacklistTable", 4,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV,
            new Vector2(0, tableHeight));
        if (!table) return;

        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 40);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        var itemsToRemove = new List<uint>();

        foreach (var itemId in blacklistedIds.ToList())
        {
            var itemRow = Plugin.DataManager.GameData?.GetExcelSheet<Lumina.Excel.Sheets.Item>()?.GetRow(itemId);

            ImGui.TableNextRow(ImGuiTableRowFlags.None, 34f);

            ImGui.TableSetColumnIndex(0);
            if (itemRow.HasValue && itemRow.Value.Icon > 0)
            {
                DrawItemIcon(itemRow.Value.Icon, 28f);
            }
            else
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(Theme.TextFaint, "?");
            }

            ImGui.TableSetColumnIndex(1);
            ImGui.AlignTextToFramePadding();
            if (itemRow.HasValue)
            {
                var item = itemRow.Value;
                Theme.RarityGem(item.Rarity, 9f);
                ImGui.SameLine(0, 7);
                ImGui.TextColored(Theme.RarityColor(item.Rarity), item.Name.ExtractText());
            }
            else
            {
                ImGui.TextColored(Theme.TextMuted, "Unknown item");
            }

            ImGui.TableSetColumnIndex(2);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.TextFaint, itemId.ToString());

            ImGui.TableSetColumnIndex(3);
            if (Theme.GhostButton($"Restore##Remove_{itemId}", new Vector2(100, 0), Theme.Good))
            {
                itemsToRemove.Add(itemId);
            }
        }

        foreach (var itemId in itemsToRemove)
        {
            config.BlacklistedItemIds?.Remove(itemId);
        }

        if (itemsToRemove.Count > 0)
        {
            plugin.ConfigService.Save();
        }
    }

    // ==================================================================
    // EXPORT
    // ==================================================================

    private void DrawExportTab()
    {
        var history = plugin.HistoryService.GetHistory();

        Theme.SectionHeader("Your collection", FontAwesomeIcon.Database);

        var cardWidth = (ImGui.GetContentRegionAvail().X - 20) / 3f;
        Theme.StatCard(FontAwesomeIcon.Gem, "Items in history", history.TotalItemsObtained.ToString("N0"), Theme.Gold, cardWidth);
        ImGui.SameLine(0, 10);
        Theme.StatCard(FontAwesomeIcon.CalendarAlt, "Days tracked", history.DailyStatistics.Count.ToString("N0"), Theme.Crystal, cardWidth);
        ImGui.SameLine(0, 10);
        Theme.StatCard(FontAwesomeIcon.Clock, "Last updated", history.LastUpdated.ToString("MM/dd HH:mm"), Theme.Good, cardWidth);

        ImGui.Dummy(new Vector2(0, 16));
        Theme.SectionHeader("Export", FontAwesomeIcon.FileExport);
        ImGui.TextColored(Theme.TextMuted, "Files are written to your Documents folder.");
        ImGui.Dummy(new Vector2(0, 8));

        if (Theme.PrimaryButton("Export as JSON", new Vector2(190, 34)))
        {
            var path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                $"LootView_History_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            plugin.HistoryService.ExportToJson(path);
            Plugin.ChatGui.Print($"[LootView] History exported to: {path}");
        }
        ImGui.SameLine(0, 10);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextFaint, "Complete history with every field");

        ImGui.Dummy(new Vector2(0, 6));

        if (Theme.GhostButton("Export as CSV", new Vector2(190, 34)))
        {
            var path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                $"LootView_History_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            plugin.HistoryService.ExportToCsv(path);
            Plugin.ChatGui.Print($"[LootView] History exported to: {path}");
        }
        ImGui.SameLine(0, 10);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextFaint, "Spreadsheet-friendly");

        ImGui.Dummy(new Vector2(0, 18));
        Theme.SectionHeader("Danger zone", FontAwesomeIcon.ExclamationTriangle);

        Theme.Callout(FontAwesomeIcon.Trash, "Deleting history cannot be undone",
            "Every recorded item, duty run and daily statistic is removed permanently. Export a backup first.",
            Theme.Bad);

        ImGui.Dummy(new Vector2(0, 10));

        if (Theme.DangerButton("Delete all history", new Vector2(200, 34)))
        {
            ImGui.OpenPopup("ConfirmClear");
        }

        DrawConfirmPopup("ConfirmClear",
            "Delete your entire loot history?",
            "This removes every item, duty run and statistic. It cannot be undone.",
            "Yes, delete everything",
            () =>
            {
                plugin.HistoryService.ClearAllHistory();
                InvalidateAll();
            });

        ImGui.Dummy(new Vector2(0, 10));
    }

    private static void DrawConfirmPopup(string id, string title, string body, string confirmLabel, Action onConfirm)
    {
        using var style = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(18, 16));
        var open = true;
        using var popup = ImRaii.PopupModal(id, ref open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar);
        if (!popup) return;

        Theme.IconText(FontAwesomeIcon.ExclamationTriangle, title, Theme.Warn);
        ImGui.Dummy(new Vector2(0, 4));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 360);
        ImGui.TextColored(Theme.TextMuted, body);
        ImGui.PopTextWrapPos();

        Theme.Rule(8f);

        if (Theme.DangerButton(confirmLabel, new Vector2(210, 32)))
        {
            onConfirm();
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine(0, 10);
        if (Theme.GhostButton("Cancel", new Vector2(110, 32)))
        {
            ImGui.CloseCurrentPopup();
        }
    }

    // ==================================================================
    // HELPERS
    // ==================================================================

    /// <summary>Queues the overview/trends statistics for the selected date range.</summary>
    private void RequestOverviewStatistics()
    {
        DateTime? start = dateRangeOption == 3 ? null : statsStartDate;
        DateTime? end = dateRangeOption == 3 ? null : statsEndDate;

        statsAsync.Ensure((dateRangeOption, start, end, itemCount),
            () => plugin.HistoryService.CalculateStatistics(start, end));
    }

    /// <summary>Queues the two period snapshots the analytics tab compares.</summary>
    private void RequestComparisonStatistics()
    {
        var days = comparisonDays;
        var currentEnd = DateTime.Now;
        var currentStart = currentEnd.AddDays(-days);
        var previousEnd = currentStart;
        var previousStart = previousEnd.AddDays(-days);

        // The range endpoints move every frame, so the key uses the day count instead.
        currentPeriodAsync.Ensure((days, itemCount, "current"),
            () => plugin.HistoryService.CalculateStatistics(currentStart, currentEnd));
        previousPeriodAsync.Ensure((days, itemCount, "previous"),
            () => plugin.HistoryService.CalculateStatistics(previousStart, previousEnd));
    }

    /// <summary>Queues the duty leaderboard and the recent-run list.</summary>
    private void RequestDutyStatistics()
    {
        dutyStatsAsync.Ensure(itemCount, () => plugin.HistoryService.CalculateDutyStatistics()
            .Where(d => d.Value.ContentType != "Content Type 0")
            .ToDictionary(k => k.Key, v => v.Value));

        recentRunsAsync.Ensure(itemCount, () => plugin.HistoryService.GetRecentDutyRuns(50)
            .Where(r => r.ContentType != "Content Type 0")
            .ToList());
    }

    /// <summary>Queues the filtered, sorted history the browser tab pages through.</summary>
    private void RequestFilteredHistory()
    {
        var search = searchQuery;
        var rarity = filterRarity;
        var zone = filterZone;
        var hqOnly = filterHQOnly;
        var ownOnly = filterOwnLootOnly;
        var descending = sortDescending;
        var column = sortColumn;

        historyAsync.Ensure((search, rarity, zone, hqOnly, ownOnly, column, descending, itemCount), () =>
        {
            // Snapshot first: the list keeps growing on the game thread while we filter.
            IEnumerable<LootItem> items = plugin.HistoryService.SnapshotItems();

            if (!string.IsNullOrWhiteSpace(search))
                items = items.Where(i => i.ItemName.Contains(search, StringComparison.OrdinalIgnoreCase));
            if (rarity != 999)
                items = items.Where(i => i.Rarity == rarity);
            if (!string.IsNullOrWhiteSpace(zone))
                items = items.Where(i => i.ZoneName.Contains(zone, StringComparison.OrdinalIgnoreCase));
            if (hqOnly)
                items = items.Where(i => i.IsHQ);
            if (ownOnly)
                items = items.Where(i => i.IsOwnLoot);

            return column switch
            {
                "ItemName" => descending ? items.OrderByDescending(i => i.ItemName).ToList() : items.OrderBy(i => i.ItemName).ToList(),
                "Rarity" => descending ? items.OrderByDescending(i => i.Rarity).ToList() : items.OrderBy(i => i.Rarity).ToList(),
                "Zone" => descending ? items.OrderByDescending(i => i.ZoneName).ToList() : items.OrderBy(i => i.ZoneName).ToList(),
                _ => descending ? items.OrderByDescending(i => i.Timestamp).ToList() : items.OrderBy(i => i.Timestamp).ToList()
            };
        });
    }

    /// <summary>Drops every cached result so the next frame recomputes from scratch.</summary>
    private void InvalidateAll()
    {
        statsAsync.Invalidate();
        historyAsync.Invalidate();
        currentPeriodAsync.Invalidate();
        previousPeriodAsync.Invalidate();
        dutyStatsAsync.Invalidate();
        recentRunsAsync.Invalidate();
        bestRunsAsync.Invalidate();
        fastestRunsAsync.Invalidate();
    }

    /// <summary>Full-panel placeholder for the first computation of a tab.</summary>
    private static void DrawLoading(string message)
    {
        var avail = ImGui.GetContentRegionAvail();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Math.Max((avail.Y - 80f) * 0.4f, 10f));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max((avail.X - 26f) * 0.5f, 0));

        Theme.Spinner();

        ImGui.Dummy(new Vector2(0, 10));
        Theme.CenteredText(message, Theme.Text);
        Theme.CenteredText("The game keeps running while this finishes", Theme.TextFaint);
    }

    /// <summary>Inline hint that a refresh is running behind the results on screen.</summary>
    private static void DrawWorkingBadge()
    {
        var dots = new string('.', 1 + (int)(Theme.Time * 3f) % 3);
        ImGui.AlignTextToFramePadding();
        Theme.Badge($"updating{dots}", Theme.Crystal);
    }

    private void UpdateDateRange()
    {
        var now = DateTime.Now;
        switch (dateRangeOption)
        {
            case 0: // Today
                statsStartDate = now.Date;
                statsEndDate = now;
                break;
            case 1: // This Week
                statsStartDate = now.Date.AddDays(-(int)now.DayOfWeek);
                statsEndDate = now;
                break;
            case 2: // This Month
                statsStartDate = new DateTime(now.Year, now.Month, 1);
                statsEndDate = now;
                break;
            case 3: // All Time - handled with nulls in the query
                break;
        }
    }

    private void DrawItemIcon(uint iconId, float size = 24f)
    {
        if (iconId == 0)
        {
            ImGui.Dummy(new Vector2(size, size));
            return;
        }

        if (!iconCache.TryGetValue(iconId, out var icon))
        {
            icon = Plugin.TextureProvider.GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(iconId));
            iconCache[iconId] = icon;
        }

        var wrap = icon?.GetWrapOrDefault();
        if (wrap != null)
        {
            ImGui.Image(wrap.Handle, new Vector2(size, size));
        }
        else
        {
            ImGui.Dummy(new Vector2(size, size));
        }
    }

    private void PerformZoneSearch()
    {
        zoneSearchResults.Clear();
        zoneSearchPerformed = true;

        if (string.IsNullOrWhiteSpace(zoneSearchQuery))
        {
            return;
        }

        var contentFinderSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ContentFinderCondition>();
        if (contentFinderSheet == null) return;

        var query = zoneSearchQuery.ToLower();

        foreach (var cfc in contentFinderSheet)
        {
            if (cfc.RowId == 0 || cfc.Content.RowId == 0) continue;
            if (string.IsNullOrEmpty(cfc.Name.ToString())) continue;

            var name = cfc.Name.ToString();

            if (name.ToLower().Contains(query))
            {
                zoneSearchResults.Add(new ZoneSearchResult
                {
                    ContentFinderConditionId = cfc.RowId,
                    TerritoryId = cfc.TerritoryType.RowId,
                    Name = name,
                    ContentType = GetContentTypeName(cfc.ContentType.RowId),
                    ItemLevel = cfc.ItemLevelRequired
                });
            }
        }

        zoneSearchResults = zoneSearchResults.OrderBy(r => r.Name).ToList();

        Plugin.Log.Info($"Zone search for '{zoneSearchQuery}' found {zoneSearchResults.Count} results");
    }

    private static string GetContentTypeName(uint contentTypeId)
    {
        return contentTypeId switch
        {
            2 => "Dungeon",
            4 => "Trial",
            5 => "Raid",
            7 => "Quest Battle",
            9 => "Guildhest",
            16 => "Deep Dungeon",
            21 => "Ultimate Raid",
            26 => "Variant Dungeon",
            28 => "Criterion Dungeon",
            _ => "Other"
        };
    }

    private void LoadZoneLootTable(uint contentFinderConditionId)
    {
        Plugin.Log.Info($"Loading loot table for ContentFinderCondition {contentFinderConditionId}");
        plugin.LootTableWindow.LoadZoneById(contentFinderConditionId);
    }

    private class ZoneSearchResult
    {
        public uint ContentFinderConditionId { get; set; }
        public uint TerritoryId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string ContentType { get; set; } = string.Empty;
        public ushort ItemLevel { get; set; }
    }

    public override void Dispose()
    {
        iconCache.Clear();
    }
}
