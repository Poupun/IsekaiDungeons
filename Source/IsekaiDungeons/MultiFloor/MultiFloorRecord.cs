using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// One floor of one multi-floor dungeon, as saved.
    ///
    /// Deliberately thinner than TowerFloorRecord. A tower floor has to
    /// remember what it ROLLED, because nothing else knows: the archetype, the
    /// dungeon kind, the rank, the name. Here all of that is authored on the
    /// def and derivable from the floor number, so the record only carries what
    /// the def cannot know — the pinned map id, and what the player did.
    /// </summary>
    public class MultiFloorRecord : IExposable
    {
        public int floor;

        /// <summary>
        /// Pinned pocket-map id. The whole mechanism behind "the same floor
        /// comes back": vanilla re-seeds every gen step from the parent id, so
        /// pinning it reproduces terrain, rock and planting, not just layout.
        /// </summary>
        public int parentId;

        /// <summary>Fixed at first generation so a revisit lays out identically.</summary>
        public int layoutSeed;

        /// <summary>Beaten. A cleared floor regenerates deliberately empty.</summary>
        public bool cleared;

        /// <summary>The party has set foot here at least once.</summary>
        public bool discovered;

        /// <summary>Which treasure rooms have already been emptied.</summary>
        public ulong treasureTakenMask;

        /// <summary>Bit per room index: fought out or its event used. See TowerFloorRecord.roomSpentMask.</summary>
        public ulong roomSpentMask;

        /// <summary>Ore and scatter resources were generated once already.</summary>
        public bool resourcesGenerated;

        /// <summary>Layout the room masks belong to. See DungeonMapParent.layoutSignature.</summary>
        public int layoutSignature;

        /// <summary>
        /// This floor has already had its one regrowth.
        ///
        /// A LATCH, and it exists to keep Gore Hell's spine on the right side
        /// of a rule the population step states outright: the mod pays per
        /// KILL, so a cleared floor that repopulates every visit is an income
        /// stream you can run forever. One regrowth per floor per dungeon is
        /// bounded — the way out costs you once — and that is the version of
        /// the mechanic that was actually designed.
        /// </summary>
        public bool regrown;

        public void ExposeData()
        {
            Scribe_Values.Look(ref floor, "floor", 0);
            Scribe_Values.Look(ref parentId, "parentId", 0);
            Scribe_Values.Look(ref layoutSeed, "layoutSeed", 0);
            Scribe_Values.Look(ref cleared, "cleared", defaultValue: false);
            Scribe_Values.Look(ref discovered, "discovered", defaultValue: false);
            Scribe_Values.Look(ref treasureTakenMask, "treasureTakenMask", 0UL);
            Scribe_Values.Look(ref roomSpentMask, "roomSpentMask", 0UL);
            Scribe_Values.Look(ref resourcesGenerated, "resourcesGenerated", defaultValue: false);
            Scribe_Values.Look(ref layoutSignature, "layoutSignature", 0);
            Scribe_Values.Look(ref regrown, "regrown", defaultValue: false);
        }
    }

    /// <summary>
    /// Pocket-map id allocation for every floor in the mod.
    ///
    /// THIS TYPE EXISTS BECAUSE THE TOWER'S SCHEME CANNOT SCALE, and the
    /// failure would have been ugly. TowerLadder.ParentIdFor is
    /// int.MaxValue - 1024 - floor: one fixed block, which is correct for
    /// exactly one tower and wrong the moment a second multi-floor thing
    /// exists. Two dungeons sharing a parent id means two floors sharing one
    /// map seed AND one save record, which does not throw — it quietly serves
    /// the wrong map.
    ///
    /// So multi-floor dungeons get their own region, well clear of the tower's
    /// hundred, carved into fixed-size blocks and handed out one per instance.
    /// UniqueIDsManager counts UP from zero, so the whole descending region is
    /// unreachable by ordinary allocation no matter how long a game runs.
    /// </summary>
    public static class MultiFloorIds
    {
        /// <summary>
        /// Top of the region. Sits far below the tower's block
        /// (int.MaxValue - 1024 - floor) with room for the tower to grow past
        /// a hundred floors without ever reaching us.
        /// </summary>
        private const int RegionTop = int.MaxValue - 100000;

        /// <summary>
        /// Ids per instance. Sixteen covers the deepest shipped dungeon with
        /// slack, and a power of two keeps the arithmetic obvious.
        /// </summary>
        public const int BlockSize = 16;

        /// <summary>Instances before the region wraps. Far more than a save will ever hold.</summary>
        public const int MaxInstances = 4096;

        /// <summary>
        /// The id for one floor of one instance.
        ///
        /// Keyed on the world object's own id rather than on a counter we
        /// keep, so it survives save, load and deletion of other dungeons with
        /// no bookkeeping. The modulo is what bounds the region; a collision
        /// needs 4096 multi-floor dungeons alive at once in one save.
        /// </summary>
        public static int For(int worldObjectId, int floor)
        {
            int block = ((worldObjectId % MaxInstances) + MaxInstances) % MaxInstances;
            return RegionTop - (block * BlockSize) - Mathf.Clamp(floor, 0, BlockSize - 1);
        }
    }
}
