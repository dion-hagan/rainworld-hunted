using System;
using System.Collections.Generic;
using Hunted.Core;
using MoreSlugcats;
using Noise;
using RWCustom;
using UnityEngine;

namespace Hunted.Game
{
    /// <summary>
    /// Stage 2 brain: a real slugcat body (Downpour's NPC slugcat, the <see cref="Player"/>
    /// class) driven by this AI. The body reads its inputs from <see cref="Update"/> every
    /// tick, exactly like a slugpup does, so it inherits real slugcat movement.
    ///
    /// Two layers:
    ///  - the movement layer (<see cref="Move"/> and its helpers) is the slugpup's
    ///    path-to-input conversion, ported so it can be tuned for pursuit;
    ///  - the decision layer (<see cref="Think"/>) is scavenger-like: hunt the player,
    ///    pick a throwing position, throw when lined up, grab better weapons, flee
    ///    predators it cannot fight, and head for cover before the rain.
    ///
    /// It subclasses <see cref="SlugNPCAI"/> only so that <c>Player.AI</c> is non-null,
    /// which keeps every NPC code path in the game (food, saves, HUD) treating this
    /// body as an NPC rather than as the human player.
    /// </summary>
    public class PursuerAI : SlugNPCAI, IUseARelationshipTracker, IReactToSocialEvents
    {
        public enum Mode
        {
            /// <summary>Player not known: head for the player's room.</summary>
            Travel,
            /// <summary>Player recently seen, now lost: go to the last known position.</summary>
            Search,
            /// <summary>Better weapon nearby: go and take it.</summary>
            Scavenge,
            /// <summary>Player visible or very close: take a throwing position and attack.</summary>
            Engage,
            /// <summary>Outgunned by a predator: break away, throwing when lined up.</summary>
            Flee,
            /// <summary>Rain imminent: head for a shelter that is not the player's.</summary>
            EscapeRain,
        }

        private const int RainHideTicks = 40 * 45;
        private const float ThrowRangePx = 520f;
        private const float BombMinRangePx = 160f;
        private const int SenseInterval = 40 * 8;
        private const int RememberTargetTicks = 40 * 30;

        public Mode mode = Mode.Travel;

        // Movement layer state (ported from the slugpup AI).
        private bool jumping;
        private bool catchPoles;
        private int jumpDir;
        private int forceJump;
        private int catchDelay;
        private int transportDelay;
        private int turnDelay;
        private int throwAtTarget;
        private readonly bool cutCorners;

        // Decision layer state.
        private Tracker.CreatureRepresentation target;
        private PhysicalObject wantedItem;
        private WorldCoordinate attackPos;
        private WorldCoordinate testThrowPos;
        private int changeAttackPositionDelay;
        private int senseCooldown;
        private int fleeHold;
        private int errorLogCooldown;

        // Learned tactics: one decision per TacticHoldTicks while armed and engaging.
        private const int TacticHoldTicks = 20;
        private Tactic tactic = Tactic.Throw;
        private int tacticTicks;
        private readonly float[] situation = new float[PursuerLearner.FeatureCount];
        private readonly List<IntVector2> previousAttackPositions = new List<IntVector2>();
        private readonly List<IntVector2> targetArea = new List<IntVector2>(50);
        private readonly List<IntVector2> floodFill = new List<IntVector2>(50);

        public PursuerAI(AbstractCreature creature, World world) : base(creature, world)
        {
            // The slugpup constructor installed the modules; only the weights change.
            // Prey and friends never drive this AI: the decision layer picks the target itself.
            SetWeight(preyTracker, 0f);
            SetWeight(friendTracker, 0f);
            SetWeight(threatTracker, 1f);
            cutCorners = false;
            attackPos = creature.pos;
            testThrowPos = creature.pos;
        }

        private void SetWeight(AIModule module, float weight)
        {
            UtilityComparer.UtilityTracker tracker = utilityComparer.GetUtilityTracker(module);
            if (tracker != null)
            {
                tracker.weight = weight;
            }
        }

        public string DebugState
        {
            get
            {
                string s = mode.ToString();
                if (mode == Mode.Engage)
                {
                    s += "/" + tactic;
                }
                if (target != null)
                {
                    s += target.VisualContact ? " (sees you)" : " (last seen " + (target.TicksSinceSeen / 40) + "s ago)";
                }
                return s;
            }
        }

        public override void NewRoom(Room room)
        {
            base.NewRoom(room);
            previousAttackPositions.Clear();
            attackPos = creature.pos;
            testThrowPos = creature.pos;
            wantedItem = null;
            throwAtTarget = 0;
            jumping = false;
        }

        // ------------------------------------------------------------------ per tick

