using System;
using BepInEx;
using Hunted.Game;
using Menu.Remix.MixedUI;

namespace Hunted
{
    [BepInPlugin(MOD_ID, "Hunted", VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string MOD_ID = "dion_hunted";
        public const string VERSION = "0.1.0";

        private bool initialized;

        public void OnEnable()
        {
            On.RainWorld.OnModsInit += RainWorld_OnModsInit;
        }

        public void OnDisable()
        {
            On.RainWorld.OnModsInit -= RainWorld_OnModsInit;
        }

        private void RainWorld_OnModsInit(On.RainWorld.orig_OnModsInit orig, RainWorld self)
        {
            orig(self);
            if (initialized)
            {
                return;
            }
            initialized = true;
            try
            {
                Options.Instance = new Options();
                MachineConnector.SetRegisteredOI(MOD_ID, Options.Instance);
                Hooks.Apply();
                HuntedLog.Info("Hunted " + VERSION + " initialized.");
            }
            catch (Exception e)
            {
                HuntedLog.Error("Hunted failed to initialize", e);
            }
        }
    }
}
