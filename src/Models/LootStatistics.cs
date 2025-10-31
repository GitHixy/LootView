using System;
using System.Collections.Generic;
using System.Linq;

namespace LootView.Models;

/// <summary>
/// Calculated statistics from loot history
/// </summary>
public class LootStatistics
{
    // Overall Statistics
    public int TotalItems { get; set; }
    public int TotalUnique { get; set; }
    public int TotalHQ { get; set; }
    public int TotalOwnLoot { get; set; }
    public int TotalPartyLoot { get; set; }

    // Rarity Breakdown
    public Dictionary<uint, RarityStats> ByRarity { get; set; } = new();

    // Zone Statistics
    public Dictionary<string, ZoneStats> ByZone { get; set; } = new();

    // Time-based Statistics
    public Dictionary<DateTime, int> DailyItems { get; set; } = new();
    public Dictionary<int, int> HourlyItems { get; set; } = new(); // Hour of day (0-23) -> count

    // Top Items
    public List<ItemFrequency> MostCommonItems { get; set; } = new();
    public List<LootItem> RarestItems { get; set; } = new();
    public List<LootItem> RecentItems { get; set; } = new();

    // Trends
    public double ItemsPerDay { get; set; }
    public double HQPercentage { get; set; }
    public int CurrentStreak { get; set; } // Days with at least one item
    public int LongestStreak { get; set; }

    // Date Range
    public DateTime? FirstItemDate { get; set; }
    public DateTime? LastItemDate { get; set; }
    public int DaysPlayed { get; set; }
}

/// <summary>
/// Statistics for a specific rarity level
/// </summary>
public class RarityStats
{
    public uint Rarity { get; set; }
    public int Count { get; set; }
    public int HQCount { get; set; }
    public double Percentage { get; set; }
    public LootItem LatestItem { get; set; }
}

/// <summary>
/// Statistics for a specific zone
/// </summary>
public class ZoneStats
{
    public string ZoneName { get; set; } = string.Empty;
    public int TotalItems { get; set; }
    public int UniqueItems { get; set; }
    public uint MostCommonRarity { get; set; }
    public DateTime LastVisited { get; set; }
    public LootItem BestItem { get; set; } // Highest rarity
}

/// <summary>
/// Item frequency tracking
/// </summary>
public class ItemFrequency
{
    public uint ItemId { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public uint IconId { get; set; }
    public uint Rarity { get; set; }
    public int Count { get; set; }
    public int HQCount { get; set; }
    public DateTime LastObtained { get; set; }
}
