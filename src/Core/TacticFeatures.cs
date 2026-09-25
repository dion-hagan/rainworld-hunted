namespace Hunted.Core
{
    /// <summary>
    /// The situation vector the slugcat Pursuer's tactic policy sees, in order. The game
    /// (<c>PursuerAI.ChooseTactic</c>) and the arena (<c>ArenaBrain.ChooseTactic</c>) both
    /// build it in this order and on these scales; a saved policy is only valid for this
    /// count, which is why <c>TacticPolicy.TryParse</c> checks it.
    /// </summary>
    public static class TacticFeatures
    {
        public static readonly string[] Names =
        {
            "dx",             // target x offset / 400, clamped to [-1, 1]
            "dy",             // target y offset / 400, clamped to [-1, 1]
            "distance",       // target distance / 400, clamped to [0, 1]
            "visual_contact", // 1 when the target is in sight
            "since_seen",     // ticks since last seen / 400, clamped to [0, 1]
            "target_above",   // 1 when the target is more than a tile higher
            "target_armed",   // 1 when the target holds a weapon
            "target_speed",   // target speed / 10, clamped to [0, 1]
            "my_weapon",      // held weapon value / 3 (rock 0.17, spear 0.33, explosive 1)
            "threat",         // threat tracker utility (predators around)
            "lined_up",       // 1 when level, in range and in sight
            "holding_bomb",   // 1 when the weapon is a scavenger bomb
        };

        /// <summary>A constant so the game can use it in constants; a test pins it to the list above.</summary>
        public const int Count = 12;
    }
}
