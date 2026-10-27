using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using LootView.Models;
using LootView.UI;

namespace LootView.Windows;

/// <summary>
/// The ticker: while the loot window is closed, each drop slides in on a slim strip that can sit
/// anywhere on screen, stays a few seconds and fades out. It reads its direction from where it was
/// placed: low on the screen new drops come in at the bottom and push the older ones up, high on
/// the screen the other way round, and it hugs whichever side it is nearer to. A drop of something
/// already showing adds to that row instead of starting a new one, so gathering doesn't flood it.
/// </summary>
public sealed class LootTicker : IDisposable
{
    private static float Width => Theme.Px(300f);
    private static float RowHeight => Math.Max(Theme.Px(30f), ImGui.GetTextLineHeight() + Theme.Px(12f));
    private static float RowGap => Theme.Px(4f);
    private static float IconSize => RowHeight - Theme.Px(8f);
    private static float ControlsHeight => Theme.Px(26f);

    private const float EnterSeconds = 0.25f;
    private const float LeaveSeconds = 0.35f;

    /// <summary>How long the empty strip stays visible after minimising, so you can see where it went.</summary>
    private const float PlaceholderSeconds = 5f;

    private readonly Plugin plugin;
    private readonly List<Row> rows = new();

    private bool hovered;
    private bool dragging, pressedInside;
    private DateTime placeholderUntil = DateTime.MinValue;

    private sealed class Row
    {
        public LootItem Item = null!;
        public long Quantity;
        public DateTime Expires;
        public DateTime Bumped = DateTime.MinValue;
        public float Presence;
        public bool Leaving;
    }

    public LootTicker(Plugin plugin)
    {
        this.plugin = plugin;
        plugin.LootTracker.LootObtained += OnLootObtained;
    }

    private Configuration Config => plugin.ConfigService.Configuration;

    /// <summary>The ticker stands in for the loot window while it's closed, and can run beside it too.</summary>
    private bool Active => Config.ShowTicker && (Config.TickerWithWindowOpen || !plugin.LootWindow.IsOpen) && Plugin.ClientState.IsLoggedIn;

    /// <summary>Shows the empty strip for a few seconds, e.g. right after minimising into it.</summary>
    public void Reveal() => placeholderUntil = DateTime.Now.AddSeconds(PlaceholderSeconds);

    private readonly Queue<(DateTime At, LootItem Item)> preview = new();

    /// <summary>
    /// Turns the ticker on, closing the loot window into it unless it runs beside the window, and
    /// plays a few sample drops on it: gear, a minion,
    /// a party member's drop, and crystals that add up on one row. They only exist on the ticker.
    /// </summary>
    public void Preview()
    {
        Config.ShowTicker = true;
        if (!Config.TickerWithWindowOpen)
        {
            Config.IsVisible = false;
            plugin.LootWindow.IsOpen = false;
        }
        plugin.ConfigService.Save();

        preview.Clear();
        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
        var at = DateTime.Now;

        void Queue(uint itemId, uint quantity, double gap, string? player = null)
        {
            if (!sheet.TryGetRow(itemId, out var row)) return;
            at = at.AddSeconds(gap);
            preview.Enqueue((at, new LootItem
            {
                ItemId = itemId,
                ItemName = row.Name.ExtractText(),
                IconId = row.Icon,
                Rarity = row.Rarity,
                Quantity = quantity,
                IsOwnLoot = player == null,
                PlayerName = player ?? Plugin.ObjectTable.LocalPlayer?.Name.TextValue ?? "You",
            }));
        }

        var gear = sheet.Where(i => i.Rarity == 3 && i.EquipSlotCategory.RowId != 0 && i.Icon > 0).OrderByDescending(i => i.LevelItem.RowId).Skip(Random.Shared.Next(30)).FirstOrDefault();
        var minion = sheet.Where(i => i.ItemUICategory.RowId == 81 && i.Icon > 0).Skip(Random.Shared.Next(40)).FirstOrDefault();

        Queue(gear.RowId, 1, 0.3);
        Queue(4, 3, 0.9);
        Queue(minion.RowId, 1, 1.0);
        Queue(4, 5, 0.8);
        Queue(1, 2_400, 0.9);
        Queue(gear.RowId, 1, 0.9, "Y'shtola Rhul");
        Queue(4, 2, 0.7);
    }

