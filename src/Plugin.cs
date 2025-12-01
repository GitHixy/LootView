using System;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Gui.Dtr;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using LootView.Services;
using LootView.Windows;
using LootView.Models;

namespace LootView;

/// <summary>
/// Main plugin class for LootView
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    public string Name => "LootView";
    
    private const string CommandName = "/lootview";
    private const string CommandAlt = "/lv";

    // Dalamud Services
    [PluginService] public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] public static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] public static IClientState ClientState { get; private set; } = null!;
    [PluginService] public static IFramework Framework { get; private set; } = null!;
    [PluginService] public static IGameGui GameGui { get; private set; } = null!;
    [PluginService] public static IDataManager DataManager { get; private set; } = null!;
    [PluginService] public static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] public static IPluginLog Log { get; private set; } = null!;
    [PluginService] public static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] public static IGameInteropProvider GameInteropProvider { get; private set; } = null!;
    [PluginService] public static ICondition Condition { get; private set; } = null!;
    [PluginService] public static IDtrBar DtrBar { get; private set; } = null!;

    // Plugin Services
    public LootTrackingService LootTracker { get; private set; }
    public ConfigurationService ConfigService { get; private set; }
    public HistoryService HistoryService { get; private set; }
    public LootTableService LootTableService { get; private set; }
    
    // DTR Bar Entry
    private IDtrBarEntry? dtrEntry;
    
    // Windows
    public LootWindow LootWindow { get; private set; }
    public ConfigWindow ConfigWindow { get; private set; }
    public StatisticsWindow StatisticsWindow { get; private set; }
    public LootTableWindow LootTableWindow { get; private set; }
    public RollWindow RollWindow { get; private set; }
    
    // Configuration accessor for services
    public Configuration Configuration => ConfigService.Configuration;

    // Duty tracking state
    private bool wasInDuty = false;
    private Lumina.Excel.Sheets.ContentFinderCondition? currentDuty = null;

    public Plugin()
    {
        try
        {
            Log.Info("LootView plugin initializing...");

            // Initialize configuration service
            ConfigService = new ConfigurationService();

            // Initialize history service
            HistoryService = new HistoryService(ConfigService);

            // Initialize loot table service
            LootTableService = new LootTableService();

            // Initialize loot tracking service
            LootTracker = new LootTrackingService(ConfigService);
            LootTracker.SetHistoryService(HistoryService);
            LootTracker.LootObtained += OnLootObtained;

            // Initialize windows
            LootWindow = new LootWindow(this);
            ConfigWindow = new ConfigWindow(this, ConfigService);
            StatisticsWindow = new StatisticsWindow(this);
            LootTableWindow = new LootTableWindow(this);
            RollWindow = new RollWindow(this);

            // Register commands
            CommandManager.AddHandler(CommandAlt, new CommandInfo(OnCommand)
            {
                HelpMessage = "Toggle LootView window\n→ /lv config - Open settings"
            });

            // Register windows with plugin interface
            PluginInterface.UiBuilder.Draw += DrawUI;
            PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
            PluginInterface.UiBuilder.OpenMainUi += OpenMainUi;

            // Register login event
            ClientState.Login += OnLogin;

            // Register duty finder event
            ClientState.CfPop += OnDutyPop;

            // Register condition change for duty tracking
            Condition.ConditionChange += OnConditionChange;

            // Initialize DTR bar entry (server info bar)
            InitializeDtrBar();

            // Initialize services
            LootTracker.Initialize();

            Log.Info("LootView plugin initialized successfully!");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize LootView plugin");
            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            Log.Info("LootView plugin disposing...");

            // Unregister loot tracking event
            if (LootTracker != null)
            {
                LootTracker.LootObtained -= OnLootObtained;
            }

            // Dispose services in reverse order
            LootTableService?.Dispose();
            LootTracker?.Dispose();
            HistoryService?.Dispose();
            ConfigService?.Dispose();

            // Unregister UI
            PluginInterface.UiBuilder.Draw -= DrawUI;
            PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
            PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
            
            // Unregister events
            ClientState.Login -= OnLogin;
            
            // Remove DTR bar entry
            dtrEntry?.Remove();

            // Dispose windows
            LootWindow?.Dispose();
            ConfigWindow?.Dispose();
            StatisticsWindow?.Dispose();
            LootTableWindow?.Dispose();
            RollWindow?.Dispose();

            // Remove commands
            CommandManager.RemoveHandler(CommandAlt);

            Log.Info("LootView plugin disposed successfully!");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error disposing LootView plugin");
        }
    }

    private void OnCommand(string command, string args)
    {
        try
        {
            var arguments = args.Trim().ToLower();

            if (arguments == "config")
            {
                OpenConfigUi();
            }
            else
            {
                ToggleMainWindow();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error handling command: {Command} {Args}", command, args);
            ChatGui.PrintError($"[LootView] Error handling command: {ex.Message}");
        }
    }

    private void DrawUI()
    {
        try
        {
            LootWindow?.Draw();
            ConfigWindow?.Draw();
            StatisticsWindow?.Draw();
            LootTableWindow?.Draw();
            RollWindow?.Draw();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in DrawUI");
        }
    }

    private void OpenConfigUi()
    {
        ConfigWindow.IsOpen = true;
    }

    private void OpenMainUi()
    {
        LootWindow.IsOpen = true;
    }

    private void ToggleMainWindow()
    {
        LootWindow.IsOpen = !LootWindow.IsOpen;
        ConfigService.Configuration.IsVisible = LootWindow.IsOpen;
        ConfigService.Save();
    }

    private void OnLogin()
    {
        try
        {
            if (ConfigService.Configuration.OpenOnLogin)
            {
                LootWindow.IsOpen = true;
                Log.Info("LootView window opened automatically on login");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in OnLogin");
        }
    }

    private void OnDutyPop(Lumina.Excel.Sheets.ContentFinderCondition duty)
    {
        try
        {
            Log.Debug($"Duty popped: {duty.Name.ExtractText()}");
            currentDuty = duty;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in OnDutyPop");
        }
    }

    private void OnConditionChange(ConditionFlag flag, bool value)
    {
        try
        {
            // Track when entering/leaving duty
            if (flag == ConditionFlag.BoundByDuty)
            {
                if (value && !wasInDuty)
                {
                    // Entered duty
                    OnDutyEnter();
                }
                else if (!value && wasInDuty)
                {
                    // Left duty
                    OnDutyLeave();
                }
                wasInDuty = value;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in OnConditionChange");
        }
    }

    private void OnDutyEnter()
    {
        try
        {
            // Always try to get duty info from current territory (important for roulettes!)
            var territoryId = ClientState.TerritoryType;
            Log.Debug($"OnDutyEnter: Territory {territoryId}, currentDuty is {(currentDuty == null ? "null" : "set")}");
            
            var territorySheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>();
            if (territorySheet != null && territorySheet.TryGetRow(territoryId, out var territory))
            {
                var cfcSheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.ContentFinderCondition>();
                if (cfcSheet != null && territory.ContentFinderCondition.RowId > 0)
                {
                    if (cfcSheet.TryGetRow(territory.ContentFinderCondition.RowId, out var cfc))
                    {
                        currentDuty = cfc;
                        Log.Debug($"Got duty from territory: {cfc.Name.ExtractText()} (CFC {cfc.RowId})");
                    }
                    else
                    {
                        Log.Warning($"Could not find ContentFinderCondition {territory.ContentFinderCondition.RowId}");
                    }
                }
                else
                {
                    Log.Debug($"Territory {territoryId} has no ContentFinderCondition");
                }
            }

            if (currentDuty != null)
            {
                var contentType = GetContentType(currentDuty.Value.ContentType.RowId);
                var dutyName = currentDuty.Value.Name.ExtractText();
                Log.Info($"Entered duty: {dutyName} ({contentType}) via territory {territoryId}");
                
                HistoryService.TrackDutyStart(
                    currentDuty.Value.RowId,
                    dutyName,
                    contentType,
                    currentDuty.Value.ClassJobLevelRequired,
                    currentDuty.Value.ItemLevelRequired
                );
                
                // Auto-show loot window if configured
                if (ConfigService.Configuration.ShowOnDutyStart)
                {
                    LootWindow.IsOpen = true;
                }
            }
            else
            {
                Log.Warning($"Could not determine duty for territory {territoryId}");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in OnDutyEnter");
        }
    }

    private void OnDutyLeave()
    {
        try
        {
            Log.Debug("Left duty");
            // When leaving a duty, mark it as ended but not necessarily completed
            // The duty is only marked as "Completed = true" if we've seen loot drops
            // Otherwise it's marked as abandoned/failed
            HistoryService.EndCurrentDuty();
            currentDuty = null;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in OnDutyLeave");
        }
    }

    private string GetContentType(uint contentTypeId)
    {
        // Map content type IDs to readable names
        return contentTypeId switch
        {
            2 => "Dungeon",
            3 => "Guildhest",
            4 => "Trial",
            5 => "Raid",
            6 => "PvP",
            7 => "Quest Battle",
            8 => "V&C Dungeon",
            9 => "Treasure Hunt",
            16 => "Deep Dungeon",
            21 => "Alliance Raid",
            26 => "Eureka",
            27 => "Ultimate Raid",
            28 => "Bozja/Zadnor",
            29 => "Variant Dungeon",
            30 => "Criterion Dungeon",
            _ => $"Content Type {contentTypeId}"
        };
    }

    private void OnLootObtained(LootItem item)
    {
        try
        {
            // Update DTR bar when loot is obtained
            UpdateDtrBar();
            
            // Roll window is now automatically updated via RollsUpdated event
            // when a winner is marked in the roll tracking
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error handling loot obtained for DTR bar");
        }
    }

    private void InitializeDtrBar()
    {
        try
        {
            // Create DTR bar entry (server info bar button)
            dtrEntry = DtrBar.Get("LootView");
            
            if (dtrEntry != null)
            {
                // Register click handler to toggle overlay (must be set before showing)
                dtrEntry.OnClick = OnDtrBarClick;
                
                // Set initial text and tooltip
                UpdateDtrBar();
                
                // Show the entry
                dtrEntry.Shown = Configuration.ShowDtrBar;
                
                Log.Info("DTR bar entry initialized");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize DTR bar entry");
        }
    }

    private void UpdateDtrBar()
    {
        if (dtrEntry == null) return;

        try
        {
            // Get current session stats
            var history = HistoryService.GetHistory();
            var todayDate = DateTime.Today;
            
            // Simple text without item count
            dtrEntry.Text = "LootView";
            
            // Update tooltip
            dtrEntry.Tooltip = "Click to toggle overlay";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to update DTR bar");
        }
    }

    private void OnDtrBarClick(DtrInteractionEvent _)
    {
        try
        {
            // Toggle the main overlay window
            LootWindow.IsOpen = !LootWindow.IsOpen;
            Log.Debug($"DTR bar clicked, overlay is now {(LootWindow.IsOpen ? "open" : "closed")}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error handling DTR bar click");
        }
    }

    public void UpdateDtrBarVisibility(bool show)
    {
        if (dtrEntry != null)
        {
            dtrEntry.Shown = show;
            Log.Debug($"DTR bar visibility set to {show}");
        }
    }

}