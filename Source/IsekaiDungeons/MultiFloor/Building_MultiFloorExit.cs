using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// The way out of a multi-floor dungeon, standing where the party arrived.
    ///
    /// Behaves exactly like the tower's exit (the whole party leaves together
    /// and forms a caravan on the dungeon's tile), so it inherits all of that.
    /// What it changes is what it LOOKS and READS like. The tower exit is a
    /// pale stair sinking toward the Tower's pink, which is right for a tower
    /// and wrong for a hole in the ground: most of these dungeons go DOWN, so
    /// their way out climbs UP toward daylight, and it is built from the
    /// dungeon's own stone. The Choir Spire is the exception that goes up, so
    /// its way out is the one that sinks.
    /// </summary>
    public class Building_MultiFloorExit : Building_TowerExit
    {
        private MultiFloorMapParent Parent => Map?.Parent as MultiFloorMapParent;
        private MultiFloorDungeonDef Plan => Parent?.Plan;
        private bool Descends => Plan?.descends ?? true;

        /// <summary>
        /// The dungeon's own art, "Exit" in its structureArt folder. Constant for
        /// the building's life, so the def can stay in the map mesh.
        /// </summary>
        public override Graphic Graphic =>
            Plan?.StructureGraphic("Exit", def.graphicData.drawSize) ?? base.Graphic;

        // Descending dungeon: the way out is up. Ascending one: down.
        public override string LabelNoCount =>
            (Descends ? "IsekaiDungeons.MultiFloor.ExitUp" : "IsekaiDungeons.MultiFloor.ExitDown").Translate();

        public override string DescriptionFlavor =>
            "IsekaiDungeons.MultiFloor.ExitDescription".Translate(
                (Plan?.LabelCap ?? string.Empty).Named("PLACE"));

        protected override string LeaveLabel =>
            "IsekaiDungeons.MultiFloor.Leave".Translate((Plan?.LabelCap ?? string.Empty).Named("PLACE"));

        protected override string LeaveDesc => "IsekaiDungeons.MultiFloor.LeaveDesc".Translate();

        public override string GetInspectString()
        {
            MultiFloorMapParent p = Parent;
            if (p?.Plan == null)
                return base.GetInspectString();
            return "IsekaiDungeons.MultiFloor.ExitInspect".Translate(
                p.floor.Named("FLOOR"), p.Plan.floorCount.Named("TOTAL"));
        }
    }
}
