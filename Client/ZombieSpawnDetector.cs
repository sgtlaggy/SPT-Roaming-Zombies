using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Communications;
using UnityEngine;
using UnityEngine.Networking;

namespace ZombieHorde.Client
{
    /// <summary>
    /// Per-raid component that:
    ///   1. Tracks spawned infected bots
    ///   2. Drives melee zombies (those with a KnifeController) toward the player —
    ///      bypasses the native RunToEnemyUpdate path check, which fails reliably in
    ///      this EFT version and prevents infected brains from pursuing combatively
    ///   3. Triggers KnifeController.MakeKnifeKick() at close range so zombies actually
    ///      swing — the native combat layer returns the decision but nothing downstream
    ///      acts on it
    ///   4. Shows a HUD notification + plays a horde alert audio clip on first spawn
    /// Pistol zombies (BotDifficulty=hard / EZombieMode.Shooting) are left alone —
    /// they route through EFT's standard shoot AI and engage the player natively.
    /// </summary>
    public class ZombieSpawnDetector : MonoBehaviour
    {
        private static readonly HashSet<string> ZombieTypes = new HashSet<string>
        {
            "infectedAssault",
            "infectedPmc",
            "infectedCivil",
            "infectedLaborant"
        };

        // Melee attack tuning
        private const float MeleeRange         = 4f;     // call MakeKnifeKick within this distance
        private const float SwingCooldown      = 1.0f;   // per-zombie seconds between swings
        private const float PursuitMinRange    = 4f;     // no pursuit when already in melee range
        private const float PursuitMaxRange    = 40f;    // stop pursuing beyond this
        private const float PursuitRepathEvery = 1.5f;   // per-zombie seconds between re-path calls
        private const float ChestHeightOffset  = 1.4f;   // Player.Transform.position is at feet — offset up
                                                         // to chest height so zombies face/swing at torso,
                                                         // not at the ground. Without this, knife box-cast
                                                         // angles down and hits only legs.

        private GameWorld _gameWorld;
        private bool      _hordeDetected;
        private AudioClip _hordeClip;

        private readonly List<Player>              _zombies    = new List<Player>();
        private readonly Dictionary<Player, float> _nextSwing  = new Dictionary<Player, float>();
        private readonly Dictionary<Player, float> _nextRepath = new Dictionary<Player, float>();

        // Reflection cache — resolved once on first use against EFT's obfuscated API surface
        private static bool       _reflectionResolved;
        private static MethodInfo _makeKnifeKickMethod;
        private static MethodInfo _goToPointMethod;
        private static MethodInfo _lookToPointMethod;
        private static PropertyInfo _weaponManagerProp;
        private static PropertyInfo _meleeDataProp;
        private static PropertyInfo _knifeControllerProp;
        private static PropertyInfo _steeringProp;

        public static void Init()
        {
            if (Singleton<GameWorld>.Instantiated)
                Singleton<GameWorld>.Instance.GetOrAddComponent<ZombieSpawnDetector>();
        }

        private void Start()
        {
            _gameWorld     = Singleton<GameWorld>.Instance;
            _hordeDetected = false;

            _gameWorld.OnPersonAdd += OnPersonAdd;

            foreach (var player in _gameWorld.AllAlivePlayersList)
                CheckPlayer(player as Player);

            LoadAudioClip();
        }

        private void OnPersonAdd(IPlayer iPlayer) => CheckPlayer(iPlayer as Player);

        private void CheckPlayer(Player player)
        {
            if (player == null || player.IsYourPlayer) return;

            var role = player.Profile?.Info?.Settings?.Role;
            if (role == null || !ZombieTypes.Contains(role.ToString())) return;

            FixInfectedSide(player);

            if (!_zombies.Contains(player))
                _zombies.Add(player);

            if (!_hordeDetected)
            {
                _hordeDetected = true;
                OnHordeSpawned();
            }
        }

