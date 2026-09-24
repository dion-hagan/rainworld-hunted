using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Hunted.Core
{
    public enum PursuerStatus
    {
        /// <summary>Tracked as data, moving along the shelter graph.</summary>
        Traveling,
        /// <summary>In (or next to) the player's region; realized as a creature when that region is loaded.</summary>
        Arrived,
        /// <summary>Killed; waits for RespawnCycle.</summary>
        Dead,
    }

    /// <summary>
    /// Everything about the Pursuer that survives between cycles. Serialized
    /// into the death-persistent save data as one "key=value|..." string, so
    /// it must never contain the game's own separators (angle-bracket tags).
    /// </summary>
    public sealed class PursuerState
    {
        public const int CurrentVersion = 1;

        public int Version = CurrentVersion;
        public PursuerStatus Status = PursuerStatus.Traveling;
        public string Region;
        public string Shelter;
        public List<string> Inventory = new List<string>();
        public int RespawnCycle = -1;
        public int PlayerKills;
        public int ScavKills;
        public int Seed;
        /// <summary>Last room the Pursuer was known to be in while it existed in the player's region.</summary>
        public string LastRoom;
        public int CyclesTracked;

        public PursuerState Clone()
        {
            return new PursuerState
            {
                Version = Version,
                Status = Status,
                Region = Region,
                Shelter = Shelter,
                Inventory = new List<string>(Inventory),
                RespawnCycle = RespawnCycle,
                PlayerKills = PlayerKills,
                ScavKills = ScavKills,
                Seed = Seed,
                LastRoom = LastRoom,
                CyclesTracked = CyclesTracked,
            };
        }

        public string Serialize()
        {
            var sb = new StringBuilder();
            Append(sb, "v", Version.ToString(CultureInfo.InvariantCulture));
            Append(sb, "st", Status.ToString());
            Append(sb, "rg", Region ?? "");
            Append(sb, "sh", Shelter ?? "");
            Append(sb, "inv", string.Join(",", Inventory.ToArray()));
            Append(sb, "rc", RespawnCycle.ToString(CultureInfo.InvariantCulture));
            Append(sb, "pk", PlayerKills.ToString(CultureInfo.InvariantCulture));
            Append(sb, "sk", ScavKills.ToString(CultureInfo.InvariantCulture));
            Append(sb, "sd", Seed.ToString(CultureInfo.InvariantCulture));
            Append(sb, "lr", LastRoom ?? "");
            Append(sb, "ct", CyclesTracked.ToString(CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static void Append(StringBuilder sb, string key, string value)
        {
            if (sb.Length > 0)
            {
                sb.Append('|');
            }
            sb.Append(key).Append('=').Append(Sanitize(value));
        }

        private static string Sanitize(string value)
        {
            return value.Replace("|", "").Replace("<", "").Replace(">", "").Replace("=", "");
        }

        public static bool TryParse(string text, out PursuerState state)
        {
            state = null;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            var result = new PursuerState();
            bool sawVersion = false;
            foreach (string pair in text.Split('|'))
            {
                int eq = pair.IndexOf('=');
                if (eq < 0)
                {
                    continue;
                }
                string key = pair.Substring(0, eq);
                string value = pair.Substring(eq + 1);
                switch (key)
                {
                    case "v":
                        sawVersion = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result.Version);
                        break;
                    case "st":
                        if (!Enum.TryParse(value, out result.Status))
                        {
                            result.Status = PursuerStatus.Traveling;
                        }
                        break;
                    case "rg": result.Region = value.Length == 0 ? null : value; break;
                    case "sh": result.Shelter = value.Length == 0 ? null : value; break;
                    case "inv":
                        result.Inventory = new List<string>();
                        foreach (string item in value.Split(','))
                        {
                            if (item.Length > 0)
                            {
                                result.Inventory.Add(item);
                            }
                        }
                        break;
                    case "rc": int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result.RespawnCycle); break;
                    case "pk": int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result.PlayerKills); break;
                    case "sk": int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result.ScavKills); break;
                    case "sd": int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result.Seed); break;
                    case "lr": result.LastRoom = value.Length == 0 ? null : value; break;
                    case "ct": int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result.CyclesTracked); break;
                }
            }
            if (!sawVersion || result.Shelter == null)
            {
                return false;
            }
            state = result;
            return true;
        }

        public override string ToString()
        {
            return Status + " @ " + Region + "/" + Shelter + " holding " + GearTier.Describe(Inventory);
        }
    }
}
