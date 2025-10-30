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
            
            // Detect loot messages:
            // - "You obtain X ItemName" (your loot)
            // - "PlayerName obtains X ItemName" (party member loot)
            // - "You have successfully extracted a ItemName" (aetherial reduction, desynthesis)
            // - "You exchange X ItemName for a ItemName" (vendor trades)
            if (messageText.Contains("You obtain") || messageText.Contains(" obtains "))
            {
                ProcessObtainMessage(messageText, message);
            }
            else if (messageText.Contains("successfully extract"))
            {
                ProcessExtractionMessage(messageText, message);
            }
            else if (messageText.Contains("You exchange") && messageText.Contains(" for "))
            {
                ProcessExchangeMessage(messageText, message);
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
                }
                else
                {
                    // Fallback
                    playerName = localPlayer.Name.TextValue;
                    isOwnLoot = true;
                    remaining = messageText.Replace("You obtain ", "").Trim();
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
                                           "bottles of ", "bottle of ", "pieces of ", "piece of " };
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
                    var unitWords = new[] { "chunk of ", "pinch of ", "bottle of ", "piece of " };
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
                         quantityPart.Equals("pieces", StringComparison.OrdinalIgnoreCase))
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

            AddLootItem(lootItem);
            
            Plugin.Log.Info($"Loot tracked: {playerName} obtained {itemName} x{quantity}" + (isHQ ? " HQ" : ""));
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
        
        return cleaned.Trim();
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
            
            // Try without 's' at the end (plural -> singular)
            // Case-insensitive check for 's' or 'S' at the end
            if (searchName.Length > 2 && (searchName.EndsWith("s", StringComparison.OrdinalIgnoreCase)))
            {
                var singularName = searchName.Substring(0, searchName.Length - 1);
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
        try
        {
            // Try both "extracted a " and "extracted an "
            int startIndex = messageText.IndexOf("extracted a ", StringComparison.OrdinalIgnoreCase);
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

            if (startIndex == -1 || startIndex >= messageText.Length)
                return;

            // Get the item name (rest of the message after "extracted a/an")
            string itemName = messageText.Substring(startIndex).Trim();
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
        // We only care about the item received (after "for")
        try
        {
            // Try both "for a " and "for an " (note: these patterns also match "exchange a X for a Y")
            int startIndex = messageText.IndexOf("for a ", StringComparison.OrdinalIgnoreCase);
            if (startIndex == -1)
            {
                startIndex = messageText.IndexOf("for an ", StringComparison.OrdinalIgnoreCase);
                if (startIndex != -1)
                    startIndex += "for an ".Length;
            }
            else
            {
                startIndex += "for a ".Length;
            }

            if (startIndex == -1 || startIndex >= messageText.Length)
                return;

            // Get the received item name (rest of the message after "for a/an")
            string itemName = messageText.Substring(startIndex).Trim();
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
                    Source = LootSource.Exchange,
                    TerritoryType = Plugin.ClientState.TerritoryType,
                    ZoneName = GetCurrentZoneName()
                };
                
                AddLootItem(lootItem);
                Plugin.Log.Info($"[Exchange] Found item by name: ID={itemData.Value.ItemId}, Icon={itemData.Value.IconId}, Name={itemData.Value.Name}, Rarity={itemData.Value.Rarity}");
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