    private void UpdatePreview()
    {
        while (preview.Count > 0 && preview.Peek().At <= DateTime.Now)
        {
            var item = preview.Dequeue().Item;
            item.Timestamp = DateTime.Now;
            OnLootObtained(item);
        }
    }

    private void OnLootObtained(LootItem item)
    {
        if (!Active || item.ItemId == 0 || item.Quantity == 0) return;

        var config = Config;
        if (config.ShowOnlyOwnLoot && !item.IsOwnLoot) return;
        if (config.MinimumRarity > 0 && item.Rarity < config.MinimumRarity) return;
        if (config.ShowHQOnly && !item.IsHQ) return;
        if (config.BlacklistedItemIds?.Contains(item.ItemId) ?? false) return;

        var expires = DateTime.Now.AddSeconds(Math.Max(config.TickerSeconds, 2));

        var same = rows.FirstOrDefault(r => !r.Leaving && r.Item.ItemId == item.ItemId && r.Item.IsHQ == item.IsHQ && r.Item.PlayerName == item.PlayerName);
        if (same != null)
        {
            same.Quantity += item.Quantity;
            same.Expires = expires;
            same.Bumped = DateTime.Now;
            return;
        }

        rows.Insert(0, new Row { Item = item, Quantity = item.Quantity, Expires = expires });

        // Past the row limit the oldest make way.
        var limit = Math.Max(config.TickerRows, 1);
        foreach (var old in rows.Where(r => !r.Leaving).Skip(limit))
            old.Leaving = true;
    }

    // ============================================================================
    // DRAWING
    // ============================================================================

    public void Draw()
    {
        if (!Active)
        {
            rows.Clear();
            hovered = dragging = false;
            return;
        }

        var dt = Math.Clamp(ImGui.GetIO().DeltaTime, 0f, 0.1f);
        UpdatePreview();
        Update(dt);

        // The empty strip shows while you might be looking for it: right after minimising, while
        // the settings are open, or while it's being dragged.
        var placeholder = rows.Count == 0 && (DateTime.Now < placeholderUntil || plugin.ConfigWindow.IsOpen || dragging);
        if (rows.Count == 0 && !placeholder)
        {
            hovered = dragging = false;
            return;
        }

        using var theme = Theme.Push();
        Theme.UiScale = Math.Clamp(Config.LootWindowScale, LootWindow.MinScale, LootWindow.MaxScale);
        try
        {
            DrawWindow(placeholder);
        }
        finally
        {
            Theme.UiScale = 1f;
        }
    }

    private void Update(float dt)
    {
        var now = DateTime.Now;
        foreach (var row in rows)
        {
            // Rows wait while you're reading them.
            if (hovered && !row.Leaving) row.Expires = row.Expires.AddSeconds(dt);
            if (!row.Leaving && now >= row.Expires) row.Leaving = true;

            row.Presence = row.Leaving
                ? Math.Max(row.Presence - dt / LeaveSeconds, 0f)
                : Math.Min(row.Presence + dt / EnterSeconds, 1f);
        }

        rows.RemoveAll(r => r.Leaving && r.Presence <= 0f);
    }

