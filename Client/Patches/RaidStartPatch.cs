using System.Reflection;
using EFT;
using SPT.Reflection.Patching;
using HarmonyLib;

namespace ZombieHorde.Client.Patches
{
    public class RaidStartPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return typeof(GameWorld).GetMethod(nameof(GameWorld.OnGameStarted), BindingFlags.Public | BindingFlags.Instance);
        }

        [PatchPrefix]
        public static void PatchPrefix()
        {
            ZombieSpawnDetector.Init();
        }
    }
}
