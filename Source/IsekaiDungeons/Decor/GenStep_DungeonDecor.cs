using System.Collections.Generic;
using IsekaiLeveling.MobRanking;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Dresses a dungeon for its rank. Order 425: after the entrance (420) has
    /// put the exit down, so decor can keep clear of it, and before population
    /// (430), so creatures and chests land on whatever floor is left open.
    ///
    /// Three things, all keyed to the register the rank sits in
    /// (DungeonTunables.DecorRegisterFor):
    ///   1. FINISH — carved rock floors go rough, rough-hewn, smooth, and the rock
    ///      shell round rooms gets smoothed, as rank rises. Costless vanilla
    ///      terrain and walls that follow the map's own rock, so no art and
    ///      nothing to refund.
    ///   2. ROOMS — each room spends a decor budget from its environment's
    ///      DungeonDecorSetDef. The boss room sits one register above the rest.
    ///   3. CORRIDORS — wall torches and a little filth, from the stronghold
    ///      register up.
    ///
    /// Deterministic: vanilla pushes a seed per gen step, so a tower floor that
    /// rebuilds on a revisit comes back dressed exactly as it was.
    /// </summary>
    public class GenStep_DungeonDecor : GenStep
    {
        /// <summary>Arbitrary unique constant — must differ from every other GenStep's SeedPart.</summary>
        public override int SeedPart => 519384201;

        public override void Generate(Map map, GenStepParams parms)
        {
            DungeonMapComponent comp = map.GetComponent<DungeonMapComponent>();
            if (comp?.rooms == null || comp.rooms.Count == 0)
                return;
            if (!DungeonTunables.DecorEnabled)
                return;

            DungeonContext.Resolve(map, out DungeonKindDef kind, out MobRankTier rank);
            DungeonDecorSetDef set = DecorSets.For(kind, map.generatorDef);
            if (set == null)
                return;

            float rankIndex = map.Parent is DungeonMapParent parent ? parent.RankIndex : (int)rank;
            new DecorPass(map, comp, kind, set, rankIndex).Run();
        }
    }

    /// <summary>Which set dresses which map. A set naming the kind beats a set naming the generator.</summary>
    public static class DecorSets
    {
        private static Dictionary<string, DungeonDecorSetDef> byKind;
        private static Dictionary<string, DungeonDecorSetDef> byGenerator;

        public static DungeonDecorSetDef For(DungeonKindDef kind, MapGeneratorDef generator)
        {
            if (byKind == null)
                Build();
            if (kind != null && byKind.TryGetValue(kind.defName, out DungeonDecorSetDef k))
                return k;
            if (generator != null && byGenerator.TryGetValue(generator.defName, out DungeonDecorSetDef g))
                return g;
            return null;
        }

        private static void Build()
        {
            byKind = new Dictionary<string, DungeonDecorSetDef>();
            byGenerator = new Dictionary<string, DungeonDecorSetDef>();
            foreach (DungeonDecorSetDef set in DefDatabase<DungeonDecorSetDef>.AllDefsListForReading)
            {
                foreach (string k in set.kinds)
                    byKind[k] = set;
                foreach (string g in set.generators)
                    byGenerator[g] = set;
            }
        }
    }

    /// <summary>One map's worth of decorating. Scratch state lives here, not in statics.</summary>
    internal class DecorPass
    {
        private readonly Map map;
        private readonly DungeonMapComponent comp;
        private readonly DungeonKindDef kind;
        private readonly DungeonDecorSetDef set;
        private readonly float rankIndex;
        private readonly DecorRegister baseRegister;
        private readonly bool carved;

        private readonly CellIndices indices;
        private readonly int[] roomOf;            // room index + 1, 0 = not a room cell
        private readonly bool[] keepClear;        // nothing solid may go here
        private readonly bool[] nearDoor;         // only door-flanking pieces may go here
        private readonly List<List<Entrance>> entrances = new List<List<Entrance>>();

        private readonly List<IntVec3> fireLights = new List<IntVec3>();
        private readonly List<IntVec3> plantLights = new List<IntVec3>();

        private int pieces, roomsDressed, corridorLights, floorsRefined, wallsSmoothed;

        private static readonly int[] RoomFinish = { 0, 1, 1, 2, 2 };        // 0 rough, 1 rough-hewn, 2 smooth
        private static readonly int[] CorridorFinish = { 0, 0, 1, 2, 2 };
        private static readonly string[] FinishSuffix = { "_Rough", "_RoughHewn", "_Smooth" };

        private struct Entrance
        {
            public IntVec3 cell;    // first cell inside the room
            public Rot4 inward;     // direction from the doorway into the room
        }

        public DecorPass(Map map, DungeonMapComponent comp, DungeonKindDef kind, DungeonDecorSetDef set, float rankIndex)
        {
            this.map = map;
            this.comp = comp;
            this.kind = kind;
            this.set = set;
            this.rankIndex = rankIndex;
            baseRegister = DungeonTunables.DecorRegisterFor(rankIndex);
            carved = kind.layoutMode != DungeonLayoutMode.Open;
            indices = map.cellIndices;
            roomOf = new int[indices.NumGridCells];
            keepClear = new bool[indices.NumGridCells];
            nearDoor = new bool[indices.NumGridCells];
        }

        public void Run()
        {
            IndexRooms();
            MarkKeepClear();
            RecordExistingLights();

            if (carved)
            {
                if (set.refineFloors && DungeonTunables.DecorRefineFloors)
                    RefineFloors();
                if (set.smoothWalls && DungeonTunables.DecorSmoothWalls)
                    SmoothWalls();
            }

            for (int i = 0; i < comp.rooms.Count; i++)
                DressRoom(comp.rooms[i]);

            if (carved)
                DressCorridors();

            if (IsekaiDungeonsSettings.Current.logGenerationSummaries)
                Log.Message($"{IsekaiDungeonsMod.LogPrefix} Decor {set.defName} @{baseRegister} (rank {rankIndex:0.#}): " +
                            $"{pieces} pieces in {roomsDressed}/{comp.rooms.Count} rooms, {corridorLights} corridor lights, " +
                            $"{floorsRefined} floors refined, {wallsSmoothed} walls smoothed.");
        }

        // ── Map reading ─────────────────────────────────────────────────────

        private DecorRegister RegisterOf(DungeonRoom room) =>
            room.role == DungeonRoomRole.Boss && baseRegister < DecorRegister.Wonder
                ? baseRegister + 1
                : baseRegister;

        private void IndexRooms()
        {
            for (int r = 0; r < comp.rooms.Count; r++)
            {
                foreach (IntVec3 c in comp.rooms[r].rect)
                {
                    if (c.InBounds(map))
                        roomOf[indices.CellToIndex(c)] = r + 1;
                }
            }
        }

        private int RoomIndexAt(IntVec3 c) => c.InBounds(map) ? roomOf[indices.CellToIndex(c)] - 1 : -1;

        /// <summary>A wall a piece can stand against or hang on: solid, full, and not a door.</summary>
        private bool IsWall(IntVec3 c)
        {
            if (!c.InBounds(map))
                return false;
            Building e = c.GetEdifice(map);
            return e != null && !(e is Building_Door)
                   && e.def.passability == Traversability.Impassable
                   && e.def.Fillage == FillCategory.Full;
        }

        private bool Passable(IntVec3 c) =>
            c.InBounds(map) && !c.Impassable(map) && c.GetTerrain(map).passability != Traversability.Impassable;

        /// <summary>A cell the carve opened that is not inside any room: a corridor, or a doorway.</summary>
        private bool IsCorridorCell(IntVec3 c) =>
            c.InBounds(map) && RoomIndexAt(c) < 0 && map.roofGrid.RoofAt(c) == RoofDefOf.RoofRockThin
            && !(c.GetEdifice(map) is Building e && e.def.building != null && e.def.building.isNaturalRock);

        private void MarkKeepClear()
        {
            for (int r = 0; r < comp.rooms.Count; r++)
            {
                DungeonRoom room = comp.rooms[r];
                var list = new List<Entrance>();
                entrances.Add(list);

                // The way out keeps a ring of breathing room round it. Only the
                // way out: an Open zone can overlap vanilla ruins, and reserving
                // space round every ruin wall would leave those zones bare.
                foreach (IntVec3 c in room.rect)
                {
                    if (!c.InBounds(map))
                        continue;
                    Building b = c.GetFirstBuilding(map);
                    if (b != null && b.Position == c && IsWayOut(b))
                        MarkRadius(b.Position, 2.9f + Mathf.Max(b.def.size.x, b.def.size.z) * 0.5f, keepClear);
                }

                // The centres downstream code reaches for: the exit and arrival on
                // the entry room, the stairs and the clear loot on the boss room,
                // the chests on a treasure room.
                //
                // The boss radius is the STAIR's. The stair is placed after
                // generation, so it cannot be looked up here; FootprintSpot roots
                // its 2x2 at CenteredRoot, the cell at or just south-west of the
                // centre, and wants the one-cell ring round it standable. That
                // 4x4 block reaches a diagonal 2.83 from the centre: 2.9 keeps all
                // of it clear, so decor never pushes the stair off the middle.
                switch (room.role)
                {
                    case DungeonRoomRole.Entry: MarkRadius(room.rect.CenterCell, 3.9f, keepClear); break;
                    case DungeonRoomRole.Boss: MarkRadius(room.rect.CenterCell, 2.9f, keepClear); break;
                    case DungeonRoomRole.Treasure: MarkRadius(room.rect.CenterCell, 1.5f, keepClear); break;
                }

                if (!carved)
                    continue;

                // Doorways: a passable cell on the ring just outside the rect.
                // The walkway three cells in stays clear; the cells beside the
                // doorway are reserved for pieces that flank it.
                CellRect ring = room.rect.ExpandedBy(1);
                foreach (IntVec3 c in ring.EdgeCells)
                {
                    if (!c.InBounds(map) || room.rect.Contains(c))
                        continue;
                    if (!Passable(c) && !(c.GetEdifice(map) is Building_Door))
                        continue;
                    for (int d = 0; d < 4; d++)
                    {
                        Rot4 inward = new Rot4(d);
                        IntVec3 inside = c + inward.FacingCell;
                        if (!room.rect.Contains(inside))
                            continue;
                        list.Add(new Entrance { cell = inside, inward = inward });
                        for (int step = 0; step < 3; step++)
                        {
                            IntVec3 w = inside + inward.FacingCell * step;
                            if (w.InBounds(map))
                                keepClear[indices.CellToIndex(w)] = true;
                        }
                        MarkRadius(inside, 1.5f, nearDoor);
                    }
                }
            }
        }

        private static bool IsWayOut(Building b) =>
            b is MapPortal
            || b is Building_DungeonExit
            || b is Building_TowerExit;   // the tower's and every multi-floor dungeon's

        private void MarkRadius(IntVec3 center, float radius, bool[] grid)
        {
            int n = GenRadial.NumCellsInRadius(radius);
            for (int i = 0; i < n; i++)
            {
                IntVec3 c = center + GenRadial.RadialPattern[i];
                if (c.InBounds(map))
                    grid[indices.CellToIndex(c)] = true;
            }
        }

        /// <summary>
        /// Lights already on the map, and the one that is coming. The exit glows,
        /// and the stair that the tower and multi-floor code put in the boss room
        /// after generation glows too — both would kill a cave plant beside them.
        /// </summary>
        private void RecordExistingLights()
        {
            List<Thing> glowers = map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial);
            for (int i = 0; i < glowers.Count; i++)
            {
                if (glowers[i].def.GetCompProperties<CompProperties_Glower>() != null)
                    fireLights.Add(glowers[i].Position);
            }
            for (int r = 0; r < comp.rooms.Count; r++)
            {
                if (comp.rooms[r].role == DungeonRoomRole.Boss)
                    fireLights.Add(comp.rooms[r].rect.CenterCell);
            }
        }

        private bool FarFrom(List<IntVec3> lights, IntVec3 c, float distance)
        {
            float d2 = distance * distance;
            for (int i = 0; i < lights.Count; i++)
            {
                if (lights[i].DistanceToSquared(c) < d2)
                    return false;
            }
            return true;
        }

        // ── Finish ──────────────────────────────────────────────────────────

        /// <summary>
        /// Moves a carved rock floor up the rough → rough-hewn → smooth ladder.
        /// Only the rock's own generated floors are touched, so a kind that
        /// authors its floor — flagstone, the sanctum stone, mud — keeps it.
        /// </summary>
        private void RefineFloors()
        {
            foreach (IntVec3 c in map.AllCells)
            {
                int room = RoomIndexAt(c);
                bool corridor = room < 0 && IsCorridorCell(c);
                if (room < 0 && !corridor)
                    continue;

                TerrainDef t = c.GetTerrain(map);
                int level = FinishLevel(t.defName, out string rock);
                if (level < 0)
                    continue;

                DecorRegister reg = room >= 0 ? RegisterOf(comp.rooms[room]) : baseRegister;
                int target = room >= 0 ? RoomFinish[(int)reg] : CorridorFinish[(int)reg];
                if (target <= level)
                    continue;

                TerrainDef refined = DefDatabase<TerrainDef>.GetNamedSilentFail(rock + FinishSuffix[target]);
                if (refined == null)
                    continue;
                map.terrainGrid.SetTerrain(c, refined);
                floorsRefined++;
            }
        }

        private static int FinishLevel(string defName, out string rock)
        {
            rock = null;
            for (int i = FinishSuffix.Length - 1; i >= 0; i--)
            {
                if (defName.EndsWith(FinishSuffix[i]))
                {
                    rock = defName.Substring(0, defName.Length - FinishSuffix[i].Length);
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// Smooths the natural rock round rooms: the boss room from the stronghold
        /// register, every room from temple, corridors too at wonder.
        ///
        /// Vanilla's own recipe (SmoothableWallUtility.SmoothWall) — destroy with
        /// WillReplace, which skips the roof-holder collapse check, then spawn the
        /// rock's smoothedThing in the same cell. Smoothed rock inherits RockBase,
        /// so it still holds the roof and still cannot be deconstructed. Left
        /// ownerless, where vanilla would hand it to the player.
        /// </summary>
        private void SmoothWalls()
        {
            var shell = new HashSet<IntVec3>();
            for (int r = 0; r < comp.rooms.Count; r++)
            {
                DungeonRoom room = comp.rooms[r];
                DecorRegister reg = RegisterOf(room);
                bool smooth = reg >= DecorRegister.Temple
                              || (reg >= DecorRegister.Stronghold && room.role == DungeonRoomRole.Boss);
                if (smooth)
                    CollectShell(room.rect.ExpandedBy(1), shell);
            }
            if (baseRegister >= DecorRegister.Wonder)
            {
                foreach (IntVec3 c in map.AllCells)
                {
                    if (IsCorridorCell(c))
                        CollectShell(CellRect.SingleCell(c).ExpandedBy(1), shell);
                }
            }

            foreach (IntVec3 c in shell)
            {
                Building rock = c.GetEdifice(map);
                ThingDef smoothed = rock?.def.building?.smoothedThing;
                if (smoothed == null)
                    continue;
                Rot4 rot = rock.Rotation;
                rock.Destroy(DestroyMode.WillReplace);
                GenSpawn.Spawn(ThingMaker.MakeThing(smoothed), c, map, rot, WipeMode.Vanish);
                wallsSmoothed++;
            }
        }

        private void CollectShell(CellRect area, HashSet<IntVec3> shell)
        {
            foreach (IntVec3 c in area)
            {
                if (!c.InBounds(map))
                    continue;
                Building e = c.GetEdifice(map);
                if (e != null && e.def.building != null && e.def.building.isNaturalRock
                    && e.def.building.smoothedThing != null)
                    shell.Add(c);
            }
        }

        // ── Rooms ───────────────────────────────────────────────────────────

        private void DressRoom(DungeonRoom room)
        {
            int roomIndex = comp.rooms.IndexOf(room);
            DecorRegister reg = RegisterOf(room);
            int area = room.rect.Area;

            DecorLight light;
            if (room.role == DungeonRoomRole.Entry || room.role == DungeonRoomRole.Boss)
                light = set.fireLight ? DecorLight.Fire : DecorLight.None;
            else if (set.plantLight && (reg <= set.plantLightUpTo || !set.fireLight))
                light = DecorLight.Plant;
            else
                light = set.fireLight ? DecorLight.Fire : DecorLight.None;

            var pool = new List<DecorEntry>();
            for (int i = 0; i < set.entries.Count; i++)
            {
                DecorEntry e = set.entries[i];
                if (!e.Resolved || !e.AllowsRegister(reg) || !e.AllowsRole(room.role) || area < e.minRoomArea)
                    continue;
                if (e.dirtyOnly && !kind.dirty)
                    continue;
                DecorLight l = e.Light;
                if (l != DecorLight.None && l != light)
                    continue;
                pool.Add(e);
            }
            if (pool.Count == 0)
                return;

            float budget = DungeonTunables.DecorPointsByRank.Evaluate(rankIndex) * set.density
                           * DungeonTunables.DecorDensityMultiplier
                           * RoleFactor(room.role)
                           * Mathf.Lerp(DungeonTunables.DecorDepthStart, DungeonTunables.DecorDepthEnd, Mathf.Clamp01(room.depthFrac))
                           * Mathf.Clamp(area / DungeonTunables.DecorReferenceArea,
                                         DungeonTunables.DecorAreaFactorMin, DungeonTunables.DecorAreaFactorMax);

            var placedCount = new Dictionary<DecorEntry, int>();
            var failures = new Dictionary<DecorEntry, int>();
            int before = pieces;

            for (int attempt = 0; attempt < DungeonTunables.DecorAttemptsPerRoom && budget > 0.2f && pool.Count > 0; attempt++)
            {
                DecorEntry pick = PickAffordable(pool, budget);
                if (pick == null)
                    break;

                if (TryPlace(pick, room, roomIndex))
                {
                    budget -= pick.cost;
                    placedCount.TryGetValue(pick, out int n);
                    placedCount[pick] = ++n;
                    if (n >= pick.maxPerRoom)
                        pool.Remove(pick);
                }
                else
                {
                    failures.TryGetValue(pick, out int f);
                    failures[pick] = ++f;
                    if (f >= 3)
                        pool.Remove(pick);
                }
            }

            if (pieces > before)
                roomsDressed++;
        }

        private static float RoleFactor(DungeonRoomRole role)
        {
            switch (role)
            {
                case DungeonRoomRole.Entry: return DungeonTunables.DecorEntryFactor;
                case DungeonRoomRole.Treasure: return DungeonTunables.DecorTreasureFactor;
                case DungeonRoomRole.Boss: return DungeonTunables.DecorBossFactor;
                default: return DungeonTunables.DecorCombatFactor;
            }
        }

        private static DecorEntry PickAffordable(List<DecorEntry> pool, float budget)
        {
            float total = 0f;
            for (int i = 0; i < pool.Count; i++)
            {
                if (pool[i].cost <= budget + 0.5f)
                    total += pool[i].weight;
            }
            if (total <= 0f)
                return null;
            float roll = Rand.Value * total;
            for (int i = 0; i < pool.Count; i++)
            {
                if (pool[i].cost > budget + 0.5f)
                    continue;
                roll -= pool[i].weight;
                if (roll <= 0f)
                    return pool[i];
            }
            return pool[pool.Count - 1];
        }

        private bool TryPlace(DecorEntry e, DungeonRoom room, int roomIndex)
        {
            if (e.filthDef != null)
                return PlaceFilth(e, room, roomIndex);

            switch (e.placement)
            {
                case DecorPlacement.WallAttached: return PlaceOnWall(e, room, roomIndex, attached: true);
                case DecorPlacement.AgainstWall: return PlaceOnWall(e, room, roomIndex, attached: false);
                case DecorPlacement.Corner: return PlaceInCorner(e, room, roomIndex);
                case DecorPlacement.Centerpiece: return PlaceCenterpiece(e, room, roomIndex);
                case DecorPlacement.ColumnPairs: return PlaceColumns(e, room, roomIndex);
                case DecorPlacement.DoorFlank: return PlaceDoorFlank(e, roomIndex);
                case DecorPlacement.Ring: return PlaceRing(e, room, roomIndex);
                default: return PlaceScatter(e, room, roomIndex);
            }
        }

        /// <summary>A cell a solid piece may occupy: in this room, open, empty, and not reserved.</summary>
        private bool Free(IntVec3 c, int roomIndex, bool allowNearDoor = false)
        {
            if (!c.InBounds(map) || RoomIndexAt(c) != roomIndex)
                return false;
            int i = indices.CellToIndex(c);
            if (keepClear[i] || (nearDoor[i] && !allowNearDoor))
                return false;
            if (!c.Standable(map) || c.GetFirstBuilding(map) != null || c.GetFirstItem(map) != null)
                return false;
            Plant p = c.GetPlant(map);
            return p == null || !p.def.plant.IsTree;
        }

        private bool FootprintFree(ThingDef def, IntVec3 c, Rot4 rot, int roomIndex, bool allowNearDoor = false)
        {
            foreach (IntVec3 f in GenAdj.OccupiedRect(c, rot, def.size))
            {
                if (!Free(f, roomIndex, allowNearDoor))
                    return false;
            }
            return true;
        }

        private List<IntVec3> FreeCells(DungeonRoom room, int roomIndex)
        {
            var cells = new List<IntVec3>();
            foreach (IntVec3 c in room.rect)
            {
                if (Free(c, roomIndex))
                    cells.Add(c);
            }
            return cells;
        }

        private bool PlaceScatter(DecorEntry e, DungeonRoom room, int roomIndex)
        {
            List<IntVec3> cells = FreeCells(room, roomIndex);
            cells.Shuffle();
            for (int i = 0; i < cells.Count && i < 30; i++)
            {
                Rot4 rot = e.PlacedDef.rotatable ? Rot4.Random : Rot4.North;
                if (TrySpawn(e, cells[i], rot, room, roomIndex))
                {
                    GrowCluster(e, cells[i], room, roomIndex);
                    return true;
                }
            }
            return false;
        }

        private bool PlaceOnWall(DecorEntry e, DungeonRoom room, int roomIndex, bool attached)
        {
            var options = new List<(IntVec3 cell, Rot4 wall)>();
            foreach (IntVec3 c in room.rect)
            {
                if (!Free(c, roomIndex))
                    continue;
                for (int d = 0; d < 4; d++)
                {
                    Rot4 dir = new Rot4(d);
                    if (IsWall(c + dir.FacingCell))
                        options.Add((c, dir));
                }
            }
            options.Shuffle();
            for (int i = 0; i < options.Count && i < 40; i++)
            {
                // An attachment faces the wall it hangs on; furniture faces the room.
                Rot4 rot = attached ? options[i].wall : options[i].wall.Opposite;
                if (!e.PlacedDef.rotatable)
                    rot = Rot4.North;
                if (TrySpawn(e, options[i].cell, rot, room, roomIndex))
                {
                    GrowCluster(e, options[i].cell, room, roomIndex);
                    return true;
                }
            }
            return false;
        }

        private bool PlaceInCorner(DecorEntry e, DungeonRoom room, int roomIndex)
        {
            var corners = new List<(IntVec3 cell, Rot4 wall)>();
            foreach (IntVec3 c in room.rect)
            {
                if (!Free(c, roomIndex))
                    continue;
                bool n = IsWall(c + IntVec3.North), s = IsWall(c + IntVec3.South);
                bool w = IsWall(c + IntVec3.West), east = IsWall(c + IntVec3.East);
                if ((n || s) && (w || east))
                    corners.Add((c, n ? Rot4.North : Rot4.South));
            }
            corners.Shuffle();
            for (int i = 0; i < corners.Count; i++)
            {
                Rot4 rot = e.PlacedDef.rotatable ? corners[i].wall.Opposite : Rot4.North;
                if (TrySpawn(e, corners[i].cell, rot, room, roomIndex))
                {
                    GrowCluster(e, corners[i].cell, room, roomIndex);
                    return true;
                }
            }
            return false;
        }

        private bool PlaceCenterpiece(DecorEntry e, DungeonRoom room, int roomIndex)
        {
            IntVec3 center = room.rect.CenterCell;
            List<IntVec3> cells = FreeCells(room, roomIndex);
            cells.SortBy(c => c.DistanceToSquared(center));
            for (int i = 0; i < cells.Count && i < 25; i++)
            {
                if (TrySpawn(e, cells[i], e.PlacedDef.rotatable ? Rot4.South : Rot4.North, room, roomIndex))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// A colonnade: mirrored pairs down the long axis, inset two cells from
        /// the walls, spreading out from the middle so a short run still reads
        /// symmetric. The whole colonnade is one placement.
        /// </summary>
        private bool PlaceColumns(DecorEntry e, DungeonRoom room, int roomIndex)
        {
            CellRect r = room.rect;
            if (Mathf.Min(r.Width, r.Height) < 7)
                return false;
            bool alongX = r.Width >= r.Height;
            int inset = 2;
            int pitch = Mathf.Max(e.PlacedDef.size.x, e.PlacedDef.size.z) + 2;
            int lineA = alongX ? r.minZ + inset : r.minX + inset;
            int lineB = alongX ? r.maxZ - inset : r.maxX - inset;
            int lo = (alongX ? r.minX : r.minZ) + inset;
            int hi = (alongX ? r.maxX : r.maxZ) - inset;
            int mid = (lo + hi) / 2;

            int placed = 0;
            for (int k = 0; mid - k * pitch >= lo || mid + k * pitch <= hi; k++)
            {
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    if (k == 0 && sign > 0)
                        continue;
                    int along = mid + sign * k * pitch;
                    if (along < lo || along > hi)
                        continue;
                    IntVec3 a = alongX ? new IntVec3(along, 0, lineA) : new IntVec3(lineA, 0, along);
                    IntVec3 b = alongX ? new IntVec3(along, 0, lineB) : new IntVec3(lineB, 0, along);
                    if (!FootprintFree(e.PlacedDef, a, Rot4.North, roomIndex)
                        || !FootprintFree(e.PlacedDef, b, Rot4.North, roomIndex))
                        continue;
                    if (TrySpawn(e, a, Rot4.North, room, roomIndex) && TrySpawn(e, b, Rot4.North, room, roomIndex))
                        placed += 2;
                }
            }
            return placed > 0;
        }

        /// <summary>Either side of each doorway. On the wall for an attachment, one step in for anything standing.</summary>
        private bool PlaceDoorFlank(DecorEntry e, int roomIndex)
        {
            DungeonRoom room = comp.rooms[roomIndex];
            List<Entrance> list = entrances[roomIndex];
            bool attached = e.PlacedDef.building != null && e.PlacedDef.building.isAttachment;
            int placed = 0;
            for (int i = 0; i < list.Count; i++)
            {
                Entrance en = list[i];
                Rot4 side = en.inward.Rotated(RotationDirection.Clockwise);
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    IntVec3 c = en.cell + side.FacingCell * sign * 2;
                    Rot4 rot = attached ? en.inward.Opposite : en.inward;
                    if (!attached)
                        c += en.inward.FacingCell;
                    if (attached && !IsWall(c + en.inward.Opposite.FacingCell))
                        continue;
                    if (!e.PlacedDef.rotatable)
                        rot = Rot4.North;
                    if (FootprintFree(e.PlacedDef, c, rot, roomIndex, allowNearDoor: true)
                        && TrySpawn(e, c, rot, room, roomIndex, allowNearDoor: true))
                        placed++;
                }
            }
            return placed > 0;
        }

        /// <summary>Evenly spaced round the room's centre — standing stones, a colonnade on open ground.</summary>
        private bool PlaceRing(DecorEntry e, DungeonRoom room, int roomIndex)
        {
            CellRect r = room.rect;
            float radius = Mathf.Max(3f, Mathf.Min(r.Width, r.Height) * 0.33f);
            int count = radius >= 7f ? 8 : 6;
            float offset = Rand.Range(0f, 360f);
            IntVec3 center = r.CenterCell;
            int placed = 0;
            for (int i = 0; i < count; i++)
            {
                float angle = (offset + i * 360f / count) * Mathf.Deg2Rad;
                IntVec3 c = new IntVec3(center.x + Mathf.RoundToInt(Mathf.Cos(angle) * radius), 0,
                                        center.z + Mathf.RoundToInt(Mathf.Sin(angle) * radius));
                if (TrySpawn(e, c, Rot4.North, room, roomIndex))
                    placed++;
            }
            return placed > 0;
        }

        private bool PlaceFilth(DecorEntry e, DungeonRoom room, int roomIndex)
        {
            var cells = new List<IntVec3>();
            foreach (IntVec3 c in room.rect)
            {
                if (c.InBounds(map) && RoomIndexAt(c) == roomIndex && c.Standable(map) && c.GetFirstBuilding(map) == null)
                    cells.Add(c);
            }
            if (cells.Count == 0)
                return false;
            IntVec3 start = cells.RandomElement();
            int want = e.cluster.RandomInRange;
            int made = 0;
            int n = GenRadial.NumCellsInRadius(2.9f);
            for (int i = 0; i < n && made < want; i++)
            {
                IntVec3 c = start + GenRadial.RadialPattern[i];
                if (i > 0 && !Rand.Chance(0.55f))
                    continue;
                if (c.InBounds(map) && RoomIndexAt(c) == roomIndex && c.Standable(map)
                    && FilthMaker.TryMakeFilth(c, map, e.filthDef, 1))
                    made++;
            }
            if (made > 0)
                pieces++;
            return made > 0;
        }

        /// <summary>Extra pieces of a cluster round the first one. Small things only.</summary>
        private void GrowCluster(DecorEntry e, IntVec3 first, DungeonRoom room, int roomIndex)
        {
            int extra = e.cluster.RandomInRange - 1;
            if (extra <= 0)
                return;
            int n = GenRadial.NumCellsInRadius(2.2f);
            for (int i = 1; i < n && extra > 0; i++)
            {
                IntVec3 c = first + GenRadial.RadialPattern[i];
                if (Rand.Chance(0.5f) && TrySpawn(e, c, e.PlacedDef.rotatable ? Rot4.Random : Rot4.North, room, roomIndex))
                    extra--;
            }
        }

        // ── Spawning ────────────────────────────────────────────────────────

        private bool TrySpawn(DecorEntry e, IntVec3 c, Rot4 rot, DungeonRoom room, int roomIndex, bool allowNearDoor = false)
        {
            ThingDef def = e.PlacedDef;
            if (def == null || !FootprintFree(def, c, rot, roomIndex, allowNearDoor))
                return false;

            DecorLight light = e.Light;
            if (light == DecorLight.Fire
                && (!FarFrom(plantLights, c, DungeonTunables.DecorPlantFireDistance)
                    || !FarFrom(fireLights, c, DungeonTunables.DecorFireSpacing)))
                return false;
            if (light == DecorLight.Plant && !FarFrom(fireLights, c, DungeonTunables.DecorPlantFireDistance))
                return false;

            CellRect footprint = GenAdj.OccupiedRect(c, rot, def.size);
            if (def.passability == Traversability.Impassable && !KeepsRoomConnected(footprint, room, roomIndex))
                return false;

            if (e.patchDef != null)
            {
                foreach (IntVec3 f in footprint)
                {
                    if (!f.GetTerrain(map).IsWater)
                        map.terrainGrid.SetTerrain(f, e.patchDef);
                }
            }

            if (e.plantDef != null)
            {
                var plant = (Plant)ThingMaker.MakeThing(e.plantDef);
                plant.Growth = Rand.Range(0.75f, 1f);
                GenSpawn.Spawn(plant, c, map);
            }
            else
            {
                GenSpawn.Spawn(ThingMaker.MakeThing(def), c, map, rot, WipeMode.Vanish);
            }

            if (light == DecorLight.Fire)
                fireLights.Add(c);
            else if (light == DecorLight.Plant)
                plantLights.Add(c);
            pieces++;
            return true;
        }

        /// <summary>
        /// Whether a solid footprint would cut the room: every open cell and every
        /// doorway must still reach every other with the footprint blocked. On an
        /// open field there are no doorways, so the footprint just has to stand
        /// clear of every other solid thing.
        /// </summary>
        private bool KeepsRoomConnected(CellRect footprint, DungeonRoom room, int roomIndex)
        {
            if (!carved)
            {
                foreach (IntVec3 c in footprint.ExpandedBy(1))
                {
                    if (!footprint.Contains(c) && !Passable(c))
                        return false;
                }
                return true;
            }

            var open = new HashSet<IntVec3>();
            foreach (IntVec3 c in room.rect)
            {
                if (!footprint.Contains(c) && Passable(c))
                    open.Add(c);
            }
            if (open.Count == 0)
                return false;

            IntVec3 start = IntVec3.Invalid;
            List<Entrance> list = entrances[roomIndex];
            for (int i = 0; i < list.Count && !start.IsValid; i++)
            {
                if (open.Contains(list[i].cell))
                    start = list[i].cell;
            }
            if (!start.IsValid)
            {
                foreach (IntVec3 c in open)
                {
                    start = c;
                    break;
                }
            }

            var seen = new HashSet<IntVec3> { start };
            var queue = new Queue<IntVec3>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                IntVec3 c = queue.Dequeue();
                for (int d = 0; d < 4; d++)
                {
                    IntVec3 n = c + GenAdj.CardinalDirections[d];
                    if (open.Contains(n) && seen.Add(n))
                        queue.Enqueue(n);
                }
            }
            return seen.Count == open.Count;
        }

        // ── Corridors ───────────────────────────────────────────────────────

        private void DressCorridors()
        {
            int spacing = DungeonTunables.CorridorLightSpacingFor(baseRegister);
            bool lights = set.corridorLightDef != null && spacing > 0 && baseRegister >= set.corridorLightFrom;
            var filthDefs = new List<ThingDef>(set.corridorFilthDefs);
            if (kind.dirty)
                filthDefs.AddRange(set.corridorFilthDirtyDefs);
            bool filth = filthDefs.Count > 0;
            if (!lights && !filth)
                return;

            var placedLights = new List<IntVec3>();
            float filthChance = 0.015f * (1f + (int)baseRegister * 0.35f);

            foreach (IntVec3 c in map.AllCells)
            {
                if (!IsCorridorCell(c) || c.GetEdifice(map) != null)
                    continue;

                if (filth && Rand.Chance(filthChance) && c.Standable(map))
                    FilthMaker.TryMakeFilth(c, map, filthDefs.RandomElement(), 1);

                if (!lights || !c.Standable(map) || !FarFrom(placedLights, c, spacing)
                    || !FarFrom(plantLights, c, DungeonTunables.DecorPlantFireDistance))
                    continue;

                // Never beside a doorway: the door is the fog boundary and the
                // torch would light the room before it is entered.
                bool byDoor = false;
                for (int i = 0; i < 8 && !byDoor; i++)
                {
                    IntVec3 n = c + GenAdj.AdjacentCells[i];
                    byDoor = n.InBounds(map) && n.GetEdifice(map) is Building_Door;
                }
                if (byDoor)
                    continue;

                for (int d = 0; d < 4; d++)
                {
                    Rot4 dir = new Rot4(d);
                    if (!IsWall(c + dir.FacingCell))
                        continue;
                    GenSpawn.Spawn(ThingMaker.MakeThing(set.corridorLightDef), c, map, dir, WipeMode.Vanish);
                    placedLights.Add(c);
                    fireLights.Add(c);
                    corridorLights++;
                    break;
                }
            }
        }
    }
}
