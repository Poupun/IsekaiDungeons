using System.Linq;
using System.Collections.Generic;
using IsekaiLeveling.MobRanking;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Rolls a floor's IDENTITY: which archetype, which dungeon backs it, how
    /// hard it is, and what it is called.
    ///
    /// PURE AND SEEDED. Roll(towerSeed, floor) always returns the same answer,
    /// so a floor the player has not reached yet needs no storage at all — it
    /// is recomputed on demand. Only floors that have actually been seen get
    /// written to the save.
    ///
    /// The deliberate consequence: an unvisited floor can change identity if
    /// the modlist changes (a new dungeon def joins the pool). A visited one
    /// never can, because its record is frozen the moment it is first rolled.
    /// </summary>
    public static class TowerLadder
    {
        /// <summary>
        /// Floor to continuous rank. Front-loaded in RANK, back-loaded in
        /// FLOORS — which is what a tower story does and what keeps the mod's
        /// exponential curves climbing at a near-constant rate per floor
        /// (about 1.04x early, 1.015x late) rather than in ten cliffs.
        ///
        /// THE CLIMB STOPS AT SSS. Nation is not a step on this ladder: the
        /// only Nation-ranked thing in the tower is Elijah herself, on the
        /// summit. Everything a climber walks through on the way up is SSS at
        /// its worst.
        ///
        /// Default points (settings: Advanced, tower rank by floor):
        /// floor 1 F, 4 E, 10 D, 25 between C and B, 50 between A and S,
        /// 70 SS, 81 SSS, 100 SSS. SSS from floor 81, and SSS all the way to
        /// the top: pulling the ceiling down from Nation left the old shape with
        /// a two-floor SSS band and twenty-six floors of SS, which is a plateau
        /// rather than a climb; this lands the last stretch where the registers
        /// put the Deep Landings and the Choir.
        ///
        /// A floor's rank is frozen in its record the first time it is rolled,
        /// so changing the curve only moves floors nobody has seen yet.
        /// </summary>
        public static SimpleCurve RankCurve => TowerTunables.RankCurve;

        /// <summary>
        /// Map edge length by floor. Kept modest — a floor is a stage, not a
        /// region. Defaults 150 at floor 1, 180 at 25, 210 at 50, 250 at 100.
        /// </summary>
        public static SimpleCurve SizeCurve => TowerTunables.SizeCurve;

        /// <summary>
        /// World-object IDs for floor map parents, counting DOWN from the top of
        /// the int range. UniqueIDsManager.GetNextWorldObjectID counts UP from
        /// zero, so a collision needs two billion world objects. Pinning the ID
        /// is what makes vanilla's own gen steps reproduce, because a pocket
        /// map's seed is HashCombineInt(worldSeed, parent.ID).
        /// </summary>
        public static int ParentIdFor(int floor) => int.MaxValue - 1024 - floor;

        /// <summary>
        /// Builds the record for a floor. Deterministic in (towerSeed, floor)
        /// given a fixed def database.
        /// </summary>
        public static TowerFloorRecord Roll(int towerSeed, int floor)
        {
            var rec = new TowerFloorRecord
            {
                floor = floor,
                parentId = ParentIdFor(floor),
            };

            Rand.PushState(Gen.HashCombineInt(towerSeed, floor));
            try
            {
                rec.rankIndex = Mathf.Clamp(RankCurve.Evaluate(floor), 0f, 9f);
                rec.rank = (MobRankTier)Mathf.Clamp(Mathf.FloorToInt(rec.rankIndex), 0, 9);
                rec.mapSize = Mathf.RoundToInt(SizeCurve.Evaluate(floor));
                rec.layoutSeed = Rand.Int;
                rec.archetype = PickArchetype(floor);
                rec.kindDef = PickDungeon(rec);
                rec.name = NameFor(rec);
            }
            finally
            {
                Rand.PopState();
            }
            return rec;
        }

        /// <summary>
        /// Forced archetypes win (boss floors sit on a fixed cadence so the
        /// player can count towards them), otherwise a weighted roll among
        /// those whose floor band allows it.
        /// </summary>
        private static TowerFloorArchetypeDef PickArchetype(int floor)
        {
            List<TowerFloorArchetypeDef> all = DefDatabase<TowerFloorArchetypeDef>.AllDefsListForReading;
            if (all.Count == 0)
                return null;

            // An archetype can OWN a stretch of the tower outright, with no
            // roll: the opening floors, which have to be gentle every time
            // rather than on average, and the summit, whose band is exactly one
            // floor. Checked before everything else, including forceEvery — a
            // guardian floor must not land inside the tutorial.
            for (int i = 0; i < all.Count; i++)
            {
                TowerFloorArchetypeDef a = all[i];
                bool ownsBand = a.exclusiveBand || (a.minFloor >= 0 && a.minFloor == a.maxFloor);
                if (ownsBand && InBand(a, floor))
                    return a;
            }

            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].forceEvery > 0 && floor % all[i].forceEvery == 0 && InBand(all[i], floor))
                    return all[i];
            }

            var pool = new List<TowerFloorArchetypeDef>();
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].forceEvery <= 0 && all[i].weight > 0f && InBand(all[i], floor))
                    pool.Add(all[i]);
            }
            if (pool.Count == 0)
                return all[0];
            return pool.RandomElementByWeight(a => a.weight);
        }

        private static bool InBand(TowerFloorArchetypeDef a, int floor) =>
            (a.minFloor < 0 || floor >= a.minFloor) && (a.maxFloor < 0 || floor <= a.maxFloor);

        /// <summary>
        /// Picks the dungeon that backs this floor: one whose rank band
        /// contains the floor's rank and whose layout mode the archetype
        /// allows. Falls back progressively rather than failing — a tower that
        /// cannot roll a floor is worse than one that repeats a dungeon.
        /// </summary>
        private static DungeonKindDef PickDungeon(TowerFloorRecord rec)
        {
            List<DungeonKindDef> all = DefDatabase<DungeonKindDef>.AllDefsListForReading;
            var exactBoth = new List<DungeonKindDef>();   // right rank AND right mode
            var exactRank = new List<DungeonKindDef>();   // right rank, any mode

            for (int i = 0; i < all.Count; i++)
            {
                DungeonKindDef d = all[i];
                // Available is false when requiresFullRoster is set and part of
                // the roster is missing (a sibling mod is not installed). The
                // dungeon selector has always honoured it; the tower did not,
                // so a tower floor could roll a dungeon whose creatures cannot
                // spawn — an empty floor with no boss and no way to clear it.
                // TOWER POOL ONLY. The colony's 67 dungeons are tuned around a
                // single gate with a countdown; a tower floor is one of a
                // hundred with none. Sharing the pool meant every tower change
                // retuned the storyteller's dungeons as a side effect.
                if (!d.towerOnly || !d.Available)
                    continue;
                if (rec.rank < d.minRank || rec.rank > d.maxRank)
                    continue;

                // A set-piece floor is only ever where it was authored. This
                // must be checked BEFORE exactRank, or the rank-only fallback
                // below would happily seat the summit on floor 80.
                bool pinnedHere = rec.archetype != null
                    && rec.archetype.dungeonKinds.Count > 0
                    && rec.archetype.dungeonKinds.Contains(d);
                if (d.pinnedOnly && !pinnedHere)
                    continue;

                exactRank.Add(d);

                // dungeonKinds pins the ENVIRONMENT, which layoutModes cannot:
                // "Open" matches five different dungeons, so without this a
                // village could land on an ash waste or a void plain. When the
                // list is set it is absolute — a floor that must be livable is
                // not livable "usually".
                if (rec.archetype != null && rec.archetype.dungeonKinds.Count > 0)
                {
                    if (rec.archetype.dungeonKinds.Contains(d))
                        exactBoth.Add(d);
                    continue;
                }

                if (rec.archetype == null
                    || rec.archetype.layoutModes.Count == 0
                    || rec.archetype.layoutModes.Contains(d.layoutMode))
                    exactBoth.Add(d);
            }

            if (exactBoth.Count > 0)
                return exactBoth.RandomElementByWeight(d => Mathf.Max(0.01f, d.selectionWeight));

            // RANK BEATS FLAVOUR, ALWAYS. Dropping the archetype's preferred
            // layout gives the player an indoor floor where an open one was
            // wanted — a cosmetic loss. Dropping the rank band instead hands
            // them a dungeon whose ROSTER is from another tier entirely, and no
            // amount of encounter-budget scaling fixes that: an SSS roster on
            // floor 1 is an SSS fight labelled F. That is exactly how floor 1
            // ended up being The Unmade.
            // A pinned archetype does NOT fall back to "any dungeon at this
            // rank" — that is exactly how a market ends up on a void plain.
            // Better to take the pinned kind at the wrong rank than the right
            // rank in a place nobody could live.
            //
            // A pinned kind whose bestiary is missing (Isekai Creatures not
            // installed) is STILL taken, after any available one: the pin is
            // about the place, not the occupants. Its rooms fill from the
            // generic rank-banded providers, and a village floor is peaceful
            // anyway. Skipping it sent markets into carved caves with no
            // village generator.
            if (rec.archetype != null && rec.archetype.dungeonKinds.Count > 0)
            {
                DungeonKindDef pinned = rec.archetype.dungeonKinds.FirstOrDefault(k => k != null && k.Available)
                                        ?? rec.archetype.dungeonKinds.FirstOrDefault(k => k != null);
                if (pinned != null)
                {
                    bool inBand = rec.rank >= pinned.minRank && rec.rank <= pinned.maxRank;
                    if (!inBand)
                        WarnOnce(rec.floor, $"Floor {rec.floor}: no pinned dungeon at rank {rec.rank}; using '{pinned.defName}' out of band.");
                    return pinned;
                }
            }

            if (exactRank.Count > 0)
                return exactRank.RandomElementByWeight(d => Mathf.Max(0.01f, d.selectionWeight));

            // Genuinely nothing at this rank: take the CLOSEST band rather than
            // anything at all, and say so.
            // Among equally close bands, one with the archetype's layout wins:
            // without Isekai Creatures floors 1-3 (rank F) have no F-rank tower
            // dungeon, and def order alone put the carved Threshold entrance on
            // an open forest.
            DungeonKindDef nearest = null;
            int best = int.MaxValue;
            for (int i = 0; i < all.Count; i++)
            {
                DungeonKindDef d = all[i];
                if (!d.towerOnly || !d.Available)
                    continue;
                // Even the last resort must not seat a set-piece floor
                // somewhere it was not authored: this loop rescans everything,
                // so without the check the summit could still land here.
                if (d.pinnedOnly)
                    continue;
                int dist = (int)rec.rank < (int)d.minRank
                    ? (int)d.minRank - (int)rec.rank
                    : (int)rec.rank - (int)d.maxRank;
                bool layoutOk = rec.archetype == null
                    || rec.archetype.layoutModes.Count == 0
                    || rec.archetype.layoutModes.Contains(d.layoutMode);
                int score = dist * 2 + (layoutOk ? 0 : 1);
                if (score < best) { best = score; nearest = d; }
            }
            if (nearest != null)
            {
                WarnOnce(rec.floor, $"No dungeon authored at rank {rec.rank} for floor "
                                  + $"{rec.floor}; using '{nearest.defName}' ({nearest.minRank}-{nearest.maxRank}).");
                return nearest;
            }
            return IsekaiDungeonsDefOf.IsekaiDungeons_BreakDungeon;
        }

        /// <summary>
        /// Floors are rolled from Preview, which the floor picker and the world
        /// menu call every GUI pass, so a fallback warning used to repeat every
        /// frame while a window was open. Once per floor per session is enough.
        /// </summary>
        private static readonly HashSet<int> warnedFloors = new HashSet<int>();

        private static void WarnOnce(int floor, string message)
        {
            if (warnedFloors.Add(floor))
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} {message}");
        }

        /// <summary>
        /// "Floor 23 - Village" — the archetype's own word when it has one,
        /// otherwise the dungeon's label, so a plain crawl reads as the place
        /// it actually is rather than as a category.
        /// </summary>
        private static string NameFor(TowerFloorRecord rec)
        {
            string what = !rec.archetype?.floorLabel.NullOrEmpty() == true
                ? rec.archetype.floorLabel
                : rec.kindDef?.label ?? "unknown";
            return "IsekaiDungeons.Tower.FloorName".Translate(
                rec.floor.Named("FLOOR"),
                what.CapitalizeFirst().Named("WHAT"));
        }
    }
}
