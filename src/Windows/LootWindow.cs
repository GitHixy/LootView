using System;
using System.Linq;
using System.Numerics;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Components;
using LootView.Models;

namespace LootView.Windows;

/// <summary>
/// Main loot display window - shows loot in a fancy customizable way
/// </summary>
public class LootWindow : Window
{
    private readonly Plugin plugin;
    private readonly List<ParticleEffect> particles = new();
    private readonly Random random = new();
    private DateTime lastUpdate = DateTime.Now;
    private readonly Dictionary<Guid, bool> particlesSpawned = new(); // Track which items have spawned particles

    public LootWindow(Plugin plugin) : base("LootView - Tracker by GitHixy###LootViewMain")
    {
        this.plugin = plugin;
        
        // Set initial visibility from config
        IsOpen = plugin.ConfigService.Configuration.IsVisible;
        
        // Set window constraints
        SizeConstraintMin = new Vector2(400, 300);
        SizeConstraintMax = new Vector2(1200, 900);
        Size = new Vector2(600, 400);
        
        // Set initial window flags based on lock state
        UpdateWindowFlags();
    }
    
    private void UpdateWindowFlags()
    {
        var config = plugin.ConfigService.Configuration;
        
        // Start with base flags
        WindowFlags = ImGuiWindowFlags.None;
        
        // Add lock flags if enabled
        if (config.LockWindowPosition)
        {
            WindowFlags |= ImGuiWindowFlags.NoMove;
        }
        
        if (config.LockWindowSize)
        {
            WindowFlags |= ImGuiWindowFlags.NoResize;
        }
    }
    
    private bool IsInDuty()
    {
        try
        {
            var territoryId = Plugin.ClientState.TerritoryType;
            if (territoryId == 0) return false;
            
            var territorySheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>();
            if (territorySheet == null) return false;
            
            if (territorySheet.TryGetRow(territoryId, out var territory))
            {
                return territory.ContentFinderCondition.RowId > 0;
            }
            
            return false;
        }
        catch
        {
            return false;
        }
    }

