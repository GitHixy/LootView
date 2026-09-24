using System;
using System.Collections.Generic;
using System.Linq;

namespace LootView.UI;

/// <summary>What a single release note is about, which drives its colour and tag.</summary>
public enum NoteKind
{
    Added,
    Changed,
    Improved,
    Fixed,
    Removed,
}

/// <summary>One line in a release's notes.</summary>
public readonly record struct Note(NoteKind Kind, string Text);

/// <summary>One published version and everything that went into it.</summary>
public readonly record struct Release(string Version, string Headline, Note[] Notes);

/// <summary>
/// The release notes shown after an update. Newest first - add a new entry at the top
/// whenever the assembly version in LootView.csproj changes.
/// </summary>
public static class Changelog
{
    public static readonly Release[] Releases =
    [
        new("1.5.0", "Roll timers, passes and feedback",
        [
            new(NoteKind.Added, "Every item in the roll window shows how long is left to roll on it, with a bar that "
                                + "turns amber and then red as time runs out."),
            new(NoteKind.Added, "Passes now appear in the results, along with items you aren't allowed to roll on and "
                                + "party members who never chose."),
            new(NoteKind.Added, "While an item is open, the roll window lists who in your party still has to decide."),
            new(NoteKind.Improved, "Every loot session now reaches an end: items everyone passed on, items that timed out "
                                   + "and rolls you left the duty on all close properly."),
            new(NoteKind.Added, "When an item is awarded, its card lights up: a sweep of light, sparks from the "
                                + "winner's crown and rings from the item. Follows your particle effect settings."),
            new(NoteKind.Changed, "Each resolved item now leaves the roll window on its own after 20 seconds, with a "
                                  + "countdown on the card. Change how long in Settings → Tracking."),
            new(NoteKind.Added, "A Diagnostics page in Settings with LootView's live log, filterable by level and text, "
                                + "including the debug lines Dalamud normally hides."),
            new(NoteKind.Added, "Copy report puts your version, a few settings and the recent log (or just the lines you "
                                + "select) on the clipboard, ready to paste into a GitHub issue."),
            new(NoteKind.Added, "Report a bug and Suggest a feature buttons open a pre-filled GitHub issue. "
                                + "Feature ideas are very welcome there too."),
        ]),

        new("1.4.0", "A complete visual overhaul",
        [
            new(NoteKind.Changed, "Every window has been redesigned around a single Eorzean theme: dark glass panels, brass rules and crystal-blue highlights."),
            new(NoteKind.Removed, "The Compact and Neon layouts are gone. Classic is now the only view, and it received all of the design work."),
            new(NoteKind.Changed, "Settings moved from stacked collapsing headers to a sectioned window with a navigation rail and animated toggles."),
            new(NoteKind.Improved, "Statistics charts are now properly drawn, with hover tooltips, in place of the old text bars."),
            new(NoteKind.Improved, "The loot list highlights new drops with a brass sweep, marks rarity on every row and sizes its columns to the content."),
            new(NoteKind.Changed, "The roll window is now a set of rarity-tinted cards with a countdown ring and a crown on the winner."),
            new(NoteKind.Added, "Estimated market value of everything you loot, priced for your home world "
                                + "through Universalis. The running total is independent of how many items the list "
                                + "keeps, and has its own reset button."),
            new(NoteKind.Added, "Release notes - this window - appear once after each update."),
            new(NoteKind.Changed, "The overlay now waits until your character has finished logging in before it appears."),
            new(NoteKind.Fixed, "A drawing error inside a window could leave ImGui's window stack unbalanced."),
        ]),

        new("1.3.0", "Better tracking and a blacklist",
        [
            new(NoteKind.Added, "Item blacklist, managed from the right-click menu on any loot row."),
            new(NoteKind.Added, "Open the tracker automatically when a duty starts."),
            new(NoteKind.Improved, "Crafting recognises 'loop of' items and 'is added to your inventory' messages."),
            new(NoteKind.Fixed, "Item names beginning with 'The' are matched correctly."),
        ]),

        new("1.2.6", "Crafting and gathering fixes",
        [
            new(NoteKind.Improved, "Synthesis tracking understands coil, plank, length, stack and bolt measure words."),
            new(NoteKind.Added, "Support for 'sack of' items such as nuts."),
        ]),
    ];

    /// <summary>The running assembly's version, formatted the way the notes list them.</summary>
    public static string CurrentVersion
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>The newest release, or null when the list is empty.</summary>
    public static Release? Latest => Releases.Length > 0 ? Releases[0] : null;

    /// <summary>
    /// Everything published since <paramref name="sinceVersion"/>. An unknown or missing
    /// version yields just the newest release, so an upgrade never dumps the whole history.
    /// </summary>
    public static IReadOnlyList<Release> Since(string sinceVersion)
    {
        if (!Version.TryParse(sinceVersion, out var seen))
            return Latest.HasValue ? [Latest.Value] : [];

        return Releases
            .Where(r => Version.TryParse(r.Version, out var v) && v > seen)
            .ToList();
    }

    /// <summary>True when the user has not yet dismissed the notes for the running version.</summary>
    public static bool HasUnseenNotes(string lastSeenVersion)
        => !string.Equals(lastSeenVersion, CurrentVersion, StringComparison.Ordinal)
           && Since(lastSeenVersion).Count > 0;

    public static string KindLabel(NoteKind kind) => kind switch
    {
        NoteKind.Added => "NEW",
        NoteKind.Changed => "CHANGED",
        NoteKind.Improved => "IMPROVED",
        NoteKind.Fixed => "FIXED",
        NoteKind.Removed => "REMOVED",
        _ => "NOTE",
    };

    public static System.Numerics.Vector4 KindColor(NoteKind kind) => kind switch
    {
        NoteKind.Added => Theme.Good,
        NoteKind.Changed => Theme.Crystal,
        NoteKind.Improved => Theme.Gold,
        NoteKind.Fixed => Theme.Warn,
        NoteKind.Removed => Theme.Bad,
        _ => Theme.TextMuted,
    };
}
