using IsekaiLeveling.MobRanking;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// World object owning one live tower floor.
    ///
    /// Subclasses DungeonMapParent so every GenStep, the map component, the
    /// aggro sweep and the loot generator resolve rank/kind/seed exactly as
    /// they do for an ordinary dungeon — the tower reuses the whole pipeline
    /// rather than forking it. All it adds is which floor this is.
    ///
    /// Its ID is PINNED by TowerFloors.Open before generation. That is not
    /// cosmetic: a pocket map's seed is HashCombineInt(worldSeed, parent.ID)
    /// and vanilla re-seeds every gen step from it, so pinning the ID is what
    /// makes a rebuilt floor come back identical down to where the grass grew.
    /// </summary>
    public class TowerFloorMapParent : DungeonMapParent
    {
        public int floor;

        /// <summary>
        /// A settlement is built on this floor.
        ///
        /// Lives on the PARENT rather than only on the archetype because
        /// GenStep_TowerVillage runs inside map generation, where the record is
        /// not conveniently reachable but the parent always is.
        /// </summary>
        public bool village;

        /// <summary>
        /// Footprint of the generated settlement, or an invalid rect if there
        /// is none.
        ///
        /// Recorded because the two halves of a village are built at different
        /// times: GenStep_TowerVillage lays down the houses during map
        /// generation, and TowerVillage.Build puts people in them afterwards.
        /// Without this the second half has no idea where the first half went,
        /// which is exactly how the villagers ended up standing at the exit.
        /// </summary>
        public CellRect villageRect = CellRect.Empty;

        /// <summary>
        /// "Tower of Gods • Floor 42".
        ///
        /// The archetype used to be in the label ("Floor 42 — Crypt") and has
        /// moved into the description. A world tile should answer WHERE at a
        /// glance, and every one of these tiles belonged to the same tower
        /// without ever saying so.
        /// </summary>
        public override string Label =>
            "IsekaiDungeons.Tower.FloorTileName".Translate(floor.Named("FLOOR"));

        /// <summary>
        /// The floor's own line plus the tower's lore. Kept on the WORLD OBJECT
        /// rather than in the def because the first two facts - which floor and
        /// what rank - are per instance, and a def description cannot see them.
        /// </summary>
        public override string GetDescription() =>
            "IsekaiDungeons.Tower.FloorDesc".Translate(
                floor.Named("FLOOR"),
                (kindDef?.label ?? "an ordinary course").Named("WHAT"),
                MobRankUtility.GetRankString(rank).Named("RANK"));

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref floor, "floor", 0);
            Scribe_Values.Look(ref village, "village", defaultValue: false);
            Scribe_Values.Look(ref villageRect, "villageRect", CellRect.Empty);
        }
    }
}
