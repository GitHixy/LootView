using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using LootView.UI;

namespace LootView.Windows;

/// <summary>
/// Real-time window showing active roll sessions, auto-closes 15 seconds after the last winner.
/// </summary>
public class RollWindow : Window
{
    private const double AutoCloseSeconds = 15.0;
    private const float IconSize = 34f;

    private readonly Plugin plugin;
    private DateTime allItemsAwardedTime = DateTime.MinValue;

    public RollWindow(Plugin plugin) : base("Loot Rolls###LootViewRolls")
    {
        this.plugin = plugin;

        IsOpen = false;

        WindowFlags = ImGuiWindowFlags.NoTitleBar |
                      ImGuiWindowFlags.NoScrollbar |
                      ImGuiWindowFlags.AlwaysAutoResize |
                      ImGuiWindowFlags.NoResize;

        SizeConstraintMin = new Vector2(360, 50);
        SizeConstraintMax = new Vector2(700, 640);

        plugin.LootTracker.RollsUpdated += OnRollsUpdated;
    }

    /// <summary>Rolls only happen in-game, so never draw this outside a session.</summary>
    protected override bool ShouldDraw => Plugin.ClientState.IsLoggedIn;

    private void OnRollsUpdated()
    {
        var activeRolls = plugin.LootTracker.ActiveRolls;

        if (activeRolls.Count > 0 && !IsOpen)
        {
            IsOpen = true;
            Plugin.Log.Info($"Roll window opened - {activeRolls.Count} active roll(s)");
        }

        // Check if ALL items have been awarded (all have winners)
        if (activeRolls.Count > 0 && activeRolls.All(r => !string.IsNullOrEmpty(r.WinnerName)))
        {
            if (allItemsAwardedTime == DateTime.MinValue)
            {
                allItemsAwardedTime = DateTime.Now;
                Plugin.Log.Info("All items awarded, starting 15 second countdown");
            }
        }
        else
        {
            allItemsAwardedTime = DateTime.MinValue;
        }
    }

    protected override void DrawContents()
    {
        try
        {
            var activeRolls = plugin.LootTracker.ActiveRolls;

            // Auto-close 15 seconds after ALL items awarded
            if (allItemsAwardedTime != DateTime.MinValue &&
                (DateTime.Now - allItemsAwardedTime).TotalSeconds > AutoCloseSeconds)
            {
                IsOpen = false;
                allItemsAwardedTime = DateTime.MinValue;

                plugin.LootTracker.ClearCompletedRolls();

                Plugin.Log.Info("Roll window auto-closed after 15 seconds - cleared completed roll data");
                return;
            }

            if (activeRolls.Count == 0)
            {
                IsOpen = false;
                return;
            }

            DrawHeader();

            var localPlayerName = Plugin.ObjectTable.LocalPlayer?.Name.TextValue ?? "You";

            for (var index = 0; index < activeRolls.Count; index++)
            {
                DrawRollCard(activeRolls[index], localPlayerName, index);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error drawing roll window");
        }
    }

    private void DrawHeader()
    {
        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        const float h = 28f;
        var width = Math.Max(ImGui.GetContentRegionAvail().X, 340f);

        // Countdown ring doubles as the dice glyph's frame once every item is awarded.
        var center = new Vector2(origin.X + 12f, origin.Y + h * 0.5f);

        if (allItemsAwardedTime != DateTime.MinValue)
        {
            var elapsed = (DateTime.Now - allItemsAwardedTime).TotalSeconds;
            var remaining = (float)Math.Max(AutoCloseSeconds - elapsed, 0);
            var fraction = remaining / (float)AutoCloseSeconds;

            dl.AddCircle(center, 11f, Theme.U32(Theme.Line), 32, 2f);

            dl.PathClear();
            const int segs = 32;
            for (var i = 0; i <= segs; i++)
            {
                var a = -MathF.PI * 0.5f + MathF.PI * 2f * fraction * i / segs;
                dl.PathLineTo(new Vector2(center.X + MathF.Cos(a) * 11f, center.Y + MathF.Sin(a) * 11f));
            }
            dl.PathStroke(Theme.U32(fraction < 0.3f ? Theme.Bad : Theme.Gold, 0.95f), ImDrawFlags.None, 2f);
        }
        else
        {
            var pulse = 0.55f + 0.45f * MathF.Sin(Theme.Time * 3.2f);
            dl.AddCircleFilled(center, 11f, Theme.U32(Theme.Gold, 0.10f + pulse * 0.10f), 32);
            dl.AddCircle(center, 11f, Theme.U32(Theme.Gold, 0.5f + pulse * 0.4f), 32, 1.4f);
        }

        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var glyph = FontAwesomeIcon.Dice.ToIconString();
            var gs = ImGui.CalcTextSize(glyph);
            dl.AddText(center - gs * 0.5f, Theme.U32(Theme.GoldBright), glyph);
        }

        ImGui.SetCursorScreenPos(new Vector2(origin.X + 30f, origin.Y + 4f));
        using (new Theme.FontScale(1.08f))
        {
            ImGui.TextColored(Theme.GoldBright, "Loot Rolls");
        }

        if (allItemsAwardedTime != DateTime.MinValue)
        {
            var remaining = AutoCloseSeconds - (DateTime.Now - allItemsAwardedTime).TotalSeconds;
            ImGui.SameLine(0, 10);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.TextFaint, $"closing in {remaining:F0}s");
        }

