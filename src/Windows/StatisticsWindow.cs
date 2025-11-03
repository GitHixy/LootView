using System;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;
using System.IO;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using LootView.Models;

namespace LootView.Windows;

public class StatisticsWindow : Window
{
    private Plugin plugin;
    private LootStatistics cachedStats;
    private DateTime lastStatsUpdate = DateTime.MinValue;
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
    private const int itemsPerPage = 50;
    private string sortColumn = "Timestamp";
    private bool sortDescending = true;
    
    // For analytics
    private int comparisonDays = 7;

    // For duty tracker
    private string selectedContentType = "All";
    private uint selectedDutyId = 0;

    // For zone finder
    private string zoneSearchQuery = "";
    private List<ZoneSearchResult> zoneSearchResults = new();
    private bool zoneSearchPerformed = false;
    private uint selectedZoneForLootTable = 0;
    private string selectedZoneName = "";

    public StatisticsWindow(Plugin plugin) : base("Loot Statistics & History###LootView_Statistics")
    {
        this.plugin = plugin;
        
        Size = new Vector2(800, 600);
        SizeConstraintMin = new Vector2(600, 400);
    }

    protected override void DrawContents()
    {
        // Apply background alpha from configuration
        BgAlpha = plugin.Configuration.BackgroundAlpha;
        
        // Refresh stats every 5 seconds or when stale
        if ((DateTime.Now - lastStatsUpdate).TotalSeconds > 5)
        {
            RefreshStatistics();
        }

        if (cachedStats == null)
        {
            ImGui.Text("Loading statistics...");
            return;
        }

        DrawTabs();
    }