    private void DrawWindow(bool placeholder)
    {
        var viewport = ImGui.GetMainViewport();
        var screen = viewport.Size;
        var growUp = Config.TickerY > 0.5f;
        var onRight = Config.TickerX > 0.5f;

        var slot = RowHeight + RowGap;
        var stack = placeholder ? slot : rows.Sum(r => Ease(r.Presence) * slot);
        var controls = hovered || placeholder ? ControlsHeight : 0f;
        var height = Math.Max(stack + controls, 1f);

        // The anchor is the strip's centre across and its base edge down: the bottom when it grows
        // upwards, the top when it grows downwards. New rows appear at the base.
        var baseY = viewport.Pos.Y + Config.TickerY * screen.Y;
        var top = growUp ? baseY - height : baseY;
        var left = viewport.Pos.X + Config.TickerX * screen.X - Width * 0.5f;

        ImGui.SetNextWindowPos(new Vector2(left, top));
        ImGui.SetNextWindowSize(new Vector2(Width, height));

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize |
                                       ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing |
                                       ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoScrollWithMouse |
                                       ImGuiWindowFlags.NoBackground;

        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (ImGui.Begin("##LootViewTicker", flags))
        {
            ImGui.SetWindowFontScale(Theme.UiScale);
            try
            {
                var dl = ImGui.GetWindowDrawList();
                var origin = new Vector2(left, top);

                // Controls sit at the far end from the base, so showing them never moves a row.
                var controlsY = growUp ? origin.Y : origin.Y + stack;
                var stackTop = growUp ? origin.Y + controls : origin.Y;

                if (placeholder)
                    DrawPlaceholder(dl, new Vector2(left, stackTop), onRight);
                else
                    DrawRows(dl, left, stackTop, stack, growUp, onRight);

                if (controls > 0)
                    DrawControls(new Vector2(left, controlsY), onRight);

                hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByPopup | ImGuiHoveredFlags.ChildWindows);
                HandleDrag(origin, height, screen, viewport.Pos);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Error drawing the loot ticker");
            }
        }
        ImGui.End();
    }

    private void DrawRows(ImDrawListPtr dl, float left, float stackTop, float stack, bool growUp, bool onRight)
    {
        var slot = RowHeight + RowGap;

        // Newest at the base edge: the bottom of the stack when growing up, the top otherwise.
        var y = growUp ? stackTop + stack : stackTop;
        foreach (var row in rows)
        {
            // A row takes its share of the stack as it fades in or out, so the others glide.
            var share = Ease(row.Presence) * slot;
            if (growUp) y -= share;

            DrawRow(dl, row, left, y, onRight);

            if (!growUp) y += share;
        }
    }

    private void DrawRow(ImDrawListPtr dl, Row row, float left, float y, bool onRight)
    {
        var item = row.Item;
        var alpha = Ease(row.Presence);
        if (alpha <= 0.01f) return;

        var rarity = Theme.RarityColor(item.Rarity);
        var unlock = Config.ShowUnlockStatus ? ItemUnlocks.Get(item.ItemId) : UnlockStatus.NotCollectible;
        var textY = y + (RowHeight - ImGui.GetTextLineHeight()) * 0.5f;

        var name = item.ItemName;
        var quantity = row.Quantity > 1 ? $"x{row.Quantity:N0}" : string.Empty;
        var player = item.IsOwnLoot ? string.Empty : item.PlayerName;

        // The pill is as wide as what it holds, up to the strip's width.
        var pad = Theme.Px(8f);
        var gap = Theme.Px(7f);
        var nameWidth = Math.Min(ImGui.CalcTextSize(name).X, Theme.Px(170f));
        var qtyWidth = quantity.Length > 0 ? ImGui.CalcTextSize(quantity).X + gap : 0f;
        var playerWidth = player.Length > 0 ? Math.Min(ImGui.CalcTextSize(player).X, Theme.Px(90f)) + gap : 0f;
        var hqWidth = item.IsHQ ? Theme.Px(20f) : 0f;
        var pillWidth = Math.Min(Theme.Px(5f) + IconSize + gap + nameWidth + hqWidth + qtyWidth + playerWidth + pad, Width);

        // New rows slide in from the side the strip hugs.
        var slide = (1f - alpha) * Theme.Px(26f) * (onRight ? 1f : -1f);
        var x = (onRight ? left + Width - pillWidth : left) + slide;
        var min = new Vector2(x, y);
        var max = new Vector2(x + pillWidth, y + RowHeight);

        var bump = (float)Math.Max(0, 1 - (DateTime.Now - row.Bumped).TotalSeconds / 0.6);
        var radius = RowHeight * 0.5f;

        if (unlock == UnlockStatus.Locked)
        {
            var glow = 0.5f + 0.5f * MathF.Sin(Theme.Time * 3f);
            dl.AddRectFilled(min - new Vector2(Theme.Px(2f)), max + new Vector2(Theme.Px(2f)), Theme.U32(Theme.GoldBright, (0.12f + 0.12f * glow) * alpha), radius + Theme.Px(2f));
        }

        dl.AddRectFilled(min, max, Theme.U32(Theme.Ink, 0.86f * alpha), radius);
        dl.AddRectFilledMultiColor(min, new Vector2(max.X, min.Y + RowHeight * 0.5f),
            Theme.U32(rarity, 0.10f * alpha), Theme.U32(rarity, 0.04f * alpha), Theme.U32(rarity, 0f), Theme.U32(rarity, 0f));
        var edge = unlock == UnlockStatus.Locked ? Theme.GoldBright : Theme.Mix(rarity, Theme.GoldBright, bump);
        dl.AddRect(min, max, Theme.U32(edge, (0.45f + 0.5f * bump) * alpha), radius, ImDrawFlags.None, 1f);

        // Icon.
        var iconPos = new Vector2(x + Theme.Px(5f), y + (RowHeight - IconSize) * 0.5f);
        if (item.IconId > 0)
        {
            try
            {
                var tex = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(item.IconId)).GetWrapOrDefault();
                if (tex != null)
                    dl.AddImageRounded(tex.Handle, iconPos, iconPos + new Vector2(IconSize), Vector2.Zero, Vector2.One, Theme.U32(new Vector4(1, 1, 1, 1), alpha), IconSize * 0.5f);
            }
            catch { /* Ignore icon loading errors */ }
        }
        dl.AddCircle(iconPos + new Vector2(IconSize * 0.5f), IconSize * 0.5f, Theme.U32(rarity, 0.8f * alpha), 24, 1f);
        ItemUnlocks.DrawIconSeal(dl, iconPos + new Vector2(IconSize), unlock, 10f);

        // Name, HQ, quantity, and who got it when it wasn't you.
        var tx = iconPos.X + IconSize + gap;
        Theme.ClipText(dl, new Vector2(tx, textY), nameWidth + 1f, name, Theme.U32(rarity, alpha));
        tx += nameWidth;

        if (item.IsHQ)
        {
            using (new Theme.FontScale(0.8f))
                dl.AddText(new Vector2(tx + Theme.Px(4f), textY + Theme.Px(1f)), Theme.U32(Theme.Warn, alpha), "HQ");
            tx += hqWidth;
        }

        if (quantity.Length > 0)
        {
            tx += gap;
            var qtyColor = Theme.Mix(Theme.CrystalBright, Theme.GoldBright, bump);
            dl.AddText(new Vector2(tx, textY), Theme.U32(qtyColor, alpha), quantity);
            tx += qtyWidth - gap;
        }

        if (player.Length > 0)
        {
            tx += gap;
            Theme.ClipText(dl, new Vector2(tx, textY), playerWidth - gap, player, Theme.U32(Theme.TextMuted, alpha));
        }

        // Hover for the item's details, right-click for its actions, as in the loot window.
        ImGui.SetCursorScreenPos(min);
        ImGui.InvisibleButton($"##tick_{item.Id}", max - min);
        if (ImGui.IsItemHovered() && !dragging && Config.ShowTooltips)
        {
            ItemTooltip.Show(item.ItemId, item.IsHQ, () =>
            {
                if (row.Quantity > 1)
                    ItemTooltip.Row(FontAwesomeIcon.LayerGroup, "Quantity", $"x{row.Quantity:N0}");
                ItemTooltip.MarketRow(plugin, item.ItemId, item.IsHQ);
                Theme.IconText(item.IsOwnLoot ? FontAwesomeIcon.Star : FontAwesomeIcon.User,
                    item.IsOwnLoot ? "You obtained this" : item.PlayerName, item.IsOwnLoot ? Theme.Good : Theme.Text);
            }, "Drag to move the ticker · right-click for more", unlock);
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            ImGui.OpenPopup($"##tickerctx{item.Id}");

        using var popup = ImRaii.Popup($"##tickerctx{item.Id}");
        if (popup)
        {
            Theme.SetFontScale(1f);
            ImGui.TextColored(rarity, name);
            ImGui.Separator();
            ItemActions.DrawMenuItems(item.ItemId, name, plugin.MarketPriceService.IsMarketable(item.ItemId));
        }
    }

    private void DrawPlaceholder(ImDrawListPtr dl, Vector2 at, bool onRight)
    {
        const string text = "LootView ticker - drag to move";
        var gs = Theme.IconWidth(FontAwesomeIcon.GripLines);
        var ts = ImGui.CalcTextSize(text);
        var width = Theme.Px(14f) + gs + Theme.Px(8f) + ts.X + Theme.Px(14f);

        var x = onRight ? at.X + Width - width : at.X;
        var min = new Vector2(x, at.Y);
        var max = new Vector2(x + width, at.Y + RowHeight);
        var pulse = 0.5f + 0.5f * MathF.Sin(Theme.Time * 2.5f);

        dl.AddRectFilled(min, max, Theme.U32(Theme.Ink, 0.75f), RowHeight * 0.5f);
        dl.AddRect(min, max, Theme.U32(Theme.Gold, 0.35f + 0.35f * pulse), RowHeight * 0.5f, ImDrawFlags.None, 1f);

        var textY = at.Y + (RowHeight - ts.Y) * 0.5f;
        using (ImRaii.PushFont(UiBuilder.IconFont))
            dl.AddText(new Vector2(x + Theme.Px(14f), textY + 1f), Theme.U32(Theme.Gold), FontAwesomeIcon.GripLines.ToIconString());
        dl.AddText(new Vector2(x + Theme.Px(14f) + gs + Theme.Px(8f), textY), Theme.U32(Theme.TextMuted), text);
    }

    private void DrawControls(Vector2 at, bool onRight)
    {
        const float size = 20f; // IconButton scales it itself
        var button = Theme.Px(size);
        var gap = Theme.Px(4f);
        var y = at.Y + (ControlsHeight - button) * 0.5f;
        var expand = !plugin.LootWindow.IsOpen;
        var count = expand ? 2 : 1;
        var x = onRight ? at.X + Width - button * count - gap * (count - 1) : at.X;

        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(gap, gap));
        ImGui.SetCursorScreenPos(new Vector2(x, y));
        if (expand)
        {
            if (Theme.IconButton("##TickerExpand", FontAwesomeIcon.ExpandAlt, "Open the loot window", Theme.Gold, false, size))
            {
                plugin.LootWindow.IsOpen = true;
                Config.IsVisible = true;
                plugin.ConfigService.Save();
            }

            ImGui.SameLine();
        }

        if (Theme.IconButton("##TickerOff", FontAwesomeIcon.Times, "Turn the ticker off\nBring it back in Settings → General", Theme.Bad, false, size))
        {
            Config.ShowTicker = false;
            plugin.ConfigService.Save();
        }
    }

    /// <summary>
    /// Dragging anywhere on the strip moves it. On release its direction is re-read from where it
    /// ended up, and the anchor moves to the matching edge so the strip doesn't jump.
    /// </summary>
    private void HandleDrag(Vector2 origin, float height, Vector2 screen, Vector2 viewportPos)
    {
        var io = ImGui.GetIO();

        // A press on the strip, rows included, becomes a move once the mouse travels a little,
        // so a plain click still reaches the row or button under it.
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            pressedInside = true;

        if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            if (pressedInside && !dragging && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 4f))
                dragging = true;

            if (dragging)
            {
                Config.TickerX = Math.Clamp(Config.TickerX + io.MouseDelta.X / screen.X, 0.02f, 0.98f);
                Config.TickerY = Math.Clamp(Config.TickerY + io.MouseDelta.Y / screen.Y, 0.02f, 0.98f);
                ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
            }
            return;
        }

        pressedInside = false;
        if (!dragging) return;
        dragging = false;

        var top = origin.Y - viewportPos.Y;
        var bottom = top + height;
        var growUp = (top + bottom) * 0.5f > screen.Y * 0.5f;
        Config.TickerY = Math.Clamp((growUp ? bottom : top) / screen.Y, 0.02f, 0.98f);
        plugin.ConfigService.Save();
    }

    private static float Ease(float t) => t * t * (3f - 2f * t);

    public void Dispose() => plugin.LootTracker.LootObtained -= OnLootObtained;
}
