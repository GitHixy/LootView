using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using LootView.Models;

namespace LootView.Services;

/// <summary>
/// Simplified service for tracking loot obtained by players
/// This version focuses on functionality over complex hooks
/// </summary>
public class LootTrackingService : IDisposable
{
    private readonly ConfigurationService configService;
    private readonly List<LootItem> lootHistory = new();
    private readonly object lootHistoryLock = new();
    private HistoryService historyService;
    
    // Roll tracking - use a list to allow multiple drops of the same item
    private readonly List<RollInfo> activeRolls = new();
    private readonly object rollLock = new();
    
    // Deduplication tracking - prevent same item from being added twice within a short time window
    private readonly Dictionary<string, DateTime> recentlyAddedItems = new();
    private readonly object deduplicationLock = new();

    public IReadOnlyList<LootItem> LootHistory
    {
        get
        {
            lock (lootHistoryLock)
            {
                return lootHistory.ToList();
            }
        }
    }

    public IReadOnlyList<RollInfo> ActiveRolls
    {
        get
        {
            lock (rollLock)
            {
                return activeRolls.ToList();
            }
        }
    }

    public event Action<LootItem> LootObtained;
    public event Action RollsUpdated;

    /// <summary>
    /// Clear all completed roll sessions (ones with winners)
    /// </summary>
    public void ClearCompletedRolls()
    {
        lock (rollLock)
        {
            var completedCount = activeRolls.RemoveAll(r => !string.IsNullOrEmpty(r.WinnerName));
            if (completedCount > 0)
            {
                Plugin.Log.Info($"Cleared {completedCount} completed roll session(s)");
                RollsUpdated?.Invoke();
            }
        }
    }

    /// <summary>
    /// Clear ALL roll sessions (completed or not)
    /// </summary>
    public void ClearAllRolls()
    {
        lock (rollLock)
        {
            var totalCount = activeRolls.Count;
            activeRolls.Clear();
            if (totalCount > 0)
            {
                Plugin.Log.Info($"Cleared all {totalCount} roll session(s)");
                RollsUpdated?.Invoke();
            }
        }
    }

    public LootTrackingService(ConfigurationService configService)
    {
        this.configService = configService;
    }

    public void SetHistoryService(HistoryService service)
    {
        historyService = service;
    }

    public void Initialize()
    {
        try
        {
            Plugin.Log.Info("Initializing loot tracking service...");
            
            // Subscribe to chat messages for loot detection
            Plugin.ChatGui.ChatMessage += OnChatMessage;
            
            Plugin.Log.Info("Loot tracking service initialized successfully");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to initialize loot tracking service");
            throw;
        }
    }

    private void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        try
        {
            var messageText = message.TextValue;
            
            // Log for debugging what messages we see
            if (configService.Configuration.EnableDebugLogging)
            {
                Plugin.Log.Debug($"Chat [{type}]: {messageText}");
            }
            
            // Always log fishing messages for debugging
            if (messageText.Contains("You land"))
            {
                Plugin.Log.Info($"Detected fishing-like message [{type}]: {messageText}");
            }
            
            // Detect loot messages:
            // - "You obtain X ItemName" (your loot)
            // - "PlayerName obtains X ItemName" (party member loot)
            // - "X ItemName are obtained" or "ItemName is obtained" (passive voice)
            // - "You have successfully extracted a ItemName" (aetherial reduction, desynthesis)
            // - "You exchange X ItemName for a ItemName" (vendor trades)
            // - "A bonus of X gil has been awarded for using the duty roulette" (roulette bonus)
            // - "You roll Need/Greed on the ItemName. XX!" (roll tracking)
            // - "PlayerName rolls Need/Greed on the ItemName. XX!" (party roll tracking)
            // - "A ItemName has been added to the loot list." (roll started)
            // - "You land a ItemName measuring N ilms!" (fishing)
            if (messageText.Contains("has been added to the loot list"))
            {
                ProcessLootListAddedMessage(messageText, message);
            }
            else if ((messageText.Contains(" roll") || messageText.Contains(" rolls ")) && 
                (messageText.Contains(" Need ") || messageText.Contains(" Greed ") || messageText.Contains("Need on") || messageText.Contains("Greed on")))
            {
                ProcessRollMessage(messageText, message);
            }
            else if (messageText.Contains("land ") && messageText.Contains("ilms!"))
            {
                ProcessFishingMessage(messageText, message);
            }
            else if (messageText.Contains("You obtain") || messageText.Contains(" obtains ") || messageText.Contains("You synthesize"))
            {
                ProcessObtainMessage(messageText, message);
            }
            else if (messageText.Contains(" are obtained") || messageText.Contains(" is obtained"))
            {
                ProcessPassiveObtainMessage(messageText, message);
            }
            else if (messageText.Contains(" is added to your inventory"))
            {
                ProcessInventoryAddMessage(messageText, message);
            }
            else if (messageText.Contains("successfully extract"))
            {
                ProcessExtractionMessage(messageText, message);
            }
            else if (messageText.Contains("You exchange") && messageText.Contains(" for "))
            {
                ProcessExchangeMessage(messageText, message);
            }
            else if (messageText.Contains("A bonus of") && messageText.Contains("gil has been awarded for using the duty roulette"))
            {
                ProcessRouletteBonusMessage(messageText, message);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error processing chat message for loot detection");
        }
    }

