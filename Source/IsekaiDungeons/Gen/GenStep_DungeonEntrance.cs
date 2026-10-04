using System.Collections.Generic;
using IsekaiLeveling.MobRanking;
using RimWorld;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Spawns the return portal (the dungeon exit) in the entry room and sets
    /// the player start spot next to it.
    ///
    /// Modeled on core GenStep_PlaceCaveExit but deterministic: the cave exit
    /// picks a random standable cell, while our exit lands on the entry room's
    /// center — the room the layout step carved specifically for it (XML order
    /// 420, right after IsekaiDungeons_Layout).
    ///
    /// "On the center" means the whole 3x3 and a walkable ring round it, found
    /// by FootprintSpot. A carved room is empty at this point, so there it is
    /// always the exact center, as before. An Open zone is untouched field,
    /// and its center can be a ruin wall, a boulder, a tree or water; the exit
    /// then moves to the nearest spot it fits, and only when there is none
    /// does ClearArea make room by force.
    ///
    /// Wiring notes:
    ///  - the exit building's own SpawnSetup (vanilla PocketMapExit) grabs
    ///    PocketMapUtility.currentlyGeneratingPortal and wires entrance↔exit;
    ///    nothing to do here for the return trip;
    ///  - DungeonMapComponent.MapGenerated() resolves the exit from
    ///    listerThings as a fallback, but we also assign comp.exit directly
    ///    (belt and braces — the component is constructed before GenSteps run);
    ///  - MapPortal.GetDestinationLocation() returns exit.Position, so pawns
    ///    entering the gate arrive at the exit; PlayerStartSpot covers every
    ///    other arrival path (dev spawn, scenario logic).
    ///
    /// Seeding: the happy path uses no randomness at all. The degenerate
    /// fallbacks draw from CellFinder, which runs inside the seeded per-step
    /// Rand state vanilla's MapGenerator pushes around Generate — still
    /// deterministic per map, no manual PushState needed.
    /// </summary>
    public class GenStep_DungeonEntrance : GenStep
    {
        /// <summary>Clear radius around the exit spawn cell. The exit is (3,3), so radius 3
        /// covers its footprint plus one ring of breathing room, mirroring the
        /// vanilla cave-exit clear (theirs is 4.5 for a bigger building).</summary>
        private const float ClearRadius = 3f;

        /// <summary>How far from the entry room's center FootprintSpot looks for a spot the exit fits.</summary>
        private const float FitRadius = 8f;

        /// <summary>Arbitrary unique constant — must differ from every other GenStep's SeedPart.</summary>
        public override int SeedPart => 823471217;

        /// <summary>Snapshot buffer — GetThingList returns the live grid list and Destroy()
        /// mutates it. Single-threaded generation makes a static scratch list safe.</summary>
        private static readonly List<Thing> tmpThings = new List<Thing>();

        public override void Generate(Map map, GenStepParams parms)
        {
            DungeonMapComponent comp = map.GetComponent<DungeonMapComponent>();
            IntVec3 center = ResolveEntryCenter(map, comp, out CellRect entryRoom);

            // WHICH EXIT depends on what generated this map.
            //
            // A dungeon floor's exit is a PocketMapExit: it binds to the gate
            // that opened it, and PocketMapExit.SpawnSetup ERRORS OUT if there
            // is no portal to bind to. A tower floor has no gate at all — the
            // party arrived by caravan — so spawning one there is guaranteed to
            // fail, and it cannot even be cleaned up afterwards because
            // PocketMapExit is destroyable=false.
            //
            // So the tower gets its own exit, which leads back to the world
            // rather than to a building.
            // Multi-floor dungeons are the same situation as the tower: no
            // gate building to walk back into, so they need the world-facing
            // exit rather than the gate-bound one.
            ThingDef exitDef;
            if (map.Parent is MultiFloorMapParent)
            {
                // Its own building: the same leave-as-a-caravan behaviour as the
                // tower's, in the dungeon's own stone, pointing the right way.
                exitDef = IsekaiDungeonsDefOf.IsekaiDungeons_MultiFloorExit;
            }
            else if (map.Parent is TowerFloorMapParent)
            {
                exitDef = IsekaiDungeonsDefOf.IsekaiDungeons_TowerExit;
            }
            else
            {
                // The gate's own portal props name the exit def; fall back to
                // our shipped def when generating without a gate (dev tools, G5).
                Building_DungeonGate gate = PocketMapUtility.currentlyGeneratingPortal as Building_DungeonGate;
                exitDef = gate?.def?.portal?.exitDef ?? IsekaiDungeonsDefOf.IsekaiDungeons_DungeonExit;
            }

            // Fit the WHOLE footprint, not just the center cell. When nothing
            // fits, the room center stands and ClearArea clears it by force.
            // Never outside the room: GenStep_DungeonDecor finds the way out by
            // scanning room rects, and keeps its ring clear only if it is there.
            if (entryRoom.Area > 0
                && FootprintSpot.TryFind(map, exitDef, FootprintSpot.CenteredRoot(entryRoom, exitDef.size),
                                         entryRoom, FitRadius, out IntVec3 fitted, out int _,
                                         allowOutsideRoom: false))
                center = fitted;

            ClearArea(map, center);

            Thing exitThing = GenSpawn.Spawn(ThingMaker.MakeThing(exitDef), center, map);

            // Belt and braces: hand the exit to the component right now.
            // MapGenerated()'s listerThings scan still covers foreign exitDefs
            // that don't subclass Building_DungeonExit.
            if (comp != null && exitThing is Building_DungeonExit dungeonExit)
                comp.exit = dungeonExit;

            MapGenerator.PlayerStartSpot = FindStartSpot(map, exitThing, center);
        }

        // ── Entry room resolution ───────────────────────────────────────────

        /// <summary>
        /// The entry room's center, from the layout the previous step
        /// registered. Layered fallbacks (each logged, G5):
        ///   1. the room flagged Entry (contract: always index 0);
        ///   2. rooms[0] if no room carries the Entry role;
        ///   3. any standable cell away from the edge (vanilla cave-exit roll)
        ///      — covers a foreign generator that carved SOMETHING;
        ///   4. an emergency pad carved at map center — covers a fully solid
        ///      map, because an unreachable exit would trap every entering pawn.
        /// </summary>
        private static IntVec3 ResolveEntryCenter(Map map, DungeonMapComponent comp, out CellRect room)
        {
            room = CellRect.Empty;
            if (comp != null && comp.rooms.Count > 0)
            {
                for (int i = 0; i < comp.rooms.Count; i++)
                {
                    if (comp.rooms[i].role == DungeonRoomRole.Entry)
                    {
                        room = comp.rooms[i].rect;
                        return room.CenterCell;
                    }
                }
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Dungeon entrance: no room is flagged Entry — using the first room instead.");
                room = comp.rooms[0].rect;
                return room.CenterCell;
            }

            Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Dungeon entrance: no layout registered (layout GenStep missing or failed) — falling back to a random standable cell.");
            if (CellFinder.TryFindRandomCell(map,
                    c => c.Standable(map) && c.DistanceToEdge(map) > 5.5f,
                    out IntVec3 cell))
                return cell;

            Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Dungeon entrance: map has no standable cells — carving an emergency pad at map center.");
            CarveEmergencyPad(map, map.Center);
            return map.Center;
        }

        /// <summary>
        /// Last-resort pad on a still-solid map: floor + thin roof over the
        /// clear radius so the exit and the start spot are guaranteed valid.
        /// Rock things themselves are removed by ClearArea right after.
        /// </summary>
        private static void CarveEmergencyPad(Map map, IntVec3 center)
        {
            GenStep_DungeonLayout.ResolveContext(map, out DungeonKindDef kind, out MobRankTier _);
            // An OPEN dungeon is already open ground everywhere - there is no
            // solid map to carve a pad out of, and stamping gravel under a rock
            // roof in the middle of a snowfield is exactly what Open forbids.
            if (kind != null && kind.layoutMode == DungeonLayoutMode.Open)
                return;

            foreach (IntVec3 cell in GenRadial.RadialCellsAround(center, ClearRadius, useCenter: true))
            {
                if (!cell.InBounds(map))
                    continue;
                map.terrainGrid.SetTerrain(cell, TerrainDefOf.Gravel);
                map.roofGrid.SetRoof(cell, RoofDefOf.RoofRockThin);
            }
        }

        // ── Spawn-site preparation ──────────────────────────────────────────

        /// <summary>
        /// Destroys every destroyable thing in the clear radius (vanilla
        /// cave-exit pattern). In the normal flow the entry room is already
        /// empty carved floor and this is a no-op; it matters when a fallback
        /// path put the exit somewhere the layout never carved. Cells whose
        /// rock we removed here get floored and thin-roofed like a carved
        /// cell, so the exit never sits in a thick-roofed pocket.
        /// </summary>
        private static void ClearArea(Map map, IntVec3 center)
        {
            GenStep_DungeonLayout.ResolveContext(map, out DungeonKindDef kind, out MobRankTier _);
            TerrainDef forcedFloor = kind != null ? kind.floorTerrain : null;

            foreach (IntVec3 cell in GenRadial.RadialCellsAround(center, ClearRadius, useCenter: true))
            {
                if (!cell.InBounds(map))
                    continue;

                // Natural floor of the rock we are about to remove — same
                // record-before-destroy rule as the layout carve.
                Building edifice = cell.GetEdifice(map);
                TerrainDef naturalFloor = null;
                if (edifice != null && edifice.def.building != null)
                    naturalFloor = edifice.def.building.naturalTerrain;

                bool removedEdifice = false;
                tmpThings.Clear();
                tmpThings.AddRange(cell.GetThingList(map));
                for (int i = 0; i < tmpThings.Count; i++)
                {
                    Thing thing = tmpThings[i];
                    if (thing.Destroyed || !thing.def.destroyable)
                        continue;
                    if (thing == edifice)
                        removedEdifice = true;
                    thing.Destroy();
                }

                // OPEN dungeons must leave the field untouched. The only edifice
                // that can stand here at order 420 is a scattered ruin wall, and
                // re-terraining + roofing its cell would drop a gravel patch
                // under a floating slab of rock in the middle of an untouched
                // snowfield (and darken the landing pad). Clearing the ruin is
                // right; rebuilding a cave floor under it is not.
                bool openField = kind != null && kind.layoutMode == DungeonLayoutMode.Open;
                if (removedEdifice && !openField)
                {
                    map.terrainGrid.SetTerrain(cell, forcedFloor ?? naturalFloor ?? TerrainDefOf.Gravel);
                    map.roofGrid.SetRoof(cell, RoofDefOf.RoofRockThin);
                }
            }
        }

        /// <summary>
        /// Standable cell adjacent to the exit building — where entering pawns
        /// and dev-spawned things appear. Fallback ladder (logged, G5): the
        /// 8-way ring around the building's footprint → any standable cell
        /// within 8 of the center → the center itself (the exit's own cell;
        /// ugly but never invalid, and vanilla tolerates it).
        /// </summary>
        private static IntVec3 FindStartSpot(Map map, Thing exitThing, IntVec3 center)
        {
            foreach (IntVec3 cell in GenAdj.CellsAdjacent8Way(exitThing))
            {
                if (cell.InBounds(map) && cell.Standable(map))
                    return cell;
            }

            IntVec3 near = CellFinder.StandableCellNear(center, map, 8f);
            if (near.IsValid)
                return near;

            Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Dungeon entrance: no standable cell near the exit — player start spot set on the exit itself.");
            return center;
        }
    }
}
