using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using LootView.Services;

namespace LootView.Windows;

public class ConfigWindow : Window
{
    private readonly Plugin plugin;
    private readonly ConfigurationService configService;

    public ConfigWindow(Plugin plugin, ConfigurationService configService) 
        : base("LootView Configuration###LootViewConfig")
    {
        this.plugin = plugin;
        this.configService = configService;
        
        Size = new Vector2(500, 400);
        SizeConstraintMin = new Vector2(400, 300);
    }

    protected override void DrawContents()
    {
        try
        {
            var config = configService.Configuration;
            
            // Apply background alpha from configuration
            BgAlpha = config.BackgroundAlpha;

            ImGui.Text("LootView Settings");
            ImGui.Separator();
            
            // Statistics button at the top
            if (ImGui.Button("Open Statistics & History", new Vector2(200, 30)))
            {
                plugin.StatisticsWindow.IsOpen = true;
            }
            ImGui.SameLine();
            ImGui.TextDisabled("View your loot statistics, trends, and export history");
            
            ImGui.Spacing();
            ImGui.Separator();
            
            // Display Settings
            if (ImGui.CollapsingHeader("Display Settings", ImGuiTreeNodeFlags.DefaultOpen))
            {
                bool openOnLogin = config.OpenOnLogin;
                if (ImGui.Checkbox("Open Window on Login", ref openOnLogin))
                {
                    config.OpenOnLogin = openOnLogin;
                    configService.Save();
                }
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "(?)");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Automatically open the loot tracker window when you log into the game");
                }
                
                bool showOnlyMyLoot = config.ShowOnlyOwnLoot;
                if (ImGui.Checkbox("Show Only My Loot", ref showOnlyMyLoot))
                {
                    config.ShowOnlyOwnLoot = showOnlyMyLoot;
                    configService.Save();
                }
                
                bool showDtrBar = config.ShowDtrBar;
                if (ImGui.Checkbox("Show Server Info Bar Button", ref showDtrBar))
                {
                    config.ShowDtrBar = showDtrBar;
                    configService.Save();
                    
                    // Update DTR bar visibility immediately
                    plugin.UpdateDtrBarVisibility(showDtrBar);
                }
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "(?)");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Show a button in the server info bar (next to the server name) to toggle the overlay\nClick the button to open/close the loot window");
                }
                
                int maxItems = config.MaxDisplayedItems;
                if (ImGui.SliderInt("Max Items", ref maxItems, 10, 200))
                {
                    config.MaxDisplayedItems = maxItems;
                    configService.Save();
                }
            }
            
            // Tracking Settings
            if (ImGui.CollapsingHeader("Tracking"))
            {
                bool trackPartyLoot = config.TrackAllPartyLoot;
                if (ImGui.Checkbox("Track Party Loot", ref trackPartyLoot))
                {
                    config.TrackAllPartyLoot = trackPartyLoot;
                    configService.Save();
                }
            }
            
            // Visual Effects Settings
            if (ImGui.CollapsingHeader("Visual Effects", ImGuiTreeNodeFlags.DefaultOpen))
            {
                bool showTooltips = config.ShowTooltips;
                if (ImGui.Checkbox("Show Item Tooltips", ref showTooltips))
                {
                    config.ShowTooltips = showTooltips;
                    configService.Save();
                }
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "(?)");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Show detailed item information when hovering over items");
                }
                
                bool enableParticles = config.EnableParticleEffects;
                if (ImGui.Checkbox("Enable Particle Effects", ref enableParticles))
                {
                    config.EnableParticleEffects = enableParticles;
                    configService.Save();
                }
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "(?)");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Show rarity-based particle effects when obtaining new items");
                }
                
                if (config.EnableParticleEffects)
                {
                    ImGui.Indent();
                    float particleIntensity = config.ParticleIntensity;
                    if (ImGui.SliderFloat("Particle Intensity", ref particleIntensity, 0.0f, 2.0f, "%.1f"))
                    {
                        config.ParticleIntensity = particleIntensity;
                        configService.Save();
                    }
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "(?)");
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("Controls the number of particles spawned\n0.0 = Minimal, 1.0 = Normal, 2.0 = Maximum");
                    }
                    ImGui.Unindent();
                }
                
                float bgAlpha = config.BackgroundAlpha;
                if (ImGui.SliderFloat("Background Alpha", ref bgAlpha, 0.0f, 1.0f, "%.2f"))
                {
                    config.BackgroundAlpha = bgAlpha;
                    configService.Save();
                }
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "(?)");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Transparency level for window backgrounds\n0.0 = Fully Transparent, 1.0 = Fully Opaque");
                }
            }
            
            // History & Statistics Settings
            if (ImGui.CollapsingHeader("History & Statistics"))
            {
                bool enableHistory = config.EnableHistoryTracking;
                if (ImGui.Checkbox("Enable History Tracking", ref enableHistory))
                {
                    config.EnableHistoryTracking = enableHistory;
                    configService.Save();
                }
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "(?)");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Persistently save all loot items to disk for statistics and history tracking");
                }
                
                if (config.EnableHistoryTracking)
                {
                    ImGui.Indent();
                    
                    bool autoSave = config.EnableHistoryAutoSave;
                    if (ImGui.Checkbox("Auto-Save History", ref autoSave))
                    {
                        config.EnableHistoryAutoSave = autoSave;
                        configService.Save();
                    }
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "(?)");
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("Automatically save history to disk periodically (every 5 minutes)");
                    }
                    
                    bool saveOnClear = config.SaveToHistoryOnClear;
                    if (ImGui.Checkbox("Save to History on Clear", ref saveOnClear))
                    {
                        config.SaveToHistoryOnClear = saveOnClear;
                        configService.Save();
                    }
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "(?)");
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("Save current items to persistent history when clearing the display list");
                    }
                    
                    int retentionDays = config.HistoryRetentionDays;
                    if (ImGui.SliderInt("Retention Days", ref retentionDays, 7, 365))
                    {
                        config.HistoryRetentionDays = retentionDays;
                        configService.Save();
                    }
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "(?)");
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("Number of days to keep in history before automatic cleanup");
                    }
                    
                    ImGui.Unindent();
                }
                
                ImGui.Spacing();
                var history = plugin.HistoryService.GetHistory();
                ImGui.TextColored(new Vector4(0.5f, 0.9f, 0.5f, 1.0f), $"{history.TotalItemsObtained:N0} items tracked");
                ImGui.SameLine();
                ImGui.TextDisabled($"({history.DailyStatistics.Count} days)");
                
                if (ImGui.Button("Open Statistics Window"))
                {
                    plugin.StatisticsWindow.IsOpen = true;
                }
            }
            
            ImGui.Separator();
            if (ImGui.Button("Close"))
            {
                IsOpen = false;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error drawing config window");
            ImGui.TextColored(new Vector4(1, 0, 0, 1), "Error!");
        }
    }
}
