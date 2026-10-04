using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// The floor lifecycle for multi-floor dungeons: build one, tear one down.
    ///
    /// This is TowerFloors with the singleton taken out. Every lookup the tower
    /// does by TYPE — "the map whose parent is a floor parent", "the world
    /// object that is a tower" — has to be done by OWNER here, because there
    /// are many of these and two can be open at once. That single change is
    /// most of what separates the two files.
    ///
    /// What is NOT duplicated: the evacuation half of teardown. TowerFloors.Retire
    /// already does the part that is easy to get catastrophically wrong (the
    /// corpse pass, without which a colonist who died on a floor you walk away
    /// from is destroyed outright, no body and no message), and its record
    /// harvest early-returns on a parent that is not a tower floor. So it is
    /// safe to reuse verbatim, and reusing it means that bug can only ever be
    /// fixed in one place.
    /// </summary>
    public static class MultiFloorFloors
    {
        /// <summary>Resolve a dungeon by world-object id. Used by floors to find their owner.</summary>
        public static WorldObject_MultiFloorDungeon DungeonById(int id)
        {
            if (id < 0)
                return null;
            List<WorldObject> all = Find.WorldObjects?.AllWorldObjects;
            if (all == null)
                return null;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] is WorldObject_MultiFloorDungeon d && d.ID == id)
                    return d;
            }
            return null;
        }

        /// <summary>The live floor map of ONE dungeon, or null if nobody is inside it.</summary>
        public static Map LiveFloorOf(WorldObject_MultiFloorDungeon dungeon)
        {
            if (dungeon == null)
                return null;
            List<Map> maps = Find.Maps;
            for (int i = 0; i < maps.Count; i++)
            {
                if (maps[i].Parent is MultiFloorMapParent p && p.ownerId == dungeon.ID)
                    return maps[i];
            }
            return null;
        }

        /// <summary>The dungeon a map belongs to, if it is a multi-floor floor at all.</summary>
        public static WorldObject_MultiFloorDungeon OwnerOf(Map map) =>
            (map?.Parent as MultiFloorMapParent)?.Owner;

        /// <summary>The floor number a map is, or 0 if it is not one of ours.</summary>
        public static int FloorOf(Map map) =>
            (map?.Parent as MultiFloorMapParent)?.floor ?? 0;

        // ── Build ───────────────────────────────────────────────────────────

        /// <summary>
        /// Builds a floor and returns its map, or null if generation failed.
        ///
        /// Transactional, like the tower's: a throw cleans up the half-built
        /// parent rather than leaving an orphan world object and a map nobody
        /// can reach.
        /// </summary>
        public static Map Open(WorldObject_MultiFloorDungeon dungeon, int floor)
        {
            MultiFloorDungeonDef plan = dungeon?.Def;
            if (plan == null)
                return null;

            floor = Mathf.Clamp(floor, 1, plan.floorCount);
            DungeonKindDef kind = plan.KindForFloor(floor);
            if (kind?.mapGenerator == null)
            {
                Log.Error($"{IsekaiDungeonsMod.LogPrefix} {plan.defName} floor {floor} has no map generator.");
                return null;
            }

            MultiFloorRecord rec = dungeon.EnsureRecord(floor);

            var parent = (MultiFloorMapParent)WorldObjectMaker.MakeWorldObject(
                IsekaiDungeonsDefOf.IsekaiDungeons_MultiFloorParent);

            // PIN THE ID BEFORE GENERATING. Vanilla re-seeds every gen step from
            // the parent id, so this one assignment is what makes a revisited
            // floor come back identical down to where the grass grew.
            parent.ID = rec.parentId;
            parent.ownerId = dungeon.ID;
            parent.floor = floor;
            parent.mapGenerator = kind.mapGenerator;
            parent.kindDef = kind;
            parent.rank = plan.RankForFloor(floor);
            parent.rankIndex = plan.RankIndexForFloor(floor);
            parent.layoutSeed = rec.layoutSeed;

            // A beaten floor comes back empty; treasure already taken stays taken.
            parent.suppressPopulation = rec.cleared;
            parent.treasureTakenMask = rec.treasureTakenMask;
            parent.roomSpentMask = rec.roomSpentMask;
            parent.suppressResources = rec.resourcesGenerated;
            parent.layoutSignature = rec.layoutSignature;

            // A surface biome on an invalid tile crashes in FinalizeInit through
            // the plant growth cache, so floors borrow the dungeon's own tile
            // and inherit the local biome with it.
            parent.Tile = dungeon.Tile;

            Map map;
            try
            {
                int size = Mathf.Clamp(DungeonTunables.ScaledMapSize(plan.MapSizeForFloor(floor)), 120, 320);
                map = MapGenerator.GenerateMap(new IntVec3(size, 1, size), parent,
                                               parent.mapGenerator, null, null, isPocketMap: true);
            }
            catch (Exception ex)
            {
                Log.Error($"{IsekaiDungeonsMod.LogPrefix} {plan.defName} floor {floor} map generation threw; cleaning up. {ex}");
                try { parent.Destroy(); } catch { }
                return null;
            }

            Find.World.pocketMaps.Add(parent);
            rec.resourcesGenerated = true;
            PlaceStair(map, plan, floor);

            rec.discovered = true;
            if (floor > dungeon.deepestReached)
                dungeon.deepestReached = floor;

            RunSpine(plan, w => w.OnFloorGenerated(new MultiFloorContext(dungeon, floor, map)), "OnFloorGenerated");
            return map;
        }

        /// <summary>
        /// The stair onward, in the boss room at the far end so getting deeper
        /// means crossing the floor.
        ///
        /// The LAST floor gets none, and that absence is the message: you look
        /// for the way on and there isn't one, which is how the tower's summit
        /// reads too. Placed the same way as the Tower's: FootprintSpot finds
        /// a spot the whole 2x2 fits, with a walkable ring round it.
        /// </summary>
        private static void PlaceStair(Map map, MultiFloorDungeonDef plan, int floor)
        {
            if (plan.IsLastFloor(floor))
                return;

            ThingDef def = IsekaiDungeonsDefOf.IsekaiDungeons_MultiFloorStair;
            IntVec3 cell = FootprintSpot.StairCell(map, def, $"{plan.defName} floor {floor} stair");
            if (!cell.IsValid)
            {
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} No cell for the {plan.defName} floor {floor} stair; "
                          + "the floor will have no way on.");
                return;
            }

            var stair = (Building_MultiFloorStair)ThingMaker.MakeThing(def);
            stair.floor = floor;
            GenSpawn.Spawn(stair, cell, map, WipeMode.VanishOrMoveAside);
        }

        // ── Teardown ────────────────────────────────────────────────────────

        /// <summary>
        /// Writes what happened on this floor into the record, then evacuates
        /// and destroys the map. The record write has to happen FIRST: in a
        /// moment there will be no map left to ask.
        /// </summary>
        public static void Retire(Map floorMap, Map destination) =>
            TowerFloors.Retire(floorMap, destination);

        /// <summary>
        /// Called by TowerFloors.Retire, which both forms share. Public for
        /// that reason and no other.
        /// </summary>
        public static void HarvestRecord(Map floorMap)
        {
            if (!(floorMap?.Parent is MultiFloorMapParent parent))
                return;
            WorldObject_MultiFloorDungeon dungeon = parent.Owner;
            DungeonMapComponent comp = floorMap.GetComponent<DungeonMapComponent>();
            MultiFloorRecord rec = dungeon?.RecordIfSeen(parent.floor);
            if (rec == null || comp == null)
                return;

            // The clear itself already banked this (MultiFloorRun.Finish). This
            // is the fallback for a floor won before that existed: bookkeeping
            // only, since the map is about to go and has nowhere to put a reward.
            if (comp.IsCleared)
            {
                rec.cleared = true;
                if (dungeon.Def != null && dungeon.Def.IsLastFloor(parent.floor))
                    MultiFloorRun.Complete(dungeon, parent.floor, null, IntVec3.Invalid, parent.rank);
            }

            // One bit per room. The masks only hold while the layout matches
            // the one they were recorded against (see TowerFloors.LayoutSignature).
            if (comp.rooms == null)
                return;
            int sig = TowerFloors.LayoutSignature(comp.rooms);
            if (rec.layoutSignature != sig)
            {
                if (rec.layoutSignature != 0)
                {
                    rec.treasureTakenMask = 0UL;
                    rec.roomSpentMask = 0UL;
                }
                rec.layoutSignature = sig;
            }
            for (int i = 0; i < comp.rooms.Count && i < 64; i++)
            {
                DungeonRoom room = comp.rooms[i];
                if (room.role == DungeonRoomRole.Treasure && room.entered)
                    rec.treasureTakenMask |= 1UL << i;
                if (TowerFloors.IsRoomSpent(room, floorMap))
                    rec.roomSpentMask |= 1UL << i;
            }
        }

        /// <summary>
        /// Every live floor of one dungeon, retired. Called when the dungeon
        /// itself is removed — a pocket map whose owner is gone is unreachable
        /// by any ordinary path and would tick forever inside the save.
        /// </summary>
        public static void CloseAllFloorsOf(WorldObject_MultiFloorDungeon dungeon)
        {
            if (dungeon == null)
                return;
            var doomed = new List<Map>();
            List<Map> maps = Find.Maps;
            for (int i = 0; i < maps.Count; i++)
            {
                if (maps[i].Parent is MultiFloorMapParent p && p.ownerId == dungeon.ID)
                    doomed.Add(maps[i]);
            }
            for (int i = 0; i < doomed.Count; i++)
                Retire(doomed[i], null);
        }

        /// <summary>
        /// Runs a spine hook, swallowing anything it throws.
        ///
        /// A spine is flavour layered on a working dungeon. Letting one take
        /// the floor down with it would make every themed dungeon less
        /// reliable than a plain one, which is exactly backwards.
        /// </summary>
        private static void RunSpine(MultiFloorDungeonDef plan, Action<MultiFloorSpineWorker> call, string hook)
        {
            MultiFloorSpineWorker worker = plan?.Spine;
            if (worker == null)
                return;
            try
            {
                call(worker);
            }
            catch (Exception ex)
            {
                Log.Error($"{IsekaiDungeonsMod.LogPrefix} {plan.defName} spine threw in {hook}; "
                        + $"the floor is unaffected. {ex}");
            }
        }

        /// <summary>Public entry points for the hooks fired from outside generation.</summary>
        public static void NotifyFloorEntered(WorldObject_MultiFloorDungeon d, int floor, Map map)
        {
            RunSpine(d?.Def, w => w.OnFloorEntered(new MultiFloorContext(d, floor, map)), "OnFloorEntered");
            MultiFloorQuest.Refresh(d);
        }

        /// <summary>The map-state poll, for spines that act during the fight.</summary>
        public static void NotifyFloorTick(Map map, DungeonMapComponent comp)
        {
            if (!(map?.Parent is MultiFloorMapParent p))
                return;
            WorldObject_MultiFloorDungeon d = p.Owner;
            if (d?.Def?.Spine == null)
                return;
            RunSpine(d.Def, w => w.OnFloorTick(new MultiFloorContext(d, p.floor, map), comp), "OnFloorTick");
        }

        public static void NotifyFloorLeft(WorldObject_MultiFloorDungeon d, int floor, Map map) =>
            RunSpine(d?.Def, w => w.OnFloorLeft(new MultiFloorContext(d, floor, map)), "OnFloorLeft");
    }
}