        /// <summary>Called by Player.checkInput every tick the body is conscious. Must leave cat.input[0] set.</summary>
        public override void Update()
        {
            try
            {
                Think();
            }
            catch (Exception e)
            {
                if (errorLogCooldown <= 0)
                {
                    errorLogCooldown = 400;
                    HuntedLog.Error("PursuerAI failed; standing still this tick", e);
                }
                if (cat != null && cat.input != null)
                {
                    cat.input[0] = default(Player.InputPackage);
                }
            }
            if (errorLogCooldown > 0)
            {
                errorLogCooldown--;
            }
        }

        /// <summary>
        /// Room noises come from other objects' updates (the player's own movement raises
        /// them), so an exception here would abort the player's update, not ours. Never throw,
        /// and ignore noises until the modules have a room to place them in.
        /// </summary>
        public override void HeardNoise(InGameNoise noise)
        {
            if (noiseTracker == null || noiseTracker.room == null)
            {
                return;
            }
            try
            {
                base.HeardNoise(noise);
            }
            catch (Exception e)
            {
                if (errorLogCooldown <= 0)
                {
                    errorLogCooldown = 400;
                    HuntedLog.Error("PursuerAI could not process a noise", e);
                }
            }
        }

        private void UpdateModules()
        {
            // What ArtificialIntelligence.Update does for every AI, minus expedition extras.
            timeInRoom++;
            for (int i = 0; i < modules.Count; i++)
            {
                modules[i].Update();
            }
        }

        /// <summary>
        /// The game hands the room to the AI through Creature.NewRoom when the body enters a
        /// room. Paths that skip it (a body created straight into a realized room, a second
        /// Realize on a live body) would leave every module without a room, so catch up here.
        /// </summary>
        private void EnsureRoom(Player body)
        {
            if (lastRoom != body.room.abstractRoom.index)
            {
                NewRoom(body.room);
            }
        }

        private void Think()
        {
            Player body = cat;
            if (body == null || body.room == null)
            {
                return;
            }
            EnsureRoom(body);
            UpdateModules();
            forceJump = Math.Max(forceJump - 1, 0);
            catchDelay = Math.Max(catchDelay - 1, 0);
            turnDelay = Math.Max(turnDelay - 1, 0);
            if (transportDelay > 0)
            {
                transportDelay--;
            }
            if (senseCooldown > 0)
            {
                senseCooldown--;
            }
            if (fleeHold > 0)
            {
                fleeHold--;
            }

            HuntedSession session = HuntedSession.Current;
            World world = creature.world;
            AbstractCreature player = session != null ? session.TargetPlayer() : null;
            target = FindTarget(player);
            Sense(player);

            bool rainSoon = world.rainCycle != null && !creature.ignoreCycle && world.rainCycle.TimeUntilRain < RainHideTicks;
            float threat = threatTracker.Utility();
            float held = BestHeldWeaponValue();
            bool lethal = held >= 1f;

            if (rainSoon)
            {
                mode = Mode.EscapeRain;
            }
            else if (fleeHold > 0 || threat > (lethal ? 0.75f : 0.5f))
            {
                mode = Mode.Flee;
                if (fleeHold == 0)
                {
                    fleeHold = 60;
                }
            }
            else if (target != null && (target.VisualContact || target.TicksSinceSeen < 200))
            {
                mode = Mode.Engage;
            }
            else if (WantsBetterWeapon(held))
            {
                mode = Mode.Scavenge;
            }
            else if (target != null && target.TicksSinceSeen < RememberTargetTicks)
            {
                mode = Mode.Search;
            }
            else
            {
                mode = Mode.Travel;
            }

            WorldCoordinate coord = creature.pos;
            switch (mode)
            {
                case Mode.EscapeRain:
                    coord = session != null ? session.RainShelterCoordinate(world, creature.pos.room) ?? creature.pos : creature.pos;
                    break;
                case Mode.Flee:
                    coord = threatTracker.FleeTo(creature.pos, 10, 30, true);
                    if (lethal)
                    {
                        ConsiderThrowingAtThreat();
                    }
                    break;
                case Mode.Engage:
                    coord = EngageUpdate(held);
                    break;
                case Mode.Scavenge:
                    coord = wantedItem != null ? wantedItem.abstractPhysicalObject.pos : creature.pos;
                    break;
                case Mode.Search:
                    coord = target.BestGuessForPosition();
                    break;
                default:
                    coord = session != null ? session.HuntTargetCoordinate(world) ?? creature.pos : creature.pos;
                    break;
            }
            coord = KeepOutOfShelters(coord, session, world);
            creature.abstractAI.SetDestination(coord);

            GrabUpdate(held);
            Move();
        }

        /// <summary>The Pursuer never follows the player into a shelter (or gate): it waits outside instead.</summary>
        private WorldCoordinate KeepOutOfShelters(WorldCoordinate coord, HuntedSession session, World world)
        {
            if (mode == Mode.EscapeRain)
            {
                return coord;
            }
            AbstractRoom room = world.GetAbstractRoom(coord.room);
            if (room == null || (!room.shelter && !room.gate))
            {
                return coord;
            }
            WorldCoordinate? outside = session != null ? session.HuntTargetCoordinate(world) : null;
            if (outside.HasValue && outside.Value.room != coord.room)
            {
                return outside.Value;
            }
            return creature.pos;
        }

