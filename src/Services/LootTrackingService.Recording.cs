using System;
using System.Collections.Generic;
using System.Linq;
using LootView.Models;

namespace LootView.Services;

/// <summary>
/// What every loot message boils down to, whichever way it was read: a roll session starting,
/// a lot cast, a roll revealed, an item obtained. Both the English chat parser and the
/// language-independent log message reader feed these.
/// </summary>
public partial class LootTrackingService
{
    /// <summary>An item was added to the loot list: open a roll session for it.</summary>
    private void StartRollSession((uint ItemId, uint IconId, uint Rarity, string Name) itemData)
    {
        lock (rollLock)
        {
            // Always add a new roll session (allows multiple drops of the same item)
            activeRolls.Add(new RollInfo
            {
                ItemName = itemData.Name,
                ItemId = itemData.ItemId,
                IconId = itemData.IconId,
                Rarity = itemData.Rarity,
                RollStartTime = DateTime.Now
            });

            Plugin.Log.Info($"Roll session started for: {itemData.Name} (ID: {itemData.ItemId})");
            Plugin.Log.Info($"Total active roll sessions: {activeRolls.Count}");
        }

        RollsUpdated?.Invoke();
    }

    /// <summary>
    /// Someone cast their lot. The game keeps the choice hidden until the item is resolved, so
    /// the player shows as Decided until then.
    /// </summary>
    private void RecordCastLot(string playerName, (uint ItemId, uint IconId, uint Rarity, string Name) itemData)
    {
        lock (rollLock)
        {
            var rollInfo = activeRolls.FirstOrDefault(r =>
                r.ItemId == itemData.ItemId && !r.IsFinished && FindPlayerKey(r, playerName) == null);

            if (rollInfo == null)
            {
                Plugin.Log.Debug($"No open roll session for {playerName}'s lot on {itemData.Name}");
                return;
            }

            rollInfo.PlayerRolls[playerName] = (RollKind.Decided, 0);
        }

        Plugin.Log.Info($"Lot cast: {playerName} decided on {itemData.Name}");
        RollsUpdated?.Invoke();
    }

    /// <summary>A Need or Greed roll was revealed.</summary>
    private void RecordRoll(string playerName, (uint ItemId, uint IconId, uint Rarity, string Name) itemData, string rollType, int rollValue)
    {
        var itemId = itemData.ItemId;

        lock (rollLock)
        {
            // Find the first roll session for this item that doesn't have this player's roll yet.
            // A pass or "can't roll" placeholder from the same player is replaced by the real roll.
            var rollInfo = activeRolls.FirstOrDefault(r => r.ItemId == itemId && !r.IsFinished && FindRollerKey(r, playerName) == null)
                           ?? activeRolls.FirstOrDefault(r => r.ItemId == itemId && FindRollerKey(r, playerName) == null);

            if (rollInfo == null)
            {
                // No matching session found, create a new one (shouldn't happen if loot list message was received)
                rollInfo = new RollInfo
                {
                    ItemName = itemData.Name,
                    ItemId = itemId,
                    IconId = itemData.IconId,
                    Rarity = itemData.Rarity
                };
                activeRolls.Add(rollInfo);
                Plugin.Log.Warning($"Creating new roll session for {itemData.Name} (loot list message may have been missed)");
            }

            var placeholder = FindPlayerKey(rollInfo, playerName);
            if (placeholder != null)
                rollInfo.PlayerRolls.Remove(placeholder);

            rollInfo.PlayerRolls[playerName] = (rollType, rollValue);
        }

        Plugin.Log.Info($"Roll tracked: {playerName} rolled {rollType} {rollValue} on {itemData.Name}");
        RollsUpdated?.Invoke();
    }

    /// <summary>
    /// Someone obtained an item. With <paramref name="linkRolls"/>, an open roll session for the
    /// item is closed with this player as its winner and their roll attached to the drop.
    /// </summary>
    private void RecordObtain(string playerName, bool isOwnLoot, (uint ItemId, uint IconId, uint Rarity, string Name)? itemData,
        string fallbackName, uint quantity, bool isHQ, LootSource source, bool linkRolls)
    {
        var itemName = itemData?.Name ?? fallbackName;

        var lootItem = new LootItem
        {
            ItemName = itemName,
            ItemId = itemData?.ItemId ?? 0,
            IconId = itemData?.IconId ?? 0,
            Rarity = itemData?.Rarity ?? 1,
            Quantity = quantity,
            IsHQ = isHQ,
            PlayerName = playerName,
            PlayerContentId = isOwnLoot ? Plugin.PlayerState.ContentId : 0,
            IsOwnLoot = isOwnLoot,
            Source = source,
            TerritoryType = (ushort)Plugin.ClientState.TerritoryType,
            ZoneName = GetCurrentZoneName()
        };

        if (linkRolls)
        {
            lock (rollLock)
            {
                var itemId = itemData?.ItemId ?? 0;
                // Prefer the session this player actually rolled on; passes never win.
                var candidates = itemId > 0
                    ? activeRolls.Where(r => r.ItemId == itemId && string.IsNullOrEmpty(r.WinnerName)).ToList()
                    : new List<RollInfo>();
                var rollInfo = candidates.FirstOrDefault(r => FindRollerKey(r, playerName) != null)
                               ?? candidates.FirstOrDefault();

                var winnerRollName = rollInfo == null ? string.Empty : FindRollerKey(rollInfo, playerName) ?? string.Empty;
                if (rollInfo != null && winnerRollName.Length > 0)
                {
                    var roll = rollInfo.PlayerRolls[winnerRollName];
                    lootItem.RollType = roll.RollType;
                    lootItem.RollValue = roll.RollValue;
                    Plugin.Log.Info($"Adding roll info to loot ('{playerName}' -> '{winnerRollName}'): {roll.RollType} {roll.RollValue}");

                    // Don't remove yet - let the RollWindow handle cleanup after display timeout
                    rollInfo.WinnerName = winnerRollName;
                    rollInfo.FinishedAt ??= DateTime.Now;
                    Plugin.Log.Info($"Winner marked: {winnerRollName} won {itemName}");

                    RollsUpdated?.Invoke();
                }
            }
        }

        AddLootItem(lootItem);
    }
}
