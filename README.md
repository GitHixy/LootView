# LootView

A comprehensive loot tracking plugin for Final Fantasy XIV with advanced statistics, real-time roll monitoring, and zone loot tables. Built as a Dalamud plugin.

## Features

### Real-Time Loot & Roll Tracking
- **Live Display**: Automatically detects and displays loot as you obtain it
- **Roll Monitoring**: Real-time Need/Greed roll tracking with compact auto-closing window
- **Item Icons**: Shows actual item icons from the game with crisp rendering
- **Rarity Colors**: Items colored by their in-game rarity (white/green/blue/purple/pink)
- **Highlight Animation**: New items pulse with a golden glow for visibility
- **Party Loot Display**: See who obtained what items (color-coded by player)
- **Smart Detection**: Works with combat, gathering, retainers, dungeons, raids, and more
- **Bonus Tracking**: Automatically tracks duty roulette bonus gil rewards
- **Blacklist System**: Right-click items to hide them from tracking, manage blacklist in Statistics window

### Zone Finder & Loot Tables
- **Browse Any Duty**: View loot tables for any dungeon, trial, or raid
- **Search by Name**: Find duties by typing their name
- **Garland Tools Integration**: Powered by comprehensive Garland Tools API
- **Current Zone Loading**: Automatically load loot table for your current duty
- **Work-in-Progress Disclaimer**: Clear messaging about ongoing improvements

### Advanced Statistics & Analytics
- **Comprehensive Dashboard**: Overview of all your loot activity
- **Duty Tracker**: Track items obtained per duty/instance run
- **History Browser**: Search and filter through your complete loot history
- **Trends Analysis**: Visualize loot patterns over time
- **Peak Activity Times**: See when you're most active
- **Items Per Hour**: Track your farming efficiency
- **Blacklist Management**: View and manage all blacklisted items in one place
- **Export Functionality**: Export your data to CSV/JSON

### Customization
- **Window Styles**: Three distinct visual styles (Classic, Compact, Neon)
- **Lock Window**: Lock position and size to prevent accidental movement
- **Background Transparency**: Adjustable window opacity
- **Filter Options**: Show only your loot or see everyone's in party
- **Auto-Show Settings**: Configure window to open on login or when entering duties
- **Server Info Bar**: Optional button in game's top bar for quick access
- **Particle Effects**: Toggle visual effects for new item notifications

### User Interface
- **Icon Buttons**: Clean, modern interface with FontAwesome icons
- **Quick Access**: Stats, Lock, and Config buttons
- **Tooltips**: Helpful hints on hover
- **Organized Tabs**: Overview, History, Trends, Analytics, Duty Tracker, Zone Finder, Blacklist, Export
- **Responsive Design**: Adjustable window sizes with smooth scrolling

## Installation

### From Custom Repository
1. In your Dalamud settings, add the custom repository URL: `https://raw.githubusercontent.com/GitHixy/LootView/main/repo.json`
2. Search for "LootView" in the plugin installer
3. Install and enable the plugin

## Usage

### Commands
- `/lv` or `/lootview` - Toggle the main loot window
- `/lv config` - Open configuration window

### Main Window Controls
- **Clear All** - Clear current session's loot display
- **Stats Button** - Open Statistics & History window
- **Table Button** - Open Zone Loot Table window (when in duty)
- **Lock Button** - Lock/unlock window position and size
- **Config Button** - Open settings
- **Ko-fi Button** - Support plugin development
- **Show Only My Loot** - Filter to show only your loot in party
- **Style Selector** - Switch between Classic, Compact, or Neon visual styles
- **Right-Click Items** - Add or remove items from blacklist

### Statistics Window
- **Overview**: General statistics and summary
- **History**: Searchable loot history with filters
- **Trends**: Visualize your loot patterns over time
- **Analytics**: Deep dive into your loot data
- **Duty Tracker**: Track loot per duty/instance
- **Zone Finder**: Search and browse duty loot tables
- **Blacklist**: View and manage items hidden from tracking
- **Export**: Export your data to CSV or JSON

### Configuration Options
- **Open Window on Login**: Automatically show loot window when you log in
- **Open Window on Duty Start**: Automatically show loot window when entering a duty
- **Show Only My Loot**: Filter to display only your own loot
- **Show Server Info Bar Button**: Add quick access button to game's top bar
- **Max Items**: Control how many items to display (10-200)
- **Window Style**: Choose between Classic table, Compact list, or Neon cyberpunk theme
- **Background Alpha**: Adjust window transparency
- **Enable Particle Effects**: Toggle visual effects for new items
- **History Settings**: Configure automatic history tracking and retention

