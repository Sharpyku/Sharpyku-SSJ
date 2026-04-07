# Sharpyku-SSJ — Strafe Sync Jump Plugin for CS2

A **Strafe Sync Jump (SSJ)** plugin for CS2 servers running [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp). Displays per-jump strafe statistics in chat, helping players improve their bunny hopping technique.

Uses a **velocity cross/dot product** algorithm (inspired by [FL-StrafeMaster](https://github.com/JumperBhop/FL-StrafeMaster)) for accurate strafe sync calculation.

![CS2](https://img.shields.io/badge/CS2-Plugin-blue)
![.NET 8](https://img.shields.io/badge/.NET-8.0-purple)
![License](https://img.shields.io/badge/License-MIT-green)

## Features

- **Per-jump stats** — Pre speed, speed, gain %, sync %, strafe count
- **Accurate sync** — Velocity-based cross/dot product sync algorithm
- **Autobhop support** — Detects in-air re-jumps (same-tick land+jump)
- **Fully configurable** — Auto-generated `config.json` with all settings (tag, colors, database, defaults, tuning)
- **Startzone integration** — Only tracks jumps after leaving the start zone (requires [SharpTimer](https://github.com/DEAFPS/SharpTimer))
- **Per-player settings** — Toggle SSJ on/off, repeat mode, max jumps to display (1-10)
- **MySQL/MariaDB persistence** — Player settings saved to database, persist across server restarts
- **T3Menu integration** — Interactive settings menu via `!ssj` command (requires [T3Menu-API](https://github.com/T3Marius/T3Menu-API))

## Chat Output

```
[SSJ] Jump 1 | Pre: 340 | Speed: 412 | Gain: +21.2% | Sync: 87% | Strafes: 4
[SSJ] Jump 2 | Speed: 478 | Gain: +16.0% | Sync: 82% | Strafes: 5
[SSJ] Jump 3 | Speed: 531 | Gain: +11.1% | Sync: 74% | Strafes: 6
```

- **Pre** — Horizontal speed at the start of the first jump
- **Speed** — Horizontal speed at landing
- **Gain** — Speed gained during the jump (%)
- **Sync** — Percentage of air ticks where turn direction matched strafe acceleration
- **Strafes** — Number of strafe direction changes during the jump

Colors: Sync ≥80% = green, ≥60% = yellow, ≥40% = gold, <40% = red

## Requirements

- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) v1.0.364+
- [T3Menu-API](https://github.com/T3Marius/T3Menu-API) — for the `!ssj` settings menu
- [SharpTimer](https://github.com/DEAFPS/SharpTimer) — for startzone detection (optional — without it, SSJ tracks all jumps)
- **MySQL or MariaDB** — for saving player settings (optional — without it, settings reset on reconnect)

## Installation

1. Download the latest release from [Releases](../../releases)
2. Extract the `SSJ-Plugin` folder to:
   ```
   csgo/addons/counterstrikesharp/plugins/SSJ-Plugin/
   ```
3. Make sure `SSJ-Plugin.dll` and `MySqlConnector.dll` are both in the folder
4. Start/restart your server — a `config.json` will be auto-generated
5. Edit the config to your liking (see below)

### File Structure
```
csgo/addons/counterstrikesharp/
├── plugins/
│   └── SSJ-Plugin/
│       ├── SSJ-Plugin.dll
│       └── MySqlConnector.dll
└── configs/
    └── plugins/
        └── SSJ-Plugin/
            └── config.json         ← auto-created on first load
```

## Configuration

On first server start, the plugin auto-generates a config file at:
```
csgo/addons/counterstrikesharp/configs/plugins/SSJ-Plugin/config.json
```

**All plugin settings are configured from this single file.** Here's the full default config with explanations:

```json
{
  "ChatTag": "[SSJ]",
  "ChatTagColor": "DarkBlue",
  "StartzoneOnly": true,
  "DefaultMaxJumps": 6,
  "DefaultEnabled": true,
  "DefaultRepeat": true,
  "MinAirTicksToReport": 2,
  "PrintThrottleTicks": 8,
  "GroundSettleTicksAir": 3,
  "ChainResetGroundTicks": 24,
  "Database": {
    "Enabled": false,
    "Host": "localhost",
    "Port": 3306,
    "Database": "BhopServer",
    "User": "root",
    "Password": "",
    "TableName": "ssj_settings"
  }
}
```

### Config Options

| Option | Default | Description |
|--------|---------|-------------|
| `ChatTag` | `[SSJ]` | The tag shown in chat before each message. Change to anything you want (e.g. `[BHOP]`, `[EG]`) |
| `ChatTagColor` | `DarkBlue` | Color of the tag. Options: `DarkBlue`, `Blue`, `LightBlue`, `Red`, `DarkRed`, `Green`, `Lime`, `Gold`, `Yellow`, `Purple`, `Magenta`, `Olive`, `Orange`, `Grey`, `LightRed`, `LightPurple` |
| `StartzoneOnly` | `true` | Only track SSJ after player leaves the start zone. Set to `false` to track all jumps anywhere |
| `DefaultMaxJumps` | `6` | Default number of jumps to display per run (players can override via `!ssj` menu) |
| `DefaultEnabled` | `true` | Whether SSJ is enabled by default for new players |
| `DefaultRepeat` | `true` | Whether stats repeat every run by default |
| `MinAirTicksToReport` | `2` | Minimum air ticks before a jump is reported (filters micro-jumps) |
| `PrintThrottleTicks` | `8` | Minimum ticks between chat messages (prevents spam) |
| `GroundSettleTicksAir` | `3` | Ticks in air before takeoff is confirmed |
| `ChainResetGroundTicks` | `24` | Ticks on ground before bhop chain resets |

### Database Settings

| Option | Default | Description |
|--------|---------|-------------|
| `Database.Enabled` | `false` | Set to `true` to enable MySQL/MariaDB persistence |
| `Database.Host` | `localhost` | Database server address |
| `Database.Port` | `3306` | Database server port |
| `Database.Database` | `BhopServer` | Database name |
| `Database.User` | `root` | Database username |
| `Database.Password` | `""` | Database password |
| `Database.TableName` | `ssj_settings` | Name of the settings table |

The plugin automatically creates the database table on first connect. Player settings (Enabled, Repeat, MaxJumps) are saved per SteamID.

> **No database?** Leave `Database.Enabled` as `false` — the plugin works fine without it, settings just won't persist between sessions.

## Commands

| Command | Description                  |
|---------|------------------------------|
| `!ssj`  | Open SSJ settings menu       |

### Settings Menu (via T3Menu)

- **Enabled** — Toggle SSJ on/off
- **Repeat** — Show stats every run or only the first time
- **Jumps** — Slider to set max jumps to display (1-10)
- **Reset Stats** — Reset current bhop chain

## How Sync Works

The plugin uses a **velocity-based cross/dot product** algorithm:

1. Each tick, it takes the player's horizontal velocity vector `(vx, vy)`
2. Computes the **rotation angle** between current and previous velocity using:
   - `dot = prev·curr` (cosine of angle)
   - `cross = prev×curr` (sine of angle)
   - `angle = atan2(cross, dot)`
3. Projects the **acceleration vector** onto the perpendicular axis of the heading direction
4. If the turn direction matches the strafe acceleration direction → that tick counts as **synced**
5. `Sync% = (synced ticks / total air ticks) × 100`

This is more accurate than yaw-based methods because it measures actual velocity changes rather than just view angles.

## Building from Source

```bash
dotnet build -c Release
```

### Dependencies (NuGet)
- `CounterStrikeSharp.API` 1.0.364
- `MySqlConnector` 2.3.7

### Shared API References
The `.csproj` references `T3MenuSharedAPI.dll` and `SharpTimerAPI.dll` — adjust the `HintPath` entries to match your local setup.

## Credits

- Sync algorithm inspired by [FL-StrafeMaster](https://github.com/JumperBhop/FL-StrafeMaster) by JumperBhop
- Built on [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) by roflmuffin
- Menu system by [T3Menu-API](https://github.com/T3Marius/T3Menu-API) by T3Marius
- Timer integration via [SharpTimer](https://github.com/DEAFPS/SharpTimer) API

## License

[MIT](LICENSE)
