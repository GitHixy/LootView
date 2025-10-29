using System;

namespace LootView.Models;

/// <summary>
/// Represents a loot item that was obtained
/// </summary>
public class LootItem
{
    /// <summary>
    /// Unique identifier for this loot event
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The item ID from the game data
    /// </summary>
    public uint ItemId { get; set; }

    /// <summary>
    /// The item name
    /// </summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>
    /// The item icon ID
    /// </summary>
    public uint IconId { get; set; }

    /// <summary>
    /// The item quality/rarity
    /// </summary>
    public uint Rarity { get; set; }

    /// <summary>
    /// The quantity obtained
    /// </summary>
    public uint Quantity { get; set; }

    /// <summary>
    /// High Quality flag
    /// </summary>
    public bool IsHQ { get; set; }

    /// <summary>
    /// When this item was obtained
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>
    /// Name of the player who obtained this item
    /// </summary>
    public string PlayerName { get; set; } = string.Empty;

    /// <summary>
    /// Content ID of the player who obtained this item
    /// </summary>
    public ulong PlayerContentId { get; set; }

    /// <summary>
    /// Whether this item was obtained by the local player
    /// </summary>
    public bool IsOwnLoot { get; set; }

    /// <summary>
    /// The source of this loot (monster, chest, gathering, etc.)
    /// </summary>
    public LootSource Source { get; set; }

    /// <summary>
    /// The territory where this item was obtained
    /// </summary>
    public ushort TerritoryType { get; set; }

    /// <summary>
    /// The zone name where this item was obtained
    /// </summary>
    public string ZoneName { get; set; } = string.Empty;
}

/// <summary>
/// Source types for loot
/// </summary>
public enum LootSource
{
    Unknown,
    Monster,
    Chest,
    Gathering,
    Quest,
    Crafting,
    Purchase,
    Other
}