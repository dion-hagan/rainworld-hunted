using System.Collections.Generic;
using Hunted.Core;
using MoreSlugcats;

namespace Hunted.Game
{
    /// <summary>Creates and destroys the Pursuer creature and translates its gear to and from item codes.</summary>
    internal static class PursuerSpawner
    {
        public static CreatureTemplate Template(PursuerBody body)
        {
            if (body == PursuerBody.Slugcat && PursuerBodies.SlugcatAvailable())
            {
                return StaticWorld.GetCreatureTemplate(MoreSlugcatsEnums.CreatureTemplateType.SlugNPC);
            }
            if (ModManager.DLCShared && DLCSharedEnums.CreatureTemplateType.ScavengerElite != null)
            {
                CreatureTemplate elite = StaticWorld.GetCreatureTemplate(DLCSharedEnums.CreatureTemplateType.ScavengerElite);
                if (elite != null)
                {
                    return elite;
                }
            }
            return StaticWorld.GetCreatureTemplate(CreatureTemplate.Type.Scavenger);
        }

        /// <summary>How many items the body can carry: a scavenger fills its hands and back, a slugcat NPC keeps one hand.</summary>
        public static int HeldItems(PursuerBody body)
        {
            return body == PursuerBody.Slugcat ? 1 : 4;
        }

        /// <summary>
        /// Spawns the Pursuer at a node of <paramref name="room"/>. When the room is
        /// realized it enters through that node's pipe, like any creature
        /// travelling between rooms.
        /// </summary>
        public static AbstractCreature Spawn(World world, AbstractRoom room, int node, IEnumerable<string> inventory, PursuerBody body)
        {
            CreatureTemplate template = Template(body);
            var coord = new WorldCoordinate(room.index, -1, -1, node);
            var creature = new AbstractCreature(world, template, null, coord, world.game.GetNewID());
            PursuerMark.Mark(creature);
            creature.saveCreature = false;
            creature.ignoreCycle = false;
            creature.personality.aggression = 1f;
            creature.personality.bravery = 1f;
            creature.personality.dominance = 0.9f;
            creature.personality.energy = 1f;
            creature.personality.nervous = 0.05f;
            creature.personality.sympathy = 0f;
            if (creature.state is PlayerNPCState npcState)
            {
                npcState.forceFullGrown = true;
                npcState.isPup = false;
            }

            room.AddEntity(creature);
            GiveGear(creature, TrimToBody(inventory, body));
            ConfigureAbstractAI(creature);

            if (room.realizedRoom != null && room.realizedRoom.shortCutsReady && node > -1)
            {
                creature.Realize();
                if (creature.realizedCreature != null)
                {
                    creature.realizedCreature.inShortcut = true;
                    world.game.shortcuts.CreatureEnterFromAbstractRoom(creature.realizedCreature, room, node);
                }
            }
            return creature;
        }

        /// <summary>The items a body can actually hold, best first.</summary>
        public static List<string> TrimToBody(IEnumerable<string> inventory, PursuerBody body)
        {
            var items = new List<string>();
            if (inventory != null)
            {
                items.AddRange(inventory);
            }
            items.Sort((a, b) => GearTier.TierOf(b).CompareTo(GearTier.TierOf(a)));
            int max = HeldItems(body);
            if (items.Count > max)
            {
                items.RemoveRange(max, items.Count - max);
            }
            return items;
        }

        public static void ConfigureAbstractAI(AbstractCreature creature)
        {
            if (creature.abstractAI is ScavengerAbstractAI scav)
            {
                if (scav.squad != null)
                {
                    scav.squad.RemoveMember(creature);
                }
                scav.squad = null;
                scav.freeze = 0;
                scav.dontMigrate = 0;
                creature.world?.scavengersWorldAI?.scavengers.Remove(scav);
            }
            else if (creature.abstractAI is SlugNPCAbstractAI slug)
            {
                slug.isTamed = false;
                slug.toldToStay = null;
            }
        }