    protected override void DrawContents()
    {
        try
        {
            var config = plugin.ConfigService.Configuration;
            
            // Apply background alpha from configuration
            BgAlpha = config.BackgroundAlpha;
            
            // Update particle system
            if (config.EnableParticleEffects)
            {
                UpdateParticles();
            }
            
            // Controls - Left side
            bool showOnlyMyLoot = config.ShowOnlyOwnLoot;
            if (ImGui.Checkbox("Show Only My Loot", ref showOnlyMyLoot))
            {
                config.ShowOnlyOwnLoot = showOnlyMyLoot;
                config.ShowOnlyMyLoot = showOnlyMyLoot; // Sync both properties
                plugin.ConfigService.Save();
            }
            
            // Style selector on left
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            var styleNames = Enum.GetNames(typeof(LootWindowStyle));
            var currentStyle = (int)config.WindowStyle;
            if (ImGui.Combo("##Style", ref currentStyle, styleNames, styleNames.Length))
            {
                config.WindowStyle = (LootWindowStyle)currentStyle;
                plugin.ConfigService.Save();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Change visual style");
            }
            
            // Right side buttons - Clear, Stats, Lock, [Table], Config
            // Check if we're in a duty (has ContentFinderCondition)
            var isInDuty = IsInDuty();
            
            ImGui.SameLine();
            var availableWidth = ImGui.GetContentRegionAvail().X;
            var iconButtonWidth = 30f; // Icon buttons (Stats, Lock, [Table], Config)
            var clearButtonWidth = 70f; // "Clear All" button width
            var spacing = ImGui.GetStyle().ItemSpacing.X;
            
            // Calculate button count: base 4 (Stats, Lock, Config, Ko-fi) + 1 if in duty (Table)
            var buttonCount = isInDuty ? 5 : 4;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + availableWidth - clearButtonWidth - (iconButtonWidth * buttonCount) - (spacing * buttonCount));
            
            // Clear All button
            if (ImGui.Button("Clear All", new Vector2(clearButtonWidth, 0)))
            {
                plugin.LootTracker.ClearLoot();
            }
            
            // Stats button
            ImGui.SameLine();
            if (ImGuiComponents.IconButton("StatsButton", FontAwesomeIcon.ChartLine, new Vector2(iconButtonWidth, 0)))
            {
                plugin.StatisticsWindow.IsOpen = true;
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Open Statistics & History");
            }
            
            // Lock/Unlock button
            ImGui.SameLine();
            var isLocked = config.LockWindowPosition;
            var lockIcon = isLocked ? FontAwesomeIcon.Lock : FontAwesomeIcon.LockOpen;
            if (ImGuiComponents.IconButton("LockButton", lockIcon, new Vector2(iconButtonWidth, 0)))
            {
                config.LockWindowPosition = !config.LockWindowPosition;
                config.LockWindowSize = config.LockWindowPosition;
                plugin.ConfigService.Save();
                UpdateWindowFlags();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(isLocked ? "Unlock window" : "Lock window position and size");
            }
            
            // Loot Table button (only show in duties/instances)
            if (isInDuty)
            {
                ImGui.SameLine();
                if (ImGuiComponents.IconButton("LootTableButton", FontAwesomeIcon.Table, new Vector2(iconButtonWidth, 0)))
                {
                    plugin.LootTableWindow.IsOpen = true;
                    plugin.LootTableWindow.LoadCurrentZone();
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Show Duty Loot Table");
                }
            }
            
            // Config button
            ImGui.SameLine();
            if (ImGuiComponents.IconButton("ConfigButton", FontAwesomeIcon.Cog, new Vector2(iconButtonWidth, 0)))
            {
                plugin.ConfigWindow.IsOpen = true;
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Open Settings");
            }
            
            // Ko-fi donation button
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.13f, 0.59f, 0.95f, 1.0f)); // Ko-fi blue
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.16f, 0.65f, 1.0f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.10f, 0.53f, 0.85f, 1.0f));
            if (ImGuiComponents.IconButton("KofiButton", FontAwesomeIcon.Coffee, new Vector2(iconButtonWidth, 0)))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://ko-fi.com/hixyllian",
                    UseShellExecute = true
                });
            }
            ImGui.PopStyleColor(3);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("☕ Support development - Buy me a coffee!\nClick to open Ko-fi page");
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
                
                // Render based on selected style
                switch (config.WindowStyle)
                {
                    case LootWindowStyle.Classic:
                        DrawClassicStyle(lootItems);
                        break;
                    case LootWindowStyle.Compact:
                        DrawCompactStyle(lootItems);
                        break;
                    case LootWindowStyle.Neon:
                        DrawNeonStyle(lootItems);
                        break;
                    default:
                        DrawClassicStyle(lootItems);
                        break;
                }
            }
            
            // Draw particles on top of everything
            if (config.EnableParticleEffects)
            {
                DrawParticles();
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error drawing loot window");
            ImGui.TextColored(new Vector4(1, 0, 0, 1), "Error displaying loot!");
        }
    }

    private void DrawClassicStyle(System.Collections.Generic.List<LootItem> lootItems)
    {
        // Classic table layout - the original style
        if (ImGui.BeginChild("LootItemsChild"))
        {
            if (ImGui.BeginTable("LootTable", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
            {
                ImGui.TableSetupColumn("Icon", ImGuiTableColumnFlags.WidthFixed, 28);
                ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Quantity", ImGuiTableColumnFlags.WidthFixed, 80);
                ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 80);
                ImGui.TableHeadersRow();
                
                foreach (var item in lootItems)
                {
                    ImGui.TableNextRow();
                    var age = (DateTime.Now - item.Timestamp).TotalSeconds;
                    if (age < 3.0)
                    {
                        var fadeOut = 1.0 - age / 3.0;
                        var pulse = 0.6 + 0.4 * Math.Sin(age * 8.0);
                        var alpha = (float)(0.6 * fadeOut * pulse);
                        var highlightColor = new Vector4(1.0f, 0.9f, 0.4f, alpha);
                        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(highlightColor));
                    }
                    
                    ImGui.TableSetColumnIndex(0);
                    
                    // Get icon position BEFORE rendering
                    var iconScreenPos = ImGui.GetCursorScreenPos();
                    
                    RenderIcon(item, new Vector2(20, 20));
                    
                    // Spawn particles for new items at the icon position
                    if (age < 0.1 && plugin.ConfigService.Configuration.EnableParticleEffects)
                    {
                        // Use screen coordinates directly, add offset to center of icon
                        SpawnParticlesForItem(item, iconScreenPos + new Vector2(10, 10));
                    }
                    
                    // Show tooltip on hover
                    if (ImGui.IsItemHovered())
                    {
                        ShowItemTooltip(item);
                    }
                    
                    // Check for right-click on icon
                    if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                    {
                        ImGui.OpenPopup($"##ItemContextMenu_{item.ItemId}_{item.Timestamp.Ticks}");
                    }
                    
                    ImGui.TableSetColumnIndex(1);
                    var nameColor = GetRarityColor(item.Rarity);
                    ImGui.TextColored(nameColor, ToTitleCase(item.ItemName));
                    if (item.IsHQ)
                    {
                        ImGui.SameLine();
                        ImGui.TextColored(new Vector4(1.0f, 0.9f, 0.3f, 1.0f), "HQ");
                    }
                    
                    // Show tooltip on name hover too
                    if (ImGui.IsItemHovered())
                    {
                        ShowItemTooltip(item);
                    }
                    
                    // Check for right-click on name
                    if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                    {
                        ImGui.OpenPopup($"##ItemContextMenu_{item.ItemId}_{item.Timestamp.Ticks}");
                    }
                    
                    ImGui.TableSetColumnIndex(2);
                    ImGui.Text($"x{item.Quantity}");
                    
                    // Check for right-click on quantity
                    if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                    {
                        ImGui.OpenPopup($"##ItemContextMenu_{item.ItemId}_{item.Timestamp.Ticks}");
                    }
                    
                    ImGui.TableSetColumnIndex(3);
                    var playerColor = item.IsOwnLoot ? new Vector4(0.2f, 1.0f, 0.2f, 1.0f) : new Vector4(1.0f, 1.0f, 1.0f, 1.0f);
                    ImGui.TextColored(playerColor, item.PlayerName);
                    
                    // Check for right-click on player name
                    if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                    {
                        ImGui.OpenPopup($"##ItemContextMenu_{item.ItemId}_{item.Timestamp.Ticks}");
                    }
                    
                    ImGui.TableSetColumnIndex(4);
                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), FormatTimeAgo(item.Timestamp));
                    
                    // Check for right-click on time
                    if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                    {
                        ImGui.OpenPopup($"##ItemContextMenu_{item.ItemId}_{item.Timestamp.Ticks}");
                    }
                    
                    // Right-click context menu for blacklist management
                    if (ImGui.BeginPopup($"##ItemContextMenu_{item.ItemId}_{item.Timestamp.Ticks}"))
                    {
                        var config = plugin.ConfigService.Configuration;
                        bool isBlacklisted = config.BlacklistedItemIds?.Contains(item.ItemId) ?? false;
                        
                        if (isBlacklisted)
                        {
                            if (ImGui.MenuItem($"Remove '{item.ItemName}' from Blacklist"))
                            {
                                if (config.BlacklistedItemIds != null)
                                {
                                    config.BlacklistedItemIds.Remove(item.ItemId);
                                    plugin.ConfigService.Save();
                                }
                            }
                        }
                        else
                        {
                            if (ImGui.MenuItem($"Add '{item.ItemName}' to Blacklist"))
                            {
                                if (config.BlacklistedItemIds == null)
                                {
                                    config.BlacklistedItemIds = new System.Collections.Generic.List<uint>();
                                }
                                if (!config.BlacklistedItemIds.Contains(item.ItemId))
                                {
                                    config.BlacklistedItemIds.Add(item.ItemId);
                                    plugin.ConfigService.Save();
                                }
                            }
                        }
                        
                        ImGui.EndPopup();
                    }
                }
                
                ImGui.EndTable();
            }
            ImGui.EndChild();
        }
    }

    private void DrawCompactStyle(System.Collections.Generic.List<LootItem> lootItems)
    {
        // Compact single-line entries + PROGRESS BAR for item age
        var config = plugin.ConfigService.Configuration;
        if (ImGui.BeginChild("LootItemsChild"))
        {
            foreach (var item in lootItems)
            {
                var age = (DateTime.Now - item.Timestamp).TotalSeconds;
                if (age < 3.0)
                {
                    var fadeOut = 1.0 - age / 3.0;
                    var alpha = (float)(0.3 * fadeOut) * config.BackgroundAlpha;
                    ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(1.0f, 0.9f, 0.4f, alpha));
                    ImGui.BeginChild($"##highlight_{item.ItemId}_{item.Timestamp.Ticks}", new Vector2(ImGui.GetContentRegionAvail().X, 26), false);
                    ImGui.EndChild();
                    ImGui.PopStyleColor();
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() - 26);
                }
                
                // Get icon position BEFORE rendering
                var iconScreenPos = ImGui.GetCursorScreenPos();
                
                RenderIcon(item, new Vector2(18, 18));
                
                // Spawn particles for new items at the icon position
                if (age < 0.1 && plugin.ConfigService.Configuration.EnableParticleEffects)
                {
                    // Use screen coordinates directly, add offset to center of icon
                    SpawnParticlesForItem(item, iconScreenPos + new Vector2(9, 9));
                }
                
                // Show tooltip on icon hover
                if (ImGui.IsItemHovered())
                {
                    ShowItemTooltip(item);
                }
                
                ImGui.SameLine();
                
                var nameColor = GetRarityColor(item.Rarity);
                ImGui.TextColored(nameColor, ToTitleCase(item.ItemName));
                
                // Show tooltip on name hover
                if (ImGui.IsItemHovered())
                {
                    ShowItemTooltip(item);
                }
                
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1.0f), $"x{item.Quantity}");
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), $"• {FormatTimeAgo(item.Timestamp)}");
                
                // Tiny progress bar showing item freshness (fades over 10 minutes)
                var freshness = Math.Max(0, 1.0 - age / 600.0); // 10 minutes
                if (freshness > 0)
                {
                    ImGui.SameLine();
                    ImGui.SetCursorPosX(ImGui.GetContentRegionAvail().X + ImGui.GetCursorPosX() - 50);
                    ImGui.PushStyleColor(ImGuiCol.PlotHistogram, new Vector4(0.3f, 0.7f, 1.0f, 0.6f));
                    ImGui.ProgressBar((float)freshness, new Vector2(45, 3), "");
                    ImGui.PopStyleColor();
                }
                
                // Right-click context menu for blacklist management
                // Add invisible button covering the item row to detect right-clicks
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() - ImGui.GetTextLineHeightWithSpacing());
                ImGui.InvisibleButton($"##CompactItemRow_{item.ItemId}_{item.Timestamp.Ticks}", new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetTextLineHeightWithSpacing()));
                
                if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                {
                    ImGui.OpenPopup($"##CompactContextMenu_{item.ItemId}_{item.Timestamp.Ticks}");
                }
                
                if (ImGui.BeginPopup($"##CompactContextMenu_{item.ItemId}_{item.Timestamp.Ticks}"))
                {
                    var configInner = plugin.ConfigService.Configuration;
                    bool isBlacklisted = configInner.BlacklistedItemIds?.Contains(item.ItemId) ?? false;
                    
                    if (isBlacklisted)
                    {
                        if (ImGui.MenuItem($"Remove '{item.ItemName}' from Blacklist"))
                        {
                            if (configInner.BlacklistedItemIds != null)
                            {
                                configInner.BlacklistedItemIds.Remove(item.ItemId);
                                plugin.ConfigService.Save();
                            }
                        }
                    }
                    else
                    {
                        if (ImGui.MenuItem($"Add '{item.ItemName}' to Blacklist"))
                        {
                            if (configInner.BlacklistedItemIds == null)
                            {
                                configInner.BlacklistedItemIds = new System.Collections.Generic.List<uint>();
                            }
                            if (!configInner.BlacklistedItemIds.Contains(item.ItemId))
                            {
                                configInner.BlacklistedItemIds.Add(item.ItemId);
                                plugin.ConfigService.Save();
                            }
                        }
                    }
                    
                    ImGui.EndPopup();
                }
            }
            ImGui.EndChild();
        }
    }

    private void DrawNeonStyle(System.Collections.Generic.List<LootItem> lootItems)
    {
        // Cyberpunk/tech aesthetic with neon colors + SCANLINE EFFECT
        var config = plugin.ConfigService.Configuration;
        if (ImGui.BeginChild("LootItemsChild"))
        {
            var time = DateTime.Now.TimeOfDay.TotalSeconds;
            
            foreach (var item in lootItems)
            {
                var age = (DateTime.Now - item.Timestamp).TotalSeconds;
                var glowPulse = age < 5.0 ? (float)(Math.Sin(age * 5.0) * 0.5 + 0.5) * (1.0 - age / 5.0) : 0f;
                
                var bgAlpha = config.BackgroundAlpha;
                var bgColor = new Vector4(0.05f, 0.05f, 0.15f, bgAlpha);
                var borderColor = new Vector4(0.0f, (float)(0.8f + glowPulse * 0.2f), 1.0f, (float)(0.8f + glowPulse * 0.2f));
                
                ImGui.PushStyleColor(ImGuiCol.ChildBg, bgColor);
                ImGui.PushStyleColor(ImGuiCol.Border, borderColor);
                ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, (float)(1.5f + glowPulse * 1.0f));
                
                if (ImGui.BeginChild($"##neon_{item.ItemId}_{item.Timestamp.Ticks}", new Vector2(ImGui.GetContentRegionAvail().X, 50), true))
                {
                    // CRT Scanline effect
                    var windowPos = ImGui.GetWindowPos();
                    var scanlineOffset = (float)((time * 100) % ImGui.GetWindowHeight());
                    ImGui.GetWindowDrawList().AddLine(
                        new Vector2(windowPos.X, windowPos.Y + scanlineOffset),
                        new Vector2(windowPos.X + ImGui.GetWindowWidth(), windowPos.Y + scanlineOffset),
                        ImGui.GetColorU32(new Vector4(0.0f, 1.0f, 1.0f, 0.1f)),
                        1.0f
                    );
                    
                    ImGui.SetCursorPos(new Vector2(10, 8));
                    
                    // Neon glow effect around icon
                    if (glowPulse > 0)
                    {
                        var glowColor = new Vector4(0.0f, 1.0f, 1.0f, (float)(glowPulse * 0.5f));
                        var iconPos = ImGui.GetCursorScreenPos();
                        ImGui.GetWindowDrawList().AddRect(
                            new Vector2(iconPos.X - 2, iconPos.Y - 2),
                            new Vector2(iconPos.X + 36, iconPos.Y + 36),
                            ImGui.GetColorU32(glowColor),
                            4.0f,
                            ImDrawFlags.None,
                            2.0f
                        );
                    }
                    
                    // Get icon position BEFORE rendering
                    var iconScreenPos = ImGui.GetCursorScreenPos();
                    
                    RenderIcon(item, new Vector2(32, 32));
                    
                    // Spawn particles for new items at the icon position
                    if (age < 0.1 && plugin.ConfigService.Configuration.EnableParticleEffects)
                    {
                        // Use screen coordinates directly, add offset to center of icon
                        SpawnParticlesForItem(item, iconScreenPos + new Vector2(16, 16));
                    }
                    
                    // Show tooltip on icon hover
                    if (ImGui.IsItemHovered())
                    {
                        ShowItemTooltip(item);
                    }
                    
                    ImGui.SameLine();
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 10);
                    
                    ImGui.BeginGroup();
                    var nameColor = GetRarityColor(item.Rarity);
                    // Add cyan tint to name
                    nameColor = new Vector4(
                        nameColor.X * 0.7f + 0.3f * 0.0f,
                        nameColor.Y * 0.7f + 0.3f * 1.0f,
                        nameColor.Z * 0.7f + 0.3f * 1.0f,
                        1.0f
                    );
                    ImGui.TextColored(nameColor, ToTitleCase(item.ItemName).ToUpper());
                    
                    // Show tooltip on name hover
                    if (ImGui.IsItemHovered())
                    {
                        ShowItemTooltip(item);
                    }
                    
                    ImGui.TextColored(new Vector4(0.0f, 0.9f, 0.9f, 1.0f), $"[{item.Quantity}x] {item.PlayerName} // {FormatTimeAgo(item.Timestamp)}");
                    ImGui.EndGroup();
                }
                ImGui.EndChild();
                
                // Right-click context menu for blacklist management
                // Check if the child window was right-clicked
                if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                {
                    ImGui.OpenPopup($"##NeonItemContextMenu_{item.ItemId}_{item.Timestamp.Ticks}");
                }
                
                if (ImGui.BeginPopup($"##NeonItemContextMenu_{item.ItemId}_{item.Timestamp.Ticks}"))
                {
                    var configInner = plugin.ConfigService.Configuration;
                    bool isBlacklisted = configInner.BlacklistedItemIds?.Contains(item.ItemId) ?? false;
                    
                    if (isBlacklisted)
                    {
                        if (ImGui.MenuItem($"Remove '{item.ItemName}' from Blacklist"))
                        {
                            if (configInner.BlacklistedItemIds != null)
                            {
                                configInner.BlacklistedItemIds.Remove(item.ItemId);
                                plugin.ConfigService.Save();
                            }
                        }
                    }
                    else
                    {
                        if (ImGui.MenuItem($"Add '{item.ItemName}' to Blacklist"))
                        {
                            if (configInner.BlacklistedItemIds == null)
                            {
                                configInner.BlacklistedItemIds = new System.Collections.Generic.List<uint>();
                            }
                            if (!configInner.BlacklistedItemIds.Contains(item.ItemId))
                            {
                                configInner.BlacklistedItemIds.Add(item.ItemId);
                                plugin.ConfigService.Save();
                            }
                        }
                    }
                    
                    ImGui.EndPopup();
                }
                
                ImGui.PopStyleVar();
                ImGui.PopStyleColor(2);
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 4);
            }
            ImGui.EndChild();
        }
    }

    private void RenderIcon(LootItem item, Vector2 size)
    {
        if (item.IconId > 0)
        {
            try
            {
                var iconTexture = Plugin.TextureProvider.GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(item.IconId)).GetWrapOrDefault();
                if (iconTexture != null)
                {
                    ImGui.Image(iconTexture.Handle, size);
                    return;
                }
            }
            catch { /* Ignore icon loading errors */ }
        }
        
        // Placeholder
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.2f, 0.2f, 0.2f, 0.3f));
        ImGui.BeginChild($"##placeholder_{item.ItemId}_{item.Timestamp.Ticks}", size, true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoInputs);
        ImGui.SetCursorPos(new Vector2(size.X / 2 - 4, size.Y / 2 - 8));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.5f, 0.5f, 0.5f, 0.8f));
        ImGui.Text("?");
        ImGui.PopStyleColor();
        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    private string FormatTimeAgo(DateTime timestamp)
    {
        var elapsed = DateTime.Now - timestamp;
        return elapsed.TotalMinutes < 1 
            ? $"{elapsed.Seconds}s ago" 
            : elapsed.TotalHours < 1 
                ? $"{(int)elapsed.TotalMinutes}m ago"
                : $"{(int)elapsed.TotalHours}h ago";
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

    private string ToTitleCase(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        // Use TextInfo for proper title casing
        var textInfo = System.Globalization.CultureInfo.CurrentCulture.TextInfo;
        var titleCased = textInfo.ToTitleCase(text.ToLower());
        
        // Fix common words that should stay lowercase
        var wordsToLower = new[] { " Of ", " The ", " A ", " An ", " And ", " Or ", " In ", " On ", " At ", " To ", " For ", " With " };
        foreach (var word in wordsToLower)
        {
            titleCased = titleCased.Replace(word, word.ToLower());
        }
        
        return titleCased;
    }

    // ============================================================================
    // TOOLTIP SYSTEM
    // ============================================================================
    
    private void ShowItemTooltip(LootItem item)
    {
        if (!plugin.ConfigService.Configuration.ShowTooltips)
            return;

        ImGui.BeginTooltip();
        
        // Header with colored item name based on rarity
        var rarityColor = GetRarityColor(item.Rarity);
        ImGui.PushStyleColor(ImGuiCol.Text, rarityColor);
        ImGui.Text(ToTitleCase(item.ItemName));
        ImGui.PopStyleColor();
        
        if (item.IsHQ)
        {
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.9f, 0.3f, 1.0f));
            ImGui.Text("HQ");
            ImGui.PopStyleColor();
        }
        
        ImGui.Separator();
        
        // Item metadata
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.7f, 0.7f, 0.7f, 1.0f));
        ImGui.Text($"Quantity: x{item.Quantity}");
        ImGui.Text($"Item ID: {item.ItemId}");
        ImGui.Text($"Rarity: {GetRarityName(item.Rarity)}");
        ImGui.PopStyleColor();
        
        // Roll info (if applicable)
        if (!string.IsNullOrEmpty(item.RollType))
        {
            ImGui.Separator();
            var rollColor = item.RollType == "Need" ? new Vector4(0.3f, 1.0f, 0.3f, 1.0f) : new Vector4(0.3f, 0.7f, 1.0f, 1.0f);
            ImGui.PushStyleColor(ImGuiCol.Text, rollColor);
            ImGui.Text($"🎲 {item.RollType}: {item.RollValue}");
            ImGui.PopStyleColor();
        }
        
        // Location info (only if we have zone name)
        if (!string.IsNullOrEmpty(item.ZoneName))
        {
            ImGui.Separator();
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.8f, 0.9f, 1.0f, 1.0f));
            ImGui.Text($"Zone: {item.ZoneName}");
            ImGui.PopStyleColor();
        }
        
        // Player and time info
        ImGui.Separator();
        var playerColor = item.IsOwnLoot ? new Vector4(0.4f, 1.0f, 0.4f, 1.0f) : new Vector4(1.0f, 1.0f, 1.0f, 1.0f);
        ImGui.PushStyleColor(ImGuiCol.Text, playerColor);
        ImGui.Text(item.IsOwnLoot ? "You obtained this" : $"Looted by: {item.PlayerName}");
        ImGui.PopStyleColor();
        
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.7f, 0.7f, 0.7f, 1.0f));
        ImGui.Text($"{FormatTimeAgo(item.Timestamp)} ({item.Timestamp:HH:mm:ss})");
        ImGui.PopStyleColor();
        
        ImGui.EndTooltip();
    }
    
    private string GetRarityName(uint rarity)
    {
        return rarity switch
        {
            1 => "Common",
            2 => "Uncommon",
            3 => "Rare",
            4 => "Relic",
            7 => "Aetherial",
            _ => "Unknown"
        };
    }

    // ============================================================================
    // PARTICLE SYSTEM
    // ============================================================================
    
    private void UpdateParticles()
    {
        var now = DateTime.Now;
        var deltaTime = (float)(now - lastUpdate).TotalSeconds;
        lastUpdate = now;
        
        // Update existing particles
        for (int i = particles.Count - 1; i >= 0; i--)
        {
            particles[i].Update(deltaTime);
            if (!particles[i].IsAlive)
            {
                particles.RemoveAt(i);
            }
        }
    }
    
    private void SpawnParticlesForItem(LootItem item, Vector2 position)
    {
        // Check if we already spawned particles for this item
        if (particlesSpawned.ContainsKey(item.Id))
            return;
            
        particlesSpawned[item.Id] = true;
        
        var config = plugin.ConfigService.Configuration;
        if (!config.EnableParticleEffects)
            return;
        
        // Get rarity-specific particle configuration
        var particleConfig = GetParticleConfigForRarity(item.Rarity);
        var particleCount = (int)(particleConfig.Count * config.ParticleIntensity);
        
        for (int i = 0; i < particleCount; i++)
        {
            var angle = random.NextDouble() * Math.PI * 2;
            var speed = particleConfig.Speed * (0.5f + (float)random.NextDouble() * 0.5f);
            var velocity = new Vector2(
                (float)Math.Cos(angle) * speed,
                (float)Math.Sin(angle) * speed - particleConfig.InitialYVelocity
            );
            
            var particle = new ParticleEffect
            {
                Position = position + new Vector2((float)random.NextDouble() * 20 - 10, (float)random.NextDouble() * 20 - 10),
                Velocity = velocity,
                Color = particleConfig.Color,
                Size = particleConfig.Size * (0.7f + (float)random.NextDouble() * 0.6f),
                Life = particleConfig.Life * (0.8f + (float)random.NextDouble() * 0.4f),
                MaxLife = particleConfig.Life,
                Type = particleConfig.Type,
                Rotation = (float)(random.NextDouble() * Math.PI * 2),
                RotationSpeed = ((float)random.NextDouble() - 0.5f) * 4f
            };
            
            particles.Add(particle);
        }
        
        // Add special effect rings for rare items
        if (item.Rarity >= 3)
        {
            for (int i = 0; i < 3; i++)
            {
                particles.Add(new ParticleEffect
                {
                    Position = position,
                    Velocity = Vector2.Zero,
                    Color = new Vector4(particleConfig.Color.X, particleConfig.Color.Y, particleConfig.Color.Z, 0.6f),
                    Size = 10f + i * 25f,
                    Life = 1.5f + i * 0.4f,
                    MaxLife = 1.5f + i * 0.4f,
                    Type = ParticleType.Ring,
                    Rotation = 0,
                    RotationSpeed = 0
                });
            }
        }
    }
    
    private void DrawParticles()
    {
        if (particles.Count == 0)
            return;
            
        var drawList = ImGui.GetWindowDrawList();
        
        foreach (var particle in particles)
        {
            // Particle.Position is already in screen coordinates
            var screenPos = particle.Position;
            
            switch (particle.Type)
            {
                case ParticleType.Spark:
                    // Bright small point
                    drawList.AddCircleFilled(screenPos, particle.Size, ImGui.GetColorU32(particle.Color), 8);
                    break;
                    
                case ParticleType.Glow:
                    // Soft glowing orb with gradient
                    drawList.AddCircleFilled(screenPos, particle.Size, ImGui.GetColorU32(particle.Color), 16);
                    var glowColor = new Vector4(particle.Color.X, particle.Color.Y, particle.Color.Z, particle.Color.W * 0.3f);
                    drawList.AddCircleFilled(screenPos, particle.Size * 1.5f, ImGui.GetColorU32(glowColor), 16);
                    break;
                    
                case ParticleType.Star:
                    // Star shape using lines
                    for (int i = 0; i < 4; i++)
                    {
                        var angle = particle.Rotation + i * (float)Math.PI / 2;
                        var offset = new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * particle.Size;
                        drawList.AddLine(screenPos - offset, screenPos + offset, ImGui.GetColorU32(particle.Color), 3f);
                    }
                    break;
                    
                case ParticleType.Ring:
                    // Expanding ring
                    var ringSize = particle.Size * (1f - particle.Life / particle.MaxLife) * 3f;
                    drawList.AddCircle(screenPos, ringSize, ImGui.GetColorU32(particle.Color), 32, 3f);
                    break;
                    
                case ParticleType.Trail:
                    // Motion trail
                    var trailEnd = screenPos - particle.Velocity * 0.1f;
                    drawList.AddLine(screenPos, trailEnd, ImGui.GetColorU32(particle.Color), particle.Size);
                    break;
                    
                case ParticleType.Shimmer:
                    // Twinkling star
                    var shimmerSize = particle.Size * (0.5f + 0.5f * (float)Math.Sin(particle.Life * 10));
                    drawList.AddCircleFilled(screenPos, shimmerSize, ImGui.GetColorU32(particle.Color), 8);
                    break;
            }
        }
    }
    
    private (int Count, float Speed, float InitialYVelocity, Vector4 Color, float Size, float Life, ParticleType Type) GetParticleConfigForRarity(uint rarity)
    {
        return rarity switch
        {
            // Common (White) - Simple sparks
            1 => (
                Count: 15,
                Speed: 120f,
                InitialYVelocity: 60f,
                Color: new Vector4(1.0f, 1.0f, 1.0f, 1.0f),
                Size: 3f,
                Life: 1.2f,
                Type: ParticleType.Spark
            ),
            
            // Uncommon (Green) - Glowing orbs
            2 => (
                Count: 20,
                Speed: 130f,
                InitialYVelocity: 70f,
                Color: new Vector4(0.3f, 1.0f, 0.3f, 1.0f),
                Size: 4f,
                Life: 1.5f,
                Type: ParticleType.Glow
            ),
            
            // Rare (Blue) - Stars with shimmer
            3 => (
                Count: 30,
                Speed: 150f,
                InitialYVelocity: 80f,
                Color: new Vector4(0.4f, 0.6f, 1.0f, 1.0f),
                Size: 6f,
                Life: 2.0f,
                Type: ParticleType.Star
            ),
            
            // Relic (Purple) - Multiple effects
            4 => (
                Count: 45,
                Speed: 180f,
                InitialYVelocity: 100f,
                Color: new Vector4(0.8f, 0.4f, 1.0f, 1.0f),
                Size: 7f,
                Life: 2.5f,
                Type: ParticleType.Shimmer
            ),
            
            // Aetherial (Pink) - Trails and sparkles
            7 => (
                Count: 35,
                Speed: 160f,
                InitialYVelocity: 90f,
                Color: new Vector4(1.0f, 0.6f, 0.8f, 1.0f),
                Size: 6f,
                Life: 2.2f,
                Type: ParticleType.Trail
            ),
            
            // Default - Basic sparks
            _ => (
                Count: 15,
                Speed: 120f,
                InitialYVelocity: 60f,
                Color: new Vector4(0.8f, 0.8f, 0.8f, 1.0f),
                Size: 3f,
                Life: 1.2f,
                Type: ParticleType.Spark
            )
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