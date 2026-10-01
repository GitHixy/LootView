using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace LootView.Services;

/// <summary>One currency the player holds, as of the last refresh.</summary>
public sealed class CurrencyEntry
{
    public uint ItemId { get; init; }
    public string Name { get; init; } = string.Empty;
    public uint IconId { get; init; }

    /// <summary>Player commendations: not an item, so drawn with a glyph instead of a game icon.</summary>
    public bool IsCommendation { get; init; }

    public long Count { get; set; }

    /// <summary>The most the player can hold, or 0 when the cap is too large to matter (gil, MGP).</summary>
    public long Cap { get; set; }

    /// <summary>Earned this week and the weekly limit, for the capped tomestone. Both 0 otherwise.</summary>
    public long Weekly { get; set; }
    public long WeeklyCap { get; set; }

    /// <summary>How many more the game will still let the player earn, for limited currencies. -1 when not limited.</summary>
    public long Remaining { get; set; } = -1;

    /// <summary>The last gain, and when it happened, so the panel can flash it.</summary>
    public long LastGain { get; set; }
    public DateTime GainedAt { get; set; } = DateTime.MinValue;
}

/// <summary>
/// Reads every currency the player has straight from the game, rather than from a fixed list:
/// the Currency inventory (gil, seals, tomestones, MGP, PvP marks...) and the game's currency
/// manager (scrips, tribal currencies, ventures, field-operation currencies). Anything new a
/// patch adds shows up without an update.
/// </summary>
public sealed unsafe class CurrencyService
{
    /// <summary>Caps at or above this are the game's overflow guard, not a limit worth showing.</summary>
    private const long MeaningfulCapLimit = 1_000_000;

    private const uint GilItemId = 1;

    /// <summary>Key for player commendations, which have no item id of their own.</summary>
    public const uint CommendationsId = uint.MaxValue;

    /// <summary>Storm, Serpent and Flame Seals are items 20-22, in Grand Company order (1-3).</summary>
    private const uint SealItemOffset = 19;

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(500);

    private readonly Dictionary<uint, (string Name, uint IconId, long StackSize)> itemInfo = new();
    private readonly Dictionary<uint, CurrencyEntry> byId = new();
    private readonly List<CurrencyEntry> entries = new();
    private Dictionary<uint, bool>? weeklyTomestones;
    private DateTime lastRefresh = DateTime.MinValue;
    private ulong contentId;

    /// <summary>Currencies the player holds at least one of, gil first, in the game's own order.</summary>
    public IReadOnlyList<CurrencyEntry> Entries => entries;

    /// <summary>Skips the throttle on the next <see cref="Update"/>, e.g. right after a drop.</summary>
    public void RequestRefresh() => lastRefresh = DateTime.MinValue;

    public void Update()
    {
        var now = DateTime.Now;
        if (now - lastRefresh < RefreshInterval) return;
        lastRefresh = now;

        try
        {
            Refresh(now);
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Failed to read currencies");
        }
    }

