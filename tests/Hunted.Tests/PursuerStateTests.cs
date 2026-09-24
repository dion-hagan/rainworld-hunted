using System.Collections.Generic;
using Hunted.Core;
using Xunit;

namespace Hunted.Tests
{
    public class PursuerStateTests
    {
        [Fact]
        public void RoundTripsThroughTheSaveString()
        {
            var s = new PursuerState
            {
                Status = PursuerStatus.Arrived,
                Region = "GW",
                Shelter = "GW_S04",
                Inventory = new List<string> { GearTier.ExplosiveSpear, GearTier.Rock },
                RespawnCycle = 12,
                PlayerKills = 2,
                ScavKills = 5,
                Seed = 123456,
                LastRoom = "GW_A22",
                CyclesTracked = 7,
            };
            string text = s.Serialize();
            Assert.DoesNotContain("<", text);
            Assert.DoesNotContain(">", text);
            Assert.True(PursuerState.TryParse(text, out PursuerState back), text);
            Assert.Equal(s.Status, back.Status);
            Assert.Equal(s.Region, back.Region);
            Assert.Equal(s.Shelter, back.Shelter);
            Assert.Equal(s.Inventory, back.Inventory);
            Assert.Equal(s.RespawnCycle, back.RespawnCycle);
            Assert.Equal(s.PlayerKills, back.PlayerKills);
            Assert.Equal(s.ScavKills, back.ScavKills);
            Assert.Equal(s.Seed, back.Seed);
            Assert.Equal(s.LastRoom, back.LastRoom);
            Assert.Equal(s.CyclesTracked, back.CyclesTracked);
            Assert.Equal(PursuerState.CurrentVersion, back.Version);
        }

        [Fact]
        public void EmptyInventoryAndNullsSurvive()
        {
            var s = new PursuerState { Shelter = "SU_S01", Region = "SU" };
            Assert.True(PursuerState.TryParse(s.Serialize(), out PursuerState back));
            Assert.Empty(back.Inventory);
            Assert.Null(back.LastRoom);
            Assert.Equal(-1, back.RespawnCycle);
        }

        [Fact]
        public void GarbageIsRejected()
        {
            Assert.False(PursuerState.TryParse("", out _));
            Assert.False(PursuerState.TryParse(null, out _));
            Assert.False(PursuerState.TryParse("hello world", out _));
            Assert.False(PursuerState.TryParse("v=1|st=Traveling", out _)); // no shelter
        }

        [Fact]
        public void UnknownFieldsAreIgnoredForForwardCompatibility()
        {
            Assert.True(PursuerState.TryParse("v=2|st=Arrived|sh=SU_S01|rg=SU|future=thing", out PursuerState s));
            Assert.Equal(2, s.Version);
            Assert.Equal(PursuerStatus.Arrived, s.Status);
        }

        [Fact]
        public void GearAddDropsTheWeakestItemWhenFull()
        {
            var inv = new List<string> { GearTier.Rock, GearTier.Spear };
            Assert.Equal(GearTier.Rock, GearTier.Add(inv, GearTier.ScavengerBomb));
            Assert.Equal(new[] { GearTier.ScavengerBomb, GearTier.Spear }, inv);
            Assert.Equal(GearTier.Rock, GearTier.Add(inv, GearTier.Rock)); // nothing worth replacing
            Assert.Equal(2, inv.Count);
        }
    }
}
