using IsekaiLeveling.MobRanking;
using RimWorld.Planet;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// World object owning one live floor of one multi-floor dungeon.
    ///
    /// Subclasses DungeonMapParent for the same reason the tower's floor
    /// parent does: every GenStep, the aggro sweep, the loot generator and the
    /// room events resolve kind, rank and seed off this, so a floor is an
    /// ordinary dungeon map as far as all of them are concerned.
    ///
    /// What it adds is which floor, and WHOSE. The tower could get away with
    /// only the floor number because there is exactly one tower; here the
    /// owner has to be carried explicitly, or a floor cannot find its way back
    /// to the record book that made it.
    /// </summary>
    public class MultiFloorMapParent : DungeonMapParent
    {
        /// <summary>1-based, the number the player sees.</summary>
        public int floor;

        /// <summary>
        /// The world object this floor belongs to, by id.
        ///
        /// An ID rather than a reference: world objects resolve in an
        /// unspecified order on load, and a direct reference to one that has
        /// not been rebuilt yet is null exactly when the floor needs it most.
        /// </summary>
        public int ownerId = -1;

        public WorldObject_MultiFloorDungeon Owner => MultiFloorFloors.DungeonById(ownerId);

        public MultiFloorDungeonDef Plan => Owner?.Def;

        /// <summary>"Hell Hole • Floor 4". Says WHERE at a glance, which is a world tile's job.</summary>
        public override string Label
        {
            get
            {
                MultiFloorDungeonDef plan = Plan;
                if (plan == null)
                    return base.Label;
                return "IsekaiDungeons.MultiFloor.FloorTileName".Translate(
                    plan.LabelCap.Named("PLACE"), floor.Named("FLOOR"));
            }
        }

        public override string GetDescription()
        {
            MultiFloorDungeonDef plan = Plan;
            if (plan == null)
                return base.GetDescription();
            return "IsekaiDungeons.MultiFloor.FloorDesc".Translate(
                floor.Named("FLOOR"),
                plan.floorCount.Named("TOTAL"),
                plan.LabelCap.Named("PLACE"),
                MobRankUtility.GetRankString(rank).Named("RANK"))
                + "\n\n" + plan.description;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref floor, "floor", 0);
            Scribe_Values.Look(ref ownerId, "ownerId", -1);
        }
    }
}
