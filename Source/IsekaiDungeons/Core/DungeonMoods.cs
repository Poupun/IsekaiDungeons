using System.Collections.Generic;
using IsekaiLeveling;
using IsekaiLeveling.MobRanking;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Every dungeon mood thought, in one place.
    ///
    /// WHY THIS DELEGATES RATHER THAN DUPLICATES. The base mod's IsekaiMoods is
    /// documented as "the ONLY entry point" for event moods, and it earns that:
    /// null pawn, no mood need (animals and mechs have none), dead pawns,
    /// missing defs, the master settings toggle and stage clamping all live
    /// there. Reimplementing any of it here would mean a dungeon thought could
    /// throw on a path a quest thought survives, and would silently ignore a
    /// player who had switched Isekai mood thoughts off. So this class is
    /// nothing but names, rank-to-stage mapping, and one-line forwards.
    ///
    /// The defs themselves live in THIS mod (Defs/ThoughtDefs/DungeonThoughts.xml)
    /// — IsekaiMoods.Gain resolves by defName out of the shared DefDatabase, so
    /// it neither knows nor cares which mod declared them.
    ///
    /// A TYPO'D defName IS A SILENT NO-OP: the thought simply never appears in
    /// game, with no error. Tools/validate_moods.py cross-checks every constant
    /// below against the XML for exactly that reason.
    /// </summary>
    public static class DungeonMoods
    {
        // ── Clearing ──
        public const string DungeonCleared = "IsekaiDungeons_DungeonCleared";  // staged 0..3 by rank
        public const string FloorCleared = "IsekaiDungeons_FloorCleared";      // staged 0..2 by depth
        public const string SummitConquered = "IsekaiDungeons_SummitConquered";
        public const string GateSealed = "IsekaiDungeons_GateSealed";
        public const string BeastPutDown = "IsekaiDungeons_BeastPutDown";

        // ── Failure ──
        public const string GateBroke = "IsekaiDungeons_GateBroke";            // staged 0..2 by rank

        // ── Situational (no code path: ThoughtWorker_DungeonDread drives it) ──
        public const string DungeonDread = "IsekaiDungeons_DungeonDread";

        // ────────────────────────────────────────────────────────────────────
        //  Rank → stage
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Four bands for the clear thought. Deliberately coarser than the ten
        /// ranks: a colonist does not feel a measurably different thing about
        /// an E gate and an F gate, and ten near-identical stage descriptions
        /// would read as padding.
        /// </summary>
        public static int ClearStage(MobRankTier rank)
        {
            if (rank >= MobRankTier.SS) return 3;
            if (rank >= MobRankTier.A) return 2;
            if (rank >= MobRankTier.C) return 1;
            return 0;
        }

        /// <summary>Three bands for the dread and break thoughts.</summary>
        public static int ThreatStage(MobRankTier rank)
        {
            if (rank >= MobRankTier.S) return 2;
            if (rank >= MobRankTier.C) return 1;
            return 0;
        }

        /// <summary>Tower depth band: lower / middle / upper third of the climb.</summary>
        public static int FloorStage(int floor, int totalFloors)
        {
            if (totalFloors <= 0) return 0;
            float t = (float)floor / totalFloors;
            if (t >= 0.67f) return 2;
            if (t >= 0.34f) return 1;
            return 0;
        }

        // ────────────────────────────────────────────────────────────────────
        //  Forwards
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The clear payout, to each pawn handed in — the ones who were
        /// actually standing in the dungeon, not the whole colony. Someone who
        /// stayed home to cook did not clear anything.
        /// </summary>
        public static void GainClear(List<Pawn> delvers, MobRankTier rank)
        {
            if (delvers == null) return;
            int stage = ClearStage(rank);
            for (int i = 0; i < delvers.Count; i++)
                IsekaiMoods.Gain(delvers[i], DungeonCleared, stage);
        }

        /// <summary>One floor of the tower, to the pawns who took it.</summary>
        public static void GainFloorCleared(List<Pawn> delvers, int floor, int totalFloors)
        {
            if (delvers == null) return;
            int stage = FloorStage(floor, totalFloors);
            for (int i = 0; i < delvers.Count; i++)
                IsekaiMoods.Gain(delvers[i], FloorCleared, stage);
        }

        /// <summary>
        /// The tower finished. Colony-wide and map-wide: pass null so it
        /// reaches every colonist everywhere rather than only the party that
        /// happens to be standing on floor 100.
        /// </summary>
        public static void GainSummit()
        {
            IsekaiMoods.GainColonyWide(null, SummitConquered);
        }

        /// <summary>A gate on the colony map resolved, felt by everyone at home.</summary>
        public static void GainGateSealed(Map colonyMap)
        {
            if (colonyMap == null) return;
            IsekaiMoods.GainColonyWide(colonyMap, GateSealed);
        }

        /// <summary>An escaped dungeon boss killed on colony ground.</summary>
        public static void GainBeastPutDown(Map colonyMap)
        {
            if (colonyMap == null) return;
            IsekaiMoods.GainColonyWide(colonyMap, BeastPutDown);
        }

        /// <summary>
        /// A gate burst. Colony-wide dread on the map it burst onto.
        ///
        /// Safe to call more than once for the same gate: the def carries
        /// stackLimit 1, so the breach and the terminal break — separate
        /// moments on one gate — refresh a single dread instead of stacking
        /// two. That is why neither call site needs a latch.
        /// </summary>
        public static void GainGateBroke(Map colonyMap, MobRankTier rank)
        {
            if (colonyMap == null) return;
            IsekaiMoods.GainColonyWide(colonyMap, GateBroke, ThreatStage(rank));
        }
    }
}
