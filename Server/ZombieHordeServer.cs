using System.Reflection;
using System.Text.Json.Serialization;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Web;

namespace ZombieHorde;

// SPT 4.1 replaced AbstractModMetadata + IModWebMetadata with a single IModMetadata
// interface. IsBundleMod is gone; HasPrepatcher is new (unrelated to this mod).
public record ZombieHordeMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.vonbraunz.roamingzombies";
    public string Name { get; init; } = "Roaming Zombies";
    public string Author { get; init; } = "DrBraun";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("1.2.1");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.2");
    public bool HasPrepatcher { get; init; } = false;
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
    public string License { get; init; } = "MIT";
}

/// <summary>
/// Singleton service that owns the loaded config and can re-inject zombie
/// BossLocationSpawn entries on demand — called at startup and after each raid end.
/// </summary>
[Injectable(InjectionType = InjectionType.Singleton)]
public class ZombieSpawnService(
    ISptLogger<ZombieSpawnService> logger,
    LocationTable locationTable,
    RandomUtil randomUtil)
{
    public static readonly Dictionary<string, string> MapZones = new()
    {
        ["bigmap"]         = "ZoneDormitory,ZoneGasStation,ZoneScavBase,ZoneBrige,ZoneCustoms,ZoneOldVill",
        ["factory4_night"] = "BotZone",
        ["interchange"]    = "ZoneCenterBot,ZoneCenter,ZoneOLI,ZoneIDEA,ZoneGoshan",
        ["laboratory"]     = "BotZoneFloor1,BotZoneFloor2,BotZoneBasement",
        ["lighthouse"]     = "Zone_TreatmentContainers,Zone_Chalet,Zone_Blockpost,Zone_DestroyedHouse,Zone_Rocks,Zone_Village",
        ["rezervbase"]     = "ZoneRailStrorage,ZonePTOR2,ZoneBarrack,ZoneSubStorage,ZonePTOR1",
        ["sandbox"]        = "ZoneSandbox",
        ["sandbox_high"]   = "ZoneSandbox",
        ["shoreline"]      = "ZoneGreenHouses,ZonePort,ZoneSanatorium1,ZoneSanatorium2,ZoneSmuglers,ZoneMeteoStation",
        ["tarkovstreets"]  = "ZoneCarShowroom,ZoneClimova,ZoneMvd,ZoneSW01,ZoneConcordia",
        ["woods"]          = "ZoneWoodCutter,ZoneScavBase2,ZoneMiniHouse,ZoneBrokenVill,ZoneBigRocks",
        ["labyrinth"]      = "BotZone"
    };

    private static readonly string[] ZombieTypes =
    [
        "infectedAssault",
        "infectedPmc",
        "infectedCivil",
        "infectedLaborant"
    ];

    private ZombieHordeConfig? _config;

    public void Initialize(ZombieHordeConfig config)
    {
        _config = config;
    }

    public void InjectSpawns()
    {
        if (_config == null)
        {
            logger.Warning("[RoamingZombies] InjectSpawns called before config was loaded — skipping");
            return;
        }

        var locationDict = locationTable.GetDictionary();
        var totalMaps = 0;

        foreach (var (map, zone) in MapZones)
        {
            if (!_config.SpawnChance.TryGetValue(map, out var chance))
                continue;

            // Roll dice server-side so the percentage actually works.
            // alwaysSpawn bypasses the roll entirely and forces spawns.
            if (!_config.AlwaysSpawn)
            {
                if (chance <= 0)
                    continue;
                if (chance < 100 && randomUtil.GetInt(1, 101) > chance)
                    continue;
            }

            var actualKey = locationTable.GetMappedKey(map);
            if (!locationDict.TryGetValue(actualKey, out var location))
                continue;

            // ─── WAVE LOGIC (tested + working in 1.2.1, disabled for release) ────────────
            // Scaffolding for the v2.0 "rolling waves throughout the raid" feature is kept
            // here so we can re-enable with a one-line change when 2.0 ships. With
            // WaveCount = 1 the for loop degenerates to a single pass at SpawnDelaySeconds,
            // matching pre-1.2.1 single-spawn behavior. Bump WaveCount to 5 to get
            // 5 waves spaced WaveIntervalSeconds apart.
            const int WaveCount           = 1;    // 1 = single spawn; >1 = wave-based
            const int WaveIntervalSeconds = 60;   // seconds between consecutive waves
            // ──────────────────────────────────────────────────────────────────────────────

            for (int wave = 0; wave < WaveCount; wave++)
            {
                // First wave at SpawnDelaySeconds, each subsequent wave WaveInterval later.
                var waveTime = _config.SpawnDelaySeconds + (wave * WaveIntervalSeconds);

                foreach (var zombieType in ZombieTypes)
                {
                    // Melee zombies (BotDifficulty.normal → EZombieMode.Fast → knife).
                    // Full hordeSize count.
                    var meleeCount = randomUtil.GetInt(_config.HordeSize.Min, _config.HordeSize.Max + 1);
                    location.Base.BossLocationSpawn.Add(new BossLocationSpawn
                    {
                        BossName             = zombieType,
                        BossChance           = 100,
                        BossDifficulty       = "normal",
                        BossEscortType       = zombieType,
                        BossEscortAmount     = meleeCount.ToString(),
                        BossEscortDifficulty = "normal",
                        BossZone             = zone,
                        Delay                = 0,
                        DependKarma          = false,
                        DependKarmaPVE       = false,
                        ForceSpawn           = _config.AlwaysSpawn,
                        IgnoreMaxBots        = _config.IgnoreMaxBots,
                        SpawnMode            = null,
                        Supports             = null!,
                        Time                 = waveTime,
                        TriggerId            = "",
                        TriggerName          = ""
                    });

                    // Pistol zombies (BotDifficulty.hard → EZombieMode.Shooting → Makarov).
                    // Smaller count for variety — these use shootFromPlace / attackMoving
                    // decisions which go through EFT's standard bot logic nodes and should
                    // attack natively without our ForceKnifeKick workaround.
                    var pistolCount = randomUtil.GetInt(_config.PistolHordeSize.Min, _config.PistolHordeSize.Max + 1);
                    if (pistolCount > 0)
                    {
                        location.Base.BossLocationSpawn.Add(new BossLocationSpawn
                        {
                            BossName             = zombieType,
                            BossChance           = 100,
                            BossDifficulty       = "hard",
                            BossEscortType       = zombieType,
                            BossEscortAmount     = pistolCount.ToString(),
                            BossEscortDifficulty = "hard",
                            BossZone             = zone,
                            Delay                = 0,
                            DependKarma          = false,
                            DependKarmaPVE       = false,
                            ForceSpawn           = _config.AlwaysSpawn,
                            IgnoreMaxBots        = _config.IgnoreMaxBots,
                            SpawnMode            = null,
                            Supports             = null!,
                            Time                 = waveTime,
                            TriggerId            = "",
                            TriggerName          = ""
                        });
                    }
                }
            }

            totalMaps++;
        }

        logger.Info($"[RoamingZombies] Zombie spawns injected into {totalMaps} maps");
    }
}

