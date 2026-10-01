using System;
using System.Collections.Generic;

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
    public bool ShowOnDutyStart { get; set; } = false;
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
    /// <summary>Scale of the loot window's contents: text, rows, icons and buttons. 1 is the original size.</summary>
    public float LootWindowScale { get; set; } = 1.0f;
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
    /// <summary>
    /// Fall back to the old reader that parses English chat text. Only honoured on English
    /// clients; everyone else always uses the game's log messages.
    /// </summary>
    public bool UseLegacyChatReader { get; set; } = false;
    public bool TrackAllPartyLoot { get; set; } = true;
    public bool TrackGatheringLoot { get; set; } = true;
    public bool TrackCraftingLoot { get; set; } = false;
    public bool EnableRollTracking { get; set; } = true;
    /// <summary>How long a resolved item stays in the roll window before it disappears.</summary>
    public int RollResultSeconds { get; set; } = 20;
    /// <summary>Need, Greed and Pass buttons on each open item in the roll window.</summary>
    public bool ShowRollButtons { get; set; } = true;
    /// <summary>Hide the game's own Need/Greed window while LootView's roll buttons can stand in for it.</summary>
    public bool HideNativeRollWindow { get; set; } = true;
    public bool ShowDtrBar { get; set; } = true; // Show button in server info bar

    // Visual Effects
    public bool ShowTooltips { get; set; } = true;
    /// <summary>Mark minions, mounts, cards and other collectibles you already have, or still need.</summary>
    public bool ShowUnlockStatus { get; set; } = true;
    public bool EnableParticleEffects { get; set; } = true;
    /// <summary>A large banner at the top of the screen when you receive a player commendation.</summary>
    public bool ShowCommendationBanner { get; set; } = true;
    /// <summary>Size of the commendation banner. 1 is the original size.</summary>
    public float CommendationBannerScale { get; set; } = 1.0f;
    public float ParticleIntensity { get; set; } = 1.0f; // 0.0 to 2.0, controls particle count

    // History & Statistics
    public bool EnableHistoryTracking { get; set; } = true;
    public bool EnableHistoryAutoSave { get; set; } = true;
    public int HistoryRetentionDays { get; set; } = 90; // Keep history for 90 days by default
    public bool SaveToHistoryOnClear { get; set; } = true; // Save items to history when clearing the list

    // Market prices
    /// <summary>Look up estimated values on Universalis for the player's home world.</summary>
    public bool EnableMarketPrices { get; set; } = true;

    // Currencies
    /// <summary>A drawer beside the loot window with every currency you hold and its cap.</summary>
    public bool ShowCurrencyPanel { get; set; } = true;
    /// <summary>Whether the currency drawer is pulled out.</summary>
    public bool CurrencyPanelOpen { get; set; } = false;
    /// <summary>Which side of the loot window the currency drawer slides out of.</summary>
    public bool CurrencyPanelOnLeft { get; set; } = false;

    // Release notes
    /// <summary>Last plugin version whose changelog the user dismissed.</summary>
    public string LastSeenVersion { get; set; } = string.Empty;

    // Blacklist
    public List<uint> BlacklistedItemIds { get; set; } = new List<uint>(); // Item IDs to not display in LootWindow

    // Colors (stored as packed RGBA values)
    public uint OwnLootColor { get; set; } = 0xFF00FF00; // Green
    public uint PartyLootColor { get; set; } = 0xFF0080FF; // Blue
    public uint RareLootColor { get; set; } = 0xFFFF8000; // Orange
    public uint LegendaryLootColor { get; set; } = 0xFFFF0080; // Pink
}