        private void Update()
        {
            if (_gameWorld == null) return;

            _zombies.RemoveAll(z => z == null || !z.HealthController.IsAlive);
            if (_zombies.Count == 0) return;

            // Gather live human players. On Fika headless there's no MainPlayer (no local
            // human), but remote players still appear in AllAlivePlayersList. On solo SPT
            // and Fika host there's a MainPlayer but we want to target the closest human
            // per zombie rather than assuming MainPlayer is the only target.
            var humans = _humans;
            humans.Clear();
            foreach (var p in _gameWorld.AllAlivePlayersList)
            {
                if (p is Player player && !player.IsAI
                    && player.HealthController != null && player.HealthController.IsAlive)
                {
                    humans.Add(player);
                }
            }
            if (humans.Count == 0) return;

            foreach (var zombie in _zombies)
            {
                // Find the closest human to this zombie
                Player target = null;
                float closestDist = float.MaxValue;
                var zombiePos = zombie.Transform.position;
                for (int i = 0; i < humans.Count; i++)
                {
                    var d = Vector3.Distance(zombiePos, humans[i].Transform.position);
                    if (d < closestDist) { closestDist = d; target = humans[i]; }
                }
                if (target == null) continue;

                var targetPos = target.Transform.position;

                // Pursue when 4–40m: face target + nav toward them
                if (closestDist >= PursuitMinRange && closestDist <= PursuitMaxRange)
                    DriveMeleePursuit(zombie, targetPos);

                // Swing when within melee range. Pass feet position — TriggerMeleeSwing
                // offsets upward internally for the face-aim so the hit box-cast reaches
                // torso level instead of angling down into legs.
                if (closestDist < MeleeRange)
                    TriggerMeleeSwing(zombie, targetPos);
            }
        }

        // Reused per-frame to avoid allocating a new list each Update()
        private readonly List<Player> _humans = new List<Player>();

        /// <summary>
        /// Drives a melee zombie toward the player. Calls BotOwner.Steering.LookToPoint
        /// (face player) + BotOwner.GoToPoint (nav toward player). Throttled to avoid
        /// thrashing the NavMesh each frame. Pistol zombies (no KnifeController) pursue
        /// natively via EFT's standard shoot-AI, so we skip them.
        /// </summary>
        private void DriveMeleePursuit(Player zombie, Vector3 playerPos)
        {
            if (_nextRepath.TryGetValue(zombie, out var next) && Time.time < next) return;

            try
            {
                var botOwner = zombie.AIData?.BotOwner;
                if (botOwner == null) return;

                ResolveReflection(botOwner);

                // Melee only — pistol zombies have no KnifeController
                var wm = _weaponManagerProp?.GetValue(botOwner);
                var melee = _meleeDataProp?.GetValue(wm);
                var knifeCtrl = _knifeControllerProp?.GetValue(melee);
                if (knifeCtrl == null) return;

                _nextRepath[zombie] = Time.time + PursuitRepathEvery;

                // Face the target at chest height (feet-level would angle the swing-cast down).
                // But pathfind to feet level — NavMesh agents walk on the ground.
                var aimPos = playerPos + Vector3.up * ChestHeightOffset;
                FacePoint(botOwner, aimPos);
                InvokeGoToPoint(_goToPointMethod, botOwner, playerPos);
            }
            catch { /* pursuit is best-effort */ }
        }

        /// <summary>
        /// Forces a knife swing on the zombie. The native combat layer returns the
        /// oneMeleeAttack decision but nothing downstream acts on it; calling
        /// MakeKnifeKick() directly triggers the swing animation + enables the
        /// KnifeCollider for the hit-cast. Native damage pipeline handles the rest.
        /// </summary>
        private void TriggerMeleeSwing(Player zombie, Vector3 playerPos)
        {
            if (_nextSwing.TryGetValue(zombie, out var next) && Time.time < next) return;

            try
            {
                var botOwner = zombie.AIData?.BotOwner;
                if (botOwner == null) return;

                ResolveReflection(botOwner);

                var wm = _weaponManagerProp?.GetValue(botOwner);
                var melee = _meleeDataProp?.GetValue(wm);
                var knifeCtrl = _knifeControllerProp?.GetValue(melee);
                if (knifeCtrl == null) return;  // pistol zombie — skip silently

                // Lazy-resolve MakeKnifeKick on first melee zombie. We can't resolve this
                // in ResolveReflection because the first zombie we see might be a pistol
                // zombie (KnifeController is null), and we'd permanently cache a miss.
                if (_makeKnifeKickMethod == null)
                {
                    var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    _makeKnifeKickMethod = knifeCtrl.GetType().GetMethod("MakeKnifeKick", flags, null, Type.EmptyTypes, null);
                    Plugin.Log.LogInfo($"[RoamingZombies] MakeKnifeKick resolved on first melee zombie: {_makeKnifeKickMethod != null}");
                    if (_makeKnifeKickMethod == null) return;
                }

                _nextSwing[zombie] = Time.time + SwingCooldown;

                // Face the target at chest height — KnifeCollider box-cast starts from
                // WeaponRoot (waist-ish) and extends in LookDirection. Aim at torso, not feet.
                var aimPos = playerPos + Vector3.up * ChestHeightOffset;
                FacePoint(botOwner, aimPos);
                _makeKnifeKickMethod.Invoke(knifeCtrl, null);
            }
            catch { /* swing is best-effort */ }
        }