        public static void GiveGear(AbstractCreature creature, IEnumerable<string> inventory)
        {
            if (inventory == null)
            {
                return;
            }
            World world = creature.world;
            AbstractRoom room = creature.Room;
            int grasp = 0;
            foreach (string code in inventory)
            {
                if (grasp >= 4)
                {
                    break;
                }
                AbstractPhysicalObject item = CreateItem(world, code, creature.pos);
                if (item == null)
                {
                    continue;
                }
                room.AddEntity(item);
                new AbstractPhysicalObject.CreatureGripStick(creature, item, grasp, true);
                grasp++;
            }
        }

        private static AbstractPhysicalObject CreateItem(World world, string code, WorldCoordinate pos)
        {
            EntityID id = world.game.GetNewID();
            switch (code)
            {
                case GearTier.Rock:
                    return new AbstractPhysicalObject(world, AbstractPhysicalObject.AbstractObjectType.Rock, null, pos, id);
                case GearTier.Spear:
                    return new AbstractSpear(world, null, pos, id, false);
                case GearTier.ExplosiveSpear:
                    return new AbstractSpear(world, null, pos, id, true);
                case GearTier.ElectricSpear:
                    return new AbstractSpear(world, null, pos, id, false, true);
                case GearTier.ScavengerBomb:
                    return new AbstractPhysicalObject(world, AbstractPhysicalObject.AbstractObjectType.ScavengerBomb, null, pos, id);
                case GearTier.Lantern:
                    return new AbstractPhysicalObject(world, AbstractPhysicalObject.AbstractObjectType.Lantern, null, pos, id);
                default:
                    HuntedLog.Warn("Unknown gear code " + code);
                    return null;
            }
        }

        /// <summary>Item codes for everything the creature is carrying.</summary>
        public static List<string> ReadInventory(AbstractCreature creature)
        {
            var result = new List<string>();
            if (creature == null)
            {
                return result;
            }
            foreach (AbstractPhysicalObject.AbstractObjectStick stick in creature.stuckObjects)
            {
                if (!(stick is AbstractPhysicalObject.CreatureGripStick) || stick.A != creature)
                {
                    continue;
                }
                string code = CodeFor(stick.B);
                if (code != null)
                {
                    result.Add(code);
                }
            }
            return result;
        }

        public static string CodeFor(AbstractPhysicalObject item)
        {
            if (item == null)
            {
                return null;
            }
            if (item is AbstractSpear spear)
            {
                if (spear.explosive)
                {
                    return GearTier.ExplosiveSpear;
                }
                if (spear.electric)
                {
                    return GearTier.ElectricSpear;
                }
                return GearTier.Spear;
            }
            if (item.type == AbstractPhysicalObject.AbstractObjectType.Rock)
            {
                return GearTier.Rock;
            }
            if (item.type == AbstractPhysicalObject.AbstractObjectType.ScavengerBomb)
            {
                return GearTier.ScavengerBomb;
            }
            if (item.type == AbstractPhysicalObject.AbstractObjectType.Lantern)
            {
                return GearTier.Lantern;
            }
            return null;
        }

        /// <summary>Removes the creature and everything it carries from the world.</summary>
        public static void Despawn(AbstractCreature creature)
        {
            if (creature == null)
            {
                return;
            }
            for (int i = creature.stuckObjects.Count - 1; i >= 0; i--)
            {
                AbstractPhysicalObject.AbstractObjectStick stick = creature.stuckObjects[i];
                if (stick.A == creature && stick.B != null)
                {
                    AbstractPhysicalObject item = stick.B;
                    stick.Deactivate();
                    item.realizedObject?.Destroy();
                    item.Room?.RemoveEntity(item);
                    item.Destroy();
                }
            }
            creature.realizedCreature?.Destroy();
            creature.Room?.RemoveEntity(creature);
            creature.Destroy();
            PursuerMark.Unmark(creature);
        }
    }
}
