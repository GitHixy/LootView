using System;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Components;
using LootView.Services;

namespace LootView.Windows;

/// <summary>
/// Window for displaying zone/duty loot tables
/// </summary>
public class LootTableWindow : Window
{
    private readonly Plugin plugin;
    private LootTableService.ZoneLootTable currentLootTable;
    private bool isLoading;
    private Task<LootTableService.ZoneLootTable> loadingTask;

    public LootTableWindow(Plugin plugin) : base("Zone Loot Table###LootTableWindow")
    {
        this.plugin = plugin;
        
        SizeConstraintMin = new Vector2(700, 500);
        SizeConstraintMax = new Vector2(1600, 1200);
        Size = new Vector2(1000, 700);
        
        currentLootTable = null;
        isLoading = false;
    }

    public void LoadCurrentZone()
    {
        if (isLoading) return;

        isLoading = true;
        loadingTask = plugin.LootTableService.GetCurrentZoneLootTableAsync();
    }

    public void LoadZoneById(uint contentFinderConditionId)
    {
        if (isLoading) return;

        isLoading = true;
        loadingTask = plugin.LootTableService.GetZoneLootTableByIdAsync(contentFinderConditionId);
    }

    protected override void DrawContents()
    {
        try
        {
            // Check if we have a loading task
            if (loadingTask != null && loadingTask.IsCompleted)
            {
                currentLootTable = loadingTask.Result;
                loadingTask = null;
                isLoading = false;
            }

            // Header with zone name and refresh button
            DrawHeader();

            ImGui.Separator();
            ImGui.Spacing();

            // Content
            if (isLoading)
            {
                DrawLoadingState();
            }
            else if (currentLootTable == null)
            {
                DrawEmptyState();
            }
            else if (!string.IsNullOrEmpty(currentLootTable.ErrorMessage))
            {
                DrawErrorState();
            }
            else
            {
                DrawLootTable();
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error drawing loot table window");
            ImGui.TextColored(new Vector4(1, 0, 0, 1), "Error displaying loot table");
            ImGui.Text(ex.Message);
        }
    }

    private void DrawHeader()
    {
        var zoneName = currentLootTable?.ZoneName ?? "No Zone Selected";
        
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.Text(FontAwesomeIcon.Table.ToIconString());
        }
        
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.6f, 0.8f, 1.0f, 1), zoneName);
        
        ImGui.SameLine();
        var refreshButtonPos = ImGui.GetContentRegionAvail().X - 100;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + refreshButtonPos);
        
        if (ImGuiComponents.IconButton("RefreshLootTable", FontAwesomeIcon.Sync))
        {
            LoadCurrentZone();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Refresh loot table for current zone");
        }
    }

    private void DrawLoadingState()
    {
        var windowSize = ImGui.GetWindowSize();
        var textSize = ImGui.CalcTextSize("Loading loot table...");
        
        ImGui.SetCursorPos(new Vector2(
            (windowSize.X - textSize.X) / 2,
            (windowSize.Y - textSize.Y) / 2
        ));
        
        ImGui.TextColored(new Vector4(0.6f, 0.8f, 1.0f, 1), "Loading loot table...");
        
        // Spinning icon
        ImGui.SetCursorPos(new Vector2(
            (windowSize.X - 20) / 2,
            (windowSize.Y) / 2 + 30
        ));
        
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.Text(FontAwesomeIcon.Spinner.ToIconString());
        }
    }

    private void DrawEmptyState()
    {
        var windowSize = ImGui.GetWindowSize();
        
        ImGui.SetCursorPos(new Vector2(20, (windowSize.Y - 100) / 2));
        
        ImGui.PushTextWrapPos(windowSize.X - 40);
        ImGui.TextWrapped("No loot table loaded. Enter a duty or instance and click the refresh button to load the loot table for that zone.");
        ImGui.PopTextWrapPos();
        
        ImGui.Spacing();
        ImGui.Spacing();
        
        if (ImGui.Button("Load Current Zone", new Vector2(200, 30)))
        {
            LoadCurrentZone();
        }
    }

    private void DrawErrorState()
    {
        ImGui.TextColored(new Vector4(1, 0.5f, 0, 1), "Failed to load loot table");
        ImGui.Spacing();
        ImGui.TextWrapped(currentLootTable.ErrorMessage);
        ImGui.Spacing();
        ImGui.Spacing();
        
        if (ImGui.Button("Try Again"))
        {
            LoadCurrentZone();
        }
    }

    private void DrawLootTable()
    {
        if (currentLootTable.Items.Count == 0)
        {
            ImGui.TextColored(new Vector4(1, 1, 0, 1), "No loot data available for this zone");
            return;
        }

        // Info header with stats
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.8f, 0.9f, 1.0f, 1.0f));
        ImGui.Text($"📦 {currentLootTable.Items.Count} items");
        ImGui.SameLine();
        ImGui.Text($" | ");
        ImGui.SameLine();
        ImGui.Text($"🎯 Content Finder: {currentLootTable.ContentFinderConditionId}");
        ImGui.SameLine();
        ImGui.Text($" | ");
        ImGui.SameLine();
        ImGui.Text($"🗺️ Territory: {currentLootTable.TerritoryId}");
        ImGui.PopStyleColor();
        
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Calculate available height for table (leave space for header and padding)
        var availableHeight = ImGui.GetContentRegionAvail().Y - 10;

        // Create scrollable table with proper column sizing
        if (ImGui.BeginTable("LootTableTable", 6, 
            ImGuiTableFlags.Borders | 
            ImGuiTableFlags.RowBg | 
            ImGuiTableFlags.ScrollY | 
            ImGuiTableFlags.Sortable |
            ImGuiTableFlags.Resizable |
            ImGuiTableFlags.SizingFixedFit,
            new Vector2(0, availableHeight)))
        {
            // Setup columns - icon and ilvl are small and fixed, rest stretch proportionally
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort | ImGuiTableColumnFlags.NoResize, 36);
            ImGui.TableSetupColumn("Item Name", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("iLvl", ImGuiTableColumnFlags.WidthFixed, 40);
            ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthFixed, 120);
            ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthFixed, 180);
            ImGui.TableSetupColumn("Rarity", ImGuiTableColumnFlags.WidthFixed, 75);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            // Draw items
            foreach (var item in currentLootTable.Items)
            {
                ImGui.TableNextRow();

                var itemColor = GetRarityColor(item.Rarity);

                // Icon column
                ImGui.TableSetColumnIndex(0);
                if (item.ItemId > 0 && item.IconId > 0)
                {
                    try
                    {
                        var iconTexture = Plugin.TextureProvider.GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(item.IconId)).GetWrapOrDefault();
                        if (iconTexture != null)
                        {
                            ImGui.Image(iconTexture.Handle, new Vector2(32, 32));
                            if (ImGui.IsItemHovered())
                            {
                                ImGui.SetTooltip($"{item.ItemName}\nItem Level: {item.ItemLevel}\n{item.Category}\nSource: {item.Source}");
                            }
                        }
                    }
                    catch { /* Ignore icon loading errors */ }
                }

                // Item name column
                ImGui.TableSetColumnIndex(1);
                ImGui.TextColored(itemColor, item.ItemName);

                // Item Level column
                ImGui.TableSetColumnIndex(2);
                if (item.ItemLevel > 0)
                {
                    ImGui.Text(item.ItemLevel.ToString());
                }

                // Category column
                ImGui.TableSetColumnIndex(3);
                ImGui.TextColored(new Vector4(0.85f, 0.85f, 0.85f, 1), item.Category);

                // Source column
                ImGui.TableSetColumnIndex(4);
                ImGui.TextColored(new Vector4(0.7f, 0.9f, 0.7f, 1), item.Source);

                // Rarity column
                ImGui.TableSetColumnIndex(5);
                if (item.ItemId > 0)
                {
                    ImGui.TextColored(itemColor, GetRarityText(item.Rarity));
                }
            }

            ImGui.EndTable();
        }
    }

    private Vector4 GetRarityColor(int rarity)
    {
        return rarity switch
        {
            1 => new Vector4(1.0f, 1.0f, 1.0f, 1.0f),     // Common (white)
            2 => new Vector4(0.2f, 1.0f, 0.2f, 1.0f),     // Uncommon (green)
            3 => new Vector4(0.2f, 0.5f, 1.0f, 1.0f),     // Rare (blue)
            4 => new Vector4(0.64f, 0.21f, 0.93f, 1.0f),  // Relic (purple)
            7 => new Vector4(0.95f, 0.68f, 0.95f, 1.0f),  // Aetherial (pink)
            _ => new Vector4(0.8f, 0.8f, 0.8f, 1.0f)      // Default (gray)
        };
    }

    private string GetRarityText(int rarity)
    {
        return rarity switch
        {
            1 => "Common",
            2 => "Uncommon",
            3 => "Rare",
            4 => "Relic",
            7 => "Aetherial",
            _ => ""
        };
    }

    public override void Dispose()
    {
        base.Dispose();
    }
}
