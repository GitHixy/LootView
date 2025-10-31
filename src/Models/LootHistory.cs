using System;
using System.Collections.Generic;

namespace LootView.Models;

/// <summary>
/// Persistent storage for loot history across sessions
/// </summary>
[Serializable]
public class LootHistory
{
    /// <summary>
    /// Version of the history format
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// All loot items ever obtained (full history)
    /// </summary>
    public List<LootItem> AllItems { get; set; } = new();

    /// <summary>
    /// Daily statistics aggregated by date
    /// </summary>
    public Dictionary<DateTime, DailyStats> DailyStatistics { get; set; } = new();

    /// <summary>
    /// All duty/dungeon runs tracked
    /// </summary>
    public List<DutyRun> DutyRuns { get; set; } = new();

    /// <summary>
    /// When this history was last updated
    /// </summary>
    public DateTime LastUpdated { get; set; } = DateTime.Now;

    /// <summary>
    /// Total number of items ever obtained
    /// </summary>
    public int TotalItemsObtained => AllItems.Count;
}

/// <summary>
/// Statistics for a single day
/// </summary>
[Serializable]
public class DailyStats
{
    /// <summary>
    /// The date for these statistics
    /// </summary>
    public DateTime Date { get; set; }

    /// <summary>
    /// Total items obtained on this day
    /// </summary>
    public int TotalItems { get; set; }

    /// <summary>
    /// Items by rarity
    /// </summary>
    public Dictionary<uint, int> ItemsByRarity { get; set; } = new();

    /// <summary>
    /// Items by zone
    /// </summary>
    public Dictionary<string, int> ItemsByZone { get; set; } = new();

    /// <summary>
    /// Most valuable item (by rarity) obtained this day
    /// </summary>
    public LootItem MostValuableItem { get; set; }

    /// <summary>
    /// Total HQ items obtained
    /// </summary>
    public int HQItemCount { get; set; }

    /// <summary>
    /// Play time in minutes (estimated from first to last loot)
    /// </summary>
    public int EstimatedPlayMinutes { get; set; }
}
