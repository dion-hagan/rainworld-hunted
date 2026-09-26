using System;

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
            "on_ground",      // 1 when standing on the ground with nothing in progress: a move can start
            "spear_incoming", // 1 when a weapon the target threw is flying at me
            "target_below",   // 1 when the target is more than a tile lower
        };

        /// <summary>A constant so the game can use it in constants; a test pins it to the list above.</summary>
        public const int Count = 15;

        /// <summary>
        /// How close a weapon flying at the body counts as incoming: the range within which a
        /// belly slide started on sight is flat before the weapon arrives (a level throw drops
        /// onto a flat body from about 240 px, and the slide's crouch takes eight ticks of a
        /// 40 px per tick flight).
        /// </summary>
        public const float IncomingRangePx = 230f;
        /// <summary>A weapon further above or below the main chunk than this is not aimed at the body.</summary>
        public const float IncomingBandPx = 60f;

        /// <summary>
        /// The spear_incoming predicate, shared by the game and the arena: <paramref name="dx"/>,
        /// <paramref name="dy"/> is the body's main chunk relative to the weapon, and
        /// <paramref name="vx"/>, <paramref name="vy"/> the weapon's velocity. True when the weapon
        /// is within range, within the height band, and moving toward the body.
        /// </summary>
        public static bool Incoming(float dx, float dy, float vx, float vy)
        {
            if (Math.Abs(dy) > IncomingBandPx || dx * dx + dy * dy > IncomingRangePx * IncomingRangePx)
            {
                return false;
            }
            return dx * vx + dy * vy > 0f;
        }
    }
}
