using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using LootView.Models;
using LootView.Services;

namespace LootView.Windows;

/// <summary>
/// Real-time window showing active roll sessions, auto-closes 10 seconds after last winner
/// </summary>
public class RollWindow : Window
{
    private readonly Plugin plugin;
    private DateTime allItemsAwardedTime = DateTime.MinValue;
    private const double AutoCloseSeconds = 15.0;

    public RollWindow(Plugin plugin) : base("Loot Rolls###LootViewRolls")
    {
        this.plugin = plugin;
        
        // Start hidden
        IsOpen = false;
        
        // Set window flags
        WindowFlags = ImGuiWindowFlags.NoTitleBar | 
                     ImGuiWindowFlags.NoScrollbar | 
                     ImGuiWindowFlags.AlwaysAutoResize |
                     ImGuiWindowFlags.NoResize;
        
        // Compact size
        SizeConstraintMin = new Vector2(350, 50);
        SizeConstraintMax = new Vector2(700, 600);
        
        // Subscribe to roll updates
        plugin.LootTracker.RollsUpdated += OnRollsUpdated;
    }

    private void OnRollsUpdated()
    {
        // Show window when there are active rolls
        var activeRolls = plugin.LootTracker.ActiveRolls;
        
        if (activeRolls.Count > 0 && !IsOpen)
        {
            IsOpen = true;
            Plugin.Log.Info($"Roll window opened - {activeRolls.Count} active roll(s)");
        }
        
        // Check if ALL items have been awarded (all have winners)
        if (activeRolls.Count > 0 && activeRolls.All(r => !string.IsNullOrEmpty(r.WinnerName)))
        {
            if (allItemsAwardedTime == DateTime.MinValue)
            {
                allItemsAwardedTime = DateTime.Now;
                Plugin.Log.Info("All items awarded, starting 15 second countdown");
            }
        }
        else
        {
            // Reset timer if there are still items without winners
            allItemsAwardedTime = DateTime.MinValue;
        }
    }

    protected override void DrawContents()
    {
        try
        {
            var activeRolls = plugin.LootTracker.ActiveRolls;
            
            // Auto-close 15 seconds after ALL items awarded
            if (allItemsAwardedTime != DateTime.MinValue && 
                (DateTime.Now - allItemsAwardedTime).TotalSeconds > AutoCloseSeconds)
            {
                IsOpen = false;
                allItemsAwardedTime = DateTime.MinValue;
                
                // Clear completed roll data for next boss
                plugin.LootTracker.ClearCompletedRolls();
                
                Plugin.Log.Info("Roll window auto-closed after 15 seconds - cleared completed roll data");
                return;
            }
            
            if (activeRolls.Count == 0)
            {
                IsOpen = false;
                return;
            }

            // Header with title and countdown
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.8f, 0.9f, 1.0f, 1.0f));
            
            // Show countdown in title if all items are awarded
            if (allItemsAwardedTime != DateTime.MinValue)
            {
                var remainingTime = AutoCloseSeconds - (DateTime.Now - allItemsAwardedTime).TotalSeconds;
                ImGui.Text($"🎲 Loot Rolls - Closing in {remainingTime:F0}s");
            }
            else
            {
                ImGui.Text("🎲 Loot Rolls");
            }
            
            ImGui.PopStyleColor();
            
            // Manual close button on the same line
            ImGui.SameLine(ImGui.GetWindowWidth() - 35);
            if (ImGui.Button("X", new Vector2(25, 0)))
            {
                IsOpen = false;
                allItemsAwardedTime = DateTime.MinValue;
                
                // Clear ALL roll data when manually closed
                plugin.LootTracker.ClearAllRolls();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Close and clear all rolls");
            }

            ImGui.Separator();
            ImGui.Spacing();

            var localPlayerName = Plugin.ClientState.LocalPlayer?.Name.TextValue ?? "You";

            // Show each active roll session
            foreach (var rollInfo in activeRolls)
            {
                ImGui.PushID($"roll_{rollInfo.ItemId}_{rollInfo.ItemName}");
                ImGui.BeginGroup();

                // Item header with icon
                if (rollInfo.IconId > 0)
                {
                    try
                    {
                        var iconTexture = Plugin.TextureProvider.GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(rollInfo.IconId)).GetWrapOrDefault();
                        if (iconTexture != null)
                        {
                            ImGui.Image(iconTexture.Handle, new Vector2(32, 32));
                            ImGui.SameLine();
                        }
                    }
                    catch { /* Ignore icon errors */ }
                }

                ImGui.BeginGroup();
                
                // Item name with rarity color
                var rarityColor = GetRarityColor(rollInfo.Rarity);
                ImGui.TextColored(rarityColor, rollInfo.ItemName);

                // Show all rolls for this item, sorted
                var sortedRolls = rollInfo.GetSortedRolls().ToList();
                
                if (sortedRolls.Count > 0)
                {
                    foreach (var (playerName, rollType, rollValue, isWinner) in sortedRolls)
                    {
                        ImGui.Indent(10);
                        
                        // Winner gets gold star and highlight
                        if (isWinner)
                        {
                            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.84f, 0.0f, 1.0f)); // Gold
                            ImGui.Text("★");
                            ImGui.PopStyleColor();
                            ImGui.SameLine();
                        }
                        
                        // Player name (green for you, white for others)
                        var isLocalPlayer = playerName.Equals(localPlayerName, StringComparison.OrdinalIgnoreCase) ||
                                           playerName.StartsWith(localPlayerName, StringComparison.OrdinalIgnoreCase);
                        var playerColor = isLocalPlayer ? new Vector4(0.3f, 1.0f, 0.3f, 1.0f) : new Vector4(0.9f, 0.9f, 0.9f, 1.0f);
                        ImGui.TextColored(playerColor, playerName);
                        
                        ImGui.SameLine();
                        ImGui.Text("-");
                        
                        ImGui.SameLine();
                        // Roll type and value (green for Need, blue for Greed)
                        var rollColor = rollType == "Need" ? new Vector4(0.3f, 1.0f, 0.3f, 1.0f) : new Vector4(0.3f, 0.7f, 1.0f, 1.0f);
                        ImGui.TextColored(rollColor, $"{rollType} {rollValue}");

                        ImGui.Unindent(10);
                    }
                }
                else
                {
                    ImGui.Indent(10);
                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "Waiting for rolls...");
                    ImGui.Unindent(10);
                }

                ImGui.EndGroup();
                ImGui.EndGroup();
                
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
                
                ImGui.PopID();
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error drawing roll window");
        }
    }

    private Vector4 GetRarityColor(uint rarity)
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

    public override void Dispose()
    {
        plugin.LootTracker.RollsUpdated -= OnRollsUpdated;
        base.Dispose();
    }
}
