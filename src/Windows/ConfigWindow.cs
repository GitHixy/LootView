using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using LootView.Services;
using LootView.UI;

namespace LootView.Windows;

public class ConfigWindow : Window
{
    private readonly Plugin plugin;
    private readonly ConfigurationService configService;

    private int section;

    private const string RepoUrl = "https://github.com/GitHixy/LootView";
    private const int ReportLineLimit = 200;

    // Diagnostics view state. The filtered list is rebuilt only when the log or the filter changes.
    private int logLevelFilter;
    private string logSearch = string.Empty;
    private bool logAutoScroll = true;
    private readonly HashSet<LogEntry> selectedLogLines = [];
    private List<LogEntry> visibleLogLines = [];
    private (int Revision, int Level, string Search) logViewKey = (-1, -1, string.Empty);
    private int scrolledRevision = -1;

    private static readonly (FontAwesomeIcon Icon, string Label, string Blurb)[] Sections =
    [
        (FontAwesomeIcon.SlidersH, "General", "Window behaviour and what the tracker shows"),
        (FontAwesomeIcon.Crosshairs, "Tracking", "Which loot events LootView listens for"),
        (FontAwesomeIcon.PaintBrush, "Appearance", "How the overlay sits on your screen"),
        (FontAwesomeIcon.Magic, "Effects", "Drop flourishes and item tooltips"),
        (FontAwesomeIcon.Database, "History", "Long-term storage and statistics"),
        (FontAwesomeIcon.Bug, "Diagnostics", "Logs, bug reports and feature ideas"),
        (FontAwesomeIcon.InfoCircle, "About", "Version, links and support"),
    ];

    public ConfigWindow(Plugin plugin, ConfigurationService configService)
        : base("LootView Settings###LootViewConfig")
    {
        this.plugin = plugin;
        this.configService = configService;

        Size = new Vector2(660, 480);
        SizeConstraintMin = new Vector2(600, 420);
        SizeConstraintMax = new Vector2(1100, 900);
        WindowFlags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
    }

    protected override void DrawContents()
    {
        try
        {
            var config = configService.Configuration;
            BgAlpha = Math.Max(config.BackgroundAlpha, 0.85f);

            Theme.WindowHeader(FontAwesomeIcon.Cog, "Settings", Sections[section].Blurb);

            var avail = ImGui.GetContentRegionAvail();
            const float railWidth = 158f;

            DrawNavRail(new Vector2(railWidth, avail.Y));

            ImGui.SameLine(0, 12);

            using var pane = Theme.Region("##ConfigPane", new Vector2(0, avail.Y));
            if (!pane) return;

            switch (section)
            {
                case 0: DrawGeneral(); break;
                case 1: DrawTracking(); break;
                case 2: DrawAppearance(); break;
                case 3: DrawEffects(); break;
                case 4: DrawHistory(); break;
                case 5: DrawDiagnostics(); break;
                case 6: DrawAbout(); break;
            }

            ImGui.Dummy(new Vector2(0, 8));
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error drawing config window");
            ImGui.TextColored(Theme.Bad, "Error drawing settings.");
        }
    }