        // Close button, right aligned.
        ImGui.SetCursorScreenPos(new Vector2(origin.X + width - 24f, origin.Y + (h - 24f) * 0.5f));
        if (Theme.IconButton("##CloseRolls", FontAwesomeIcon.Times, "Close and clear all rolls", Theme.Bad, false, 24f))
        {
            IsOpen = false;
            allItemsAwardedTime = DateTime.MinValue;
            plugin.LootTracker.ClearAllRolls();
        }

        ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + h));
        ImGui.Dummy(new Vector2(width, 0));
        Theme.Rule(4f);
    }

    private void DrawRollCard(Services.RollInfo rollInfo, string localPlayerName, int index)
    {
        using var id = ImRaii.PushId($"roll_{index}_{rollInfo.ItemId}");

        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = Math.Max(ImGui.GetContentRegionAvail().X, 340f);

        var sortedRolls = rollInfo.GetSortedRolls().ToList();
        var rarityColor = Theme.RarityColor(rollInfo.Rarity);
        var decided = !string.IsNullOrEmpty(rollInfo.WinnerName);

        var rowsHeight = Math.Max(sortedRolls.Count, 1) * 21f;
        var cardHeight = 14f + IconSize + 8f + rowsHeight;

        var max = new Vector2(origin.X + width, origin.Y + cardHeight);

        // Card shell, tinted by the item's rarity.
        dl.AddRectFilled(origin, max, Theme.U32(Theme.Surface, 0.85f), Theme.Radius);
        dl.AddRectFilledMultiColor(origin, max,
            Theme.U32(rarityColor, 0.12f), Theme.U32(rarityColor, 0.03f),
            Theme.U32(rarityColor, 0f), Theme.U32(rarityColor, 0.05f));
        dl.AddRect(origin, max, Theme.U32(rarityColor, decided ? 0.5f : 0.3f), Theme.Radius, ImDrawFlags.None, 1f);
        dl.AddRectFilled(new Vector2(origin.X, origin.Y + 6), new Vector2(origin.X + 2.5f, max.Y - 6),
            Theme.U32(rarityColor, 0.9f), 1.5f);

        // Item icon.
        var iconPos = new Vector2(origin.X + 12f, origin.Y + 9f);
        if (rollInfo.IconId > 0)
        {
            try
            {
                var tex = Plugin.TextureProvider
                    .GetFromGameIcon(new Dalamud.Interface.Textures.GameIconLookup(rollInfo.IconId))
                    .GetWrapOrDefault();
                if (tex != null)
                {
                    if (!decided)
                    {
                        // Undecided items breathe, so your eye goes to what's still in play.
                        var pulse = 0.5f + 0.5f * MathF.Sin(Theme.Time * 3f);
                        dl.AddRectFilled(iconPos - new Vector2(3, 3),
                            iconPos + new Vector2(IconSize + 3, IconSize + 3),
                            Theme.U32(rarityColor, 0.12f + pulse * 0.18f), 6f);
                    }

                    dl.AddImage(tex.Handle, iconPos, iconPos + new Vector2(IconSize, IconSize));
                    dl.AddRect(iconPos, iconPos + new Vector2(IconSize, IconSize),
                        Theme.U32(rarityColor, 0.7f), 4f, ImDrawFlags.None, 1f);
                }
            }
            catch { /* Ignore icon errors */ }
        }

        // Item name + state.
        ImGui.SetCursorScreenPos(new Vector2(origin.X + 12f + IconSize + 11f, origin.Y + 10f));
        ImGui.TextColored(rarityColor, rollInfo.ItemName);

        ImGui.SetCursorScreenPos(new Vector2(origin.X + 12f + IconSize + 11f, origin.Y + 10f + ImGui.GetTextLineHeight() + 3f));
        if (decided)
        {
            Theme.Badge($"Won by {rollInfo.WinnerName}", Theme.Gold);
        }
        else
        {
            Theme.Badge($"{sortedRolls.Count} rolled", Theme.Crystal);
        }

        // Roll rows.
        var rowY = origin.Y + 9f + IconSize + 8f;
        if (sortedRolls.Count == 0)
        {
            dl.AddText(new Vector2(origin.X + 18f, rowY), Theme.U32(Theme.TextFaint), "Waiting for rolls...");
        }
        else
        {
            foreach (var (playerName, rollType, rollValue, isWinner) in sortedRolls)
            {
                DrawRollRow(dl, new Vector2(origin.X, rowY), width, playerName, rollType, rollValue, isWinner, localPlayerName);
                rowY += 21f;
            }
        }

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(width, cardHeight));
        ImGui.Dummy(new Vector2(0, 6));
    }

    private static void DrawRollRow(ImDrawListPtr dl, Vector2 origin, float width, string playerName,
        string rollType, int rollValue, bool isWinner, string localPlayerName)
    {
        var textY = origin.Y + (21f - ImGui.GetTextLineHeight()) * 0.5f;
        var x = origin.X + 18f;

        if (isWinner)
        {
            // Winners get a soft brass band behind the whole row.
            dl.AddRectFilled(
                new Vector2(origin.X + 10f, origin.Y + 1f),
                new Vector2(origin.X + width - 10f, origin.Y + 20f),
                Theme.U32(Theme.Gold, 0.13f), 4f);

            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                var glyph = FontAwesomeIcon.Crown.ToIconString();
                dl.AddText(new Vector2(x, textY), Theme.U32(Theme.GoldBright), glyph);
                x += ImGui.CalcTextSize(glyph).X + 7f;
            }
        }
        else
        {
            x += 4f;
        }

        var isLocalPlayer = playerName.Equals(localPlayerName, StringComparison.OrdinalIgnoreCase) ||
                            playerName.StartsWith(localPlayerName, StringComparison.OrdinalIgnoreCase);
        var playerColor = isLocalPlayer ? Theme.Good : Theme.Text;
        dl.AddText(new Vector2(x, textY), Theme.U32(playerColor), playerName);

        // Roll type pill and value, right aligned.
        var valueText = rollValue.ToString();
        var vw = ImGui.CalcTextSize(valueText).X;
        var valueX = origin.X + width - 18f - vw;
        dl.AddText(new Vector2(valueX, textY), Theme.U32(isWinner ? Theme.GoldBright : Theme.Text), valueText);

        var rollColor = rollType == "Need" ? Theme.Good : Theme.Crystal;
        var ts = ImGui.CalcTextSize(rollType);
        var pillMax = new Vector2(valueX - 8f, origin.Y + 3.5f);
        var pillMin = new Vector2(pillMax.X - ts.X - 12f, pillMax.Y);
        var pillBottom = new Vector2(pillMax.X, origin.Y + 17.5f);

        dl.AddRectFilled(pillMin, pillBottom, Theme.U32(rollColor, 0.16f), 7f);
        dl.AddText(new Vector2(pillMin.X + 6f, textY), Theme.U32(rollColor), rollType);
    }

    public override void Dispose()
    {
        plugin.LootTracker.RollsUpdated -= OnRollsUpdated;
        base.Dispose();
    }
}
