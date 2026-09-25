using System;
using Hunted.Core;
using RWCustom;
using UnityEngine;

namespace Hunted.Game
{
    /// <summary>
    /// All of the mod's game hooks in one place. Every handler defers to the
    /// vanilla code unless the creature involved is the Pursuer, and never lets
    /// an exception escape into the game loop.
    /// </summary>
    internal static class Hooks
    {
        public static void Apply()
        {
            // Session lifecycle
            On.StoryGameSession.ctor += StoryGameSession_ctor;
            On.RainWorldGame.ctor += RainWorldGame_ctor;
            On.RainWorldGame.ShutDownProcess += RainWorldGame_ShutDownProcess;
            On.RainWorldGame.Update += RainWorldGame_Update;
            On.OverWorld.WorldLoaded += OverWorld_WorldLoaded;

            // Saving
            On.PlayerProgression.SaveWorldStateAndProgression += PlayerProgression_SaveWorldStateAndProgression;
            On.DeathPersistentSaveData.SaveToString += DeathPersistentSaveData_SaveToString;

            // Offscreen migration
            On.ScavengerAbstractAI.AbstractBehavior += ScavengerAbstractAI_AbstractBehavior;
            On.ScavengerAbstractAI.NewWorld += ScavengerAbstractAI_NewWorld;
            On.ScavengerAbstractAI.RoomGhostScary += ScavengerAbstractAI_RoomGhostScary;

            // Hunting
            On.ScavengerAI.ctor += ScavengerAI_ctor;
            On.ScavengerAI.PlayerRelationship += ScavengerAI_PlayerRelationship;
            On.ScavengerAI.LikeOfPlayer += ScavengerAI_LikeOfPlayer;
            On.ScavengerAI.CurrentPlayerAggression += ScavengerAI_CurrentPlayerAggression;
            On.ScavengerAI.IUseARelationshipTracker_UpdateDynamicRelationship += ScavengerAI_UpdateDynamicRelationship;
            On.ScavengerAI.PackLeader += ScavengerAI_PackLeader;
            On.ScavengerAI.DecideBehavior += ScavengerAI_DecideBehavior;
            On.ScavengerAI.SocialEvent += ScavengerAI_SocialEvent;

            // Deaths and kills
            On.AbstractCreature.Die += AbstractCreature_Die;
            On.Creature.Die += Creature_Die;
            On.Player.Die += Player_Die;
            On.SocialEventRecognizer.Killing += SocialEventRecognizer_Killing;

            // Looks and HUD
            On.ScavengerGraphics.ctor += ScavengerGraphics_ctor;
            On.HUD.HUD.InitSinglePlayerHud += HUD_InitSinglePlayerHud;
            On.Menu.SleepAndDeathScreen.GetDataFromGame += SleepAndDeathScreen_GetDataFromGame;
        }

        private static bool IsPursuer(AbstractCreature creature)
        {
            HuntedSession s = HuntedSession.Current;
            return s != null && s.IsPursuer(creature);
        }

        private static bool IsScavengerKind(CreatureTemplate template)
        {
            return template != null && template.TopAncestor().type == CreatureTemplate.Type.Scavenger;
        }

        // ---------------------------------------------------------------- session

        private static void StoryGameSession_ctor(On.StoryGameSession.orig_ctor orig, StoryGameSession self, SlugcatStats.Name saveStateNumber, RainWorldGame game)
        {
            orig(self, saveStateNumber, game);
            try
            {
                if (game.rainWorld.safariMode || (ModManager.MSC && game.wasAnArtificerDream))
                {
                    HuntedSession.End();
                    return;
                }
                HuntedSession.Start(game, self.saveState);
            }
            catch (Exception e)
            {
                HuntedLog.Error("StoryGameSession hook failed", e);
            }
        }

        private static void RainWorldGame_ctor(On.RainWorldGame.orig_ctor orig, RainWorldGame self, ProcessManager manager)
        {
            orig(self, manager);
            try
            {
                HuntedSession s = HuntedSession.Current;
                if (s != null && s.game == self)
                {
                    s.OnGameStarted();
                }
            }
            catch (Exception e)
            {
                HuntedLog.Error("RainWorldGame ctor hook failed", e);
            }
        }

