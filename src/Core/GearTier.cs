using System.Collections.Generic;

namespace Hunted.Core
{
    /// <summary>
    /// Item codes the Pursuer's inventory is stored as, and how they rank.
    /// Codes double as save-string tokens, so never rename them.
    /// </summary>
    public static class GearTier
    {
        public const string Rock = "Rock";
        public const string Spear = "Spear";
        public const string ExplosiveSpear = "ExplosiveSpear";
        public const string ElectricSpear = "ElectricSpear";
        public const string ScavengerBomb = "ScavengerBomb";
        public const string Lantern = "Lantern";

        /// <summary>Two hands, like a slugcat.</summary>
        public const int MaxHeld = 2;

        public static int TierOf(string item)
        {
            switch (item)
            {
                case Rock: return 1;
                case Spear: return 2;
                case ElectricSpear: return 3;
                case ExplosiveSpear: return 3;
                case ScavengerBomb: return 4;
                default: return 0;
            }
        }

        public static int BestTier(IEnumerable<string> inventory)
        {
            int best = 0;
            if (inventory == null)
            {
                return best;
            }
            foreach (string item in inventory)
            {
                int tier = TierOf(item);
                if (tier > best)
                {
                    best = tier;
                }
            }
            return best;
        }

        /// <summary>Adds an item, dropping the weakest one when both hands are full. Returns the item that was left behind, or null.</summary>
        public static string Add(List<string> inventory, string item)
        {
            if (inventory.Count < MaxHeld)
            {
                inventory.Add(item);
                return null;
            }
            int worstIdx = 0;
            for (int i = 1; i < inventory.Count; i++)
            {
                if (TierOf(inventory[i]) < TierOf(inventory[worstIdx]))
                {
                    worstIdx = i;
                }
            }
            if (TierOf(inventory[worstIdx]) >= TierOf(item))
            {
                return item;
            }
            string dropped = inventory[worstIdx];
            inventory[worstIdx] = item;
            return dropped;
        }

        public static string Describe(IEnumerable<string> inventory)
        {
            if (inventory == null)
            {
                return "nothing";
            }
            var parts = new List<string>();
            foreach (string item in inventory)
            {
                parts.Add(item);
            }
            return parts.Count == 0 ? "nothing" : string.Join(", ", parts.ToArray());
        }
    }
}
