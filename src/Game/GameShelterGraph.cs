using System;
using System.Collections.Generic;
using System.IO;
using Hunted.Core;

namespace Hunted.Game
{
    /// <summary>
    /// Builds the shelter graph for the current campaign from the game's world
    /// files, resolved through AssetManager so merged/modded regions count.
    /// </summary>
    public static class GameShelterGraph
    {
        /// <summary>Every region acronym the base game or its DLC can ever use; anything else is a mod region.</summary>
        private static readonly HashSet<string> KnownGameRegions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SU", "HI", "DS", "CC", "GW", "SH", "SL", "SI", "LF", "UW", "SS", "SB",
            "VS", "LM", "RM", "UG", "CL", "HR", "DM", "LC", "OE", "MS",
        };

        public static ShelterGraph Build(SlugcatStats.Name slugcat)
        {
            SlugcatStats.Timeline timeline = SlugcatStats.SlugcatToTimeline(slugcat);
            string timelineName = timeline?.value ?? slugcat.value;

            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string r in SlugcatStats.SlugcatStoryRegions(slugcat))
            {
                allowed.Add(r);
            }
            foreach (string r in SlugcatStats.SlugcatOptionalRegions(slugcat))
            {
                allowed.Add(r);
            }

            var acronyms = new List<string>();
            string regionsPath = AssetManager.ResolveFilePath("World" + Path.DirectorySeparatorChar + "regions.txt");
            if (File.Exists(regionsPath))
            {
                foreach (string line in File.ReadAllLines(regionsPath))
                {
                    string acr = line.Trim().ToUpperInvariant();
                    if (acr.Length > 0 && !acronyms.Contains(acr))
                    {
                        acronyms.Add(acr);
                    }
                }
            }

            var regions = new List<ParsedRegion>();
            var skipped = new List<string>();
            foreach (string acr in acronyms)
            {
                bool dlcOnly = KnownGameRegions.Contains(acr) || IsWatcherRegion(acr);
                if (dlcOnly && !allowed.Contains(acr))
                {
                    skipped.Add(acr);
                    continue;
                }
                string proper;
                try
                {
                    proper = Region.GetProperRegionAcronym(timeline, acr);
                }
                catch (Exception e)
                {
                    HuntedLog.Warn("GetProperRegionAcronym failed for " + acr + ": " + e.Message);
                    proper = acr;
                }
                if (!string.Equals(proper, acr, StringComparison.OrdinalIgnoreCase))
                {
                    skipped.Add(acr + "->" + proper);
                    continue;
                }
                string lower = acr.ToLowerInvariant();
                string path = AssetManager.ResolveFilePath("World" + Path.DirectorySeparatorChar + lower + Path.DirectorySeparatorChar + "world_" + lower + ".txt");
                if (!File.Exists(path))
                {
                    skipped.Add(acr + "(no file)");
                    continue;
                }
                try
                {
                    regions.Add(WorldFileParser.Parse(acr, File.ReadAllLines(path), timelineName, cond => true));
                }
                catch (Exception e)
                {
                    HuntedLog.Warn("Could not parse world file for " + acr + ": " + e.Message);
                }
            }

            ShelterGraph graph = ShelterGraphBuilder.Build(regions);
            HuntedLog.Info("Shelter graph for " + slugcat.value + " (" + timelineName + "): " + regions.Count + " regions, " + graph.RoomCount + " rooms, " + graph.Nodes.Count + " shelters. Skipped: " + string.Join(", ", skipped.ToArray()));
            return graph;
        }

        private static bool IsWatcherRegion(string acr)
        {
            return acr.Length == 4 && acr[0] == 'W';
        }
    }
}