        private static void RainWorldGame_ShutDownProcess(On.RainWorldGame.orig_ShutDownProcess orig, RainWorldGame self)
        {
            orig(self);
            HuntedSession.End();
        }

        private static void RainWorldGame_Update(On.RainWorldGame.orig_Update orig, RainWorldGame self)
        {
            orig(self);
            HuntedSession s = HuntedSession.Current;
            if (s == null || s.game != self)
            {
                return;
            }
            try
            {
                s.Update();
            }
            catch (Exception e)
            {
                HuntedLog.Error("Update hook failed", e);
            }
        }

        private static void OverWorld_WorldLoaded(On.OverWorld.orig_WorldLoaded orig, OverWorld self, bool warpUsed)
        {
            World old = self.activeWorld;
            orig(self, warpUsed);
            try
            {
                HuntedSession.Current?.OnWorldSwitched(old, self.activeWorld);
            }
            catch (Exception e)
            {
                HuntedLog.Error("WorldLoaded hook failed", e);
            }
        }

        // ---------------------------------------------------------------- saving

        private static bool PlayerProgression_SaveWorldStateAndProgression(On.PlayerProgression.orig_SaveWorldStateAndProgression orig, PlayerProgression self, bool malnourished)
        {
            SaveContext.Sleeping = true;
            SaveContext.Malnourished = malnourished;
            try
            {
                return orig(self, malnourished);
            }
            finally
            {
                SaveContext.Sleeping = false;
                SaveContext.Malnourished = false;
            }
        }

        private static string DeathPersistentSaveData_SaveToString(On.DeathPersistentSaveData.orig_SaveToString orig, DeathPersistentSaveData self, bool saveAsIfPlayerDied, bool saveAsIfPlayerQuit)
        {
            HuntedSession s = HuntedSession.Current;
            if (s != null && s.OwnsSaveData(self))
            {
                try
                {
                    s.PrepareSave(saveAsIfPlayerDied, saveAsIfPlayerQuit);
                    s.WriteToSaveData(self);
                }
                catch (Exception e)
                {
                    HuntedLog.Error("Save hook failed", e);
                }
            }
            return orig(self, saveAsIfPlayerDied, saveAsIfPlayerQuit);
        }

        // ---------------------------------------------------------------- offscreen migration

        private static void ScavengerAbstractAI_AbstractBehavior(On.ScavengerAbstractAI.orig_AbstractBehavior orig, ScavengerAbstractAI self, int time)
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

        private static void ScavengerAbstractAI_NewWorld(On.ScavengerAbstractAI.orig_NewWorld orig, ScavengerAbstractAI self, World newWorld)
        {
            orig(self, newWorld);
            if (IsPursuer(self.parent))
            {
                PursuerSpawner.ConfigureAbstractAI(self.parent);
            }
        }

        private static float ScavengerAbstractAI_RoomGhostScary(On.ScavengerAbstractAI.orig_RoomGhostScary orig, ScavengerAbstractAI self, int testRoom)
        {
            return IsPursuer(self.parent) ? 0f : orig(self, testRoom);
        }

        // ---------------------------------------------------------------- hunting

        private static void ScavengerAI_ctor(On.ScavengerAI.orig_ctor orig, ScavengerAI self, AbstractCreature creature, World world)
        {
            orig(self, creature, world);
            if (!IsPursuer(creature))
            {
                return;
            }
            try
            {
                // It has always hated the player, and knows it well.
                if (creature.state.socialMemory != null && world.game.Players != null)
                {
                    foreach (AbstractCreature player in world.game.Players)
                    {
                        SocialMemory.Relationship rel = creature.state.socialMemory.GetOrInitiateRelationship(player.ID);
                        rel.like = -1f;
                        rel.tempLike = -1f;
                        rel.know = 1f;
                    }
                }
            }
            catch (Exception e)
            {
                HuntedLog.Error("ScavengerAI ctor hook failed", e);
            }
        }

