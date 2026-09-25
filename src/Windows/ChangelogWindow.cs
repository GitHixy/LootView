using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using LootView.UI;

namespace LootView.Windows;

/// <summary>
/// The "what's new" panel shown once after the plugin updates, and on demand from
/// Settings → About.
/// </summary>
public class ChangelogWindow : Window
{
    private readonly Plugin plugin;
    private IReadOnlyList<Release> releases = Array.Empty<Release>();

    public ChangelogWindow(Plugin plugin) : base("What's New###LootViewChangelog")
    {
        this.plugin = plugin;

        IsOpen = false;
        Size = new Vector2(580, 540);
        SizeConstraintMin = new Vector2(480, 340);
        SizeConstraintMax = new Vector2(900, 1000);
        WindowFlags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        CenterOnAppearing = true;
    }

    /// <summary>Opens the notes published since the given version.</summary>
    public void ShowSince(string lastSeenVersion)
    {
        releases = Changelog.Since(lastSeenVersion);
        if (releases.Count == 0) return;

        IsOpen = true;
    }

    /// <summary>Opens the full history, for the button in Settings → About.</summary>
    public void ShowAll()
    {
        releases = Changelog.Releases;
        IsOpen = true;
    }

    protected override void DrawContents()
    {
        BgAlpha = Math.Max(plugin.Configuration.BackgroundAlpha, 0.92f);

        var current = Changelog.CurrentVersion;

        Theme.WindowHeader(FontAwesomeIcon.Gift, "What's new in LootView",
            releases.Count > 1
                ? $"You're now on {current} - here's everything since your last update"
                : $"Version {current}");

        var footerHeight = ImGui.GetFrameHeight() + 26f;
        using (var body = Theme.Region("##ChangelogBody",
                   new Vector2(0, ImGui.GetContentRegionAvail().Y - footerHeight)))
        {
            if (body)
            {
                if (releases.Count == 0)
                {
                    Theme.EmptyState(FontAwesomeIcon.CheckCircle, "You're up to date",
                        "Nothing new since your last visit.");
                }
                else
                {
                    for (var i = 0; i < releases.Count; i++)
                    {
                        DrawRelease(releases[i], i == 0);
                    }
                }
            }
        }

        DrawFooter(current);
    }

    private static void DrawRelease(Release release, bool isLatest)
    {
        var dl = ImGui.GetWindowDrawList();

        // Version plate: brass for the release you just moved to, muted for older ones.
        var accent = isLatest ? Theme.Gold : Theme.TextMuted;
        ImGui.AlignTextToFramePadding();
        Theme.Badge($"v{release.Version}", accent, isLatest);
        ImGui.SameLine(0, 10);
        ImGui.AlignTextToFramePadding();
        using (new Theme.FontScale(1.08f))
        {
            ImGui.TextColored(isLatest ? Theme.Text : Theme.TextMuted, release.Headline);
        }

        ImGui.Dummy(new Vector2(0, 6));

        // Tags share one column so the sentences all start on the same left edge.
        var tagWidth = 0f;
        foreach (var note in release.Notes)
            tagWidth = Math.Max(tagWidth, ImGui.CalcTextSize(Changelog.KindLabel(note.Kind)).X);
        tagWidth += 18f;

        foreach (var note in release.Notes)
        {
            var origin = ImGui.GetCursorScreenPos();
            var label = Changelog.KindLabel(note.Kind);
            var color = Changelog.KindColor(note.Kind);

            ImGui.SetCursorScreenPos(new Vector2(origin.X + tagWidth + 8f, origin.Y));
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            ImGui.TextColored(Theme.Text, note.Text);
            ImGui.PopTextWrapPos();

            // The tag is drawn after the text so it can be vertically centred on line one.
            var ts = ImGui.CalcTextSize(label);
            var tagMin = new Vector2(origin.X, origin.Y + 1f);
            var tagMax = new Vector2(origin.X + tagWidth, origin.Y + ts.Y + 5f);
            dl.AddRectFilled(tagMin, tagMax, Theme.U32(color, 0.16f), 3f);
            dl.AddRect(tagMin, tagMax, Theme.U32(color, 0.4f), 3f, ImDrawFlags.None, 1f);

            using (new Theme.FontScale(0.82f))
            {
                var ls = ImGui.CalcTextSize(label);
                dl.AddText(new Vector2(origin.X + (tagWidth - ls.X) * 0.5f, origin.Y + (ts.Y + 5f - ls.Y) * 0.5f + 1f),
                    Theme.U32(color), label);
            }

            ImGui.Dummy(new Vector2(0, 3));
        }

        ImGui.Dummy(new Vector2(0, 6));
        Theme.Rule(4f);
    }

    private void DrawFooter(string current)
    {
        ImGui.Dummy(new Vector2(0, 2));

        if (Theme.PrimaryButton("Got it", new Vector2(130, ImGui.GetFrameHeight() + 6)))
        {
            Dismiss(current);
        }

        ImGui.SameLine(0, 10);
        if (Theme.GhostButton("Patreon", new Vector2(110, ImGui.GetFrameHeight() + 6), Theme.Crystal))
        {
            OpenUrl("https://www.patreon.com/GitHixy");
        }

        ImGui.SameLine(0, 10);
        if (Theme.GhostButton("GitHub", new Vector2(110, ImGui.GetFrameHeight() + 6)))
        {
            OpenUrl("https://github.com/GitHixy/LootView");
        }
    }

    private void Dismiss(string current)
    {
        plugin.ConfigService.Configuration.LastSeenVersion = current;
        plugin.ConfigService.Save();
        IsOpen = false;
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

    /// <summary>
    /// Closing with the title-bar button counts as having read the notes, so an update
    /// never nags twice. Unloading without closing does not, so notes survive a logout.
    /// </summary>
    protected override void OnClosed() => Dismiss(Changelog.CurrentVersion);
}
