using System.Collections.Generic;

namespace Hunted.Core
{
    /// <summary>
    /// Item codes the Pursuer's inventory is stored as, how they rank, and what a
    /// slugcat body can carry: two hands plus a spear on the back, like Hunter.
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

        /// <summary>Two hands, like a slugcat. A hand holds anything, but only one hand can hold a spear.</summary>
        public const int Hands = 2;
        /// <summary>One spear rides on the back, like Hunter's.</summary>
        public const int BackSpears = 1;
        /// <summary>A slugcat holds one spear in its hands; the other hand takes a rock, bomb or lantern.</summary>
        public const int HandSpears = 1;
        /// <summary>Everything the body can carry at once: both hands and the back.</summary>
        public const int MaxHeld = Hands + BackSpears;

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

        public static bool IsSpear(string item)
        {
            return item == Spear || item == ExplosiveSpear || item == ElectricSpear;
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

        /// <summary>
        /// True when the body can carry all of <paramref name="items"/> at once: at most
        /// <see cref="Hands"/> things that are not spears, at most one spear per hand slot
        /// allowed plus one on the back, and no more than <see cref="MaxHeld"/> in total.
        /// </summary>
        public static bool CanCarry(IEnumerable<string> items)
        {
            int count = 0;
            int spears = 0;
            if (items != null)
            {
                foreach (string item in items)
                {
                    count++;
                    if (IsSpear(item))
                    {
                        spears++;
                    }
                }
            }
            return count <= MaxHeld && spears <= HandSpears + BackSpears && count - spears <= Hands;
        }

        /// <summary>True when <paramref name="item"/> can be carried on top of <paramref name="inventory"/> without dropping anything.</summary>
        public static bool Fits(IEnumerable<string> inventory, string item)
        {
            var items = new List<string>();
            if (inventory != null)
            {
                items.AddRange(inventory);
            }
            items.Add(item);
            return CanCarry(items);
        }

        /// <summary>
        /// Adds an item. When it does not fit, the weakest item whose place it can take is
        /// dropped for it. Returns the item that was left behind (the new one when nothing
        /// it could replace is weaker), or null when nothing was dropped.
        /// </summary>
        public static string Add(List<string> inventory, string item)
        {
            if (Fits(inventory, item))
            {
                inventory.Add(item);
                return null;
            }
            int worstIdx = -1;
            for (int i = 0; i < inventory.Count; i++)
            {
                if (worstIdx != -1 && TierOf(inventory[i]) >= TierOf(inventory[worstIdx]))
                {
                    continue;
                }
                var trial = new List<string>(inventory);
                trial[i] = item;
                if (CanCarry(trial))
                {
                    worstIdx = i;
                }
            }
            if (worstIdx == -1 || TierOf(inventory[worstIdx]) >= TierOf(item))
            {
                return item;
            }
            string dropped = inventory[worstIdx];
            inventory[worstIdx] = item;
            return dropped;
        }

        /// <summary>The best items of <paramref name="inventory"/> the body can carry at once, best first.</summary>
        public static List<string> TrimToCarryable(IEnumerable<string> inventory)
        {
            var sorted = new List<string>();
            if (inventory != null)
            {
                sorted.AddRange(inventory);
            }
            sorted.Sort((a, b) => TierOf(b).CompareTo(TierOf(a)));
            var kept = new List<string>();
            foreach (string item in sorted)
            {
                if (Fits(kept, item))
                {
                    kept.Add(item);
                }
            }
            return kept;
        }

        /// <summary>
        /// Which of the carried items rides on the back: the weakest spear, whenever the
        /// hands alone could not hold everything (a second spear, or a third item). Null
        /// when everything fits in the hands. Returns an index into <paramref name="inventory"/>.
        /// </summary>
        public static int BackSpearIndex(IList<string> inventory)
        {
            if (inventory == null)
            {
                return -1;
            }
            int spears = 0;
            int worst = -1;
            for (int i = 0; i < inventory.Count; i++)
            {
                if (!IsSpear(inventory[i]))
                {
                    continue;
                }
                spears++;
                if (worst == -1 || TierOf(inventory[i]) < TierOf(inventory[worst]))
                {
                    worst = i;
                }
            }
            if (worst == -1 || (spears <= HandSpears && inventory.Count <= Hands))
            {
                return -1;
            }
            return worst;
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
