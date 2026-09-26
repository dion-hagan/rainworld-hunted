namespace Hunted.Core.Arena
{
    /// <summary>
    /// A thrown weapon in flight, with the game's numbers: <c>Weapon.Thrown</c> gives it 40 px
    /// per tick along the throw direction, and a thrown spear (<c>Spear.Update</c>) feels half
    /// gravity (0.9 down, 0.45 back), so it drops about 13 px over 300 px and 38 px over 520.
    /// Collisions are resolved by the match, which knows both fighters.
    /// </summary>
    public sealed class Projectile
    {
        /// <summary>Pixels per tick along the throw (Weapon.Thrown: 40 * force, force 1).</summary>
        public const float Speed = 40f;
        /// <summary>Net downward acceleration while thrown (Spear: gravity 0.9 minus 0.45 added back each tick).</summary>
        public const float Drop = 0.45f;

        public readonly WeaponKind Kind;
        public readonly Fighter Thrower;
        public Vec2 Pos;
        public Vec2 Vel;
        public int Age;
        /// <summary>A weapon thrown straight down out of a flip passes through one-way platforms (the game sets goThroughFloors on it); one thrown up passes them from below anyway.</summary>
        public bool ThroughPlatforms;

        public Projectile(WeaponKind kind, Fighter thrower, Vec2 pos, Vec2 vel, bool throughPlatforms = false)
        {
            Kind = kind;
            Thrower = thrower;
            Pos = pos;
            Vel = vel;
            ThroughPlatforms = throughPlatforms;
        }

        /// <summary>Advances one tick and returns where it was before, for the sweep test.</summary>
        public Vec2 Advance()
        {
            Vec2 prev = Pos;
            Age++;
            Vel.Y -= Drop;
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
