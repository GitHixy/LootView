# LootView

**See every item you and your party pick up, as it drops.**

A loot tracker for Final Fantasy XIV, built on [Dalamud](https://github.com/goatcorp/Dalamud). It shows drops live as they happen, keeps a permanent searchable history, and turns that history into statistics about where your loot actually comes from.

[![Patreon](https://img.shields.io/badge/Patreon-Support%20me-FF424D?logo=patreon&logoColor=white)](https://www.patreon.com/GitHixy)

[![License](https://img.shields.io/badge/license-GPL--3.0-blue)](LICENSE)
![Dalamud API](https://img.shields.io/badge/Dalamud%20API-15-6FB8E0)
![Version](https://img.shields.io/badge/version-1.5.1-D6B068)

---

## What it does

### Live loot overlay

Every drop appears the moment you get it, with its icon, rarity, quantity, who received it and how long ago. New items are highlighted with a brass sweep so you can catch them at a glance, and rarity is marked on each row.

The parser handles the awkward parts of FFXIV's chat messages:

- Measure words such as "3 rolls of", "a bunch of" or "2 baskets of" are read as quantities, while genuine item names like *Sack of Nuts* and *Basket of Flowers* are left intact.
- Multi-catch fishing ("You land 2 mossy globules…") records the full count.
- Crafting, gathering, retainers, chests, duty rewards and roulette bonus gil are all tracked.
- Duplicate chat messages for the same drop are collapsed rather than double-counted.

### Estimated value

A strip above the list keeps a running market value of everything you've picked up, priced for your home world through [Universalis](https://universalis.app/). Two figures, because they answer different questions: **Avg** is what those items have actually been selling for, **Now** is what the cheapest listings are going for today.

Only sellable items are looked up — whether an item can reach the market board is answered from the game's own data, so nothing untradable ever costs a request. Prices are cached for 30 minutes. Figures exclude the 5% market board tax, and anything Universalis has no data for is counted separately rather than silently as zero.

Turn it off in **Settings → General → Market prices** if you'd rather the plugin made no network requests while you play.

### Need / Greed rolls

A panel appears while rolls are open, with a card for every item on the loot list:

- **Time left** to roll, as a countdown and a bar that turns amber and then red as it runs out.
- **Every result**: Need and Greed rolls with their values, passes, items you aren't allowed to roll on, and party members who never chose. Your own choice shows the moment you make it; everyone else's is revealed by the game once the item is resolved.
- **Who is still deciding** in your party while the item is open.
- **The winner**, crowned, with a burst of light and sparks on the card as the result comes in.

Each resolved item leaves the panel on its own after 20 seconds (adjustable in **Settings → Tracking**), and the panel closes when none are left. If you leave the duty before rolls finish, the open items wind down the same way. You can turn the panel off entirely in the settings.

### Statistics and history

- **Overview** — totals, rarity split, play streaks and your most common items, over today, this week, this month or all time.
- **History** — every item ever tracked, filtered by name, rarity, zone, high quality or owner, and paged.
- **Trends** — daily and hourly activity charts, plus your rarest finds.
- **Analytics** — the last *N* days compared against the period before them, across items, rarity and zones.
- **Duties** — a leaderboard of every duty you've run, with clear times, items per run and your personal bests.
- **Zone Finder** — look up the loot table for any dungeon, trial or raid by name.
- **Blacklist** — everything you've hidden from tracking, in one place.
- **Export** — the whole history as JSON or CSV.

All of it is computed on a background thread, so opening the window or changing a filter never stalls the game, however large your history gets.

### Loot tables

Open the loot table for the duty you're in from the overlay's toolbar, or search for any other duty from the Zone Finder tab. Data comes from the [Garland Tools](https://www.garlandtools.org/) API.

> Loot table data is still being corrected and expanded. Some entries may be incomplete or inaccurate.

### Blacklist

Right-click any row in the loot list to stop tracking that item. Blacklisted items stay hidden across sessions and can be restored from **Statistics → Blacklist**.

---

## Installing

1. Open the plugin installer in game with `/xlplugins`.
2. Search for **LootView**.
3. Install and enable it.

---

## Using it

### Commands

| Command | What it does |
| --- | --- |
| `/lv` | Toggle the loot overlay |
| `/lv config` | Open the settings window |

You can also toggle the overlay from the server info bar, next to the server name.

### Overlay toolbar

| Button | What it does |
| --- | --- |
| People / person | Switch between all party loot and only your own |
| Broom | Clear the current list |
| Chart | Open Statistics & History |
| Table | Loot table for the duty you're in *(only shown inside a duty)* |
| Lock | Pin the window's position and size |
| Cog | Settings |
| Heart | Support on Patreon |

Right-click a row to blacklist the item or copy its name. Hover a row for the full item details.

---

## Settings

Settings are grouped into sections down the left of the window.

| Section | What's in it |
| --- | --- |
| **General** | Open on login, open when a duty starts, show only your loot, how many items the list keeps (10–200), server info bar button, market price estimates |
| **Tracking** | Track party loot, show the Need/Greed roll window, how long results stay on screen |
| **Appearance** | Lock position and size, background opacity |
| **Effects** | Item tooltips, particle effects and their intensity |
| **History** | Keep a permanent history, auto-save every 5 minutes, save to history when clearing the list |
| **Diagnostics** | LootView's live log, a one-click report to paste into a bug report, and buttons to report a bug or suggest a feature on GitHub |
| **About** | Version, release notes, links |

---

## Your data

Your loot history lives in your Dalamud configuration folder as `loot_history.json`, alongside `config.json`. Nothing about your loot, your character or your play is uploaded anywhere.

Two features do make outbound requests, and both send only an identifier — never your character, your history or anything about you:

- **Market prices** ask `universalis.app` for the going rate of item IDs currently in your loot list, along with your world's name. This runs automatically while the overlay is open; switch it off in **Settings → General → Market prices**.
- **Zone Finder / loot tables** ask `garlandtools.org` for a duty's drop table, and only when you open one.

Use **Statistics → Export** to take a backup at any time. **Statistics → Export → Delete all history** removes everything permanently.

---

## Building from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and a working Dalamud install — the build finds Dalamud through your XIVLauncher directory.

```bash
git clone https://github.com/GitHixy/LootView.git
cd LootView
dotnet build -c Release
```

The built plugin lands in `bin/Release/`. To run it as a dev plugin, point Dalamud's dev plugin locations at that folder, or copy the output into `%AppData%\XIVLauncher\devPlugins\LootView\`.

`icon.png` and `icon.svg` are both generated from `tools/make_icon.py`, which shares its
palette with `src/UI/Theme.cs`. Run `python tools/make_icon.py .` from the repo root after
changing it, so the two files stay in step.

---

## Release notes

The plugin shows what's new the first time you log in after an update, and you can reopen it any time from **Settings → About → What's new**.

**1.5.1** replaces the Ko-fi links with Patreon.

**1.5.0** rebuilds the roll panel around the whole life of a loot roll: a timer on every item, passes and non-rolls in the results, who is still deciding, a flourish for the winner, and results that clear themselves after a configurable time. It also adds a Diagnostics page for reporting bugs and suggesting features.

**1.4.0** is a complete visual overhaul: one coherent theme across every window, a redesigned settings window, properly drawn statistics charts, and a rebuilt loot list and roll panel. The Compact and Neon layouts were removed — Classic is now the only view and received all of the design work. Statistics were also moved off the draw thread, so large histories no longer freeze the game.

Full history is on the [Releases](https://github.com/GitHixy/LootView/releases) page.

---

## Contributing

Issues, feature requests and pull requests are all welcome — use the [issue tracker](https://github.com/GitHixy/LootView/issues). If you're reporting a bug, the Dalamud log (`/xllog`) and what you were doing at the time help a lot.

## Support the plugin

LootView is free and always will be. If it's saved you some time, support on Patreon is genuinely appreciated.

[![Patreon](https://img.shields.io/badge/Patreon-Support%20me-FF424D?logo=patreon&logoColor=white)](https://www.patreon.com/GitHixy)

## Licence

[GPL-3.0](LICENSE).

## Thanks

- The [Dalamud](https://github.com/goatcorp/Dalamud) team, for the plugin framework.
- [Universalis](https://universalis.app/), for the market board data.
- [Garland Tools](https://www.garlandtools.org/), for the loot table data.
- The FFXIV plugin community, for the help and the example.

---

*Not affiliated with Square Enix. FINAL FANTASY XIV © SQUARE ENIX CO., LTD.*
