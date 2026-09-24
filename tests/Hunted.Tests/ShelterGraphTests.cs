using System.Linq;
using Hunted.Core;
using Xunit;

namespace Hunted.Tests
{
    public class ShelterGraphTests
    {
        [Fact]
        public void EveryShelterBecomesANode()
        {
            ShelterGraph g = TestWorlds.Build();
            Assert.Equal(new[] { "A_S1", "A_S2", "A_S3", "B_S1", "B_S2", "B_S3", "B_S4" }, g.Nodes.Select(n => n.Name).OrderBy(n => n));
            Assert.Equal("A", g.Get("A_S1").Region);
            Assert.Equal("B", g.Get("B_S1").Region);
            Assert.Equal("A_1", g.Get("A_S1").EntranceRoom);
        }

        [Fact]
        public void EdgesOnlyConnectShelterNeighboursAndCarryRoomCounts()
        {
            ShelterGraph g = TestWorlds.Build();
            ShelterNode s2 = g.Get("A_S2");
            var edges = s2.Edges.ToDictionary(e => e.To.Name, e => e.Rooms);
            Assert.Equal(4, edges["A_S1"]);  // A_S2 - A_3 - A_2 - A_1 - A_S1
            Assert.Equal(4, edges["A_S3"]);  // A_S2 - A_3 - A_2 - A_4 - A_S3
            Assert.Equal(6, edges["B_S1"]);  // A_S2 - A_3 - A_5 - GATE - B_1 - B_2 - B_S1 (gate merged across regions)
            Assert.DoesNotContain("B_S2", edges.Keys); // B_S1 sits between them
            Assert.Equal(3, edges.Count);

            ShelterNode b2 = g.Get("B_S2");
            Assert.Equal(new[] { "B_S1", "B_S3" }, b2.Edges.Select(e => e.To.Name).OrderBy(x => x));
        }

        [Fact]
        public void EdgesAreSymmetric()
        {
            ShelterGraph g = TestWorlds.Build();
            foreach (ShelterNode node in g.Nodes)
            {
                foreach (ShelterEdge edge in node.Edges)
                {
                    ShelterEdge back = edge.To.Edges.Single(e => e.To == node);
                    Assert.Equal(edge.Rooms, back.Rooms);
                }
            }
        }

        [Fact]
        public void ShortestPathAndHopsAcrossTheGate()
        {
            ShelterGraph g = TestWorlds.Build();
            var path = g.ShortestPath(g.Get("A_S1"), g.Get("B_S4"));
            Assert.Equal(new[] { "A_S1", "A_S2", "B_S1", "B_S2", "B_S3", "B_S4" }, path.Select(n => n.Name));
            Assert.Equal(5, g.HopDistance(g.Get("A_S1"), g.Get("B_S4")));
            Assert.Equal(0, g.HopDistance(g.Get("A_S1"), g.Get("A_S1")));
        }

        [Fact]
        public void NearestShelterAndRoomDistance()
        {
            ShelterGraph g = TestWorlds.Build();
            Assert.Equal("A_S3", g.NearestShelter("A_4").Name);
            Assert.Equal("A_S1", g.NearestShelter("A_S1").Name);
            Assert.Contains(g.NearestShelter("GATE_A_B").Name, new[] { "A_S2", "B_S1" }); // both 3 rooms away
            Assert.Equal(2, g.RoomDistance("A_2", "A_S3"));
            Assert.Equal(0, g.RoomDistance("A_2", "A_2"));
            Assert.Equal(-1, g.RoomDistance("A_2", "NOPE"));
            Assert.Null(g.NearestShelter("NOPE"));
        }

        [Fact]
        public void UnreachableShelterHasNoPath()
        {
            string[] island = { "ROOMS", "C_S1 : C_1 : SHELTER", "C_1 : C_S1", "END ROOMS" };
            ShelterGraph g = ShelterGraphBuilder.Build(new[]
            {
                WorldFileParser.Parse("A", TestWorlds.RegionA, "White"),
                WorldFileParser.Parse("C", island, "White"),
            });
            Assert.Null(g.ShortestPath(g.Get("A_S1"), g.Get("C_S1")));
            Assert.Equal(-1, g.HopDistance(g.Get("A_S1"), g.Get("C_S1")));
        }
    }
}
