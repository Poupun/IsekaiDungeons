using System.Collections.Generic;
using IsekaiLeveling.MobRanking;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// How a multi-floor dungeon gets onto the planet: the guild reports it.
    ///
    /// WHY A REPORT AND NOT A CONTRACT. A gate contract is work with a
    /// deadline — the gate is counting down, and the whole shape of the quest
    /// is "beat the clock". A multi-floor dungeon has no gate and no break
    /// timer; it is a PLACE, and it will still be there next season. Wrapping
    /// that in quest machinery built around expiry would mean inventing a
    /// deadline the fiction does not have, and then explaining why nothing
    /// happens when it passes.
    ///
    /// So the guild does what a guild would actually do with a hole nobody has
    /// to go down today: it surveys it, marks the map, and posts what its
    /// scouts saw. The reward for going is what is inside, which is the honest
    /// version of the deal and needs no contract to express.
    ///
    /// It still arrives through the guild's own scheduler, alongside the gate
    /// contracts, so it reads as guild work rather than as a thing that
    /// appeared.
    /// </summary>
    public static class MultiFloorReports
    {
        /// <summary>
        /// Surveyed dungeons allowed on the planet at once.
        ///
        /// Low on purpose. These are permanent, so unlike a contract they never
        /// clear themselves off the map, and a world dotted with a dozen
        /// unvisited holes turns a landmark into scenery. A setting (Deep dungeons tab).
        /// </summary>
        public static int MaxOnPlanet => IsekaiDungeonsSettings.Current.maxDeepDungeonsOnPlanet;

        /// <summary>
        /// Posts a survey, or does nothing and says why in the return value.
        ///
        /// Every failure here is a quiet one by design: the guild not having
        /// anything to report this cycle is not news, and the scheduler will
        /// come back around.
        /// </summary>
        public static bool TryPost(Map colonyMap)
        {
            if (colonyMap == null || !IsekaiDungeonsSettings.Current.deepDungeonsEnabled)
                return false;
            if (CountOnPlanet() >= MaxOnPlanet)
                return false;

            MobRankTier colonyRank = RaidRankSystem.GetColonyBestRank(colonyMap);
            MultiFloorDungeonDef plan = PickPlan(colonyRank);
            if (plan == null)
                return false;

            return Place(colonyMap, plan) != null;
        }

        /// <summary>
        /// Marks one specific dungeon on the planet and sends the guild's
        /// letter, skipping every gate TryPost applies (setting, cap, colony
        /// rank, already surveyed). Only the tile search can fail. TryPost's
        /// own tail, public so the dev tools post through the same letter and
        /// tile rules as the scheduler.
        /// </summary>
        public static WorldObject_MultiFloorDungeon Place(Map colonyMap, MultiFloorDungeonDef plan)
        {
            if (colonyMap == null || plan == null)
                return null;

            // Distance is keyed to the dungeon's OPENING rank rather than its
            // deepest. What the scouts found is the mouth of the thing; how bad
            // it gets further down is not a travel consideration, and pushing a
            // ten-floor descent to the far edge of the map because its bottom
            // is Nation-rank would put the shallow floors out of reach too.
            if (!DungeonContractFactory.TryFindContractTile(colonyMap.Tile, plan.minRank, out PlanetTile tile))
                return null;

            var dungeon = (WorldObject_MultiFloorDungeon)WorldObjectMaker.MakeWorldObject(
                IsekaiDungeonsDefOf.IsekaiDungeons_MultiFloorDungeon);
            dungeon.planDef = plan;
            dungeon.Tile = tile;
            Find.WorldObjects.Add(dungeon);
            MultiFloorQuest.Post(dungeon);

            Find.LetterStack.ReceiveLetter(
                "IsekaiDungeons.MultiFloor.Report.Label".Translate(plan.LabelCap.Named("PLACE")),
                "IsekaiDungeons.MultiFloor.Report.Text".Translate(
                    plan.LabelCap.Named("PLACE"),
                    plan.floorCount.Named("FLOORS"),
                    MobRankUtility.GetRankString(plan.minRank).Named("TOPRANK"),
                    MobRankUtility.GetRankString(plan.maxRank).Named("DEEPRANK"),
                    plan.description.Named("WHAT"),
                    MultiFloorRun.BountyFor(plan).ToString("N0").Named("SILVER")),
                LetterDefOf.PositiveEvent,
                dungeon);
            return dungeon;
        }

        /// <summary>
        /// What the guild can plausibly have surveyed, weighted.
        ///
        /// GATED ON THE COLONY'S OWN RANK, not on the dungeon's depth. A survey
        /// is information, and information about a place you cannot survive is
        /// worse than no information — it puts a marker on the map that reads
        /// as content and is a wipe. minColonyRank is each dungeon's statement
        /// of who it is for.
        ///
        /// A def already surveyed is skipped. Two hell holes is not a richer
        /// world, it is the same hole twice.
        /// </summary>
        internal static MultiFloorDungeonDef PickPlan(MobRankTier colonyRank)
        {
            var already = new HashSet<MultiFloorDungeonDef>();
            List<WorldObject> all = Find.WorldObjects?.AllWorldObjects;
            if (all != null)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    if (all[i] is WorldObject_MultiFloorDungeon d && d.planDef != null && !d.completed)
                        already.Add(d.planDef);
                }
            }

            var pool = new List<MultiFloorDungeonDef>();
            List<MultiFloorDungeonDef> defs = DefDatabase<MultiFloorDungeonDef>.AllDefsListForReading;
            for (int i = 0; i < defs.Count; i++)
            {
                MultiFloorDungeonDef d = defs[i];
                if (d.selectionWeight <= 0f || already.Contains(d))
                    continue;
                if (colonyRank < d.minColonyRank)
                    continue;
                if (!d.Playable)
                    continue;
                pool.Add(d);
            }
            if (pool.Count == 0)
                return null;
            return pool.RandomElementByWeightWithFallback(d => Mathf.Max(0.01f, d.selectionWeight));
        }

        public static int CountOnPlanet()
        {
            List<WorldObject> all = Find.WorldObjects?.AllWorldObjects;
            if (all == null)
                return 0;
            int n = 0;
            for (int i = 0; i < all.Count; i++)
            {
                // A conquered one is on its way off the map and must not hold a
                // slot while the party walks out of it.
                if (all[i] is WorldObject_MultiFloorDungeon d && !d.completed)
                    n++;
            }
            return n;
        }

        /// <summary>
        /// Whether a survey is worth rolling at all right now, so the scheduler
        /// can spend the cycle on a gate contract instead of burning it on a
        /// report it cannot post.
        /// </summary>
        public static bool AnythingToReport(Map colonyMap) =>
            colonyMap != null
            && IsekaiDungeonsSettings.Current.deepDungeonsEnabled
            && CountOnPlanet() < MaxOnPlanet
            && PickPlan(RaidRankSystem.GetColonyBestRank(colonyMap)) != null;
    }
}