        // ------------------------------------------------------------------ perception

        private Tracker.CreatureRepresentation FindTarget(AbstractCreature preferred)
        {
            Tracker.CreatureRepresentation best = null;
            List<AbstractCreature> players = creature.world.game.Players;
            if (players == null)
            {
                return null;
            }
            for (int i = 0; i < players.Count; i++)
            {
                AbstractCreature p = players[i];
                if (p == null || p.state == null || !p.state.alive)
                {
                    continue;
                }
                Tracker.CreatureRepresentation rep = tracker.RepresentationForCreature(p, false);
                if (rep == null)
                {
                    continue;
                }
                if (best == null || rep.TicksSinceSeen < best.TicksSinceSeen || (p == preferred && rep.TicksSinceSeen == best.TicksSinceSeen))
                {
                    best = rep;
                }
            }
            return best;
        }

        /// <summary>
        /// The Pursuer tracks the player down to the room; inside the room it relies on its
        /// eyes, but every few seconds it gets a fix on a player it has lost so a hunt can
        /// never stall forever behind a corner.
        /// </summary>
        private void Sense(AbstractCreature player)
        {
            if (player == null || senseCooldown > 0 || player.pos.room != creature.pos.room || player.realizedCreature == null)
            {
                return;
            }
            Tracker.CreatureRepresentation rep = tracker.RepresentationForCreature(player, false);
            if (rep == null || rep.TicksSinceSeen > 400)
            {
                tracker.SeeCreature(player);
                senseCooldown = SenseInterval;
            }
        }

        // ------------------------------------------------------------------ combat

        public static float WeaponValue(PhysicalObject obj)
        {
            if (obj == null)
            {
                return 0f;
            }
            if (obj is Spear spear)
            {
                if (spear.mode == Weapon.Mode.StuckInWall)
                {
                    return 0f;
                }
                var abstractSpear = spear.abstractPhysicalObject as AbstractSpear;
                if (abstractSpear != null && abstractSpear.explosive)
                {
                    return 3f;
                }
                if (abstractSpear != null && abstractSpear.electric)
                {
                    return 2f;
                }
                return 1f;
            }
            if (obj is ScavengerBomb)
            {
                return 2f;
            }
            if (obj is Rock)
            {
                return 0.5f;
            }
            return 0f;
        }

        private float BestHeldWeaponValue()
        {
            float best = 0f;
            for (int i = 0; i < cat.grasps.Length; i++)
            {
                if (cat.grasps[i] != null)
                {
                    best = Mathf.Max(best, WeaponValue(cat.grasps[i].grabbed));
                }
            }
            return best;
        }

        private PhysicalObject HeldWeapon()
        {
            PhysicalObject best = null;
            float bestValue = 0f;
            for (int i = 0; i < cat.grasps.Length; i++)
            {
                if (cat.grasps[i] != null && WeaponValue(cat.grasps[i].grabbed) > bestValue)
                {
                    bestValue = WeaponValue(cat.grasps[i].grabbed);
                    best = cat.grasps[i].grabbed;
                }
            }
            return best;
        }

        private bool HoldingThis(PhysicalObject obj)
        {
            for (int i = 0; i < cat.grasps.Length; i++)
            {
                if (cat.grasps[i] != null && cat.grasps[i].grabbed == obj)
                {
                    return true;
                }
            }
            return false;
        }

        private bool CanGrabItem(PhysicalObject obj)
        {
            return obj != null && cat.CanIPickThisUp(obj) && cat.NPCGrabCheck(obj);
        }

        /// <summary>Best reachable weapon lying around this room that beats what is in hand.</summary>
        private PhysicalObject NearestBetterWeapon(float held)
        {
            PhysicalObject best = null;
            float bestScore = 0f;
            for (int i = 0; i < itemTracker.ItemCount; i++)
            {
                ItemTracker.ItemRepresentation rep = itemTracker.GetRep(i);
                PhysicalObject obj = rep.representedItem.realizedObject;
                if (obj == null || obj.room != cat.room || obj.grabbedBy.Count > 0 || HoldingThis(obj))
                {
                    continue;
                }
                float value = WeaponValue(obj);
                if (value <= held || value <= 0f || !pathFinder.CoordinateReachable(rep.representedItem.pos))
                {
                    continue;
                }
                float distance = Vector2.Distance(obj.firstChunk.pos, cat.firstChunk.pos);
                float score = value * Mathf.Clamp01(1f - distance / 2500f);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = obj;
                }
            }
            return best;
        }

        private bool WantsBetterWeapon(float held)
        {
            if (held >= 1f)
            {
                wantedItem = null;
                return false;
            }
            if (wantedItem != null && (wantedItem.room != cat.room || wantedItem.grabbedBy.Count > 0 || HoldingThis(wantedItem) || wantedItem.slatedForDeletetion))
            {
                wantedItem = null;
            }
            if (wantedItem == null)
            {
                wantedItem = NearestBetterWeapon(held);
            }
            return wantedItem != null;
        }

