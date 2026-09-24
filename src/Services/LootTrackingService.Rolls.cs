#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace LootView.Services;

/// <summary>
/// Roll session lifecycle: passes, per-item timers read from the game's loot list,
/// and closing sessions once the game is done with them.
/// </summary>
public partial class LootTrackingService
{
    /// <summary>A session the loot list never confirmed is dropped this long after it started.</summary>
    private const double UnmatchedSessionSeconds = RollInfo.DefaultRollSeconds + 30;

    private const int LootSlots = 16;

    // Whether each loot slot's Time field counts down (-1) or up (+1); 0 until observed.
    private readonly float[] lastSlotTime = new float[LootSlots];
    private readonly int[] slotTimeDirection = new int[LootSlots];

    private string LocalPlayerName => Plugin.ObjectTable.LocalPlayer?.Name.TextValue ?? "You";

    /// <summary>Names of your own party, or just you when solo.</summary>
    public List<string> PartyMemberNames()
    {
        var names = new List<string>();
        foreach (var member in Plugin.PartyList)
        {
            var name = member.Name.TextValue;
            if (!string.IsNullOrEmpty(name))
                names.Add(name);
        }

        if (names.Count == 0)
            names.Add(LocalPlayerName);

        return names;
    }

    /// <summary>
    /// Chat and party list spell names differently ("Kaia  Tanne", "Kaia TanneSagittarius"),
    /// so treat two names as the same player when one is a prefix of the other.
    /// </summary>
    public static bool SameName(string a, string b)
    {
        a = a.Replace("  ", " ").Trim();
        b = b.Replace("  ", " ").Trim();
        if (a.Length == 0 || b.Length == 0) return false;

        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) ||
               b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindPlayerKey(RollInfo roll, string name)
    {
        if (roll.PlayerRolls.ContainsKey(name)) return name;
        return roll.PlayerRolls.Keys.FirstOrDefault(k => SameName(k, name));
    }

    /// <summary>The key of the player whose Need or Greed roll has been revealed under this name, if any.</summary>
    private static string? FindRollerKey(RollInfo roll, string name)
    {
        var key = FindPlayerKey(roll, name);
        if (key == null) return null;

        var entry = roll.PlayerRolls[key];
        return RollKind.IsRoll(entry.RollType) && entry.RollValue != RollKind.PendingValue ? key : null;
    }

    /// <summary>The player a message is about: from its player link when present, else from the text.</summary>
    private string ActorName(SeString message, string textPrefix)
    {
        var player = message.Payloads.OfType<PlayerPayload>().FirstOrDefault();
        return player != null ? player.PlayerName : CleanPlayerName(textPrefix.Trim());
    }

    private static uint NormalizeItemId(uint id) => id switch
    {
        > 1_000_000 => id - 1_000_000, // HQ
        > 500_000 => id - 500_000,     // collectable
        _ => id,
    };

    /// <summary>
    /// "Name casts his lot for the X." - sent for any choice, Need and Greed included; the game only
    /// reveals which once the item is resolved. The player is shown as Decided until then, and as a
    /// Pass if no Need/Greed roll turns up. Your own choice is read from the loot list instead.
    /// </summary>
    private void ProcessCastLotMessage(string messageText, SeString message)
    {
        try
        {
            if (messageText.StartsWith("You cast your lot", StringComparison.Ordinal))
                return;

            var castsIndex = messageText.IndexOf(" casts ", StringComparison.Ordinal);
            if (castsIndex <= 0) return;
            var playerName = ActorName(message, messageText[..castsIndex]);

            var itemPayload = message.Payloads.OfType<ItemPayload>().FirstOrDefault();
            (uint ItemId, uint IconId, uint Rarity, string Name)? itemData = null;

            if (itemPayload != null)
            {
                itemData = GetItemDataById(itemPayload.ItemId);
            }
            else
            {
                var forIndex = messageText.IndexOf(" lot for ", StringComparison.Ordinal);
                if (forIndex > 0)
                {
                    var name = messageText[(forIndex + 9)..].TrimEnd('.', ' ');
                    if (name.StartsWith("the ", StringComparison.OrdinalIgnoreCase)) name = name[4..];
                    itemData = FindItemByName(name);
                }
            }

            if (!itemData.HasValue)
            {
                Plugin.Log.Warning($"Could not find item for cast lot message: {messageText}");
                return;
            }

            var itemId = itemData.Value.ItemId;

            lock (rollLock)
            {
                var rollInfo = activeRolls.FirstOrDefault(r =>
                    r.ItemId == itemId && !r.IsFinished && FindPlayerKey(r, playerName) == null);

                if (rollInfo == null)
                {
                    Plugin.Log.Debug($"No open roll session for {playerName}'s lot on {itemData.Value.Name}");
                    return;
                }

                rollInfo.PlayerRolls[playerName] = (RollKind.Decided, 0);
            }

            Plugin.Log.Info($"Lot cast: {playerName} decided on {itemData.Value.Name}");
            RollsUpdated?.Invoke();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error processing pass message: {Message}", messageText);
        }
    }

