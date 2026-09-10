using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using ZombieHorde.Client.Patches;

namespace ZombieHorde.Client
{
    [BepInPlugin("com.vonbraunz.roamingzombies", "Roaming Zombies", "1.3.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource Log;

        // F12 ConfigurationManager-visible settings.
        public static ConfigEntry<bool> EnableHordeNotification;
        public static ConfigEntry<bool> EnableHordeAudio;

        private void Awake()
        {
            Log = Logger;

            BindConfig();

            new RaidStartPatch().Enable();
            new InfectedSidePatch().Enable();

            Logger.LogInfo("[RoamingZombies] Client loaded — patches active");
        }

        private void BindConfig()
        {
            const string notif = "Notifications";

            EnableHordeNotification = Config.Bind(
                notif, "Show Horde Spawn Alert", true,
                "Display a UI notification when zombies are first detected in the raid.");

            EnableHordeAudio = Config.Bind(
                notif, "Play Horde Audio", true,
                "Play horde_alert.ogg/.wav (if present in the plugin folder) when zombies are first detected.");
        }
    }
}