        private WorldCoordinate EngageUpdate(float held)
        {
            Creature victim = target.representedCreature.realizedCreature;
            if (victim == null)
            {
                return target.BestGuessForPosition();
            }
            if (held < 0.5f)
            {
                // Unarmed: arm up if anything usable is close, otherwise keep the pressure on.
                if (WantsBetterWeapon(held) && wantedItem != null && Vector2.Distance(wantedItem.firstChunk.pos, cat.firstChunk.pos) < 600f)
                {
                    return wantedItem.abstractPhysicalObject.pos;
                }
                return target.BestGuessForPosition();
            }
            ChooseTactic(victim, held);
            WorldCoordinate coord;
            switch (tactic)
            {
                case Tactic.CloseIn:
                    coord = target.BestGuessForPosition();
                    break;
                case Tactic.Wait:
                    coord = creature.pos;
                    break;
                default:
                    FindAttackPosition(target);
                    coord = attackPos;
                    break;
            }
            if (tactic != Tactic.Reposition && tactic != Tactic.CloseIn && throwAtTarget == 0 && turnDelay == 0 && victim.room == cat.room)
            {
                int chunk = UnityEngine.Random.Range(0, victim.bodyChunks.Length);
                if (GoodAttackPos(victim.bodyChunks[chunk]))
                {
                    throwAtTarget = (int)Mathf.Sign(victim.bodyChunks[chunk].pos.x - cat.firstChunk.pos.x);
                }
            }
            return coord;
        }

        /// <summary>
        /// Every TacticHoldTicks, asks the learner which tactic fits the situation. With
        /// learning off (or no session) the Pursuer always throws, which is the old behaviour.
        /// </summary>
        private void ChooseTactic(Creature victim, float held)
        {
            if (tacticTicks-- > 0)
            {
                return;
            }
            tacticTicks = TacticHoldTicks;
            PursuerLearner learner = HuntedSession.Current?.Learner;
            if (learner == null || !learner.Enabled)
            {
                tactic = Tactic.Throw;
                return;
            }
            Vector2 me = cat.mainBodyChunk.pos;
            Vector2 them = victim.mainBodyChunk.pos;
            Vector2 offset = them - me;
            bool armed = victim is Player p && p.grasps != null && System.Array.Exists(p.grasps, g => g != null && g.grabbed is Weapon);
            int i = 0;
            situation[i++] = Mathf.Clamp(offset.x / 400f, -1f, 1f);
            situation[i++] = Mathf.Clamp(offset.y / 400f, -1f, 1f);
            situation[i++] = Mathf.Clamp01(offset.magnitude / 400f);
            situation[i++] = target.VisualContact ? 1f : 0f;
            situation[i++] = Mathf.Clamp01(target.TicksSinceSeen / 400f);
            situation[i++] = offset.y > 20f ? 1f : 0f;
            situation[i++] = armed ? 1f : 0f;
            situation[i++] = Mathf.Clamp01(victim.mainBodyChunk.vel.magnitude / 10f);
            situation[i++] = Mathf.Clamp01(held / 3f);
            situation[i++] = threatTracker.Utility();
            situation[i++] = GoodAttackPos(victim.mainBodyChunk) ? 1f : 0f;
            situation[i++] = HeldWeapon() is ScavengerBomb ? 1f : 0f;
            tactic = learner.Choose(situation);
        }

        private void ConsiderThrowingAtThreat()
        {
            Tracker.CreatureRepresentation rep = threatTracker.mostThreateningCreature;
            Creature threat = rep?.representedCreature?.realizedCreature;
            if (threat == null || threat.room != cat.room || throwAtTarget != 0 || turnDelay > 0)
            {
                return;
            }
            int chunk = UnityEngine.Random.Range(0, threat.bodyChunks.Length);
            if (GoodAttackPos(threat.bodyChunks[chunk]) && UnityEngine.Random.value < 0.1f)
            {
                throwAtTarget = (int)Mathf.Sign(threat.bodyChunks[chunk].pos.x - cat.firstChunk.pos.x);
            }
        }

        /// <summary>Level with the chunk (slugcat throws are horizontal), in range, in sight, and not in bomb splash range.</summary>
        private bool GoodAttackPos(BodyChunk chunk)
        {
            Vector2 dir = Custom.DirVec(cat.mainBodyChunk.pos, chunk.pos);
            if (dir.y > 0.05f || dir.y < -0.2f)
            {
                return false;
            }
            float distance = Vector2.Distance(cat.mainBodyChunk.pos, chunk.pos);
            if (distance > ThrowRangePx)
            {
                return false;
            }
            if (HeldWeapon() is ScavengerBomb && distance < BombMinRangePx)
            {
                return false;
            }
            return VisualContact(chunk);
        }

