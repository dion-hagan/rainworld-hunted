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
        public void GearFillsBothHandsAndTheBack()
        {
            // Hunter's loadout: a spear in one hand, a bomb in the other, a spear on the back.
            var inv = new List<string> { GearTier.Rock, GearTier.Spear };
            Assert.Null(GearTier.Add(inv, GearTier.ScavengerBomb));
            Assert.Equal(new[] { GearTier.Rock, GearTier.Spear, GearTier.ScavengerBomb }, inv);
            Assert.False(GearTier.CanCarry(new[] { GearTier.Rock, GearTier.ScavengerBomb, GearTier.Rock })); // three hand items
            Assert.True(GearTier.CanCarry(new[] { GearTier.Rock, GearTier.ScavengerBomb, GearTier.Spear })); // the spear rides on the back
            Assert.True(GearTier.CanCarry(new[] { GearTier.Spear, GearTier.ExplosiveSpear, GearTier.Rock }));
            Assert.False(GearTier.CanCarry(new[] { GearTier.Spear, GearTier.ExplosiveSpear, GearTier.ElectricSpear })); // one hand spear, one back spear
        }

        [Fact]
        public void GearAddDropsTheWeakestItemWhenFull()
        {
            var inv = new List<string> { GearTier.Rock, GearTier.Spear, GearTier.ScavengerBomb };
            Assert.Equal(GearTier.Rock, GearTier.Add(inv, GearTier.Rock)); // nothing worth replacing
            Assert.Equal(GearTier.Rock, GearTier.Add(inv, GearTier.ExplosiveSpear)); // the hands take it, the plain spear goes on the back
            Assert.Equal(new[] { GearTier.ExplosiveSpear, GearTier.Spear, GearTier.ScavengerBomb }, inv);
            Assert.Equal(3, inv.Count);

            // The back only takes spears: with both hands busy a rock has to beat a hand item.
            inv = new List<string> { GearTier.Rock, GearTier.ScavengerBomb };
            Assert.Equal(GearTier.Rock, GearTier.Add(inv, GearTier.Rock));
            Assert.Equal(2, inv.Count);

            // Three spears cannot all be carried: the weakest goes.
            inv = new List<string> { GearTier.Spear, GearTier.ElectricSpear };
            Assert.Equal(GearTier.Spear, GearTier.Add(inv, GearTier.ExplosiveSpear));
            Assert.Equal(new[] { GearTier.ExplosiveSpear, GearTier.ElectricSpear }, inv);
        }

        [Fact]
        public void GearTrimsToWhatTheBodyCanCarryBestFirst()
        {
            var kept = GearTier.TrimToCarryable(new[] { GearTier.Rock, GearTier.Spear, GearTier.Rock, GearTier.ScavengerBomb, GearTier.ExplosiveSpear });
            Assert.Equal(new[] { GearTier.ScavengerBomb, GearTier.ExplosiveSpear, GearTier.Spear }, kept);
            Assert.Empty(GearTier.TrimToCarryable(null));
        }

        [Fact]
        public void TheWeakestSpearRidesOnTheBackOnlyWhenTheHandsAreShort()
        {
            Assert.Equal(-1, GearTier.BackSpearIndex(new[] { GearTier.Spear, GearTier.Rock }));
            Assert.Equal(-1, GearTier.BackSpearIndex(new[] { GearTier.Rock, GearTier.ScavengerBomb }));
            Assert.Equal(0, GearTier.BackSpearIndex(new[] { GearTier.Spear, GearTier.ExplosiveSpear }));
            Assert.Equal(2, GearTier.BackSpearIndex(new[] { GearTier.ExplosiveSpear, GearTier.ScavengerBomb, GearTier.Spear }));
            Assert.Equal(1, GearTier.BackSpearIndex(new[] { GearTier.Rock, GearTier.Spear, GearTier.ScavengerBomb }));
        }
    }
}
