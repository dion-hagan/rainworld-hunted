using System;
using System.Reflection;
using MonoMod.RuntimeDetour;
using MoreSlugcats;
using RWCustom;
using UnityEngine;

namespace Hunted.Game
{
    /// <summary>
    /// Hooks that only matter for the slugcat body (Stage 2): installing the AI,
    /// making the body an adult with Hunter stats, letting spears hit and be thrown
    /// despite Jolly's friendly-fire rule, colours, and how other creatures see it.
    /// </summary>
    internal static class Stage2Hooks
    {
        private static readonly Color BodyColor = new Color(0.17f, 0.15f, 0.19f);
        private static readonly Color EyeColor = new Color(0.95f, 0.16f, 0.12f);
        private static SlugcatStats pursuerStats;
        private static Hook slugcatStatsHook;

        public static void Apply()
        {
            On.AbstractCreature.InitiateAI += AbstractCreature_InitiateAI;
            On.MoreSlugcats.SlugNPCAbstractAI.AbstractBehavior += SlugNPCAbstractAI_AbstractBehavior;
            On.Player.GetInitialSlugcatClass += Player_GetInitialSlugcatClass;
            On.Player.ShortCutColor += Player_ShortCutColor;
            On.Weapon.HitThisObject += Weapon_HitThisObject;
            On.PlayerGraphics.ApplyPalette += PlayerGraphics_ApplyPalette;
            On.PlayerGraphics.DrawSprites += PlayerGraphics_DrawSprites;
            On.ArtificialIntelligence.StaticRelationship += ArtificialIntelligence_StaticRelationship;

            // Player.slugcatStats is a property, so HookGen has no event for it; detour the getter directly.
            MethodInfo getter = typeof(Player).GetProperty("slugcatStats", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod();
            if (getter != null)
            {
                slugcatStatsHook = new Hook(getter, new Func<Func<Player, SlugcatStats>, Player, SlugcatStats>(Player_get_slugcatStats));
            }
            else
            {
                HuntedLog.Warn("Player.slugcatStats getter not found; the slugcat Pursuer will use the campaign slugcat's stats.");
            }
        }

        private static bool IsSlugcatPursuer(AbstractCreature creature)
        {
            return PursuerMark.IsMarked(creature) && PursuerBodies.IsSlugcat(creature);
        }

        private static void AbstractCreature_InitiateAI(On.AbstractCreature.orig_InitiateAI orig, AbstractCreature self)
        {
            if (IsSlugcatPursuer(self) && self.abstractAI != null)
            {
                if (self.abstractAI.RealAI is PursuerAI)
                {
                    // Realize ran again on a live body (Abstractize clears RealAI, so this is
                    // the same body): keep the AI and what it remembers about the player.
                    return;
                }
                try
                {
                    self.abstractAI.RealAI = new PursuerAI(self, self.world);
                    return;
                }
                catch (Exception e)
                {
                    HuntedLog.Error("Could not create the Pursuer AI; falling back to the slugpup AI", e);
                }
            }
            orig(self);
        }

        private static void SlugNPCAbstractAI_AbstractBehavior(On.MoreSlugcats.SlugNPCAbstractAI.orig_AbstractBehavior orig, SlugNPCAbstractAI self, int time)
        {
            HuntedSession s = HuntedSession.Current;
            if (s == null || !s.IsPursuer(self.parent))
            {
                orig(self, time);
                return;
            }
            try
            {
                s.AbstractBehavior(self, time);
            }
            catch (Exception e)
            {
                HuntedLog.Error("Pursuer abstract behavior failed; using vanilla", e);
                orig(self, time);
            }
        }

        private static void Player_GetInitialSlugcatClass(On.Player.orig_GetInitialSlugcatClass orig, Player self)
        {
            orig(self);
            if (IsSlugcatPursuer(self.abstractCreature))
            {
                // Not a pup: an adult body. Hunter's speed and throws come from the slugcatStats
                // detour below; the class itself stays Survivor because Player's constructor has a
                // Hunter-only branch that reads room.game with no null check, and room is null when
                // a creature is realized on its way into a room (AbstractCreature.ChangeRooms).
                // Red would also give the body Hunter's illness after enough cycles.
                self.SlugCatClass = SlugcatStats.Name.White;
            }
        }

        private static SlugcatStats Player_get_slugcatStats(Func<Player, SlugcatStats> orig, Player self)
        {
            if (IsSlugcatPursuer(self.abstractCreature))
            {
                if (pursuerStats == null)
                {
                    pursuerStats = new SlugcatStats(SlugcatStats.Name.Red, false);
                }
                return pursuerStats;
            }
            return orig(self);
        }

        private static Color Player_ShortCutColor(On.Player.orig_ShortCutColor orig, Player self)
        {
            return IsSlugcatPursuer(self.abstractCreature) ? EyeColor : orig(self);
        }

        private static bool Weapon_HitThisObject(On.Weapon.orig_HitThisObject orig, Weapon self, PhysicalObject obj)
        {
            // Jolly's friendly fire rule would otherwise make the Pursuer's spears pass through the player and vice versa.
            if (obj is Player hit && PursuerMark.IsMarked(hit.abstractCreature))
            {
                return true;
            }
            if (self.thrownBy is Player thrower && PursuerMark.IsMarked(thrower.abstractCreature))
            {
                return true;
            }
            return orig(self, obj);
        }

        private static void PlayerGraphics_ApplyPalette(On.PlayerGraphics.orig_ApplyPalette orig, PlayerGraphics self, RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, RoomPalette palette)
        {
            orig(self, sLeaser, rCam, palette);
            if (!(self.owner is Player owner) || !IsSlugcatPursuer(owner.abstractCreature) || sLeaser.sprites == null)
            {
                return;
            }
            Color body = Color.Lerp(BodyColor, palette.blackColor, 0.2f);
            for (int i = 0; i < sLeaser.sprites.Length; i++)
            {
                if (i != 9)
                {
                    sLeaser.sprites[i].color = body;
                }
            }
            if (sLeaser.sprites.Length > 11)
            {
                sLeaser.sprites[10].color = body;
                sLeaser.sprites[11].color = Color.Lerp(body, Color.white, 0.2f);
            }
        }

        private static void PlayerGraphics_DrawSprites(On.PlayerGraphics.orig_DrawSprites orig, PlayerGraphics self, RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, float timeStacker, Vector2 camPos)
        {
            orig(self, sLeaser, rCam, timeStacker, camPos);
            if (sLeaser.sprites != null && sLeaser.sprites.Length > 9 && self.owner is Player owner && IsSlugcatPursuer(owner.abstractCreature))
            {
                sLeaser.sprites[9].color = EyeColor;
            }
        }

        private static CreatureTemplate.Relationship ArtificialIntelligence_StaticRelationship(On.ArtificialIntelligence.orig_StaticRelationship orig, ArtificialIntelligence self, AbstractCreature otherCreature)
        {
            // Every creature treats the slugcat Pursuer exactly as it treats a slugcat: lizards hunt it, vultures snatch it.
            if (IsSlugcatPursuer(otherCreature) && self.creature != null && !PursuerMark.IsMarked(self.creature))
            {
                CreatureTemplate slugcat = StaticWorld.GetCreatureTemplate(CreatureTemplate.Type.Slugcat);
                if (slugcat != null)
                {
                    return self.creature.creatureTemplate.CreatureRelationship(slugcat);
                }
            }
            return orig(self, otherCreature);
        }
    }
}