        private static CreatureTemplate.Relationship ScavengerAI_PlayerRelationship(On.ScavengerAI.orig_PlayerRelationship orig, ScavengerAI self, RelationshipTracker.DynamicRelationship dRelation)
        {
            if (!IsPursuer(self.creature))
            {
                return orig(self, dRelation);
            }
            if (dRelation.state is ScavengerAI.ScavengerTrackState state)
            {
                state.taggedViolenceType = ScavengerAI.ViolenceType.Lethal;
            }
            return new CreatureTemplate.Relationship(CreatureTemplate.Relationship.Type.Attacks, 1f);
        }

        private static float ScavengerAI_LikeOfPlayer(On.ScavengerAI.orig_LikeOfPlayer orig, ScavengerAI self, RelationshipTracker.DynamicRelationship dRelation)
        {
            return IsPursuer(self.creature) ? 0f : orig(self, dRelation);
        }

        private static float ScavengerAI_CurrentPlayerAggression(On.ScavengerAI.orig_CurrentPlayerAggression orig, ScavengerAI self, AbstractCreature player)
        {
            return IsPursuer(self.creature) ? 1f : orig(self, player);
        }

        private static CreatureTemplate.Relationship ScavengerAI_UpdateDynamicRelationship(On.ScavengerAI.orig_IUseARelationshipTracker_UpdateDynamicRelationship orig, ScavengerAI self, RelationshipTracker.DynamicRelationship dRelation)
        {
            AbstractCreature other = dRelation?.trackerRep?.representedCreature;
            if (other != null)
            {
                bool selfIsPursuer = IsPursuer(self.creature);
                if (selfIsPursuer && IsScavengerKind(other.creatureTemplate))
                {
                    // Other scavengers are rivals, never pack mates. Attack when they get in the way.
                    Tag(dRelation, ScavengerAI.ViolenceType.Lethal);
                    return new CreatureTemplate.Relationship(CreatureTemplate.Relationship.Type.Attacks, 0.5f);
                }
                if (!selfIsPursuer && IsPursuer(other))
                {
                    // Every scavenger treats the Pursuer as a hostile intruder.
                    Tag(dRelation, ScavengerAI.ViolenceType.Lethal);
                    return new CreatureTemplate.Relationship(CreatureTemplate.Relationship.Type.Attacks, 0.7f);
                }
            }
            return orig(self, dRelation);
        }

        private static void Tag(RelationshipTracker.DynamicRelationship dRelation, ScavengerAI.ViolenceType type)
        {
            if (dRelation.state is ScavengerAI.ScavengerTrackState state)
            {
                state.taggedViolenceType = type;
            }
        }

        private static Tracker.CreatureRepresentation ScavengerAI_PackLeader(On.ScavengerAI.orig_PackLeader orig, ScavengerAI self)
        {
            return IsPursuer(self.creature) ? null : orig(self);
        }

        private static void ScavengerAI_DecideBehavior(On.ScavengerAI.orig_DecideBehavior orig, ScavengerAI self)
        {
            orig(self);
            if (!IsPursuer(self.creature))
            {
                return;
            }
            // No socializing, no guard duty, no following pack leaders: hunt or travel.
            if (self.behavior == ScavengerAI.Behavior.CommunicateWithPlayer || self.behavior == ScavengerAI.Behavior.FindPackLeader || self.behavior == ScavengerAI.Behavior.GuardOutpost)
            {
                bool elsewhere = self.creature.abstractAI != null && self.creature.pos.room != self.creature.abstractAI.MigrationDestination.room;
                self.behavior = elsewhere ? ScavengerAI.Behavior.Travel : ScavengerAI.Behavior.Idle;
            }
            self.tradeSpot = null;
            self.wantToTradeWith = null;
        }

        private static void ScavengerAI_SocialEvent(On.ScavengerAI.orig_SocialEvent orig, ScavengerAI self, SocialEventRecognizer.EventID ID, Creature subjectCrit, Creature objectCrit, PhysicalObject involvedItem)
        {
            if (IsPursuer(self.creature) || IsPursuer(subjectCrit?.abstractCreature) || IsPursuer(objectCrit?.abstractCreature))
            {
                return; // gifts, threats and killings involving the Pursuer change nobody's mind
            }
            orig(self, ID, subjectCrit, objectCrit, involvedItem);
        }