        private void FindAttackPosition(Tracker.CreatureRepresentation rep)
        {
            IntVector2 targetTile = rep.BestGuessForPosition().Tile;
            if (rep.representedCreature.creatureTemplate.PreBakedPathingIndex < 0)
            {
                targetArea.Clear();
                targetArea.Add(targetTile);
            }
            else
            {
                QuickConnectivity.FloodFill(cat.room, rep.representedCreature.creatureTemplate, targetTile, 20, 500, targetArea);
            }
            IntVector2 pos;
            if (UnityEngine.Random.value < 0.5f && targetArea.Count > 0)
            {
                IntVector2 origin = targetArea[UnityEngine.Random.Range(0, targetArea.Count)];
                pos = origin;
                int side = UnityEngine.Random.value < 0.5f ? -1 : 1;
                for (int i = 0; i < 40 && !cat.room.HasAnySolid(origin + new IntVector2(side * i, 0)); i++)
                {
                    if (i > 5 && pathFinder.CoordinateViable(cat.room.GetWorldCoordinate(origin + new IntVector2(side * i, 0))))
                    {
                        pos = origin + new IntVector2(side * i, 0);
                        break;
                    }
                }
            }
            else
            {
                pos = creature.pos.Tile + new IntVector2(UnityEngine.Random.Range(1, 10) * (UnityEngine.Random.value < 0.5f ? -1 : 1), UnityEngine.Random.Range(1, 10) * (UnityEngine.Random.value < 0.5f ? -1 : 1));
            }
            WorldCoordinate candidate = cat.room.GetWorldCoordinate(pos);
            if (pathFinder.CoordinateViable(candidate) && SpearThrowPositionScore(candidate, targetTile) > SpearThrowPositionScore(testThrowPos, targetTile))
            {
                testThrowPos = candidate;
            }
            if (testThrowPos != attackPos && (Custom.ManhattanDistance(creature.pos, attackPos) < 3 || SpearThrowPositionScore(testThrowPos, targetTile) > SpearThrowPositionScore(attackPos, targetTile) + changeAttackPositionDelay))
            {
                attackPos = testThrowPos;
                changeAttackPositionDelay = 300;
                previousAttackPositions.Insert(0, testThrowPos.Tile);
                if (previousAttackPositions.Count > 20)
                {
                    previousAttackPositions.RemoveAt(20);
                }
            }
            if (changeAttackPositionDelay > 0)
            {
                changeAttackPositionDelay--;
            }
        }

        private float SpearThrowPositionScore(WorldCoordinate tst, IntVector2 targetTile)
        {
            if (!pathFinder.CoordinateViable(tst))
            {
                return float.MinValue;
            }
            QuickConnectivity.FloodFill(cat.room, creature.creatureTemplate, tst.Tile, 40, 500, floodFill);
            int maxY = int.MinValue;
            int minY = int.MaxValue;
            for (int i = 0; i < targetArea.Count; i++)
            {
                maxY = Math.Max(maxY, targetArea[i].y);
                minY = Math.Min(minY, targetArea[i].y);
            }
            float score = 0f;
            for (int j = 0; j < floodFill.Count; j++)
            {
                if (floodFill[j].y < minY || floodFill[j].y > maxY || !cat.room.VisualContact(floodFill[j], targetTile))
                {
                    continue;
                }
                for (int k = 0; k < targetArea.Count; k++)
                {
                    if (floodFill[j].y == targetArea[k].y && NoSolidTilesBetween(floodFill[j].x, targetArea[k].x, floodFill[j].y))
                    {
                        score += 1f;
                    }
                }
            }
            score = score != 0f ? score + 100f : 1f;
            for (int l = 0; l < floodFill.Count; l++)
            {
                if (floodFill[l].FloatDist(creature.pos.Tile) < 3f)
                {
                    score *= 2f;
                    break;
                }
            }
            score *= Custom.LerpMap(tst.Tile.FloatDist(targetTile), 30f, 60f, 1f, 0f);
            score *= Custom.LerpMap(tst.Tile.FloatDist(targetTile), 5f, 0f, 1f, 0.1f);
            score *= 1f - threatTracker.ThreatOfArea(tst, true);
            score *= Math.Abs(tst.Tile.y - targetTile.y) >= 3 ? Mathf.InverseLerp(5f, 10f, tst.Tile.FloatDist(targetTile)) : 1f;
            for (int n = 1; n < previousAttackPositions.Count; n++)
            {
                score -= Custom.LerpMap(tst.Tile.FloatDist(previousAttackPositions[n]), 0f, 5f, 50f, 0f);
            }
            return score;
        }

        private bool NoSolidTilesBetween(int xA, int xB, int y)
        {
            if (xB < xA)
            {
                int t = xA;
                xA = xB;
                xB = t;
            }
            for (int x = xA; x <= xB; x++)
            {
                if (cat.room.HasAnySolid(x, y))
                {
                    return false;
                }
            }
            return true;
        }

