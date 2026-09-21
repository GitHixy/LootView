using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using LootView.Models;

namespace LootView.Services;

/// <summary>
/// Service for managing persistent loot history and statistics
/// </summary>
public class HistoryService : IDisposable
{
    private readonly ConfigurationService configService;
    private readonly string historyFilePath;
    private LootHistory history = new();
    private readonly object historyLock = new();
    private DateTime lastSave = DateTime.MinValue;
    private bool isDirty = false;
    
    // Statistics cache
    private LootStatistics cachedAllTimeStats = null;
    private DateTime lastAllTimeStatsUpdate = DateTime.MinValue;
    private int lastHistoryCount = 0;

    public HistoryService(ConfigurationService configService)
    {
        this.configService = configService;
        
        // Store history in plugin config directory
        var configDir = Plugin.PluginInterface.ConfigDirectory.FullName;
        historyFilePath = Path.Combine(configDir, "loot_history.json");
        
        LoadHistory();
    }

    /// <summary>
    /// Get read-only access to the full history
    /// </summary>
    public LootHistory GetHistory()
    {
        lock (historyLock)
        {
            return history;
        }
    }

    /// <summary>
    /// A point-in-time copy of every tracked item. Taking the copy under the lock lets
    /// callers filter and sort it on a background thread without racing new drops.
    /// </summary>
    public List<LootItem> SnapshotItems()
    {
        lock (historyLock)
        {
            return history.AllItems.ToList();
        }
    }

    /// <summary>The number of items currently in history, for cheap change detection.</summary>
    public int ItemCount
    {
        get
        {
            lock (historyLock)
            {
                return history.AllItems.Count;
            }
        }
    }

    /// <summary>
    /// Add a loot item to the persistent history
    /// </summary>
    public void AddItem(LootItem item)
    {
        lock (historyLock)
        {
            // Invalidate cached stats when new items are added
            cachedAllTimeStats = null;
            // Add to full history
            history.AllItems.Add(item);
            history.LastUpdated = DateTime.Now;

            // Update daily statistics
            var date = item.Timestamp.Date;
            if (!history.DailyStatistics.ContainsKey(date))
            {
                history.DailyStatistics[date] = new DailyStats
                {
                    Date = date
                };
            }

            var dailyStats = history.DailyStatistics[date];
            dailyStats.TotalItems++;

            // Update rarity stats
            if (!dailyStats.ItemsByRarity.ContainsKey(item.Rarity))
            {
                dailyStats.ItemsByRarity[item.Rarity] = 0;
            }
            dailyStats.ItemsByRarity[item.Rarity]++;

            // Update zone stats
            if (!string.IsNullOrEmpty(item.ZoneName))
            {
                if (!dailyStats.ItemsByZone.ContainsKey(item.ZoneName))
                {
                    dailyStats.ItemsByZone[item.ZoneName] = 0;
                }
                dailyStats.ItemsByZone[item.ZoneName]++;
            }

            // Track HQ items
            if (item.IsHQ)
            {
                dailyStats.HQItemCount++;
            }

            // Update most valuable item
            if (dailyStats.MostValuableItem == null || item.Rarity > dailyStats.MostValuableItem.Rarity)
            {
                dailyStats.MostValuableItem = item;
            }

            isDirty = true;

            // Auto-save periodically (every 5 minutes or when dirty)
            if (configService.Configuration.EnableHistoryAutoSave &&
                (DateTime.Now - lastSave).TotalMinutes >= 5)
            {
                SaveHistory();
            }
        }
    }

    /// <summary>
    /// Add multiple items to history (for bulk operations like clearing current list)
    /// </summary>
    public void AddItems(IEnumerable<LootItem> items)
    {
        foreach (var item in items)
        {
            AddItem(item);
        }
    }

