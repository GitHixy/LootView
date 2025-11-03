using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Lumina.Excel.Sheets;

namespace LootView.Services;

/// <summary>
/// Service for retrieving loot table information for zones and duties via external APIs
/// </summary>
public class LootTableService : IDisposable
{
    private static readonly HttpClient httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    public class LootTableEntry
    {
        public uint ItemId { get; set; }
        public string ItemName { get; set; } = string.Empty;
        public uint IconId { get; set; }
        public int Rarity { get; set; }
        public int ItemLevel { get; set; }
        public string Category { get; set; } = string.Empty;
        public string DropRate { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
    }

    public class ZoneLootTable
    {
        public string ZoneName { get; set; } = string.Empty;
        public ushort TerritoryId { get; set; }
        public uint ContentFinderConditionId { get; set; }
        public List<LootTableEntry> Items { get; set; } = new();
        public bool IsLoading { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
    }

    // API Response models for Garland Tools
    private class GarlandInstanceData
    {
        [JsonPropertyName("instance")]
        public GarlandInstance Instance { get; set; } = new();
        
        [JsonPropertyName("partials")]
        public List<GarlandPartial> Partials { get; set; } = new();
    }

    private class GarlandInstance
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }
        
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
        
        [JsonPropertyName("rewards")]
        public List<int> Rewards { get; set; } = new();
        
        [JsonPropertyName("fights")]
        public List<GarlandFight> Fights { get; set; } = new();
        
        [JsonPropertyName("coffers")]
        public List<GarlandCoffer> Coffers { get; set; } = new();
    }

    private class GarlandFight
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
        
