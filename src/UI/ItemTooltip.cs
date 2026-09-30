using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Item = Lumina.Excel.Sheets.Item;

namespace LootView.UI;

/// <summary>
/// The item card shown when hovering an item in the loot and roll windows: item level, job,
/// stats, bonuses, materia and description, read from the game's Item sheet. Callers can append
/// their own rows (who got it, where, the roll) below the item details.
/// </summary>
public static class ItemTooltip
{
    // BaseParam rows that the game shows as the headline numbers rather than as bonuses.
    private const uint PhysicalDamage = 12;
    private const uint MagicDamage = 13;
    private const uint BlockRate = 17;
    private const uint BlockStrength = 18;
    private const uint Defense = 21;
    private const uint MagicDefense = 24;

    private const uint SoulCrystalSlot = 17;

    private sealed record Details(
        string Name,
        uint IconId,
        uint Rarity,
        string Category,
        int ItemLevel,
        int EquipLevel,
        string Jobs,
        List<(string Label, string Value)> MainStats,
        List<(string Label, int Value)> Bonuses,
        int MateriaSlots,
        bool AdvancedMelding,
        List<string> Traits,
        string Description,
        uint VendorPrice,
        bool CanTryOn);

    private static readonly Dictionary<(uint, bool), Details?> Cache = new();

    /// <summary>Whether the item is gear whose look can be previewed in the Fitting Room.</summary>
    public static bool CanTryOn(uint itemId) => Get(itemId, false)?.CanTryOn ?? false;