        private static void FacePoint(object botOwner, Vector3 point)
        {
            if (_lookToPointMethod == null || _steeringProp == null) return;
            try
            {
                var steering = _steeringProp.GetValue(botOwner);
                if (steering != null)
                    InvokeWithVector3(_lookToPointMethod, steering, point);
            }
            catch { }
        }

        /// <summary>
        /// Calls a method whose first parameter is Vector3. Fills remaining parameters
        /// with defaults so we work across minor signature variations between EFT versions.
        /// </summary>
        private static void InvokeWithVector3(MethodInfo method, object target, Vector3 point)
        {
            if (method == null || target == null) return;
            var parms = method.GetParameters();
            var args = new object[parms.Length];
            args[0] = point;
            for (int i = 1; i < parms.Length; i++)
                args[i] = parms[i].HasDefaultValue ? parms[i].DefaultValue : Default(parms[i].ParameterType);
            method.Invoke(target, args);
        }

        private static object Default(Type t)
        {
            if (t == typeof(bool))  return false;
            if (t == typeof(float)) return -1f;
            if (t.IsValueType)      return Activator.CreateInstance(t);
            return null;
        }

        /// <summary>
        /// Calls GoToPoint with pathfinding-friendly defaults. Unlike InvokeWithVector3
        /// (bool → false), this defaults bools to TRUE — the multi-param overloads have
        /// flags like "getUpWithCheck", "slowAtTheEnd", "changeNavMeshLink" where false
        /// disables the very pathfinding features we need, reverting the zombie to
        /// straight-line steering and the beelining-into-props bug.
        /// </summary>
        private static void InvokeGoToPoint(MethodInfo method, object target, Vector3 point)
        {
            if (method == null || target == null) return;
            var parms = method.GetParameters();
            var args = new object[parms.Length];
            args[0] = point;
            for (int i = 1; i < parms.Length; i++)
            {
                if (parms[i].HasDefaultValue)
                    args[i] = parms[i].DefaultValue;
                else if (parms[i].ParameterType == typeof(bool))
                    args[i] = true;      // enable pathfinding-feature flags
                else if (parms[i].ParameterType == typeof(float))
                    args[i] = -1f;       // -1 = use bot config default (reach distance, etc.)
                else if (parms[i].ParameterType.IsValueType)
                    args[i] = Activator.CreateInstance(parms[i].ParameterType);
                else
                    args[i] = null;
            }
            method.Invoke(target, args);
        }

        /// <summary>
        /// Resolves the obfuscated EFT API surface on first use against any BotOwner type.
        /// These are type-level reflection handles so one resolution works for every zombie.
        /// Note: MakeKnifeKick is NOT resolved here because KnifeController is null for
        /// pistol zombies (they never draw a knife). If the first zombie we see is a
        /// pistol one we'd cache a miss. That's resolved lazily in TriggerMeleeSwing
        /// from the first real melee zombie.
        /// </summary>
        private static void ResolveReflection(object botOwner)
        {
            if (_reflectionResolved) return;

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            _weaponManagerProp = botOwner.GetType().GetProperty("WeaponManager", flags);
            var wm = _weaponManagerProp?.GetValue(botOwner);
            if (wm != null)
            {
                _meleeDataProp = wm.GetType().GetProperty("Melee", flags);
                var melee = _meleeDataProp?.GetValue(wm);
                _knifeControllerProp = melee?.GetType().GetProperty("KnifeController", flags);
            }

            // Pick the RICHEST GoToPoint overload. BotOwner has several:
            //   (Vector3)                                            <- direct steer, ignores NavMesh
            //   (Vector3, bool, float, ...)                          <- pathfinding-capable
            //   (Vector3, bool, float, bool, bool, bool, ...)        <- fully-featured
            // FirstOrDefault was hitting the 1-param direct-steer variant, which walks zombies
            // in a straight line until they hit a tree/container (v1.2.0 stuck-on-props bug).
            // OrderByDescending(ParamCount) + First picks the pathfinding variant.
            _goToPointMethod = botOwner.GetType().GetMethods(flags)
                .Where(m => m.Name == "GoToPoint"
                         && m.GetParameters().Length >= 1
                         && m.GetParameters()[0].ParameterType == typeof(Vector3))
                .OrderByDescending(m => m.GetParameters().Length)
                .FirstOrDefault();
            if (_goToPointMethod != null)
            {
                var sig = string.Join(", ", _goToPointMethod.GetParameters().Select(p => p.ParameterType.Name));
                Plugin.Log.LogInfo($"[RoamingZombies] GoToPoint overload resolved: ({sig})");
            }

            _steeringProp = botOwner.GetType().GetProperty("Steering", flags);
            var steering = _steeringProp?.GetValue(botOwner);
            if (steering != null)
            {
                _lookToPointMethod = steering.GetType().GetMethods(flags)
                    .FirstOrDefault(m => m.Name == "LookToPoint"
                                      && m.GetParameters().Length >= 1
                                      && m.GetParameters()[0].ParameterType == typeof(Vector3));
            }

            _reflectionResolved = true;
            Plugin.Log.LogInfo(
                $"[RoamingZombies] API resolved — WeaponManager={_weaponManagerProp != null} " +
                $"Melee={_meleeDataProp != null} KnifeCtrl prop={_knifeControllerProp != null} " +
                $"GoToPoint={_goToPointMethod != null} LookToPoint={_lookToPointMethod != null}");
        }

