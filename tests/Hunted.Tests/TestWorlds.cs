using System.Collections.Generic;
using Hunted.Core;

namespace Hunted.Tests
{
    /// <summary>
    /// A tiny two-region world used by the graph and tracker tests. Shelters are
    /// dead-end rooms, like in the real game.
    ///
    ///   Region A:  A_S1 - A_1 - A_2 - A_3 - A_5 - GATE_A_B
    ///                            |      |
    ///                           A_4    A_S2
    ///                            |
    ///                           A_S3
    ///   Region B:  GATE_A_B - B_1 - B_2 - B_3 - B_4 - B_5 - B_6 - B_7 - B_8 - B_S4
    ///                                |           |           |
    ///                               B_S1        B_S2        B_S3
    ///
    /// Shelter chain (hops): A_S1 - A_S2 - B_S1 - B_S2 - B_S3 - B_S4, with A_S3 next to both A_S1 and A_S2.
    /// Room distances: A_S1-A_S2 4, A_S1-A_S3 4, A_S2-A_S3 4, A_S2-B_S1 6, B_S1-B_S2 4, B_S2-B_S3 4, B_S3-B_S4 4.
    /// </summary>
    public static class TestWorlds
    {
        public static readonly string[] RegionA =
        {
            "ROOMS",
            "A_S1 : A_1 : SHELTER",
            "A_1 : A_S1, A_2",
            "A_2 : A_1, A_3, A_4",
            "A_3 : A_2, A_S2, A_5",
            "A_S2 : A_3 : SHELTER",
            "A_4 : A_2, A_S3",
            "A_S3 : A_4 : SHELTER",
            "A_5 : A_3, GATE_A_B",
            "GATE_A_B : A_5, DISCONNECTED : GATE",
            "END ROOMS",
        };

        public static readonly string[] RegionB =
        {
            "ROOMS",
            "GATE_A_B : DISCONNECTED, B_1 : GATE",
            "B_1 : GATE_A_B, B_2",
            "B_2 : B_1, B_S1, B_3",
            "B_S1 : B_2 : SHELTER",
            "B_3 : B_2, B_4",
            "B_4 : B_3, B_S2, B_5",
            "B_S2 : B_4 : SHELTER",
            "B_5 : B_4, B_6",
            "B_6 : B_5, B_S3, B_7",
            "B_S3 : B_6 : SHELTER",
            "B_7 : B_6, B_8",
            "B_8 : B_7, B_S4",
            "B_S4 : B_8 : SHELTER",
            "END ROOMS",
        };

        public static ShelterGraph Build()
        {
            var regions = new List<ParsedRegion>
            {
                WorldFileParser.Parse("A", RegionA, "White"),
                WorldFileParser.Parse("B", RegionB, "White"),
            };
            return ShelterGraphBuilder.Build(regions);
        }
    }
}
