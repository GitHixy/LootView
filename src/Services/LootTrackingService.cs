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

    public event Action<LootItem> LootObtained;

    public LootTrackingService(ConfigurationService configService)
    {
        this.configService = configService;
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
            
            // Detect "You obtain" messages
            if (messageText.Contains("You obtain"))
            {
                ProcessObtainMessage(messageText, message);
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

            // Parse "You obtain X ItemName." or "You obtain X ItemName HQ."
            // Example: "You obtain 3 wind shards."
            // Example: "You obtain a potion."
            
            string itemName = itemNameFromPayload;
            uint quantity = 1;
            bool isHQ = messageText.Contains(" HQ");
            
            // Remove "You obtain " from the start
            var remaining = messageText.Replace("You obtain ", "").Trim();
            
            // Check for quantity (number at start)
            var parts = remaining.Split(' ', 2);
            if (parts.Length >= 2 && uint.TryParse(parts[0], out var parsedQty))
            {
                quantity = parsedQty;
                itemName = parts[1].TrimEnd('.', ' ');
            }
            else if (remaining.StartsWith("a ") || remaining.StartsWith("an "))
            {
                // "a potion" or "an item"
                quantity = 1;
                itemName = remaining.Substring(remaining.IndexOf(' ') + 1).TrimEnd('.', ' ');
            }
            else
            {
                itemName = remaining.TrimEnd('.', ' ');
            }
            
            // Remove HQ suffix if present
            if (isHQ)
            {
                itemName = itemName.Replace(" HQ", "").Trim();
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
                itemName = ToTitleCase(itemName);
                itemData = FindItemByName(itemName);
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
            
            Plugin.Log.Info($"Loot tracked: {itemName} x{quantity}" + (isHQ ? " HQ" : ""));
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error processing obtain message: {Message}", message);
        }
    }

    private string GetCurrentZoneName()
    {
        try
        {
            // For now, return a simple zone name - we'll implement proper data loading later
            return $"Zone {Plugin.ClientState.TerritoryType}";
        }
        catch
        {
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
                Plugin.Log.Debug("Found item: ID={ItemId}, Icon={IconId}, Name={Name}", item.RowId, iconId, itemName);
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
        
        return cleaned.Trim();
    }
    
    private (uint ItemId, uint IconId, uint Rarity, string Name)? FindItemByName(string itemName)
    {
        try
        {
            var itemSheet = Plugin.DataManager.GameData?.GetExcelSheet<Lumina.Excel.Sheets.Item>();
            if (itemSheet == null) return null;
            
            // Search for item by name (case insensitive)
            var searchName = itemName.ToLower();
            foreach (var item in itemSheet)
            {
                var itemNameStr = item.Name.ExtractText();
                if (itemNameStr.Equals(searchName, StringComparison.OrdinalIgnoreCase))
                {
                    return (item.RowId, item.Icon, item.Rarity, itemNameStr);
                }
            }
            
            // If exact match not found, try partial match
            foreach (var item in itemSheet)
            {
                var itemNameStr = item.Name.ExtractText();
                if (!string.IsNullOrWhiteSpace(itemNameStr) && 
                    itemNameStr.Contains(searchName, StringComparison.OrdinalIgnoreCase))
                {
                    return (item.RowId, item.Icon, item.Rarity, itemNameStr);
                }
            }
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

            // Filter by time if auto-hide is enabled
            if (config.AutoHideAfterTime)
            {
                var cutoffTime = DateTime.Now.AddMinutes(-config.AutoHideMinutes);
                filtered = filtered.Where(l => l.Timestamp >= cutoffTime);
            }

            return filtered.Take(config.MaxDisplayedItems).ToList();
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