    private void ProcessObtainMessage(string messageText, SeString message)
    {
        try
        {
            var localPlayer = Plugin.ClientState.LocalPlayer;
            if (localPlayer == null) return;

            // Try to extract item ID from SeString payload (for linked items)
            uint? itemIdFromPayload = null;
            string itemNameFromPayload = null;
            
            foreach (var payload in message.Payloads)
            {
                if (payload is Dalamud.Game.Text.SeStringHandling.Payloads.ItemPayload itemPayload)
                {
                    itemIdFromPayload = itemPayload.ItemId;
                    itemNameFromPayload = itemPayload.DisplayName;
                    Plugin.Log.Debug("Found item payload: ID={ItemId}, Name={Name}", itemPayload.ItemId, itemPayload.DisplayName);
                    break;
                }
            }

            // Parse loot messages:
            // - "You obtain X ItemName" (your loot)
            // - "You synthesize X ItemName" (crafting)
            // - "PlayerName obtains X ItemName" (party member loot)
            
            string itemName = itemNameFromPayload;
            string playerName;
            bool isOwnLoot;
            uint quantity = 1;
            bool isHQ = messageText.Contains(" HQ");
            
            string remaining;
            
            if (messageText.StartsWith("You obtain "))
            {
                // Your loot
                playerName = localPlayer.Name.TextValue;
                isOwnLoot = true;
                remaining = messageText.Replace("You obtain ", "").Trim();
            }
            else if (messageText.StartsWith("You synthesize "))
            {
                // Crafting output
                playerName = localPlayer.Name.TextValue;
                isOwnLoot = true;
                remaining = messageText.Replace("You synthesize ", "").Trim();
            }
            else
            {
                // Party member loot: "PlayerName obtains X ItemName"
                // May include server name for cross-world players: "PlayerName WorldName obtains..."
                var obtainsIndex = messageText.IndexOf(" obtains ");
                if (obtainsIndex > 0)
                {
                    playerName = messageText.Substring(0, obtainsIndex).Trim();
                    
                    // Remove server name if present (cross-world players show as "Name ServerName")
                    // Server names are typically capitalized single words after the player name
                    playerName = CleanPlayerName(playerName);
                    
                    isOwnLoot = playerName.Equals(localPlayer.Name.TextValue, StringComparison.OrdinalIgnoreCase);
                    remaining = messageText.Substring(obtainsIndex + " obtains ".Length).Trim();
                    
                    Plugin.Log.Debug("Parsed obtain message: PlayerName={PlayerName}, Remaining={Remaining}", playerName, remaining);
                }
                else
                {
                    // Fallback
                    playerName = localPlayer.Name.TextValue;
                    isOwnLoot = true;
                    remaining = messageText.Replace("You obtain ", "").Trim();
                    
                    Plugin.Log.Debug("Fallback obtain parsing: Remaining={Remaining}", remaining);
                }
            }
            
            // Check for quantity (number at start)
            var parts = remaining.Split(' ', 2);
            if (parts.Length >= 2)
            {
                string quantityPart = parts[0];
                
                // Handle bonus format: "54(+9)" -> extract total
                if (quantityPart.Contains("(+") && quantityPart.Contains(")"))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(quantityPart, @"^(\d+)\(\+(\d+)\)$");
                    if (match.Success)
                    {
                        var baseQty = uint.Parse(match.Groups[1].Value);
                        var bonusQty = uint.Parse(match.Groups[2].Value);
                        quantity = baseQty + bonusQty;
                        itemName = parts[1].TrimEnd('.', ' ');
                    }
                }
                // Handle comma-separated numbers: "1,000" -> 1000
                else if (quantityPart.Contains(","))
                {
                    var cleanedQty = quantityPart.Replace(",", "");
                    if (uint.TryParse(cleanedQty, out var parsedQty))
                    {
                        quantity = parsedQty;
                        itemName = parts[1].TrimEnd('.', ' ');
                    }
                }
                // Handle regular number
                else if (uint.TryParse(quantityPart, out var parsedQty))
                {
                    quantity = parsedQty;
                    var rest = parts[1];
                    
                    // Check if the next part is a unit word: "2 chunks of ItemName"
                    var unitWords = new[] { "chunks of ", "chunk of ", "pinches of ", "pinch of ", 
                                           "bottles of ", "bottle of ", "pieces of ", "piece of ",
                                           "phials of ", "phial of ", "stalks of ", "stalk of ",
                                           "sets of ", "set of ", "bundles of ", "bundle of ",
                                           "pots of ", "pot of ", "coils of ", "coil of ",
                                           "planks of ", "plank of ", "lengths of ", "length of ",
                                           "stacks of ", "stack of ", "bolts of ", "bolt of ",
                                           "loops of ", "loop of " };
                    foreach (var unit in unitWords)
                    {
                        if (rest.StartsWith(unit, StringComparison.OrdinalIgnoreCase))
                        {
                            rest = rest.Substring(unit.Length);
                            break;
                        }
                    }
                    
                    itemName = rest.TrimEnd('.', ' ');
                }
                // Handle "a chunk of ItemName" / "chunks of ItemName" etc.
                else if (quantityPart.Equals("a", StringComparison.OrdinalIgnoreCase) || 
                         quantityPart.Equals("an", StringComparison.OrdinalIgnoreCase))
                {
                    quantity = 1;
                    var rest = parts[1];
                    
                    // Remove unit words like "chunk of", "pinch of", "bottle of"
                    var unitWords = new[] { "chunk of ", "pinch of ", "bottle of ", "piece of ", "phial of ", "stalk of ", "coil of ", "plank of ", "length of ", "stack of ", "bolt of ", "loop of " };
                    foreach (var unit in unitWords)
                    {
                        if (rest.StartsWith(unit, StringComparison.OrdinalIgnoreCase))
                        {
                            rest = rest.Substring(unit.Length);
                            break;
                        }
                    }
                    
                    itemName = rest.TrimEnd('.', ' ');
                }
                // Handle "chunks of ItemName" / "pinches of ItemName" etc. (plural with no number)
                else if (quantityPart.Equals("chunks", StringComparison.OrdinalIgnoreCase) ||
                         quantityPart.Equals("pinches", StringComparison.OrdinalIgnoreCase) ||
                         quantityPart.Equals("bottles", StringComparison.OrdinalIgnoreCase) ||
                         quantityPart.Equals("pieces", StringComparison.OrdinalIgnoreCase) ||
                         quantityPart.Equals("phials", StringComparison.OrdinalIgnoreCase) ||
                         quantityPart.Equals("stalks", StringComparison.OrdinalIgnoreCase) ||
                         quantityPart.Equals("coils", StringComparison.OrdinalIgnoreCase) ||
                         quantityPart.Equals("planks", StringComparison.OrdinalIgnoreCase) ||
                         quantityPart.Equals("lengths", StringComparison.OrdinalIgnoreCase) ||
                         quantityPart.Equals("stacks", StringComparison.OrdinalIgnoreCase) ||
                         quantityPart.Equals("bolts", StringComparison.OrdinalIgnoreCase) ||
                         quantityPart.Equals("loops", StringComparison.OrdinalIgnoreCase))
                {
                    quantity = 1; // Default to 1 if no number specified
                    var rest = parts[1];
                    
                    // Remove "of "
                    if (rest.StartsWith("of ", StringComparison.OrdinalIgnoreCase))
                    {
                        rest = rest.Substring(3);
                    }
                    
                    itemName = rest.TrimEnd('.', ' ');
                }
            }
            
            // If no quantity found yet, check for article + item name formats
            if (itemName == null && parts.Length >= 2)
            {
                string firstWord = parts[0];
                
                // Handle "a ItemName" / "an ItemName"
                if (firstWord.Equals("a", StringComparison.OrdinalIgnoreCase) || 
                    firstWord.Equals("an", StringComparison.OrdinalIgnoreCase))
                {
                    quantity = 1;
                    itemName = parts[1].TrimEnd('.', ' ');
                }
                // Handle "the ItemName"
                else if (firstWord.Equals("the", StringComparison.OrdinalIgnoreCase))
                {
                    quantity = 1;
                    itemName = "the " + parts[1].TrimEnd('.', ' '); // Keep "the" in item name
                }
            }
            
            // Fallback: if still no item name found
            if (itemName == null)
            {
                if (remaining.StartsWith("a ") || remaining.StartsWith("an "))
                {
                    // "a potion" or "an item"
                    quantity = 1;
                    itemName = remaining.Substring(remaining.IndexOf(' ') + 1).TrimEnd('.', ' ');
                }
                else if (remaining.StartsWith("the "))
                {
                    // "the item" - keep "the" as it's part of the item name
                    quantity = 1;
                    itemName = remaining.TrimEnd('.', ' ');
                }
                else
                {
                    itemName = remaining.TrimEnd('.', ' ');
                }
            }
            
            // Ensure itemName is not null before processing
            if (string.IsNullOrEmpty(itemName))
            {
                Plugin.Log.Error("Item name is null or empty after parsing. Message: {Message}", messageText);
                return;
            }
            
            // Remove HQ suffix if present
            if (isHQ)
            {
                itemName = itemName.Replace(" HQ", "").Trim();
            }
            
            // Remove surrounding quotes if present (from some chat formats)
            itemName = itemName.Trim('"', '\'', ' ');
            
            // Normalize plural unit words to singular for item names
            // E.g., "sacks of Nuts" -> "sack of Nuts"
            if (itemName.StartsWith("sacks of ", StringComparison.OrdinalIgnoreCase))
            {
                itemName = "sack of " + itemName.Substring(9);
            }
            
            // If we have item ID from payload, use it directly; otherwise try to find by name
            (uint ItemId, uint IconId, uint Rarity, string Name)? itemData = null;
            
            if (itemIdFromPayload.HasValue)
            {
                // We have the item ID from the link payload - get data directly
                itemData = GetItemDataById(itemIdFromPayload.Value);
                if (itemData.HasValue)
                {
                    itemName = itemData.Value.Name;
                }
            }
            else
            {
                // No payload, clean and search by name
                itemName = CleanItemName(itemName);
                // Don't use ToTitleCase - game item names have specific capitalization
                itemData = FindItemByName(itemName);
                
                if (itemData.HasValue)
                {
                    Plugin.Log.Info("Item found by name: ID={ItemId}, Icon={IconId}, Name={Name}, Rarity={Rarity}", 
                        itemData.Value.ItemId, itemData.Value.IconId, itemData.Value.Name, itemData.Value.Rarity);
                }
                else
                {
                    Plugin.Log.Warning("Item not found by name: '{ItemName}'", itemName);
                }
            }
            
            var lootItem = new LootItem
            {
                ItemName = itemData?.Name ?? itemName,
                ItemId = itemData?.ItemId ?? 0,
                IconId = itemData?.IconId ?? 0,
                Rarity = itemData?.Rarity ?? 1,
                Quantity = quantity,
                IsHQ = isHQ,
                PlayerName = playerName,
                PlayerContentId = isOwnLoot ? Plugin.ClientState.LocalContentId : 0,
                IsOwnLoot = isOwnLoot,
                Source = LootSource.Unknown,
                TerritoryType = Plugin.ClientState.TerritoryType,
                ZoneName = GetCurrentZoneName()
            };

            // Check if there was a roll for this item
            lock (rollLock)
            {
                var itemId = itemData?.ItemId ?? 0;
                // Find the first roll for this item that doesn't have a winner yet
                var rollInfo = itemId > 0 ? activeRolls.FirstOrDefault(r => r.ItemId == itemId && string.IsNullOrEmpty(r.WinnerName)) : null;
                
                if (rollInfo != null)
                {
                    string winnerRollName = string.Empty;
                    
                    // Try exact match first
                    if (rollInfo.PlayerRolls.TryGetValue(playerName, out var roll))
                    {
                        lootItem.RollType = roll.RollType;
                        lootItem.RollValue = roll.RollValue;
                        winnerRollName = playerName;
                        Plugin.Log.Info($"Adding roll info to loot (exact match): {roll.RollType} {roll.RollValue}");
                    }
                    else
                    {
                        // Try partial match (for truncated names like "Kaia  Tanne" vs "Kaia TanneSagittarius")
                        // Clean up the player name by removing extra spaces
                        var cleanedPlayerName = playerName.Replace("  ", " ").Trim();
                        
                        foreach (var kvp in rollInfo.PlayerRolls)
                        {
                            var rollPlayerName = kvp.Key;
                            // Check if roll player name starts with the truncated obtained player name
                            if (rollPlayerName.StartsWith(cleanedPlayerName, StringComparison.OrdinalIgnoreCase))
                            {
                                lootItem.RollType = kvp.Value.RollType;
                                lootItem.RollValue = kvp.Value.RollValue;
                                winnerRollName = rollPlayerName;
                                Plugin.Log.Info($"Adding roll info to loot (partial match '{cleanedPlayerName}' -> '{rollPlayerName}'): {kvp.Value.RollType} {kvp.Value.RollValue}");
                                break;
                            }
                        }
                    }
                    
                    // Mark the winner in the roll info
                    if (!string.IsNullOrEmpty(winnerRollName))
                    {
                        rollInfo.WinnerName = winnerRollName;
                        Plugin.Log.Info($"Winner marked: {winnerRollName} won {itemName}");
                        
                        // Notify that rolls have been updated (winner marked)
                        RollsUpdated?.Invoke();
                    }
                    
                    // Don't remove yet - let the RollWindow handle cleanup after display timeout
                }
            }

            AddLootItem(lootItem);
            
            Plugin.Log.Info($"Loot tracked: {playerName} obtained {itemName} x{quantity}" + (isHQ ? " HQ" : ""));
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error processing obtain message: {Message}", message);
        }
    }

    private void ProcessPassiveObtainMessage(string messageText, SeString message)
    {
        // Handle "X ItemName are obtained" or "ItemName is obtained"
        // Example: "8 pinches of ironquartz sand are obtained."
        try
        {
            var localPlayer = Plugin.ClientState.LocalPlayer;
            if (localPlayer == null) return;

            // Try to extract item ID from SeString payload (for linked items)
            uint? itemIdFromPayload = null;
            string itemNameFromPayload = null;
            
            foreach (var payload in message.Payloads)
            {
                if (payload is Dalamud.Game.Text.SeStringHandling.Payloads.ItemPayload itemPayload)
                {
                    itemIdFromPayload = itemPayload.ItemId;
                    itemNameFromPayload = itemPayload.DisplayName;
                    Plugin.Log.Debug("Found item payload: ID={ItemId}, Name={Name}", itemPayload.ItemId, itemPayload.DisplayName);
                    break;
                }
            }

            string itemName = itemNameFromPayload;
            uint quantity = 1;
            bool isHQ = messageText.Contains(" HQ");
            
            // Remove " are obtained." or " is obtained." from the end
            string remaining = messageText;
            if (remaining.EndsWith(" are obtained.", StringComparison.OrdinalIgnoreCase))
            {
                remaining = remaining.Substring(0, remaining.Length - " are obtained.".Length).Trim();
            }
            else if (remaining.EndsWith(" is obtained.", StringComparison.OrdinalIgnoreCase))
            {
                remaining = remaining.Substring(0, remaining.Length - " is obtained.".Length).Trim();
            }
            else if (remaining.EndsWith(" are obtained", StringComparison.OrdinalIgnoreCase))
            {
                remaining = remaining.Substring(0, remaining.Length - " are obtained".Length).Trim();
            }
            else if (remaining.EndsWith(" is obtained", StringComparison.OrdinalIgnoreCase))
            {
                remaining = remaining.Substring(0, remaining.Length - " is obtained".Length).Trim();
            }
            
            // Now parse quantity and item name from remaining
            // Same logic as ProcessObtainMessage
            var parts = remaining.Split(' ', 2);
            if (parts.Length >= 2)
            {
                string quantityPart = parts[0];
                
                // Handle bonus format: "54(+9)" -> extract total
                if (quantityPart.Contains("(+") && quantityPart.Contains(")"))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(quantityPart, @"^(\d+)\(\+(\d+)\)$");
                    if (match.Success)
                    {
                        var baseQty = uint.Parse(match.Groups[1].Value);
                        var bonusQty = uint.Parse(match.Groups[2].Value);
                        quantity = baseQty + bonusQty;
                        itemName = parts[1].TrimEnd('.', ' ');
                    }
                }
                // Handle comma-separated numbers: "1,000" -> 1000
                else if (quantityPart.Contains(","))
                {
                    var cleanedQty = quantityPart.Replace(",", "");
                    if (uint.TryParse(cleanedQty, out var parsedQty))
                    {
                        quantity = parsedQty;
                        itemName = parts[1].TrimEnd('.', ' ');
                    }
                }
                // Handle regular number
                else if (uint.TryParse(quantityPart, out var parsedQty))
                {
                    quantity = parsedQty;
                    var rest = parts[1];
                    
                    // Check if the next part is a unit word: "2 chunks of ItemName"
                    var unitWords = new[] { "chunks of ", "chunk of ", "pinches of ", "pinch of ", 
                                           "bottles of ", "bottle of ", "pieces of ", "piece of ",
                                           "phials of ", "phial of ", "stalks of ", "stalk of ",
                                           "sets of ", "set of ", "bundles of ", "bundle of " };
                    foreach (var unit in unitWords)
                    {
                        if (rest.StartsWith(unit, StringComparison.OrdinalIgnoreCase))
                        {
                            rest = rest.Substring(unit.Length);
                            break;
                        }
                    }
                    
                    itemName = rest.TrimEnd('.', ' ');
                }
                // Handle "a chunk of ItemName" / "chunks of ItemName" etc.
                else if (quantityPart.Equals("a", StringComparison.OrdinalIgnoreCase) || 
                         quantityPart.Equals("an", StringComparison.OrdinalIgnoreCase))
                {
                    quantity = 1;
                    var rest = parts[1];
                    
                    // Remove unit words like "chunk of", "pinch of", "bottle of"
                    var unitWords = new[] { "chunk of ", "pinch of ", "bottle of ", "piece of ", "phial of ", "stalk of " };
                    foreach (var unit in unitWords)
                    {
                        if (rest.StartsWith(unit, StringComparison.OrdinalIgnoreCase))
                        {
                            rest = rest.Substring(unit.Length);
                            break;
                        }
                    }
                    
                    itemName = rest.TrimEnd('.', ' ');
                }
                else
                {
                    // No quantity number, treat entire remaining as item name
                    itemName = remaining.TrimEnd('.', ' ');
                }
            }
            else
            {
                // Single word item name
                itemName = remaining.TrimEnd('.', ' ');
            }
            
            // Remove HQ suffix if present
            if (isHQ)
            {
                itemName = itemName.Replace(" HQ", "").Trim();
            }
            
            // Remove surrounding quotes if present (from some chat formats)
            itemName = itemName.Trim('"', '\'', ' ');
            
            // Normalize plural unit words to singular for item names
            // E.g., "sacks of Nuts" -> "sack of Nuts"
            if (itemName.StartsWith("sacks of ", StringComparison.OrdinalIgnoreCase))
            {
                itemName = "sack of " + itemName.Substring(9);
            }
            
            // If we have item ID from payload, use it directly; otherwise try to find by name
            (uint ItemId, uint IconId, uint Rarity, string Name)? itemData = null;
            
            if (itemIdFromPayload.HasValue)
            {
                itemData = GetItemDataById(itemIdFromPayload.Value);
                if (itemData.HasValue)
                {
                    itemName = itemData.Value.Name;
                }
            }
            else
            {
                // No payload, clean and search by name
                itemName = CleanItemName(itemName);
                itemData = FindItemByName(itemName);
                
                if (itemData.HasValue)
                {
                    Plugin.Log.Info("Found item by name: ID={ItemId}, Icon={IconId}, Name={Name}, Rarity={Rarity}", 
                        itemData.Value.ItemId, itemData.Value.IconId, itemData.Value.Name, itemData.Value.Rarity);
                }
                else
                {
                    Plugin.Log.Warning("Item not found by name: '{ItemName}'", itemName);
                }
            }
            
            var lootItem = new LootItem
            {
                ItemName = itemData?.Name ?? itemName,
                ItemId = itemData?.ItemId ?? 0,
                IconId = itemData?.IconId ?? 0,
                Rarity = itemData?.Rarity ?? 1,
                Quantity = quantity,
                IsHQ = isHQ,
                PlayerName = localPlayer.Name.TextValue,
                PlayerContentId = Plugin.ClientState.LocalContentId,
                IsOwnLoot = true,
                Source = LootSource.Unknown,
                TerritoryType = Plugin.ClientState.TerritoryType,
                ZoneName = GetCurrentZoneName()
            };

            AddLootItem(lootItem);
            
            Plugin.Log.Info($"Loot tracked (passive): {itemName} x{quantity}" + (isHQ ? " HQ" : ""));
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error processing passive obtain message: {Message}", messageText);
        }
    }

    private void ProcessInventoryAddMessage(string messageText, SeString message)
    {
        // Handle "The ItemName is added to your inventory" or "ItemName is added to your inventory"
        // Example: "The afflatus spinning wheel is added to your inventory."
        try
        {
            var localPlayer = Plugin.ClientState.LocalPlayer;
            if (localPlayer == null) return;

            // Try to extract item ID from SeString payload (for linked items)
            uint? itemIdFromPayload = null;
            string itemNameFromPayload = null;
            
            foreach (var payload in message.Payloads)
            {
                if (payload is Dalamud.Game.Text.SeStringHandling.Payloads.ItemPayload itemPayload)
                {
                    itemIdFromPayload = itemPayload.ItemId;
                    itemNameFromPayload = itemPayload.DisplayName;
                    Plugin.Log.Debug("Found item payload: ID={ItemId}, Name={Name}", itemPayload.ItemId, itemPayload.DisplayName);
                    break;
                }
            }

            string itemName = itemNameFromPayload;
            uint quantity = 1; // Inventory add messages don't specify quantity, assume 1
            bool isHQ = messageText.Contains(" HQ");
            
            // Remove " is added to your inventory." or " is added to your inventory" from the end
            string remaining = messageText;
            if (remaining.EndsWith(" is added to your inventory.", StringComparison.OrdinalIgnoreCase))
            {
                remaining = remaining.Substring(0, remaining.Length - " is added to your inventory.".Length).Trim();
            }
            else if (remaining.EndsWith(" is added to your inventory", StringComparison.OrdinalIgnoreCase))
            {
                remaining = remaining.Substring(0, remaining.Length - " is added to your inventory".Length).Trim();
            }
            
            // Remove "The " or "the " prefix if present
            if (remaining.StartsWith("The ", StringComparison.OrdinalIgnoreCase))
            {
                remaining = remaining.Substring(4).Trim();
            }
            
            itemName = remaining;
            
            // Remove HQ suffix if present
            if (isHQ)
            {
                itemName = itemName.Replace(" HQ", "").Trim();
            }
            
            // Remove surrounding quotes if present (from some chat formats)
            itemName = itemName.Trim('"', '\'', ' ');
            
            // If we have item ID from payload, use it directly; otherwise try to find by name
            (uint ItemId, uint IconId, uint Rarity, string Name)? itemData = null;
            
            if (itemIdFromPayload.HasValue)
            {
                itemData = GetItemDataById(itemIdFromPayload.Value);
                if (itemData.HasValue)
                {
                    itemName = itemData.Value.Name;
                }
            }
            else
            {
                // No payload, clean and search by name
                itemName = CleanItemName(itemName);
                itemData = FindItemByName(itemName);
                
                if (itemData.HasValue)
                {
                    Plugin.Log.Info("Found item by name: ID={ItemId}, Icon={IconId}, Name={Name}, Rarity={Rarity}", 
                        itemData.Value.ItemId, itemData.Value.IconId, itemData.Value.Name, itemData.Value.Rarity);
                }
                else
                {
                    Plugin.Log.Warning("Item not found by name: '{ItemName}'", itemName);
                }
            }
            
            var lootItem = new LootItem
            {
                ItemName = itemData?.Name ?? itemName,
                ItemId = itemData?.ItemId ?? 0,
                IconId = itemData?.IconId ?? 0,
                Rarity = itemData?.Rarity ?? 1,
                Quantity = quantity,
                IsHQ = isHQ,
                PlayerName = localPlayer.Name.TextValue,
                PlayerContentId = Plugin.ClientState.LocalContentId,
                IsOwnLoot = true,
                Source = LootSource.Other,
                TerritoryType = Plugin.ClientState.TerritoryType,
                ZoneName = GetCurrentZoneName(),
                Timestamp = DateTime.Now
            };

            AddLootItem(lootItem);
            
            Plugin.Log.Info($"Loot tracked (inventory add): {lootItem.PlayerName} obtained {itemName} x{quantity}" + (isHQ ? " HQ" : ""));
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error processing inventory add message: {Message}", messageText);
        }
    }

    private void ProcessFishingMessage(string messageText, SeString message)
    {
        try
        {
            Plugin.Log.Info($"Processing fishing message: {messageText}");
            
            var localPlayer = Plugin.ClientState.LocalPlayer;
            if (localPlayer == null) return;

            // Format: "You land a ItemName measuring N ilms!"
            // Extract item name and ID from SeString payload
            uint? itemIdFromPayload = null;
            string itemNameFromPayload = null;
            uint? iconIdFromPayload = null;
            uint? rarityFromPayload = null;
            
            // Log all payloads for debugging
            Plugin.Log.Info($"Fishing message has {message.Payloads.Count} payloads");
            foreach (var payload in message.Payloads)
            {
                Plugin.Log.Info($"Payload type: {payload.GetType().Name}");
                if (payload is Dalamud.Game.Text.SeStringHandling.Payloads.ItemPayload itemPayload)
                {
                    itemIdFromPayload = itemPayload.ItemId;
                    Plugin.Log.Info($"Found item payload (fishing): ID={itemPayload.ItemId}");
                    
                    // Get item data from Lumina using the ID
                    var itemData = GetItemDataById(itemPayload.ItemId);
                    if (itemData.HasValue)
                    {
                        itemNameFromPayload = itemData.Value.Name;
                        iconIdFromPayload = itemData.Value.IconId;
                        rarityFromPayload = itemData.Value.Rarity;
                        Plugin.Log.Info($"Got item data from Lumina: Name={itemNameFromPayload}, Icon={iconIdFromPayload}, Rarity={rarityFromPayload}");
                    }
                    break;
                }
            }

            if (itemIdFromPayload == null || string.IsNullOrEmpty(itemNameFromPayload))
            {
                Plugin.Log.Warning($"Could not extract item from fishing message via payload: {messageText}");
                Plugin.Log.Warning($"Attempting to parse item name from text...");
                
                // Fallback: try to parse the item name from the message text
                // Formats: 
                // - "You land a [item name] measuring X.X ilms!"
                // - "You land an [item name] measuring X.X ilms!"
                // - "You land [quantity] [item name] measuring X.X ilms!"
                
                var landIndex = messageText.IndexOf("You land ");
                if (landIndex < 0)
                {
                    Plugin.Log.Warning($"Could not find 'You land' in fishing message");
                    return;
                }
                
                var afterLand = messageText.Substring(landIndex + 9).Trim(); // After "You land "
                var measuringIndex = messageText.IndexOf(" measuring ");
                
                if (measuringIndex < 0)
                {
                    Plugin.Log.Warning($"Could not find ' measuring ' in fishing message");
                    return;
                }
                
                // Extract the part between "You land " and " measuring "
                var itemPart = messageText.Substring(landIndex + 9, measuringIndex - (landIndex + 9)).Trim();
                
                // Check if it starts with "a " or "an " or "a bouquet of " or a number
                string extractedName;
                if (itemPart.StartsWith("a bouquet of "))
                {
                    extractedName = itemPart.Substring(13).Trim(); // Remove "a bouquet of "
                }
                else if (itemPart.StartsWith("a "))
                {
                    extractedName = itemPart.Substring(2).Trim();
                }
                else if (itemPart.StartsWith("an "))
                {
                    extractedName = itemPart.Substring(3).Trim();
                }
                else if (char.IsDigit(itemPart[0]))
                {
                    // Format: "N itemname" - skip the quantity number
                    var spaceIdx = itemPart.IndexOf(' ');
                    if (spaceIdx > 0)
                    {
                        extractedName = itemPart.Substring(spaceIdx + 1).Trim();
                    }
                    else
                    {
                        extractedName = itemPart;
                    }
                }
                else
                {
                    extractedName = itemPart;
                }
                
                // Clean up multiple spaces and trim
                extractedName = System.Text.RegularExpressions.Regex.Replace(extractedName, @"\s+", " ").Trim();
                
                // Apply additional cleaning
                extractedName = CleanItemName(extractedName);
                
                Plugin.Log.Info($"Extracted item name from text: {extractedName}");
                
                // Try to find item ID from Lumina
                var itemData = FindItemByName(extractedName);
                if (itemData.HasValue)
                {
                    itemIdFromPayload = itemData.Value.ItemId;
                    itemNameFromPayload = itemData.Value.Name;
                    iconIdFromPayload = itemData.Value.IconId;
                    rarityFromPayload = itemData.Value.Rarity;
                    Plugin.Log.Info($"Found item in Lumina: ID={itemIdFromPayload}, Name={itemNameFromPayload}, Icon={iconIdFromPayload}");
                }
                else
                {
                    Plugin.Log.Warning($"Could not find item in Lumina for: {extractedName}");
                    return;
                }
            }

            string itemName = itemNameFromPayload;
            uint quantity = 1; // Fishing always gives 1 item
            bool isHQ = messageText.Contains(" HQ") || itemName.Contains(" HQ");

            // Remove HQ suffix if present
            if (itemName.EndsWith(" HQ"))
            {
                itemName = itemName.Substring(0, itemName.Length - 3).Trim();
            }

            // Extract ilms measurement from message: "measuring X.X ilms!"
            var measuringIdx = messageText.IndexOf(" measuring ");
            var ilmsIdx = messageText.IndexOf(" ilms!");
            if (measuringIdx >= 0 && ilmsIdx > measuringIdx)
            {
                var ilmsValue = messageText.Substring(measuringIdx + 11, ilmsIdx - (measuringIdx + 11)).Trim();
                itemName = $"{itemName} [{ilmsValue} ilms]";
                Plugin.Log.Info($"Added ilms to fish name: {itemName}");
            }

            var lootItem = new LootItem
            {
                ItemId = itemIdFromPayload.Value,
                ItemName = itemName,
                IconId = iconIdFromPayload ?? 0,
                Rarity = rarityFromPayload ?? 1,
                Quantity = quantity,
                IsHQ = isHQ,
                Timestamp = DateTime.Now,
                PlayerName = localPlayer.Name.TextValue,
                PlayerContentId = Plugin.ClientState.LocalContentId,
                IsOwnLoot = true,
                Source = LootSource.Gathering, // Fishing counts as gathering
                TerritoryType = Plugin.ClientState.TerritoryType,
                ZoneName = GetCurrentZoneName()
            };

            AddLootItem(lootItem);
            
            Plugin.Log.Info($"Loot tracked (fishing): {itemName} x{quantity}" + (isHQ ? " HQ" : ""));
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error processing fishing message: {Message}", messageText);
        }
    }

    private string GetCurrentZoneName()
    {
        try
        {
            var territoryId = Plugin.ClientState.TerritoryType;
            var territorySheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>();
            
            if (territorySheet != null && territorySheet.TryGetRow(territoryId, out var territory))
            {
                // Get the PlaceName row directly
                var placeNameSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.PlaceName>();
                if (placeNameSheet != null && placeNameSheet.TryGetRow(territory.PlaceName.RowId, out var placeName))
                {
                    var name = placeName.Name.ToString();
                    if (!string.IsNullOrEmpty(name))
                    {
                        return name;
                    }
                }
            }
            
            return $"Zone {territoryId}";
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to get zone name for territory {TerritoryId}", Plugin.ClientState.TerritoryType);
            return "Unknown Zone";
        }
    }
    
    private string ToTitleCase(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;
        
        var words = text.Split(' ');
        for (int i = 0; i < words.Length; i++)
        {
            if (words[i].Length > 0)
            {
                words[i] = char.ToUpper(words[i][0]) + words[i].Substring(1).ToLower();
            }
        }
        return string.Join(" ", words);
    }
    
    private (uint ItemId, uint IconId, uint Rarity, string Name)? GetItemDataById(uint itemId)
    {
        try
        {
            var itemSheet = Plugin.DataManager.GameData?.GetExcelSheet<Lumina.Excel.Sheets.Item>();
            if (itemSheet == null) return null;
            
            // Items in FFXIV have a special encoding for different types:
            // Regular items: just the ID
            // HQ items: ID > 500000 (ID + 500000)
            // Collectables: ID > 500000
            // Event items/currencies: might be in a different range
            
            var actualItemId = itemId;
            
            // Strip HQ flag if present (items over 500000 are HQ versions)
            if (itemId > 500000)
            {
                actualItemId = itemId - 500000;
            }
            
            // Also check for event item flag (1000000+)
            if (itemId > 1000000)
            {
                actualItemId = itemId % 1000000;
            }
            
            if (itemSheet.TryGetRow(actualItemId, out var item))
            {
                var iconId = item.Icon;
                var itemName = item.Name.ExtractText();
                Plugin.Log.Info("Found item: ID={ItemId}, Icon={IconId}, Name={Name}, Rarity={Rarity}", item.RowId, iconId, itemName, item.Rarity);
                return (item.RowId, iconId, item.Rarity, itemName);
            }
            
            Plugin.Log.Warning("Could not find item with ID: {ItemId} (tried: {ActualItemId})", itemId, actualItemId);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error getting item data by ID: {ItemId}", itemId);
        }
        
        return null;
    }
    
    private string CleanItemName(string itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName))
            return itemName;
        
        // Remove common link characters and special markers
        // These characters appear when items are linked in chat (from retainers, market board, etc.)
        var cleaned = itemName;
        
        // Remove any character with code point < 32 (control characters)
        // or specific FFXIV link markers (U+E0BB and similar)
        cleaned = new string(cleaned.Where(c => c >= 32 && c < 0xE000).ToArray());
        
        cleaned = cleaned.Trim();
        
        // Remove surrounding quotes if present
        if (cleaned.StartsWith("\"") && cleaned.EndsWith("\"") && cleaned.Length > 1)
        {
            cleaned = cleaned.Substring(1, cleaned.Length - 2).Trim();
        }
        if (cleaned.StartsWith("'") && cleaned.EndsWith("'") && cleaned.Length > 1)
        {
            cleaned = cleaned.Substring(1, cleaned.Length - 2).Trim();
        }
        
        // Remove unit words that appear in gathering/crafting materials
        // These need to be stripped to match the actual item names in Lumina
        var unitPrefixes = new[] 
        { 
            "chunks of ", "chunk of ",
            "clumps of ", "clump of ",
            "pinches of ", "pinch of ",
            "bottles of ", "bottle of ",
            "pieces of ", "piece of ",
            "phials of ", "phial of ",
            "stalks of ", "stalk of ",
            "handfuls of ", "handful of ",
            "portions of ", "portion of ",
            "sets of ", "set of ",
            "bundles of ", "bundle of ",
            "pots of ", "pot of "
        };
        
        foreach (var prefix in unitPrefixes)
        {
            if (cleaned.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned.Substring(prefix.Length).Trim();
                break;
            }
        }
        
        // Special case: Fix "Tomestones" -> "Tomestone" for currency names
        // Example: "Allagan Tomestones of Heliometry" -> "Allagan Tomestone of Heliometry"
        if (cleaned.Contains("tomestones", StringComparison.OrdinalIgnoreCase))
        {
            // Find and replace "tomestones" with "tomestone" (case-insensitive)
            var index = cleaned.IndexOf("tomestones", StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                cleaned = cleaned.Substring(0, index) + "Tomestone" + cleaned.Substring(index + "tomestones".Length);
            }
        }
        
        // Special case: Fix "Helixes" -> "Helix" for items like "Glass Helixes"
        // Example: "Glass Helixes" -> "Glass Helix"
        if (cleaned.Contains("helixes", StringComparison.OrdinalIgnoreCase))
        {
            // Find and replace "helixes" with "helix" (case-insensitive)
            var index = cleaned.IndexOf("helixes", StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                cleaned = cleaned.Substring(0, index) + "Helix" + cleaned.Substring(index + "helixes".Length);
            }
        }
        
        // Special case: Fix "Leaves" -> "Leaf" for items like "Cupflower Leaves"
        // Example: "Cupflower Leaves" -> "Cupflower Leaf"
        if (cleaned.EndsWith(" leaves", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned.Substring(0, cleaned.Length - 7) + " Leaf";
        }
        
        return cleaned;
    }
    
    private (uint ItemId, uint IconId, uint Rarity, string Name)? FindItemByName(string itemName)
    {
        try
        {
            var itemSheet = Plugin.DataManager.GameData?.GetExcelSheet<Lumina.Excel.Sheets.Item>();
            if (itemSheet == null) return null;
            
            Plugin.Log.Info("Searching for item: '{ItemName}'", itemName);
            
            // Search for item by name (case insensitive)
            var searchName = itemName.ToLower();
            
            // Try exact match first
            foreach (var item in itemSheet)
            {
                var itemNameStr = item.Name.ExtractText();
                if (itemNameStr.Equals(searchName, StringComparison.OrdinalIgnoreCase))
                {
                    Plugin.Log.Info("Found item (exact): ID={ItemId}, Icon={IconId}, Name={Name}", item.RowId, item.Icon, itemNameStr);
                    return (item.RowId, item.Icon, item.Rarity, itemNameStr);
                }
            }
            
            // Try with/without "The" prefix
            // Some items may be searched for with "the" but the actual item name doesn't have it (or vice versa)
            string alternateSearch = searchName;
            if (searchName.StartsWith("the ", StringComparison.OrdinalIgnoreCase))
            {
                // Try without "the " prefix
                alternateSearch = searchName.Substring(4);
                Plugin.Log.Info("Trying without 'the' prefix: '{AlternateName}'", alternateSearch);
                foreach (var item in itemSheet)
                {
                    var itemNameStr = item.Name.ExtractText();
                    if (itemNameStr.Equals(alternateSearch, StringComparison.OrdinalIgnoreCase))
                    {
                        Plugin.Log.Info("Found item (without 'the'): ID={ItemId}, Icon={IconId}, Name={Name}", item.RowId, item.Icon, itemNameStr);
                        return (item.RowId, item.Icon, item.Rarity, itemNameStr);
                    }
                }
            }
            else
            {
                // Try with "the " prefix
                alternateSearch = "the " + searchName;
                Plugin.Log.Info("Trying with 'the' prefix: '{AlternateName}'", alternateSearch);
                foreach (var item in itemSheet)
                {
                    var itemNameStr = item.Name.ExtractText();
                    if (itemNameStr.Equals(alternateSearch, StringComparison.OrdinalIgnoreCase))
                    {
                        Plugin.Log.Info("Found item (with 'the'): ID={ItemId}, Icon={IconId}, Name={Name}", item.RowId, item.Icon, itemNameStr);
                        return (item.RowId, item.Icon, item.Rarity, itemNameStr);
                    }
                }
            }
            
            // Try plural to singular conversions
            var singularForms = new List<string>();
            
            // Handle common plural patterns
            if (searchName.EndsWith("ies", StringComparison.OrdinalIgnoreCase))
            {
                // berries -> berry
                singularForms.Add(searchName.Substring(0, searchName.Length - 3) + "y");
            }
            else if (searchName.EndsWith("ves", StringComparison.OrdinalIgnoreCase))
            {
                // leaves -> leaf, knives -> knife
                singularForms.Add(searchName.Substring(0, searchName.Length - 3) + "f");
                singularForms.Add(searchName.Substring(0, searchName.Length - 3) + "fe");
            }
            else if (searchName.EndsWith("xes", StringComparison.OrdinalIgnoreCase))
            {
                // boxes -> box
                singularForms.Add(searchName.Substring(0, searchName.Length - 2));
            }
            else if (searchName.EndsWith("ses", StringComparison.OrdinalIgnoreCase))
            {
                // glasses -> glass
                singularForms.Add(searchName.Substring(0, searchName.Length - 2));
            }
            else if (searchName.EndsWith("ixes", StringComparison.OrdinalIgnoreCase))
            {
                // helixes -> helix,ixes -> ix
                singularForms.Add(searchName.Substring(0, searchName.Length - 2));
            }
            else if (searchName.EndsWith("s", StringComparison.OrdinalIgnoreCase) && searchName.Length > 2)
            {
                // Basic plural: items -> item
                singularForms.Add(searchName.Substring(0, searchName.Length - 1));
            }
            
            foreach (var singularName in singularForms)
            {
                Plugin.Log.Info("Trying singular form: '{SingularName}'", singularName);
                foreach (var item in itemSheet)
                {
                    var itemNameStr = item.Name.ExtractText();
                    if (itemNameStr.Equals(singularName, StringComparison.OrdinalIgnoreCase))
                    {
                        Plugin.Log.Info("Found item (singular): ID={ItemId}, Icon={IconId}, Name={Name}", item.RowId, item.Icon, itemNameStr);
                        return (item.RowId, item.Icon, item.Rarity, itemNameStr);
                    }
                }
            }
            
            // Try EventItem sheet for special currencies
            var eventItemSheet = Plugin.DataManager.GameData?.GetExcelSheet<Lumina.Excel.Sheets.EventItem>();
            if (eventItemSheet != null)
            {
                Plugin.Log.Info("Searching EventItem sheet...");
                foreach (var item in eventItemSheet)
                {
                    var itemNameStr = item.Name.ExtractText();
                    if (itemNameStr.Equals(searchName, StringComparison.OrdinalIgnoreCase))
                    {
                        Plugin.Log.Info("Found EventItem (exact): ID={ItemId}, Icon={IconId}, Name={Name}", item.RowId, item.Icon, itemNameStr);
                        return (item.RowId, item.Icon, 1, itemNameStr);
                    }
                }
                
                // Try singular for EventItem
                // Case-insensitive check for 's' or 'S' at the end
                if (searchName.Length > 2 && (searchName.EndsWith("s", StringComparison.OrdinalIgnoreCase)))
                {
                    var singularName = searchName.Substring(0, searchName.Length - 1);
                    foreach (var item in eventItemSheet)
                    {
                        var itemNameStr = item.Name.ExtractText();
                        if (itemNameStr.Equals(singularName, StringComparison.OrdinalIgnoreCase))
                        {
                            Plugin.Log.Info("Found EventItem (singular): ID={ItemId}, Icon={IconId}, Name={Name}", item.RowId, item.Icon, itemNameStr);
                            return (item.RowId, item.Icon, 1, itemNameStr);
                        }
                    }
                }
            }
            
            // Last resort: partial match
            foreach (var item in itemSheet)
            {
                var itemNameStr = item.Name.ExtractText();
                if (!string.IsNullOrWhiteSpace(itemNameStr) && 
                    itemNameStr.Contains(searchName, StringComparison.OrdinalIgnoreCase))
                {
                    Plugin.Log.Info("Found item (partial): ID={ItemId}, Icon={IconId}, Name={Name}", item.RowId, item.Icon, itemNameStr);
                    return (item.RowId, item.Icon, item.Rarity, itemNameStr);
                }
            }
            
            Plugin.Log.Warning("Item not found: '{ItemName}'", itemName);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error finding item by name: {ItemName}", itemName);
        }
        
        return null;
    }

    private void AddTestLootItems()
    {
        // Add some test data so the window has something to show
        var testItems = new[]
        {
            new LootItem
            {
                ItemName = "Wind Shard",
                ItemId = 2,
                IconId = 60461,
                Rarity = 1,
                Quantity = 3,
                IsHQ = false,
                PlayerName = "Test Player",
                PlayerContentId = 12345,
                IsOwnLoot = true,
                Source = LootSource.Gathering,
                TerritoryType = 1,
                ZoneName = "Test Zone",
                Timestamp = DateTime.Now.AddMinutes(-5)
            },
            new LootItem
            {
                ItemName = "Mythril Ore",
                ItemId = 3,
                IconId = 21204,
                Rarity = 2,
                Quantity = 1,
                IsHQ = true,
                PlayerName = "Another Player",
                PlayerContentId = 67890,
                IsOwnLoot = false,
                Source = LootSource.Gathering,
                TerritoryType = 1,
                ZoneName = "Test Zone",
                Timestamp = DateTime.Now.AddMinutes(-3)
            },
            new LootItem
            {
                ItemName = "Potion of Strength",
                ItemId = 4,
                IconId = 20601,
                Rarity = 3,
                Quantity = 2,
                IsHQ = false,
                PlayerName = "Test Player",
                PlayerContentId = 12345,
                IsOwnLoot = true,
                Source = LootSource.Monster,
                TerritoryType = 1,
                ZoneName = "Test Zone",
                Timestamp = DateTime.Now.AddMinutes(-1)
            }
        };

        foreach (var item in testItems)
        {
            AddLootItem(item);
        }
    }

    private void AddLootItem(LootItem lootItem)
    {
        // Deduplication: Only prevent duplicates from the SAME event (within 500ms)
        // This catches cases where "You obtain X" and "X is added to your inventory" 
        // fire for the same loot, but allows multiple exchanges/transactions
        var dedupeKey = $"{lootItem.ItemId}_{lootItem.PlayerName}_{lootItem.Quantity}_{lootItem.IsHQ}";
        
        lock (deduplicationLock)
        {
            // Clean up old entries (older than 3 seconds)
            var now = DateTime.Now;
            var expiredKeys = recentlyAddedItems.Where(kvp => (now - kvp.Value).TotalSeconds > 3).Select(kvp => kvp.Key).ToList();
            foreach (var key in expiredKeys)
            {
                recentlyAddedItems.Remove(key);
            }
            
            // Check if this item was just added (within 500ms = same event)
            if (recentlyAddedItems.TryGetValue(dedupeKey, out var lastAddedTime))
            {
                var timeSinceLastAdd = (now - lastAddedTime).TotalMilliseconds;
                if (timeSinceLastAdd < 500) // Same event if under 500ms
                {
                    Plugin.Log.Debug($"Skipping duplicate loot item: {lootItem.ItemName} (added {timeSinceLastAdd:F0}ms ago - same event)");
                    return; // Skip this duplicate
                }
            }
            
            // Record this addition
            recentlyAddedItems[dedupeKey] = now;
        }
        
        lock (lootHistoryLock)
        {
            lootHistory.Insert(0, lootItem); // Add to beginning for newest first

            // Limit history size
            var maxItems = configService.Configuration.MaxDisplayedItems * 2; // Keep more in memory than displayed
            if (lootHistory.Count > maxItems)
            {
                lootHistory.RemoveRange(maxItems, lootHistory.Count - maxItems);
            }
        }

        // Save to persistent history if enabled
        if (configService.Configuration.EnableHistoryTracking && historyService != null)
        {
            try
            {
                historyService.AddItem(lootItem);
                historyService.IncrementDutyItemCount(lootItem.ItemId, lootItem.Quantity, lootItem.IsHQ);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Failed to save item to history");
            }
        }

        // Notify subscribers
        LootObtained?.Invoke(lootItem);

        // Log if debug enabled
        if (configService.Configuration.EnableDebugLogging)
        {
            Plugin.Log.Debug("Loot obtained: {ItemName} x{Quantity} by {PlayerName}", 
                lootItem.ItemName, lootItem.Quantity, lootItem.PlayerName);
        }

        // Show chat notification if enabled
        if (configService.Configuration.ShowChatNotifications && lootItem.IsOwnLoot)
        {
            Plugin.ChatGui.Print($"[LootView] Obtained: {lootItem.ItemName} x{lootItem.Quantity}");
        }
    }

    public void ClearHistory()
    {
        // Save items to persistent history before clearing (if enabled)
        if (configService.Configuration.SaveToHistoryOnClear && 
            configService.Configuration.EnableHistoryTracking && 
            historyService != null)
        {
            try
            {
                lock (lootHistoryLock)
                {
                    if (lootHistory.Any())
                    {
                        Plugin.Log.Info($"Saving {lootHistory.Count} items to persistent history before clearing");
                        historyService.AddItems(lootHistory);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Failed to save items to history before clearing");
            }
        }

        lock (lootHistoryLock)
        {
            lootHistory.Clear();
        }
        Plugin.Log.Info("Loot history cleared");
    }

    public void ClearLoot()
    {
        ClearHistory();
    }

    public void AddTestItems()
    {
        AddTestLootItems();
    }

    public IEnumerable<LootItem> GetFilteredLoot()
    {
        var config = configService.Configuration;
        
        lock (lootHistoryLock)
        {
            var filtered = lootHistory.AsEnumerable();

            // Filter by own loot only
            if (config.ShowOnlyOwnLoot)
            {
                filtered = filtered.Where(l => l.IsOwnLoot);
            }

            // Filter by minimum rarity
            if (config.MinimumRarity > 0)
            {
                filtered = filtered.Where(l => l.Rarity >= config.MinimumRarity);
            }

            // Filter by HQ only
            if (config.ShowHQOnly)
            {
                filtered = filtered.Where(l => l.IsHQ);
            }
            
            // Filter by blacklist
            if (config.BlacklistedItemIds != null && config.BlacklistedItemIds.Count > 0)
            {
                filtered = filtered.Where(l => !config.BlacklistedItemIds.Contains(l.ItemId));
            }

            // Filter by time if auto-hide is enabled
            if (config.AutoHideAfterTime)
            {
                var cutoffTime = DateTime.Now.AddMinutes(-config.AutoHideMinutes);
                filtered = filtered.Where(l => l.Timestamp >= cutoffTime);
            }

            return filtered.Take(config.MaxDisplayedItems).ToList();
        }
    }

    private string CleanPlayerName(string playerName)
    {
        // Remove server name suffix from cross-world players
        // Example: "Elina KhojinSpriggan" -> "Elina Khojin"
        
        if (string.IsNullOrEmpty(playerName))
            return playerName;

        // List of known server names (expand as needed)
        var servers = new[] { "Spriggan", "Phoenix", "Odin", "Shiva", "Lich", "Zodiark", "Twintania", "Ragnarok",
                              "Cerberus", "Louisoix", "Moogle", "Omega", "Chaos", "Midgardsormr", "Adamantoise",
                              "Cactuar", "Faerie", "Gilgamesh", "Jenova", "Sargatanas", "Siren", "Behemoth",
                              "Excalibur", "Exodus", "Famfrit", "Hyperion", "Lamia", "Leviathan", "Ultros",
                              "Balmung", "Brynhildr", "Coeurl", "Diabolos", "Goblin", "Malboro", "Mateus",
                              "Zalera", "Aegis", "Atomos", "Carbuncle", "Garuda", "Gungnir", "Kujata",
                              "Ramuh", "Tonberry", "Typhon", "Alexander", "Bahamut", "Durandal", "Fenrir",
                              "Ifrit", "Ridill", "Tiamat", "Ultima", "Valefor", "Yojimbo", "Zeromus",
                              "Anima", "Asura", "Belias", "Chocobo", "Hades", "Ixion", "Mandragora",
                              "Masamune", "Pandaemonium", "Shinryu", "Titan", "Alpha", "Phantom", "Raiden",
                              "Sagittarius" };

        // Check if the player name ends with a server name (no space between)
        foreach (var server in servers)
        {
            if (playerName.EndsWith(server, StringComparison.OrdinalIgnoreCase))
            {
                // Remove the server suffix
                var cleanName = playerName.Substring(0, playerName.Length - server.Length);
                
                // Add space before the last word if it's missing
                // Example: "ElinaKhojin" -> "Elina Khojin"
                if (cleanName.Length > 0)
                {
                    // Find the last capital letter before the removed server
                    for (int i = cleanName.Length - 1; i > 0; i--)
                    {
                        if (char.IsUpper(cleanName[i]) && i > 0)
                        {
                            cleanName = cleanName.Insert(i, " ");
                            break;
                        }
                    }
                }
                
                return cleanName.Trim();
            }
        }

        // If no server suffix found, return as-is
        return playerName;
    }

    private void ProcessExtractionMessage(string messageText, SeString message)
    {
        // Handle "You have successfully extracted a ItemName" or "extracted an ItemName"
        // Also handle "You successfully extract a ItemName from the ItemName"
        try
        {
            // Try to extract item ID from SeString payload (for linked items)
            uint? itemIdFromPayload = null;
            string itemNameFromPayload = null;
            
            foreach (var payload in message.Payloads)
            {
                if (payload is Dalamud.Game.Text.SeStringHandling.Payloads.ItemPayload itemPayload)
                {
                    itemIdFromPayload = itemPayload.ItemId;
                    itemNameFromPayload = itemPayload.DisplayName;
                    Plugin.Log.Debug("Found item payload in extraction: ID={ItemId}, Name={Name}", itemPayload.ItemId, itemPayload.DisplayName);
                    break;
                }
            }

            // If we have payload data, use it directly
            if (itemIdFromPayload.HasValue && !string.IsNullOrEmpty(itemNameFromPayload))
            {
                var itemDataFromPayload = GetItemDataById(itemIdFromPayload.Value);
                if (itemDataFromPayload.HasValue)
                {
                    var lootItem = new LootItem
                    {
                        ItemName = itemDataFromPayload.Value.Name,
                        ItemId = itemDataFromPayload.Value.ItemId,
                        IconId = itemDataFromPayload.Value.IconId,
                        Rarity = itemDataFromPayload.Value.Rarity,
                        Quantity = 1,
                        IsHQ = false,
                        PlayerName = Plugin.ClientState.LocalPlayer?.Name.ToString() ?? "You",
                        PlayerContentId = Plugin.ClientState.LocalContentId,
                        IsOwnLoot = true,
                        Source = LootSource.Extraction,
                        TerritoryType = Plugin.ClientState.TerritoryType,
                        ZoneName = GetCurrentZoneName()
                    };
                    
                    AddLootItem(lootItem);
                    Plugin.Log.Info($"[Extraction] Found item from payload: ID={itemDataFromPayload.Value.ItemId}, Name={itemDataFromPayload.Value.Name}");
                    return;
                }
            }

            // Fallback: parse from text
            // Try both "extract a " and "extract an " (present tense)
            int startIndex = messageText.IndexOf("extract a ", StringComparison.OrdinalIgnoreCase);
            if (startIndex == -1)
            {
                startIndex = messageText.IndexOf("extract an ", StringComparison.OrdinalIgnoreCase);
                if (startIndex != -1)
                    startIndex += "extract an ".Length;
            }
            else
            {
                startIndex += "extract a ".Length;
            }
            
            // Also try past tense "extracted a/an"
            if (startIndex == -1)
            {
                startIndex = messageText.IndexOf("extracted a ", StringComparison.OrdinalIgnoreCase);
                if (startIndex == -1)
                {
                    startIndex = messageText.IndexOf("extracted an ", StringComparison.OrdinalIgnoreCase);
                    if (startIndex != -1)
                        startIndex += "extracted an ".Length;
                }
                else
                {
                    startIndex += "extracted a ".Length;
                }
            }

            if (startIndex == -1 || startIndex >= messageText.Length)
                return;

            // Get the item name (until " from the" if present, otherwise rest of message)
            string itemName = messageText.Substring(startIndex).Trim();
            
            // Remove " from the ItemName" part if present
            var fromIndex = itemName.IndexOf(" from the ", StringComparison.OrdinalIgnoreCase);
            if (fromIndex > 0)
            {
                itemName = itemName.Substring(0, fromIndex).Trim();
            }
            
            itemName = CleanItemName(itemName);

            if (string.IsNullOrEmpty(itemName))
                return;

            // Try to find the item
            var itemData = FindItemByName(itemName);
            
            if (itemData.HasValue)
            {
                var lootItem = new LootItem
                {
                    ItemName = itemData.Value.Name,
                    ItemId = itemData.Value.ItemId,
                    IconId = itemData.Value.IconId,
                    Rarity = itemData.Value.Rarity,
                    Quantity = 1,
                    IsHQ = false,
                    PlayerName = Plugin.ClientState.LocalPlayer?.Name.ToString() ?? "You",
                    PlayerContentId = Plugin.ClientState.LocalContentId,
                    IsOwnLoot = true,
                    Source = LootSource.Extraction,
                    TerritoryType = Plugin.ClientState.TerritoryType,
                    ZoneName = GetCurrentZoneName()
                };
                
                AddLootItem(lootItem);
                Plugin.Log.Info($"[Extraction] Found item by name: ID={itemData.Value.ItemId}, Icon={itemData.Value.IconId}, Name={itemData.Value.Name}, Rarity={itemData.Value.Rarity}");
            }
            else
            {
                Plugin.Log.Warning($"[Extraction] Could not find item data for: {itemName}");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"Error processing extraction message: {messageText}");
        }
    }

    private void ProcessExchangeMessage(string messageText, SeString message)
    {
        // Handle "You exchange a ItemName for a TargetItem" or "You exchange X ItemName for an TargetItem"
        // Also handle "You exchange X item for 14 chunks of ItemName" (with quantity and prefixes)
        // We only care about the item received (after "for")
        try
        {
            // Try to extract item ID from SeString payload (for linked items)
            uint? itemIdFromPayload = null;
            string itemNameFromPayload = null;
            
            foreach (var payload in message.Payloads)
            {
                if (payload is Dalamud.Game.Text.SeStringHandling.Payloads.ItemPayload itemPayload)
                {
                    itemIdFromPayload = itemPayload.ItemId;
                    itemNameFromPayload = itemPayload.DisplayName;
                    Plugin.Log.Debug("Found item payload in exchange: ID={ItemId}, Name={Name}", itemPayload.ItemId, itemPayload.DisplayName);
                    break;
                }
            }

            // Find the " for " part
            int forIndex = messageText.IndexOf(" for ", StringComparison.OrdinalIgnoreCase);
            if (forIndex == -1 || forIndex >= messageText.Length - 5)
                return;

            // Get everything after " for "
            string afterFor = messageText.Substring(forIndex + 5).Trim();
            
            // Parse quantity and item name
            // Format: "14 chunks of ItemName" or "a ItemName" or "an ItemName"
            uint quantity = 1;
            string itemName;

            // Check if it starts with a number
            var parts = afterFor.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && uint.TryParse(parts[0], out uint parsedQty))
            {
                quantity = parsedQty;
                // Remove the number from the string
                afterFor = parts.Length > 1 ? parts[1].Trim() : "";
            }
            else if (afterFor.StartsWith("a ", StringComparison.OrdinalIgnoreCase))
            {
                afterFor = afterFor.Substring(2).Trim();
            }
            else if (afterFor.StartsWith("an ", StringComparison.OrdinalIgnoreCase))
            {
                afterFor = afterFor.Substring(3).Trim();
            }

            itemName = afterFor;

            // If we have payload data, use it
            if (itemIdFromPayload.HasValue && !string.IsNullOrEmpty(itemNameFromPayload))
            {
                itemName = itemNameFromPayload;
            }

            itemName = CleanItemName(itemName);

            if (string.IsNullOrEmpty(itemName))
                return;

            // Try to find the item
            var itemData = itemIdFromPayload.HasValue ? GetItemDataById(itemIdFromPayload.Value) : FindItemByName(itemName);
            
            if (itemData.HasValue)
            {
                var lootItem = new LootItem
                {
                    ItemName = itemData.Value.Name,
                    ItemId = itemData.Value.ItemId,
                    IconId = itemData.Value.IconId,
                    Rarity = itemData.Value.Rarity,
                    Quantity = quantity,
                    IsHQ = false,
                    PlayerName = Plugin.ClientState.LocalPlayer?.Name.ToString() ?? "You",
                    PlayerContentId = Plugin.ClientState.LocalContentId,
                    IsOwnLoot = true,
                    Source = LootSource.Exchange,
                    TerritoryType = Plugin.ClientState.TerritoryType,
                    ZoneName = GetCurrentZoneName()
                };
                
                AddLootItem(lootItem);
                Plugin.Log.Info($"[Exchange] Found item: ID={itemData.Value.ItemId}, Name={itemData.Value.Name}, Qty={quantity}");
            }
            else
            {
                Plugin.Log.Warning($"[Exchange] Could not find item data for: {itemName}");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"Error processing exchange message: {messageText}");
        }
    }

    private void ProcessRouletteBonusMessage(string messageText, SeString message)
    {
        // Handle "A bonus of 12,000 gil has been awarded for using the duty roulette."
        try
        {
            var localPlayer = Plugin.ClientState.LocalPlayer;
            if (localPlayer == null) return;

            // Extract gil amount from message
            // Pattern: "A bonus of X gil has been awarded"
            var match = System.Text.RegularExpressions.Regex.Match(messageText, @"A bonus of ([\d,]+) gil");
            if (!match.Success)
            {
                Plugin.Log.Debug("Could not parse roulette bonus amount from: {Message}", messageText);
                return;
            }

            var gilAmountStr = match.Groups[1].Value.Replace(",", "");
            if (!uint.TryParse(gilAmountStr, out uint gilAmount))
            {
                Plugin.Log.Warning("Failed to parse gil amount: {Amount}", match.Groups[1].Value);
                return;
            }

            // Gil item ID in FFXIV is 1 (the standard currency)
            var itemData = GetItemDataById(1);
            
            if (itemData.HasValue)
            {
                var lootItem = new LootItem
                {
                    ItemName = itemData.Value.Name, // Just use "Gil"
                    ItemId = itemData.Value.ItemId,
                    IconId = itemData.Value.IconId,
                    Rarity = itemData.Value.Rarity,
                    Quantity = gilAmount,
                    IsHQ = false,
                    PlayerName = localPlayer.Name.TextValue,
                    PlayerContentId = Plugin.ClientState.LocalContentId,
                    IsOwnLoot = true,
                    Source = LootSource.DutyRoulette,
                    TerritoryType = Plugin.ClientState.TerritoryType,
                    ZoneName = GetCurrentZoneName()
                };

                AddLootItem(lootItem);
                Plugin.Log.Info($"Duty Roulette Bonus tracked: {gilAmount:N0} gil");
            }
            else
            {
                Plugin.Log.Warning("Could not find gil item data (ID 1)");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error processing roulette bonus message: {Message}", messageText);
        }
    }

    private void ProcessLootListAddedMessage(string messageText, SeString message)
    {
        // Handle "A pair of demon boots has been added to the loot list."
        // This marks the start of a roll session for an item
        try
        {
            Plugin.Log.Info($"Processing loot list added: {messageText}");
            
            // Extract item from payload
            uint? itemIdFromPayload = null;
            string itemNameFromPayload = null;
            
            foreach (var payload in message.Payloads)
            {
                if (payload is Dalamud.Game.Text.SeStringHandling.Payloads.ItemPayload itemPayload)
                {
                    itemIdFromPayload = itemPayload.ItemId;
                    itemNameFromPayload = itemPayload.DisplayName;
                    Plugin.Log.Info($"Found item payload in loot list: {itemNameFromPayload} (ID: {itemIdFromPayload})");
                    break;
                }
            }

            if (string.IsNullOrEmpty(itemNameFromPayload))
            {
                // Try to parse from text as fallback
                // Formats: 
                // - "A [item name] has been added to the loot list."
                // - "An [item name] has been added to the loot list."  
                // - "[item name] has been added to the loot list." (no article)
                var match = System.Text.RegularExpressions.Regex.Match(messageText, @"^(?:A |An )?(.+?) has been added to the loot list\.");
                if (match.Success)
                {
                    itemNameFromPayload = match.Groups[1].Value;
                    Plugin.Log.Info($"Extracted item from text: {itemNameFromPayload}");
                }
            }

            if (string.IsNullOrEmpty(itemNameFromPayload))
            {
                Plugin.Log.Warning($"Could not extract item name from loot list message: {messageText}");
                return;
            }

            // Get full item data
            var itemData = itemIdFromPayload.HasValue 
                ? GetItemDataById(itemIdFromPayload.Value) 
                : FindItemByName(itemNameFromPayload);

            if (!itemData.HasValue)
            {
                Plugin.Log.Warning($"Could not find item data for: {itemNameFromPayload}");
                return;
            }

            // Create or update roll tracking for this item
            lock (rollLock)
            {
                var itemId = itemData.Value.ItemId;
                var itemName = itemData.Value.Name;
                
                // Always add a new roll session (allows multiple drops of the same item)
                activeRolls.Add(new RollInfo
                {
                    ItemName = itemName,
                    ItemId = itemId,
                    IconId = itemData.Value.IconId,
                    Rarity = itemData.Value.Rarity,
                    RollStartTime = DateTime.Now
                });
                
                Plugin.Log.Info($"Roll session started for: {itemName} (ID: {itemId})");
                Plugin.Log.Info($"Total active roll sessions: {activeRolls.Count}");
                
                // Notify that rolls have been updated
                RollsUpdated?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error processing loot list added message: {Message}", messageText);
        }
    }

    private void ProcessRollMessage(string messageText, SeString message)
    {
        // Handle roll messages:
        // - "You roll Need on the ItemName. 95!"
        // - "You roll Greed on the ItemName. 42!"
        // - "PlayerName rolls Need on the ItemName. 87!"
        // - "You cast your lot for the ItemName." (Pass) - we ignore these
        try
        {
            Plugin.Log.Debug($"ProcessRollMessage called with: {messageText}");
            
            // Extract item from payload
            uint? itemIdFromPayload = null;
            string itemNameFromPayload = null;
            
            foreach (var payload in message.Payloads)
            {
                if (payload is Dalamud.Game.Text.SeStringHandling.Payloads.ItemPayload itemPayload)
                {
                    itemIdFromPayload = itemPayload.ItemId;
                    itemNameFromPayload = itemPayload.DisplayName;
                    Plugin.Log.Debug($"Found item payload: {itemNameFromPayload} (ID: {itemIdFromPayload})");
                    break;
                }
            }

            // Parse roll type and value
            string playerName;
            string rollType;
            int rollValue = 0;
            string itemName = itemNameFromPayload ?? "";

            // Pattern: "You roll Need/Greed on [the/a] ItemName. XX!"
            // Pattern: "PlayerName rolls Need/Greed on [the/a] ItemName. XX!"
            if (messageText.StartsWith("You roll "))
            {
                playerName = Plugin.ClientState.LocalPlayer?.Name.TextValue ?? "You";
                
                if (messageText.Contains("roll Need on "))
                {
                    rollType = "Need";
                    var match = System.Text.RegularExpressions.Regex.Match(messageText, @"\.?\s*(\d+)!");
                    if (match.Success)
                        rollValue = int.Parse(match.Groups[1].Value);
                }
                else if (messageText.Contains("roll Greed on "))
                {
                    rollType = "Greed";
                    var match = System.Text.RegularExpressions.Regex.Match(messageText, @"\.?\s*(\d+)!");
                    if (match.Success)
                        rollValue = int.Parse(match.Groups[1].Value);
                }
                else
                {
                    return; // Pass or unknown
                }
            }
            else if (messageText.Contains(" rolls "))
            {
                // Extract player name - format: "PlayerNameServer rolls Need on the item. 95!"
                var rollsIndex = messageText.IndexOf(" rolls ");
                if (rollsIndex == -1)
                {
                    Plugin.Log.Debug("Could not find ' rolls ' in message");
                    return;
                }
                
                playerName = messageText.Substring(0, rollsIndex).Trim();
                Plugin.Log.Debug($"Extracted player name: {playerName}");
                
                if (messageText.Contains("rolls Need on "))
                {
                    rollType = "Need";
                    // Match the number before the exclamation mark: ". 95!"
                    var match = System.Text.RegularExpressions.Regex.Match(messageText, @"\.\s*(\d+)!");
                    if (match.Success)
                    {
                        rollValue = int.Parse(match.Groups[1].Value);
                        Plugin.Log.Debug($"Extracted Need roll value: {rollValue}");
                    }
                    else
                    {
                        Plugin.Log.Debug($"Could not extract roll value from: {messageText}");
                    }
                }
                else if (messageText.Contains("rolls Greed on "))
                {
                    rollType = "Greed";
                    // Match the number before the exclamation mark: ". 42!"
                    var match = System.Text.RegularExpressions.Regex.Match(messageText, @"\.\s*(\d+)!");
                    if (match.Success)
                    {
                        rollValue = int.Parse(match.Groups[1].Value);
                        Plugin.Log.Debug($"Extracted Greed roll value: {rollValue}");
                    }
                    else
                    {
                        Plugin.Log.Debug($"Could not extract roll value from: {messageText}");
                    }
                }
                else
                {
                    Plugin.Log.Debug($"Not a Need/Greed roll, ignoring");
                    return; // Pass or unknown
                }
            }
            else
            {
                return; // Not a roll message we recognize
            }

            if (string.IsNullOrEmpty(itemName))
            {
                // Try to extract from message text
                var onIndex = messageText.IndexOf(" on ");
                if (onIndex > 0)
                {
                    var afterOn = messageText.Substring(onIndex + 4);
                    // Remove "the " or "a "
                    afterOn = afterOn.Replace("the ", "").Replace("a ", "");
                    // Extract until the period
                    var periodIndex = afterOn.IndexOf('.');
                    if (periodIndex > 0)
                    {
                        itemName = afterOn.Substring(0, periodIndex).Trim();
                    }
                }
            }

            if (string.IsNullOrEmpty(itemName))
                return;

            // Get item data to get the item ID
            var itemData = itemIdFromPayload.HasValue 
                ? GetItemDataById(itemIdFromPayload.Value) 
                : FindItemByName(itemName);

            if (!itemData.HasValue)
            {
                Plugin.Log.Warning($"Could not find item data for roll: {itemName}");
                return;
            }

            var itemId = itemData.Value.ItemId;
            var cleanedPlayerName = CleanPlayerName(playerName);

            // Track the roll - find an active roll for this item that doesn't have this player's roll yet
            lock (rollLock)
            {
                // Find the first roll session for this item that doesn't have this player's roll
                var rollInfo = activeRolls.FirstOrDefault(r => r.ItemId == itemId && !r.PlayerRolls.ContainsKey(cleanedPlayerName));
                
                if (rollInfo == null)
                {
                    // No matching session found, create a new one (shouldn't happen if loot list message was received)
                    rollInfo = new RollInfo
                    {
                        ItemName = itemData.Value.Name,
                        ItemId = itemId,
                        IconId = itemData.Value.IconId,
                        Rarity = itemData.Value.Rarity
                    };
                    activeRolls.Add(rollInfo);
                    Plugin.Log.Warning($"Creating new roll session for {itemData.Value.Name} (loot list message may have been missed)");
                }

                rollInfo.PlayerRolls[cleanedPlayerName] = (rollType, rollValue);
            }

            Plugin.Log.Info($"Roll tracked: {cleanedPlayerName} rolled {rollType} {rollValue} on {itemData.Value.Name}");
            
            // Notify that rolls have been updated
            RollsUpdated?.Invoke();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error processing roll message: {Message}", messageText);
        }
    }

    public void Dispose()
    {
        try
        {
            Plugin.Log.Info("Disposing loot tracking service...");

            // Unsubscribe from chat events
            Plugin.ChatGui.ChatMessage -= OnChatMessage;

            lock (lootHistoryLock)
            {
                lootHistory.Clear();
            }

            Plugin.Log.Info("Loot tracking service disposed");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error disposing loot tracking service");
        }
    }
}

/// <summary>
/// Tracks information about an active loot roll
/// </summary>
public class RollInfo
{
    public string ItemName { get; set; } = string.Empty;
    public uint ItemId { get; set; }
    public uint IconId { get; set; }
    public uint Rarity { get; set; }
    public Dictionary<string, (string RollType, int RollValue)> PlayerRolls { get; set; } = new();
    public DateTime RollStartTime { get; set; } = DateTime.Now;
    public string WinnerName { get; set; } = string.Empty;
    
    /// <summary>
    /// Get rolls sorted by value (highest first), with Need rolls before Greed
    /// </summary>
    public IEnumerable<(string PlayerName, string RollType, int RollValue, bool IsWinner)> GetSortedRolls()
    {
        return PlayerRolls
            .Select(kvp => (
                PlayerName: kvp.Key,
                RollType: kvp.Value.RollType,
                RollValue: kvp.Value.RollValue,
                IsWinner: !string.IsNullOrEmpty(WinnerName) && kvp.Key == WinnerName
            ))
            .OrderByDescending(r => r.RollType == "Need" ? 1 : 0) // Need before Greed
            .ThenByDescending(r => r.RollValue); // Then by roll value
    }
}