namespace Hunted.Core.Arena
{
    /// <summary>
    /// A thrown weapon in flight. Slugcat throws are horizontal; the weapon flies straight
    /// for a while and then drops, like a spear in the game. Collisions are resolved by
    /// the match, which knows both fighters.
    /// </summary>
    public sealed class Projectile
    {
        /// <summary>Pixels per tick. A spear crosses a screen in well under a second.</summary>
        public const float Speed = 40f;
        /// <summary>Ticks of level flight before gravity takes over (about 560 px, a little past the AI's throw range).</summary>
        public const int StraightTicks = 14;

        public readonly WeaponKind Kind;
        public readonly Fighter Thrower;
        public Vec2 Pos;
        public Vec2 Vel;
        public int Age;

        public Projectile(WeaponKind kind, Fighter thrower, Vec2 pos, Vec2 vel)
        {
            Kind = kind;
            Thrower = thrower;
            Pos = pos;
            Vel = vel;
        }

        /// <summary>Advances one tick and returns where it was before, for the sweep test.</summary>
        public Vec2 Advance()
        {
            Vec2 prev = Pos;
            Age++;
            if (Age > StraightTicks)
            {
                Vel.Y -= Fighter.Gravity;
            }
            Pos += Vel;
            return prev;
        }
    }

    /// <summary>A weapon lying on a surface, waiting to be picked up.</summary>
    public sealed class GroundItem
    {
        public readonly WeaponKind Kind;
        public Vec2 Pos;

        public GroundItem(WeaponKind kind, Vec2 pos)
        {
            Kind = kind;
            Pos = pos;
        }
    }
}
