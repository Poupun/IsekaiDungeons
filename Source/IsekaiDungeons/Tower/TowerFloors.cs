using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// The floor lifecycle: build one, tear one down.
    ///
    /// There is NO building at the foot of the tower and no ground floor to
    /// walk across. Travelling to the tower's tile and going in puts you on a
    /// floor — the tower IS its floors. That is why this is a static service
    /// rather than a method on a gate: nothing physical owns a floor, so the
    /// only thing that can own the lifecycle is the tower's own record book.
    ///
    /// FLOORS ARE POCKET MAPS, deliberately, even though they now hang off a
    /// world tile rather than a portal. MapGenerator seeds a world-tile map
    /// from HashCombineInt(worldSeed, tile) and a pocket map from
    /// HashCombineInt(worldSeed, parent.ID). Every floor shares one tile — so
    /// as world-tile maps all hundred floors would generate byte-identical
    /// terrain, rock and planting. As pocket maps with a pinned parent ID, each
    /// floor gets its own character AND comes back exactly as it was.
    /// </summary>
    public static class TowerFloors
    {
        /// <summary>The live floor map, if the player is inside the tower at all.</summary>
        public static Map LiveFloor
        {
            get
            {
                List<Map> maps = Find.Maps;
                for (int i = 0; i < maps.Count; i++)
                {
                    if (maps[i].Parent is TowerFloorMapParent)
                        return maps[i];
                }
                return null;
            }
        }

        public static TowerFloorMapParent LiveParent => LiveFloor?.Parent as TowerFloorMapParent;

        /// <summary>The tower on the planet, or null if none has been raised.</summary>
        public static WorldObject_Tower Tower
        {
            get
            {
                List<WorldObject> all = Find.WorldObjects.AllWorldObjects;
                for (int i = 0; i < all.Count; i++)
                {
                    if (all[i] is WorldObject_Tower t)
                        return t;
                }
                return null;
            }
        }

        /// <summary>
        /// Builds a floor and returns its map, or null if generation failed.
        ///
        /// Generation is TRANSACTIONAL: a throw cleans up the half-built parent
        /// and returns null rather than leaving an orphan world object and a
        /// map nobody can reach.
        /// </summary>
        public static Map Open(int floor)
        {
            TowerComponent comp = TowerComponent.Get;
            WorldObject_Tower tower = Tower;
            if (comp == null || !comp.Exists || tower == null)
                return null;

            TowerFloorRecord rec = comp.EnsureRecord(floor);
            if (rec.kindDef?.mapGenerator == null)
            {
                Log.Error($"{IsekaiDungeonsMod.LogPrefix} Tower floor {floor} rolled a dungeon with no map generator.");
                return null;
            }

            var parent = (TowerFloorMapParent)WorldObjectMaker.MakeWorldObject(
                IsekaiDungeonsDefOf.IsekaiDungeons_TowerFloorParent);

            // PIN THE ID BEFORE GENERATING. This one assignment is the entire
            // mechanism behind "the same floor comes back" — vanilla re-seeds
            // every gen step from the parent ID, Plants and Fog included, so
            // pinning it reproduces the whole map and not merely our layout.
            // The reserved descending range cannot collide: UniqueIDsManager
            // counts up from zero.
            parent.ID = rec.parentId;
            parent.mapGenerator = rec.kindDef.mapGenerator;
            parent.kindDef = rec.kindDef;
            parent.rank = rec.rank;
            parent.rankIndex = rec.rankIndex;
            parent.layoutSeed = rec.layoutSeed;
            parent.floor = rec.floor;

            // The archetype's lens on the borrowed dungeon.
            TowerFloorArchetypeDef a = rec.archetype;
            parent.roomCountFactor = a?.roomCountFactor ?? 1f;
            parent.encounterFactor = a?.encounterFactor ?? 1f;
            parent.treasureRoomsOverride = a?.treasureRooms ?? -1;
            parent.peaceful = a?.peaceful ?? false;
            parent.village = a?.village ?? false;

            // The floor's own history. A beaten floor comes back empty; treasure
            // already taken stays taken.
            parent.suppressPopulation = rec.cleared;
            parent.treasureTakenMask = rec.treasureTakenMask;
            parent.roomSpentMask = rec.roomSpentMask;
            parent.suppressResources = rec.resourcesGenerated;
            parent.layoutSignature = rec.layoutSignature;

            // A surface biome on an invalid tile crashes in FinalizeInit via the
            // plant growth cache. The tower's own tile is the right answer, and
            // it also means the floor inherits the local biome.
            parent.Tile = tower.Tile;

            Map map;
            try
            {
                // The record keeps the size the floor was rolled with; the map-size
                // setting scales it at generation, inside the same TPS guard the
                // gates use.
                int size = UnityEngine.Mathf.Clamp(DungeonTunables.ScaledMapSize(rec.mapSize), 120, 320);
                map = MapGenerator.GenerateMap(new IntVec3(size, 1, size), parent,
                                               parent.mapGenerator, null, null, isPocketMap: true);
            }
            catch (Exception ex)
            {
                Log.Error($"{IsekaiDungeonsMod.LogPrefix} Tower floor {rec.floor} map generation threw; cleaning up. {ex}");
                CleanUpFailedMap(parent);
                return null;
            }

            Find.World.pocketMaps.Add(parent);
            rec.resourcesGenerated = true;
            PlaceStair(map, rec);
            // The exit is placed by GenStep_DungeonEntrance, which knows this
            // is a tower floor and spawns the world-facing exit instead of the
            // gate-bound one.

            if (rec.archetype != null && rec.archetype.village)
                rec.villageState = TowerVillage.Build(map, rec, rec.villageState);

            return map;
        }

        private static void CleanUpFailedMap(TowerFloorMapParent parent)
        {
            try
            {
                List<Map> maps = Current.Game.Maps;
                for (int i = maps.Count - 1; i >= 0; i--)
                {
                    if (maps[i].Parent == parent)
                    {
                        Current.Game.DeinitAndRemoveMap(maps[i], notifyPlayer: false);
                        break;
                    }
                }
                Find.World.pocketMaps.Remove(parent);
            }
            catch (Exception ex)
            {
                Log.Error($"{IsekaiDungeonsMod.LogPrefix} Failed to clean up a broken tower floor: {ex}");
            }
        }

        /// <summary>
        /// The up-stair, in the boss room at the far end, so climbing means
        /// crossing the floor. Placed from CODE rather than a GenStepDef so the
        /// tower can borrow any of the mod's ten environments without editing a
        /// single MapGeneratorDef. FootprintSpot picks a spot the whole 2x2
        /// fits, with a walkable ring round it.
        /// </summary>
        private static void PlaceStair(Map map, TowerFloorRecord rec)
        {
            // The summit has nowhere above it, and leaving the stair off is what
            // makes that legible: you look for the way up and there isn't one.
            if (rec.floor >= TowerTunables.FloorCount)
                return;

            ThingDef def = IsekaiDungeonsDefOf.IsekaiDungeons_TowerStair;
            IntVec3 cell = FootprintSpot.StairCell(map, def, $"Tower floor {rec.floor} stair");
            if (!cell.IsValid)
            {
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} No cell for the floor {rec.floor} stair; the floor will have no way up.");
                return;
            }

            var stair = (Building_TowerStair)ThingMaker.MakeThing(def);
            stair.floor = rec.floor;
            // MoveAside only matters on the last-resort pick: every other spot
            // is already free of items.
            GenSpawn.Spawn(stair, cell, map, WipeMode.VanishOrMoveAside);
        }

        /// <summary>
        /// The floor's exit building: the tower's, or a multi-floor dungeon's
        /// (a subclass). Matched by class, not def, so both forms count.
        /// </summary>
        public static Building WayOut(Map map)
        {
            List<Building> b = map?.listerBuildings?.allBuildingsNonColonist;
            if (b == null)
                return null;
            for (int i = 0; i < b.Count; i++)
            {
                if (b[i] is Building_TowerExit)
                    return b[i];
            }
            return null;
        }

        public static bool HasWayOut(Map map) => WayOut(map) != null;

        /// <summary>Where a party arriving on a floor should be put.</summary>
        public static IntVec3 ArrivalCell(Map map)
        {
            Building exit = WayOut(map);
            if (exit != null)
                return exit.Position;
            DungeonMapComponent comp = map.GetComponent<DungeonMapComponent>();
            if (comp?.rooms != null && comp.rooms.Count > 0)
                return comp.rooms[0].rect.CenterCell;
            return map.Center;
        }

        // ── Teardown ────────────────────────────────────────────────────────

        /// <summary>
        /// Destroys a floor, but never anything that cannot be replaced.
        ///
        /// THE CORPSE PASS IS MANDATORY, not defensive. MapPawns.AllPawnsUnspawned
        /// removes dead pawns, so MapDeiniter.PassPawnsToWorld never sees a
        /// corpse's InnerPawn: a colonist who died on a floor the player walks
        /// away from is destroyed outright — no body, no grave, no resurrection,
        /// and no message saying so.
        ///
        /// <paramref name="destination"/> is the floor above when climbing. When
        /// null, whatever is rescued leaves the tower as a caravan on its tile —
        /// which is the only way off a pocket map now that there is no gate to
        /// walk back to.
        /// </summary>
        public static void Retire(Map floorMap, Map destination)
        {
            if (floorMap == null)
                return;

            HarvestRecord(floorMap);

            var rescuedPawns = new List<Pawn>();
            var rescuedThings = new List<Thing>();
            try
            {
                // Where a fallen colonist's things ended up. Vanilla drops the
                // weapon and inventory BESIDE a body (Pawn.Kill) and a downed
                // pawn drops its weapon at its feet; the map is destroyed a
                // moment later, so those have to be gathered with the pawn.
                var gatherAround = new List<IntVec3>();
                var gathered = new HashSet<Thing>();

                var pawns = new List<Pawn>(floorMap.mapPawns.AllPawnsSpawned);
                for (int i = 0; i < pawns.Count; i++)
                {
                    Pawn p = pawns[i];
                    if (p.Dead)
                        continue;
                    // Prisoners and guests travel with the party: a captured
                    // mirror or villager, a quest lodger. They used to be left
                    // behind and lost with the map.
                    bool ours = p.Faction == Faction.OfPlayer || p.IsPrisonerOfColony
                                || p.HostFaction == Faction.OfPlayer;
                    if (!ours)
                        continue;
                    if (p.Downed)
                    {
                        gatherAround.Add(p.Position);
                        Thing dropped = p.mindState?.droppedWeapon;
                        if (dropped != null && dropped.Spawned && dropped.Map == floorMap)
                            gathered.Add(dropped);
                    }
                    p.DeSpawn();
                    rescuedPawns.Add(p);
                }

                // Then bodies. WorldPawnGC keeps only colonists and bonded
                // animals, so an unbonded pack animal left behind is discarded
                // permanently too — hence "every player-faction pawn", not
                // "every colonist".
                List<Thing> corpses = floorMap.listerThings.ThingsInGroup(ThingRequestGroup.Corpse);
                for (int i = corpses.Count - 1; i >= 0; i--)
                {
                    if (corpses[i] is Corpse corpse && corpse.InnerPawn?.Faction == Faction.OfPlayer)
                    {
                        gatherAround.Add(corpse.Position);
                        corpse.DeSpawn();
                        rescuedThings.Add(corpse);
                    }
                }

                for (int g = 0; g < gatherAround.Count; g++)
                {
                    foreach (IntVec3 c in GenRadial.RadialCellsAround(gatherAround[g], 1.5f, true))
                    {
                        if (!c.InBounds(floorMap))
                            continue;
                        List<Thing> here = c.GetThingList(floorMap);
                        for (int t = 0; t < here.Count; t++)
                        {
                            Thing thing = here[t];
                            if (thing.def.category == ThingCategory.Item && thing.def.EverHaulable
                                && !(thing is Corpse))
                                gathered.Add(thing);
                        }
                    }
                }
                foreach (Thing thing in gathered)
                {
                    if (!thing.Spawned)
                        continue;
                    thing.DeSpawn();
                    rescuedThings.Add(thing);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"{IsekaiDungeonsMod.LogPrefix} Error evacuating a tower floor: {ex}");
            }

            PocketMapUtility.DestroyPocketMap(floorMap);

            if (destination != null)
                DeliverToMap(rescuedPawns, rescuedThings, destination);
            else
                DeliverToWorld(rescuedPawns, rescuedThings);
        }

        /// <summary>
        /// Writes what happened on this floor into the record. This is the ONE
        /// moment the outcome can still be observed — in a moment there will be
        /// no map to ask.
        /// </summary>
        private static void HarvestRecord(Map floorMap)
        {
            // Retire is shared with the multi-floor dungeons, which keep their
            // records on their own world object rather than in TowerComponent.
            // Routing here rather than at the call sites means every way out of
            // a floor - the stair, the exit, the dungeon being removed - lands
            // on the right book without each one having to know.
            if (floorMap?.Parent is MultiFloorMapParent)
            {
                MultiFloorFloors.HarvestRecord(floorMap);
                return;
            }

            var parent = floorMap.Parent as TowerFloorMapParent;
            DungeonMapComponent dungeon = floorMap.GetComponent<DungeonMapComponent>();
            TowerFloorRecord rec = parent != null ? TowerComponent.Get?.RecordIfSeen(parent.floor) : null;
            if (rec == null || dungeon == null)
                return;

            if (dungeon.IsCleared)
                rec.cleared = true;

            if (rec.villageState == TowerVillageState.Friendly)
                rec.villageState = TowerVillage.Observe(floorMap, rec.villageState);

            // A treasure room the party actually walked into is spent. The room
            // index survives regeneration because the layout is a pure function
            // of the pinned seed, so one bit per room is enough.
            if (dungeon.rooms == null)
                return;

            // Masks recorded against a different layout describe other rooms:
            // start them over for this one.
            int sig = LayoutSignature(dungeon.rooms);
            if (rec.layoutSignature != sig)
            {
                if (rec.layoutSignature != 0)
                {
                    rec.treasureTakenMask = 0UL;
                    rec.roomSpentMask = 0UL;
                }
                rec.layoutSignature = sig;
            }

            for (int i = 0; i < dungeon.rooms.Count; i++)
            {
                DungeonRoom room = dungeon.rooms[i];
                if (room.index < 0 || room.index >= 64)
                    continue;
                if (room.role == DungeonRoomRole.Treasure && room.entered)
                    rec.treasureTakenMask |= 1UL << room.index;
                if (IsRoomSpent(room, floorMap))
                    rec.roomSpentMask |= 1UL << room.index;
            }
        }

        /// <summary>
        /// A combat room is spent once the party fought it out: it was entered
        /// or woken, and none of its creatures is still standing on this map.
        /// A room with an event is spent as soon as it is entered, because the
        /// event IS the reward (Mana Spring cores, the Guarded Hoard roll) and
        /// grabbing it without killing the guards must not reset it either.
        /// Shared with the multi-floor records.
        /// </summary>
        /// <summary>
        /// A stable hash of the room layout: every room's index, role and rect.
        /// Never 0, so 0 can mean "not recorded yet".
        /// </summary>
        internal static int LayoutSignature(List<DungeonRoom> rooms)
        {
            unchecked
            {
                int h = 17;
                for (int i = 0; i < rooms.Count; i++)
                {
                    DungeonRoom r = rooms[i];
                    h = h * 31 + r.index;
                    h = h * 31 + (int)r.role;
                    h = h * 31 + r.rect.minX;
                    h = h * 31 + r.rect.minZ;
                    h = h * 31 + r.rect.Width;
                    h = h * 31 + r.rect.Height;
                }
                return h == 0 ? 1 : h;
            }
        }

        internal static bool IsRoomSpent(DungeonRoom room, Map map)
        {
            if (room == null || room.role != DungeonRoomRole.Combat)
                return false;
            if (room.entered && room.eventDef != null)
                return true;
            if (!room.entered && !room.aggroed)
                return false;
            for (int i = 0; i < room.creatures.Count; i++)
            {
                Pawn c = room.creatures[i];
                if (c != null && !c.Dead && !c.Destroyed && !c.Downed && c.MapHeld == map)
                    return false;
            }
            return true;
        }

        private static void DeliverToMap(List<Pawn> pawns, List<Thing> things, Map destination)
        {
            IntVec3 anchor = ArrivalCell(destination);
            for (int i = 0; i < pawns.Count; i++)
                SpawnNear(pawns[i], anchor, destination);
            for (int i = 0; i < things.Count; i++)
                SpawnNear(things[i], anchor, destination);
        }

        private static void SpawnNear(Thing t, IntVec3 anchor, Map map)
        {
            IntVec3 cell = CellFinder.RandomClosewalkCellNear(anchor, map, 6, c => c.Standable(map));
            if (!cell.IsValid)
                cell = CellFinder.StandableCellNear(anchor, map, 16);
            if (cell.IsValid)
                GenSpawn.Spawn(t, cell, map);
            else if (t is Pawn p && !p.Dead && p.holdingOwner == null)
                Find.WorldPawns.PassToWorld(p);
        }

        /// <summary>
        /// Leaving the tower: everyone rescued becomes a caravan standing on the
        /// tower's tile. There is no gate to step back through, so this IS the
        /// way out, and it drops the player exactly where they would be if they
        /// had turned around at the door.
        /// </summary>
        private static void DeliverToWorld(List<Pawn> pawns, List<Thing> things)
        {
            WorldObject_Tower tower = Tower;
            PlanetTile tile = tower?.Tile ?? PlanetTile.Invalid;

            if (pawns.Count == 0)
            {
                // Nobody walked out. Anything still held is unreachable, so say
                // so rather than deleting it in silence.
                if (things.Count > 0)
                {
                    Log.Warning($"{IsekaiDungeonsMod.LogPrefix} {things.Count} recovered item(s) had no living pawn to carry them out of the tower.");
                    Messages.Message("IsekaiDungeons.Tower.NothingWalkedOut".Translate(),
                                     MessageTypeDefOf.NegativeEvent, historical: true);
                }
                return;
            }

            try
            {
                Caravan caravan = CaravanMaker.MakeCaravan(pawns, Faction.OfPlayer, tile,
                                                           addToWorldPawnsIfNotAlready: true);
                for (int i = 0; i < things.Count; i++)
                    CaravanInventoryUtility.GiveThing(caravan, things[i]);

                Messages.Message("IsekaiDungeons.Tower.LeftTower".Translate(pawns.Count.Named("COUNT")),
                                 caravan, MessageTypeDefOf.NeutralEvent);
                CameraJumper.TryJumpAndSelect(caravan);
            }
            catch (Exception ex)
            {
                Log.Error($"{IsekaiDungeonsMod.LogPrefix} Could not form a caravan leaving the tower: {ex}");
                for (int i = 0; i < pawns.Count; i++)
                {
                    if (!pawns[i].Dead && pawns[i].holdingOwner == null)
                        Find.WorldPawns.PassToWorld(pawns[i]);
                }
            }
        }
    }
}