        // ---------------------------------------------------------------- deaths and kills

        private static void AbstractCreature_Die(On.AbstractCreature.orig_Die orig, AbstractCreature self)
        {
            bool wasAlive = self.state != null && self.state.alive;
            orig(self);
            if (wasAlive && IsPursuer(self))
            {
                HuntedSession.Current.OnPursuerDied(self);
            }
        }

        private static void Creature_Die(On.Creature.orig_Die orig, Creature self)
        {
            AbstractCreature killer = self.killTag;
            bool wasAlive = !self.dead;
            orig(self);
            if (wasAlive && killer != null && self is Scavenger && !IsPursuer(self.abstractCreature) && IsPursuer(killer))
            {
                HuntedSession.Current.OnScavengerKilledByPursuer(self);
            }
        }

        private static void Player_Die(On.Player.orig_Die orig, Player self)
        {
            bool wasAlive = !self.dead;
            orig(self);
            if (wasAlive)
            {
                HuntedSession.Current?.OnPlayerDied(self);
            }
        }

        private static void SocialEventRecognizer_Killing(On.SocialEventRecognizer.orig_Killing orig, SocialEventRecognizer self, Creature killer, Creature victim)
        {
            if (victim != null && IsPursuer(victim.abstractCreature))
            {
                // Killing the Pursuer is not a crime against the scavenger community.
                self.SocialEvent(SocialEventRecognizer.EventID.Killing, killer, victim, null);
                if (killer is Player player && player.SessionRecord != null)
                {
                    player.SessionRecord.AddKill(victim);
                }
                return;
            }
            orig(self, killer, victim);
        }

        // ---------------------------------------------------------------- looks and HUD

        private static void ScavengerGraphics_ctor(On.ScavengerGraphics.orig_ctor orig, ScavengerGraphics self, PhysicalObject ow)
        {
            orig(self, ow);
            if (!(ow is Scavenger scavenger) || !IsPursuer(scavenger.abstractCreature))
            {
                return;
            }
            // Dark, desaturated body with blood-red markings and eyes: never mistaken for a local.
            self.bodyColor = new HSLColor(0.62f, 0.15f, 0.14f);
            self.headColor = new HSLColor(0.62f, 0.12f, 0.1f);
            self.bellyColor = new HSLColor(0.6f, 0.1f, 0.2f);
            self.decorationColor = new HSLColor(0.99f, 0.95f, 0.45f);
            self.eyeColor = new HSLColor(0.99f, 1f, 0.55f);
            self.bodyColorBlack = 0.1f;
            self.headColorBlack = 0.1f;
            self.bellyColorBlack = 0.1f;
        }

        private static void HUD_InitSinglePlayerHud(On.HUD.HUD.orig_InitSinglePlayerHud orig, HUD.HUD self, RoomCamera cam)
        {
            orig(self, cam);
            try
            {
                self.AddPart(new HuntedHudPart(self));
            }
            catch (Exception e)
            {
                HuntedLog.Error("HUD hook failed", e);
            }
        }

        private static void SleepAndDeathScreen_GetDataFromGame(On.Menu.SleepAndDeathScreen.orig_GetDataFromGame orig, Menu.SleepAndDeathScreen self, Menu.KarmaLadderScreen.SleepDeathScreenDataPackage package)
        {
            orig(self, package);
            string summary = HuntedSession.LastCycleSummary;
            if (string.IsNullOrEmpty(summary) || self.pages == null || self.pages.Count == 0)
            {
                return;
            }
            try
            {
                HuntedSession.LastCycleSummary = null;
                var label = new Menu.MenuLabel(self, self.pages[0], summary, new Vector2(0f, 52f), new Vector2(1366f, 20f), false);
                label.label.color = new Color(0.9f, 0.35f, 0.3f);
                self.pages[0].subObjects.Add(label);
            }
            catch (Exception e)
            {
                HuntedLog.Error("Sleep screen hook failed", e);
            }
        }
    }
}
