using System.Collections.Concurrent;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using T3MenuSharedApi;
using SharpTimerAPI;
using SharpTimerAPI.Events;

namespace SSJPlugin;

public class SSJPlugin : BasePlugin
{
    public override string ModuleAuthor => "EliteGames";
    public override string ModuleName => "SSJ-Plugin";
    public override string ModuleVersion => "2.1.0";

    // ─── Sync algorithm constants (from FL-StrafeMaster) ───
    private const float  AccelDeadzone          = 0.20f;
    private const float  TurnDeadzoneRad        = 0.0035f;
    private const float  JumpImpulseDelta       = 150f;
    private const float  FallingVzThreshold     = -20f;

    private readonly ConcurrentDictionary<int, PlayerData> _players = new();
    private readonly ConcurrentDictionary<ulong, PlayerSettings> _settings = new();
    private IT3MenuManager? _menuManager;
    private ISharpTimerEventSender? _stEventSender;
    private string? _dbConnectionString;
    private PluginConfig _config = new();

    // ─── Plugin Config (auto-generated JSON) ───
    public class PluginConfig
    {
        public string ChatTag { get; set; } = "[SSJ]";
        public string ChatTagColor { get; set; } = "DarkBlue";
        public bool StartzoneOnly { get; set; } = true;
        public int DefaultMaxJumps { get; set; } = 6;
        public bool DefaultEnabled { get; set; } = true;
        public bool DefaultRepeat { get; set; } = true;
        public int MinAirTicksToReport { get; set; } = 2;
        public int PrintThrottleTicks { get; set; } = 8;
        public int GroundSettleTicksAir { get; set; } = 3;
        public int ChainResetGroundTicks { get; set; } = 24;
        public DatabaseConfig Database { get; set; } = new();
    }

    public class DatabaseConfig
    {
        public bool Enabled { get; set; } = false;
        public string Host { get; set; } = "localhost";
        public int Port { get; set; } = 3306;
        public string Database { get; set; } = "BhopServer";
        public string User { get; set; } = "root";
        public string Password { get; set; } = "";
        public string TableName { get; set; } = "ssj_settings";
    }

    public override void Load(bool hotReload)
    {
        LoadConfig();
        RegisterListener<Listeners.OnTick>(OnTick);
        RegisterListener<Listeners.OnClientDisconnect>(slot => _players.TryRemove(slot, out _));
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);

        if (hotReload)
        {
            foreach (var p in Utilities.GetPlayers())
                if (p is { IsValid: true, IsBot: false, IsHLTV: false })
                    _players[p.Slot] = new PlayerData();
        }
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        try { _menuManager = new PluginCapability<IT3MenuManager>("t3menu:manager").Get(); }
        catch { Logger.LogWarning("[SSJ] T3Menu API not found. !ssj menu unavailable."); }