    /// <summary>
    /// Calculate statistics from the history
    /// </summary>
    public LootStatistics CalculateStatistics(DateTime? startDate = null, DateTime? endDate = null)
    {
        List<LootItem> itemList;
        List<DateTime> activeDates;
        int daysPlayed;
        int historyCount;

        // The lock is held only long enough to copy what we need. Aggregating a large
        // history takes a while, and doing that under the lock would stall loot tracking
        // on the game thread for the whole duration.
        lock (historyLock)
        {
            // Use cached "All Time" stats if available and no date filters
            if (!startDate.HasValue && !endDate.HasValue)
            {
                if (cachedAllTimeStats != null && history.AllItems.Count == lastHistoryCount)
                {
                    Plugin.Log.Debug("Using cached all-time statistics");
                    return cachedAllTimeStats;
                }
            }

            var items = history.AllItems.AsEnumerable();

            // Apply date filters
            if (startDate.HasValue)
            {
                items = items.Where(i => i.Timestamp >= startDate.Value);
            }
            if (endDate.HasValue)
            {
                items = items.Where(i => i.Timestamp <= endDate.Value);
            }

            itemList = items.ToList();
            activeDates = history.DailyStatistics.Keys.ToList();
            daysPlayed = history.DailyStatistics.Count;
            historyCount = history.AllItems.Count;
        }

        var stats = BuildStatistics(itemList, activeDates, daysPlayed);

        // Cache the all-time stats
        if (!startDate.HasValue && !endDate.HasValue)
        {
            lock (historyLock)
            {
                cachedAllTimeStats = stats;
                lastHistoryCount = historyCount;
                lastAllTimeStatsUpdate = DateTime.Now;
                Plugin.Log.Debug("Cached all-time statistics ({Count} items)", historyCount);
            }
        }

        return stats;
    }

    /// <summary>
    /// Aggregates a snapshot of loot into statistics. Pure and lock-free, so it is safe
    /// to run on a background thread.
    /// </summary>
    private static LootStatistics BuildStatistics(List<LootItem> itemList, List<DateTime> activeDates, int daysPlayed)
    {
        var stats = new LootStatistics();

        if (itemList.Count == 0)
        {
            return stats;
        }

        // Counted once up front: the rarest-items sort needs a per-item total, and
        // recounting inside the comparer turns that section into an O(n^2) walk.
        var occurrences = new Dictionary<uint, int>();
        foreach (var item in itemList)
        {
            occurrences.TryGetValue(item.ItemId, out var seen);
            occurrences[item.ItemId] = seen + 1;
        }

        // Overall statistics
        stats.TotalItems = itemList.Count;
        stats.TotalUnique = occurrences.Count;
        stats.TotalHQ = itemList.Count(i => i.IsHQ);
        stats.TotalOwnLoot = itemList.Count(i => i.IsOwnLoot);
        stats.TotalPartyLoot = stats.TotalItems - stats.TotalOwnLoot;
        stats.HQPercentage = stats.TotalItems > 0 ? (stats.TotalHQ * 100.0 / stats.TotalItems) : 0;

        // Date range
        stats.FirstItemDate = itemList.Min(i => i.Timestamp);
        stats.LastItemDate = itemList.Max(i => i.Timestamp);
        var daysSpan = (stats.LastItemDate.Value - stats.FirstItemDate.Value).TotalDays + 1;
        stats.DaysPlayed = daysPlayed;
        stats.ItemsPerDay = daysSpan > 0 ? stats.TotalItems / daysSpan : 0;

        // Rarity breakdown
        foreach (var group in itemList.GroupBy(i => i.Rarity))
        {
            var count = group.Count();
            stats.ByRarity[group.Key] = new RarityStats
            {
                Rarity = group.Key,
                Count = count,
                HQCount = group.Count(i => i.IsHQ),
                Percentage = (count * 100.0) / stats.TotalItems,
                LatestItem = group.OrderByDescending(i => i.Timestamp).First()
            };
        }

        // Zone statistics
        var zoneGroups = itemList.Where(i => !string.IsNullOrEmpty(i.ZoneName))
                                 .GroupBy(i => i.ZoneName);
        foreach (var group in zoneGroups)
        {
            stats.ByZone[group.Key] = new ZoneStats
            {
                ZoneName = group.Key,
                TotalItems = group.Count(),
                UniqueItems = group.Select(i => i.ItemId).Distinct().Count(),
                MostCommonRarity = group.GroupBy(i => i.Rarity)
                                       .OrderByDescending(g => g.Count())
                                       .First().Key,
                LastVisited = group.Max(i => i.Timestamp),
                BestItem = group.OrderByDescending(i => i.Rarity).First()
            };
        }

        // Daily items
        foreach (var group in itemList.GroupBy(i => i.Timestamp.Date))
        {
            stats.DailyItems[group.Key] = group.Count();
        }

        // Hourly distribution
        foreach (var group in itemList.GroupBy(i => i.Timestamp.Hour))
        {
            stats.HourlyItems[group.Key] = group.Count();
        }

        // Most common items
        stats.MostCommonItems = itemList.GroupBy(i => i.ItemId)
                                   .Select(g => new ItemFrequency
                                   {
                                       ItemId = g.Key,
                                       ItemName = g.First().ItemName,
                                       IconId = g.First().IconId,
                                       Rarity = g.First().Rarity,
                                       Count = g.Count(),
                                       HQCount = g.Count(i => i.IsHQ),
                                       LastObtained = g.Max(i => i.Timestamp)
                                   })
                                   .OrderByDescending(f => f.Count)
                                   .Take(20)
                                   .ToList();

        // Rarest items (highest rarity, least common)
        stats.RarestItems = itemList
            .OrderByDescending(i => i.Rarity)
            .ThenBy(i => occurrences[i.ItemId])
            .Take(20)
            .ToList();

        // Recent items
        stats.RecentItems = itemList
            .OrderByDescending(i => i.Timestamp)
            .Take(50)
            .ToList();

        // Streaks
        CalculateStreaks(stats, activeDates);

        return stats;
    }