/// <summary>
/// IOnLoad — loads config and does the initial spawn injection.
/// SPT 4.1 removed the old OnLoadOrder.PostDBModLoader stage as part of the
/// Config/Database-server removal; PostLoad (the last defined stage) is the closest
/// equivalent for "run once everything, including other mods' DB edits, is loaded".
/// The +70000 offset mirrors the old BPS-relative ordering — re-verify against BPS's
/// own 4.1 priority once that mod updates.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.PostLoad + 70000)]
public class ZombieHordeServer(
    ISptLogger<ZombieHordeServer> logger,
    ZombieSpawnService spawnService,
    ModHelper modHelper,
    JsonUtil jsonUtil) : IOnLoad
{
    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var modPath  = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var config   = await jsonUtil.DeserializeFromFileAsync<ZombieHordeConfig>(Path.Combine(modPath, "config.json"));

        if (config == null)
        {
            logger.Error("[RoamingZombies] Failed to load config.json");
            return;
        }

        spawnService.Initialize(config);
        spawnService.InjectSpawns();
    }
}

/// <summary>
/// Re-injects zombie spawns after every raid end.
/// BPS hooks /client/match/local/end and wipes ALL BossLocationSpawn entries before
/// rebuilding its own. Because alphabetical order puts _botplacementsystem before
/// ZombieHorde at equal TypePriority, BPS's handler fires first — our handler fires
/// second, after the wipe, and puts the zombies back.
/// </summary>
[Injectable]
public class ZombieHordeRouter(ZombieSpawnService spawnService, JsonUtil jsonUtil)
    : StaticRouter(
        jsonUtil,
        [
            new RouteAction(
                "/client/match/local/end",
                async (url, info, sessionId, output, cancellationToken) =>
                {
                    spawnService.InjectSpawns();
                    return output;
                })
        ])
{ }

public record ZombieHordeConfig
{
    [JsonPropertyName("hordeSize")]
    public required HordeSizeConfig HordeSize { get; set; }

    /// <summary>
    /// Count of pistol (BotDifficulty.hard / EZombieMode.Shooting) zombies to spawn
    /// per infected type, in addition to the melee (normal) zombies. Set min=max=0 to disable.
    /// </summary>
    [JsonPropertyName("pistolHordeSize")]
    public HordeSizeConfig PistolHordeSize { get; set; } = new HordeSizeConfig { Min = 1, Max = 2 };

    [JsonPropertyName("spawnDelaySeconds")]
    public int SpawnDelaySeconds { get; set; }

    [JsonPropertyName("spawnChance")]
    public required Dictionary<string, int> SpawnChance { get; set; }

    [JsonPropertyName("ignoreMaxBots")]
    public bool IgnoreMaxBots { get; set; } = true;

    /// <summary>
    /// When true, zombies always spawn on every raid regardless of spawnChance.
    /// Also sets ForceSpawn=true to bypass bot limits.
    /// </summary>
    [JsonPropertyName("alwaysSpawn")]
    public bool AlwaysSpawn { get; set; } = false;
}

public record HordeSizeConfig
{
    [JsonPropertyName("min")]
    public int Min { get; set; }

    [JsonPropertyName("max")]
    public int Max { get; set; }
}
