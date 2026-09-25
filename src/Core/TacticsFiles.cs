namespace Hunted.Core
{
    /// <summary>
    /// Names of the learned-tactics files in the game's ModConfigs folder. The per-slot
    /// files are what the Pursuer learns about one player; the baseline is what the
    /// arena trained against the Stage 2 rules, used as the starting point for a slot
    /// that has no file of its own. "Forget" deletes per-slot files only, so its name
    /// must not match the per-slot pattern.
    /// </summary>
    public static class TacticsFiles
    {
        public const string PerSlotPrefix = "dion_hunted_tactics_";
        public const string PerSlotPattern = PerSlotPrefix + "*.txt";
        public const string Baseline = "dion_hunted_baseline_tactics.txt";

        public static string PerSlot(string slot, string slugcat)
        {
            return PerSlotPrefix + slot + "_" + slugcat + ".txt";
        }
    }
}