    private static void CalculateStreaks(LootStatistics stats, List<DateTime> activeDates)
    {
        var dates = activeDates.OrderBy(d => d).ToList();
        if (dates.Count == 0)
        {
            return;
        }

        // The streak walks probe day by day, so membership has to be O(1), not a list scan.
        var dateSet = new HashSet<DateTime>(dates);

        int currentStreak = 0;
        int longestStreak = 0;
        int tempStreak = 1;

        // Check if today or yesterday has items (current streak)
        var today = DateTime.Now.Date;
        var yesterday = today.AddDays(-1);
        
        if (dateSet.Contains(today))
        {
            currentStreak = 1;
            var checkDate = yesterday;
            while (dateSet.Contains(checkDate))
            {
                currentStreak++;
                checkDate = checkDate.AddDays(-1);
            }
        }
        else if (dateSet.Contains(yesterday))
        {
            currentStreak = 1;
            var checkDate = yesterday.AddDays(-1);
            while (dateSet.Contains(checkDate))
            {
                currentStreak++;
                checkDate = checkDate.AddDays(-1);
            }
        }

        // Calculate longest streak
        for (int i = 1; i < dates.Count; i++)
        {
            if ((dates[i] - dates[i - 1]).Days == 1)
            {
                tempStreak++;
            }
            else
            {
                longestStreak = Math.Max(longestStreak, tempStreak);
                tempStreak = 1;
            }
        }
        longestStreak = Math.Max(longestStreak, tempStreak);

        stats.CurrentStreak = currentStreak;
        stats.LongestStreak = longestStreak;
    }