        [JsonPropertyName("coffer")]
        public GarlandCoffer? Coffer { get; set; }
    }

    private class GarlandCoffer
    {
        [JsonPropertyName("items")]
        public List<int> Items { get; set; } = new();
    }

    private class GarlandPartial
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;
        
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;
        
        [JsonPropertyName("obj")]
        public GarlandItemObj? Obj { get; set; }
    }

    private class GarlandItemObj
    {
        [JsonPropertyName("i")]
        public int ItemId { get; set; }
        
        [JsonPropertyName("n")]
        public string Name { get; set; } = string.Empty;
        
        // Level can be either an int (for items) or string (for quests with location name)
        // We'll just ignore it since we get level from game data anyway
        
        [JsonPropertyName("c")]
        public int IconId { get; set; }
        
        [JsonPropertyName("t")]
        public int Type { get; set; }
    }

    private readonly Dictionary<uint, ZoneLootTable> cachedLootTables = new();
    private readonly Dictionary<uint, Task<ZoneLootTable>> loadingTasks = new();

    public LootTableService()
    {
        httpClient.DefaultRequestHeaders.Add("User-Agent", "LootView-FFXIV-Plugin/1.1.0");
        Plugin.Log.Info("LootTableService initialized with API support");
    }

    /// <summary>
    /// Get the loot table for the current zone/duty (async)
    /// </summary>
    public async Task<ZoneLootTable> GetCurrentZoneLootTableAsync()
    {
        try
        {
            var territoryId = Plugin.ClientState.TerritoryType;
            if (territoryId == 0)
            {
                Plugin.Log.Debug("Not in a valid territory");
                return new ZoneLootTable
                {
                    ZoneName = "Unknown",
                    ErrorMessage = "Not in a valid territory"
                };
            }

            return await GetZoneLootTableAsync(territoryId);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to get current zone loot table");
            return new ZoneLootTable
            {
                ZoneName = "Error",
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Get the loot table for a zone by ContentFinderCondition ID (async)
    /// </summary>
    public async Task<ZoneLootTable> GetZoneLootTableByIdAsync(uint contentFinderConditionId)
    {
        try
        {
            var contentFinderSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ContentFinderCondition>();
            if (contentFinderSheet == null || !contentFinderSheet.TryGetRow(contentFinderConditionId, out var cfc))
            {
                Plugin.Log.Warning($"ContentFinderCondition {contentFinderConditionId} not found");
                return new ZoneLootTable
                {
                    ZoneName = "Unknown",
                    ErrorMessage = "Zone not found"
                };
            }

            var territoryId = (ushort)cfc.TerritoryType.RowId;
            return await GetZoneLootTableAsync(territoryId);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"Failed to get loot table for ContentFinderCondition {contentFinderConditionId}");
            return new ZoneLootTable
            {
                ZoneName = "Error",
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Get the loot table for a specific territory (async)
    /// </summary>
    public async Task<ZoneLootTable> GetZoneLootTableAsync(ushort territoryId)
    {
        try
        {
            // Check cache first
            if (cachedLootTables.TryGetValue(territoryId, out var cached))
            {
                Plugin.Log.Debug($"Returning cached loot table for territory {territoryId}");
                return cached;
            }

            // Check if already loading (without lock to avoid await in lock)
            Task<ZoneLootTable> existingTask = null;
            lock (loadingTasks)
            {
                if (loadingTasks.TryGetValue(territoryId, out existingTask))
                {
                    Plugin.Log.Debug($"Found existing load task for territory {territoryId}");
                }
            }

            if (existingTask != null)
            {
                return await existingTask;
            }

            // Start loading
            var task = LoadZoneLootTableAsync(territoryId);
            lock (loadingTasks)
            {
                loadingTasks[territoryId] = task;
            }

            var result = await task;

            lock (loadingTasks)
            {
                loadingTasks.Remove(territoryId);
            }

            return result;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"Failed to get loot table for territory {territoryId}");
            return new ZoneLootTable
            {
                TerritoryId = territoryId,
                ZoneName = "Error",
                ErrorMessage = ex.Message
            };
        }
    }

    private async Task<ZoneLootTable> LoadZoneLootTableAsync(ushort territoryId)
    {
        var lootTable = new ZoneLootTable
        {
            TerritoryId = territoryId,
            IsLoading = true
        };

        try
        {
            var territorySheet = Plugin.DataManager.GetExcelSheet<TerritoryType>();
            if (territorySheet == null)
            {
                lootTable.ErrorMessage = "Failed to load territory data";
                return lootTable;
            }

            if (!territorySheet.TryGetRow(territoryId, out var territory))
            {
                lootTable.ErrorMessage = $"Territory {territoryId} not found";
                return lootTable;
            }

            lootTable.ZoneName = GetZoneName(territory);
            
            Plugin.Log.Info($"Territory {territoryId} - {lootTable.ZoneName}");
            Plugin.Log.Info($"  ContentFinderCondition ID: {territory.ContentFinderCondition.RowId}");
            Plugin.Log.Info($"  TerritoryIntendedUse: {territory.TerritoryIntendedUse}");

            // Try to get content finder condition for duties
            if (territory.ContentFinderCondition.RowId > 0)
            {
                var cfcSheet = Plugin.DataManager.GetExcelSheet<ContentFinderCondition>();
                if (cfcSheet != null && cfcSheet.TryGetRow(territory.ContentFinderCondition.RowId, out var cfc))
                {
                    lootTable.ContentFinderConditionId = cfc.RowId;
                    Plugin.Log.Info($"Found ContentFinderCondition {cfc.RowId} for {lootTable.ZoneName}");
                    Plugin.Log.Info($"  CFC Name: {cfc.Name}");
                    Plugin.Log.Info($"  Content RowId: {cfc.Content.RowId}");
                    Plugin.Log.Info($"  Item Level: {cfc.ItemLevelRequired}");
                    Plugin.Log.Info($"  Content Type: {cfc.ContentType.RowId}");
                    
                    // Try to fetch from API
                    await FetchLootFromAPIAsync(cfc, lootTable);
                }
                else
                {
                    Plugin.Log.Warning($"Could not find ContentFinderCondition {territory.ContentFinderCondition.RowId}");
                }
            }
            else
            {
                Plugin.Log.Info("Territory has no ContentFinderCondition (likely overworld zone)");
            }

            // If no items found, provide fallback info
            if (lootTable.Items.Count == 0)
            {
                AddFallbackInfo(lootTable, territory);
            }

            lootTable.IsLoading = false;
            
            // Cache the result
            cachedLootTables[territoryId] = lootTable;
            
            Plugin.Log.Info($"Loaded loot table for {lootTable.ZoneName} with {lootTable.Items.Count} items");
            return lootTable;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"Failed to load loot table for territory {territoryId}");
            lootTable.IsLoading = false;
            lootTable.ErrorMessage = ex.Message;
            return lootTable;
        }
    }

    private async Task FetchLootFromAPIAsync(ContentFinderCondition cfc, ZoneLootTable lootTable)
    {
        try
        {
            var instanceId = cfc.Content.RowId;
            Plugin.Log.Info($"Fetching loot data for ContentFinder {cfc.RowId}, Instance {instanceId}, iLevel {cfc.ItemLevelRequired}");
            
            if (instanceId > 0)
            {
                // Correct Garland Tools API endpoint: /db/doc/instance/{lang}/{api_version}/{instance_id}.json
                var url = $"https://www.garlandtools.org/db/doc/instance/en/2/{instanceId}.json";
                Plugin.Log.Info($"Requesting Garland Tools: {url}");
                
                var response = await httpClient.GetAsync(url);
                Plugin.Log.Info($"API Response: {response.StatusCode}");
                
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    Plugin.Log.Debug($"Received JSON (length: {json.Length})");
                    
                    var data = JsonSerializer.Deserialize<GarlandInstanceData>(json);
                    
                    if (data?.Instance != null && data.Instance.Rewards != null && data.Instance.Rewards.Count > 0)
                    {
                        Plugin.Log.Info($"✅ Successfully parsed Garland data: {data.Instance.Name} with {data.Instance.Rewards.Count} rewards, {data.Partials.Count} partials");
                        await ProcessGarlandDataAsync(data, lootTable);
                        Plugin.Log.Info($"Successfully fetched {lootTable.Items.Count} items from Garland Tools");
                        return;
                    }
                    else
                    {
                        Plugin.Log.Warning("Garland data was null or had no rewards");
                    }
                }
                else
                {
                    Plugin.Log.Warning($"Garland Tools API returned status {response.StatusCode}");
                }
            }
            else
            {
                Plugin.Log.Info("Instance ID is 0, skipping API request");
            }

            // Fallback
            Plugin.Log.Info("Using fallback game data");
            InferItemsFromGameData(cfc, lootTable);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to fetch loot from API");
            InferItemsFromGameData(cfc, lootTable);
        }
    }

    private async Task ProcessGarlandDataAsync(GarlandInstanceData data, ZoneLootTable lootTable)
    {
        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        if (itemSheet == null) return;

        var instance = data.Instance;
        var processedItems = new HashSet<uint>();

        // Build sets of items from different sources for better categorization
        var bossDropItems = new HashSet<int>();
        var treasureCofferItems = new HashSet<int>();
        
        // Collect boss fight drops
        foreach (var fight in instance.Fights)
        {
            if (fight.Coffer?.Items != null)
            {
                foreach (var itemId in fight.Coffer.Items)
                {
                    bossDropItems.Add(itemId);
                }
            }
        }
        
        // Collect treasure coffer items
        foreach (var coffer in instance.Coffers)
        {
            foreach (var itemId in coffer.Items)
            {
                treasureCofferItems.Add(itemId);
            }
        }
        
        Plugin.Log.Info($"Found {bossDropItems.Count} boss drop items, {treasureCofferItems.Count} treasure coffer items");

        // Process all rewards from the instance
        foreach (var itemId in instance.Rewards)
        {
            if (processedItems.Contains((uint)itemId)) continue;
            
            if (itemSheet.TryGetRow((uint)itemId, out var item))
            {
                // Determine the most specific source
                var source = GetItemSource(instance, itemId);
                
                // Override source for completion rewards (items not in any coffer)
                if (!bossDropItems.Contains(itemId) && !treasureCofferItems.Contains(itemId))
                {
                    source = "Completion Reward";
                }
                
                var entry = new LootTableEntry
                {
                    ItemId = (uint)itemId,
                    ItemName = item.Name.ToString(),
                    IconId = item.Icon,
                    Rarity = item.Rarity,
                    ItemLevel = item.LevelItem.RowId > 0 ? (int)item.LevelItem.RowId : 0,
                    Category = GetItemCategory(item),
                    Source = source
                };

                lootTable.Items.Add(entry);
                processedItems.Add((uint)itemId);
            }
        }

        Plugin.Log.Info($"Processed {lootTable.Items.Count} items from rewards list");

        // Sort by source type (bosses first, then treasure, then completion), then rarity, then name
        lootTable.Items = lootTable.Items
            .OrderBy(e => e.Source.Contains("Completion") ? 2 : (e.Source.Contains("Treasure") ? 1 : 0))
            .ThenByDescending(e => e.Rarity)
            .ThenBy(e => e.ItemName)
            .ToList();

        await Task.CompletedTask;
    }

    private string GetItemSource(GarlandInstance instance, int itemId)
    {
        // Check boss fights
        foreach (var fight in instance.Fights)
        {
            if (fight.Coffer?.Items.Contains(itemId) == true)
            {
                return fight.Name;
            }
        }
        
        // Check treasure coffers
        foreach (var coffer in instance.Coffers)
        {
            if (coffer.Items.Contains(itemId))
            {
                return "Treasure Coffer";
            }
        }
        
        return "Duty Reward";
    }

    private string GetItemCategory(Item item)
    {
        var categoryId = item.ItemUICategory.RowId;
        var categorySheet = Plugin.DataManager.GetExcelSheet<ItemUICategory>();
        
        if (categorySheet != null && categorySheet.TryGetRow(categoryId, out var category))
        {
            return category.Name.ToString();
        }
        
        return "Unknown";
    }

    private void InferItemsFromGameData(ContentFinderCondition cfc, ZoneLootTable lootTable)
    {
        try
        {
            var itemLevel = cfc.ItemLevelRequired;
            var dutyName = cfc.Name.ToString();
            
            lootTable.Items.Add(new LootTableEntry
            {
                ItemId = 0,
                ItemName = $"📋 {dutyName}",
                Category = "Duty Information",
                Source = "Game Data"
            });
            
            lootTable.Items.Add(new LootTableEntry
            {
                ItemId = 0,
                ItemName = $"⚔️ Item Level: {itemLevel}",
                Category = "Duty Information",
                Source = "Game Data"
            });

            lootTable.Items.Add(new LootTableEntry
            {
                ItemId = 0,
                ItemName = "❌ Loot table data not available from external sources",
                Category = "Status",
                Source = "System"
            });

            lootTable.Items.Add(new LootTableEntry
            {
                ItemId = 0,
                ItemName = "💡 Check external databases: Gamer Escape, Consolegameswiki, or Teamcraft",
                Category = "Suggestion",
                Source = "System"
            });
            
            lootTable.Items.Add(new LootTableEntry
            {
                ItemId = 0,
                ItemName = "💡 Loot will still be tracked in your Statistics & History",
                Category = "Suggestion",
                Source = "System"
            });
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to infer items from game data");
        }
    }

    private void AddFallbackInfo(ZoneLootTable lootTable, TerritoryType territory)
    {
        var zoneType = territory.ContentFinderCondition.RowId > 0 ? "Duty/Instance" : "Overworld Zone";
        var intendedUse = territory.TerritoryIntendedUse;
        
        lootTable.Items.Add(new LootTableEntry
        {
            ItemId = 0,
            ItemName = $"Zone Type: {zoneType}",
            Category = "Information",
            Source = "Game Data"
        });

        lootTable.Items.Add(new LootTableEntry
        {
            ItemId = 0,
            ItemName = $"Territory Use: {intendedUse}",
            Category = "Information",
            Source = "Game Data"
        });

        lootTable.Items.Add(new LootTableEntry
        {
            ItemId = 0,
            ItemName = "No loot table data available from external sources",
            Category = "Information",
            Source = "System"
        });

        if (territory.ContentFinderCondition.RowId == 0)
        {
            lootTable.Items.Add(new LootTableEntry
            {
                ItemId = 0,
                ItemName = "💡 Tip: Loot tables work best for dungeons, trials, and raids",
                Category = "Information",
                Source = "System"
            });
        }
        else
        {
            lootTable.Items.Add(new LootTableEntry
            {
                ItemId = 0,
                ItemName = "💡 This duty's loot data may not be in the external database yet",
                Category = "Information",
                Source = "System"
            });
        }
    }

    private string GetZoneName(TerritoryType territory)
    {
        try
        {
            var placeNameSheet = Plugin.DataManager.GetExcelSheet<PlaceName>();
            if (placeNameSheet != null)
            {
                if (placeNameSheet.TryGetRow(territory.PlaceName.RowId, out var placeName))
                {
                    var name = placeName.Name.ToString();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        return name;
                    }
                }
            }

            return $"Territory #{territory.RowId}";
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to get zone name");
            return $"Territory #{territory.RowId}";
        }
    }

    public void ClearCache()
    {
        cachedLootTables.Clear();
        Plugin.Log.Info("Loot table cache cleared");
    }

    public void Dispose()
    {
        cachedLootTables.Clear();
        loadingTasks.Clear();
        Plugin.Log.Info("LootTableService disposed");
    }
}
