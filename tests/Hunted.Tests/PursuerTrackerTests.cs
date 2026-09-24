using System.Collections.Generic;
using Hunted.Core;
using Xunit;

namespace Hunted.Tests
{
    public class PursuerTrackerTests
    {
        private static TrackerConfig Config(int spawnMin = 4, int hops = 2)
        {
            return new TrackerConfig { SpawnMinHops = spawnMin, HopsPerCycle = hops, OffscreenUpgrades = false, RespawnCycles = 3, RetreatHops = 3 };
        }

        [Fact]
        public void InitializePlacesThePursuerFarEnoughAway()
        {
            ShelterGraph g = TestWorlds.Build();
            for (int seed = 0; seed < 20; seed++)
            {
                PursuerState s = PursuerTracker.Initialize(g, "A_S1", seed, Config(spawnMin: 4), 1);
                Assert.Equal(PursuerStatus.Traveling, s.Status);
                Assert.True(PursuerTracker.HopsAway(g, s, "A_S1") >= 4, s.Shelter);
                Assert.Equal(seed, s.Seed);
            }
        }

        [Fact]
        public void InitializeFallsBackToTheFarthestShelterWhenNothingIsFarEnough()
        {
            ShelterGraph g = TestWorlds.Build();
            PursuerState s = PursuerTracker.Initialize(g, "A_S1", 1, Config(spawnMin: 99), 1);
            Assert.Equal("B_S4", s.Shelter);
        }

        [Fact]
        public void AcceptanceSixHopsAwayThreeSleepsArrives()
        {
            // Given the Pursuer is 6 hops away, when the player sleeps successfully 3 times,
            // then the Pursuer is in the player's region (Arrived) at the next cycle start.
            string[] longChain =
            {
                "ROOMS",
                "A_S0 : A_0 : SHELTER", "A_0 : A_S0, A_S1",
                "A_S1 : A_0, A_1 : SHELTER", "A_1 : A_S1, A_S2",
                "A_S2 : A_1, A_2 : SHELTER", "A_2 : A_S2, A_S3",
                "A_S3 : A_2, A_3 : SHELTER", "A_3 : A_S3, A_S4",
                "A_S4 : A_3, A_4 : SHELTER", "A_4 : A_S4, A_S5",
                "A_S5 : A_4, A_5 : SHELTER", "A_5 : A_S5, A_S6",
                "A_S6 : A_5 : SHELTER",
                "END ROOMS",
            };
            ShelterGraph g = ShelterGraphBuilder.Build(new[] { WorldFileParser.Parse("A", longChain, "White") });
            var s = new PursuerState { Shelter = "A_S6", Region = "A", Status = PursuerStatus.Traveling };
            Assert.Equal(6, PursuerTracker.HopsAway(g, s, "A_S0"));
            TrackerConfig cfg = Config(hops: 2);
            cfg.ArriveWithinHops = 2;

            s = PursuerTracker.OnCycleSurvived(g, s, "A_S0", false, null, cfg, 2);
            Assert.Equal("A_S4", s.Shelter);
            Assert.Equal(PursuerStatus.Traveling, s.Status);
            s = PursuerTracker.OnCycleSurvived(g, s, "A_S0", false, null, cfg, 3);
            Assert.Equal("A_S2", s.Shelter);
            Assert.Equal(PursuerStatus.Arrived, s.Status); // within 2 hops
            s = PursuerTracker.OnCycleSurvived(g, s, "A_S0", false, null, cfg, 4);
            Assert.Equal(PursuerStatus.Arrived, s.Status);
            Assert.Equal("A", s.Region);
        }

        [Fact]
        public void StarvedSleepMovesAnExtraHop()
        {
            ShelterGraph g = TestWorlds.Build();
            var s = new PursuerState { Shelter = "B_S4", Region = "B" };
            TrackerConfig cfg = Config(hops: 2);
            cfg.ArriveWithinHops = 0;
            PursuerState fed = PursuerTracker.OnCycleSurvived(g, s, "A_S1", false, null, cfg, 2);
            PursuerState starved = PursuerTracker.OnCycleSurvived(g, s, "A_S1", true, null, cfg, 2);
            Assert.Equal("B_S2", fed.Shelter);
            Assert.Equal("B_S1", starved.Shelter);
        }

        [Fact]
        public void NeverOvershootsThePlayer()
        {
            ShelterGraph g = TestWorlds.Build();
            var s = new PursuerState { Shelter = "A_S2", Region = "A" };
            TrackerConfig cfg = Config(hops: 5);
            cfg.ArriveWithinHops = 0;
            PursuerState next = PursuerTracker.OnCycleSurvived(g, s, "A_S1", false, null, cfg, 2);
            Assert.Equal("A_S1", next.Shelter);
            Assert.Equal(PursuerStatus.Arrived, next.Status);
        }

        [Fact]
        public void ArrivedPursuerFollowsItsInWorldPosition()
        {
            ShelterGraph g = TestWorlds.Build();
            var s = new PursuerState { Shelter = "A_S2", Region = "A", Status = PursuerStatus.Arrived };
            var snap = new InWorldSnapshot { Alive = true, LastRoom = "A_4", Inventory = new List<string> { GearTier.Spear } };
            PursuerState next = PursuerTracker.OnCycleSurvived(g, s, "A_S1", false, snap, Config(), 2);
            Assert.Equal(PursuerStatus.Arrived, next.Status);
            Assert.Equal("A_S3", next.Shelter); // nearest shelter to A_4
            Assert.Equal(new[] { GearTier.Spear }, next.Inventory);
            Assert.Equal("A_4", next.LastRoom);
        }