        private void GrabUpdate(float held)
        {
            if (wantedItem == null)
            {
                return;
            }
            if (HoldingThis(wantedItem) || wantedItem.room != cat.room || wantedItem.grabbedBy.Count > 0 || wantedItem.slatedForDeletetion)
            {
                wantedItem = null;
                return;
            }
            if (!Custom.DistLess(cat.mainBodyChunk.pos, wantedItem.firstChunk.pos, 40f) || !CanGrabItem(wantedItem))
            {
                return;
            }
            // Hands full of something worse: drop it for the better weapon.
            if (cat.grasps[0] != null && WeaponValue(cat.grasps[0].grabbed) < WeaponValue(wantedItem))
            {
                cat.ReleaseGrasp(0);
            }
            cat.NPCForceGrab(wantedItem);
        }

        // ------------------------------------------------------------------ movement layer (slugpup port)

        private void Move()
        {
            Player.InputPackage input = default(Player.InputPackage);
            MovementConnection movementConnection = (pathFinder as StandardPather).FollowPath(creature.pos, true);
            AbstractCreatureAI abstractAI = creature.abstractAI;
            bool atDestination = abstractAI.destination.room == creature.Room.index && (new Vector2(abstractAI.destination.x, abstractAI.destination.y) - new Vector2(creature.pos.x, creature.pos.y)).magnitude < 1.5f;
            if (wantedItem == null && atDestination)
            {
                movementConnection = default(MovementConnection);
                cat.standing = true;
            }

            if (jumping)
            {
                input.y = catchPoles ? 1 : 0;
                input.x = jumpDir;
                input.jmp = true;
                if ((cat.bodyMode != Player.BodyModeIndex.Default || OnHorizontalBeam()) && forceJump == 0)
                {
                    jumping = false;
                }
            }
            else if (movementConnection != default(MovementConnection))
            {
                catchPoles = false;
                if (((movementConnection.type == MovementConnection.MovementType.ShortCut && cat.room.GetTile(movementConnection.startCoord.Tile).Terrain == Room.Tile.TerrainType.ShortcutEntrance && cat.room.shortcutData(movementConnection.startCoord.Tile).LeadingSomewhere) || (movementConnection.type == MovementConnection.MovementType.NPCTransportation && transportDelay <= 0)) && creature.pos == movementConnection.startCoord)
                {
                    if (movementConnection.type == MovementConnection.MovementType.NPCTransportation)
                    {
                        cat.NPCTransportationDestination = movementConnection.destinationCoord;
                        transportDelay = 80;
                    }
                    cat.enteringShortCut = movementConnection.StartTile;
                }
                bool wantJump = false;
                int jumpDirection = 0;
                bool jumpCatchPoles = false;
                bool flag3 = false;
                bool dropIntoTunnel = false;
                bool hangingFromBeam = false;
                bool beamJump = false;
                bool climbPhase = false;
                bool beamToBeam = false;
                WorldCoordinate startCoord = movementConnection.startCoord;
                WorldCoordinate destinationCoord = movementConnection.destinationCoord;
                List<MovementConnection> upcoming = GetUpcoming();
                if (upcoming != null)
                {
                    for (int i = 0; i < upcoming.Count; i++)
                    {
                        if (wantJump && !AnyClimb() && !TileClimbable(upcoming[i].destinationCoord) && Mathf.Abs(upcoming[i].destinationCoord.x - creature.pos.x) <= upcoming[i].destinationCoord.y - creature.pos.y + 1 && upcoming[i].destinationCoord.y > creature.pos.y && jumpDirection == 0)
                        {
                            jumpDirection = (int)Mathf.Sign(upcoming[i].destinationCoord.x - creature.pos.x);
                        }
                        if (!climbPhase && ((!AnyClimb() && TileClimbable(upcoming[i].destinationCoord) && cat.room.GetTile(upcoming[i].destinationCoord).verticalBeam && upcoming[i].destinationCoord.y == upcoming[i].startCoord.y + 1 && upcoming[i].destinationCoord.x == upcoming[i].startCoord.x) || (!AnyClimb() && Tunnel(upcoming[i].destinationCoord) && upcoming[i].destinationCoord.y == upcoming[i].startCoord.y - 1) || cat.bodyMode == Player.BodyModeIndex.Default))
                        {
                            climbPhase = true;
                            wantJump = false;
                        }
                        if (!wantJump && !climbPhase && ((cat.animation == Player.AnimationIndex.LedgeGrab && i == 0) || (cutCorners && i < 2 && OnVerticalBeam() && upcoming[i].destinationCoord.x != creature.pos.x && VisualContact(cat.room.MiddleOfTile(upcoming[i].destinationCoord), 0f)) || (!AnyClimb() && !TileClimbable(upcoming[i].destinationCoord) && !Tunnel(upcoming[i].destinationCoord) && Mathf.Abs(upcoming[i].destinationCoord.x - creature.pos.x) <= upcoming[i].destinationCoord.y - creature.pos.y + 1 && upcoming[i].destinationCoord.y > creature.pos.y)))
                        {
                            wantJump = true;
                            dropIntoTunnel = false;
                            jumpDirection = upcoming[i].destinationCoord.x > creature.pos.x ? 1 : -1;
                            if (upcoming[i].destinationCoord.x == creature.pos.x)
                            {
                                jumpDirection = 0;
                            }
                            if (TileClimbable(upcoming[i].destinationCoord))
                            {
                                jumpCatchPoles = true;
                            }
                        }
                        if (!hangingFromBeam && cat.animation == Player.AnimationIndex.HangFromBeam)
                        {
                            hangingFromBeam = true;
                        }
                        if (!wantJump && !dropIntoTunnel && upcoming[i].destinationCoord.y < upcoming[i].startCoord.y && Tunnel(upcoming[i].destinationCoord) && i == 1 && cat.bodyMode != Player.BodyModeIndex.CorridorClimb)
                        {
                            dropIntoTunnel = true;
                        }
                    }
                }
                if (!wantJump && !climbPhase && !AnyClimb() && movementConnection.startCoord.y < movementConnection.destinationCoord.y && movementConnection.startCoord.x == movementConnection.destinationCoord.x)
                {
                    wantJump = true;
                    dropIntoTunnel = false;
                    jumpCatchPoles = true;
                }
                if (!beamToBeam && catchDelay == 0 && cat.room.GetTile(movementConnection.startCoord).horizontalBeam && cat.room.GetTile(movementConnection.destinationCoord).horizontalBeam && !OnHorizontalBeam())
                {
                    beamToBeam = true;
                }
                if (!TileClimbable(movementConnection.destinationCoord) && OnVerticalBeam() && VisualContact(cat.room.MiddleOfTile(movementConnection.destinationCoord), 0f))
                {
                    beamJump = true;
                }
                if (movementConnection.type == MovementConnection.MovementType.DropToClimb)
                {
                    catchPoles = true;
                    catchDelay = 5;
                }
                if (wantJump)
                {
                    Jump(jumpDirection, jumpCatchPoles, ref input);
                }
                if (startCoord.x > destinationCoord.x)
                {
                    input.x--;
                }
                if (startCoord.x < destinationCoord.x)
                {
                    input.x++;
                }
                if (startCoord.y < destinationCoord.y && AnyClimb())
                {
                    input.y++;
                }
                if ((startCoord.y > destinationCoord.y && startCoord.x == destinationCoord.x) || dropIntoTunnel)
                {
                    input.y--;
                }
                if (beamJump)
                {
                    input.jmp = UnityEngine.Random.Range(0, 10) != 0;
                }
                if (flag3 && cat.bodyMode != Player.BodyModeIndex.Crawl)
                {
                    input.y = UnityEngine.Random.Range(0, 2) != 0 ? -1 : 0;
                }
                else if ((!flag3 && cat.bodyMode == Player.BodyModeIndex.Crawl) || beamToBeam)
                {
                    input.y = UnityEngine.Random.Range(0, 2) != 0 ? 1 : 0;
                }
                if ((OnAnyBeam() && movementConnection.type > MovementConnection.MovementType.SemiDiagonalReach && movementConnection.type < MovementConnection.MovementType.LizardTurn) || (OnHorizontalBeam() && movementConnection.startCoord.y > movementConnection.destinationCoord.y))
                {
                    input.jmp = UnityEngine.Random.Range(0, 10) != 0;
                }
                if (movementConnection.destinationCoord.y < creature.pos.y && ((Tunnel(movementConnection.destinationCoord) && cat.bodyMode != Player.BodyModeIndex.CorridorClimb) || cat.room.GetTile(cat.room.GetTilePosition(cat.bodyChunks[1].pos) - new IntVector2(0, 1)).Terrain == Room.Tile.TerrainType.Floor))
                {
                    input.y = UnityEngine.Random.Range(0, 10) != 0 ? -1 : 0;
                }
                if (hangingFromBeam || (TileClimbable(movementConnection.startCoord) && !OnAnyBeam() && movementConnection.startCoord.y < movementConnection.destinationCoord.y) || (movementConnection.destinationCoord.y > movementConnection.startCoord.y && OnHorizontalBeam()))
                {
                    input.y = UnityEngine.Random.Range(0, 2) != 0 ? 1 : 0;
                }
            }
            else
            {
                input.y = ((catchPoles && catchDelay == 0) || cat.room.PointSubmerged(cat.firstChunk.pos)) ? 1 : 0;
            }

            if (throwAtTarget != 0)
            {
                if (cat.ThrowDirection == throwAtTarget)
                {
                    input.thrw = true;
                    turnDelay = 5;
                    throwAtTarget = 0;
                }
                else
                {
                    input.x = throwAtTarget;
                }
            }
            if (turnDelay > 0)
            {
                input.x = cat.ThrowDirection;
            }

            float stuck = stuckTracker.Utility();
            if (stuck > 0.1f)
            {
                if (UnityEngine.Random.value < UnityEngine.Random.Range(0f, 0.6f * stuck))
                {
                    int rx = UnityEngine.Random.Range(-1, 2);
                    input.x = rx != 0 ? rx : input.x;
                }
                if (UnityEngine.Random.value < UnityEngine.Random.Range(0f, 0.6f * stuck))
                {
                    int ry = UnityEngine.Random.Range(OnHorizontalBeam() ? 0 : -1, 2);
                    input.y = ry != 0 ? ry : input.y;
                }
                if (!OnAnyBeam() && UnityEngine.Random.value < UnityEngine.Random.Range(0f, 0.6f * stuck))
                {
                    input.jmp = UnityEngine.Random.Range(0, 2) == 0;
                }
            }
            cat.input[0] = input;
        }

