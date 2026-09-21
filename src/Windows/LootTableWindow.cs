using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using LootView.Services;
using LootView.UI;

namespace LootView.Windows;

/// <summary>
/// Window for displaying zone/duty loot tables.
/// </summary>
public class LootTableWindow : Window
{
    private readonly Plugin plugin;
    private LootTableService.ZoneLootTable currentLootTable;
    private bool isLoading;
    private Task<LootTableService.ZoneLootTable> loadingTask;

    private string filter = string.Empty;
    private int rarityFilter; // 0 = any

    public LootTableWindow(Plugin plugin) : base("Zone Loot Table###LootTableWindow")
    {
        this.plugin = plugin;

        SizeConstraintMin = new Vector2(720, 480);
        SizeConstraintMax = new Vector2(1600, 1200);
        Size = new Vector2(1000, 700);
        WindowFlags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

        currentLootTable = null;
        isLoading = false;
    }

    public void LoadCurrentZone()
    {
        if (isLoading) return;

        isLoading = true;
        loadingTask = plugin.LootTableService.GetCurrentZoneLootTableAsync();
    }

    public void LoadZoneById(uint contentFinderConditionId)
    {
        if (isLoading) return;

        isLoading = true;
        loadingTask = plugin.LootTableService.GetZoneLootTableByIdAsync(contentFinderConditionId);
    }

    protected override void DrawContents()
    {
        try
        {
            BgAlpha = Math.Max(plugin.Configuration.BackgroundAlpha, 0.85f);

            if (loadingTask != null && loadingTask.IsCompleted)
            {
                currentLootTable = loadingTask.Result;
                loadingTask = null;
                isLoading = false;
            }

            DrawHeader();

            if (isLoading)
            {
                DrawLoadingState();
            }
            else if (currentLootTable == null)
            {
                DrawEmptyState();
            }
            else if (!string.IsNullOrEmpty(currentLootTable.ErrorMessage))
            {
                DrawErrorState();
            }
            else
            {
                DrawLootTable();
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error drawing loot table window");
            ImGui.TextColored(Theme.Bad, "Error displaying loot table");
            ImGui.TextColored(Theme.TextMuted, ex.Message);
        }
    }

    private void DrawHeader()
    {
        var zoneName = currentLootTable?.ZoneName ?? "No zone selected";
        var subtitle = currentLootTable is null
            ? "Enter a duty and refresh to load its drops"
            : $"Content Finder {currentLootTable.ContentFinderConditionId}  ·  Territory {currentLootTable.TerritoryId}";

        Theme.WindowHeader(FontAwesomeIcon.Table, zoneName, subtitle, () =>
        {
            var right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
            ImGui.SetCursorPosX(right - 30f);
            if (Theme.IconButton("##RefreshLootTable", FontAwesomeIcon.Sync, "Reload the table for the zone you're in", Theme.Crystal))
            {
                LoadCurrentZone();
            }
        });
    }

    private void DrawLoadingState()
    {
        var avail = ImGui.GetContentRegionAvail();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Math.Max((avail.Y - 80f) * 0.4f, 10f));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max((avail.X - 26f) * 0.5f, 0));

        Theme.Spinner();

