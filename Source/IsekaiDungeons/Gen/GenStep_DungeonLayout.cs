using System.Collections.Generic;
using IsekaiLeveling.MobRanking;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Carves the dungeon interior out of the solid rock fill.
    ///
    /// Runs AFTER the core rock pipeline (ElevationFertility →
    /// Underground_RocksFromGrid → Terrain, our XML order 410), so at this
    /// point every cell holds a natural-rock edifice under a thick rock roof
    /// (MapGenerator pre-fills RoofRockThick on isUnderground generators).
    /// This step:
    ///   1. resolves rank/kind for the dungeon being generated,
    ///   2. runs the pure DungeonLayoutPlan algorithm,
    ///   3. carves every room + corridor cell — destroy the rock, floor the
    ///      cell, swap the roof to thin rock,
    ///   4. hands the room list to DungeonMapComponent for the later GenSteps
    ///      (entrance, population) and the runtime aggro/clear logic.
    ///
    /// Fog is deliberately NOT touched here — the core Fog GenStep runs after
    /// us and fogs everything outside the player start room on its own.
    /// </summary>
    public class GenStep_DungeonLayout : GenStep
    {
        /// <summary>Arbitrary unique constant — must differ from every other GenStep's SeedPart.</summary>
        public override int SeedPart => 823471203;

        /// <summary>Per-cell snapshot buffer: GetThingList returns the LIVE grid list and
        /// Destroy() mutates it, so we copy before iterating. Map generation is
        /// single-threaded; one static scratch list is safe and allocation-free per cell.</summary>
        private static readonly List<Thing> tmpThings = new List<Thing>();

        public override void Generate(Map map, GenStepParams parms)
        {
            ResolveContext(map, out DungeonKindDef kind, out MobRankTier rank);

            // SEEDING. Vanilla wraps every GenStep.Generate in
            // Rand.PushState(HashCombine(mapSeed, SeedPart)), so plain Rand here
            // is already deterministic for a given map — which is enough while a
            // map is generated once and then lives forever.
            //
            // It is NOT enough for a map that gets destroyed and rebuilt. The
            // pocket-map seed is HashCombineInt(worldSeed, parent.ID) and a
            // rebuilt map gets a fresh parent, so Rand.Int would hand out a
            // different layout for what is supposed to be the same place. A
            // caller that needs reproducibility therefore pins the seed on the
            // map parent, and we use that instead of rolling.
            var dungeonParent = map.Parent as DungeonMapParent;
            int seed = dungeonParent != null && dungeonParent.layoutSeed != -1
                ? dungeonParent.layoutSeed
                : Rand.Int;
            float rankIndex = dungeonParent?.RankIndex ?? (int)rank;
            DungeonLayoutPlan plan = DungeonLayoutPlan.Generate(
                map.Size.x, seed, kind, rank, rankIndex,
                dungeonParent?.roomCountFactor ?? 1f,
                dungeonParent?.treasureRoomsOverride ?? -1);

            // Carve (subtractive, underground) or Build (additive, surface).
            // Both end with the same invariant: every room/corridor cell is
            // floored and passable, and the complex is ringed by Full-fillage
            // edifices so the fog flood-fill cannot leak between rooms.
            if (kind.layoutMode == DungeonLayoutMode.Open)
            {
                // OPEN: touch NOTHING. The biome generator already produced the
                // field, and that field IS the dungeon. No carving, no wall
                // shell, no doors, no corridors — walls on an open plain would
                // just be a room dungeon wearing a snow texture. The zones in
                // the plan are invisible encounter anchors, not architecture;
                // difficulty comes from what stands where, and the map edge
                // already contains the party (pocket maps have no exit grid).
                // Decor (ruins, chunks) is handled by vanilla scatter GenSteps
                // listed on the surface MapGeneratorDefs, not by us.
            }
            else
            {
                bool[] inside = BuildInsideGrid(map, plan);
                if (kind.layoutMode == DungeonLayoutMode.Build)
                    BuildPlan(map, plan, kind, inside);
                else
                    CarvePlan(map, plan, kind);

                // Doors last: they need their cell already floored and cleared.
                if (kind.placeDoors)
                    PlaceDoors(map, plan, kind, inside);
            }

            DungeonMapComponent comp = map.GetComponent<DungeonMapComponent>();
            if (comp != null)
                comp.RegisterLayout(plan.rooms);
            else
                // Someone pointed a foreign MapGeneratorDef at this GenStep.
                // The map still carves (harmless), but no rooms means no
                // population, no aggro and no clear — say so loudly (G5).
                Log.Error($"{IsekaiDungeonsMod.LogPrefix} GenStep_DungeonLayout ran on a map without DungeonMapComponent — the generator def must list IsekaiDungeons.DungeonMapComponent in customMapComponents.");
        }

        // ── Context resolution ──────────────────────────────────────────────

        /// <summary>
        /// Rank/kind source, in the contract's priority order:
        ///   1. map.Parent as DungeonMapParent — the gate assigns both fields
        ///      BEFORE calling MapGenerator.GenerateMap, and the parent
        ///      outlives the gate, so it is the canonical identity;
        ///   2. PocketMapUtility.currentlyGeneratingPortal as the gate — set
        ///      by vanilla MapPortal.GeneratePocketMap around generation;
        ///   3. rank F + the BreakDungeon def, with a warning — a wrong-but-
        ///      playable dungeon beats a silently empty map (G5).
        /// Internal so GenStep_DungeonEntrance reuses the identical logic.
        /// </summary>
        /// <summary>
        /// Kept as a thin forwarder so existing call sites read naturally; the
        /// real implementation is PUBLIC on DungeonContext so third-party gen
        /// steps can use the same resolution the built-in ones do.
        /// </summary>
        internal static void ResolveContext(Map map, out DungeonKindDef kind, out MobRankTier rank)
        {
            DungeonContext.Resolve(map, out kind, out rank);
        }

        // -- Shared geometry -------------------------------------------------

        /// <summary>
        /// Flat bool grid of every cell that ends up INSIDE the dungeon (rooms
        /// plus corridors). Built once and shared by the carve/build/door
        /// passes so overlapping rects - corridor legs overlap rooms and each
        /// other by construction - are handled exactly once, and all three
        /// passes agree on what "inside" means.
        /// </summary>
        private static bool[] BuildInsideGrid(Map map, DungeonLayoutPlan plan)
        {
            bool[] inside = new bool[map.cellIndices.NumGridCells]; // generation-time alloc - fine
            for (int i = 0; i < plan.rooms.Count; i++)
                MarkRect(map, plan.rooms[i].rect, inside);
            for (int i = 0; i < plan.corridors.Count; i++)
                MarkRect(map, plan.corridors[i], inside);
            return inside;
        }

        // -- Building (surface dungeons) -------------------------------------

        /// <summary>
        /// Additive counterpart to CarvePlan, for dungeons whose environment is
        /// an open biome rather than solid rock (snowfields, ruins, foundries).
        ///
        /// Two passes:
        ///   1. INTERIOR - clear every inside cell of blocking things (trees,
        ///      boulders, generator scatter) and lay the themed floor. Roofing
        ///      is opt-in: open sky is what sells "another world" rather than
        ///      "a building", and it also frees the room-size roof-support cap.
        ///   2. SHELL - every cell that is NOT inside but touches an inside
        ///      cell 8-way becomes a wall. That ring is what makes the fog
        ///      flood-fill stop (ThingDef.MakeFog == Fillage.Full), which is
        ///      the whole reason unexplored rooms stay dark.
        /// </summary>
        private static void BuildPlan(Map map, DungeonLayoutPlan plan, DungeonKindDef kind, bool[] inside)
        {
            CellIndices indices = map.cellIndices;
            TerrainDef floor = kind.floorTerrain;
            ThingDef wallDef = kind.wallDef ?? ThingDefOf.Wall;
            ThingDef wallStuff = kind.wallStuff
                ?? (wallDef.MadeFromStuff ? GenStuff.DefaultStuffFor(wallDef) : null);

            // Pass 1 - interior.
            foreach (IntVec3 cell in map.AllCells)
            {
                if (!inside[indices.CellToIndex(cell)])
                    continue;

                ClearCell(map, cell);
                if (floor != null)
                    map.terrainGrid.SetTerrain(cell, floor);
                map.roofGrid.SetRoof(cell, kind.roofed ? RoofDefOf.RoofConstructed : null);
            }

            // Pass 2 - shell. Collected first so we never read a cell we just
            // wrote while deciding whether its neighbour is inside.
            List<IntVec3> shell = new List<IntVec3>();
            foreach (IntVec3 cell in map.AllCells)
            {
                if (inside[indices.CellToIndex(cell)])
                    continue;
                if (!TouchesInside(map, cell, inside))
                    continue;
                shell.Add(cell);
            }

            for (int i = 0; i < shell.Count; i++)
            {
                IntVec3 cell = shell[i];
                ClearCell(map, cell);
                Thing wall = ThingMaker.MakeThing(wallDef, wallStuff);
                GenSpawn.Spawn(wall, cell, map, WipeMode.Vanish);
            }
        }

        /// <summary>True when any 8-way neighbour of the cell is inside the dungeon.</summary>
        private static bool TouchesInside(Map map, IntVec3 cell, bool[] inside)
        {
            CellIndices indices = map.cellIndices;
            for (int i = 0; i < 8; i++)
            {
                IntVec3 n = cell + GenAdj.AdjacentCells[i];
                if (n.InBounds(map) && inside[indices.CellToIndex(n)])
                    return true;
            }
            return false;
        }

        /// <summary>Destroys everything destroyable standing on a cell (snapshot-safe, never pawns).</summary>
        private static void ClearCell(Map map, IntVec3 cell)
        {
            tmpThings.Clear();
            tmpThings.AddRange(cell.GetThingList(map));
            for (int i = 0; i < tmpThings.Count; i++)
            {
                Thing t = tmpThings[i];
                if (t.def.destroyable && t.def.category != ThingCategory.Pawn)
                    t.Destroy();
            }
            tmpThings.Clear();
        }

        // -- Doors -----------------------------------------------------------

        /// <summary>
        /// Puts a door on every cell where a corridor meets a room.
        ///
        /// This is the exploration mechanic, not decoration: FloodFillerFog
        /// stops at Full-fillage edifices, and doors are fillPercent 1, so a
        /// closed door is a fog boundary exactly like a wall. Without these,
        /// stepping into a corridor flood-unfogs every room that corridor
        /// touches and the dungeon is revealed at a glance. See
        /// Building_DungeonDoor for why they latch open once used.
        ///
        /// Detection is geometric: walk each room's 1-cell perimeter ring; any
        /// ring cell that is itself "inside" the complex must be a corridor
        /// mouth, because rooms are always placed with a wall gap between them.
        /// A 2-wide corridor therefore yields a 2-cell doorway, which reads
        /// correctly as a vanilla double door.
        /// </summary>
        private static void PlaceDoors(Map map, DungeonLayoutPlan plan, DungeonKindDef kind, bool[] inside)
        {
            ThingDef doorDef = kind.doorDef ?? IsekaiDungeonsDefOf.IsekaiDungeons_DungeonDoor;
            if (doorDef == null)
                return;
            ThingDef doorStuff = kind.doorStuff
                ?? (doorDef.MadeFromStuff ? GenStuff.DefaultStuffFor(doorDef) : null);

            ThingDef sealDef = ResolveSealDef(map, kind);
            ThingDef sealStuff = (sealDef != null && sealDef.MadeFromStuff)
                ? (kind.wallStuff ?? GenStuff.DefaultStuffFor(sealDef))
                : null;

            int mouthWidth = Mathf.Clamp(kind.corridorWidth, 2, 4);   // matches DungeonLayoutPlan
            int placed = 0;
            int sealedCells = 0;

            for (int r = 0; r < plan.rooms.Count; r++)
            {
                CellRect room = plan.rooms[r].rect;
                CellRect ring = room.ExpandedBy(1);

                // One edge at a time so a "run" is strictly one-dimensional and
                // contiguity is unambiguous.
                ProcessEdge(map, EdgeCells(ring, Rot4.North), IntVec3.North, inside, mouthWidth,
                            doorDef, doorStuff, sealDef, sealStuff, ref placed, ref sealedCells);
                ProcessEdge(map, EdgeCells(ring, Rot4.South), IntVec3.South, inside, mouthWidth,
                            doorDef, doorStuff, sealDef, sealStuff, ref placed, ref sealedCells);
                ProcessEdge(map, EdgeCells(ring, Rot4.East), IntVec3.East, inside, mouthWidth,
                            doorDef, doorStuff, sealDef, sealStuff, ref placed, ref sealedCells);
                ProcessEdge(map, EdgeCells(ring, Rot4.West), IntVec3.West, inside, mouthWidth,
                            doorDef, doorStuff, sealDef, sealStuff, ref placed, ref sealedCells);
            }

            if (placed == 0)
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Placed no dungeon doors - rooms will unfog as a group, losing the room-by-room reveal.");
            else if (Prefs.DevMode)
                Log.Message($"{IsekaiDungeonsMod.LogPrefix} Dungeon doors: {placed} placed, {sealedCells} over-wide boundary cells sealed.");
        }

        /// <summary>
        /// Cells along one edge of a room's perimeter ring, corners EXCLUDED,
        /// in scan order (west to east, or south to north) so that adjacency in
        /// the returned list means adjacency on the map.
        /// </summary>
        private static List<IntVec3> EdgeCells(CellRect ring, Rot4 side)
        {
            var cells = new List<IntVec3>();
            if (side == Rot4.North || side == Rot4.South)
            {
                int z = side == Rot4.North ? ring.maxZ : ring.minZ;
                for (int x = ring.minX + 1; x <= ring.maxX - 1; x++)
                    cells.Add(new IntVec3(x, 0, z));
            }
            else
            {
                int x = side == Rot4.East ? ring.maxX : ring.minX;
                for (int z = ring.minZ + 1; z <= ring.maxZ - 1; z++)
                    cells.Add(new IntVec3(x, 0, z));
            }
            return cells;
        }

        /// <summary>
        /// Turns the open stretches of one room edge into proper doorways.
        ///
        /// A legitimate corridor mouth is exactly corridorWidth cells wide, so
        /// any contiguous open run of that size (or one cell more, allowing for
        /// a diagonal approach) becomes doors end to end.
        ///
        /// A LONGER run is the UNION of two different things: a corridor running
        /// flush along this edge (the artifact that produced long lines of doors)
        /// AND, usually, the room's real perpendicular corridor mouth somewhere
        /// inside that same run. Picking the doorway by geometric CENTRE - the
        /// first attempt at this - lands the doors on the flush stretch, which
        /// dead-ends against solid ground one cell further out, and seals the
        /// real mouth: the room chain is severed and the boss room can become
        /// unreachable, making the dungeon uncompletable.
        ///
        /// So the doorway is chosen by TOPOLOGY, not position: a cell is a
        /// genuine mouth when its OUTWARD neighbour (one cell further from the
        /// room, perpendicular to this edge) is also inside the complex, i.e.
        /// the corridor really does continue away from the room. Flush overlap
        /// fails that test and is sealed; every genuine mouth stays open.
        /// </summary>
        private static void ProcessEdge(Map map, List<IntVec3> edge, IntVec3 outward, bool[] inside, int mouthWidth,
                                        ThingDef doorDef, ThingDef doorStuff,
                                        ThingDef sealDef, ThingDef sealStuff,
                                        ref int placed, ref int sealedCells)
        {
            CellIndices indices = map.cellIndices;
            int i = 0;
            while (i < edge.Count)
            {
                if (!IsOpenCandidate(map, edge[i], inside, indices))
                {
                    i++;
                    continue;
                }

                int runStart = i;
                while (i < edge.Count && IsOpenCandidate(map, edge[i], inside, indices))
                    i++;
                int runLength = i - runStart;

                bool naturalMouth = runLength <= mouthWidth + 1;

                // Does this over-wide run contain any real passage at all?
                bool anyMouth = false;
                if (!naturalMouth)
                {
                    for (int c = runStart; c < runStart + runLength; c++)
                    {
                        if (IsCorridorMouth(map, edge[c], outward, inside, indices))
                        {
                            anyMouth = true;
                            break;
                        }
                    }
                }

                // Fallback if a run somehow contains no outward passage: keep a
                // centred doorway open rather than walling the run shut. Sealing
                // every cell of a run is the one outcome that can disconnect a
                // room, so it must never be reachable (G5).
                int fallbackStart = runStart + (runLength - Mathf.Min(runLength, mouthWidth)) / 2;
                int fallbackEnd = fallbackStart + Mathf.Min(runLength, mouthWidth) - 1;

                for (int c = runStart; c < runStart + runLength; c++)
                {
                    IntVec3 cell = edge[c];
                    bool isDoor = naturalMouth
                        || (anyMouth ? IsCorridorMouth(map, cell, outward, inside, indices)
                                     : (c >= fallbackStart && c <= fallbackEnd));

                    ClearCell(map, cell);
                    if (isDoor)
                    {
                        // Factionless on purpose - Building_DungeonDoor.PawnCanOpen
                        // grants the delving party passage explicitly and denies
                        // the dungeon's own creatures.
                        GenSpawn.Spawn(ThingMaker.MakeThing(doorDef, doorStuff), cell, map, WipeMode.Vanish);
                        placed++;
                    }
                    else if (sealDef != null)
                    {
                        GenSpawn.Spawn(ThingMaker.MakeThing(sealDef, sealStuff), cell, map, WipeMode.Vanish);
                        sealedCells++;
                    }
                }
            }
        }

        /// <summary>
        /// True when this ring cell is a real corridor mouth: the cell one step
        /// OUTWARD (away from the room, perpendicular to its edge) is also part
        /// of the complex, so the corridor genuinely continues rather than just
        /// running flush along the room's wall.
        /// </summary>
        private static bool IsCorridorMouth(Map map, IntVec3 cell, IntVec3 outward, bool[] inside, CellIndices indices)
        {
            IntVec3 beyond = cell + outward;
            if (!beyond.InBounds(map))
                return false;
            return inside[indices.CellToIndex(beyond)];
        }

        /// <summary>A ring cell that is open (part of the complex) and not already a door.</summary>
        private static bool IsOpenCandidate(Map map, IntVec3 cell, bool[] inside, CellIndices indices)
        {
            if (!cell.InBounds(map))
                return false;
            if (!inside[indices.CellToIndex(cell)])
                return false;
            return !(cell.GetEdifice(map) is Building_Door);
        }

        /// <summary>
        /// What to seal an over-wide boundary with. Build dungeons use their own
        /// themed wall. Carve dungeons sample the map's actual natural rock so
        /// the patch is indistinguishable from the surrounding cave wall rather
        /// than reading as a constructed wall in the middle of a cavern.
        /// </summary>
        private static ThingDef ResolveSealDef(Map map, DungeonKindDef kind)
        {
            if (kind.layoutMode == DungeonLayoutMode.Build)
                return kind.wallDef ?? ThingDefOf.Wall;

            List<Thing> all = map.listerThings.AllThings;
            for (int i = 0; i < all.Count; i++)
            {
                ThingDef d = all[i].def;
                if (d?.building != null && d.building.isNaturalRock && d.passability == Traversability.Impassable)
                    return d;
            }
            // Map somehow has no rock left (fully carved); a plain wall still
            // seals the fog, which is what actually matters here.
            return kind.wallDef ?? ThingDefOf.Wall;
        }


        // ── Carving ─────────────────────────────────────────────────────────

        /// <summary>
        /// Carves the union of all room and corridor rects. Marked into a flat
        /// bool grid first so overlapping rects (corridor legs overlap rooms
        /// and each other by construction) are carved exactly once.
        /// </summary>
        private static void CarvePlan(Map map, DungeonLayoutPlan plan, DungeonKindDef kind)
        {
            CellIndices indices = map.cellIndices;
            bool[] carve = new bool[indices.NumGridCells]; // generation-time alloc - fine

            for (int i = 0; i < plan.rooms.Count; i++)
                MarkRect(map, plan.rooms[i].rect, carve);
            for (int i = 0; i < plan.corridors.Count; i++)
                MarkRect(map, plan.corridors[i], carve);

            TerrainDef forcedFloor = kind != null ? kind.floorTerrain : null;

            foreach (IntVec3 cell in map.AllCells)
            {
                if (carve[indices.CellToIndex(cell)])
                    CarveCell(map, cell, forcedFloor);
            }
        }

        /// <summary>
        /// Marks a rect for carving, clipped to the map and additionally kept
        /// one cell off the map border. The plan already guarantees a 4-cell
        /// margin for rooms; this guard is belt-and-braces for corridors on
        /// degenerate map sizes — carving the outermost ring would let pawns
        /// path along the map edge and looks broken besides.
        /// </summary>
        private static void MarkRect(Map map, CellRect rect, bool[] carve)
        {
            CellRect clipped = rect.ClipInsideMap(map);
            CellIndices indices = map.cellIndices;
            int maxXBound = map.Size.x - 2;
            int maxZBound = map.Size.z - 2;
            foreach (IntVec3 cell in clipped.Cells)
            {
                if (cell.x < 1 || cell.z < 1 || cell.x > maxXBound || cell.z > maxZBound)
                    continue;
                carve[indices.CellToIndex(cell)] = true;
            }
        }

        /// <summary>
        /// Opens one cell:
        ///  - remembers the rock edifice's natural floor BEFORE destroying it
        ///    (that def is the terrain a pawn-mined cell would get, so carved
        ///    granite floors read as granite, marble as marble…),
        ///  - destroys mineables/edifices/rock chunks (destroyable only),
        ///  - floors the cell: def.floorTerrain override → mined rock's
        ///    naturalTerrain → Gravel as the final fallback,
        ///  - swaps the pre-filled RoofRockThick for RoofRockThin. Thick roof
        ///    over an open room interior is collapse bait (thick roofs need
        ///    support within ~6.9 cells and room centers can exceed that);
        ///    thin rock roof is what vanilla caves use over open space and it
        ///    keeps the map fully enclosed. Wall cells are never carved, so
        ///    they keep their thick roof and the mountain look.
        /// </summary>
        private static void CarveCell(Map map, IntVec3 cell, TerrainDef forcedFloor)
        {
            Building edifice = cell.GetEdifice(map);
            TerrainDef naturalFloor = null;
            if (edifice != null && edifice.def.building != null)
                naturalFloor = edifice.def.building.naturalTerrain;

            tmpThings.Clear();
            tmpThings.AddRange(cell.GetThingList(map));
            for (int i = 0; i < tmpThings.Count; i++)
            {
                Thing thing = tmpThings[i];
                if (thing.Destroyed || !thing.def.destroyable)
                    continue;
                // Rock walls are mineable Buildings; compressed-rock chunks
                // (if a future generator scatters any) are Items in the chunk
                // categories. Anything else this early in the pipeline is left
                // alone on purpose.
                bool isChunk = thing.def.thingCategories != null &&
                    (thing.def.thingCategories.Contains(ThingCategoryDefOf.Chunks) ||
                     thing.def.thingCategories.Contains(ThingCategoryDefOf.StoneChunks));
                if (thing.def.mineable || thing is Building || isChunk)
                    thing.Destroy();
            }

            TerrainDef floor = forcedFloor ?? naturalFloor ?? TerrainDefOf.Gravel;
            map.terrainGrid.SetTerrain(cell, floor);
            map.roofGrid.SetRoof(cell, RoofDefOf.RoofRockThin);
        }
    }
}