### Blacklist System
- **Add Items**: Right-click any item in the Loot Window and select "Add to Blacklist"
- **Remove Items**: Right-click a blacklisted item and select "Remove from Blacklist", or manage in Statistics > Blacklist tab
- **Persistent**: Blacklist is saved across sessions

## Contributing

Contributions are welcome! Please feel free to submit issues, feature requests, or pull requests.

## License

This project is licensed under the GPL-3.0 License - see the [LICENSE](LICENSE) file for details.

## Acknowledgments

- Thanks to the [Dalamud](https://github.com/goatcorp/Dalamud) team for the excellent plugin framework
- Thanks to the FFXIV modding community for continued support and inspiration
- Garland Tools API for loot table data

## Support

If you encounter any issues or have questions:

1. Check the [Issues](https://github.com/GitHixy/LootView/issues) page for existing reports
2. Create a new issue with detailed information about the problem

## Changelog

### Version 1.3.0

#### Major Features Added
- **Blacklist System**: Right-click items to add/remove from blacklist, preventing them from appearing in loot tracking
- **Blacklist Management Tab**: New tab in Statistics window to view and manage all blacklisted items at once
- **Loop Tracking**: Added support for "loop of" and "loops of" in crafting synthesis messages
- **Inventory Messages**: Now tracks "is added to your inventory" messages for more comprehensive coverage
- **Auto-Show on Duty**: New option to automatically show loot window when entering a duty
- **Item Name Matching**: Improved item lookup to handle "The" prefix variations

#### Bug Fixes
- Fixed right-click context menus not working properly in all three window styles
- Added deduplication system to prevent items from appearing twice when multiple chat messages trigger
- Fixed context menu positioning and interaction issues

#### Technical Improvements
- Enhanced message parsing with additional unit word patterns
- Improved item name cleaning and matching logic
- Better handling of nullable item data from game sheets
- Optimized duplicate detection with 2-second window and automatic cleanup

### Version 1.2.5

#### Major Features Added
- **Real-Time Roll Tracking**: Live Need/Greed roll monitoring with auto-closing compact window
- **Zone Finder & Loot Tables**: Browse and search loot tables for any duty, powered by Garland Tools API
- **Duty Roulette Bonus Tracking**: Automatically tracks bonus gil from duty roulette completions
- **Ko-fi Integration**: Support plugin development with built-in donation button

#### Enhanced Loot Parsing
- **Unit Words Support**: Added parsing for "Phials of", "Stalks of", "Chunks of", "Bottles of", "Pieces of", "Handfuls of", "Portions of"
- **Improved Plural Handling**: Enhanced detection for irregular plurals (-ixes to -ix, -xes to -x, -ses to -s, -ies to -y, -ves to -f/-fe)
- **Enhanced Loot List Detection**: Improved regex for all loot list message formats ("A", "An", or no article)
- **Player Name Cleaning**: Automatic server suffix removal from player names

#### Roll System Improvements
- **Multiple Item Support**: Handle multiple drops of the same item in roll tracking
- **Auto-Close Timer**: Roll window automatically closes 15 seconds after all winners are determined
- **Smart Cleanup**: Manual window close clears all active rolls, auto-close only clears completed
- **Real-Time Updates**: Live roll display with countdown timer in window title

#### User Experience Enhancements
- **Loot Table Disclaimer**: Clear messaging about work-in-progress status and ongoing improvements
- **Improved UI Styling**: Enhanced button layouts and Ko-fi brand integration
- **Manual History Cleanup**: Clear UI messaging that history cleanup is manual-only operation

### Version 1.2.0

- Major Bug/Logic Fixes

### Version 1.1.0
- Added comprehensive Statistics & History system
- Added Duty Tracker with detailed run statistics
- Added Trends and Analytics visualizations
- Added data export functionality (CSV/JSON)
- Improved UI with FontAwesome icons throughout
- Added window lock functionality
- Added Server Info Bar integration
- Added advanced filtering and search in history
- Improved background transparency controls
- Fixed various UI and tracking issues

### Version 1.0.0
- Initial release
- Real-time loot tracking
- Party loot display
- Configuration system

---

**Disclaimer**: This plugin is not affiliated with Square Enix or Final Fantasy XIV. Use at your own risk.