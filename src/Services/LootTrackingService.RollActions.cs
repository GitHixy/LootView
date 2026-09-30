#nullable enable

using System;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace LootView.Services;

/// <summary>A choice you can make on an item up for rolls. The values are the game's own.</summary>
public enum RollChoice : uint
{
    Need = 1,  // RollResult.Needed
    Greed = 2, // RollResult.Greeded
    Pass = 5,  // RollResult.Passed
}

/// <summary>Which choices the game currently accepts from you on an item.</summary>
public readonly record struct RollOptions(bool CanNeed, bool CanGreed, bool CanPass, string? NeedBlockedReason)
{
    public bool Any => CanNeed || CanGreed || CanPass;

    public bool Allows(RollChoice choice) => choice switch
    {
        RollChoice.Need => CanNeed,
        RollChoice.Greed => CanGreed,
        _ => CanPass,
    };
}

/// <summary>
/// Rolling Need, Greed or Pass straight from the roll window. The game's own roll function is
/// called with the same arguments the Need/Greed window uses, one roll per click, and only for
/// choices the game's loot list says are open to you.
/// </summary>
public partial class LootTrackingService
{
    /// <summary>
    /// RollItem(Loot*, RollResult option, uint slot): checks the slot is below 16, then sends the
    /// roll for that slot's chest and item index. The game has two byte-identical copies; either works.
    /// </summary>
    private const string RollItemSignature = "41 83 F8 ?? 0F 83 ?? ?? ?? ?? 48 89 5C 24 08";

    /// <summary>How long a click waits for the game to confirm before the buttons come back.</summary>
    public const double RollPendingSeconds = 5;

    private unsafe delegate* unmanaged<Loot*, RollResult, uint, void> rollItem;

    /// <summary>Whether the roll function was found; without it the window can't roll for you.</summary>
    public unsafe bool CanRollFromWindow => rollItem != null;

    private unsafe void InitializeRollActions()
    {
        try
        {
            if (Plugin.SigScanner.TryScanText(RollItemSignature, out var address))
            {
                rollItem = (delegate* unmanaged<Loot*, RollResult, uint, void>)address;
                Plugin.Log.Info($"Roll function found at {address:X}");
            }
            else
            {
                Plugin.Log.Warning("Roll function not found - Need/Greed/Pass buttons are disabled until LootView is updated");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to look up the roll function");
        }
    }

    /// <summary>
    /// What the game will accept from you on this item right now, read from its loot list.
    /// Empty once you've chosen, when the item is gone, or when you aren't allowed to roll.
    /// </summary>
    public unsafe RollOptions GetRollOptions(RollInfo roll)
    {
        if (rollItem == null || roll.IsFinished || roll.LootSlot < 0 || roll.LootSlot >= LootSlots)
            return default;

        var loot = Loot.Instance();
        if (loot == null)
            return default;

        ref var item = ref loot->Items[roll.LootSlot];
        if (NormalizeItemId(item.ItemId) != roll.ItemId || item.RollResult != RollResult.UnAwarded)
            return default;

        var greedOnlyDuty = item.LootMode is LootMode.GreedOnly or LootMode.LootMasterGreedOnly;

        return (item.LootMode, item.RollState) switch
        {
            (LootMode.Unavailable, _) => default,
            (_, RollState.UpToNeed) when greedOnlyDuty => new(false, true, true, "This duty is Greed only."),
            (_, RollState.UpToNeed) => new(true, true, true, null),
            (_, RollState.UpToGreed) => new(false, true, true, "Your current job can't Need this item."),
            (_, RollState.UpToPass) => new(false, false, true, "You can only pass on this item."),
            _ => default,
        };
    }

    /// <summary>Whether a click on this item is still waiting for the game to confirm it.</summary>
    public static bool IsRollPending(RollInfo roll)
        => roll.PendingChoice != null && (DateTime.Now - roll.PendingSince).TotalSeconds < RollPendingSeconds;

    /// <summary>Rolls on the item for you, if the game currently accepts that choice.</summary>
    public void Roll(RollInfo roll, RollChoice choice)
    {
        if (IsRollPending(roll) || !GetRollOptions(roll).Allows(choice))
            return;

        roll.PendingChoice = choice;
        roll.PendingSince = DateTime.Now;

        var slot = roll.LootSlot;
        var itemId = roll.ItemId;
        var itemName = roll.ItemName;
        Plugin.Framework.RunOnFrameworkThread(() => SendRoll(slot, itemId, itemName, choice));
    }

    private unsafe void SendRoll(int slot, uint itemId, string itemName, RollChoice choice)
    {
        try
        {
            var loot = Loot.Instance();
            if (rollItem == null || loot == null)
                return;

            // The list may have moved on between the click and this frame; never roll on the wrong item.
            ref var item = ref loot->Items[slot];
            if (NormalizeItemId(item.ItemId) != itemId || item.RollResult != RollResult.UnAwarded)
            {
                Plugin.Log.Warning($"Skipped {choice} on {itemName}: loot slot {slot} changed before the roll was sent");
                return;
            }

            rollItem(loot, (RollResult)choice, (uint)slot);
            Plugin.Log.Info($"Rolled {choice} on {itemName} (slot {slot})");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"Failed to roll {choice} on {itemName}");
        }
    }
}