    private void Refresh(DateTime now)
    {
        var inventory = InventoryManager.Instance();
        var currencyManager = CurrencyManager.Instance();
        if (inventory == null) return;

        // A different character means different wallets: start over instead of flashing the
        // whole difference as a gain.
        var player = Plugin.PlayerState.ContentId;
        if (player != contentId)
        {
            contentId = player;
            byId.Clear();
        }

        weeklyTomestones ??= LoadWeeklyTomestones();

        var seen = new List<CurrencyEntry>();
        var seenIds = new HashSet<uint>();

        void Add(uint itemId, long count, long cap, long remaining = -1)
        {
            if (itemId == 0 || !seenIds.Add(itemId)) return;
            if (count <= 0 && itemId != GilItemId) return;

            var isCommendation = itemId == CommendationsId;
            var info = isCommendation ? (Name: "Player Commendations", IconId: 0u, StackSize: 0L) : GetItemInfo(itemId);
            if (string.IsNullOrEmpty(info.Name)) return;

            if (cap <= 0) cap = info.StackSize;
            if (cap >= MeaningfulCapLimit || cap <= 1) cap = 0;

            if (!byId.TryGetValue(itemId, out var entry))
            {
                entry = new CurrencyEntry { ItemId = itemId, Name = info.Name, IconId = info.IconId, IsCommendation = isCommendation, Count = count };
                byId[itemId] = entry;
            }
            else if (count > entry.Count)
            {
                // Two gains close together read as one: the flash keeps growing instead of resetting.
                var stillShowing = now - entry.GainedAt < TimeSpan.FromSeconds(4);
                entry.LastGain = (stillShowing ? entry.LastGain : 0) + (count - entry.Count);
                entry.GainedAt = now;
            }

            entry.Count = count;
            entry.Cap = cap;
            entry.Remaining = remaining;

            if (weeklyTomestones.TryGetValue(itemId, out var limited) && limited)
            {
                entry.Weekly = inventory->GetWeeklyAcquiredTomestoneCount();
                entry.WeeklyCap = InventoryManager.GetLimitedTomestoneWeeklyLimit();
            }
            else
            {
                entry.Weekly = entry.WeeklyCap = 0;
            }

            seen.Add(entry);
        }

        Add(GilItemId, inventory->GetGil(), 0);

        var playerState = PlayerState.Instance();
        Add(CommendationsId, playerState->PlayerCommendations, 0);

        // Grand Company seals are capped by rank, not by the item's stack size.
        var grandCompany = playerState->GrandCompany;

        var container = inventory->GetInventoryContainer(InventoryType.Currency);
        if (container != null && container->IsLoaded)
        {
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0) continue;

                var itemId = slot->ItemId;
                long cap = 0;
                if (itemId > SealItemOffset && itemId <= SealItemOffset + 3)
                {
                    var gc = (byte)(itemId - SealItemOffset);
                    cap = gc == grandCompany ? inventory->GetMaxCompanySeals(gc) : 0;
                }

                Add(itemId, slot->Quantity, cap);
            }
        }

        if (currencyManager != null)
        {
            foreach (var pair in currencyManager->SpecialItemBucket)
            {
                var (_, item) = pair;
                AddManaged(currencyManager->GetItemIdBySpecialId(item.SpecialId));
            }

            foreach (var pair in currencyManager->ItemBucket)
            {
                var (itemId, _) = pair;
                AddManaged(itemId);
            }

            foreach (var pair in currencyManager->ContentItemBucket)
            {
                var (itemId, _) = pair;
                AddManaged(itemId);
            }
        }

        void AddManaged(uint itemId)
        {
            if (itemId == 0) return;
            var remaining = currencyManager->IsItemLimited(itemId) ? currencyManager->GetItemCountRemaining(itemId) : -1L;
            Add(itemId, currencyManager->GetItemCount(itemId), currencyManager->GetItemMaxCount(itemId), remaining);
        }

        entries.Clear();
        entries.AddRange(seen);
    }

    private (string Name, uint IconId, long StackSize) GetItemInfo(uint itemId)
    {
        if (itemInfo.TryGetValue(itemId, out var info)) return info;

        info = (string.Empty, 0, 0);
        var sheet = Plugin.DataManager.GetExcelSheet<Item>();
        if (sheet != null && sheet.TryGetRow(itemId, out var row))
        {
            info = (row.Name.ExtractText(), row.Icon, row.StackSize);
        }

        itemInfo[itemId] = info;
        return info;
    }

    /// <summary>The tomestones in circulation, and whether each one has a weekly limit.</summary>
    private static Dictionary<uint, bool> LoadWeeklyTomestones()
    {
        var result = new Dictionary<uint, bool>();
        var sheet = Plugin.DataManager.GetExcelSheet<TomestonesItem>();
        if (sheet == null) return result;

        foreach (var row in sheet)
        {
            if (row.Item.RowId == 0 || row.Tomestones.RowId == 0) continue;
            result[row.Item.RowId] = row.Tomestones.ValueNullable?.WeeklyLimit > 0;
        }

        return result;
    }
}