        ImGui.Dummy(new Vector2(0, 10));
        Theme.CenteredText("Reading the loot table...", Theme.Text);
        Theme.CenteredText("Fetching drop data for this instance", Theme.TextFaint);
    }

    private void DrawEmptyState()
    {
        Theme.EmptyState(
            FontAwesomeIcon.MapSigns,
            "No loot table loaded",
            "Enter a dungeon, trial or raid, then load the current zone.");

        ImGui.Dummy(new Vector2(0, 14));
        var w = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max((w - 200f) * 0.5f, 0));
        if (Theme.PrimaryButton("Load current zone", new Vector2(200, 34)))
        {
            LoadCurrentZone();
        }
    }

    private void DrawErrorState()
    {
        ImGui.Dummy(new Vector2(0, 10));
        Theme.Callout(FontAwesomeIcon.ExclamationTriangle, "Couldn't load this loot table",
            currentLootTable.ErrorMessage, Theme.Bad);

        ImGui.Dummy(new Vector2(0, 12));
        if (Theme.GhostButton("Try again", new Vector2(140, 32)))
        {
            LoadCurrentZone();
        }
    }

    private void DrawLootTable()
    {
        if (currentLootTable.Items.Count == 0)
        {
            Theme.EmptyState(FontAwesomeIcon.BoxOpen, "No loot data for this zone",
                "This instance has no drop table on record yet.");
            return;
        }

        DrawFilterBar();

        var items = currentLootTable.Items.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(filter))
            items = items.Where(i => i.ItemName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                     || i.Source.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                     || i.Category.Contains(filter, StringComparison.OrdinalIgnoreCase));

        if (rarityFilter > 0)
            items = items.Where(i => i.Rarity == rarityFilter);

        var visible = items.ToList();

        ImGui.Dummy(new Vector2(0, 4));

        if (visible.Count == 0)
        {
            Theme.EmptyState(FontAwesomeIcon.Search, "Nothing matches your filter",
                "Try a different name, or reset the rarity filter.");
            return;
        }

        var availableHeight = ImGui.GetContentRegionAvail().Y - 4;

        using var table = ImRaii.Table("LootTableTable", 6,
            ImGuiTableFlags.RowBg |
            ImGuiTableFlags.ScrollY |
            ImGuiTableFlags.Sortable |
            ImGuiTableFlags.Resizable |
            ImGuiTableFlags.BordersInnerV |
            ImGuiTableFlags.SizingFixedFit,
            new Vector2(0, availableHeight));

        if (!table) return;

        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort | ImGuiTableColumnFlags.NoResize, 36);
        ImGui.TableSetupColumn("Item Name", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("iLvl", ImGuiTableColumnFlags.WidthFixed, 44);
        ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthFixed, 130);
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthFixed, 190);
        ImGui.TableSetupColumn("Rarity", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        foreach (var item in visible)
        {
            ImGui.TableNextRow(ImGuiTableRowFlags.None, 34f);

            var itemColor = Theme.RarityColor(item.Rarity);

            // Icon column
            ImGui.TableSetColumnIndex(0);
            if (item.ItemId > 0 && item.IconId > 0)
            {
                try
                {
                    var iconTexture = Plugin.TextureProvider
                        .GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(item.IconId))
                        .GetWrapOrDefault();
                    if (iconTexture != null)
                    {
                        ImGui.Image(iconTexture.Handle, new Vector2(28, 28));
                        if (ImGui.IsItemHovered())
                        {
                            Theme.Tooltip($"{item.ItemName}\nItem Level {item.ItemLevel}\n{item.Category}\nSource: {item.Source}");
                        }
                    }
                }
                catch { /* Ignore icon loading errors */ }
            }

            // Item name column
            ImGui.TableSetColumnIndex(1);
            ImGui.AlignTextToFramePadding();
            Theme.RarityGem(item.Rarity, 9f);
            ImGui.SameLine(0, 7);
            ImGui.TextColored(itemColor, item.ItemName);

            // Item Level column
            ImGui.TableSetColumnIndex(2);
            ImGui.AlignTextToFramePadding();
            if (item.ItemLevel > 0)
            {
                ImGui.TextColored(Theme.Gold, item.ItemLevel.ToString());
            }
            else
            {
                ImGui.TextColored(Theme.TextFaint, "-");
            }

            // Category column
            ImGui.TableSetColumnIndex(3);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.TextMuted, item.Category);

            // Source column
            ImGui.TableSetColumnIndex(4);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Alpha(Theme.Crystal, 0.9f), item.Source);

            // Rarity column
            ImGui.TableSetColumnIndex(5);
            ImGui.AlignTextToFramePadding();
            if (item.ItemId > 0)
            {
                Theme.Badge(Theme.RarityName((uint)Math.Max(item.Rarity, 0)), itemColor);
            }
        }
    }

    private void DrawFilterBar()
    {
        ImGui.AlignTextToFramePadding();
        Theme.Icon(FontAwesomeIcon.Search, Theme.TextFaint);
        ImGui.SameLine(0, 8);
        ImGui.SetNextItemWidth(240);
        ImGui.InputTextWithHint("##LootTableFilter", "Filter by name, source or category", ref filter, 120);

        ImGui.SameLine(0, 12);
        Theme.SegmentedControl("##RarityFilter", ref rarityFilter, "All", "Common", "Uncommon", "Rare", "Relic");

        ImGui.SameLine();
        var count = currentLootTable.Items.Count;
        var badge = $"{count} items";
        var bw = ImGui.CalcTextSize(badge).X + 18;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(ImGui.GetContentRegionAvail().X - bw, 0));
        ImGui.AlignTextToFramePadding();
        Theme.Badge(badge, Theme.Gold);

        ImGui.Dummy(new Vector2(0, 2));
        Theme.Callout(FontAwesomeIcon.ExclamationTriangle, "Work in progress",
            "Loot table data is still being corrected and expanded. Some entries may be incomplete or inaccurate.",
            Theme.Warn);
    }

    public override void Dispose()
    {
        base.Dispose();
    }
}
