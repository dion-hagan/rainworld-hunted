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
    /// Builds the shelter graph from a real Rain World install when one is
    /// present (RAINWORLD_PATH or the default Steam folder). Skips quietly
    /// otherwise, so CI without the game still passes.
    /// </summary>
    public class InstalledGameWorldTests
    {
        private readonly ITestOutputHelper output;

        public InstalledGameWorldTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private static string WorldFolder()
        {
            string root = Environment.GetEnvironmentVariable("RAINWORLD_PATH");
            if (string.IsNullOrEmpty(root))
            {
                root = @"C:\Program Files (x86)\Steam\steamapps\common\Rain World";
            }
            string streaming = Path.Combine(root, "RainWorld_Data", "StreamingAssets");
            string merged = Path.Combine(streaming, "mergedmods", "world");
            if (Directory.Exists(merged) && File.Exists(Path.Combine(merged, "regions.txt")))
            {
                return merged;
            }
            string vanilla = Path.Combine(streaming, "world");
            return Directory.Exists(vanilla) ? vanilla : null;
        }

        private static readonly string[] VanillaRegions = { "SU", "HI", "DS", "CC", "GW", "SH", "SL", "SI", "LF", "UW", "SS", "SB" };

        [Fact]
        public void VanillaSurvivorWorldFormsOneConnectedShelterGraph()
        {
            string world = WorldFolder();
            if (world == null)
            {
                output.WriteLine("Rain World not installed; skipping.");
                return;
            }
            var regions = new List<ParsedRegion>();
            foreach (string acr in VanillaRegions)
            {
                string path = Path.Combine(world, acr.ToLowerInvariant(), "world_" + acr.ToLowerInvariant() + ".txt");
                Assert.True(File.Exists(path), path);
                regions.Add(WorldFileParser.Parse(acr, File.ReadAllLines(path), "White"));
            }
            ShelterGraph g = ShelterGraphBuilder.Build(regions);
            output.WriteLine("rooms: " + g.RoomCount + ", shelters: " + g.Nodes.Count);
            Assert.True(g.Nodes.Count > 40, "expected dozens of shelters, got " + g.Nodes.Count);

            ShelterNode start = g.Get("SU_S01");
            Assert.NotNull(start);
            Dictionary<ShelterNode, PathInfo> reach = g.Dijkstra(start);
            var unreachable = g.Nodes.Where(n => !reach.ContainsKey(n)).Select(n => n.Name).ToList();
            output.WriteLine("unreachable from SU_S01: " + string.Join(", ", unreachable));
            Assert.True(reach.Count >= g.Nodes.Count * 0.9, "most shelters should be reachable from the start; unreachable: " + string.Join(", ", unreachable));

            // Crossing the SU->HI gate must be possible for the Pursuer.
            Assert.True(reach.Keys.Any(n => n.Region == "HI"), "no Industrial shelter reachable");
            int hopsToSb = reach.Where(p => p.Key.Region == "SB").Select(p => p.Value.Hops).DefaultIfEmpty(-1).Min();
            output.WriteLine("closest Subterranean shelter: " + hopsToSb + " hops");
            Assert.True(hopsToSb > 3);

            int maxHops = reach.Values.Max(p => p.Hops);
            double avgDegree = g.Nodes.Average(n => n.Edges.Count);
            output.WriteLine("farthest shelter: " + maxHops + " hops; average shelter degree: " + avgDegree.ToString("0.0"));
            foreach (var pair in reach.OrderBy(p => p.Value.Hops).ThenBy(p => p.Key.Name))
            {
                output.WriteLine("  " + pair.Value.Hops + " hops / " + pair.Value.Rooms + " rooms: " + pair.Key.Name);
            }
            Assert.True(avgDegree < 8, "graph too dense: " + avgDegree);
            Assert.True(maxHops >= 8, "graph too shallow: " + maxHops);
        }
    }
}