        [Fact]
        public void ArrivedPursuerThatDiedWaitsToRespawn()
        {
            ShelterGraph g = TestWorlds.Build();
            var s = new PursuerState { Shelter = "A_S2", Region = "A", Status = PursuerStatus.Arrived, Inventory = new List<string> { GearTier.Spear } };
            TrackerConfig cfg = Config();
            PursuerState dead = PursuerTracker.OnCycleSurvived(g, s, "A_S1", false, new InWorldSnapshot { Alive = false }, cfg, 10);
            Assert.Equal(PursuerStatus.Dead, dead.Status);
            Assert.Equal(13, dead.RespawnCycle);
            Assert.Empty(dead.Inventory); // dropped where it died

            PursuerState stillDead = PursuerTracker.OnCycleSurvived(g, dead, "A_S1", false, null, cfg, 12);
            Assert.Equal(PursuerStatus.Dead, stillDead.Status);

            PursuerState reborn = PursuerTracker.OnCycleSurvived(g, dead, "A_S1", false, null, cfg, 13);
            Assert.Equal(PursuerStatus.Traveling, reborn.Status);
            Assert.True(PursuerTracker.HopsAway(g, reborn, "A_S1") >= cfg.SpawnMinHops);
            Assert.Empty(reborn.Inventory);
        }

        [Fact]
        public void HardModeRespawnKeepsGear()
        {
            ShelterGraph g = TestWorlds.Build();
            TrackerConfig cfg = Config();
            cfg.KeepGearOnRespawn = true;
            var dead = new PursuerState { Shelter = "A_S2", Region = "A", Status = PursuerStatus.Dead, RespawnCycle = 5, Inventory = new List<string> { GearTier.ExplosiveSpear } };
            PursuerState reborn = PursuerTracker.OnCycleSurvived(g, dead, "A_S1", false, null, cfg, 5);
            Assert.Equal(new[] { GearTier.ExplosiveSpear }, reborn.Inventory);
        }

        [Fact]
        public void ArrivedPursuerLeftBehindGoesBackToTracking()
        {
            ShelterGraph g = TestWorlds.Build();
            // Pursuer arrived in region B; player slept in region A.
            var s = new PursuerState { Shelter = "B_S1", Region = "B", Status = PursuerStatus.Arrived };
            TrackerConfig cfg = Config(hops: 1);
            cfg.ArriveWithinHops = 0;
            PursuerState next = PursuerTracker.OnCycleSurvived(g, s, "A_S1", false, null, cfg, 2);
            Assert.Equal("A_S2", next.Shelter);
            Assert.Equal(PursuerStatus.Traveling, next.Status);
        }

        [Fact]
        public void PlayerDeathMakesThePursuerRetreat()
        {
            ShelterGraph g = TestWorlds.Build();
            var s = new PursuerState { Shelter = "A_S2", Region = "A", Status = PursuerStatus.Arrived, Inventory = new List<string> { GearTier.Spear } };
            TrackerConfig cfg = Config();
            cfg.RetreatHops = 3;
            PursuerState after = PursuerTracker.OnPlayerDied(g, s, "A_S1", true, cfg);
            Assert.Equal(PursuerStatus.Traveling, after.Status);
            Assert.True(PursuerTracker.HopsAway(g, after, "A_S1") >= 3, after.Shelter);
            Assert.Equal("B_S2", after.Shelter); // nearest qualifying shelter to where it was
            Assert.Equal(1, after.PlayerKills);
            Assert.Equal(new[] { GearTier.Spear }, after.Inventory); // keeps its gear
        }

        [Fact]
        public void DeadPursuerIgnoresPlayerDeath()
        {
            ShelterGraph g = TestWorlds.Build();
            var s = new PursuerState { Shelter = "A_S2", Region = "A", Status = PursuerStatus.Dead, RespawnCycle = 9 };
            PursuerState after = PursuerTracker.OnPlayerDied(g, s, "A_S1", false, Config());
            Assert.Equal(PursuerStatus.Dead, after.Status);
            Assert.Equal("A_S2", after.Shelter);
        }

        [Fact]
        public void OffscreenUpgradesAreDeterministicAndBounded()
        {
            ShelterGraph g = TestWorlds.Build();
            var cfg = new TrackerConfig { HopsPerCycle = 1, ArriveWithinHops = 0, OffscreenUpgrades = true };
            var a = new PursuerState { Shelter = "B_S4", Region = "B", Seed = 42 };
            var b = new PursuerState { Shelter = "B_S4", Region = "B", Seed = 42 };
            for (int cycle = 1; cycle < 5; cycle++)
            {
                a = PursuerTracker.OnCycleSurvived(g, a, "A_S1", false, null, cfg, cycle);
                b = PursuerTracker.OnCycleSurvived(g, b, "A_S1", false, null, cfg, cycle);
                Assert.Equal(a.Inventory, b.Inventory);
                Assert.True(a.Inventory.Count <= GearTier.MaxHeld);
            }
        }

        [Fact]
        public void UnknownPlayerShelterHoldsPosition()
        {
            ShelterGraph g = TestWorlds.Build();
            var s = new PursuerState { Shelter = "B_S4", Region = "B" };
            PursuerState next = PursuerTracker.OnCycleSurvived(g, s, "NOWHERE", false, null, Config(), 2);
            Assert.Equal("B_S4", next.Shelter);
            Assert.Equal(-1, PursuerTracker.HopsAway(g, next, "NOWHERE"));
        }
    }
}
