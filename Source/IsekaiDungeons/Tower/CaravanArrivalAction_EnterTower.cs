using RimWorld;
using RimWorld.Planet;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// What happens when a caravan finishes walking to the tower: a floor is
    /// built and the party is standing on it.
    ///
    /// No intermediate map, no gate, no ground floor. Arriving IS entering —
    /// which is the whole reason the tower is a destination rather than a
    /// building, and why "go to the tower" is a single decision instead of a
    /// journey followed by a second journey across a clearing.
    /// </summary>
    public class CaravanArrivalAction_EnterTower : CaravanArrivalAction
    {
        private WorldObject_Tower tower;
        private int floor = 1;

        public CaravanArrivalAction_EnterTower()
        {
        }

        public CaravanArrivalAction_EnterTower(WorldObject_Tower tower, int floor)
        {
            this.tower = tower;
            this.floor = floor;
        }

        public override string Label =>
            "IsekaiDungeons.Tower.Enter".Translate(
                floor.Named("FLOOR"),
                (TowerComponent.Get?.Preview(floor)?.Label ?? string.Empty).Named("NAME"));

        public override string ReportString =>
            "IsekaiDungeons.Tower.Approaching".Translate(floor.Named("FLOOR"));

        public override FloatMenuAcceptanceReport StillValid(Caravan caravan, PlanetTile destinationTile)
        {
            FloatMenuAcceptanceReport report = base.StillValid(caravan, destinationTile);
            if (!report)
                return report;
            if (tower == null || !tower.Spawned)
                return false;
            if (tower.Tile != destinationTile)
                return false;
            // FloatMenuAcceptanceReport only converts implicitly from bool —
            // the string-carrying variants are built by CannotDoThisNow/
            // WasRejected, not by assignment. The reason is already shown on
            // the world object's own menu option.
            if (TowerFloors.LiveFloor != null)
                return FloatMenuAcceptanceReport.WasRejected;
            // A caravan already on its way when Elijah fell: the tower is
            // coming down and takes nobody in.
            if (TowerComponent.Get?.summitClaimed ?? false)
                return FloatMenuAcceptanceReport.WasRejected;
            return true;
        }

        public override void Arrived(Caravan caravan)
        {
            Map map = TowerFloors.Open(floor);
            if (map == null)
            {
                // Generation already logged why. Leave the caravan standing on
                // the tile rather than deleting it — a failed entry must never
                // cost the player the expedition (G5).
                Messages.Message("IsekaiDungeons.Tower.EnterFailed".Translate(),
                                 caravan, MessageTypeDefOf.RejectInput);
                return;
            }

            TowerComponent.Get?.NotifyReached(floor);

            // The spawn-cell OVERLOAD, not an enter mode. CaravanEnterMode.Edge
            // walks the party in from the map border — meaningless on a floor —
            // and Center drops them in the middle of the layout, which on a
            // dark floor means arriving somewhere they have to walk back from.
            // Neither consults MapGenerator.PlayerStartSpot. Choosing the cell
            // per pawn is the only way to land everyone at the way out.
            IntVec3 arrival = TowerFloors.ArrivalCell(map);
            CaravanEnterMapUtility.Enter(caravan, map,
                p =>
                {
                    IntVec3 c = CellFinder.RandomClosewalkCellNear(arrival, map, 6,
                        x => x.Standable(map) && !x.Fogged(map));
                    if (!c.IsValid)
                        c = CellFinder.StandableCellNear(arrival, map, 20);
                    return c.IsValid ? c : arrival;
                },
                CaravanDropInventoryMode.DoNotDrop, draftColonists: true);

            // Arriving reveals where you arrived. Otherwise the party stands in
            // fog on an unlit floor and the player sees an empty screen.
            FloodFillerFog.FloodUnfog(arrival, map);

            Current.Game.CurrentMap = map;
            CameraJumper.TryHideWorld();
            Find.CameraDriver.JumpToCurrentMapLoc(arrival);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref tower, "tower");
            Scribe_Values.Look(ref floor, "floor", 1);
        }
    }
}
