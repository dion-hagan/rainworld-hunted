using System;

namespace Hunted.Core.Arena
{
    /// <summary>What a fighter can hold in the arena. Values follow <c>PursuerAI.WeaponValue</c>.</summary>
    public enum WeaponKind
    {
        None = 0,
        Rock = 1,
        Spear = 2,
        ExplosiveSpear = 3,
    }

    /// <summary>
    /// Everything that shapes one arena: the room, the fighters' gear, how long an
    /// encounter may last and how the scripted opponent behaves. Plain data so a run
    /// can be reproduced from its config and seed.
    /// </summary>
    public sealed class ArenaConfig
    {
        /// <summary>Ticks per encounter before it is called a draw (40 ticks per second, as in the game).</summary>
        public int MaxTicks = 40 * 60;
        /// <summary>What the learner (the Stage 3 Pursuer) starts each encounter holding.</summary>
        public WeaponKind LearnerGear = WeaponKind.Spear;
        /// <summary>What the scripted opponent (the Stage 2 rules) starts each encounter holding.</summary>
        public WeaponKind OpponentGear = WeaponKind.Spear;
        /// <summary>Spears lying around the room at the start of each encounter, so a miss is not the end of the fight.</summary>
        public int SpareSpears = 3;
        /// <summary>Minimum distance between the fighters when an encounter starts.</summary>
        public float StartSeparation = 300f;
        /// <summary>
        /// Chance per tick that the opponent jumps when a weapon is flying at it. Zero is the
        /// Stage 2 AI as it is in the game (it never dodges); a small value makes it more
        /// like a person.
        /// </summary>
        public float OpponentDodgeChance = 0f;
        /// <summary>Generate a fresh room layout for every encounter instead of the fixed default room.</summary>
        public bool RandomRooms = true;

        public ArenaConfig Clone()
        {
            return (ArenaConfig)MemberwiseClone();
        }

        public static WeaponKind ParseGear(string text)
        {
            switch ((text ?? "").Trim().ToLowerInvariant())
            {
                case "none": case "nothing": case "": return WeaponKind.None;
                case "rock": return WeaponKind.Rock;
                case "spear": return WeaponKind.Spear;
                case "explosive": case "explosivespear": case "explosive-spear": return WeaponKind.ExplosiveSpear;
                default: throw new ArgumentException("Unknown gear '" + text + "' (none, rock, spear, explosive)");
            }
        }

        /// <summary>Same scale as <c>PursuerAI.WeaponValue</c>: the learner's ninth feature is this over three.</summary>
        public static float WeaponValue(WeaponKind kind)
        {
            switch (kind)
            {
                case WeaponKind.Rock: return 0.5f;
                case WeaponKind.Spear: return 1f;
                case WeaponKind.ExplosiveSpear: return 3f;
                default: return 0f;
            }
        }
    }
}