    private void OnFrameworkUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        try
        {
            UpdateRollSessions();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error updating roll sessions");
        }
    }

    /// <summary>
    /// Matches each open session to its entry in the game's loot list, keeps its timer and your own
    /// status in sync, and closes it once the game drops the item from the list.
    /// </summary>
    private unsafe void UpdateRollSessions()
    {
        if (!configService.Configuration.EnableRollTracking)
            return;

        // The loot list empties during zone transitions; judge sessions once the new zone has loaded.
        if (Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51])
            return;

        var loot = Loot.Instance();
        var changed = false;
        var territory = Plugin.ClientState.TerritoryType;

        lock (rollLock)
        {
            if (!activeRolls.Any(r => r.ClosedAt == null))
                return;

            var now = DateTime.Now;
            var boundSlots = activeRolls
                .Where(r => r.ClosedAt == null && r.LootSlot >= 0)
                .Select(r => r.LootSlot)
                .ToHashSet();

            foreach (var roll in activeRolls)
            {
                if (roll.ClosedAt != null)
                    continue;

                if (roll.TerritoryId != territory)
                {
                    changed |= CloseSession(roll, RollCloseReason.LeftDuty);
                    continue;
                }

                if (loot == null)
                {
                    if ((now - roll.RollStartTime).TotalSeconds > UnmatchedSessionSeconds)
                        changed |= CloseSession(roll, RollCloseReason.Finished);
                    continue;
                }

                var items = loot->Items;

                if (roll.LootSlot < 0)
                {
                    for (var i = 0; i < items.Length && i < LootSlots; i++)
                    {
                        if (boundSlots.Contains(i) || NormalizeItemId(items[i].ItemId) != roll.ItemId)
                            continue;

                        roll.LootSlot = i;
                        boundSlots.Add(i);
                        slotTimeDirection[i] = 0;
                        lastSlotTime[i] = items[i].Time;
                        Plugin.Log.Debug($"Roll session {roll.ItemName} matched to loot slot {i} (Time={items[i].Time:F1}, MaxTime={items[i].MaxTime:F1})");
                        break;
                    }

                    if (roll.LootSlot < 0)
                    {
                        if ((now - roll.RollStartTime).TotalSeconds > UnmatchedSessionSeconds)
                            changed |= CloseSession(roll, RollCloseReason.Finished);
                        continue;
                    }
                }

                ref var item = ref items[roll.LootSlot];

                // The game removes an item from the list once everyone has chosen or its time is up.
                if (NormalizeItemId(item.ItemId) != roll.ItemId)
                {
                    changed |= CloseSession(roll, RollCloseReason.Finished);
                    continue;
                }

                SyncTimer(roll, item.Time, item.MaxTime, now);
                changed |= SyncLocalStatus(roll, item);
            }
        }

        if (changed)
            RollsUpdated?.Invoke();
    }

    /// <summary>
    /// The loot list stores a Time and a MaxTime per item. Whether Time counts down or up isn't
    /// documented, so watch it move once and derive the time left from whichever it turns out to be.
    /// </summary>
    private void SyncTimer(RollInfo roll, float time, float maxTime, DateTime now)
    {
        var slot = roll.LootSlot;

        if (slotTimeDirection[slot] == 0)
        {
            var delta = time - lastSlotTime[slot];
            if (Math.Abs(delta) > 0.05f)
            {
                slotTimeDirection[slot] = delta < 0 ? -1 : 1;
                Plugin.Log.Debug($"Loot slot {slot} timer counts {(delta < 0 ? "down" : "up")} (Time={time:F1}, MaxTime={maxTime:F1})");
            }
            lastSlotTime[slot] = time;
        }

        if (maxTime > 1f)
            roll.TimerSeconds = maxTime;

        float? remaining = slotTimeDirection[slot] switch
        {
            -1 => time,
            1 when maxTime > 0 => maxTime - time,
            _ => null,
        };

        // The window reads the deadline every frame, so a timer update needs no notification.
        if (remaining is not null)
            roll.Deadline = now.AddSeconds(Math.Max(remaining.Value, 0));
    }

    /// <summary>
    /// Your own choice is known exactly from the loot list, straight away, including items you aren't
    /// allowed to roll on. The roll value itself only arrives in chat once the item is resolved.
    /// </summary>
    private bool SyncLocalStatus(RollInfo roll, LootItem item)
    {
        var me = LocalPlayerName;
        if (FindRollerKey(roll, me) != null)
            return false;

        (string Kind, int Value)? status = item.RollResult switch
        {
            RollResult.Needed => (RollKind.Need, RollKind.PendingValue),
            RollResult.Greeded => (RollKind.Greed, RollKind.PendingValue),
            RollResult.Passed => (RollKind.Pass, 0),
            _ when item.RollState == RollState.Unavailable || item.LootMode == LootMode.Unavailable
                => (RollKind.CantRoll, 0),
            _ => null,
        };

        var key = FindPlayerKey(roll, me);
        if (status == null || (key != null && roll.PlayerRolls[key] == status.Value))
            return false;

        if (key != null) roll.PlayerRolls.Remove(key);
        roll.PlayerRolls[me] = status.Value;
        Plugin.Log.Info($"Your status on {roll.ItemName}: {status.Value.Kind}");
        return true;
    }

    /// <summary>
    /// Marks a session as done. When the game closed it normally, party members who never chose
    /// are listed as not rolling, since the game passes for them.
    /// </summary>
    private bool CloseSession(RollInfo roll, RollCloseReason reason)
    {
        if (roll.ClosedAt != null)
            return false;

        roll.ClosedAt = DateTime.Now;
        roll.FinishedAt ??= roll.ClosedAt;
        roll.CloseReason = reason;
        if (roll.Deadline > DateTime.Now)
            roll.Deadline = DateTime.Now;

        if (reason == RollCloseReason.Finished)
        {
            // Need and Greed rolls are revealed as the item resolves; a lot with no roll behind it was a pass.
            foreach (var key in roll.PlayerRolls.Where(p => p.Value.RollType == RollKind.Decided).Select(p => p.Key).ToList())
                roll.PlayerRolls[key] = (RollKind.Pass, 0);

            foreach (var member in PartyMemberNames())
            {
                if (FindPlayerKey(roll, member) == null)
                    roll.PlayerRolls[member] = (RollKind.NoRoll, 0);
            }
        }

        Plugin.Log.Info($"Roll session closed ({reason}): {roll.ItemName}" +
                        (string.IsNullOrEmpty(roll.WinnerName) ? "" : $", won by {roll.WinnerName}"));
        return true;
    }

    /// <summary>Closes every open session - used when leaving the duty before rolls are resolved.</summary>
    public void CloseAllRolls(RollCloseReason reason)
    {
        var changed = false;
        lock (rollLock)
        {
            foreach (var roll in activeRolls)
                changed |= CloseSession(roll, reason);
        }

        if (changed)
            RollsUpdated?.Invoke();
    }
}
