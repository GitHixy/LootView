using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Item = Lumina.Excel.Sheets.Item;

namespace LootView.UI;

public enum UnlockStatus
{
    /// <summary>Not something you learn by using it, or unknown right now.</summary>
    NotCollectible,

    /// <summary>A collectible you have already learned.</summary>
    Unlocked,

    /// <summary>A collectible you haven't learned yet.</summary>
    Locked,
}

/// <summary>
/// Whether collectibles - minions, mounts, orchestrion rolls, Triple Triad cards, emotes,
/// hairstyles, portrait frames and the like - are already unlocked on this character.
/// Gear is left out on purpose.
/// </summary>
public static class ItemUnlocks
{
    // Rows of collectible items, or null for items that aren't collectibles.
    private static readonly Dictionary<uint, Item?> Collectibles = new();

    public static UnlockStatus Get(uint itemId)
    {
        if (itemId == 0 || !Plugin.ClientState.IsLoggedIn)
            return UnlockStatus.NotCollectible;

        try
        {
            if (!Collectibles.TryGetValue(itemId, out var row))
            {
                row = null;
                if (Plugin.DataManager.GetExcelSheet<Item>().TryGetRow(itemId, out var item) &&
                    item.EquipSlotCategory.RowId == 0 &&
                    Plugin.UnlockState.IsItemUnlockable(item))
                {
                    row = item;
                }
                Collectibles[itemId] = row;
            }

            // The unlocked state itself isn't cached: it changes the moment you use the item.
            if (row is not { } collectible)
                return UnlockStatus.NotCollectible;

            return Plugin.UnlockState.IsItemUnlocked(collectible) ? UnlockStatus.Unlocked : UnlockStatus.Locked;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"Could not read the unlock state of item {itemId}");
            Collectibles[itemId] = null;
            return UnlockStatus.NotCollectible;
        }
    }

    /// <summary>A short line for tooltips, or null when the item isn't a collectible.</summary>
    public static (FontAwesomeIcon Icon, string Text, Vector4 Color)? Describe(UnlockStatus status) => status switch
    {
        UnlockStatus.Unlocked => (FontAwesomeIcon.CheckCircle, "Already unlocked", Theme.Good),
        UnlockStatus.Locked => (FontAwesomeIcon.Star, "Not unlocked yet", Theme.GoldBright),
        _ => null,
    };

    /// <summary>
    /// A small seal on the bottom-right corner of an item icon: a check when you already have
    /// it, a gold star when you don't.
    /// </summary>
    public static void DrawIconSeal(ImDrawListPtr dl, Vector2 iconMax, UnlockStatus status, float size = 13f)
    {
        if (Describe(status) is not { } seal)
            return;

        var center = iconMax - new Vector2(size * 0.3f, size * 0.3f);
        var radius = size * 0.5f;

        dl.AddCircleFilled(center, radius + 1f, Theme.U32(Theme.Ink, 1f), 16);
        dl.AddCircleFilled(center, radius, Theme.U32(seal.Color, status == UnlockStatus.Locked ? 0.95f : 0.85f), 16);

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var font = ImGui.GetFont();
            var glyph = (status == UnlockStatus.Locked ? FontAwesomeIcon.Star : FontAwesomeIcon.Check).ToIconString();
            var fontSize = size * 0.62f;
            var gs = ImGui.CalcTextSize(glyph) * (fontSize / ImGui.GetFontSize());
            dl.AddText(font, fontSize, center - gs * 0.5f, Theme.U32(Theme.Ink, 1f), glyph);
        }
    }
}