        private List<MovementConnection> GetUpcoming()
        {
            MovementConnection connection = (pathFinder as StandardPather).FollowPath(creature.pos, false);
            if (connection == default(MovementConnection))
            {
                return null;
            }
            var list = new List<MovementConnection>();
            for (int i = 0; i < 5 && connection != default(MovementConnection); i++)
            {
                list.Add(connection);
                connection = (pathFinder as StandardPather).FollowPath(connection.destinationCoord, false);
                for (int j = 0; j < list.Count && connection != default(MovementConnection); j++)
                {
                    if (list[j].destinationCoord == connection.destinationCoord)
                    {
                        connection = default(MovementConnection);
                    }
                }
            }
            return list;
        }

        private void Jump(int direction, bool poles, ref Player.InputPackage input)
        {
            jumping = true;
            jumpDir = direction;
            input.x = direction;
            catchPoles = poles;
            forceJump = 10;
        }

        private bool OnVerticalBeam() => cat.bodyMode == Player.BodyModeIndex.ClimbingOnBeam;

        private bool OnHorizontalBeam() => cat.animation == Player.AnimationIndex.HangFromBeam || cat.animation == Player.AnimationIndex.StandOnBeam;

        private bool OnAnyBeam() => OnVerticalBeam() || OnHorizontalBeam();

