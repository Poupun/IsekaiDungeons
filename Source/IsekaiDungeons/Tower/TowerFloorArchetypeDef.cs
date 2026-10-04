using System.Collections.Generic;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// What KIND of floor this is — the tower's vocabulary.
    ///
    /// An archetype does not describe a map. It describes how to CHOOSE and
    /// BEND one of the mod's existing 67 dungeons: which environments are
    /// allowed, what to override on the def, and what the floor is called. That
    /// is the whole reason the tower is affordable — it is a scheduler over
    /// content that already exists, not a second content pipeline.
    /// </summary>
    public class TowerFloorArchetypeDef : Def
    {
        /// <summary>Shown as "Floor 23 - {floorLabel}". Null uses the chosen dungeon's own label instead.</summary>
        public string floorLabel;

        /// <summary>Relative frequency when rolling an ordinary floor.</summary>
        public float weight = 1f;

        /// <summary>Floors below/above this cannot roll this archetype. -1 = no bound.</summary>
        public int minFloor = -1;
        public int maxFloor = -1;

        /// <summary>Every Nth floor is forced to this archetype (boss floors). 0 = never forced.</summary>
        public int forceEvery;

        /// <summary>
        /// Restricts which dungeon defs may back this floor. Empty = any.
        /// Matched against DungeonKindDef.layoutMode, so "Carve" gives an
        /// indoor floor and "Open" a field.
        /// </summary>
        public List<DungeonLayoutMode> layoutModes = new List<DungeonLayoutMode>();

        // ── Overrides applied to the chosen dungeon for this floor ──────────
        // All optional. Left unset, the floor plays exactly like the dungeon it
        // borrowed, which is the point: an archetype is a lens, not a rewrite.

        /// <summary>Scales the rolled room count. 1 = untouched.</summary>
        public float roomCountFactor = 1f;

        /// <summary>Scales the encounter budget. 1 = untouched.</summary>
        public float encounterFactor = 1f;

        /// <summary>Forces the treasure-room roll. -1 = the def's own range.</summary>
        public int treasureRooms = -1;

        /// <summary>Whether the up-stair is sealed until the floor's objective completes.</summary>
        public bool sealStairUntilCleared = true;

        /// <summary>
        /// This archetype OWNS its floor band: any floor inside [minFloor,
        /// maxFloor] is this archetype, with no weighted roll at all.
        ///
        /// For stretches that must be guaranteed rather than likely — the
        /// opening floors, which exist to teach the loop, and the summit.
        /// </summary>
        public bool exclusiveBand;

        /// <summary>
        /// A floor that is not a fight.
        ///
        /// GenStep_DungeonPopulation spawns NO roster creatures, NO boss and NO
        /// room events here. Everything else about the place is untouched: its
        /// treasure rooms still fill, its village still builds, and the vanilla
        /// Animals pass still puts deer on it. The tower simply is not trying
        /// to kill you on this floor.
        ///
        /// DELIBERATELY NOT the same flag as "already cleared". That one means
        /// the floor was beaten and paid out, so it also skips treasure; this
        /// one means the floor was never a fight, and it still owns its loot.
        /// </summary>
        public bool peaceful;

        /// <summary>
        /// Restricts which dungeons may back this floor, by def.
        ///
        /// layoutModes alone cannot pin an ENVIRONMENT — "Open" matches five
        /// different dungeons, so a village could land on an ash waste. Empty
        /// means any dungeon that satisfies layoutModes.
        /// </summary>
        public List<DungeonKindDef> dungeonKinds = new List<DungeonKindDef>();

        /// <summary>
        /// Build a settlement on this floor. Requires layoutMode Open — a
        /// village needs unroofed, edge-reachable ground, which a carved floor
        /// is not.
        /// </summary>
        public bool village;

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string e in base.ConfigErrors())
                yield return e;
            // Three ways an archetype gets scheduled: a weighted roll, a fixed
            // cadence, or a band pinned to exactly one floor (the summit).
            // Only an archetype with none of them is unreachable.
            bool pinnedToOneFloor = minFloor >= 0 && minFloor == maxFloor;
            if (exclusiveBand && (minFloor < 0 || maxFloor < 0))
                yield return "exclusiveBand needs both minFloor and maxFloor";
            if (weight <= 0f && forceEvery <= 0 && !pinnedToOneFloor && !exclusiveBand)
                yield return "an archetype needs weight > 0, forceEvery > 0, or a single-floor band, or it can never be scheduled";
            if (roomCountFactor <= 0f)
                yield return "roomCountFactor must be > 0";
            if (village && layoutModes.Count > 0 && !layoutModes.Contains(DungeonLayoutMode.Open))
                yield return "a village archetype must allow the Open layout mode";
            if (encounterFactor <= 0f)
                yield return "encounterFactor must be > 0";
            // With no boss, the default objective can never be met, so a
            // peaceful floor that sealed its stair would seal it forever.
            if (peaceful && sealStairUntilCleared)
                yield return "a peaceful archetype must set sealStairUntilCleared false — with no boss the floor can never be completed";
        }
    }
}
