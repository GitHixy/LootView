using System;
using System.Collections.Generic;

namespace LootView.Models;

/// <summary>
/// Represents a single duty/dungeon run
/// </summary>
public class DutyRun
{
    /// <summary>
    /// Content Finder condition ID
    /// </summary>
    public uint ContentId { get; set; }

    /// <summary>
    /// Name of the duty
    /// </summary>
    public string DutyName { get; set; } = string.Empty;

    /// <summary>
    /// Type of content (Dungeon, Raid, Trial, AllianceRaid, etc.)
    /// </summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>
    /// Level of the duty
    /// </summary>
    public byte Level { get; set; }

    /// <summary>
    /// Item level requirement
    /// </summary>
    public ushort ItemLevel { get; set; }

    /// <summary>
    /// When the duty started
    /// </summary>
    public DateTime StartedAt { get; set; }

    /// <summary>
    /// When the duty was completed (null if abandoned/failed)
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Duration in minutes
    /// </summary>
    public double DurationMinutes => CompletedAt.HasValue 
        ? (CompletedAt.Value - StartedAt).TotalMinutes 
        : 0;

    /// <summary>
    /// Whether the duty was successfully completed
    /// </summary>
    public bool Completed { get; set; }

    /// <summary>
    /// Number of items obtained during this run
    /// </summary>
    public int ItemsObtained { get; set; }

    /// <summary>
    /// Total quantity of items
    /// </summary>
    public int TotalQuantity { get; set; }

    /// <summary>
    /// Number of HQ items obtained
    /// </summary>
    public int HQItemsObtained { get; set; }

    /// <summary>
    /// List of item IDs obtained during this run
    /// </summary>
    public List<uint> ItemIds { get; set; } = new();

    /// <summary>
    /// Number of party wipes (if trackable)
    /// </summary>
    public int Wipes { get; set; }

    /// <summary>
    /// Notes about this run
    /// </summary>
    public string Notes { get; set; } = string.Empty;
}

/// <summary>
/// Aggregated statistics for a specific duty
/// </summary>
public class DutyStatistics
{
    /// <summary>
    /// Content Finder condition ID
    /// </summary>
    public uint ContentId { get; set; }

    /// <summary>
    /// Name of the duty
    /// </summary>
    public string DutyName { get; set; } = string.Empty;

    /// <summary>
    /// Type of content
    /// </summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>
    /// Level of the duty
    /// </summary>
    public byte Level { get; set; }

    /// <summary>
    /// Item level requirement
    /// </summary>
    public ushort ItemLevel { get; set; }

    /// <summary>
    /// Total number of times attempted
    /// </summary>
    public int TotalAttempts { get; set; }

    /// <summary>
    /// Number of successful completions
    /// </summary>
    public int Completions { get; set; }

    /// <summary>
    /// Completion rate percentage
    /// </summary>
    public double CompletionRate => TotalAttempts > 0 
        ? (Completions * 100.0 / TotalAttempts) 
        : 0;

    /// <summary>
    /// Total items obtained across all runs
    /// </summary>
    public int TotalItemsObtained { get; set; }

    /// <summary>
    /// Average items per run
    /// </summary>
    public double AverageItemsPerRun => Completions > 0 
        ? (double)TotalItemsObtained / Completions 
        : 0;

    /// <summary>
    /// Total time spent in this duty (minutes)
    /// </summary>
    public double TotalTimeMinutes { get; set; }

    /// <summary>
    /// Average completion time (minutes)
    /// </summary>
    public double AverageTimeMinutes => Completions > 0 
        ? TotalTimeMinutes / Completions 
        : 0;

    /// <summary>
    /// Fastest completion time (minutes)
    /// </summary>
    public double FastestTimeMinutes { get; set; } = double.MaxValue;

    /// <summary>
    /// Slowest completion time (minutes)
    /// </summary>
    public double SlowestTimeMinutes { get; set; }

    /// <summary>
    /// Most items obtained in a single run
    /// </summary>
    public int BestItemsInRun { get; set; }

    /// <summary>
    /// First time this duty was attempted
    /// </summary>
    public DateTime FirstAttempt { get; set; }

    /// <summary>
    /// Last time this duty was attempted
    /// </summary>
    public DateTime LastAttempt { get; set; }

    /// <summary>
    /// Total wipes across all attempts
    /// </summary>
    public int TotalWipes { get; set; }

    /// <summary>
    /// Unique items obtained from this duty
    /// </summary>
    public HashSet<uint> UniqueItems { get; set; } = new();
}
