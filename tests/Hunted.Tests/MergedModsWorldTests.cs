using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hunted.Core;
using Xunit;
using Xunit.Abstractions;

namespace Hunted.Tests
{
    /// <summary>
    /// Builds the Survivor shelter graph from the installed game's merged mod
    /// folder (every enabled region mod included), mirroring the region filter
    /// the mod applies in-game, and reports what modded regions do to it.
    /// Skips quietly when the game is not installed.
    /// </summary>
    public class MergedModsWorldTests
    {
        private readonly ITestOutputHelper output;

        public MergedModsWorldTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private static readonly HashSet<string> KnownGameRegions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SU", "HI", "DS", "CC", "GW", "SH", "SL", "SI", "LF", "UW", "SS", "SB",
            "VS", "LM", "RM", "UG", "CL", "HR", "DM", "LC", "OE", "MS",
        };

        // SlugcatStats.SlugcatStoryRegions(White) + SlugcatOptionalRegions(White) with MSC on.
        private static readonly HashSet<string> SurvivorRegions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SU", "HI", "DS", "CC", "GW", "SH", "VS", "SL", "SI", "LF", "UW", "SS", "SB", "OE", "MS",
        };

        private static string MergedWorldFolder()
        {
            string root = Environment.GetEnvironmentVariable("RAINWORLD_PATH");
            if (string.IsNullOrEmpty(root))
            {
                root = @"C:\Program Files (x86)\Steam\steamapps\common\Rain World";
            }
            string merged = Path.Combine(root, "RainWorld_Data", "StreamingAssets", "mergedmods", "world");
            return File.Exists(Path.Combine(merged, "regions.txt")) ? merged : null;
        }

        [Fact]
        public void SurvivorGraphWithRegionModsStaysSaneAndConnected()
        {
            string world = MergedWorldFolder();
            if (world == null)
            {
                output.WriteLine("No merged mod world folder; skipping.");
                return;
            }
            var regions = new List<ParsedRegion>();
            var skipped = new List<string>();
            foreach (string raw in File.ReadAllLines(Path.Combine(world, "regions.txt")))
            {
                string acr = raw.Trim().ToUpperInvariant();
                if (acr.Length == 0)
                {
                    continue;
                }
                bool dlcOnly = KnownGameRegions.Contains(acr) || (acr.Length == 4 && acr[0] == 'W');
                if (dlcOnly && !SurvivorRegions.Contains(acr))
                {
                    skipped.Add(acr);
                    continue;
                }
                string path = Path.Combine(world, acr.ToLowerInvariant(), "world_" + acr.ToLowerInvariant() + ".txt");
                if (!File.Exists(path))
                {
                    skipped.Add(acr + "(no file)");
                    continue;
                }
                regions.Add(WorldFileParser.Parse(acr, File.ReadAllLines(path), "White"));
            }
            output.WriteLine("skipped: " + string.Join(", ", skipped));

            ShelterGraph g = ShelterGraphBuilder.Build(regions);
            output.WriteLine("regions: " + regions.Count + ", rooms: " + g.RoomCount + ", shelters: " + g.Nodes.Count);

            ShelterNode start = g.Get("SU_S01");
            Assert.NotNull(start);
            Dictionary<ShelterNode, PathInfo> reach = g.Dijkstra(start);

            foreach (var byRegion in g.Nodes.GroupBy(n => n.Region).OrderBy(x => x.Key))
            {
                int reachable = byRegion.Count(n => reach.ContainsKey(n));
                var hops = byRegion.Where(n => reach.ContainsKey(n)).Select(n => reach[n].Hops).ToList();
                output.WriteLine(byRegion.Key.PadRight(5) + " shelters " + byRegion.Count().ToString().PadLeft(3) + ", reachable " + reachable.ToString().PadLeft(3) + (hops.Count > 0 ? ", hops " + hops.Min() + ".." + hops.Max() : ""));
            }

            // Every shelter must be a dead end or nearly so; a shelter with many room connections
            // would mean a region file uses SHELTER on a normal room.
            var busy = g.Nodes.Where(n => g.RoomNeighbors(n.Name).Count > 2).Select(n => n.Name + "(" + g.RoomNeighbors(n.Name).Count + ")").ToList();
            output.WriteLine("shelters with >2 room connections: " + string.Join(", ", busy));

            double avgDegree = g.Nodes.Where(n => reach.ContainsKey(n)).Average(n => n.Edges.Count);
            int maxHops = reach.Values.Max(p => p.Hops);
            output.WriteLine("reachable shelters: " + reach.Count + "/" + g.Nodes.Count + ", average degree " + avgDegree.ToString("0.0") + ", farthest " + maxHops + " hops");

            Assert.True(reach.Count >= 88, "the vanilla shelters must stay reachable; reachable = " + reach.Count);
            Assert.True(avgDegree < 8, "graph too dense: " + avgDegree);
            Assert.True(maxHops >= 8, "graph too shallow: " + maxHops);
            Assert.True(g.RoomNeighbors("SU_S01").Count == 1, "SU_S01 should be a dead end");
        }
    }
}
