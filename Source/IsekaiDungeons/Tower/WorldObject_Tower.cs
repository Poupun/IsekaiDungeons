using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// THE TOWER, as it exists on the planet.
    ///
    /// A place you travel to. It is NOT a MapParent and has no map of its own:
    /// there is no ground floor, no clearing, no gate to walk into. Going to
    /// the tower and going in puts you on a FLOOR — the tower is nothing but
    /// its floors, and floors are pocket maps built by TowerFloors.
    ///
    /// So this class is exactly two things: a marker on the planet, and the
    /// right-click menu a caravan gets when it is told to go there.
    /// </summary>
    public class WorldObject_Tower : WorldObject
    {
        public override string Label
        {
            get
            {
                TowerComponent comp = TowerComponent.Get;
                int reached = comp?.highestReached ?? 0;
                return reached > 0
                    ? "IsekaiDungeons.Tower.MarkerLabelProgress".Translate(reached.Named("FLOOR"))
                    : "IsekaiDungeons.Tower.MarkerLabel".Translate();
            }
        }

        /// <summary>
        /// The caravan's right-click menu.
        ///
        /// Floor 1 is always offered. Every floor the party has actually
        /// CLIMBED to is offered as well, because re-walking forty floors to
        /// reach the frontier is not difficulty, it is bookkeeping. Discovery
        /// is still earned by the stair — this only ever lists what the stair
        /// already granted.
        /// </summary>
        public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Caravan caravan)
        {
            foreach (FloatMenuOption o in base.GetFloatMenuOptions(caravan))
                yield return o;

            TowerComponent comp = TowerComponent.Get;
            if (comp == null || !comp.Exists)
                yield break;

            // Elijah is dead and the tower is coming down. Nobody goes back in:
            // the collapse waits for the tower to be empty, so letting a party
            // in would hold it up indefinitely.
            if (comp.summitClaimed)
            {
                yield return new FloatMenuOption("IsekaiDungeons.Tower.Enter.Collapsing".Translate(), null);
                yield break;
            }

            // One floor at a time. A second party wandering in while the first
            // is on floor 12 would need a second live floor, and the record
            // model has exactly one.
            if (TowerFloors.LiveFloor != null)
            {
                yield return new FloatMenuOption("IsekaiDungeons.Tower.Enter.Busy".Translate(), null);
                yield break;
            }

            yield return EnterOption(caravan, 1);

            List<TowerFloorRecord> discovered = comp.DiscoveredFloors();
            bool many = discovered.Count > 6;
            if (!many)
            {
                for (int i = 0; i < discovered.Count; i++)
                {
                    if (discovered[i].floor > 1)
                        yield return EnterOption(caravan, discovered[i].floor);
                }
            }
            else
            {
                // Past a handful of floors the float menu stops being readable.
                yield return new FloatMenuOption(
                    "IsekaiDungeons.Tower.EnterPick".Translate(), delegate
                    {
                        Find.WindowStack.Add(new Dialog_TowerFloorPicker(
                            f => Travel(caravan, f), allFloors: false));
                    });
            }

            // GOD MODE: every floor, explored or not. Testing floor 97's
            // generation should not mean beating ninety-six floors first.
            if (DebugSettings.godMode)
            {
                yield return new FloatMenuOption(
                    "IsekaiDungeons.Tower.EnterAnyDev".Translate(), delegate
                    {
                        Find.WindowStack.Add(new Dialog_TowerFloorPicker(
                            f => Travel(caravan, f), allFloors: true));
                    });
            }
        }

        private FloatMenuOption EnterOption(Caravan caravan, int floor)
        {
            TowerFloorRecord rec = TowerComponent.Get?.Preview(floor);
            string label = "IsekaiDungeons.Tower.Enter".Translate(
                floor.Named("FLOOR"),
                (rec?.Label ?? string.Empty).Named("NAME"));

            return new FloatMenuOption(label, delegate { Travel(caravan, floor); });
        }

        /// <summary>
        /// Sends the caravan here to enter at a given floor. Routed through the
        /// SAME arrival action the ordinary menu uses, so a dev-picked floor is
        /// entered exactly the way a played one is.
        /// </summary>
        private void Travel(Caravan caravan, int floor)
        {
            caravan.pather.StartPath(Tile, new CaravanArrivalAction_EnterTower(this, floor),
                                     repathImmediately: true);
        }

        public override string GetInspectString()
        {
            TowerComponent comp = TowerComponent.Get;
            if (comp == null || !comp.Exists)
                return base.GetInspectString();

            IReadOnlyList<TowerFloorRecord> records = comp.Records;
            int cleared = 0;
            for (int i = 0; i < records.Count; i++)
            {
                if (records[i].cleared)
                    cleared++;
            }
            string s = base.GetInspectString();
            if (!s.NullOrEmpty())
                s += "\n";
            s += "IsekaiDungeons.Tower.MarkerDesc".Translate(
                comp.highestReached.Named("FLOOR"),
                cleared.Named("CLEARED"));
            if (comp.summitClaimed)
                s += "\n" + "IsekaiDungeons.Tower.CollapsingInspect".Translate();
            return s;
        }

        /// <summary>
        /// Raises a tower on a tile near the player, once per playthrough.
        /// Returns the world object, or null if the planet had nowhere to put it.
        /// </summary>
        public static WorldObject_Tower TryRaise(int seed)
        {
            TowerComponent comp = TowerComponent.Get;
            if (comp == null)
                return null;

            WorldObject_Tower existing = TowerFloors.Tower;
            if (existing != null)
                return existing;

            if (!TryFindTowerTile(out PlanetTile tile))
            {
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Found no reachable tile for the tower; not raising one.");
                return null;
            }

            // Build and register FIRST, mark the component LAST. BeginTower is
            // what gates the incident from ever firing again, so setting it
            // before the world object exists would burn a once-per-playthrough
            // event on a tower that was never placed.
            var tower = (WorldObject_Tower)WorldObjectMaker.MakeWorldObject(
                IsekaiDungeonsDefOf.IsekaiDungeons_Tower);
            tower.Tile = tile;
            Find.WorldObjects.Add(tower);
            comp.BeginTower(seed);
            return tower;
        }

        /// <summary>
        /// Finds somewhere to put the tower that a caravan can actually walk to.
        ///
        /// THE REACHABILITY CHECK IS THE POINT. TileFinder picks by great-circle
        /// distance with no pathfinding, so it will happily choose an island.
        /// For a quest site that is a nuisance; for the tower it is fatal,
        /// because it is raised ONCE per playthrough and a tower nobody can
        /// reach is the whole feature gone with nothing in the log to explain it.
        /// </summary>
        private static bool TryFindTowerTile(out PlanetTile tile)
        {
            PlanetTile from = PlanetTile.Invalid;
            if (Find.AnyPlayerHomeMap != null)
                from = Find.AnyPlayerHomeMap.Tile;

            bool Reachable(PlanetTile t)
            {
                if (!from.Valid)
                    return true;
                try
                {
                    return Find.WorldReachability.CanReach(from, t);
                }
                catch
                {
                    // The reachability grid may not be built yet. Do not let
                    // that veto every candidate.
                    return true;
                }
            }

            if (TileFinder.TryFindNewSiteTile(out tile, 3, 18, validator: Reachable))
                return true;
            if (TileFinder.TryFindNewSiteTile(out tile, 3, 40, validator: Reachable))
                return true;

            if (TileFinder.TryFindNewSiteTile(out tile, 3, 40))
            {
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Tower tile {tile} may not be reachable overland from the colony.");
                return true;
            }
            return false;
        }
    }
}
