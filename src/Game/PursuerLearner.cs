using System;
using System.IO;
using Hunted.Core;
using RWCustom;

namespace Hunted.Game
{
    /// <summary>
    /// The learning half of the slugcat Pursuer: owns the <see cref="TacticPolicy"/>, turns
    /// game events into rewards and persists what was learned per save slot and campaign.
    /// Learned tactics deliberately live outside the death-persistent save data, so they
    /// survive deaths and quits instead of reverting with karma.
    /// </summary>
    public sealed class PursuerLearner
    {
        public const int FeatureCount = 12;

        public TacticPolicy Policy { get; private set; }
        public int Tick { get; private set; }
        public bool Enabled => Options.Instance == null || Options.Instance.AdaptiveTactics.Value;

        private readonly string path;
        private readonly int seed;
        private bool dirty;

        public PursuerLearner(RainWorldGame game, SaveState saveState)
        {
            seed = saveState.seed;
            string slot = game.rainWorld.options.saveSlot.ToString();
            string campaign = saveState.saveStateNumber != null ? saveState.saveStateNumber.value : "unknown";
            path = Path.Combine(Path.Combine(Custom.RootFolderDirectory(), "ModConfigs"), "dion_hunted_tactics_" + slot + "_" + campaign + ".txt");
            Load();
        }

        private void Load()
        {
            try
            {
                if (File.Exists(path) && TacticPolicy.TryParse(File.ReadAllText(path), seed, out TacticPolicy loaded))
                {
                    Policy = loaded;
                    HuntedLog.Info("Loaded learned tactics: " + loaded.Decisions + " decisions, " + loaded.Rewards + " rewards, total " + loaded.TotalReward.ToString("0.0") + ".");
                    return;
                }
            }
            catch (Exception e)
            {
                HuntedLog.Error("Could not read learned tactics; starting fresh", e);
            }
            Policy = new TacticPolicy(FeatureCount, seed);
        }

        public void Save()
        {
            if (!dirty)
            {
                return;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, Policy.Serialize());
                dirty = false;
            }
            catch (Exception e)
            {
                HuntedLog.Error("Could not save learned tactics", e);
            }
        }

        /// <summary>Throws away everything learned for this campaign.</summary>
        public void Forget()
        {
            Policy = new TacticPolicy(FeatureCount, seed);
            dirty = true;
            Save();
            HuntedLog.Info("[test] Learned tactics reset.");
        }

        public void Advance()
        {
            Tick++;
        }

        public Tactic Choose(float[] features)
        {
            dirty = true;
            return Policy.Choose(features, Tick);
        }

        public void Reward(float value, string why)
        {
            if (!Enabled)
            {
                return;
            }
            Policy.Reward(value, Tick);
            dirty = true;
            HuntedLog.Info("Tactic reward " + value.ToString("+0.0;-0.0") + " (" + why + ")");
        }

        public string Summary()
        {
            return Policy.Decisions + " decisions, reward " + Policy.TotalReward.ToString("0.0");
        }
    }
}
