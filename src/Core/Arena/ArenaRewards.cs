using System;

namespace Hunted.Core.Arena
{
    /// <summary>
    /// The learner's rewards and the arena's damage model. The reward values are the
    /// ones the game hooks use (<c>Hooks.Creature_Violence</c>, <c>Hooks.Weapon_HitWall</c>,
    /// <c>HuntedSession.OnPlayerDied</c> and <c>OnPursuerDied</c>), so a policy trained here
    /// is trained on the same scale it keeps learning on in the game. Change them together.
    /// </summary>
    public static class ArenaRewards
    {
        public const float Kill = 3f;
        public const float Death = -3f;
        public const float WallHit = -0.2f;

        /// <summary>A spear in the chest is worth two, a stunning rock half a point (the game's rule).</summary>
        public static float ForHit(float damage)
        {
            return damage >= 1f ? 2f : damage >= 0.5f ? 1f : 0.5f;
        }

        /// <summary>Proportionate to the damage taken, between a quarter and a full point (the game's rule).</summary>
        public static float ForHurt(float damage)
        {
            return -Math.Max(0.25f, Math.Min(1f, damage));
        }

        /// <summary>
        /// Damage dealt by a hit. A slugcat has one point of health, and a spear does not
        /// always kill outright, so its damage straddles one; rocks only scratch and stun.
        /// </summary>
        public static float Damage(WeaponKind kind, Random rng)
        {
            switch (kind)
            {
                case WeaponKind.Rock: return 0.2f;
                case WeaponKind.Spear: return 0.6f + 0.8f * (float)rng.NextDouble();
                case WeaponKind.ExplosiveSpear: return 2f;
                default: return 0f;
            }
        }

        /// <summary>Ticks the victim cannot act after a hit.</summary>
        public static int StunTicks(WeaponKind kind)
        {
            switch (kind)
            {
                case WeaponKind.Rock: return 80;
                case WeaponKind.Spear: return 30;
                case WeaponKind.ExplosiveSpear: return 60;
                default: return 0;
            }
        }

        /// <summary>Whether the weapon is still usable after hitting a fighter (an explosive spear is spent).</summary>
        public static bool SurvivesHit(WeaponKind kind)
        {
            return kind != WeaponKind.ExplosiveSpear;
        }
    }
}