        private bool AnyClimb() => cat.bodyMode == Player.BodyModeIndex.CorridorClimb || OnAnyBeam();

        private bool TileClimbable(WorldCoordinate coordinate) => cat.room.GetTile(coordinate).AnyBeam && !Tunnel(coordinate);

        private bool Tunnel(WorldCoordinate coordinate) => cat.room.aimap.getAItile(coordinate).narrowSpace;

        // ------------------------------------------------------------------ relationships

        AIModule IUseARelationshipTracker.ModuleToTrackRelationship(CreatureTemplate.Relationship relationship)
        {
            if (relationship.type == CreatureTemplate.Relationship.Type.Afraid)
            {
                return threatTracker;
            }
            if (relationship.type == CreatureTemplate.Relationship.Type.Attacks)
            {
                return preyTracker;
            }
            return null;
        }

        RelationshipTracker.TrackedCreatureState IUseARelationshipTracker.CreateTrackedCreatureState(RelationshipTracker.DynamicRelationship rel)
        {
            return new SlugNPCTrackState();
        }

        CreatureTemplate.Relationship IUseARelationshipTracker.UpdateDynamicRelationship(RelationshipTracker.DynamicRelationship dRelation)
        {
            AbstractCreature other = dRelation.trackerRep.representedCreature;
            if (other == null || other.state == null)
            {
                return new CreatureTemplate.Relationship(CreatureTemplate.Relationship.Type.Ignores, 0f);
            }
            if (other.state.dead)
            {
                return new CreatureTemplate.Relationship(CreatureTemplate.Relationship.Type.Ignores, 0f);
            }
            CreatureTemplate.Type top = other.creatureTemplate.TopAncestor().type;
            if (top == CreatureTemplate.Type.Slugcat)
            {
                // The one thing it is here for.
                return new CreatureTemplate.Relationship(CreatureTemplate.Relationship.Type.Attacks, 1f);
            }
            if (ModManager.MSC && top == MoreSlugcatsEnums.CreatureTemplateType.SlugNPC)
            {
                return new CreatureTemplate.Relationship(CreatureTemplate.Relationship.Type.Ignores, 0f);
            }
            if (top == CreatureTemplate.Type.Scavenger)
            {
                // Scavengers hunt it; keep them at a distance and answer with spears.
                return new CreatureTemplate.Relationship(CreatureTemplate.Relationship.Type.Afraid, 0.5f);
            }
            return StaticRelationship(other).Duplicate();
        }

        void IReactToSocialEvents.SocialEvent(SocialEventRecognizer.EventID ID, Creature subjectCrit, Creature objectCrit, PhysicalObject involvedItem)
        {
            // No gifts, no grudges, no friends: nothing the world does changes its mind.
        }
    }
}
