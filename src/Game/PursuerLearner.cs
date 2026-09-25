using System;
using System.IO;
using Hunted.Core;
using RWCustom;

namespace Hunted.Game
{
    /// <summary>
    /// The learning half of the slugcat Pursuer: owns the <see cref="TacticPolicy"/>, turns
    /// game events into rewards and persists what was learned per save slot and slugcat.
    /// Learned tactics deliberately live outside the death-persistent save data, so they
    /// survive deaths, quits and new campaigns on the same slot: they describe the player,
    /// not the run.
    /// </summary>
    public sealed class PursuerLearner
    {
        public const int FeatureCount = 12;
        private const string FilePrefix = "dion_hunted_tactics_";

        public TacticPolicy Policy { get; private set; }
        public int Tick { get; private set; }

        /// <summary>Learning only applies to the slugcat body, and only when the option is on.</summary>
        public bool Enabled => (Options.Instance == null || Options.Instance.AdaptiveTactics.Value) && PursuerBodies.Selected() == PursuerBody.Slugcat;

        private readonly string path;
        private readonly int seed;
        private bool dirty;

        public PursuerLearner(RainWorldGame game, SaveState saveState)
        {
            seed = saveState.seed;
            string slot = game.rainWorld.options.saveSlot.ToString();
            string slugcat = saveState.saveStateNumber != null ? saveState.saveStateNumber.value : "unknown";
            path = Path.Combine(Folder(), FilePrefix + slot + "_" + slugcat + ".txt");
            Load();
        }

        private static string Folder()
        {
            return Path.Combine(Custom.RootFolderDirectory(), "ModConfigs");
        }

        private void Load()
        {
            try
            {
                if (File.Exists(path) && TacticPolicy.TryParse(File.ReadAllText(path), FeatureCount, seed, out TacticPolicy loaded))
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
                Policy.Flush();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, Policy.Serialize());
                dirty = false;
            }
            catch (Exception e)
            {
                HuntedLog.Error("Could not save learned tactics", e);
            }
        }

        /// <summary>Throws away everything learned for this save slot and slugcat.</summary>
        public void Forget()
        {
            Policy = new TacticPolicy(FeatureCount, seed);
            dirty = true;
            Save();
            HuntedLog.Info("[test] Learned tactics reset.");
        }

        /// <summary>Deletes every learned-tactics file, for the Remix button (usable from the main menu).</summary>
        public static int ForgetAll()
        {
            int deleted = 0;
            try
            {
                if (Directory.Exists(Folder()))
                {
                    foreach (string file in Directory.GetFiles(Folder(), FilePrefix + "*.txt"))
                    {
                        File.Delete(file);
                        deleted++;
                    }
                }
                HuntedLog.Info("Deleted " + deleted + " learned-tactics file(s).");
            }
            catch (Exception e)
            {
                HuntedLog.Error("Could not delete learned tactics", e);
            }
            return deleted;
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

        public void NoteThrow()
        {
            Policy.NoteThrow();
        }

        public void Reward(float value, string why, bool throwOutcome = false)
        {
            if (!Enabled)
            {
                return;
            }
            Policy.Reward(value, Tick, throwOutcome);
            dirty = true;
            HuntedLog.Info("Tactic reward " + value.ToString("+0.0;-0.0") + " (" + why + ")");
        }
    }
}
