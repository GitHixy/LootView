using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using LootView.Models;

namespace LootView.Windows;

/// <summary>
/// Main loot display window - shows loot in a fancy customizable way
/// </summary>
public class LootWindow : Window
{
    private readonly Plugin plugin;

    public LootWindow(Plugin plugin) : base("LootView - Tracker by GitHixy###LootViewMain")
    {
        this.plugin = plugin;
        
        // Set initial visibility from config
        IsOpen = plugin.ConfigService.Configuration.IsVisible;
        
        // Set window constraints
        SizeConstraintMin = new Vector2(400, 300);
        SizeConstraintMax = new Vector2(1200, 900);
        Size = new Vector2(600, 400);
    }

    protected override void DrawContents()
    {
        try
        {
            var config = plugin.ConfigService.Configuration;
            
            // Controls
            bool showOnlyMyLoot = config.ShowOnlyOwnLoot;
            if (ImGui.Checkbox("Show Only My Loot", ref showOnlyMyLoot))
            {
                config.ShowOnlyOwnLoot = showOnlyMyLoot;
                config.ShowOnlyMyLoot = showOnlyMyLoot; // Sync both properties
                plugin.ConfigService.Save();
            }
            
            ImGui.SameLine();
            if (ImGui.Button("Clear All"))
            {
                plugin.LootTracker.ClearLoot();
            }
            
            // Settings button (gear icon) on the same line
            ImGui.SameLine();
            if (ImGui.Button("⚙"))
            {
                plugin.ConfigWindow.IsOpen = true;
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Open Settings");
            }
            
            ImGui.Separator();
            
            // Loot display
            var lootItems = plugin.LootTracker.GetFilteredLoot().ToList();
            
            if (lootItems.Count == 0)
            {
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "No loot to display. Items will appear here when you loot them!");
            }
            else
            {
                ImGui.Text($"Total Items: {lootItems.Count}");
                ImGui.Separator();
                
                // Child window for scrolling
                if (ImGui.BeginChild("LootItemsChild"))
                {
                    // Table
                    if (ImGui.BeginTable("LootTable", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
                    {
                        ImGui.TableSetupColumn("Icon", ImGuiTableColumnFlags.WidthFixed, 28);
                        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
                        ImGui.TableSetupColumn("Quantity", ImGuiTableColumnFlags.WidthFixed, 80);
                        ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch);
                        ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 80);
                        ImGui.TableHeadersRow();
                        
                        // Display items
                        foreach (var item in lootItems)
                        {
                            ImGui.TableNextRow();
                            
                            // Highlight animation for new items (first 3 seconds)
                            var age = (DateTime.Now - item.Timestamp).TotalSeconds;
                            if (age < 3.0)
                            {
                                // Pulsing highlight effect - more visible
                                var fadeOut = 1.0 - age / 3.0;
                                var pulse = 0.6 + 0.4 * Math.Sin(age * 8.0); // Faster pulse, higher minimum
                                var alpha = (float)(0.6 * fadeOut * pulse); // Stronger alpha
                                var highlightColor = new Vector4(1.0f, 0.9f, 0.4f, alpha);
                                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(highlightColor));
                            }
                            
                            // Icon
                            ImGui.TableSetColumnIndex(0);
                            var iconDisplayed = false;
                            if (item.IconId > 0)
                            {
                                try
                                {
                                    var iconTexture = Plugin.TextureProvider.GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(item.IconId)).GetWrapOrDefault();
                                    if (iconTexture != null)
                                    {
                                        ImGui.Image(iconTexture.Handle, new Vector2(20, 20));
                                        iconDisplayed = true;
                                    }
                                }
                                catch { /* Ignore icon loading errors */ }
                            }
                            
                            // Show placeholder if no icon (same size as icon)
                            if (!iconDisplayed)
                            {
                                var cursorPos = ImGui.GetCursorPos();
                                ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.2f, 0.2f, 0.2f, 0.3f));
                                ImGui.BeginChild($"##placeholder_{item.ItemId}_{item.Timestamp.Ticks}", new Vector2(20, 20), true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoInputs);
                                ImGui.SetCursorPos(new Vector2(6, 2));
                                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.5f, 0.5f, 0.5f, 0.8f));
                                ImGui.Text("?");
                                ImGui.PopStyleColor();
                                ImGui.EndChild();
                                ImGui.PopStyleColor();
                            }
                            
                            // Item name
                            ImGui.TableSetColumnIndex(1);
                            var nameColor = GetRarityColor(item.Rarity);
                            ImGui.TextColored(nameColor, item.ItemName);
                            if (item.IsHQ)
                            {
                                ImGui.SameLine();
                                ImGui.TextColored(new Vector4(1.0f, 0.9f, 0.3f, 1.0f), "HQ");
                            }
                            
                            // Quantity
                            ImGui.TableSetColumnIndex(2);
                            ImGui.Text($"x{item.Quantity}");
                            
                            // Player
                            ImGui.TableSetColumnIndex(3);
                            var playerColor = item.IsOwnLoot 
                                ? new Vector4(0.2f, 1.0f, 0.2f, 1.0f)
                                : new Vector4(1.0f, 1.0f, 1.0f, 1.0f);
                            ImGui.TextColored(playerColor, item.PlayerName);
                            
                            // Time
                            ImGui.TableSetColumnIndex(4);
                            var elapsed = DateTime.Now - item.Timestamp;
                            var timeText = elapsed.TotalMinutes < 1 
                                ? $"{elapsed.Seconds}s ago" 
                                : elapsed.TotalHours < 1 
                                    ? $"{(int)elapsed.TotalMinutes}m ago"
                                    : $"{(int)elapsed.TotalHours}h ago";
                            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), timeText);
                        }
                        
                        ImGui.EndTable();
                    }
                    ImGui.EndChild();
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error drawing loot window");
            ImGui.TextColored(new Vector4(1, 0, 0, 1), "Error displaying loot!");
        }
    }
    
    private Vector4 GetRarityColor(uint rarity)
    {
        // FFXIV rarity colors
        return rarity switch
        {
            1 => new Vector4(1.0f, 1.0f, 1.0f, 1.0f),      // Common (white)
            2 => new Vector4(0.3f, 1.0f, 0.3f, 1.0f),      // Uncommon (green)
            3 => new Vector4(0.4f, 0.6f, 1.0f, 1.0f),      // Rare (blue)
            4 => new Vector4(0.8f, 0.4f, 1.0f, 1.0f),      // Relic (purple)
            7 => new Vector4(1.0f, 0.6f, 0.8f, 1.0f),      // Aetherial (pink)
            _ => new Vector4(1.0f, 1.0f, 1.0f, 1.0f)       // Default (white)
        };
    }

    public override void Dispose()
    {
        // Save window visibility state
        plugin.ConfigService.Configuration.IsVisible = IsOpen;
        plugin.ConfigService.Save();
        
        base.Dispose();
    }
}