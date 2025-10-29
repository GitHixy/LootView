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

            ImGui.Text("LootView Settings");
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
