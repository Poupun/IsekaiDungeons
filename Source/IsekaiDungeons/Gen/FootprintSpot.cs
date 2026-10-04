using System.Collections.Generic;
using RimWorld;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Finds somewhere a multi-cell building fits WHOLE. Used for the buildings
    /// the generator stands inside rooms: the ways out (3x3) and the stairs
    /// (2x2).
    ///
    /// WHY NOT CellFinder.StandableCellNear. It tests ONE cell, and a
    /// building's Position is only one cell of its footprint: the centre of a
    /// 3x3, but the SOUTH-WEST corner of a 2x2 (GenAdj.OccupiedRect puts minX
    /// at x - (size.x - 1) / 2, which is x itself for an even size). The rest
    /// of the footprint was never looked at. While these were portals that
    /// went unnoticed; drawn as stairs, one can visibly straddle a wall.
    ///
    /// GenSpawn does not tidy up after a bad pick either. None of these
    /// buildings is an edifice and none is Impassable, so SpawningWipes leaves
    /// walls, rock, ruins, chunks and items standing under them. Only filth
    /// goes, and plants, because the defs set blockPlants.
    ///
    /// Three passes, nearest cell first within each:
    ///   1. CLEAN - the footprint on dry, walkable ground with no building and
    ///      no item on it, and the one-cell ring round it holding nothing that
    ///      is not standable but plants, all inside the room. The ring keeps
    ///      the art's overhang off the walls and leaves the building reachable
    ///      from every side.
    ///   2. TIGHT - the footprint just as clean, the ring only clear of walls
    ///      and rock (a tree or a chunk beside the stair is fine), still
    ///      inside the room.
    ///   3. NEARBY - pass 1's test over twice the radius with the room limit
    ///      dropped, for a room whose middle is choked (a ruin or a copse in
    ///      an Open zone).
    /// </summary>
    public static class FootprintSpot
    {
        /// <summary>
        /// The root cell that centres a building of this size in a rect, as
        /// near as whole cells allow. For an odd size this is the rect's own
        /// CenterCell, so the exit lands exactly where it always did.
        /// </summary>
        public static IntVec3 CenteredRoot(CellRect rect, IntVec2 size)
        {
            int minX = rect.minX + (rect.Width - size.x + 1) / 2;
            int minZ = rect.minZ + (rect.Height - size.z + 1) / 2;
            return new IntVec3(minX + (size.x - 1) / 2, 0, minZ + (size.z - 1) / 2);
        }

        /// <summary>
        /// Nearest root to <paramref name="near"/> where <paramref name="def"/>
        /// fits, trying the passes in order. Pass <paramref name="room"/> as
        /// CellRect.Empty when there is no room to keep inside.
        /// <paramref name="allowOutsideRoom"/> false stops before the NEARBY
        /// pass, for a building other systems look for inside its room.
        /// </summary>
        public static bool TryFind(Map map, ThingDef def, IntVec3 near, CellRect room, float radius,
                                   out IntVec3 root, out int pass, bool allowOutsideRoom = true)
        {
            int passes = allowOutsideRoom ? 3 : 2;
            for (pass = 0; pass < passes; pass++)
            {
                CellRect within = pass < 2 ? room : CellRect.Empty;
                bool clearRing = pass != 1;
                int n = GenRadial.NumCellsInRadius(
                    pass < 2 ? radius : UnityEngine.Mathf.Min(radius * 2f, GenRadial.MaxRadialPatternRadius));
                for (int i = 0; i < n; i++)
                {
                    IntVec3 c = near + GenRadial.RadialPattern[i];
                    if (Fits(map, def, c, within, clearRing))
                    {
                        root = c;
                        return true;
                    }
                }
            }
            root = IntVec3.Invalid;
            return false;
        }

        /// <summary>
        /// True when the building's footprint is clean ground and the ring
        /// round it passes: clear (see RingClear) when
        /// <paramref name="clearRing"/>, otherwise merely free of anything
        /// impassable. A non-empty <paramref name="within"/> must contain the
        /// ring as well as the footprint.
        /// </summary>
        public static bool Fits(Map map, ThingDef def, IntVec3 root, CellRect within, bool clearRing)
        {
            CellRect foot = GenAdj.OccupiedRect(root, Rot4.North, def.size);
            CellRect ring = foot.ExpandedBy(1);
            if (!ring.InBounds(map))
                return false;
            if (within.Area > 0 && !ring.FullyContainedWithin(within))
                return false;

            foreach (IntVec3 c in ring)
            {
                if (foot.Contains(c))
                {
                    if (!CleanGround(map, c))
                        return false;
                }
                else if (clearRing ? !RingClear(map, c) : c.Impassable(map))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Walkable, dry, and holding no building and no item. Plants, filth
        /// and pawns may stay: spawning wipes the first two (blockPlants) and
        /// the third walks off. Plants are allowed on purpose - a cave plant
        /// on the middle cell should not push a stair across the room.
        /// </summary>
        private static bool CleanGround(Map map, IntVec3 c)
        {
            if (!c.Walkable(map))
                return false;
            TerrainDef terrain = c.GetTerrain(map);
            if (terrain == null || terrain.IsWater)
                return false;
            List<Thing> things = c.GetThingList(map);
            for (int i = 0; i < things.Count; i++)
            {
                ThingCategory category = things[i].def.category;
                if (category == ThingCategory.Building || category == ThingCategory.Item)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Walkable, and nothing on it that is not standable except plants: no
        /// wall, rock, door, furniture or chunk against the building. Plants
        /// are left alone, not wiped.
        /// </summary>
        private static bool RingClear(Map map, IntVec3 c)
        {
            if (!c.Walkable(map))
                return false;
            List<Thing> things = c.GetThingList(map);
            for (int i = 0; i < things.Count; i++)
            {
                ThingDef d = things[i].def;
                if (d.category != ThingCategory.Plant && d.passability != Traversability.Standable)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Where a stair goes on a generated floor: in the boss room at the far
        /// end, so going on means crossing the floor. Shared by the Tower and
        /// the multi-floor dungeons, which place their stairs the same way.
        ///
        /// Runs after generation, so the room already holds its decor (kept
        /// off the middle by GenStep_DungeonDecor) and its creatures (pawns
        /// never block a spot). When no spot passes, the old single-cell pick
        /// is still better than a floor with no way on, so it is kept as the
        /// last resort and logged.
        /// </summary>
        public static IntVec3 StairCell(Map map, ThingDef def, string what)
        {
            DungeonMapComponent comp = map.GetComponent<DungeonMapComponent>();
            DungeonRoom boss = null;
            if (comp?.rooms != null)
            {
                for (int i = comp.rooms.Count - 1; i >= 0; i--)
                {
                    if (comp.rooms[i].role == DungeonRoomRole.Boss)
                    {
                        boss = comp.rooms[i];
                        break;
                    }
                }
            }

            CellRect room = boss?.rect ?? CellRect.Empty;
            IntVec3 near = boss != null ? CenteredRoot(room, def.size) : map.Center;
            if (TryFind(map, def, near, room, boss != null ? 8f : 20f, out IntVec3 root, out int pass))
            {
                if (pass == 2)
                    Log.Message($"{IsekaiDungeonsMod.LogPrefix} {what}: the boss room's middle is blocked; "
                              + $"placed at {root}, outside the room.");
                return root;
            }

            IntVec3 cell = CellFinder.StandableCellNear(near, map, 20);
            if (cell.IsValid)
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} {what}: no spot fits a {def.size.x}x{def.size.z} stair; "
                          + $"placed at {cell} and it may overlap something.");
            return cell;
        }
    }
}