        try
        {
            _stEventSender = new PluginCapability<ISharpTimerEventSender>("sharptimer:event_sender").Get();
            if (_stEventSender != null)
            {
                _stEventSender.STEventSender += OnSharpTimerEvent;
            }
        }
        catch { Logger.LogWarning("[SSJ] SharpTimer API not found. SSJ will track all jumps."); }
    }

    private void OnSharpTimerEvent(object? sender, ISharpTimerPlayerEvent e)
    {
        if (e is StartTimerEvent startEvent && startEvent.Player != null && startEvent.Player.IsValid)
        {
            var pd = _players.GetOrAdd(startEvent.Player.Slot, _ => new PlayerData());
            pd.TimerStarted = true;
            pd.ResetChain();
        }
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null && player.IsValid && !player.IsBot)
        {
            var pd = _players.GetOrAdd(player.Slot, _ => new PlayerData());
            pd.TimerStarted = false;
            pd.ResetChain();
        }
        return HookResult.Continue;
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null && player.IsValid && !player.IsBot)
        {
            ulong steamId = player.SteamID;
            if (steamId != 0)
                _ = Task.Run(async () => await LoadPlayerSettingsAsync(steamId));
        }
        return HookResult.Continue;
    }

    // ─── Config Loading ───
    private void LoadConfig()
    {
        string cfgDir = Path.Combine(ModuleDirectory, "..", "..", "..", "configs", "plugins", "SSJ-Plugin");
        string cfgPath = Path.Combine(cfgDir, "config.json");

        // Remove old database.json if it exists
        string oldDbPath = Path.Combine(cfgDir, "database.json");
        if (File.Exists(oldDbPath) && !File.Exists(cfgPath))
        {
            try { File.Delete(oldDbPath); } catch { }
        }

        if (!File.Exists(cfgPath))
        {
            Directory.CreateDirectory(cfgDir);
            _config = new PluginConfig();
            File.WriteAllText(cfgPath, JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true }));
            Logger.LogInformation("[SSJ] Created default config.json at {Path}", cfgPath);
        }
        else
        {
            try
            {
                _config = JsonSerializer.Deserialize<PluginConfig>(File.ReadAllText(cfgPath)) ?? new PluginConfig();
                Logger.LogInformation("[SSJ] Config loaded.");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[SSJ] Failed to load config.json, using defaults.");
                _config = new PluginConfig();
            }
        }

        // Setup database if enabled
        if (_config.Database.Enabled)
        {
            var db = _config.Database;
            _dbConnectionString = $"Server={db.Host};Port={db.Port};Database={db.Database};User={db.User};Password={db.Password};";
            _ = Task.Run(EnsureTableAsync);
        }
    }

    private string GetTagColor()
    {
        return _config.ChatTagColor.ToLower() switch
        {
            "darkblue" => ChatColors.DarkBlue.ToString()!,
            "blue" => ChatColors.Blue.ToString()!,
            "lightblue" => ChatColors.LightBlue.ToString()!,
            "red" => ChatColors.Red.ToString()!,
            "darkred" => ChatColors.DarkRed.ToString()!,
            "green" => ChatColors.Green.ToString()!,
            "lime" => ChatColors.Lime.ToString()!,
            "gold" => ChatColors.Gold.ToString()!,
            "yellow" => ChatColors.Yellow.ToString()!,
            "purple" => ChatColors.Purple.ToString()!,
            "magenta" => ChatColors.Magenta.ToString()!,
            "olive" => ChatColors.Olive.ToString()!,
            "orange" => ChatColors.Orange.ToString()!,
            "grey" => ChatColors.Grey.ToString()!,
            "lightred" => ChatColors.LightRed.ToString()!,
            "lightpurple" => ChatColors.LightPurple.ToString()!,
            _ => ChatColors.DarkBlue.ToString()!
        };
    }

    // ─── DB Table Creation ───
    private async Task EnsureTableAsync()
    {
        if (_dbConnectionString == null) return;
        try
        {
            await using var conn = new MySqlConnection(_dbConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                CREATE TABLE IF NOT EXISTS {_config.Database.TableName} (
                    SteamID BIGINT UNSIGNED PRIMARY KEY,
                    Enabled TINYINT NOT NULL DEFAULT 1,
                    RepeatMode TINYINT NOT NULL DEFAULT 1,
                    MaxJumps INT NOT NULL DEFAULT 6
                );";
            await cmd.ExecuteNonQueryAsync();
            Logger.LogInformation("[SSJ] Database table ready.");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[SSJ] Failed to create ssj_settings table.");
        }
    }

    // ─── Load Player Settings from DB ───
    private async Task LoadPlayerSettingsAsync(ulong steamId)
    {
        if (_dbConnectionString == null) return;
        try
        {
            await using var conn = new MySqlConnection(_dbConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT Enabled, RepeatMode, MaxJumps FROM {_config.Database.TableName} WHERE SteamID = @sid";
            cmd.Parameters.AddWithValue("@sid", steamId);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var settings = _settings.GetOrAdd(steamId, _ => new PlayerSettings
                {
                    Enabled = _config.DefaultEnabled,
                    Repeat = _config.DefaultRepeat,
                    Mode = _config.DefaultMaxJumps
                });
                settings.Enabled = reader.GetInt32(0) == 1;
                settings.Repeat = reader.GetInt32(1) == 1;
                settings.Mode = reader.GetInt32(2);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[SSJ] Failed to load settings for {SteamId}", steamId);
        }
    }

    // ─── Save Player Settings to DB ───
    private async Task SavePlayerSettingsAsync(ulong steamId, PlayerSettings settings)
    {
        if (_dbConnectionString == null) return;
        try
        {
            await using var conn = new MySqlConnection(_dbConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                INSERT INTO {_config.Database.TableName} (SteamID, Enabled, RepeatMode, MaxJumps)
                VALUES (@sid, @en, @rep, @mj)
                ON DUPLICATE KEY UPDATE Enabled=@en, RepeatMode=@rep, MaxJumps=@mj";
            cmd.Parameters.AddWithValue("@sid", steamId);
            cmd.Parameters.AddWithValue("@en", settings.Enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("@rep", settings.Repeat ? 1 : 0);
            cmd.Parameters.AddWithValue("@mj", settings.Mode);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[SSJ] Failed to save settings for {SteamId}", steamId);
        }
    }

    // ─── Player Settings (persisted by SteamID) ───
    public class PlayerSettings
    {
        public bool Enabled { get; set; }
        public bool Repeat  { get; set; }
        public int  Mode    { get; set; }
        public bool AlreadyShown { get; set; }
    }

    // ─── Air State: per-jump frame data (velocity-based sync à la FL-StrafeMaster) ───
    public class AirState
    {
        public bool  InAir;
        public int   StableAirCount;
        public int   StableGroundCount;
        public int   TotalAirTicks;       // Total frames in air
        public int   GoodTicks;           // Frames where turn direction == accel direction
        public float StartHorizSpeed;     // Speed at takeoff
        public float LastVx, LastVy, LastVz;
        public float LastHeadingDeg;      // Heading in degrees for strafe projection
        public int   StrafeCount;         // Number of strafe direction changes
        public int   LastTurnSign;        // Last turn direction (+1 left, -1 right)
        public long  LastPrintTick;

        public void Reset()
        {
            InAir = false;
            TotalAirTicks = 0;
            GoodTicks = 0;
            StartHorizSpeed = 0f;
            StrafeCount = 0;
            LastTurnSign = 0;
        }
    }

    // ─── Player Data: bhop chain tracking ───
    public class PlayerData
    {
        public AirState Air { get; set; } = new();
        public int   JumpNumber { get; set; }
        public float PreSpeed { get; set; }          // Speed before first jump in chain
        public float PrevTakeoffSpeed { get; set; }  // Takeoff speed of previous jump
        public float LastTakeoffSpeed { get; set; }  // Takeoff speed of current jump
        public bool  TimerStarted { get; set; }      // Only track SSJ after leaving startzone

        public void ResetChain()
        {
            JumpNumber = 0;
            PreSpeed = 0f;
            PrevTakeoffSpeed = 0f;
            LastTakeoffSpeed = 0f;
            Air.Reset();
        }
    }

    // ─── Main Tick: velocity cross/dot sync + ground entity landing detection ───
    private void OnTick()
    {
        foreach (var player in Utilities.GetPlayers())
        {
            if (player is null || !player.IsValid || player.IsBot || player.IsHLTV || !player.PawnIsAlive)
                continue;

            var pawn = player.PlayerPawn?.Value;
            if (pawn is null || !pawn.IsValid) continue;

            ulong steamId = player.SteamID;
            if (steamId == 0) continue;

            var cfg = _settings.GetOrAdd(steamId, _ => new PlayerSettings
            {
                Enabled = _config.DefaultEnabled,
                Repeat = _config.DefaultRepeat,
                Mode = _config.DefaultMaxJumps
            });
            if (!cfg.Enabled) continue;

            var pd = _players.GetOrAdd(player.Slot, _ => new PlayerData());

            // Only track SSJ after timer started (player left startzone)
            if (_config.StartzoneOnly && _stEventSender != null && !pd.TimerStarted) continue;
            var st = pd.Air;

            var v = pawn.AbsVelocity;
            float vx = v.X, vy = v.Y, vz = v.Z;
            float horizSpeed = MathF.Sqrt(vx * vx + vy * vy);

            // Ground detection via GroundEntity (more reliable than FL_ONGROUND for autobhop)
            bool rawOnGround = false;
            try { rawOnGround = pawn.GroundEntity.Value is not null; } catch { }

            // ─── Ground state machine ───
            if (rawOnGround)
            {
                st.StableGroundCount++;
                st.StableAirCount = 0;

                if (st.InAir && st.StableGroundCount >= 1)
                {
                    // LANDING confirmed
                    float endHoriz = MathF.Sqrt(st.LastVx * st.LastVx + st.LastVy * st.LastVy);
                    HandleLanding(player, pd, cfg, st, endHoriz);
                    st.Reset();
                }

                // Chain reset if on ground too long — SSJ stops until next startzone
                if (st.StableGroundCount > _config.ChainResetGroundTicks && pd.JumpNumber > 0)
                {
                    pd.ResetChain();
                    pd.TimerStarted = false;
                }
            }
            else
            {
                st.StableAirCount++;
                st.StableGroundCount = 0;

                if (!st.InAir && st.StableAirCount >= _config.GroundSettleTicksAir)
                {
                    // TAKEOFF confirmed
                    st.InAir = true;
                    st.TotalAirTicks = 0;
                    st.GoodTicks = 0;
                    st.StartHorizSpeed = horizSpeed;
                    st.StrafeCount = 0;
                    st.LastTurnSign = 0;
                    st.LastVx = vx; st.LastVy = vy; st.LastVz = vz;
                    st.LastHeadingDeg = SafeHeadingDeg(vx, vy);

                    // Track bhop chain
                    bool isBhop = pd.JumpNumber > 0; // if already in chain, it's a bhop (ground settle was short)
                    if (!isBhop && pd.JumpNumber > 0)
                        pd.ResetChain();

                    pd.PrevTakeoffSpeed = pd.LastTakeoffSpeed;
                    pd.LastTakeoffSpeed = horizSpeed;
                    pd.JumpNumber++;

                    if (pd.JumpNumber == 1)
                    {
                        pd.PreSpeed = horizSpeed;
                        cfg.AlreadyShown = false;
                    }
                }
            }

            // ─── Autobhop re-jump detection (in-air landing+jump in same tick) ───
            if (st.InAir)
            {
                float dvz = vz - st.LastVz;
                bool wasFalling = st.LastVz <= FallingVzThreshold;

                if (wasFalling && dvz >= JumpImpulseDelta)
                {
                    // Player landed and re-jumped in same tick (autobhop)
                    float endHoriz = MathF.Sqrt(st.LastVx * st.LastVx + st.LastVy * st.LastVy);
                    HandleLanding(player, pd, cfg, st, endHoriz);

                    // Immediately start a new jump
                    st.InAir = true;
                    st.TotalAirTicks = 0;
                    st.GoodTicks = 0;
                    st.StartHorizSpeed = horizSpeed;
                    st.StrafeCount = 0;
                    st.LastTurnSign = 0;
                    st.StableAirCount = _config.GroundSettleTicksAir;
                    st.StableGroundCount = 0;
                    st.LastVx = vx; st.LastVy = vy; st.LastVz = vz;
                    st.LastHeadingDeg = SafeHeadingDeg(vx, vy);

                    pd.PrevTakeoffSpeed = pd.LastTakeoffSpeed;
                    pd.LastTakeoffSpeed = horizSpeed;
                    pd.JumpNumber++;
                }
            }

            // ─── In-air sync tracking (cross/dot product on velocity vectors) ───
            if (st.InAir)
            {
                st.TotalAirTicks++;

                float pvx = st.LastVx, pvy = st.LastVy;
                float cvx = vx, cvy = vy;

                // Cross/dot product to get rotation angle between velocity frames
                float dot   = pvx * cvx + pvy * cvy;
                float cross = pvx * cvy - pvy * cvx;
                float ang   = MathF.Atan2(cross, dot);

                // Determine turn direction
                int turnSign = 0;
                if      (ang >  TurnDeadzoneRad) turnSign = +1;  // Turning left
                else if (ang < -TurnDeadzoneRad) turnSign = -1;  // Turning right

                // Project acceleration onto perpendicular axis (left of heading)
                const float Deg2Rad = (float)(Math.PI / 180.0);
                float hPrevRad = st.LastHeadingDeg * Deg2Rad;
                float leftPX   = -MathF.Sin(hPrevRad);
                float leftPY   =  MathF.Cos(hPrevRad);

                float dvx      = cvx - pvx;
                float dvy      = cvy - pvy;
                float projLeft = dvx * leftPX + dvy * leftPY;

                // Determine strafe acceleration direction
                int sideSign = 0;
                if      (projLeft >  AccelDeadzone) sideSign = +1;
                else if (projLeft < -AccelDeadzone) sideSign = -1;

                // SYNC: turn direction matches strafe acceleration direction
                if (turnSign != 0 && sideSign != 0 && turnSign == sideSign)
                    st.GoodTicks++;

                // Count strafe direction changes
                if (turnSign != 0 && turnSign != st.LastTurnSign)
                {
                    st.StrafeCount++;
                    st.LastTurnSign = turnSign;
                }

                st.LastVx = cvx; st.LastVy = cvy; st.LastVz = vz;
                st.LastHeadingDeg = SafeHeadingDeg(cvx, cvy);
            }
            else
            {
                st.LastVz = vz;
            }
        }
    }

    // ─── Handle Landing: print SSJ stats ───
    private void HandleLanding(CCSPlayerController player, PlayerData pd, PlayerSettings cfg, AirState st, float endHorizSpeed)
    {
        if (st.TotalAirTicks < _config.MinAirTicksToReport) return;
        if (pd.JumpNumber <= 0 || pd.JumpNumber > cfg.Mode) return;
        if (!cfg.Repeat && cfg.AlreadyShown) return;

        long tick = Server.TickCount;
        if (tick - st.LastPrintTick < _config.PrintThrottleTicks) return;

        int sync = st.TotalAirTicks > 0
            ? (int)MathF.Round(100f * st.GoodTicks / st.TotalAirTicks)
            : 0;

        float gain = endHorizSpeed - st.StartHorizSpeed;
        double gainPct = st.StartHorizSpeed > 0
            ? Math.Round((double)gain / st.StartHorizSpeed * 100.0, 1)
            : 0;

        string syncColor = sync switch
        {
            >= 80 => ChatColors.Green.ToString()!,
            >= 60 => ChatColors.Yellow.ToString()!,
            >= 40 => ChatColors.Gold.ToString()!,
            _     => ChatColors.Red.ToString()!
        };

        string gainColor = gainPct switch
        {
            > 0  => ChatColors.Green.ToString()!,
            0    => ChatColors.Yellow.ToString()!,
            _    => ChatColors.Red.ToString()!
        };

        string gainSign = gainPct >= 0 ? "+" : "";

        string tag = $"{GetTagColor()}{_config.ChatTag}{ChatColors.Default}";

        if (pd.JumpNumber == 1)
        {
            player.PrintToChat(
                $" {tag} Jump {ChatColors.LightBlue}{pd.JumpNumber}{ChatColors.Default}" +
                $" | Pre: {ChatColors.Gold}{pd.PreSpeed:F0}{ChatColors.Default}" +
                $" | Speed: {ChatColors.LightBlue}{endHorizSpeed:F0}{ChatColors.Default}" +
                $" | Gain: {gainColor}{gainSign}{gainPct}%{ChatColors.Default}" +
                $" | Sync: {syncColor}{sync}%{ChatColors.Default}" +
                $" | Strafes: {ChatColors.Purple}{st.StrafeCount}{ChatColors.Default}");
        }
        else
        {
            player.PrintToChat(
                $" {tag} Jump {ChatColors.LightBlue}{pd.JumpNumber}{ChatColors.Default}" +
                $" | Speed: {ChatColors.LightBlue}{endHorizSpeed:F0}{ChatColors.Default}" +
                $" | Gain: {gainColor}{gainSign}{gainPct}%{ChatColors.Default}" +
                $" | Sync: {syncColor}{sync}%{ChatColors.Default}" +
                $" | Strafes: {ChatColors.Purple}{st.StrafeCount}{ChatColors.Default}");
        }

        st.LastPrintTick = tick;

        if (pd.JumpNumber >= cfg.Mode)
            cfg.AlreadyShown = true;
    }

    // ─── Heading from velocity vector ───
    private static float SafeHeadingDeg(float x, float y)
    {
        if (x == 0f && y == 0f) return 0f;
        return MathF.Atan2(y, x) * (180f / MathF.PI);
    }

    // ─── !ssj Command: Open T3Menu Settings ───
    [ConsoleCommand("css_ssj", "Open SSJ settings menu")]
    public void OnSSJCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return;

        if (_menuManager == null)
        {
            player.PrintToChat($" {GetTagColor()}{_config.ChatTag}{ChatColors.Red} T3Menu API not available.");
            return;
        }

        var steamId = player.SteamID;
        var settings = _settings.GetOrAdd(steamId, _ => new PlayerSettings
        {
            Enabled = _config.DefaultEnabled,
            Repeat = _config.DefaultRepeat,
            Mode = _config.DefaultMaxJumps
        });
        OpenSSJMenu(player, settings);
    }

    private void OpenSSJMenu(CCSPlayerController player, PlayerSettings settings)
    {
        if (_menuManager == null) return;

        IT3Menu menu = _menuManager.CreateMenu("SSJ Settings", freezePlayer: false, hasSound: true, isExitable: true);

        menu.AddBoolOption("Enabled", defaultValue: settings.Enabled, (p, o) =>
        {
            settings.Enabled = !settings.Enabled;
            p.PrintToChat($" {GetTagColor()}{_config.ChatTag}{ChatColors.Default} SSJ {(settings.Enabled ? $"{ChatColors.Green}Enabled" : $"{ChatColors.Red}Disabled")}");
            _ = Task.Run(async () => await SavePlayerSettingsAsync(p.SteamID, settings));
        });

        menu.AddBoolOption("Repeat", defaultValue: settings.Repeat, (p, o) =>
        {
            settings.Repeat = !settings.Repeat;
            p.PrintToChat($" {GetTagColor()}{_config.ChatTag}{ChatColors.Default} Repeat {(settings.Repeat ? $"{ChatColors.Green}On" : $"{ChatColors.Red}Off")}");
            _ = Task.Run(async () => await SavePlayerSettingsAsync(p.SteamID, settings));
        });

        List<object> modeValues = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        menu.AddSliderOption("Jumps", values: modeValues, defaultValue: settings.Mode, displayItems: 3, (p, o, index) =>
        {
            if (o is IT3Option sliderOption && sliderOption.DefaultValue != null)
            {
                settings.Mode = (int)sliderOption.DefaultValue;
                p.PrintToChat($" {GetTagColor()}{_config.ChatTag}{ChatColors.Default} Showing {ChatColors.LightBlue}{settings.Mode}{ChatColors.Default} jumps");
                _ = Task.Run(async () => await SavePlayerSettingsAsync(p.SteamID, settings));
            }
        });

        menu.AddOption("Reset Stats", (p, o) =>
        {
            if (_players.TryGetValue(p.Slot, out var data))
                data.ResetChain();
            settings.AlreadyShown = false;
            p.PrintToChat($" {GetTagColor()}{_config.ChatTag}{ChatColors.Default} Stats reset.");
            menu.Close(p);
        });

        _menuManager.OpenMainMenu(player, menu);
    }
}
