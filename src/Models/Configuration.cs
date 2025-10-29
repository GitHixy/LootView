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
    public bool LockWindowPosition { get; set; } = false;
    public bool LockWindowSize { get; set; } = false;
    public int MaxDisplayedItems { get; set; } = 50;

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

    // Colors (stored as packed RGBA values)
    public uint OwnLootColor { get; set; } = 0xFF00FF00; // Green
    public uint PartyLootColor { get; set; } = 0xFF0080FF; // Blue
    public uint RareLootColor { get; set; } = 0xFFFF8000; // Orange
    public uint LegendaryLootColor { get; set; } = 0xFFFF0080; // Pink
}