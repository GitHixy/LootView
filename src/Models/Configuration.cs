using System;

namespace LootView.Models;

/// <summary>
/// Configuration settings for the LootView plugin
/// </summary>
[Serializable]
public class Configuration
{
    public int Version { get; set; } = 0;

    // Window Settings
    public bool IsVisible { get; set; } = false;
    public bool OpenOnLogin { get; set; } = false;
    public bool ShowOnlyOwnLoot { get; set; } = false;
    public bool ShowOnlyMyLoot { get; set; } = false; // Alias for ShowOnlyOwnLoot for UI consistency
    public bool ShowItemIcons { get; set; } = true;
    public bool ShowTimestamps { get; set; } = true;
    public bool ShowPlayerNames { get; set; } = true;
    public bool ShowZoneNames { get; set; } = true;
    public bool ShowQuantities { get; set; } = true;

    // Window Appearance
    public float WindowOpacity { get; set; } = 1.0f;
    public float BackgroundAlpha { get; set; } = 0.9f; // Background transparency for layouts
    public bool LockWindowPosition { get; set; } = false;
    public bool LockWindowSize { get; set; } = false;
    public int MaxDisplayedItems { get; set; } = 50;
    public LootWindowStyle WindowStyle { get; set; } = LootWindowStyle.Classic;

    // Filtering
    public uint MinimumRarity { get; set; } = 0;
    public bool ShowHQOnly { get; set; } = false;
    public bool AutoHideAfterTime { get; set; } = false;
    public int AutoHideMinutes { get; set; } = 5;

    // Sound & Notifications
    public bool PlaySoundOnLoot { get; set; } = false;
    public bool ShowChatNotifications { get; set; } = false;

    // Advanced
    public bool EnableDebugLogging { get; set; } = false;
    public bool TrackAllPartyLoot { get; set; } = true;
    public bool TrackGatheringLoot { get; set; } = true;
    public bool TrackCraftingLoot { get; set; } = false;
    public bool ShowDtrBar { get; set; } = true; // Show button in server info bar

    // Visual Effects
    public bool ShowTooltips { get; set; } = true;
    public bool EnableParticleEffects { get; set; } = true;
    public float ParticleIntensity { get; set; } = 1.0f; // 0.0 to 2.0, controls particle count

    // History & Statistics
    public bool EnableHistoryTracking { get; set; } = true;
    public bool EnableHistoryAutoSave { get; set; } = true;
    public int HistoryRetentionDays { get; set; } = 90; // Keep history for 90 days by default
    public bool SaveToHistoryOnClear { get; set; } = true; // Save items to history when clearing the list

    // Colors (stored as packed RGBA values)
    public uint OwnLootColor { get; set; } = 0xFF00FF00; // Green
    public uint PartyLootColor { get; set; } = 0xFF0080FF; // Blue
    public uint RareLootColor { get; set; } = 0xFFFF8000; // Orange
    public uint LegendaryLootColor { get; set; } = 0xFFFF0080; // Pink
}

/// <summary>
/// Visual style themes for the loot window
/// </summary>
public enum LootWindowStyle
{
    /// <summary>Classic table layout with icons and text</summary>
    Classic,
    
    /// <summary>Compact single-line entries with minimal spacing</summary>
    Compact,
    
    /// <summary>Cyberpunk/tech aesthetic with neon accents</summary>
    Neon
}