using System.Linq;
using Hunted.Core;
using Xunit;

namespace Hunted.Tests
{
    public class WorldFileParserTests
    {
        private static readonly string[] Sample =
        {
            "ROOMS",
            "// a comment",
            "GATE_SU_HI : SU_B13, DISCONNECTED : GATE",
            "SU_B13 : GATE_SU_HI, SU_A33",
            "SU_A33 : SU_B13, SU_S04",
            "SU_S04 : SU_A33 : SHELTER",
            "(Saint)SU_SAINTONLY : SU_A33",
            "(X-Saint)SU_NOTSAINT : SU_A33",
            "SU_HIDDEN : SU_A33 : SHELTER",
            "SU_EXCL : SU_A33 : SWARMROOM : SCAVOUTPOST",
            "SU_OLD : SU_A33, DISCONNECTED, DISCONNECTED",
            "END ROOMS",
            "",
            "CREATURES",
            "SU_A33 : 3-Pink",
            "END CREATURES",
            "",
            "CONDITIONAL LINKS",
            "Saint : HIDEROOM : SU_HIDDEN",
            "Saint,Rivulet : EXCLUSIVEROOM : SU_EXCL",
            "Saint : REPLACEROOM : SU_S04 : SU_S04SAINT",
            "Saint : SU_A33 : SU_B13 : DISCONNECTED",
            "Saint : SU_OLD : 2 : SU_B13",
            "END CONDITIONAL LINKS",
        };

        [Fact]
        public void ParsesRoomsConnectionsAndTags()
        {
            ParsedRegion region = WorldFileParser.Parse("SU", Sample, "White");

            WorldRoom gate = region.Rooms.Single(r => r.Name == "GATE_SU_HI");
            Assert.True(gate.IsGate);
            Assert.Equal(new[] { "SU_B13" }, gate.Connections); // DISCONNECTED dropped

            WorldRoom shelter = region.Rooms.Single(r => r.Name == "SU_S04");
            Assert.True(shelter.IsShelter);

            WorldRoom multiTag = WorldFileParser.Parse("SU", Sample, "Rivulet").Rooms.Single(r => r.Name == "SU_EXCL");
            Assert.Equal(new[] { "SWARMROOM", "SCAVOUTPOST" }, multiTag.Tags);
        }

        [Fact]
        public void TimelinePrefixesFilterLines()
        {
            ParsedRegion white = WorldFileParser.Parse("SU", Sample, "White");
            Assert.DoesNotContain(white.Rooms, r => r.Name == "SU_SAINTONLY");
            Assert.Contains(white.Rooms, r => r.Name == "SU_NOTSAINT");

            ParsedRegion saint = WorldFileParser.Parse("SU", Sample, "Saint");
            Assert.Contains(saint.Rooms, r => r.Name == "SU_SAINTONLY");
            Assert.DoesNotContain(saint.Rooms, r => r.Name == "SU_NOTSAINT");
        }

        [Fact]
        public void LegacyNumericTimelinesMatchVanillaSlugcats()
        {
            Assert.True(WorldFileParser.TimelineMatch("0,2", "White"));
            Assert.True(WorldFileParser.TimelineMatch("0,2", "Red"));
            Assert.False(WorldFileParser.TimelineMatch("0,2", "Yellow"));
            Assert.True(WorldFileParser.TimelineMatch("X-1", "White"));
            Assert.False(WorldFileParser.TimelineMatch("X-1", "Yellow"));
        }

        [Fact]
        public void HideAndExclusiveRoomsAreRemoved()
        {
            ParsedRegion white = WorldFileParser.Parse("SU", Sample, "White");
            Assert.Contains(white.Rooms, r => r.Name == "SU_HIDDEN");
            Assert.DoesNotContain(white.Rooms, r => r.Name == "SU_EXCL"); // exclusive to Saint/Rivulet

            ParsedRegion saint = WorldFileParser.Parse("SU", Sample, "Saint");
            Assert.DoesNotContain(saint.Rooms, r => r.Name == "SU_HIDDEN");
            Assert.Contains(saint.Rooms, r => r.Name == "SU_EXCL");

            ParsedRegion rivulet = WorldFileParser.Parse("SU", Sample, "Rivulet");
            Assert.Contains(rivulet.Rooms, r => r.Name == "SU_EXCL");
        }

        [Fact]
        public void DisabledRoomsDisappearFromConnections()
        {
            ParsedRegion saint = WorldFileParser.Parse("SU", Sample, "Saint");
            WorldRoom a33 = saint.Rooms.Single(r => r.Name == "SU_A33");
            Assert.DoesNotContain("SU_HIDDEN", a33.Connections);
        }

        [Fact]
        public void ReplaceRoomRenamesRoomAndReferences()
        {
            ParsedRegion saint = WorldFileParser.Parse("SU", Sample, "Saint");
            Assert.DoesNotContain(saint.Rooms, r => r.Name == "SU_S04");
            WorldRoom renamed = saint.Rooms.Single(r => r.Name == "SU_S04SAINT");
            Assert.True(renamed.IsShelter);
            WorldRoom a33 = saint.Rooms.Single(r => r.Name == "SU_A33");
            Assert.Contains("SU_S04SAINT", a33.Connections);
        }

        [Fact]
        public void ConditionalLinksRewireConnections()
        {
            ParsedRegion saint = WorldFileParser.Parse("SU", Sample, "Saint");
            WorldRoom a33 = saint.Rooms.Single(r => r.Name == "SU_A33");
            Assert.DoesNotContain("SU_B13", a33.Connections); // replaced by DISCONNECTED, which is dropped

            WorldRoom old = saint.Rooms.Single(r => r.Name == "SU_OLD");
            Assert.Equal(new[] { "SU_A33", "SU_B13" }, old.Connections); // 2nd DISCONNECTED slot became SU_B13

            ParsedRegion white = WorldFileParser.Parse("SU", Sample, "White");
            Assert.Contains("SU_B13", white.Rooms.Single(r => r.Name == "SU_A33").Connections);
        }

        [Fact]
        public void CustomConditionsAreConsulted()
        {
            string[] lines = { "ROOMS", "{Weird}SU_X : SU_Y", "SU_Y : SU_X", "END ROOMS" };
            ParsedRegion kept = WorldFileParser.Parse("SU", lines, "White");
            Assert.Contains(kept.Rooms, r => r.Name == "SU_X");
            ParsedRegion dropped = WorldFileParser.Parse("SU", lines, "White", cond => cond != "Weird");
            Assert.DoesNotContain(dropped.Rooms, r => r.Name == "SU_X");
        }
    }
}
