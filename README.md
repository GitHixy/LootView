# LootView

A customizable loot tracking addon for Final Fantasy XIV that displays everything you drop in a fancy way. Built as a Dalamud plugin.

## Features

- 📊 **Real-time Loot Tracking**: Automatically detects and displays loot as you obtain it
- �️ **Item Icons**: Shows actual item icons from the game (20x20)
- 🎨 **Rarity Colors**: Items colored by their real in-game rarity (white/green/blue/purple/pink)
- ✨ **Highlight Animation**: New items pulse with a golden glow for 3 seconds
- 👥 **Party Loot Display**: Shows who obtained what items (color-coded)
- 🔍 **Advanced Filtering**: Filter by player, show only your loot
- ⏰ **Time Display**: Relative timestamps (seconds/minutes/hours ago)
- ⚙️ **Easy Settings Access**: Quick access to settings via gear button in overlay
- 🚀 **Auto-Open on Login**: Optional automatic window opening when you log in
- 🎯 **Smart Detection**: Detects loot from monsters, gathering, retainers, market board, and more

## Installation

### From Official Dalamud Repository (Recommended)
*Coming soon - pending approval*

### From Custom Repository
1. In your Dalamud settings, add the custom repository URL: `[REPO_URL_HERE]`
2. Search for "LootView" in the plugin installer
3. Install and enable the plugin

### Manual Installation
1. Download the latest release from the [Releases](https://github.com/GitHixy/LootView/releases) page
2. Extract the contents to your Dalamud plugins directory
3. Restart the game or reload plugins

## Usage

### Basic Commands
- `/lootview` or `/lv` - Toggle the loot window
- `/lootview config` - Open configuration window
- `/lootview show` - Show the loot window
- `/lootview hide` - Hide the loot window
- `/lootview clear` - Clear loot history
- `/lootview help` - Show help message

### Window Features
- **Item Icons**: Visual representation of each item
- **Player Names**: See who obtained each item (color-coded)
- **Quantities**: Amount of each item obtained
- **Time Stamps**: When items were obtained
- **Zone Information**: Where items were found
- **Quality Indicators**: High Quality (HQ) items are marked

### Filtering Options
- **Own Loot Only**: Show only items you obtained
- **Minimum Rarity**: Filter by item rarity level
- **HQ Only**: Show only High Quality items
- **Search**: Find specific items or players
- **Auto-Hide**: Automatically hide old entries

## Configuration

The plugin offers extensive customization options:

### Display Settings
- Toggle visibility of icons, timestamps, player names, zones, quantities
- Adjust window opacity and locking options
- Set maximum number of displayed items

### Filtering Settings
- Default filter preferences
- Auto-hide settings for old items
- Rarity and quality filters

### Appearance Settings
- Custom colors for different loot types
- Player-specific color coding
- Rarity-based color schemes

### Advanced Settings
- Tracking options for different loot sources
- Notification preferences
- Debug logging options

## Building from Source

### Prerequisites
- .NET 8.0 SDK or later
- Visual Studio 2022 or JetBrains Rider (recommended)
- Final Fantasy XIV with Dalamud installed

### Build Steps
1. Clone this repository
2. Open `LootView.csproj` in your IDE
3. Restore NuGet packages
4. Build the solution
5. The compiled plugin will be in the `bin` directory

### Development Setup
1. Set the `DalamudLibPath` in the project file to your Dalamud installation
2. For debugging, you can attach to the `ffxiv_dx11.exe` process

## Contributing

Contributions are welcome! Please feel free to submit issues, feature requests, or pull requests.

### Guidelines
- Follow the existing code style
- Test your changes thoroughly
- Update documentation as needed
- Ensure compatibility with the latest Dalamud API

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Acknowledgments

- Thanks to the [Dalamud](https://github.com/goatcorp/Dalamud) team for the excellent plugin framework
- Thanks to the FFXIV modding community for continued support and inspiration
- Special thanks to all beta testers and contributors

## Support

If you encounter any issues or have questions:

1. Check the [Issues](https://github.com/GitHixy/LootView/issues) page for existing reports
2. Create a new issue with detailed information about the problem
3. Join the [Discord community](DISCORD_LINK_HERE) for real-time help

## Changelog

### Version 1.0.0
- Initial release
- Real-time loot tracking
- Customizable interface
- Advanced filtering system
- Party loot display
- Configuration system

---

**Disclaimer**: This plugin is not affiliated with Square Enix or Final Fantasy XIV. Use at your own risk.