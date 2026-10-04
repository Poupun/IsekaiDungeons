using System.Collections.Generic;
using IsekaiLeveling.MobRanking;
using RimWorld;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Storyteller-driven gate arrival: a plain IncidentDef + worker, so
    /// dungeon gates feel like weather — random, occasionally clustered,
    /// storyteller-paced — unlike the guild board's daily cadence.
    /// DungeonTracker adds a scheduled baseline on top (first-gate day and
    /// interval settings) and fires this same worker.
    /// </summary>
    public class IncidentWorker_DungeonBreak : IncidentWorker
    {
        protected override bool CanFireNowSub(IncidentParms parms)
        {
            if (!base.CanFireNowSub(parms))
                return false;
            // The switch covers the storyteller's own rolls too, not only our
            // schedule: "no local gates" has to mean none.
            if (!IsekaiDungeonsSettings.Current.localGatesEnabled)
                return false;
            Map map = (Map)parms.target;
            if (map == null || !map.IsPlayerHome)
                return false;
            if (map.mapPawns.FreeColonistsCount == 0)
                return false;
            return DungeonTracker.ActiveGateCount(map) < DungeonTunables.MaxActiveGatesPerMap;
        }

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            Map map = (Map)parms.target;
            // Rank FIRST (from colony power), then which dungeon exists at that
            // difficulty - see DungeonKindSelector for why that order matters.
            MobRankTier cap = RaidRankSystem.GetColonyBestRank(map);

            // TWO-PHASE, so a custom archetype's DungeonWorker.RollRank is not
            // dead code. Phase 1 rolls a CANDIDATE rank with the baseline worker
            // (rank must be decided before the kind, or adding low-rank dungeons
            // would drag a strong colony's threat curve down). Phase 2 picks the
            // kind, then lets THAT kind's own worker adjust the rank within its
            // band — which is the only point at which a third-party worker can
            // influence difficulty.
            DungeonKindDef baseline = IsekaiDungeonsDefOf.IsekaiDungeons_BreakDungeon;
            MobRankTier rank = baseline.Worker.RollRank(map, baseline);

            DungeonKindDef kind = DungeonKindSelector.SelectForRank(rank);
            if (kind == null)
                return false;

            if (kind != baseline)
            {
                MobRankTier adjusted = kind.Worker.RollRank(map, kind);
                // Clamp into the kind's own declared band: a worker may shape
                // the roll, but it must not hand out a rank the catalogue says
                // this dungeon cannot be.
                if (adjusted < kind.minRank) adjusted = kind.minRank;
                if (adjusted > kind.maxRank) adjusted = kind.maxRank;
                rank = adjusted;
            }
            bool isRedGate = rank > cap;

            if (!TryFindGateCell(map, kind, out IntVec3 cell))
                return false;

            var gate = (Building_DungeonGate)ThingMaker.MakeThing(kind.gateDef);
            gate.Initialize(kind, rank, isRedGate);
            // FullRefund, never the default Vanish. The footprint is validated
            // clear before we get here, so this should never actually refund
            // anything; it is here so that if a validation hole ever opens, the
            // player is handed their materials back instead of watching a wall
            // silently cease to exist.
            GenSpawn.Spawn(gate, cell, map, Rot4.North, WipeMode.FullRefund);

            TaggedString label = "IsekaiDungeons.Letter.GateSpawned.Label".Translate(
                MobRankUtility.GetRankString(rank).Named("RANK"));
            TaggedString text = "IsekaiDungeons.Letter.GateSpawned.Text".Translate(
                DungeonRankUtil.ColoredRankString(rank).Named("RANK"),
                DungeonTunables.BreakTimerDays(kind, (int)rank).ToString("0.#").Named("DAYS"));
            if (isRedGate)
                text += "\n\n" + "IsekaiDungeons.Letter.RedGate.Suffix".Translate();

            Find.LetterStack.ReceiveLetter(label, text, LetterDefOf.ThreatBig, gate);
            return true;
        }

        /// <summary>
        /// Somewhere a gate can tear open without taking part of the colony
        /// with it.
        ///
        /// THE BUG THIS REPLACES. The old chain had a second pass that set
        /// minDistanceToColonyBuilding to zero, which explicitly invited the
        /// gate into the middle of the base, and its validator only asked
        /// whether a cell was standable and free of edifices. Neither question
        /// notices a growing zone, a stockpile, a blueprint or an unbuilt
        /// frame. Combined with GenSpawn's default WipeMode of Vanish, a gate
        /// could and did land on a player's growing zone and delete it.
        ///
        /// The rule now: a gate may appear anywhere on the map that the player
        /// has not claimed. Zones are ALWAYS refused, even far from the base,
        /// because a growing zone out in a field is still a deliberate act. The
        /// home area is refused too, except by the last-resort pass, and even
        /// that one will not sit on a zone or anything the player owns.
        /// </summary>
        private static bool TryFindGateCell(Map map, DungeonKindDef kind, out IntVec3 cell)
        {
            IntVec2 size = kind.gateDef.size;

            // 1. Vanilla layout-aware placement, biased AWAY from the colony,
            //    then checked against our own rules. Several attempts because
            //    the finder returns one candidate at a time and the first is
            //    not always acceptable.
            LargeBuildingSpawnParms parms = LargeBuildingSpawnParms.Default.ForThing(kind.gateDef);
            parms.minDistanceToColonyBuilding = ClearOfColonyCells;
            parms.preferFarFromColony = true;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                if (LargeBuildingCellFinder.TryFindCell(out cell, map, parms)
                    && IsFootprintFree(map, cell, size, allowHomeArea: false))
                    return true;
            }

            // 2. Our own search, still strictly outside anything the player
            //    has claimed.
            if (CellFinderLoose.TryGetRandomCellWith(
                    c => c.DistanceToEdge(map) > size.x + 4
                         && IsFootprintFree(map, c, size, allowHomeArea: false),
                    map, 800, out cell))
                return true;

            // 3. Last resort: allow it near the base, because a colony that has
            //    zoned half the map should still get dungeons. It STILL refuses
            //    to stand on a zone, a blueprint, or anything the player owns.
            if (CellFinderLoose.TryGetRandomCellWith(
                    c => c.DistanceToEdge(map) > size.x + 4
                         && IsFootprintFree(map, c, size, allowHomeArea: true),
                    map, 800, out cell))
            {
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Gate placement fell back to a cell inside the home area; "
                          + "no clear ground was found outside it.");
                return true;
            }

            Log.Warning($"{IsekaiDungeonsMod.LogPrefix} No cell on {map} can hold a dungeon gate without "
                      + "destroying something the player built; incident skipped.");
            cell = IntVec3.Invalid;
            return false;
        }

        /// <summary>How far a gate keeps from colony buildings on the preferred pass.</summary>
        private const float ClearOfColonyCells = 12f;

        /// <summary>
        /// True when every cell a gate would occupy, plus a one-cell margin, is
        /// genuinely free.
        ///
        /// The margin matters: a gate flush against a wall is not something the
        /// player can walk around, and the gate is impassable in both
        /// directions.
        ///
        /// Shared with the gate summoning scroll, which must refuse exactly
        /// the same ground.
        /// </summary>
        internal static bool IsFootprintFree(Map map, IntVec3 center, IntVec2 size, bool allowHomeArea)
        {
            CellRect rect = GenAdj.OccupiedRect(center, Rot4.North, size).ExpandedBy(1);
            if (!rect.InBounds(map))
                return false;

            foreach (IntVec3 c in rect)
            {
                if (!c.InBounds(map) || c.Fogged(map) || !c.Standable(map))
                    return false;
                if (c.GetEdifice(map) != null)
                    return false;

                // A zone is player intent even when it is nowhere near the
                // base. This is the check whose absence deleted a growing zone.
                if (map.zoneManager.ZoneAt(c) != null)
                    return false;

                if (!allowHomeArea && map.areaManager.Home[c])
                    return false;

                List<Thing> things = c.GetThingList(map);
                for (int i = 0; i < things.Count; i++)
                {
                    Thing thing = things[i];
                    if (thing is Blueprint || thing is Frame)
                        return false;                       // planned work counts too
                    if (thing.Faction != null && thing.Faction == Faction.OfPlayer)
                        return false;
                }
            }
            return true;
        }
    }
}
