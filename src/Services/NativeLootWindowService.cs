#nullable enable

using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace LootView.Services;

/// <summary>
/// Keeps the game's own Need/Greed window out of sight while LootView's roll buttons can stand
/// in for it. The window is only made invisible, never closed, so the game's loot flow is
/// untouched and the window can be brought back at any time.
/// </summary>
public sealed unsafe class NativeLootWindowService : IDisposable
{
    private const string AddonName = "NeedGreed";

    private readonly Plugin plugin;

    /// <summary>The player asked to see the game's window; honoured until it closes.</summary>
    private bool revealed;

    /// <summary>Whether the window on screen was hidden by us, so we only ever restore our own doing.</summary>
    private bool hiddenByUs;

    public NativeLootWindowService(Plugin plugin)
    {
        this.plugin = plugin;

        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, AddonName, OnShowing);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, AddonName, OnShowing);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreHide, AddonName, OnClosing);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, OnClosing);
    }

    private bool ShouldHide
    {
        get
        {
            var config = plugin.Configuration;
            return config.HideNativeRollWindow && config.EnableRollTracking && config.ShowRollButtons &&
                   plugin.LootTracker.CanRollFromWindow && !revealed;
        }
    }

    /// <summary>The game's window is open but kept hidden by LootView.</summary>
    public bool IsHidden
    {
        get
        {
            var addon = FindAddon();
            return hiddenByUs && addon != null && !addon->IsVisible;
        }
    }

    /// <summary>Shows the game's window until it next closes.</summary>
    public void Reveal()
    {
        revealed = true;
        Refresh();
    }

    /// <summary>Applies the current setting to a window that is already open.</summary>
    public void Refresh()
    {
        var addon = FindAddon();
        if (addon == null)
            return;

        if (ShouldHide)
            Hide(addon);
        else
            Restore(addon);
    }

    private void OnShowing(AddonEvent type, AddonArgs args)
    {
        if (!ShouldHide)
            return;

        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon != null && addon->IsVisible)
            Hide(addon);
    }

    /// <summary>The game hid or closed its window, so the next time it opens it starts hidden again.</summary>
    private void OnClosing(AddonEvent type, AddonArgs args)
    {
        revealed = false;
        hiddenByUs = false;
    }

    private void Hide(AtkUnitBase* addon)
    {
        if (!addon->IsVisible)
            return;

        addon->IsVisible = false;
        if (!hiddenByUs)
            Plugin.Log.Debug("Hid the game's Need/Greed window");
        hiddenByUs = true;
    }

    private void Restore(AtkUnitBase* addon)
    {
        if (!hiddenByUs)
            return;

        addon->IsVisible = true;
        hiddenByUs = false;
        Plugin.Log.Debug("Restored the game's Need/Greed window");
    }

    private static AtkUnitBase* FindAddon()
    {
        var addon = Plugin.GameGui.GetAddonByName(AddonName);
        return addon.IsNull ? null : (AtkUnitBase*)addon.Address;
    }

    public void Dispose()
    {
        Plugin.AddonLifecycle.UnregisterListener(OnShowing, OnClosing);

        // Never leave the game's window invisible after LootView unloads.
        var addon = FindAddon();
        if (addon != null)
            Restore(addon);
    }
}