    private void DrawTabs()
    {
        if (ImGui.BeginTabBar("StatisticsTabs", ImGuiTabBarFlags.None))
        {
            // Overview Tab
            var overviewOpen = ImGui.BeginTabItem($"{FontAwesomeIcon.ChartPie.ToIconString()} Overview##OverviewTab");
            if (overviewOpen)
            {
                if (ImGui.BeginChild("OverviewContent", new Vector2(0, 0), false))
                {
                    DrawOverviewTab();
                    ImGui.EndChild();
                }
                ImGui.EndTabItem();
            }

            // History Tab
            var historyOpen = ImGui.BeginTabItem($"{FontAwesomeIcon.Book.ToIconString()} History##HistoryTab");
            if (historyOpen)
            {
                if (ImGui.BeginChild("HistoryContent", new Vector2(0, 0), false))
                {
                    DrawHistoryTab();
                    ImGui.EndChild();
                }
                ImGui.EndTabItem();
            }

            // Trends Tab
            var trendsOpen = ImGui.BeginTabItem($"{FontAwesomeIcon.ChartLine.ToIconString()} Trends##TrendsTab");
            if (trendsOpen)
            {
                if (ImGui.BeginChild("TrendsContent", new Vector2(0, 0), false))
                {
                    DrawTrendsTab();
                    ImGui.EndChild();
                }
                ImGui.EndTabItem();
            }

            // Analytics Tab
            var analyticsOpen = ImGui.BeginTabItem($"{FontAwesomeIcon.Calculator.ToIconString()} Analytics##AnalyticsTab");
            if (analyticsOpen)
            {
                if (ImGui.BeginChild("AnalyticsContent", new Vector2(0, 0), false))
                {
                    DrawAnalyticsTab();
                    ImGui.EndChild();
                }
                ImGui.EndTabItem();
            }

            // Duty Tracker Tab
            var dutyOpen = ImGui.BeginTabItem($"{FontAwesomeIcon.Flag.ToIconString()} Duty Tracker##DutyTrackerTab");
            if (dutyOpen)
            {
                if (ImGui.BeginChild("DutyTrackerContent", new Vector2(0, 0), false))
                {
                    DrawDutyTrackerTab();
                    ImGui.EndChild();
                }
                ImGui.EndTabItem();
            }

            // Zone Finder Tab
            var zoneFinderOpen = ImGui.BeginTabItem($"{FontAwesomeIcon.Search.ToIconString()} Zone Finder##ZoneFinderTab");
            if (zoneFinderOpen)
            {
                if (ImGui.BeginChild("ZoneFinderContent", new Vector2(0, 0), false))
                {
                    DrawZoneFinderTab();
                    ImGui.EndChild();
                }
                ImGui.EndTabItem();
            }

            // Export Tab
            var exportOpen = ImGui.BeginTabItem($"{FontAwesomeIcon.Download.ToIconString()} Export##ExportTab");
            if (exportOpen)
            {
                if (ImGui.BeginChild("ExportContent", new Vector2(0, 0), false))
                {
                    DrawExportTab();
                    ImGui.EndChild();
                }
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawOverviewTab()
    {
        if (cachedStats == null) return;

        ImGui.Spacing();
        
        // Date range selector with better layout
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Date Range:");
        ImGui.SameLine();
        if (ImGui.RadioButton("Today", ref dateRangeOption, 0)) UpdateDateRange();
        ImGui.SameLine();
        if (ImGui.RadioButton("Week", ref dateRangeOption, 1)) UpdateDateRange();
        ImGui.SameLine();
        if (ImGui.RadioButton("Month", ref dateRangeOption, 2)) UpdateDateRange();
        ImGui.SameLine();
        if (ImGui.RadioButton("All Time", ref dateRangeOption, 3)) UpdateDateRange();
        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 20);
        if (ImGui.Button("Refresh", new Vector2(100, 0)))
        {
            RefreshStatistics();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Overall Statistics
        ImGui.TextColored(new Vector4(0.3f, 0.8f, 1.0f, 1.0f), "Overall Statistics");
        ImGui.Spacing();

        DrawStatBox("Total Items", cachedStats.TotalItems.ToString("N0"), new Vector4(0.4f, 0.8f, 0.4f, 1.0f));
        ImGui.SameLine();
        DrawStatBox("Unique Items", cachedStats.TotalUnique.ToString("N0"), new Vector4(0.8f, 0.6f, 0.3f, 1.0f));
        ImGui.SameLine();
        DrawStatBox("HQ Items", $"{cachedStats.TotalHQ} ({cachedStats.HQPercentage:F1}%)", new Vector4(0.9f, 0.7f, 0.2f, 1.0f));
        ImGui.SameLine();
        DrawStatBox("Items/Day", cachedStats.ItemsPerDay.ToString("F1"), new Vector4(0.5f, 0.5f, 0.9f, 1.0f));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Two columns layout
        if (ImGui.BeginTable("OverviewTable", 2, ImGuiTableFlags.None))
        {
            ImGui.TableSetupColumn("Left", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Right", ImGuiTableColumnFlags.WidthStretch);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();

            // Rarity Breakdown
            ImGui.TextColored(new Vector4(0.3f, 0.8f, 1.0f, 1.0f), "Rarity Breakdown");
            ImGui.Spacing();

            if (cachedStats.ByRarity.Any())
            {
                foreach (var rarityKv in cachedStats.ByRarity.OrderByDescending(kv => kv.Key))
                {
                    var rarityStats = rarityKv.Value;
                    var color = GetRarityColor(rarityStats.Rarity);
                    var rarityName = GetRarityName(rarityStats.Rarity);

                    ImGui.TextColored(color, $"{rarityName}:");
                    ImGui.SameLine(150);
                    ImGui.Text($"{rarityStats.Count} ({rarityStats.Percentage:F1}%)");
                    
                    if (rarityStats.HQCount > 0)
                    {
                        ImGui.SameLine();
                        ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1.0f), $"[{rarityStats.HQCount} HQ]");
                    }
                }
            }
            else
            {
                ImGui.TextDisabled("No items found in this range");
            }

            ImGui.Spacing();
            ImGui.Spacing();

            // Play Streaks
            ImGui.TextColored(new Vector4(0.3f, 0.8f, 1.0f, 1.0f), "Play Streaks");
            ImGui.Spacing();
            ImGui.Text($"Current Streak: {cachedStats.CurrentStreak} days");
            ImGui.Text($"Longest Streak: {cachedStats.LongestStreak} days");
            ImGui.Text($"Days Played: {cachedStats.DaysPlayed}");

            if (cachedStats.FirstItemDate.HasValue && cachedStats.LastItemDate.HasValue)
            {
                ImGui.Spacing();
                ImGui.TextDisabled($"First item: {cachedStats.FirstItemDate.Value:yyyy-MM-dd}");
                ImGui.TextDisabled($"Latest item: {cachedStats.LastItemDate.Value:yyyy-MM-dd HH:mm}");
            }

            ImGui.TableNextColumn();

            // Top Zones
            ImGui.TextColored(new Vector4(0.3f, 0.8f, 1.0f, 1.0f), "Top Zones");
            ImGui.Spacing();

            var topZones = cachedStats.ByZone.OrderByDescending(z => z.Value.TotalItems).Take(10);
            if (topZones.Any())
            {
                foreach (var zone in topZones)
                {
                    var zoneStats = zone.Value;
                    ImGui.BulletText($"{zoneStats.ZoneName}");
                    ImGui.SameLine(250);
                    ImGui.Text($"{zoneStats.TotalItems} items");
                    ImGui.SameLine(350);
                    ImGui.TextDisabled($"({zoneStats.UniqueItems} unique)");
                }
            }
            else
            {
                ImGui.TextDisabled("No zone data available");
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Most Common Items
        ImGui.TextColored(new Vector4(0.3f, 0.8f, 1.0f, 1.0f), "Most Common Items");
        ImGui.Spacing();

        if (cachedStats.MostCommonItems.Any())
        {
            if (ImGui.BeginTable("CommonItems", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            {
                ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Count", ImGuiTableColumnFlags.WidthFixed, 80);
                ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthFixed, 60);
                ImGui.TableSetupColumn("Last Obtained", ImGuiTableColumnFlags.WidthFixed, 150);
                ImGui.TableHeadersRow();

                foreach (var item in cachedStats.MostCommonItems.Take(10))
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();

                    DrawItemIcon(item.IconId);
                    ImGui.SameLine();
                    ImGui.TextColored(GetRarityColor(item.Rarity), item.ItemName);

                    ImGui.TableNextColumn();
                    ImGui.Text(item.Count.ToString());

                    ImGui.TableNextColumn();
                    if (item.HQCount > 0)
                    {
                        ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1.0f), item.HQCount.ToString());
                    }
                    else
                    {
                        ImGui.TextDisabled("-");
                    }

                    ImGui.TableNextColumn();
                    ImGui.Text(item.LastObtained.ToString("MM/dd HH:mm"));
                }

                ImGui.EndTable();
            }
        }
        else
        {
            ImGui.TextDisabled("No items to display");
        }
    }

    private void DrawHistoryTab()
    {
        ImGui.Spacing();

        // Search bar
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Search:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(250);
        ImGui.InputTextWithHint("##search", "Search item names...", ref searchQuery, 100);
        
        ImGui.SameLine();
        ImGui.SetNextItemWidth(180);
        if (ImGui.BeginCombo("##rarityFilter", filterRarity == 999 ? "All Rarities" : GetRarityName(filterRarity)))
        {
            if (ImGui.Selectable("All Rarities", filterRarity == 999)) { filterRarity = 999; historyPage = 0; }
            ImGui.Separator();
            if (ImGui.Selectable("Common (1)", filterRarity == 1)) { filterRarity = 1; historyPage = 0; }
            if (ImGui.Selectable("Uncommon (2)", filterRarity == 2)) { filterRarity = 2; historyPage = 0; }
            if (ImGui.Selectable("Rare (3)", filterRarity == 3)) { filterRarity = 3; historyPage = 0; }
            if (ImGui.Selectable("Rare+ (4)", filterRarity == 4)) { filterRarity = 4; historyPage = 0; }
            if (ImGui.Selectable("Legendary (7)", filterRarity == 7)) { filterRarity = 7; historyPage = 0; }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(180);
        ImGui.InputTextWithHint("##zoneFilter", "Filter by zone...", ref filterZone, 100);

        // Second row of filters
        ImGui.Checkbox("HQ Only", ref filterHQOnly);
        ImGui.SameLine();
        ImGui.Checkbox("Own Loot Only", ref filterOwnLootOnly);
        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 20);
        if (ImGui.Button("🗑️ Clear All Filters"))
        {
            searchQuery = "";
            filterZone = "";
            filterRarity = 999;
            filterHQOnly = false;
            filterOwnLootOnly = false;
            historyPage = 0;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Get filtered history
        var history = plugin.HistoryService.GetHistory();
        var items = history.AllItems.AsEnumerable();

        // Apply all filters
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            items = items.Where(i => i.ItemName.Contains(searchQuery, StringComparison.OrdinalIgnoreCase));
        }
        if (filterRarity != 999)
        {
            items = items.Where(i => i.Rarity == filterRarity);
        }
        if (!string.IsNullOrWhiteSpace(filterZone))
        {
            items = items.Where(i => i.ZoneName.Contains(filterZone, StringComparison.OrdinalIgnoreCase));
        }
        if (filterHQOnly)
        {
            items = items.Where(i => i.IsHQ);
        }
        if (filterOwnLootOnly)
        {
            items = items.Where(i => i.IsOwnLoot);
        }

        // Apply sorting
        var filteredItems = sortColumn switch
        {
            "ItemName" => sortDescending ? items.OrderByDescending(i => i.ItemName).ToList() : items.OrderBy(i => i.ItemName).ToList(),
            "Rarity" => sortDescending ? items.OrderByDescending(i => i.Rarity).ToList() : items.OrderBy(i => i.Rarity).ToList(),
            "Zone" => sortDescending ? items.OrderByDescending(i => i.ZoneName).ToList() : items.OrderBy(i => i.ZoneName).ToList(),
            _ => sortDescending ? items.OrderByDescending(i => i.Timestamp).ToList() : items.OrderBy(i => i.Timestamp).ToList()
        };
        var totalPages = (int)Math.Ceiling(filteredItems.Count / (double)itemsPerPage);

        // Results info and pagination
        ImGui.TextColored(new Vector4(0.5f, 0.9f, 0.5f, 1.0f), $"📋 {filteredItems.Count:N0} items found");
        
        if (totalPages > 1)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetWindowWidth() - 300);
            if (ImGui.Button("⏮️ First")) historyPage = 0;
            ImGui.SameLine();
            if (ImGui.Button("◀ Prev") && historyPage > 0) historyPage--;
            ImGui.SameLine();
            ImGui.Text($"Page {historyPage + 1} / {totalPages}");
            ImGui.SameLine();
            if (ImGui.Button("Next ▶") && historyPage < totalPages - 1) historyPage++;
            ImGui.SameLine();
            if (ImGui.Button("Last ⏭️")) historyPage = totalPages - 1;
        }

        ImGui.Spacing();

        // History table with sortable columns
        var tableHeight = ImGui.GetContentRegionAvail().Y;
        if (ImGui.BeginTable("HistoryTable", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Sortable, new Vector2(0, tableHeight)))
        {
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 0, 0);
            ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 50, 1);
            ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthFixed, 120, 2);
            ImGui.TableSetupColumn("Zone", ImGuiTableColumnFlags.WidthFixed, 180, 3);
            ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthFixed, 90, 4);
            ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 140, 5);
            ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthFixed, 70, 6);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            var pageItems = filteredItems.Skip(historyPage * itemsPerPage).Take(itemsPerPage);
            foreach (var item in pageItems)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();

                DrawItemIcon(item.IconId);
                ImGui.SameLine();
                ImGui.TextColored(GetRarityColor(item.Rarity), item.ItemName);
                if (item.IsHQ)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1.0f), "[HQ]");
                }

                ImGui.TableNextColumn();
                ImGui.Text(item.Quantity.ToString());

                ImGui.TableNextColumn();
                if (item.IsOwnLoot)
                {
                    ImGui.TextColored(new Vector4(0.3f, 1.0f, 0.3f, 1.0f), item.PlayerName);
                }
                else
                {
                    ImGui.Text(item.PlayerName);
                }

                ImGui.TableNextColumn();
                ImGui.TextDisabled(item.ZoneName);

                ImGui.TableNextColumn();
                ImGui.Text(item.Source.ToString());

                ImGui.TableNextColumn();
                ImGui.Text(item.Timestamp.ToString("MM/dd HH:mm:ss"));

                ImGui.TableNextColumn();
                ImGui.TextDisabled(item.ItemId.ToString());
            }

            ImGui.EndTable();
        }
    }

    private void DrawTrendsTab()
    {
        if (cachedStats == null) return;

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.3f, 0.8f, 1.0f, 1.0f), "Daily Activity");
        ImGui.Spacing();

        // Daily chart (simplified text representation)
        var recentDays = cachedStats.DailyItems.OrderByDescending(d => d.Key).Take(14).Reverse();
        if (recentDays.Any())
        {
            var maxItems = recentDays.Max(d => d.Value);
            
            foreach (var day in recentDays)
            {
                var barLength = maxItems > 0 ? (int)((day.Value / (float)maxItems) * 40) : 0;
                var bar = new string('█', barLength);
                
                ImGui.Text($"{day.Key:MM/dd}");
                ImGui.SameLine(80);
                ImGui.TextColored(new Vector4(0.4f, 0.8f, 0.4f, 1.0f), bar);
                ImGui.SameLine();
                ImGui.Text($"{day.Value} items");
            }
        }
        else
        {
            ImGui.TextDisabled("No daily data available");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Hourly distribution
        ImGui.TextColored(new Vector4(0.3f, 0.8f, 1.0f, 1.0f), "Hourly Activity Pattern");
        ImGui.Spacing();

        if (cachedStats.HourlyItems.Any())
        {
            var maxHourly = cachedStats.HourlyItems.Max(h => h.Value);
            
            for (int hour = 0; hour < 24; hour++)
            {
                var count = cachedStats.HourlyItems.ContainsKey(hour) ? cachedStats.HourlyItems[hour] : 0;
                var barLength = maxHourly > 0 ? (int)((count / (float)maxHourly) * 30) : 0;
                var bar = new string('█', barLength);
                
                ImGui.Text($"{hour:D2}:00");
                ImGui.SameLine(60);
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.9f, 1.0f), bar);
                ImGui.SameLine();
                ImGui.Text($"{count}");
            }
        }
        else
        {
            ImGui.TextDisabled("No hourly data available");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Rarest Items
        ImGui.TextColored(new Vector4(0.3f, 0.8f, 1.0f, 1.0f), "Rarest Items Obtained");
        ImGui.Spacing();

        if (cachedStats.RarestItems.Any())
        {
            if (ImGui.BeginTable("RarestItems", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            {
                ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Rarity", ImGuiTableColumnFlags.WidthFixed, 80);
                ImGui.TableSetupColumn("Zone", ImGuiTableColumnFlags.WidthFixed, 150);
                ImGui.TableSetupColumn("Obtained", ImGuiTableColumnFlags.WidthFixed, 140);
                ImGui.TableHeadersRow();

                foreach (var item in cachedStats.RarestItems.Take(15))
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();

                    DrawItemIcon(item.IconId);
                    ImGui.SameLine();
                    ImGui.TextColored(GetRarityColor(item.Rarity), item.ItemName);
                    if (item.IsHQ)
                    {
                        ImGui.SameLine();
                        ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1.0f), "[HQ]");
                    }

                    ImGui.TableNextColumn();
                    ImGui.TextColored(GetRarityColor(item.Rarity), GetRarityName(item.Rarity));

                    ImGui.TableNextColumn();
                    ImGui.TextDisabled(item.ZoneName);

                    ImGui.TableNextColumn();
                    ImGui.Text(item.Timestamp.ToString("MM/dd HH:mm"));
                }

                ImGui.EndTable();
            }
        }
    }

    private void DrawAnalyticsTab()
    {
        if (cachedStats == null) return;

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.3f, 0.8f, 1.0f, 1.0f), "Advanced Analytics");
        ImGui.Spacing();
        ImGui.TextWrapped("Deep dive into your loot patterns and trends.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Comparison Period Selector
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Compare with:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150);
        if (ImGui.SliderInt("##comparisonDays", ref comparisonDays, 1, 30))
        {
            // Trigger recalculation if needed
        }
        ImGui.SameLine();
        ImGui.Text($"days ago");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Calculate comparison statistics
        var currentPeriodEnd = DateTime.Now;
        var currentPeriodStart = currentPeriodEnd.AddDays(-comparisonDays);
        var previousPeriodEnd = currentPeriodStart;
        var previousPeriodStart = previousPeriodEnd.AddDays(-comparisonDays);

        var currentStats = plugin.HistoryService.CalculateStatistics(currentPeriodStart, currentPeriodEnd);
        var previousStats = plugin.HistoryService.CalculateStatistics(previousPeriodStart, previousPeriodEnd);

        // Items Comparison
        ImGui.TextColored(new Vector4(0.3f, 0.8f, 1.0f, 1.0f), "Items Obtained");
        ImGui.Spacing();
        
        DrawComparisonMetric("Total Items", currentStats.TotalItems, previousStats.TotalItems);
        DrawComparisonMetric("Unique Items", currentStats.TotalUnique, previousStats.TotalUnique);
        DrawComparisonMetric("HQ Items", currentStats.TotalHQ, previousStats.TotalHQ);
        DrawComparisonMetric("Items/Day", (int)currentStats.ItemsPerDay, (int)previousStats.ItemsPerDay);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Rarity Distribution Comparison
        ImGui.TextColored(new Vector4(0.3f, 0.8f, 1.0f, 1.0f), "💎 Rarity Distribution Changes");
        ImGui.Spacing();

        if (ImGui.BeginTable("RarityComparison", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Rarity", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("Current", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("Previous", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("Change", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableHeadersRow();

            var allRarities = currentStats.ByRarity.Keys.Union(previousStats.ByRarity.Keys).OrderByDescending(r => r);
            foreach (var rarity in allRarities)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(GetRarityColor(rarity), GetRarityName(rarity));

                ImGui.TableNextColumn();
                var currentCount = currentStats.ByRarity.ContainsKey(rarity) ? currentStats.ByRarity[rarity].Count : 0;
                ImGui.Text(currentCount.ToString());

                ImGui.TableNextColumn();
                var previousCount = previousStats.ByRarity.ContainsKey(rarity) ? previousStats.ByRarity[rarity].Count : 0;
                ImGui.Text(previousCount.ToString());

                ImGui.TableNextColumn();
                var change = currentCount - previousCount;
                var changePercent = previousCount > 0 ? (change * 100.0 / previousCount) : 0;
                
                if (change > 0)
                {
                    ImGui.TextColored(new Vector4(0.3f, 1.0f, 0.3f, 1.0f), $"+{change} (+{changePercent:F1}%)");
                }
                else if (change < 0)
                {
                    ImGui.TextColored(new Vector4(1.0f, 0.3f, 0.3f, 1.0f), $"{change} ({changePercent:F1}%)");
                }
                else
                {
                    ImGui.TextDisabled("No change");
                }
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Top Zones Comparison
        ImGui.TextColored(new Vector4(0.3f, 0.8f, 1.0f, 1.0f), "Most Active Zones");
        ImGui.Spacing();

        var topCurrentZones = currentStats.ByZone.OrderByDescending(z => z.Value.TotalItems).Take(5);
        var topPreviousZones = previousStats.ByZone.OrderByDescending(z => z.Value.TotalItems).Take(5);

        if (ImGui.BeginTable("ZoneActivity", 2, ImGuiTableFlags.Borders))
        {
            ImGui.TableSetupColumn($"Last {comparisonDays} Days", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn($"Previous {comparisonDays} Days", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            
            foreach (var zone in topCurrentZones)
            {
                ImGui.BulletText($"{zone.Value.ZoneName}");
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.5f, 0.9f, 0.5f, 1.0f), $"({zone.Value.TotalItems} items)");
            }

            ImGui.TableNextColumn();
            
            foreach (var zone in topPreviousZones)
            {
                ImGui.BulletText($"{zone.Value.ZoneName}");
                ImGui.SameLine();
                ImGui.TextDisabled($"({zone.Value.TotalItems} items)");
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Peak Activity Analysis
        ImGui.TextColored(new Vector4(0.3f, 0.8f, 1.0f, 1.0f), "Peak Activity Times");
        ImGui.Spacing();

        if (currentStats.HourlyItems.Any())
        {
            var peakHour = currentStats.HourlyItems.OrderByDescending(h => h.Value).First();
            var avgPerHour = currentStats.HourlyItems.Values.Average();
            
            ImGui.Text($"Peak Hour: {peakHour.Key:D2}:00 with {peakHour.Value} items");
            ImGui.Text($"Average Per Hour: {avgPerHour:F1} items");
            
            ImGui.Spacing();
            ImGui.Text("Activity Heatmap:");
            ImGui.Spacing();
            
            // Show hourly activity in 4-hour blocks
            for (int block = 0; block < 6; block++)
            {
                var startHour = block * 4;
                var endHour = startHour + 3;
                var blockTotal = 0;
                
                for (int h = startHour; h <= endHour; h++)
                {
                    blockTotal += currentStats.HourlyItems.ContainsKey(h) ? currentStats.HourlyItems[h] : 0;
                }
                
                var intensity = avgPerHour > 0 ? blockTotal / (4.0 * avgPerHour) : 0;
                var color = new Vector4(
                    0.5f + (float)Math.Min(intensity * 0.5, 0.5),
                    0.5f,
                    0.5f - (float)Math.Min(intensity * 0.3, 0.3),
                    1.0f
                );
                
                ImGui.PushStyleColor(ImGuiCol.Button, color);
                ImGui.Button($"{startHour:D2}:00 - {endHour:D2}:59\n{blockTotal} items", new Vector2(120, 50));
                ImGui.PopStyleColor();
                
                if (block < 5)
                {
                    ImGui.SameLine();
                }
            }
        }
    }

    private void DrawComparisonMetric(string label, int currentValue, int previousValue)
    {
        ImGui.BulletText(label);
        ImGui.SameLine(200);
        ImGui.Text($"{currentValue:N0}");
        ImGui.SameLine(300);
        
        if (previousValue > 0)
        {
            var change = currentValue - previousValue;
            var changePercent = (change * 100.0 / previousValue);
            
            if (change > 0)
            {
                ImGui.TextColored(new Vector4(0.3f, 1.0f, 0.3f, 1.0f), $"▲ +{change:N0} (+{changePercent:F1}%)");
            }
            else if (change < 0)
            {
                ImGui.TextColored(new Vector4(1.0f, 0.3f, 0.3f, 1.0f), $"▼ {change:N0} ({changePercent:F1}%)");
            }
            else
            {
                ImGui.TextDisabled("━ No change");
            }
        }
        else
        {
            ImGui.TextDisabled("━ No previous data");
        }
    }

    private void DrawDutyTrackerTab()
    {
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.3f, 0.8f, 1.0f, 1.0f), "Duty/Dungeon Tracker");
        ImGui.Spacing();
        ImGui.TextWrapped("Track your performance in duties, dungeons, raids, trials, and alliance raids!");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Get duty statistics
        var dutyStats = plugin.HistoryService.CalculateDutyStatistics()
            .Where(d => d.Value.ContentType != "Content Type 0")
            .ToDictionary(k => k.Key, v => v.Value);
        var recentRuns = plugin.HistoryService.GetRecentDutyRuns(50)
            .Where(r => r.ContentType != "Content Type 0")
            .ToList();

        // Tab bar for different views
        if (ImGui.BeginTabBar("DutyTrackerTabs"))
        {
            // Overview Tab
            if (ImGui.BeginTabItem("Overview"))
            {
                ImGui.Spacing();

                if (!dutyStats.Any())
                {
                    ImGui.TextDisabled("No duty runs recorded yet. Enter a duty to start tracking!");
                }
                else
                {
                    // Summary stats
                    ImGui.Text($"Total Duties Tracked: {dutyStats.Count}");
                    ImGui.Text($"Total Runs: {dutyStats.Sum(d => d.Value.TotalAttempts)}");
                    ImGui.Text($"Total Completions: {dutyStats.Sum(d => d.Value.Completions)}");
                    ImGui.Text($"Total Items Obtained: {dutyStats.Sum(d => d.Value.TotalItemsObtained)}");
                    ImGui.Spacing();
                    ImGui.Separator();
                    ImGui.Spacing();

                    // Filter by content type
                    ImGui.Text("Filter by Type:");
                    ImGui.SameLine();
                    if (ImGui.Button("All")) selectedContentType = "All";
                    ImGui.SameLine();
                    if (ImGui.Button("Dungeon")) selectedContentType = "Dungeon";
                    ImGui.SameLine();
                    if (ImGui.Button("Trial")) selectedContentType = "Trial";
                    ImGui.SameLine();
                    if (ImGui.Button("Raid")) selectedContentType = "Raid";
                    ImGui.SameLine();
                    if (ImGui.Button("Alliance Raid")) selectedContentType = "Alliance Raid";
                    ImGui.Spacing();

                    // Filter duties
                    var filteredStats = selectedContentType == "All" 
                        ? dutyStats 
                        : dutyStats.Where(d => d.Value.ContentType == selectedContentType).ToDictionary(k => k.Key, v => v.Value);

                    // Calculate max duty name width
                    float maxNameWidth = 150f; // Minimum width
                    foreach (var stat in filteredStats)
                    {
                        var nameWidth = ImGui.CalcTextSize(stat.Value.DutyName).X + 20f; // Add padding
                        if (nameWidth > maxNameWidth)
                        {
                            maxNameWidth = nameWidth;
                        }
                    }

                    var tableHeight = ImGui.GetContentRegionAvail().Y - 20;
                    if (ImGui.BeginTable("DutyStatsTable", 8, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Sortable, new Vector2(0, tableHeight)))
                    {
                        ImGui.TableSetupColumn("Duty Name", ImGuiTableColumnFlags.WidthFixed, maxNameWidth);
                        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 100);
                        ImGui.TableSetupColumn("Runs", ImGuiTableColumnFlags.WidthFixed, 60);
                        ImGui.TableSetupColumn("Items", ImGuiTableColumnFlags.WidthFixed, 60);
                        ImGui.TableSetupColumn("Items/Run", ImGuiTableColumnFlags.WidthFixed, 80);
                        ImGui.TableSetupColumn("Avg Time", ImGuiTableColumnFlags.WidthFixed, 80);
                        ImGui.TableSetupColumn("Best Time", ImGuiTableColumnFlags.WidthFixed, 80);
                        ImGui.TableSetupColumn("Last Run", ImGuiTableColumnFlags.WidthFixed, 140);
                        ImGui.TableSetupScrollFreeze(0, 1);
                        ImGui.TableHeadersRow();

                        foreach (var stat in filteredStats.OrderByDescending(s => s.Value.TotalAttempts))
                        {
                            ImGui.TableNextRow();
                            ImGui.TableNextColumn();
                            
                            if (ImGui.Selectable($"##duty_{stat.Key}", false, ImGuiSelectableFlags.SpanAllColumns))
                            {
                                selectedDutyId = stat.Key;
                            }
                            if (ImGui.IsItemHovered())
                            {
                                ImGui.SetTooltip($"Click to see detailed stats for {stat.Value.DutyName}");
                            }
                            ImGui.SameLine();
                            ImGui.Text(stat.Value.DutyName);

                            ImGui.TableNextColumn();
                            ImGui.TextDisabled(stat.Value.ContentType);

                            ImGui.TableNextColumn();
                            ImGui.Text(stat.Value.TotalAttempts.ToString());

                            ImGui.TableNextColumn();
                            ImGui.TextColored(new Vector4(0.5f, 0.9f, 0.5f, 1.0f), stat.Value.TotalItemsObtained.ToString());

                            ImGui.TableNextColumn();
                            ImGui.Text($"{stat.Value.AverageItemsPerRun:F1}");

                            ImGui.TableNextColumn();
                            ImGui.TextDisabled($"{stat.Value.AverageTimeMinutes:F1}m");

                            ImGui.TableNextColumn();
                            if (stat.Value.FastestTimeMinutes < double.MaxValue)
                            {
                                ImGui.TextColored(new Vector4(0.3f, 1.0f, 0.3f, 1.0f), $"{stat.Value.FastestTimeMinutes:F1}m");
                            }
                            else
                            {
                                ImGui.TextDisabled("-");
                            }

                            ImGui.TableNextColumn();
                            ImGui.Text(stat.Value.LastAttempt.ToString("MM/dd HH:mm"));
                        }

                        ImGui.EndTable();
                    }
                }

                ImGui.EndTabItem();
            }

            // Recent Runs Tab
            if (ImGui.BeginTabItem("📜 Recent Runs"))
            {
                ImGui.Spacing();
                ImGui.TextColored(new Vector4(0.3f, 0.8f, 1.0f, 1.0f), "Recent Duty Runs");
                ImGui.Spacing();

                if (!recentRuns.Any())
                {
                    ImGui.TextDisabled("No recent runs to display.");
                }
                else
                {
                    // Calculate max duty name width for recent runs
                    float maxNameWidthRecent = 150f; // Minimum width
                    foreach (var run in recentRuns)
                    {
                        var nameWidth = ImGui.CalcTextSize(run.DutyName).X + 20f; // Add padding
                        if (nameWidth > maxNameWidthRecent)
                        {
                            maxNameWidthRecent = nameWidth;
                        }
                    }

                    var tableHeight = ImGui.GetContentRegionAvail().Y - 20;
                    if (ImGui.BeginTable("RecentRunsTable", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(0, tableHeight)))
                    {
                        ImGui.TableSetupColumn("Date/Time", ImGuiTableColumnFlags.WidthFixed, 140);
                        ImGui.TableSetupColumn("Duty Name", ImGuiTableColumnFlags.WidthFixed, maxNameWidthRecent);
                        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 100);
                        ImGui.TableSetupColumn("Duration", ImGuiTableColumnFlags.WidthFixed, 80);
                        ImGui.TableSetupColumn("Items", ImGuiTableColumnFlags.WidthFixed, 60);
                        ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthFixed, 50);
                        ImGui.TableSetupScrollFreeze(0, 1);
                        ImGui.TableHeadersRow();

                        foreach (var run in recentRuns)
                        {
                            ImGui.TableNextRow();
                            ImGui.TableNextColumn();
                            ImGui.Text(run.StartedAt.ToString("MM/dd HH:mm:ss"));

                            ImGui.TableNextColumn();
                            ImGui.Text(run.DutyName);

                            ImGui.TableNextColumn();
                            ImGui.TextDisabled(run.ContentType);

                            ImGui.TableNextColumn();
                            if (run.CompletedAt.HasValue)
                            {
                                ImGui.Text($"{run.DurationMinutes:F1}m");
                            }
                            else
                            {
                                ImGui.TextDisabled("-");
                            }

                            ImGui.TableNextColumn();
                            ImGui.Text(run.ItemsObtained.ToString());

                            ImGui.TableNextColumn();
                            if (run.HQItemsObtained > 0)
                            {
                                ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1.0f), run.HQItemsObtained.ToString());
                            }
                            else
                            {
                                ImGui.TextDisabled("-");
                            }
                        }

                        ImGui.EndTable();
                    }
                }

                ImGui.EndTabItem();
            }

            // Best Runs Tab
            if (selectedDutyId > 0 && ImGui.BeginTabItem("Best Runs"))
            {
                ImGui.Spacing();
                
                if (dutyStats.TryGetValue(selectedDutyId, out var selectedStats))
                {
                    ImGui.TextColored(new Vector4(0.3f, 0.8f, 1.0f, 1.0f), $"Best Runs: {selectedStats.DutyName}");
                    ImGui.Spacing();

                    var bestRuns = plugin.HistoryService.GetBestRuns(selectedDutyId, 20);
                    var fastestRuns = plugin.HistoryService.GetFastestRuns(selectedDutyId, 20);

                    if (ImGui.BeginTabBar("BestRunsTabs"))
                    {
                        if (ImGui.BeginTabItem("💎 Most Items"))
                        {
                            ImGui.Spacing();

                            if (!bestRuns.Any())
                            {
                                ImGui.TextDisabled("No completed runs yet.");
                            }
                            else
                            {
                                var tableHeight = ImGui.GetContentRegionAvail().Y - 20;
                                if (ImGui.BeginTable("BestRunsItemsTable", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(0, tableHeight)))
                                {
                                    ImGui.TableSetupColumn("Rank", ImGuiTableColumnFlags.WidthFixed, 50);
                                    ImGui.TableSetupColumn("Date/Time", ImGuiTableColumnFlags.WidthFixed, 140);
                                    ImGui.TableSetupColumn("Items", ImGuiTableColumnFlags.WidthFixed, 70);
                                    ImGui.TableSetupColumn("Duration", ImGuiTableColumnFlags.WidthFixed, 80);
                                    ImGui.TableSetupColumn("Item IDs", ImGuiTableColumnFlags.WidthStretch);
                                    ImGui.TableSetupScrollFreeze(0, 1);
                                    ImGui.TableHeadersRow();

                                    int rank = 1;
                                    foreach (var run in bestRuns)
                                    {
                                        ImGui.TableNextRow();
                                        ImGui.TableNextColumn();
                                        var rankColor = rank switch
                                        {
                                            1 => new Vector4(1.0f, 0.84f, 0.0f, 1.0f),
                                            2 => new Vector4(0.75f, 0.75f, 0.75f, 1.0f),
                                            3 => new Vector4(0.8f, 0.5f, 0.2f, 1.0f),
                                            _ => new Vector4(1.0f, 1.0f, 1.0f, 1.0f)
                                        };
                                        ImGui.TextColored(rankColor, $"#{rank}");

                                        ImGui.TableNextColumn();
                                        ImGui.Text(run.StartedAt.ToString("MM/dd HH:mm"));

                                        ImGui.TableNextColumn();
                                        ImGui.TextColored(new Vector4(0.5f, 0.9f, 0.5f, 1.0f), run.ItemsObtained.ToString());

                                        ImGui.TableNextColumn();
                                        ImGui.Text($"{run.DurationMinutes:F1}m");

                                        ImGui.TableNextColumn();
                                        ImGui.TextDisabled($"{run.ItemIds.Count} unique items");

                                        rank++;
                                    }

                                    ImGui.EndTable();
                                }
                            }

                            ImGui.EndTabItem();
                        }

                        if (ImGui.BeginTabItem("⚡ Fastest"))
                        {
                            ImGui.Spacing();

                            if (!fastestRuns.Any())
                            {
                                ImGui.TextDisabled("No completed runs yet.");
                            }
                            else
                            {
                                var tableHeight = ImGui.GetContentRegionAvail().Y - 20;
                                if (ImGui.BeginTable("FastestRunsTable", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(0, tableHeight)))
                                {
                                    ImGui.TableSetupColumn("Rank", ImGuiTableColumnFlags.WidthFixed, 50);
                                    ImGui.TableSetupColumn("Date/Time", ImGuiTableColumnFlags.WidthFixed, 140);
                                    ImGui.TableSetupColumn("Duration", ImGuiTableColumnFlags.WidthFixed, 80);
                                    ImGui.TableSetupColumn("Items", ImGuiTableColumnFlags.WidthFixed, 70);
                                    ImGui.TableSetupColumn("Item IDs", ImGuiTableColumnFlags.WidthStretch);
                                    ImGui.TableSetupScrollFreeze(0, 1);
                                    ImGui.TableHeadersRow();

                                    int rank = 1;
                                    foreach (var run in fastestRuns)
                                    {
                                        ImGui.TableNextRow();
                                        ImGui.TableNextColumn();
                                        var rankColor = rank switch
                                        {
                                            1 => new Vector4(1.0f, 0.84f, 0.0f, 1.0f),
                                            2 => new Vector4(0.75f, 0.75f, 0.75f, 1.0f),
                                            3 => new Vector4(0.8f, 0.5f, 0.2f, 1.0f),
                                            _ => new Vector4(1.0f, 1.0f, 1.0f, 1.0f)
                                        };
                                        ImGui.TextColored(rankColor, $"#{rank}");

                                        ImGui.TableNextColumn();
                                        ImGui.Text(run.StartedAt.ToString("MM/dd HH:mm"));

                                        ImGui.TableNextColumn();
                                        ImGui.TextColored(new Vector4(0.3f, 1.0f, 0.3f, 1.0f), $"{run.DurationMinutes:F1}m");

                                        ImGui.TableNextColumn();
                                        ImGui.Text(run.ItemsObtained.ToString());

                                        ImGui.TableNextColumn();
                                        ImGui.TextDisabled($"{run.ItemIds.Count} unique items");

                                        rank++;
                                    }

                                    ImGui.EndTable();
                                }
                            }

                            ImGui.EndTabItem();
                        }

                        ImGui.EndTabBar();
                    }
                }
                else
                {
                    ImGui.TextDisabled("Select a duty from the Overview tab to see best runs.");
                }

                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawExportTab()
    {
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.3f, 0.8f, 1.0f, 1.0f), "Export History");
        ImGui.Spacing();

        var history = plugin.HistoryService.GetHistory();
        ImGui.Text($"Total items in history: {history.TotalItemsObtained:N0}");
        ImGui.Text($"Days tracked: {history.DailyStatistics.Count}");
        ImGui.Text($"Last updated: {history.LastUpdated:yyyy-MM-dd HH:mm:ss}");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextWrapped("Export your loot history to external files for backup or analysis in spreadsheet applications.");
        ImGui.Spacing();

        if (ImGui.Button("Export to JSON", new Vector2(200, 30)))
        {
            var path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                $"LootView_History_{DateTime.Now:yyyyMMdd_HHmmss}.json"
            );
            plugin.HistoryService.ExportToJson(path);
            Plugin.ChatGui.Print($"History exported to: {path}");
        }

        ImGui.SameLine();
        ImGui.TextDisabled("Full history with all details");

        if (ImGui.Button("Export to CSV", new Vector2(200, 30)))
        {
            var path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                $"LootView_History_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            );
            plugin.HistoryService.ExportToCsv(path);
            Plugin.ChatGui.Print($"History exported to: {path}");
        }

        ImGui.SameLine();
        ImGui.TextDisabled("Spreadsheet-friendly format");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Data Management
        ImGui.TextColored(new Vector4(1.0f, 0.5f, 0.3f, 1.0f), "Data Management");
        ImGui.Spacing();

        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.5f, 0.9f, 0.5f, 1.0f));
        ImGui.TextWrapped("ℹ History is kept forever unless you manually delete it.");
        ImGui.PopStyleColor();
        ImGui.Spacing();
        
        ImGui.TextWrapped("Clean up old data to reduce file size. This will permanently remove items older than the specified number of days.");
        ImGui.Spacing();

        var retentionDays = plugin.Configuration.HistoryRetentionDays;
        ImGui.SetNextItemWidth(150);
        if (ImGui.SliderInt("Days to keep", ref retentionDays, 7, 365))
        {
            plugin.Configuration.HistoryRetentionDays = retentionDays;
            plugin.ConfigService.Save();
        }

        if (ImGui.Button("Clean Old History", new Vector2(200, 30)))
        {
            plugin.HistoryService.ClearOldHistory(retentionDays);
            RefreshStatistics();
        }

        ImGui.Spacing();
        ImGui.Spacing();

        // Danger zone
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.2f, 0.2f, 1.0f));
        ImGui.TextWrapped("⚠ DANGER ZONE ⚠");
        ImGui.PopStyleColor();
        ImGui.TextWrapped("This will permanently delete ALL history. This action cannot be undone!");
        ImGui.Spacing();

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.8f, 0.2f, 0.2f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(1.0f, 0.3f, 0.3f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.6f, 0.1f, 0.1f, 1.0f));
        
        if (ImGui.Button("Clear All History", new Vector2(200, 30)))
        {
            ImGui.OpenPopup("ConfirmClear");
        }
        
        ImGui.PopStyleColor(3);

        // Confirmation popup
        var confirmOpen = true;
        if (ImGui.BeginPopupModal("ConfirmClear", ref confirmOpen, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("Are you absolutely sure you want to delete ALL history?");
            ImGui.Text("This action cannot be undone!");
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            if (ImGui.Button("Yes, Delete Everything", new Vector2(200, 0)))
            {
                plugin.HistoryService.ClearAllHistory();
                RefreshStatistics();
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(100, 0)))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private void RefreshStatistics()
    {
        DateTime? start = dateRangeOption == 3 ? null : statsStartDate;
        DateTime? end = dateRangeOption == 3 ? null : statsEndDate;
        
        cachedStats = plugin.HistoryService.CalculateStatistics(start, end);
        lastStatsUpdate = DateTime.Now;
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
            case 3: // All Time
                // Will use null in the query
                break;
        }
        RefreshStatistics();
    }

    private void DrawStatBox(string label, string value, Vector4 color)
    {
        var availWidth = ImGui.GetContentRegionAvail().X / 4 - 10;
        
        ImGui.BeginChild($"##{label}StatBox", new Vector2(availWidth, 60), true);
        ImGui.TextColored(color, value);
        ImGui.TextDisabled(label);
        ImGui.EndChild();
    }

    private void DrawItemIcon(uint iconId)
    {
        var icon = Plugin.TextureProvider.GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(iconId)).GetWrapOrDefault();
        if (icon != null)
        {
            ImGui.Image(icon.Handle, new Vector2(24, 24));
        }
    }

    private Vector4 GetRarityColor(uint rarity)
    {
        return rarity switch
        {
            1 => new Vector4(1.0f, 1.0f, 1.0f, 1.0f),      // Common - White
            2 => new Vector4(0.5f, 1.0f, 0.5f, 1.0f),      // Uncommon - Green
            3 => new Vector4(0.4f, 0.7f, 1.0f, 1.0f),      // Rare - Blue
            4 => new Vector4(0.8f, 0.4f, 1.0f, 1.0f),      // Rare+ - Purple
            7 => new Vector4(1.0f, 0.5f, 0.8f, 1.0f),      // Legendary/Relic - Pink
            _ => new Vector4(0.7f, 0.7f, 0.7f, 1.0f)       // Unknown - Gray
        };
    }

    private string GetRarityName(uint rarity)
    {
        return rarity switch
        {
            1 => "Common",
            2 => "Uncommon",
            3 => "Rare",
            4 => "Rare+",
            7 => "Relic",
            _ => "Unknown"
        };
    }

    private void DrawZoneFinderTab()
    {
        ImGui.TextWrapped("Search for dungeons, trials, and raids by name to view their loot tables.");
        ImGui.Spacing();
        
        // Disclaimer box
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.2f, 0.3f, 0.4f, 0.3f));
        if (ImGui.BeginChild("DisclaimerBox", new Vector2(0, 80), true))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.9f, 0.5f, 1.0f));
            ImGui.Text("ℹ️ Supported Content:");
            ImGui.PopStyleColor();
            
            ImGui.Spacing();
            ImGui.TextWrapped("✓ Dungeons  ✓ Trials  ✓ Raids (Normal/Savage/Ultimate)");
            ImGui.TextWrapped("This feature works best with instanced battle content (dungeons, trials, raids). Overworld zones and some special content may not have loot table data available.");
            
            ImGui.EndChild();
        }
        ImGui.PopStyleColor();
        
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Search box
        ImGui.Text("Search Zone:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(300);
        if (ImGui.InputText("##ZoneSearch", ref zoneSearchQuery, 100, ImGuiInputTextFlags.EnterReturnsTrue))
        {
            PerformZoneSearch();
        }
        ImGui.SameLine();
        if (ImGui.Button("Search"))
        {
            PerformZoneSearch();
        }
        
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Search results
        if (zoneSearchPerformed && zoneSearchResults.Count == 0)
        {
            ImGui.TextColored(new Vector4(1, 0.7f, 0, 1), "No zones found matching your search.");
            ImGui.TextDisabled("Try searching for: Sastasha, Titan, Alexander, etc.");
        }
        else if (zoneSearchResults.Count > 0)
        {
            ImGui.Text($"Found {zoneSearchResults.Count} zone(s):");
            ImGui.Spacing();

            // Results table
            if (ImGui.BeginTable("ZoneSearchResultsTable", 4, 
                ImGuiTableFlags.Borders | 
                ImGuiTableFlags.RowBg | 
                ImGuiTableFlags.ScrollY,
                new Vector2(0, 300)))
            {
                ImGui.TableSetupColumn("Zone Name", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 120);
                ImGui.TableSetupColumn("Level", ImGuiTableColumnFlags.WidthFixed, 60);
                ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 120);
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableHeadersRow();

                foreach (var result in zoneSearchResults)
                {
                    ImGui.TableNextRow();

                    ImGui.TableSetColumnIndex(0);
                    ImGui.Text(result.Name);

                    ImGui.TableSetColumnIndex(1);
                    ImGui.TextColored(new Vector4(0.7f, 0.9f, 1.0f, 1), result.ContentType);

                    ImGui.TableSetColumnIndex(2);
                    if (result.ItemLevel > 0)
                    {
                        ImGui.Text($"i{result.ItemLevel}");
                    }

                    ImGui.TableSetColumnIndex(3);
                    if (ImGui.Button($"View Loot##{result.ContentFinderConditionId}"))
                    {
                        selectedZoneForLootTable = result.TerritoryId;
                        selectedZoneName = result.Name;
                        plugin.LootTableWindow.IsOpen = true;
                        LoadZoneLootTable(result.ContentFinderConditionId);
                    }
                }

                ImGui.EndTable();
            }
        }
        else
        {
            ImGui.TextDisabled("Enter a zone name and click Search to find dungeons, trials, and raids.");
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
            // Skip invalid entries
            if (cfc.RowId == 0 || cfc.Content.RowId == 0) continue;
            if (string.IsNullOrEmpty(cfc.Name.ToString())) continue;

            var name = cfc.Name.ToString();
            
            // Search by name
            if (name.ToLower().Contains(query))
            {
                var result = new ZoneSearchResult
                {
                    ContentFinderConditionId = cfc.RowId,
                    TerritoryId = cfc.TerritoryType.RowId,
                    Name = name,
                    ContentType = GetContentTypeName(cfc.ContentType.RowId),
                    ItemLevel = cfc.ItemLevelRequired
                };

                zoneSearchResults.Add(result);
            }
        }

        // Sort by name
        zoneSearchResults = zoneSearchResults.OrderBy(r => r.Name).ToList();
        
        Plugin.Log.Info($"Zone search for '{zoneSearchQuery}' found {zoneSearchResults.Count} results");
    }

    private string GetContentTypeName(uint contentTypeId)
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
        // This will trigger the loot table window to load the specific zone
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
        // Cleanup if needed
    }
}

