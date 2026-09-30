using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace LootView.UI;

/// <summary>Right-click actions shared by every window that lists items.</summary>
public static class ItemActions
{
    /// <summary>Opens the game's Fitting Room with the item on your character.</summary>
    public static void TryOn(uint itemId)
    {
        Plugin.Framework.RunOnFrameworkThread(() =>
        {
            try
            {
                if (!TryOnNow(itemId))
                    Plugin.Log.Warning($"The Fitting Room refused item {itemId}");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, $"Could not try on item {itemId}");
            }
        });
    }

    private static unsafe bool TryOnNow(uint itemId) => AgentTryon.TryOn(0, itemId);

    /// <summary>
    /// The item entries of a context menu: Try On for gear, then copy and lookup links.
    /// Draw inside an open popup.
    /// </summary>
    public static void DrawMenuItems(uint itemId, string itemName, bool isMarketable)
    {
        if (ItemTooltip.CanTryOn(itemId))
        {
            if (MenuItem(FontAwesomeIcon.Tshirt, Theme.Gold, "Try On"))
                TryOn(itemId);
        }

        if (MenuItem(FontAwesomeIcon.Copy, Theme.Crystal, "Copy item name"))
            ImGui.SetClipboardText(itemName);

        if (MenuItem(FontAwesomeIcon.Book, Theme.TextMuted, "Open on Garland Tools"))
            Util.OpenLink($"https://www.garlandtools.org/db/#item/{itemId}");

        if (isMarketable && MenuItem(FontAwesomeIcon.ChartLine, Theme.TextMuted, "Open on Universalis"))
            Util.OpenLink($"https://universalis.app/market/{itemId}");
    }

    public static bool MenuItem(FontAwesomeIcon icon, Vector4 iconColor, string label)
    {
        Theme.Icon(icon, iconColor);
        ImGui.SameLine(0, 8);
        return ImGui.MenuItem(label);
    }
}