        /// <summary>
        /// Infected bots have Role=infectedX but some code paths check Side. Force Side
        /// to Savage so the bot treats everyone as hostile and doesn't side-filter PMCs.
        /// </summary>
        private static void FixInfectedSide(Player player)
        {
            var info = player?.Profile?.Info;
            if (info == null) return;
            try
            {
                var prop = info.GetType().GetProperty("Side",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop?.CanWrite == true)
                {
                    prop.SetValue(info, EPlayerSide.Savage);
                    return;
                }
                var field = info.GetType()
                    .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(fi => fi.FieldType == typeof(EPlayerSide));
                field?.SetValue(info, EPlayerSide.Savage);
            }
            catch { /* side fix is best-effort */ }
        }

        private void OnHordeSpawned()
        {
            // Skip notification + audio on dedicated headless (no local player to see/hear)
            if (_gameWorld?.MainPlayer == null) return;

            if (Plugin.EnableHordeNotification.Value)
            {
                NotificationManager.DisplayMessageNotification(
                    "The dead are rising... zombies have been spotted nearby.",
                    ENotificationDurationType.Long);
            }

            if (Plugin.EnableHordeAudio.Value && _hordeClip != null)
                PlayClip(_hordeClip);
        }

        private void PlayClip(AudioClip clip)
        {
            var audioObj = new GameObject("ZombieHorde_Audio");
            var source   = audioObj.AddComponent<AudioSource>();
            source.clip         = clip;
            source.volume       = 0.5f;
            source.spatialBlend = 0f;
            source.Play();
            Destroy(audioObj, clip.length + 0.5f);
        }

        private void LoadAudioClip()
        {
            var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string audioPath = null;
            foreach (var ext in new[] { "ogg", "wav" })
            {
                var candidate = Path.Combine(pluginDir, $"horde_alert.{ext}");
                if (File.Exists(candidate)) { audioPath = candidate; break; }
            }
            if (audioPath == null) return;
            StartCoroutine(LoadAudioCoroutine(audioPath));
        }

        private System.Collections.IEnumerator LoadAudioCoroutine(string filePath)
        {
            var audioType = filePath.EndsWith(".ogg") ? AudioType.OGGVORBIS : AudioType.WAV;
            var uri       = "file:///" + filePath.Replace("\\", "/");
            using (var request = UnityWebRequestMultimedia.GetAudioClip(uri, audioType))
            {
                yield return request.SendWebRequest();
                if (request.result == UnityWebRequest.Result.ConnectionError ||
                    request.result == UnityWebRequest.Result.ProtocolError)
                {
                    Plugin.Log.LogError($"[RoamingZombies] Failed to load audio: {request.error}");
                    yield break;
                }
                _hordeClip = DownloadHandlerAudioClip.GetContent(request);
            }
        }

        private void OnDestroy()
        {
            if (_gameWorld != null)
                _gameWorld.OnPersonAdd -= OnPersonAdd;
        }
    }
}