    private void DrawNavRail(Vector2 size)
    {
        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();

        dl.AddRectFilled(origin, origin + size, Theme.U32(Theme.Panel, 0.55f), Theme.Radius);
        dl.AddRect(origin, origin + size, Theme.U32(Theme.Line, 0.8f), Theme.Radius, ImDrawFlags.None, 1f);

        using var child = Theme.Region("##ConfigNav", size);
        if (!child) return;

        ImGui.Dummy(new Vector2(0, 4));

        for (var i = 0; i < Sections.Length; i++)
        {
            var (icon, label, _) = Sections[i];
            var selected = section == i;

            var p = ImGui.GetCursorScreenPos();
            var w = ImGui.GetContentRegionAvail().X - 8;
            const float h = 34f;

            ImGui.SetCursorScreenPos(new Vector2(p.X + 4, p.Y));
            if (ImGui.InvisibleButton($"##nav{i}", new Vector2(w, h)))
                section = i;

            var hovered = ImGui.IsItemHovered();
            var min = new Vector2(p.X + 4, p.Y);
            var max = new Vector2(p.X + 4 + w, p.Y + h);

            if (selected)
            {
                dl.AddRectFilled(min, max, Theme.U32(Theme.Gold, 0.13f), Theme.Radius);
                dl.AddRectFilled(min, new Vector2(min.X + 2.5f, max.Y), Theme.U32(Theme.Gold, 0.95f), 1.5f);
            }
            else if (hovered)
            {
                dl.AddRectFilled(min, max, Theme.U32(Theme.Crystal, 0.12f), Theme.Radius);
            }

            var tint = selected ? Theme.GoldBright : hovered ? Theme.Text : Theme.TextMuted;

            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                var glyph = icon.ToIconString();
                var gs = ImGui.CalcTextSize(glyph);
                dl.AddText(new Vector2(min.X + 15 - gs.X * 0.5f, min.Y + (h - gs.Y) * 0.5f), Theme.U32(tint), glyph);
            }

            var ts = ImGui.CalcTextSize(label);
            dl.AddText(new Vector2(min.X + 30, min.Y + (h - ts.Y) * 0.5f), Theme.U32(tint), label);

            ImGui.Dummy(new Vector2(0, 2));
        }
    }

    // ------------------------------------------------------------------
    // Sections
    // ------------------------------------------------------------------

    private void DrawGeneral()
    {
        var config = configService.Configuration;

        Theme.SectionHeader("Startup", FontAwesomeIcon.PowerOff);

        var openOnLogin = config.OpenOnLogin;
        if (Theme.ToggleRow("Open on login", ref openOnLogin,
                "Open the loot tracker automatically when you log into the game."))
        {
            config.OpenOnLogin = openOnLogin;
            configService.Save();
        }

        var showOnDutyStart = config.ShowOnDutyStart;
        if (Theme.ToggleRow("Open when a duty starts", ref showOnDutyStart,
                "Open the loot tracker automatically when you enter a duty."))
        {
            config.ShowOnDutyStart = showOnDutyStart;
            configService.Save();
        }

        ImGui.Dummy(new Vector2(0, 8));
        Theme.SectionHeader("Contents", FontAwesomeIcon.ListUl);

        var showOnlyMyLoot = config.ShowOnlyOwnLoot;
        if (Theme.ToggleRow("Show only my loot", ref showOnlyMyLoot,
                "Hide everything your party members pick up."))
        {
            config.ShowOnlyOwnLoot = showOnlyMyLoot;
            config.ShowOnlyMyLoot = showOnlyMyLoot;
            configService.Save();
        }

        ImGui.Dummy(new Vector2(0, 4));
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Text, "Maximum items shown");
        ImGui.SameLine(0, 6);
        Theme.HelpMarker("How many recent drops the list keeps before the oldest fall off.");
        ImGui.SameLine(0, 14);

        var maxItems = config.MaxDisplayedItems;
        if (Theme.SliderInt("##MaxItems", ref maxItems, 10, 200, 210f))
        {
            config.MaxDisplayedItems = maxItems;
            configService.Save();
        }

        ImGui.Dummy(new Vector2(0, 8));
        Theme.SectionHeader("Integration", FontAwesomeIcon.PlugCircleBolt);

        var showDtrBar = config.ShowDtrBar;
        if (Theme.ToggleRow("Server info bar button", ref showDtrBar,
                "Adds a LootView entry next to the server name. Click it to toggle the overlay."))
        {
            config.ShowDtrBar = showDtrBar;
            configService.Save();
            plugin.UpdateDtrBarVisibility(showDtrBar);
        }

        ImGui.Dummy(new Vector2(0, 8));
        Theme.SectionHeader("Market prices", FontAwesomeIcon.Coins);

        var enablePrices = config.EnableMarketPrices;
        if (Theme.ToggleRow("Estimate what your loot is worth", ref enablePrices,
                "Shows a running market value above the list, priced for your home world. Only sellable items are looked up."))
        {
            config.EnableMarketPrices = enablePrices;
            configService.Save();
        }

        if (config.EnableMarketPrices)
        {
            ImGui.Dummy(new Vector2(0, 6));
            var world = plugin.MarketPriceService.WorldName;
            Theme.Callout(FontAwesomeIcon.CloudDownloadAlt, "Powered by Universalis",
                (world is null
                    ? "Prices come from universalis.app, for the world your character is from. "
                    : $"Prices come from universalis.app, for {world}. ") +
                "They are crowd-sourced, cached for 30 minutes, and exclude the 5% market board tax.",
                Theme.Crystal);

            ImGui.Dummy(new Vector2(0, 8));
            if (Theme.GhostButton("Refresh prices", new Vector2(150, 30)))
            {
                plugin.MarketPriceService.ClearCache();
            }
        }

        ImGui.Dummy(new Vector2(0, 10));
        Theme.Callout(FontAwesomeIcon.Terminal, "Chat commands",
            "/lv toggles the overlay  ·  /lv config opens this window", Theme.Crystal);
    }

    private void DrawTracking()
    {
        var config = configService.Configuration;

        Theme.SectionHeader("Sources", FontAwesomeIcon.Crosshairs);

        var trackPartyLoot = config.TrackAllPartyLoot;
        if (Theme.ToggleRow("Track party loot", ref trackPartyLoot,
                "Record items your party members obtain, not just your own."))
        {
            config.TrackAllPartyLoot = trackPartyLoot;
            configService.Save();
        }

        ImGui.Dummy(new Vector2(0, 8));
        Theme.SectionHeader("Need / Greed", FontAwesomeIcon.Dice);

        var enableRollTracking = config.EnableRollTracking;
        if (Theme.ToggleRow("Show the roll window", ref enableRollTracking,
                "Displays a live panel with every Need/Greed roll and the winner. Turn this off if you don't want the popup during loot rolls."))
        {
            config.EnableRollTracking = enableRollTracking;
            configService.Save();

            if (!enableRollTracking)
            {
                plugin.LootTracker.ClearAllRolls();
            }
        }

        if (config.EnableRollTracking)
        {
            ImGui.Dummy(new Vector2(0, 4));
            ImGui.Indent(10);

            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Text, "Keep results for");
            ImGui.SameLine(0, 6);
            Theme.HelpMarker("How many seconds an awarded item stays in the roll window before it disappears.");
            ImGui.SameLine(0, 14);

            var resultSeconds = config.RollResultSeconds;
            if (Theme.SliderInt("##RollResultSeconds", ref resultSeconds, 5, 120, 210f))
            {
                config.RollResultSeconds = resultSeconds;
                configService.Save();
            }

            ImGui.SameLine(0, 8);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.TextMuted, "seconds");

            ImGui.Unindent(10);
        }

        ImGui.Dummy(new Vector2(0, 10));
        Theme.Callout(FontAwesomeIcon.Ban, "Blacklist",
            "Right-click any item in the loot list to stop tracking it. Manage the full list from Statistics → Blacklist.",
            Theme.Warn);

        ImGui.Dummy(new Vector2(0, 10));
        if (Theme.GhostButton("Open blacklist", new Vector2(160, 30)))
        {
            plugin.StatisticsWindow.IsOpen = true;
        }
    }

    private void DrawAppearance()
    {
        var config = configService.Configuration;

        Theme.SectionHeader("Window", FontAwesomeIcon.WindowMaximize);

        var lockPos = config.LockWindowPosition;
        if (Theme.ToggleRow("Lock position and size", ref lockPos,
                "Pins the overlay in place so it can't be dragged or resized by accident."))
        {
            config.LockWindowPosition = lockPos;
            config.LockWindowSize = lockPos;
            configService.Save();
        }

        ImGui.Dummy(new Vector2(0, 8));
        Theme.SectionHeader("Transparency", FontAwesomeIcon.Adjust);

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Text, "Background opacity");
        ImGui.SameLine(0, 14);

        var bgAlpha = config.BackgroundAlpha;
        if (Theme.Slider("##BgAlpha", ref bgAlpha, 0.0f, 1.0f, "%.2f", 210f))
        {
            config.BackgroundAlpha = bgAlpha;
            configService.Save();
        }

        ImGui.Dummy(new Vector2(0, 4));
        ImGui.TextColored(Theme.TextFaint, "Preview");
        ImGui.Dummy(new Vector2(0, 2));
        DrawOpacityPreview(bgAlpha);

        ImGui.Dummy(new Vector2(0, 10));
        Theme.Callout(FontAwesomeIcon.Palette, "Rarity colours",
            "Item names use the game's own rarity ramp: white, green, blue, purple and pink.",
            Theme.Crystal);

        ImGui.Dummy(new Vector2(0, 8));
        DrawRarityLegend();
    }

    private static void DrawOpacityPreview(float alpha)
    {
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        var w = Math.Min(ImGui.GetContentRegionAvail().X, 300f);
        const float h = 46f;
        var max = new Vector2(p.X + w, p.Y + h);

        // Checkerboard so the alpha is actually legible.
        const float cell = 8f;
        for (var y = 0f; y < h; y += cell)
        {
            for (var x = 0f; x < w; x += cell)
            {
                var odd = ((int)(x / cell) + (int)(y / cell)) % 2 == 1;
                dl.AddRectFilled(
                    new Vector2(p.X + x, p.Y + y),
                    new Vector2(Math.Min(p.X + x + cell, max.X), Math.Min(p.Y + y + cell, max.Y)),
                    Theme.U32(odd ? Theme.Line : Theme.Panel, 0.9f));
            }
        }

        dl.AddRectFilled(p, max, Theme.U32(Theme.Ink, alpha), Theme.Radius);
        dl.AddRect(p, max, Theme.U32(Theme.Line), Theme.Radius, ImDrawFlags.None, 1f);
        dl.AddText(new Vector2(p.X + 12, p.Y + h * 0.5f - ImGui.GetTextLineHeight() * 0.5f),
            Theme.U32(Theme.Text), "Sample loot row");

        ImGui.Dummy(new Vector2(w, h));
    }

    private static void DrawRarityLegend()
    {
        uint[] rarities = [1, 2, 3, 4, 7];
        foreach (var r in rarities)
        {
            Theme.RarityGem(r, 10f);
            ImGui.SameLine(0, 5);
            ImGui.TextColored(Theme.RarityColor(r), Theme.RarityName(r));
            ImGui.SameLine(0, 14);
        }
        ImGui.NewLine();
    }

    private void DrawEffects()
    {
        var config = configService.Configuration;

        Theme.SectionHeader("Tooltips", FontAwesomeIcon.CommentDots);

        var showTooltips = config.ShowTooltips;
        if (Theme.ToggleRow("Show item tooltips", ref showTooltips,
                "Reveals rarity, source, zone and roll details when you hover a row."))
        {
            config.ShowTooltips = showTooltips;
            configService.Save();
        }

        ImGui.Dummy(new Vector2(0, 8));
        Theme.SectionHeader("Drop flourish", FontAwesomeIcon.Magic);

        var enableParticles = config.EnableParticleEffects;
        if (Theme.ToggleRow("Particle effects", ref enableParticles,
                "Bursts of light when an item drops, coloured by its rarity."))
        {
            config.EnableParticleEffects = enableParticles;
            configService.Save();
        }

        if (config.EnableParticleEffects)
        {
            ImGui.Dummy(new Vector2(0, 6));
            ImGui.Indent(10);

            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Text, "Intensity");
            ImGui.SameLine(0, 6);
            Theme.HelpMarker("Scales how many particles each drop spawns. 0 is minimal, 2 is maximum.");
            ImGui.SameLine(0, 14);

            var particleIntensity = config.ParticleIntensity;
            if (Theme.Slider("##ParticleIntensity", ref particleIntensity, 0.0f, 2.0f, "%.1f", 210f))
            {
                config.ParticleIntensity = particleIntensity;
                configService.Save();
            }

            ImGui.Dummy(new Vector2(0, 4));
            Theme.Meter(config.ParticleIntensity / 2f, 210f, 6f,
                config.ParticleIntensity > 1.4f ? Theme.Warn : Theme.Gold);

            ImGui.Unindent(10);
        }

        ImGui.Dummy(new Vector2(0, 10));
        Theme.Callout(FontAwesomeIcon.Feather, "Performance",
            "Effects are drawn entirely in the overlay and never touch the game's renderer. Lower the intensity if your frame time is tight.",
            Theme.Crystal);
    }

    private void DrawHistory()
    {
        var config = configService.Configuration;
        var history = plugin.HistoryService.GetHistory();

        Theme.SectionHeader("Storage", FontAwesomeIcon.Database);

        var enableHistory = config.EnableHistoryTracking;
        if (Theme.ToggleRow("Keep a permanent history", ref enableHistory,
                "Saves every tracked item to disk so statistics survive restarts."))
        {
            config.EnableHistoryTracking = enableHistory;
            configService.Save();
        }

        if (config.EnableHistoryTracking)
        {
            ImGui.Indent(10);

            var autoSave = config.EnableHistoryAutoSave;
            if (Theme.ToggleRow("Auto-save every 5 minutes", ref autoSave,
                    "Writes history to disk periodically instead of only on logout."))
            {
                config.EnableHistoryAutoSave = autoSave;
                configService.Save();
            }

            var saveOnClear = config.SaveToHistoryOnClear;
            if (Theme.ToggleRow("Save to history when clearing", ref saveOnClear,
                    "Pushes the current list into history before the Clear button wipes it."))
            {
                config.SaveToHistoryOnClear = saveOnClear;
                configService.Save();
            }

            ImGui.Unindent(10);
        }

        ImGui.Dummy(new Vector2(0, 10));
        Theme.SectionHeader("Collection", FontAwesomeIcon.ChartPie);

        var cardWidth = Math.Min((ImGui.GetContentRegionAvail().X - 10) / 2f, 220f);
        Theme.StatCard(FontAwesomeIcon.Gem, "Items tracked", history.TotalItemsObtained.ToString("N0"), Theme.Gold, cardWidth);
        ImGui.SameLine(0, 10);
        Theme.StatCard(FontAwesomeIcon.CalendarAlt, "Days recorded", history.DailyStatistics.Count.ToString("N0"), Theme.Crystal, cardWidth);

        ImGui.Dummy(new Vector2(0, 12));
        if (Theme.PrimaryButton("Open statistics & history", new Vector2(220, 32)))
        {
            plugin.StatisticsWindow.IsOpen = true;
        }

        ImGui.Dummy(new Vector2(0, 10));
        Theme.Callout(FontAwesomeIcon.ShieldAlt, "Your data stays local",
            "History lives in your Dalamud config folder and is never uploaded. Export it from Statistics → Export.",
            Theme.Good);
    }

    private void DrawDiagnostics()
    {
        Theme.SectionHeader("Feedback", FontAwesomeIcon.CommentDots);

        Theme.Callout(FontAwesomeIcon.Bug, "Something not working?",
            "1. Make the problem happen again, then come back to this page.\n" +
            "2. Press Copy report. It copies your LootView version, a few settings and the log below. " +
            "Click individual lines first if you only want to share those.\n" +
            "3. Press Report a bug, tell me what you did, what you expected and what happened instead, " +
            "then paste the report into the issue.",
            Theme.Warn);

        ImGui.Dummy(new Vector2(0, 6));
        Theme.Callout(FontAwesomeIcon.Lightbulb, "Have an idea?",
            "Issues are also the place for feature requests. Press Suggest a feature and describe what you'd " +
            "like LootView to do and how it would help you. No log needed.",
            Theme.Crystal);

        ImGui.Dummy(new Vector2(0, 8));
        if (Theme.PrimaryButton("Report a bug", new Vector2(140, 32)))
            OpenUrl(NewIssueUrl("bug", "[Bug] ",
                "**What happened?**\n\n\n**What did you expect?**\n\n\n**Steps to reproduce**\n1. \n\n" +
                "**Diagnostics report**\n<!-- Settings → Diagnostics → Copy report, then paste here -->\n"));

        ImGui.SameLine(0, 8);
        if (Theme.GhostButton("Suggest a feature", new Vector2(150, 32), Theme.Crystal))
            OpenUrl(NewIssueUrl("enhancement", "[Feature] ",
                "**What would you like LootView to do?**\n\n\n**How would it help you?**\n\n"));

        ImGui.SameLine(0, 8);
        if (Theme.GhostButton("Open issues", new Vector2(120, 32)))
            OpenUrl($"{RepoUrl}/issues");

        ImGui.Dummy(new Vector2(0, 12));
        Theme.SectionHeader("Log", FontAwesomeIcon.Terminal);

        RefreshLogView();

        if (Theme.SegmentedControl("##LogLevel", ref logLevelFilter, "All", "Info", "Warnings", "Errors"))
            RefreshLogView();

        ImGui.SameLine(0, 10);
        ImGui.SetNextItemWidth(Math.Max(ImGui.GetContentRegionAvail().X, 120f));
        if (ImGui.InputTextWithHint("##LogSearch", "Filter log lines", ref logSearch, 100))
            RefreshLogView();

        ImGui.Dummy(new Vector2(0, 4));
        var copyLabel = selectedLogLines.Count > 0 ? $"Copy report ({selectedLogLines.Count})" : "Copy report";
        if (Theme.PrimaryButton(copyLabel, new Vector2(150, 30)))
        {
            ImGui.SetClipboardText(BuildReport());
        }
        if (ImGui.IsItemHovered())
            Theme.Tooltip(selectedLogLines.Count > 0
                ? "Copies your setup and the selected lines, ready to paste into an issue."
                : $"Copies your setup and the last {ReportLineLimit} lines shown, ready to paste into an issue.");

        ImGui.SameLine(0, 8);
        if (Theme.GhostButton("Copy lines only", new Vector2(130, 30)))
        {
            ImGui.SetClipboardText(string.Join("\n", ReportLines().Select(e => e.Format())));
        }

        if (selectedLogLines.Count > 0)
        {
            ImGui.SameLine(0, 8);
            if (Theme.GhostButton("Deselect", new Vector2(90, 30)))
                selectedLogLines.Clear();
        }

        ImGui.SameLine(0, 8);
        if (Theme.DangerButton("Clear", new Vector2(70, 30)))
        {
            Plugin.Log.Clear();
            selectedLogLines.Clear();
            RefreshLogView();
        }

        ImGui.SameLine(0, 12);
        ImGui.AlignTextToFramePadding();
        ImGui.Checkbox("Auto-scroll", ref logAutoScroll);

        ImGui.Dummy(new Vector2(0, 4));
        DrawLogLines();

        ImGui.Dummy(new Vector2(0, 6));
        Theme.Callout(FontAwesomeIcon.UserSecret, "Check before you share",
            "The log can contain character names from your party. Feel free to remove anything you'd rather keep private " +
            "before posting. It only lives in memory and is cleared when the game closes.",
            Theme.TextMuted);
    }

    private void DrawLogLines()
    {
        var height = Math.Max(ImGui.GetContentRegionAvail().Y - 70f, 240f);
        using var card = Theme.Card("##LogCard", new Vector2(0, height));
        if (!card) return;

        using var rows = Theme.Region("##LogRows", Vector2.Zero, ImGuiWindowFlags.HorizontalScrollbar);
        if (!rows) return;

        if (visibleLogLines.Count == 0)
        {
            ImGui.TextColored(Theme.TextFaint, logViewKey.Level == 0 && logSearch.Length == 0
                ? "Nothing logged yet."
                : "No lines match the current filter.");
            return;
        }

        for (var i = 0; i < visibleLogLines.Count; i++)
        {
            var entry = visibleLogLines[i];
            var selected = selectedLogLines.Contains(entry);

            using (ImRaii.PushColor(ImGuiCol.Text, LevelColor(entry.Level)))
            {
                if (ImGui.Selectable($"{entry.Format()}##log{i}", selected))
                {
                    if (!selectedLogLines.Remove(entry))
                        selectedLogLines.Add(entry);
                }
            }

            using var popup = ImRaii.ContextPopupItem($"##logctx{i}");
            if (popup.Success && ImGui.Selectable("Copy this line"))
                ImGui.SetClipboardText(entry.Format());
        }

        if (logAutoScroll && scrolledRevision != Plugin.Log.Revision)
        {
            ImGui.SetScrollHereY(1f);
            scrolledRevision = Plugin.Log.Revision;
        }
    }

    private void RefreshLogView()
    {
        var key = (Plugin.Log.Revision, logLevelFilter, logSearch);
        if (key == logViewKey) return;
        logViewKey = key;

        var minLevel = (LogLevel)logLevelFilter;
        visibleLogLines = Plugin.Log.Snapshot()
            .Where(e => e.Level >= minLevel)
            .Where(e => logSearch.Length == 0
                        || e.Message.Contains(logSearch, StringComparison.OrdinalIgnoreCase)
                        || (e.Exception?.Contains(logSearch, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        // Lines that scrolled out of the buffer can't be copied any more.
        var all = Plugin.Log.Snapshot().ToHashSet();
        selectedLogLines.RemoveWhere(e => !all.Contains(e));
    }

    private IEnumerable<LogEntry> ReportLines()
    {
        return selectedLogLines.Count > 0
            ? selectedLogLines.OrderBy(e => e.Time)
            : visibleLogLines.Skip(Math.Max(0, visibleLogLines.Count - ReportLineLimit));
    }

    private string BuildReport()
    {
        var config = configService.Configuration;
        var sb = new StringBuilder();

        sb.AppendLine("### LootView diagnostics");
        sb.AppendLine($"- LootView: {Changelog.CurrentVersion}");
        sb.AppendLine($"- Dalamud: {typeof(Dalamud.Plugin.IDalamudPluginInterface).Assembly.GetName().Version}");
        sb.AppendLine($"- Game language: {Plugin.ClientState.ClientLanguage}");
        sb.AppendLine($"- Settings: party loot {OnOff(config.TrackAllPartyLoot)}, only my loot {OnOff(config.ShowOnlyOwnLoot)}, " +
                      $"roll window {OnOff(config.EnableRollTracking)}, history {OnOff(config.EnableHistoryTracking)}, " +
                      $"market prices {OnOff(config.EnableMarketPrices)}");
        sb.AppendLine($"- Generated: {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine("```text");
        foreach (var entry in ReportLines())
            sb.AppendLine(entry.Format());
        sb.AppendLine("```");

        return sb.ToString();

        static string OnOff(bool value) => value ? "on" : "off";
    }

    private static Vector4 LevelColor(LogLevel level) => level switch
    {
        LogLevel.Error => Theme.Bad,
        LogLevel.Warning => Theme.Warn,
        LogLevel.Info => Theme.Text,
        _ => Theme.TextMuted,
    };

    private static string NewIssueUrl(string label, string title, string body)
        => $"{RepoUrl}/issues/new?labels={label}&title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}";

    private void DrawAbout()
    {
        Theme.SectionHeader("LootView", FontAwesomeIcon.Gem);

        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextMuted, $"Version {Changelog.CurrentVersion}  ·  by GitHixy");
        ImGui.SameLine(0, 12);
        if (Theme.GhostButton("What's new", new Vector2(120, 0), Theme.Gold))
        {
            plugin.ChangelogWindow.ShowAll();
        }

        ImGui.Dummy(new Vector2(0, 6));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Math.Min(ImGui.GetContentRegionAvail().X, 420f));
        ImGui.TextColored(Theme.Text,
            "A live loot tracker for Final Fantasy XIV: every drop you and your party earn, " +
            "with duty statistics, roll results and searchable history.");
        ImGui.PopTextWrapPos();

        ImGui.Dummy(new Vector2(0, 14));
        Theme.SectionHeader("Support", FontAwesomeIcon.Heart);

        using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(1.0f, 0.26f, 0.30f, 0.85f))
                   .Push(ImGuiCol.ButtonHovered, new Vector4(1.0f, 0.36f, 0.40f, 1.0f))
                   .Push(ImGuiCol.ButtonActive, new Vector4(0.85f, 0.20f, 0.24f, 1.0f))
                   .Push(ImGuiCol.Text, Theme.Text))
        {
            if (ImGui.Button("Support me on Patreon", new Vector2(230, 34)))
                OpenUrl("https://www.patreon.com/GitHixy");
        }

        ImGui.SameLine(0, 10);
        if (Theme.GhostButton("GitHub", new Vector2(110, 34)))
            OpenUrl(RepoUrl);

        ImGui.SameLine(0, 10);
        if (Theme.GhostButton("Report a problem", new Vector2(150, 34)))
            section = Array.FindIndex(Sections, s => s.Label == "Diagnostics");

        ImGui.Dummy(new Vector2(0, 14));
        Theme.SectionHeader("Shortcuts", FontAwesomeIcon.Keyboard);

        ShortcutRow("/lv", "Toggle the loot overlay");
        ShortcutRow("/lv config", "Open these settings");
        ShortcutRow("Right-click a row", "Blacklist or copy an item");
    }

    private static void ShortcutRow(string command, string description)
    {
        var dl = ImGui.GetWindowDrawList();
        var p = ImGui.GetCursorScreenPos();
        var ts = ImGui.CalcTextSize(command);

        dl.AddRectFilled(p, new Vector2(p.X + ts.X + 14, p.Y + ts.Y + 5), Theme.U32(Theme.Panel, 0.95f), 4f);
        dl.AddRect(p, new Vector2(p.X + ts.X + 14, p.Y + ts.Y + 5), Theme.U32(Theme.Line), 4f, ImDrawFlags.None, 1f);
        dl.AddText(new Vector2(p.X + 7, p.Y + 2.5f), Theme.U32(Theme.GoldBright), command);

        ImGui.Dummy(new Vector2(Math.Max(ts.X + 14, 130f), ts.Y + 5));
        ImGui.SameLine(0, 12);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextMuted, description);
        ImGui.Dummy(new Vector2(0, 2));
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to open {Url}", url);
        }
    }
}
