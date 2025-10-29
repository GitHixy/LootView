using System;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
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

    // Plugin Services
    public LootTrackingService LootTracker { get; private set; }
    public ConfigurationService ConfigService { get; private set; }
    
    // Windows
    public LootWindow LootWindow { get; private set; }
    public ConfigWindow ConfigWindow { get; private set; }

    public Plugin()
    {
        try
        {
            Log.Info("LootView plugin initializing...");

            // Initialize configuration service
            ConfigService = new ConfigurationService();

            // Initialize loot tracking service
            LootTracker = new LootTrackingService(ConfigService);

            // Initialize windows
            LootWindow = new LootWindow(this);
            ConfigWindow = new ConfigWindow(this, ConfigService);

            // Register commands
            CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Opens the LootView window. Use '/lootview config' to open configuration."
            });

            CommandManager.AddHandler(CommandAlt, new CommandInfo(OnCommand)
            {
                HelpMessage = "Opens the LootView window (short version). Use '/lv config' to open configuration."
            });

            // Register windows with plugin interface
            PluginInterface.UiBuilder.Draw += DrawUI;
            PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
            PluginInterface.UiBuilder.OpenMainUi += OpenMainUi;

            // Register login event
            ClientState.Login += OnLogin;

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

            // Dispose services in reverse order
            LootTracker?.Dispose();
            ConfigService?.Dispose();

            // Unregister UI
            PluginInterface.UiBuilder.Draw -= DrawUI;
            PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
            PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
            
            // Unregister events
            ClientState.Login -= OnLogin;

            // Dispose windows
            LootWindow?.Dispose();
            ConfigWindow?.Dispose();

            // Remove commands
            CommandManager.RemoveHandler(CommandName);
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

            switch (arguments)
            {
                case "config":
                case "settings":
                    OpenConfigUi();
                    break;
                case "toggle":
                    ToggleMainWindow();
                    break;
                case "show":
                    ShowMainWindow();
                    break;
                case "hide":
                    HideMainWindow();
                    break;
                case "clear":
                    LootTracker.ClearHistory();
                    ChatGui.Print("[LootView] Loot history cleared.");
                    break;
                case "help":
                    ShowHelp();
                    break;
                default:
                    ToggleMainWindow();
                    break;
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

    private void ShowMainWindow()
    {
        LootWindow.IsOpen = true;
        ConfigService.Configuration.IsVisible = true;
        ConfigService.Save();
    }

    private void HideMainWindow()
    {
        LootWindow.IsOpen = false;
        ConfigService.Configuration.IsVisible = false;
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

    private void ShowHelp()
    {
        ChatGui.Print("[LootView] Available commands:");
        ChatGui.Print("  /lootview or /lv - Toggle the loot window");
        ChatGui.Print("  /lootview config - Open configuration window");
        ChatGui.Print("  /lootview show - Show the loot window");
        ChatGui.Print("  /lootview hide - Hide the loot window");
        ChatGui.Print("  /lootview clear - Clear loot history");
        ChatGui.Print("  /lootview toggle - Toggle the loot window");
        ChatGui.Print("  /lootview help - Show this help message");
    }
}