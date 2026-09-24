using System.Collections.Generic;
using Hunted.Core;

namespace Hunted.Game
{
    /// <summary>Creates and destroys the Pursuer creature and translates its gear to and from item codes.</summary>
    internal static class PursuerSpawner
    {
        public static CreatureTemplate Template()
        {
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

        /// <summary>
        /// Spawns the Pursuer at a node of <paramref name="room"/>. When the room is
        /// realized it enters through that node's pipe, like any creature
        /// travelling between rooms.
        /// </summary>
        public static AbstractCreature Spawn(World world, AbstractRoom room, int node, IEnumerable<string> inventory)
        {
            CreatureTemplate template = Template();
            var coord = new WorldCoordinate(room.index, -1, -1, node);
            var creature = new AbstractCreature(world, template, null, coord, world.game.GetNewID());
            creature.saveCreature = false;
            creature.ignoreCycle = false;
            creature.personality.aggression = 1f;
            creature.personality.bravery = 1f;
            creature.personality.dominance = 0.9f;
            creature.personality.energy = 1f;
            creature.personality.nervous = 0.05f;
            creature.personality.sympathy = 0f;

            room.AddEntity(creature);
            GiveGear(creature, inventory);
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

        public static void ConfigureAbstractAI(AbstractCreature creature)
        {
            if (!(creature.abstractAI is ScavengerAbstractAI ai))
            {
                return;
            }
            if (ai.squad != null)
            {
                ai.squad.RemoveMember(creature);
            }
            ai.squad = null;
            ai.freeze = 0;
            ai.dontMigrate = 0;
            creature.world?.scavengersWorldAI?.scavengers.Remove(ai);
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
        }
    }
}