    /// <summary>
    /// Draws the tooltip for the hovered item. <paramref name="footer"/> is drawn after the item
    /// details, for context the caller knows about (source, owner, roll). <paramref name="unlock"/>
    /// adds whether a collectible is already yours.
    /// </summary>
    public static void Show(uint itemId, bool isHq, Action? footer = null, string? hint = null,
        UnlockStatus unlock = UnlockStatus.NotCollectible)
    {
        var details = Get(itemId, isHq);
        var rarityColor = Theme.RarityColor(details?.Rarity ?? 1);

        using var s = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(13, 11))
            .Push(ImGuiStyleVar.WindowRounding, Theme.Radius)
            .Push(ImGuiStyleVar.ItemSpacing, new Vector2(8, 4));
        using var c = ImRaii.PushColor(ImGuiCol.PopupBg, new Vector4(0.055f, 0.078f, 0.122f, 0.98f))
            .Push(ImGuiCol.Border, Theme.Alpha(rarityColor, 0.55f));

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 22f);

        if (details == null)
        {
            ImGui.TextColored(Theme.TextMuted, "No item data available.");
        }
        else
        {
            DrawHeader(details, isHq, rarityColor);

            if (ItemUnlocks.Describe(unlock) is { } seal)
            {
                ImGui.Dummy(new Vector2(0, 2));
                Theme.IconText(seal.Icon, seal.Text, seal.Color);
            }
            DrawGear(details);
            DrawTraits(details);

            if (details.Description.Length > 0)
            {
                Theme.Rule(5f);
                ImGui.TextColored(Theme.TextMuted, details.Description);
            }

            if (details.VendorPrice > 0)
            {
                ImGui.Dummy(new Vector2(0, 2));
                Theme.IconText(FontAwesomeIcon.Coins, $"Sells to vendors for {details.VendorPrice:N0} gil", Theme.TextFaint);
            }
        }

        if (footer != null)
        {
            Theme.Rule(5f);
            footer();
        }

        if (hint != null)
        {
            ImGui.Dummy(new Vector2(0, 2));
            using var font = new Theme.FontScale(0.9f);
            ImGui.TextColored(Theme.TextFaint, hint);
        }

        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    /// <summary>
    /// The item's market board price as a tooltip row, once it has been fetched. Asks for the
    /// price when it isn't known yet, so it shows up the next time the item is hovered.
    /// </summary>
    public static void MarketRow(Plugin plugin, uint itemId, bool isHq)
    {
        var market = plugin.MarketPriceService;
        if (!plugin.Configuration.EnableMarketPrices || !market.IsMarketable(itemId))
            return;

        market.RequestPrices([itemId]);
        if (market.TryGetPrice(itemId, out var price) && price.HasData)
        {
            Row(FontAwesomeIcon.Coins, "Market", $"{price.Average(isHq):N0} gil avg", Theme.GoldBright);
            if (price.Minimum(isHq) > 0)
                Row(FontAwesomeIcon.Tag, "Listed from", $"{price.Minimum(isHq):N0} gil");
        }
        else if (market.IsFetching)
        {
            Row(FontAwesomeIcon.Coins, "Market", "fetching...", Theme.TextFaint);
        }
    }

    /// <summary>A label and value on one line, lined up with the other rows of the tooltip.</summary>
    public static void Row(FontAwesomeIcon icon, string label, string value, Vector4? valueColor = null)
    {
        Theme.Icon(icon, Theme.TextFaint);
        ImGui.SameLine(0, 8);
        ImGui.TextColored(Theme.TextMuted, label);
        ImGui.SameLine(115);
        ImGui.TextColored(valueColor ?? Theme.Text, value);
    }

    private static void DrawHeader(Details d, bool isHq, Vector4 rarityColor)
    {
        if (d.IconId > 0)
        {
            try
            {
                var tex = Plugin.TextureProvider
                    .GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(d.IconId, isHq))
                    .GetWrapOrDefault();
                if (tex != null)
                {
                    ImGui.Image(tex.Handle, new Vector2(40, 40));
                    ImGui.SameLine(0, 10);
                }
            }
            catch { /* Ignore icon loading errors */ }
        }

        ImGui.BeginGroup();
        using (new Theme.FontScale(1.08f))
        {
            ImGui.TextColored(rarityColor, d.Name);
        }

        Theme.RarityGem(d.Rarity, 9f);
        ImGui.SameLine(0, 5);
        ImGui.TextColored(Theme.Alpha(rarityColor, 0.8f), d.Category.Length > 0 ? d.Category : Theme.RarityName(d.Rarity));
        if (isHq)
        {
            ImGui.SameLine(0, 8);
            Theme.Badge("HQ", Theme.Warn);
        }
        ImGui.EndGroup();
    }

    private static void DrawGear(Details d)
    {
        if (d.ItemLevel <= 1 && d.EquipLevel <= 1 && d.MainStats.Count == 0 && d.Bonuses.Count == 0)
            return;

        Theme.Rule(5f);

        if (d.ItemLevel > 1)
        {
            ImGui.TextColored(Theme.GoldBright, $"Item Level {d.ItemLevel}");
        }

        if (d.Jobs.Length > 0)
        {
            ImGui.TextColored(Theme.Text, d.Jobs);
            if (d.EquipLevel > 1)
                ImGui.TextColored(Theme.TextMuted, $"Lv. {d.EquipLevel}");
        }

        if (d.MainStats.Count > 0)
        {
            ImGui.Dummy(new Vector2(0, 2));
            using var table = ImRaii.Table("##mainstats", d.MainStats.Count, ImGuiTableFlags.SizingFixedSame);
            if (table)
            {
                foreach (var _ in d.MainStats)
                    ImGui.TableSetupColumn(string.Empty, ImGuiTableColumnFlags.WidthFixed, 96f);

                ImGui.TableNextRow();
                foreach (var (label, _) in d.MainStats)
                {
                    ImGui.TableNextColumn();
                    using var font = new Theme.FontScale(0.88f);
                    ImGui.TextColored(Theme.TextFaint, label);
                }

                ImGui.TableNextRow();
                foreach (var (_, value) in d.MainStats)
                {
                    ImGui.TableNextColumn();
                    using var font = new Theme.FontScale(1.12f);
                    ImGui.TextColored(Theme.Text, value);
                }
            }
        }

        if (d.Bonuses.Count > 0)
        {
            ImGui.Dummy(new Vector2(0, 2));
            ImGui.TextColored(Theme.Gold, "Bonuses");

            // Two bonuses per line, like the game's own tooltip.
            for (var i = 0; i < d.Bonuses.Count; i++)
            {
                var (label, value) = d.Bonuses[i];
                if (i % 2 == 1)
                    ImGui.SameLine(ImGui.GetCursorStartPos().X + 170f);

                ImGui.TextColored(Theme.TextMuted, label);
                ImGui.SameLine(0, 5);
                ImGui.TextColored(Theme.Good, $"+{value}");
            }
        }

        if (d.MateriaSlots > 0)
        {
            ImGui.Dummy(new Vector2(0, 2));
            DrawMateriaSlots(d.MateriaSlots, d.AdvancedMelding);
        }
    }

    private static void DrawMateriaSlots(int slots, bool advanced)
    {
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        var h = ImGui.GetTextLineHeight();

        for (var i = 0; i < slots; i++)
        {
            var center = new Vector2(p.X + 6f + i * 15f, p.Y + h * 0.5f);
            dl.AddCircleFilled(center, 5f, Theme.U32(Theme.Crystal, 0.25f), 16);
            dl.AddCircle(center, 5f, Theme.U32(Theme.CrystalBright, 0.8f), 16, 1.2f);
        }

        ImGui.Dummy(new Vector2(slots * 15f, h));
        ImGui.SameLine(0, 6);
        ImGui.TextColored(Theme.TextMuted, advanced ? $"{slots} materia slots · overmelding allowed" : $"{slots} materia slots");
    }

    private static void DrawTraits(Details d)
    {
        if (d.Traits.Count == 0)
            return;

        ImGui.Dummy(new Vector2(0, 2));
        for (var i = 0; i < d.Traits.Count; i++)
        {
            if (i > 0)
                ImGui.SameLine(0, 5);

            var trait = d.Traits[i];
            var color = trait switch
            {
                "Unique" => Theme.Warn,
                "Untradable" => Theme.Bad,
                _ => Theme.Crystal,
            };

            // Wrap before a badge that would run off the tooltip.
            if (i > 0 && ImGui.GetCursorPosX() + ImGui.CalcTextSize(trait).X + 16f > ImGui.GetFontSize() * 22f)
                ImGui.NewLine();

            Theme.Badge(trait, color);
        }
    }

    private static Details? Get(uint itemId, bool isHq)
    {
        if (Cache.TryGetValue((itemId, isHq), out var cached))
            return cached;

        Details? details = null;
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Item>();
            if (sheet.TryGetRow(itemId, out var item))
                details = Build(item, isHq);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"Could not read item {itemId} for its tooltip");
        }

        Cache[(itemId, isHq)] = details;
        return details;
    }

    private static Details Build(Item item, bool isHq)
    {
        // HQ gear adds a bonus on top of the NQ value of the same parameter.
        var hqBonus = new Dictionary<uint, int>();
        if (isHq)
        {
            for (var i = 0; i < item.BaseParamSpecial.Count; i++)
            {
                var id = item.BaseParamSpecial[i].RowId;
                if (id != 0)
                    hqBonus[id] = hqBonus.GetValueOrDefault(id) + item.BaseParamValueSpecial[i];
            }
        }

        int WithHq(uint param, int value) => value + hqBonus.GetValueOrDefault(param);

        var mainStats = new List<(string, string)>();
        if (item.DamagePhys > 0 || item.DamageMag > 0)
        {
            var physical = WithHq(PhysicalDamage, item.DamagePhys);
            var magic = WithHq(MagicDamage, item.DamageMag);

            // Modern weapons carry the same value in both; older ones only fill the one their role uses.
            if (physical == magic)
                mainStats.Add(("Damage", physical.ToString()));
            else if (physical > magic)
                mainStats.Add(("Physical Damage", physical.ToString()));
            else
                mainStats.Add(("Magic Damage", magic.ToString()));

            if (item.Delayms > 0)
            {
                var delay = item.Delayms / 1000f;
                var damage = Math.Max(physical, magic);
                mainStats.Add(("Auto-attack", $"{damage * delay / 3f:F2}"));
                mainStats.Add(("Delay", $"{delay:F2}"));
            }
        }
        else if (item.Block > 0 || item.BlockRate > 0)
        {
            mainStats.Add(("Block Strength", WithHq(BlockStrength, item.Block).ToString()));
            mainStats.Add(("Block Rate", WithHq(BlockRate, item.BlockRate).ToString()));
        }
        else if (item.DefensePhys > 0 || item.DefenseMag > 0)
        {
            mainStats.Add(("Defense", WithHq(Defense, item.DefensePhys).ToString()));
            mainStats.Add(("Magic Defense", WithHq(MagicDefense, item.DefenseMag).ToString()));
        }

        var bonuses = new List<(string, int)>();
        for (var i = 0; i < item.BaseParam.Count; i++)
        {
            var param = item.BaseParam[i];
            var value = item.BaseParamValue[i];
            if (param.RowId == 0 || value == 0 || !param.IsValid)
                continue;

            bonuses.Add((param.Value.Name.ExtractText(), WithHq(param.RowId, value)));
        }

        var equipSlot = item.EquipSlotCategory.RowId;
        var isGear = equipSlot != 0;

        var traits = new List<string>();
        if (item.IsUnique) traits.Add("Unique");
        if (item.IsUntradable) traits.Add("Untradable");
        if (isGear && item.DyeCount > 0) traits.Add(item.DyeCount > 1 ? $"Dyeable x{item.DyeCount}" : "Dyeable");
        if (item.IsCollectable) traits.Add("Collectable");
        if (item.StackSize > 1) traits.Add($"Stack {item.StackSize:N0}");

        var category = item.ItemUICategory.IsValid ? item.ItemUICategory.Value.Name.ExtractText() : string.Empty;
        var jobs = isGear && item.ClassJobCategory.IsValid ? item.ClassJobCategory.Value.Name.ExtractText() : string.Empty;

        return new Details(
            Name: item.Name.ExtractText(),
            IconId: item.Icon,
            Rarity: item.Rarity,
            Category: category,
            ItemLevel: isGear ? (int)item.LevelItem.RowId : 0,
            EquipLevel: isGear ? item.LevelEquip : 0,
            Jobs: jobs,
            MainStats: mainStats,
            Bonuses: bonuses,
            MateriaSlots: item.MateriaSlotCount,
            AdvancedMelding: item.IsAdvancedMeldingPermitted,
            Traits: traits,
            Description: item.Description.ExtractText().Trim(),
            VendorPrice: item.PriceLow * (isHq ? 11u : 10u) / 10u,
            CanTryOn: isGear && equipSlot != SoulCrystalSlot);
    }
}
