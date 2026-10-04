using System.Collections.Generic;
using IsekaiLeveling.MobRanking;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// PURE layout algorithm for one dungeon: rooms + corridor carve rects,
    /// no map access, no side effects. Fully deterministic for a given seed —
    /// every Rand call happens inside Rand.PushState(seed)/PopState, so the
    /// same (mapSize, seed, def, rank) always yields the same plan. That
    /// separation keeps the algorithm unit-testable and lets debug tools
    /// re-roll layouts without touching a map (ARCHITECTURE §6.1).
    ///
    /// Shape of the result:
    ///  - rooms: Entry FIRST (index 0), Boss LAST, Combat then Treasure rooms
    ///    between, indices sequential — GenStep_DungeonPopulation ramps
    ///    encounter difficulty off this ordering.
    ///  - corridors: plain carve rects (each L-shaped link is two overlapping
    ///    legs). GenStep_DungeonLayout carves the union of rooms + corridors;
    ///    nothing here needs to know how carving works.
    ///
    /// Placement philosophy (G5): constrained placement first, then shrink,
    /// then relax spacing — each downgrade logged, and the mandatory rooms
    /// (entry/boss) get a final deterministic grid scan so the plan is NEVER
    /// returned without them. A dungeon with fewer combat rooms than rolled is
    /// acceptable; a dungeon without an entry or boss is not.
    /// </summary>
    public class DungeonLayoutPlan
    {
        /// <summary>Entry first, Boss last, Combat/Treasure between; index set sequentially.</summary>
        public List<DungeonRoom> rooms = new List<DungeonRoom>();

        /// <summary>Corridor carve rects. L-shaped links contribute two overlapping legs each.</summary>
        public List<CellRect> corridors = new List<CellRect>();

        // ── Shape record ────────────────────────────────────────────────────
        // What the generator actually managed to build, as opposed to what the
        // rank tables asked for. Placement can always fail on a crowded map, and
        // a feature that silently does not happen is worse than one that fails
        // loudly — the debug preview reports these across many seeds.

        /// <summary>Loops actually carved (shortcut corridors that found clean geometry).</summary>
        public int shortcutsPlaced;
        /// <summary>Loops the rank table budgeted for.</summary>
        public int shortcutsBudgeted;
        /// <summary>Rooms placed on optional branches, across all wings.</summary>
        public int wingRoomsPlaced;
        /// <summary>Longest wing actually built, in rooms.</summary>
        public int longestWing;

        // ── Layout constants ────────────────────────────────────────────────
        // Not in DungeonTunables: these are structural invariants of the
        // carving algorithm (edge safety, roof support, corridor junction
        // math), not gameplay balance knobs.

        /// <summary>Rooms keep at least this many cells to the map edge (contract: >= 4).</summary>
        private const int EdgeMargin = 4;

        /// <summary>Minimum rock cells between two rooms. 3 keeps walls chunky enough that
        /// pawns can't shortcut between rooms through a stray diagonal carve.</summary>
        private const int RoomGap = 3;

        /// <summary>Fallback gap when normal placement fails — still a solid wall, just thinner.</summary>
        private const int RelaxedRoomGap = 2;

        /// <summary>Random placement attempts per relaxation phase before downgrading.</summary>
        private const int TriesPerPhase = 40;

        /// <summary>Perpendicular jitter of the main chain, as a fraction of map size.
        /// 0.16 gives a visibly snaking path without pushing rooms into the corners.</summary>
        private const float PathJitterFrac = 0.16f;

        /// <summary>Along-path jitter fraction — irregular spacing so rooms don't sit on a metronome.</summary>
        private const float AlongJitterFrac = 0.05f;

        /// <summary>
        /// Produces a complete plan. mapSize is the map edge length (dungeon
        /// pocket maps are square). def/rank drive room count and treasure
        /// count; def may be null in degenerate dev-tool cases — every read
        /// has a safe default (G3).
        /// </summary>
        /// <summary>
        /// <paramref name="rankIndex"/> is the CONTINUOUS position on the
        /// F..Nation ladder used for the room-count curve; -1 means "use the
        /// enum", which is what every existing caller does. The enum is still
        /// needed separately for the rank-keyed complexity tables.
        /// </summary>
        public static DungeonLayoutPlan Generate(int mapSize, int seed, DungeonKindDef def, MobRankTier rank,
                                                 float rankIndex = -1f, float roomCountFactor = 1f,
                                                 int treasureRoomsOverride = -1)
        {
            DungeonLayoutPlan plan = new DungeonLayoutPlan();
            // The plan owns its own Rand bracket so it is deterministic even
            // when called OUTSIDE the seeded GenStep pipeline (tests, debug
            // actions). Push/Pop in try/finally so an exception can never
            // leave the global Rand state corrupted.
            Rand.PushState(seed);
            try
            {
                plan.GenerateInt(mapSize, def, rank, rankIndex >= 0f ? rankIndex : (int)rank,
                                 roomCountFactor, treasureRoomsOverride);
            }
            finally
            {
                Rand.PopState();
            }
            return plan;
        }

        // ── Core algorithm ──────────────────────────────────────────────────

        private void GenerateInt(int mapSize, DungeonKindDef def, MobRankTier rank, float rankIndex,
                                 float roomCountFactor = 1f, int treasureRoomsOverride = -1)
        {
            // Defensive clamps (G3): a broken XML curve or a tiny dev-spawned
            // map must degrade into a small-but-valid dungeon, never a crash
            // or an empty plan.
            mapSize = Mathf.Max(mapSize, 60);

            // OPEN dungeons use encounter-zone radii instead of room sizes: the
            // "rect" is just the patch of open ground a group holds, so it is
            // sized from openZoneRadiusRange (diameter) rather than from the
            // room-sized roomSizeRange.
            bool openMode = def != null && def.layoutMode == DungeonLayoutMode.Open;
            IntRange sizeRange = openMode
                ? new IntRange(def.openZoneRadiusRange.min * 2, def.openZoneRadiusRange.max * 2)
                : (def != null ? def.roomSizeRange : new IntRange(8, 12));
            int minEdge = Mathf.Max(5, sizeRange.min);
            int maxUsable = Mathf.Max(minEdge, (mapSize - 2 * EdgeMargin) / 3);
            int maxEdge = Mathf.Clamp(sizeRange.max, minEdge, maxUsable);
            sizeRange = new IntRange(minEdge, maxEdge);

            int corridorWidth = Mathf.Clamp(def != null ? def.corridorWidth : 2, 2, 4);   // never 1: see DungeonKindDef.ConfigErrors

            // Every room rect must be fully inside this — guarantees the
            // >= 4 cells of untouched rock against the map edge.
            CellRect interior = new CellRect(EdgeMargin, EdgeMargin,
                mapSize - 2 * EdgeMargin, mapSize - 2 * EdgeMargin);

            // Room-center inset: far enough from the edge that even a max-size
            // room centered there still fits inside the interior rect.
            int inset = EdgeMargin + maxEdge / 2 + 1;

            // 1. Entry near a random map edge; boss near the opposite corner —
            //    maximizes the crawl distance and makes the layout readable
            //    ("go away from the exit portal to finish").
            PickAnchors(mapSize, inset, out IntVec3 entryTarget, out IntVec3 bossTarget);

            List<CellRect> placed = new List<CellRect>();

            CellRect entryRect = PlaceMandatoryRoom(entryTarget, sizeRange, interior, placed, mapSize, "entry");
            placed.Add(entryRect);
            CellRect bossRect = PlaceMandatoryRoom(bossTarget, sizeRange, interior, placed, mapSize, "boss");
            placed.Add(bossRect);

            // 2. Combat rooms along a jittered entry→boss path. Count from the
            //    rank curve, clamped so a bad curve can't demand more rooms
            //    than the map can hold (G3).
            int zoneCap = int.MaxValue;
            int combatCount = 4;
            if (def != null && def.roomCountByRank != null)
                combatCount = Mathf.RoundToInt(def.roomCountByRank.Evaluate(rankIndex) * roomCountFactor
                                               * DungeonTunables.RoomCountMultiplier);
            combatCount = Mathf.Clamp(combatCount, 1, 16);

            // OPEN zones are 2-4x the width of a room, but roomCountByRank was
            // tuned for rooms. Packing that many huge zones onto the entry->boss
            // segment makes placement fail on nearly every high-rank generation
            // (each failure = a dropped encounter + a Log.Warning). Derive the
            // ceiling from geometry instead: how many zone diameters actually
            // fit along the chain, leaving the mandated gap between them.
            if (openMode)
            {
                // AREA, not chain length. The old estimate divided the straight
                // entry-to-boss DISTANCE by the zone pitch, which counts only the
                // zones that fit on a single line — but zones are jittered off
                // that line by PathJitterFrac and wings push further still, so a
                // field is two-dimensional and always held far more than the 1-D
                // formula admitted. It was capping a Titan Labyrinth at 7-8 of
                // its 13 designed zones, on an archetype whose whole identity is
                // zone COUNT, and logging the loss as if it were normal.
                float usable = mapSize - 2 * inset;
                float avgEdge = (minEdge + maxEdge) * 0.5f;
                float pitch = avgEdge + RoomGap;
                int fits = Mathf.FloorToInt(usable * usable * DungeonTunables.OpenPackingEfficiency / (pitch * pitch));
                zoneCap = Mathf.Max(3, fits);
                int capped = Mathf.Clamp(combatCount, 1, zoneCap);
                if (capped < combatCount)
                    Log.Message($"{IsekaiDungeonsMod.LogPrefix} Open dungeon: {combatCount} zones rolled, {capped} fit the field.");
                combatCount = capped;
            }

            List<CellRect> combatRects = new List<CellRect>();
            PlaceChainRooms(entryRect, bossRect, combatCount, sizeRange, interior, placed, mapSize, inset, combatRects);

            // 2b. AUTHOR THE SPINE'S DEPTHS.
            //
            // Depth used to be inferred downstream from a room's POSITION IN THE
            // LIST (GenStep_DungeonPopulation's `i / (n - 1)`). That is only
            // depth while the layout is a straight chain; the moment anything
            // branches, a room in a side alcove near the entrance would inherit
            // whatever depth its list slot happened to imply. So the planner —
            // the only party that knows the geometry — writes it down.
            //
            // For a chain, this is character-for-character the expression it
            // replaces, which is what makes the change safe: an F-rank dungeon
            // with no wings and no loops gets byte-identical numbers.
            int spineCount = combatRects.Count;
            float spineStep = spineCount <= 1 ? 1f : 1f / (spineCount - 1);
            var combatDepth = new List<float>(spineCount);
            for (int j = 0; j < spineCount; j++)
                combatDepth.Add(spineCount <= 1 ? 1f : j / (float)(spineCount - 1));

            // OPEN fields are the exception. PlaceChainRooms walks its targets in
            // order but every zone is jittered off the axis and re-placed on
            // failure, so on a wide field the LIST order and the walking order
            // genuinely diverge. Project each placed zone onto the entry->boss
            // axis instead and use that: it is what the player actually crosses.
            if (openMode && spineCount > 1)
            {
                IntVec3 ea = entryRect.CenterCell;
                IntVec3 eb = bossRect.CenterCell;
                float axX = eb.x - ea.x, axZ = eb.z - ea.z;
                float axLen2 = axX * axX + axZ * axZ;
                if (axLen2 > 1f)
                {
                    for (int j = 0; j < spineCount; j++)
                    {
                        IntVec3 c = combatRects[j].CenterCell;
                        float t = ((c.x - ea.x) * axX + (c.z - ea.z) * axZ) / axLen2;
                        combatDepth[j] = Mathf.Clamp(t, 0.05f, 0.95f);
                    }
                }
            }

            var combatOnPath = new List<bool>(spineCount);
            var combatTierOffset = new List<int>(spineCount);
            for (int j = 0; j < spineCount; j++)
            {
                combatOnPath.Add(true);
                combatTierOffset.Add(0);
            }

            // 3. Corridors along the chain: entry → combat… → boss. Built
            //    AFTER all main-chain rooms exist so orientation scoring can
            //    see every room it might accidentally slice through.
            //    placed indices: 0 = entry, 1 = boss, 2.. = combat in order.
            List<int> chainIndices = new List<int>();
            chainIndices.Add(0);
            for (int i = 0; i < combatRects.Count; i++)
                chainIndices.Add(2 + i);
            chainIndices.Add(1);
            // OPEN dungeons have no corridors at all — the whole map is walkable
            // ground, so linking zones would be meaningless (and carving them
            // would put paths on a field the player is supposed to cross freely).
            if (!openMode)
            {
                for (int i = 0; i < chainIndices.Count - 1; i++)
                    AddLCorridor(placed, chainIndices[i], chainIndices[i + 1], corridorWidth, mapSize);

                // 3b. SHORTCUTS — the loops that make a high-rank dungeon a GRAPH
                //     rather than a longer string. Purely additive: every one is
                //     an extra edge between two rooms that are already connected,
                //     so it cannot disconnect anything.
                AddShortcuts(placed, chainIndices, corridorWidth, mapSize, rank);
            }

            // 4. WINGS — optional branches off the spine. A wing of length 1 is
            //    exactly the old treasure stub; longer wings are a side route
            //    with their own fights, ending in a guarded prize.
            List<CellRect> treasureRects = new List<CellRect>();
            var treasureDepth = new List<float>();
            var treasureLootScale = new List<float>();
            var treasureTierOffset = new List<int>();
            // The treasure-rooms setting shifts a def's own roll. An archetype's
            // explicit override is a design statement about that floor and is
            // left exactly as authored.
            int wingCount = treasureRoomsOverride >= 0
                ? treasureRoomsOverride
                : Mathf.Max(0, (def != null ? def.treasureRoomCountRange : new IntRange(0, 2)).RandomInRange
                               + DungeonTunables.TreasureRoomsShift);
            if (wingCount > 0 && combatRects.Count > 0)
            {
                PlaceWings(wingCount, spineCount, spineStep, combatRects, combatDepth, combatOnPath,
                           combatTierOffset, sizeRange, interior, placed, mapSize, corridorWidth,
                           openMode, rank, treasureRects, treasureDepth, treasureLootScale, treasureTierOffset);
            }

            // 5. Assemble the room list in the frozen order: Entry, Combat…,
            //    Treasure…, Boss. Indices sequential — downstream consumers
            //    (population ramp, aggro sweep) rely on this ordering, and the
            //    order survives branching because list order is ROLE order and
            //    has never meant anything else. Depth now travels as data.
            int idx = 0;
            rooms.Add(NewRoom(entryRect, idx++, DungeonRoomRole.Entry, 0f, true, 0, 1f));
            for (int i = 0; i < combatRects.Count; i++)
                rooms.Add(NewRoom(combatRects[i], idx++, DungeonRoomRole.Combat,
                                  combatDepth[i], combatOnPath[i], combatTierOffset[i], 1f));
            for (int i = 0; i < treasureRects.Count; i++)
                rooms.Add(NewRoom(treasureRects[i], idx++, DungeonRoomRole.Treasure,
                                  treasureDepth[i], false, treasureTierOffset[i], treasureLootScale[i]));
            rooms.Add(NewRoom(bossRect, idx++, DungeonRoomRole.Boss, 1f, true, 0, 1f));

            // ONE structured line per generation, rather than a warning per
            // truncation. This is how you find out that a feature quietly is not
            // happening on real maps.
            if (IsekaiDungeonsSettings.Current.logGenerationSummaries)
                Log.Message($"{IsekaiDungeonsMod.LogPrefix} Layout {def?.defName ?? "?"} @{rank}: " +
                            $"spine {spineCount}, wings {wingRoomsPlaced} rooms (longest {longestWing}), " +
                            $"loops {shortcutsPlaced}/{shortcutsBudgeted}, rooms {rooms.Count}, map {mapSize}.");
        }

        private static DungeonRoom NewRoom(CellRect rect, int index, DungeonRoomRole role,
                                           float depth, bool onPath, int tierOffset, float lootScale)
        {
            return new DungeonRoom(rect, index, role)
            {
                depthFrac = depth,
                depthAuthored = true,
                onCriticalPath = onPath,
                tierOffset = tierOffset,
                lootScale = lootScale,
            };
        }

        /// <summary>
        /// Rolls the entry edge and derives the boss corner on the opposite
        /// side. Entry sits somewhere along the middle half of its edge (not
        /// in a corner — the boss owns a corner) so the two anchors are always
        /// far apart regardless of the rolls.
        /// </summary>
        private static void PickAnchors(int mapSize, int inset, out IntVec3 entryTarget, out IntVec3 bossTarget)
        {
            int lo = inset;
            int hi = mapSize - 1 - inset;
            int along = Mathf.Clamp(Mathf.RoundToInt(Rand.Range(0.3f, 0.7f) * mapSize), lo, hi);
            bool cornerFlip = Rand.Bool; // which of the two opposite corners the boss takes

            switch (Rand.RangeInclusive(0, 3))
            {
                case 0: // entry south → boss north corner
                    entryTarget = new IntVec3(along, 0, lo);
                    bossTarget = new IntVec3(cornerFlip ? lo : hi, 0, hi);
                    break;
                case 1: // entry north → boss south corner
                    entryTarget = new IntVec3(along, 0, hi);
                    bossTarget = new IntVec3(cornerFlip ? lo : hi, 0, lo);
                    break;
                case 2: // entry west → boss east corner
                    entryTarget = new IntVec3(lo, 0, along);
                    bossTarget = new IntVec3(hi, 0, cornerFlip ? lo : hi);
                    break;
                default: // entry east → boss west corner
                    entryTarget = new IntVec3(hi, 0, along);
                    bossTarget = new IntVec3(lo, 0, cornerFlip ? lo : hi);
                    break;
            }
        }

        /// <summary>
        /// Places the combat rooms on jittered points along the entry→boss
        /// segment. A room that fails even relaxed placement is SKIPPED with a
        /// warning (G5: logged, never silent) — a shorter dungeon still plays;
        /// a broken plan does not.
        /// </summary>
        private static void PlaceChainRooms(CellRect entryRect, CellRect bossRect, int count,
            IntRange sizeRange, CellRect interior, List<CellRect> placed, int mapSize, int inset,
            List<CellRect> outRects)
        {
            IntVec3 a = entryRect.CenterCell;
            IntVec3 b = bossRect.CenterCell;

            // Unit vectors along and perpendicular to the chain, in plain
            // float math (no Vector3 — keeps the dependency surface tiny).
            float dx = b.x - a.x;
            float dz = b.z - a.z;
            float len = Mathf.Sqrt(dx * dx + dz * dz);
            if (len < 1f)
                len = 1f; // degenerate anchors — jitter still spreads the rooms
            float ux = dx / len, uz = dz / len;   // along-path
            float vx = -uz, vz = ux;              // perpendicular

            float perpJitter = mapSize * PathJitterFrac;
            float alongJitter = mapSize * AlongJitterFrac;

            for (int i = 1; i <= count; i++)
            {
                float t = i / (float)(count + 1);
                float px = Mathf.Lerp(a.x, b.x, t);
                float pz = Mathf.Lerp(a.z, b.z, t);
                float off = Rand.Range(-perpJitter, perpJitter);
                float slide = Rand.Range(-alongJitter, alongJitter);
                int tx = Mathf.Clamp(Mathf.RoundToInt(px + vx * off + ux * slide), inset, mapSize - 1 - inset);
                int tz = Mathf.Clamp(Mathf.RoundToInt(pz + vz * off + uz * slide), inset, mapSize - 1 - inset);

                if (TryPlaceRoom(new IntVec3(tx, 0, tz), sizeRange, interior, placed, mapSize, out CellRect roomRect))
                {
                    outRects.Add(roomRect);
                    placed.Add(roomRect);
                }
                else
                {
                    Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Dungeon layout: could not place combat room {i}/{count} even with relaxed spacing — continuing with a shorter chain.");
                }
            }
        }

        /// <summary>
        /// Hangs OPTIONAL BRANCHES off the spine.
        ///
        /// A wing of length 1 is exactly the treasure stub this used to place —
        /// which is why F and E rank, whose table value is 1, generate the same
        /// dungeons they always did. Longer wings are a genuine side route: two
        /// to four rooms with their own fights, a tougher guard on the last of
        /// them, and a fatter prize at the end. The player can walk past all of
        /// it.
        ///
        /// Each hop tries the eight compass directions, but after the first it
        /// prefers to keep heading OUTWARD (see WingDirection) so a wing reads
        /// as a passage leading away from the spine rather than folding back
        /// over it.
        ///
        /// A wing that runs out of room simply stops: the last room it managed
        /// to place becomes the terminal. Failing to place the FIRST room is the
        /// old "no space for treasure room" case and is still only a warning.
        /// </summary>
        private void PlaceWings(int wingCount, int spineCount, float spineStep,
            List<CellRect> combatRects, List<float> combatDepth, List<bool> combatOnPath,
            List<int> combatTierOffset, IntRange sizeRange, CellRect interior, List<CellRect> placed,
            int mapSize, int corridorWidth, bool openMode, MobRankTier rank,
            List<CellRect> treasureRects, List<float> treasureDepth, List<float> treasureLootScale,
            List<int> treasureTierOffset)
        {
            // Diagonals aren't normalized on purpose: a slightly longer diagonal
            // offset reads better next to rectangular rooms than an exact ring.
            int[] dirX = { 1, 1, 0, -1, -1, -1, 0, 1 };
            int[] dirZ = { 0, 1, 1, 1, 0, -1, -1, -1 };

            int maxLen = DungeonTunables.MaxWingLengthFor(rank);

            var wingRects = new List<CellRect>();

            for (int w = 0; w < wingCount; w++)
            {
                if (wingRoomsPlaced >= DungeonTunables.MaxWingRoomsPerDungeon)
                    break;

                int anchorLocal = PickWingAnchor(spineCount);
                if (anchorLocal < 0)
                    break;

                int wingLen = maxLen <= 1 ? 1 : Rand.RangeInclusive(1, maxLen);
                wingLen = Mathf.Min(wingLen, DungeonTunables.MaxWingRoomsPerDungeon - wingRoomsPlaced);
                if (wingLen <= 0)
                    break;

                CellRect prevRect = combatRects[anchorLocal];
                // placed indices: 0 = entry, 1 = boss, 2+i = SPINE combat room i.
                // Wing rooms are appended to `placed` as they are built, so this
                // mapping only has to hold for anchors — and anchors are always
                // spine rooms, which were all placed before any wing room.
                int prevPlacedIdx = 2 + anchorLocal;
                float anchorDepth = combatDepth[anchorLocal];
                int heading = -1;

                wingRects.Clear();
                for (int hop = 1; hop <= wingLen; hop++)
                {
                    int anchorHalf = Mathf.Max(prevRect.Width, prevRect.Height) / 2;
                    int roomHalf = sizeRange.max / 2;
                    int dist = anchorHalf + RoomGap + roomHalf + 2;

                    bool placedThis = false;
                    int startDir = Rand.RangeInclusive(0, 7);
                    for (int d = 0; d < 8 && !placedThis; d++)
                    {
                        int dir = heading >= 0 ? WingDirection(heading, d) : (startDir + d) % 8;
                        IntVec3 target = new IntVec3(
                            prevRect.CenterCell.x + dirX[dir] * dist, 0,
                            prevRect.CenterCell.z + dirZ[dir] * dist);

                        if (!TryPlaceRoom(target, sizeRange, interior, placed, mapSize, out CellRect roomRect))
                            continue;

                        // A wing room touching an existing corridor would open an
                        // unplanned side entrance, so reject the direction and try
                        // the next one. Open fields carve nothing, so there are no
                        // corridors to clip.
                        if (!openMode && RoomClipsCorridors(roomRect))
                            continue;

                        placed.Add(roomRect);
                        int newPlacedIdx = placed.Count - 1;
                        // OPEN dungeons have no corridors at all. The old code
                        // called AddLCorridor here unconditionally: nothing carved
                        // those legs, but RoomClipsCorridors then treated them as
                        // hard exclusion bands, so on an unwalled field every
                        // treasure zone after the first fought phantom geometry.
                        if (!openMode)
                            AddLCorridor(placed, prevPlacedIdx, newPlacedIdx, corridorWidth, mapSize);

                        wingRects.Add(roomRect);
                        prevRect = roomRect;
                        prevPlacedIdx = newPlacedIdx;
                        heading = dir;
                        wingRoomsPlaced++;
                        placedThis = true;
                    }

                    if (!placedThis)
                        break;   // the map ran out; the wing ends here
                }

                if (wingRects.Count == 0)
                {
                    Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Dungeon layout: no space for wing {w + 1}/{wingCount} — skipped.");
                    continue;
                }

                longestWing = Mathf.Max(longestWing, wingRects.Count);

                // Roles: every room but the last is an optional fight, the last
                // is the prize. Depth grows a full spine step per hop, so a wing
                // off a mid-spine room genuinely escalates along its own length
                // instead of sitting in one difficulty bucket.
                for (int k = 0; k < wingRects.Count; k++)
                {
                    float depth = Mathf.Min(
                        anchorDepth + (k + 1) * spineStep * DungeonTunables.WingDepthStepFrac, 0.95f);
                    bool terminal = k == wingRects.Count - 1;

                    if (terminal)
                    {
                        treasureRects.Add(wingRects[k]);
                        treasureDepth.Add(depth);
                        // Sub-linear: a length-4 wing pays about twice a stub,
                        // not four times it. Paying a full roll per room put the
                        // majority of a Nation dungeon's value in a dead end.
                        treasureLootScale.Add(1f + DungeonTunables.WingLootBonusPerRoom * (wingRects.Count - 1));
                        treasureTierOffset.Add(0);
                    }
                    else
                    {
                        combatRects.Add(wingRects[k]);
                        combatDepth.Add(depth);
                        combatOnPath.Add(false);
                        // The room guarding the prize is the wing's champion.
                        // Applied inside the existing tier clamp downstream, so
                        // it can never exceed the gate's advertised rank.
                        combatTierOffset.Add(k == wingRects.Count - 2 ? 1 : 0);
                    }
                }
            }
        }

        /// <summary>
        /// Which spine room a wing hangs off. Biased to the middle: a branch off
        /// the first room is a detour before the dungeon has started, and one off
        /// the last competes with the boss for attention.
        /// </summary>
        private static int PickWingAnchor(int spineCount)
        {
            if (spineCount <= 0)
                return -1;
            if (spineCount <= 2)
                return Rand.Range(0, spineCount);
            int lo = Mathf.Max(0, Mathf.RoundToInt(spineCount * 0.25f));
            int hi = Mathf.Min(spineCount - 1, Mathf.RoundToInt(spineCount * 0.75f));
            if (hi < lo)
            {
                lo = 0;
                hi = spineCount - 1;
            }
            return Rand.RangeInclusive(lo, hi);
        }

        /// <summary>Compass preference order for wing hop d: straight on, then widening turns.</summary>
        private static readonly int[] WingTurnOrder = { 0, 1, -1, 2, -2, 3, -3, 4 };

        private static int WingDirection(int heading, int d)
        {
            int turn = WingTurnOrder[Mathf.Clamp(d, 0, WingTurnOrder.Length - 1)];
            return ((heading + turn) % 8 + 8) % 8;
        }

        // ── Shortcuts ───────────────────────────────────────────────────────

        /// <summary>
        /// Adds extra corridors between NON-ADJACENT spine rooms, turning the
        /// path into a graph with real loops: an alternative route, a flank, and
        /// somewhere to fall back to.
        ///
        /// Purely additive, and that is the whole safety argument — every
        /// shortcut is an extra edge between two rooms that were already
        /// connected by the spine, so no shortcut can ever disconnect anything
        /// and there is no repair path to get wrong.
        ///
        /// SPANS ARE KEPT DISJOINT. Without that, N shortcuts chain: at Nation,
        /// four links of skip-3 could compose into one route bypassing five of
        /// twelve rooms, and a player could walk to the boss past most of the
        /// dungeon. Disjoint spans bound the total bypass to the sum of the
        /// individual skips, drawn from non-overlapping stretches.
        /// </summary>
        private void AddShortcuts(List<CellRect> placed, List<int> chainIndices, int corridorWidth,
            int mapSize, MobRankTier rank)
        {
            int budget = DungeonTunables.CrossLinkBudgetFor(rank);
            shortcutsBudgeted = budget;
            if (budget <= 0 || chainIndices.Count < 4)
                return;

            int maxSkip = (int)rank >= (int)MobRankTier.S
                ? DungeonTunables.MaxShortcutSkipHighRank
                : DungeonTunables.MaxShortcutSkip;

            var fromPos = new List<int>();
            var toPos = new List<int>();
            for (int i = 0; i < chainIndices.Count; i++)
            {
                for (int skip = 2; skip <= maxSkip && i + skip < chainIndices.Count; skip++)
                {
                    fromPos.Add(i);
                    toPos.Add(i + skip);
                }
            }
            if (fromPos.Count == 0)
                return;

            var order = new List<int>(fromPos.Count);
            for (int i = 0; i < fromPos.Count; i++)
                order.Add(i);
            order.Shuffle();

            var consumed = new bool[chainIndices.Count];
            int made = 0;
            for (int oi = 0; oi < order.Count && made < budget; oi++)
            {
                int c = order[oi];
                int i = fromPos[c], j = toPos[c];

                bool overlaps = false;
                for (int k = i; k <= j && !overlaps; k++)
                {
                    if (consumed[k])
                        overlaps = true;
                }
                if (overlaps)
                    continue;

                if (!TryAddOptionalCorridor(placed, chainIndices[i], chainIndices[j], corridorWidth, mapSize))
                    continue;

                for (int k = i; k <= j; k++)
                    consumed[k] = true;
                made++;
            }
            shortcutsPlaced = made;
        }

        /// <summary>
        /// An L-corridor that REFUSES to be dirty. Where AddLCorridor takes the
        /// lesser evil because the dungeon must stay connected, an optional
        /// corridor has no such obligation: if neither bend is clean it simply
        /// is not built.
        ///
        /// Cleanliness is the existing CorridorCollisions predicate — the same
        /// one the spine is scored with, which already treats running FLUSH
        /// along an unrelated room as a collision — plus a privacy test that
        /// keeps every corridor a two-endpoint link. Without the privacy test a
        /// shortcut could merge into a spine corridor mid-run and produce a
        /// three-way junction in open rock, which the door placer has no concept
        /// of and would leave permanently open.
        /// </summary>
        private bool TryAddOptionalCorridor(List<CellRect> allRooms, int fromIdx, int toIdx,
            int width, int mapSize)
        {
            IntVec3 a = allRooms[fromIdx].CenterCell;
            IntVec3 b = allRooms[toIdx].CenterCell;

            CellRect h1 = HorizontalLeg(a.x, b.x, a.z, width);
            CellRect v1 = VerticalLeg(a.z, b.z, b.x, width);
            CellRect v2 = VerticalLeg(a.z, b.z, a.x, width);
            CellRect h2 = HorizontalLeg(a.x, b.x, b.z, width);

            bool preferFirst = Rand.Bool;
            for (int t = 0; t < 2; t++)
            {
                bool useFirst = t == 0 ? preferFirst : !preferFirst;
                CellRect legA = useFirst ? h1 : v2;
                CellRect legB = useFirst ? v1 : h2;

                if (CorridorCollisions(legA, legB, allRooms, fromIdx, toIdx) != 0)
                    continue;
                if (!LegsArePrivate(legA, legB, allRooms[fromIdx], allRooms[toIdx]))
                    continue;

                AddLeg(legA, mapSize);
                AddLeg(legB, mapSize);
                return true;
            }
            return false;
        }

        /// <summary>
        /// True when neither leg shares ground with an existing corridor except
        /// inside one of its own two endpoint rooms — i.e. no corridor forks.
        /// </summary>
        private bool LegsArePrivate(CellRect legA, CellRect legB, CellRect endA, CellRect endB)
        {
            for (int i = 0; i < corridors.Count; i++)
            {
                if (!Private(legA, corridors[i], endA, endB))
                    return false;
                if (!Private(legB, corridors[i], endA, endB))
                    return false;
            }
            return true;
        }

        private static bool Private(CellRect leg, CellRect other, CellRect endA, CellRect endB)
        {
            if (!leg.Overlaps(other))
                return true;
            CellRect shared = CellRect.FromLimits(
                Mathf.Max(leg.minX, other.minX), Mathf.Max(leg.minZ, other.minZ),
                Mathf.Min(leg.maxX, other.maxX), Mathf.Min(leg.maxZ, other.maxZ));
            return shared.FullyContainedWithin(endA) || shared.FullyContainedWithin(endB);
        }

        // ── Room placement ──────────────────────────────────────────────────

        /// <summary>
        /// Three-phase placement around a target point:
        ///   phase 0 — rolled size, full 3-cell gap, growing jitter;
        ///   phase 1 — minimum size, full gap (shrink before crowding);
        ///   phase 2 — minimum size, relaxed 2-cell gap (logged, G5).
        /// Returns false only when all phases are exhausted.
        /// </summary>
        private static bool TryPlaceRoom(IntVec3 target, IntRange sizeRange, CellRect interior,
            List<CellRect> placed, int mapSize, out CellRect rect)
        {
            for (int phase = 0; phase <= 2; phase++)
            {
                int gap = phase == 2 ? RelaxedRoomGap : RoomGap;
                for (int attempt = 0; attempt < TriesPerPhase; attempt++)
                {
                    int w = phase == 0 ? sizeRange.RandomInRange : sizeRange.min;
                    int h = phase == 0 ? sizeRange.RandomInRange : sizeRange.min;

                    // Jitter ramps from 0 (exactly on target) to a map-scaled
                    // radius, plus extra reach per relaxation phase — later
                    // attempts search wider instead of hammering the same spot.
                    float jitter = mapSize * 0.12f * (attempt / (float)TriesPerPhase)
                                 + phase * mapSize * 0.05f;
                    int cx = target.x + Mathf.RoundToInt(Rand.Range(-jitter, jitter));
                    int cz = target.z + Mathf.RoundToInt(Rand.Range(-jitter, jitter));

                    CellRect candidate = new CellRect(cx - w / 2, cz - h / 2, w, h);
                    if (!candidate.FullyContainedWithin(interior))
                        continue;
                    if (OverlapsAny(candidate, placed, gap))
                        continue;

                    if (phase == 2)
                        Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Dungeon layout: room placed with relaxed {RelaxedRoomGap}-cell spacing near {target} (map crowded).");
                    rect = candidate;
                    return true;
                }
            }
            rect = default;
            return false;
        }

        /// <summary>
        /// Entry/boss placement: same three phases, then a deterministic grid
        /// scan of the whole interior, then — as an absolute last resort — a
        /// clamped rect at the target that may overlap. The plan is never
        /// returned without its mandatory rooms; every downgrade logs (G5).
        /// </summary>
        private static CellRect PlaceMandatoryRoom(IntVec3 target, IntRange sizeRange, CellRect interior,
            List<CellRect> placed, int mapSize, string label)
        {
            if (TryPlaceRoom(target, sizeRange, interior, placed, mapSize, out CellRect rect))
                return rect;

            Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Dungeon layout: random placement failed for the {label} room — falling back to a grid scan.");

            int w = sizeRange.min;
            int h = sizeRange.min;
            // Stride 3 keeps the scan cheap (~50x50 candidates worst case)
            // while still visiting every neighbourhood of the interior.
            for (int z = interior.minZ; z + h - 1 <= interior.maxZ; z += 3)
            {
                for (int x = interior.minX; x + w - 1 <= interior.maxX; x += 3)
                {
                    CellRect candidate = new CellRect(x, z, w, h);
                    if (!OverlapsAny(candidate, placed, RelaxedRoomGap))
                        return candidate;
                }
            }

            // Unreachable in practice (a 150+ map cannot be this full), but
            // G5 demands a loud non-null answer, not a silent broken plan.
            Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Dungeon layout: grid scan also failed for the {label} room — placing it clamped at {target}, overlaps possible.");
            int minX = Mathf.Clamp(target.x - w / 2, interior.minX, interior.maxX - w + 1);
            int minZ = Mathf.Clamp(target.z - h / 2, interior.minZ, interior.maxZ - h + 1);
            return new CellRect(minX, minZ, w, h);
        }

        /// <summary>True if the room (grown by one wall cell) touches an existing corridor leg.</summary>
        private bool RoomClipsCorridors(CellRect roomRect)
        {
            CellRect grown = roomRect.ExpandedBy(1);
            for (int i = 0; i < corridors.Count; i++)
            {
                if (grown.Overlaps(corridors[i]))
                    return true;
            }
            return false;
        }

        /// <summary>True if the candidate (grown by the required rock gap) touches any placed room.</summary>
        private static bool OverlapsAny(CellRect candidate, List<CellRect> placed, int gap)
        {
            // ExpandedBy(gap) overlapping an existing room means the rock wall
            // between them would be thinner than 'gap' cells.
            CellRect grown = candidate.ExpandedBy(gap);
            for (int i = 0; i < placed.Count; i++)
            {
                if (grown.Overlaps(placed[i]))
                    return true;
            }
            return false;
        }

        // ── Corridors ───────────────────────────────────────────────────────

        /// <summary>
        /// Adds an L-shaped corridor between two rooms (by index into the
        /// placed-rooms list). Both bend orientations are scored by how many
        /// UNRELATED rooms they would slice through; the cleaner one wins so
        /// pawns can't skip rooms through an accidental shortcut. A tie is
        /// broken randomly for visual variety. If both orientations collide
        /// (very crowded map) the lesser evil is taken and logged — a shortcut
        /// is survivable, a disconnected dungeon is not.
        /// </summary>
        private void AddLCorridor(List<CellRect> allRooms, int fromIdx, int toIdx, int width, int mapSize)
        {
            IntVec3 a = allRooms[fromIdx].CenterCell;
            IntVec3 b = allRooms[toIdx].CenterCell;

            // Orientation 1: horizontal first, bend at (b.x, a.z).
            CellRect h1 = HorizontalLeg(a.x, b.x, a.z, width);
            CellRect v1 = VerticalLeg(a.z, b.z, b.x, width);
            // Orientation 2: vertical first, bend at (a.x, b.z).
            CellRect v2 = VerticalLeg(a.z, b.z, a.x, width);
            CellRect h2 = HorizontalLeg(a.x, b.x, b.z, width);

            int score1 = CorridorCollisions(h1, v1, allRooms, fromIdx, toIdx);
            int score2 = CorridorCollisions(v2, h2, allRooms, fromIdx, toIdx);

            bool useFirst = score1 < score2 || (score1 == score2 && Rand.Bool);
            int chosenScore = useFirst ? score1 : score2;
            if (chosenScore > 0)
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Dungeon layout: corridor {fromIdx}→{toIdx} clips {chosenScore} unrelated room(s); both bends collide on this crowded layout.");

            if (useFirst)
            {
                AddLeg(h1, mapSize);
                AddLeg(v1, mapSize);
            }
            else
            {
                AddLeg(v2, mapSize);
                AddLeg(h2, mapSize);
            }
        }

        /// <summary>Number of rooms other than the two endpoints that either leg cuts through.</summary>
        private static int CorridorCollisions(CellRect legA, CellRect legB, List<CellRect> allRooms, int fromIdx, int toIdx)
        {
            int collisions = 0;
            for (int i = 0; i < allRooms.Count; i++)
            {
                if (i == fromIdx || i == toIdx)
                    continue;
                // ExpandedBy(1): a corridor running FLUSH along an unrelated
                // room is as bad as one cutting through it. Their shared
                // boundary gets carved away wholesale, merging the two spaces,
                // and the room's whole edge then reads as one enormous doorway
                // (the long lines of doors seen in early playtests). Counting
                // near-misses as collisions makes the cleaner bend win.
                CellRect keepClear = allRooms[i].ExpandedBy(1);
                if (legA.Overlaps(keepClear) || legB.Overlaps(keepClear))
                    collisions++;
            }
            return collisions;
        }

        /// <summary>
        /// Horizontal corridor leg between two x coordinates at row z. The
        /// long axis is extended by the half-width on both ends so the two
        /// legs of an L fully cover their shared bend block regardless of
        /// travel direction — no missing corner cell at odd widths. The
        /// spill-over on the far ends lands inside the endpoint rooms, which
        /// are carved anyway.
        /// </summary>
        private static CellRect HorizontalLeg(int x0, int x1, int z, int width)
        {
            int lo = Mathf.Min(x0, x1) - (width - 1) / 2;
            int hi = Mathf.Max(x0, x1) + width / 2;
            return new CellRect(lo, z - (width - 1) / 2, hi - lo + 1, width);
        }

        /// <summary>Vertical twin of <see cref="HorizontalLeg"/>.</summary>
        private static CellRect VerticalLeg(int z0, int z1, int x, int width)
        {
            int lo = Mathf.Min(z0, z1) - (width - 1) / 2;
            int hi = Mathf.Max(z0, z1) + width / 2;
            return new CellRect(x - (width - 1) / 2, lo, width, hi - lo + 1);
        }

        /// <summary>
        /// Clamps a leg to one cell inside the map bounds and stores it.
        /// Room placement already guarantees the margin, so the clamp only
        /// ever bites on the belt-and-braces path (degenerate tiny maps).
        /// </summary>
        private void AddLeg(CellRect leg, int mapSize)
        {
            int minX = Mathf.Max(leg.minX, 1);
            int minZ = Mathf.Max(leg.minZ, 1);
            int maxX = Mathf.Min(leg.maxX, mapSize - 2);
            int maxZ = Mathf.Min(leg.maxZ, mapSize - 2);
            if (maxX < minX || maxZ < minZ)
                return; // fully outside the safe area — nothing to carve
            corridors.Add(CellRect.FromLimits(minX, minZ, maxX, maxZ));
        }
    }
}