    /// <summary>
    /// Export history to JSON file
    /// </summary>
    public void ExportToJson(string filePath)
    {
        lock (historyLock)
        {
            var json = JsonSerializer.Serialize(history, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(filePath, json);
            Plugin.Log.Info($"Exported history to {filePath}");
        }
    }

    /// <summary>
    /// Export statistics to CSV file
    /// </summary>
    public void ExportToCsv(string filePath)
    {
        lock (historyLock)
        {
            using var writer = new StreamWriter(filePath);
            
            // Header
            writer.WriteLine("Timestamp,ItemId,ItemName,Rarity,Quantity,IsHQ,PlayerName,IsOwnLoot,ZoneName,Source");
            
            // Data rows
            foreach (var item in history.AllItems.OrderBy(i => i.Timestamp))
            {
                writer.WriteLine($"{item.Timestamp:yyyy-MM-dd HH:mm:ss},{item.ItemId},\"{item.ItemName}\",{item.Rarity},{item.Quantity},{item.IsHQ},\"{item.PlayerName}\",{item.IsOwnLoot},\"{item.ZoneName}\",{item.Source}");
            }
            
            Plugin.Log.Info($"Exported history to CSV: {filePath}");
        }
    }

    /// <summary>
    /// Clear history older than specified days
    /// </summary>
    public void ClearOldHistory(int daysToKeep)
    {
        lock (historyLock)
        {
            var cutoffDate = DateTime.Now.AddDays(-daysToKeep);
            
            history.AllItems.RemoveAll(i => i.Timestamp < cutoffDate);
            
            var oldDates = history.DailyStatistics.Keys.Where(d => d < cutoffDate).ToList();
            foreach (var date in oldDates)
            {
                history.DailyStatistics.Remove(date);
            }
            
            isDirty = true;
            SaveHistory();
            
            Plugin.Log.Info($"Cleared history older than {daysToKeep} days");
        }
    }

    /// <summary>
    /// Clear all history (with confirmation)
    /// </summary>
    public void ClearAllHistory()
    {
        lock (historyLock)
        {
            history = new LootHistory();
            isDirty = true;
            SaveHistory();
            Plugin.Log.Info("All history cleared");
        }
    }

    /// <summary>
    /// Save history to disk
    /// </summary>
    public void SaveHistory()
    {
        lock (historyLock)
        {
            try
            {
                var json = JsonSerializer.Serialize(history, new JsonSerializerOptions
                {
                    WriteIndented = false // Compact for storage
                });
                
                File.WriteAllText(historyFilePath, json);
                lastSave = DateTime.Now;
                isDirty = false;
                
                Plugin.Log.Debug($"History saved: {history.AllItems.Count} items");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Failed to save history");
            }
        }
    }

    /// <summary>
    /// Load history from disk
    /// </summary>
    private void LoadHistory()
    {
        try
        {
            if (File.Exists(historyFilePath))
            {
                var json = File.ReadAllText(historyFilePath);
                var loaded = JsonSerializer.Deserialize<LootHistory>(json);
                
                if (loaded != null)
                {
                    history = loaded;
                    Plugin.Log.Info($"Loaded history: {history.AllItems.Count} items, {history.DailyStatistics.Count} days");
                }
            }
            else
            {
                Plugin.Log.Info("No existing history file found, starting fresh");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to load history, starting fresh");
            history = new LootHistory();
        }
    }

    #region Duty Tracking

    /// <summary>
    /// Start tracking a new duty run
    /// </summary>
    public void TrackDutyStart(uint contentId, string dutyName, string contentType, byte level, ushort itemLevel)
    {
        lock (historyLock)
        {
            // Check if there's an incomplete run, complete it first
            var lastRun = history.DutyRuns.LastOrDefault();
            if (lastRun != null && lastRun.CompletedAt == null)
            {
                lastRun.CompletedAt = DateTime.Now;
                lastRun.Completed = false; // Mark as incomplete/abandoned
            }

            // Add new duty run
            history.DutyRuns.Add(new DutyRun
            {
                ContentId = contentId,
                DutyName = dutyName,
                ContentType = contentType,
                Level = level,
                ItemLevel = itemLevel,
                StartedAt = DateTime.Now,
                ItemsObtained = 0,
                TotalQuantity = 0,
                HQItemsObtained = 0
            });

            isDirty = true;
        }
    }

    /// <summary>
    /// Mark current duty as completed (successfully)
    /// This should be called when we detect the duty was actually completed
    /// </summary>
    public void TrackDutyComplete()
    {
        lock (historyLock)
        {
            var lastRun = history.DutyRuns.LastOrDefault();
            if (lastRun != null && lastRun.CompletedAt == null)
            {
                lastRun.CompletedAt = DateTime.Now;
                lastRun.Completed = true;
                isDirty = true;
            }
        }
    }

    /// <summary>
    /// End the current duty run (on duty leave)
    /// Simply marks the duty as ended
    /// </summary>
    public void EndCurrentDuty()
    {
        lock (historyLock)
        {
            var lastRun = history.DutyRuns.LastOrDefault();
            if (lastRun != null && lastRun.CompletedAt == null)
            {
                lastRun.CompletedAt = DateTime.Now;
                lastRun.Completed = true;
                isDirty = true;
            }
        }
    }

    /// <summary>
    /// Increment item count for current duty run
    /// </summary>
    public void IncrementDutyItemCount(uint itemId, uint quantity, bool isHQ)
    {
        lock (historyLock)
        {
            var lastRun = history.DutyRuns.LastOrDefault();
            if (lastRun != null && lastRun.CompletedAt == null)
            {
                lastRun.ItemsObtained++;
                lastRun.TotalQuantity += (int)quantity;
                if (isHQ) lastRun.HQItemsObtained++;
                if (!lastRun.ItemIds.Contains(itemId))
                {
                    lastRun.ItemIds.Add(itemId);
                }
                isDirty = true;
            }
        }
    }

    /// <summary>
    /// Calculate statistics for all duties
    /// </summary>
    public Dictionary<uint, DutyStatistics> CalculateDutyStatistics()
    {
        // Copy the runs, then aggregate outside the lock - same reasoning as
        // CalculateStatistics: this can run on a background thread and must not
        // hold up loot tracking on the game thread.
        List<DutyRun> runsSnapshot;
        lock (historyLock)
        {
            runsSnapshot = history.DutyRuns.ToList();
        }

        {
            var dutyStats = new Dictionary<uint, DutyStatistics>();

            var groupedRuns = runsSnapshot
                .Where(r => r.ContentId > 0)
                .GroupBy(r => r.ContentId);

            foreach (var group in groupedRuns)
            {
                var runs = group.ToList();
                var completedRuns = runs.Where(r => r.Completed).ToList();

                var stats = new DutyStatistics
                {
                    ContentId = group.Key,
                    DutyName = runs.First().DutyName,
                    ContentType = runs.First().ContentType,
                    Level = runs.First().Level,
                    ItemLevel = runs.First().ItemLevel,
                    TotalAttempts = runs.Count,
                    Completions = completedRuns.Count,
                    TotalItemsObtained = completedRuns.Sum(r => r.ItemsObtained),
                    TotalTimeMinutes = completedRuns.Sum(r => r.DurationMinutes),
                    FirstAttempt = runs.Min(r => r.StartedAt),
                    LastAttempt = runs.Max(r => r.StartedAt),
                    TotalWipes = runs.Sum(r => r.Wipes)
                };

                if (completedRuns.Any())
                {
                    // Filter out runs with unrealistic durations (< 1 minute) for best/worst time calculations
                    var validDurations = completedRuns.Where(r => r.DurationMinutes >= 1.0).ToList();
                    
                    if (validDurations.Any())
                    {
                        stats.FastestTimeMinutes = validDurations.Min(r => r.DurationMinutes);
                        stats.SlowestTimeMinutes = validDurations.Max(r => r.DurationMinutes);
                    }
                    
                    stats.BestItemsInRun = completedRuns.Max(r => r.ItemsObtained);
                }

                // Collect unique items
                foreach (var run in completedRuns)
                {
                    foreach (var itemId in run.ItemIds)
                    {
                        stats.UniqueItems.Add(itemId);
                    }
                }

                dutyStats[group.Key] = stats;
            }

            return dutyStats;
        }
    }

    /// <summary>
    /// Get best runs for a specific duty
    /// </summary>
    public List<DutyRun> GetBestRuns(uint contentId, int count = 10)
    {
        lock (historyLock)
        {
            return history.DutyRuns
                .Where(r => r.ContentId == contentId && r.Completed)
                .OrderByDescending(r => r.ItemsObtained)
                .ThenBy(r => r.DurationMinutes)
                .Take(count)
                .ToList();
        }
    }

    /// <summary>
    /// Get fastest runs for a specific duty
    /// </summary>
    public List<DutyRun> GetFastestRuns(uint contentId, int count = 10)
    {
        lock (historyLock)
        {
            return history.DutyRuns
                .Where(r => r.ContentId == contentId && r.Completed && r.DurationMinutes >= 1.0)
                .OrderBy(r => r.DurationMinutes)
                .Take(count)
                .ToList();
        }
    }

    /// <summary>
    /// Get recent duty runs
    /// </summary>
    public List<DutyRun> GetRecentDutyRuns(int count = 20)
    {
        lock (historyLock)
        {
            return history.DutyRuns
                .OrderByDescending(r => r.StartedAt)
                .Take(count)
                .ToList();
        }
    }

    #endregion

    public void Dispose()
    {
        if (isDirty)
        {
            SaveHistory();
        }
    }
}
