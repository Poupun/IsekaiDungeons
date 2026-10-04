using System;
using System.Collections.Generic;
using IsekaiLeveling.MobRanking;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// What a cleared floor means inside a descent, and how a descent ends.
    ///
    /// A multi-floor floor used to clear exactly like a gate dungeon: a
    /// "Dungeon cleared!" letter, the full clear mood spike, every floor. Four
    /// floors read as four separate dungeons that happened to share stairs,
    /// and the one moment that should have been an ending (the bottom floor)
    /// was indistinguishable from the rest and paid nothing extra.
    ///
    /// So a floor now reads as a floor (its own letter, the small stacking
    /// climb thought the tower uses), and the run has a real ending: the
    /// bottom floor's clear is the conquest, with the full mood spike, a bonus
    /// chest a grade above the floor's own, the guild's bounty, and the quest
    /// closed. Loot per floor is unchanged - every floor is a full crawl and
    /// still pays like one - unless the dungeon's spine withholds it.
    ///
    /// Called from DungeonMapComponent.HandleCleared in two halves around the
    /// reward placement, so the spine gets its say before anything is placed.
    /// </summary>
    public static class MultiFloorRun
    {
        public class Clear
        {
            public WorldObject_MultiFloorDungeon dungeon;
            public MultiFloorDungeonDef plan;
            public int floor;
            public bool last;
            public bool withholdLoot;
            public MultiFloorContext ctx;
        }

        /// <summary>Null for any map that is not a multi-floor floor.</summary>
        public static Clear Begin(Map map)
        {
            if (!(map?.Parent is MultiFloorMapParent p))
                return null;
            WorldObject_MultiFloorDungeon d = p.Owner;
            if (d?.Def == null)
                return null;

            var c = new Clear
            {
                dungeon = d,
                plan = d.Def,
                floor = p.floor,
                last = d.Def.IsLastFloor(p.floor),
                ctx = new MultiFloorContext(d, p.floor, map),
            };

            MultiFloorSpineWorker spine = d.Def.Spine;
            if (spine != null)
            {
                try
                {
                    spine.OnFloorCleared(c.ctx);
                    c.withholdLoot = spine.WithholdsFloorLoot(c.ctx);
                }
                catch (Exception ex)
                {
                    Log.Error($"{IsekaiDungeonsMod.LogPrefix} {d.Def.defName} spine threw on floor clear; "
                            + $"the floor pays normally. {ex}");
                    c.withholdLoot = false;
                }
            }
            return c;
        }

        /// <summary>The floor's letter and mood. The bottom floor is the conquest and says so.</summary>
        public static void Announce(Clear c, List<Pawn> colonists, MobRankTier rank, int xpEach, TargetInfo look)
        {
            if (c.last)
            {
                // The ending gets the full clear spike, like any finished dungeon.
                DungeonMoods.GainClear(colonists, rank);
                return;   // the conquest letter comes from Finish, with the bounty in it
            }

            DungeonMoods.GainFloorCleared(colonists, c.floor, c.plan.floorCount);

            string onward = (c.plan.descends ? "IsekaiDungeons.MultiFloor.WayDown" : "IsekaiDungeons.MultiFloor.WayUp").Translate();
            string text = "IsekaiDungeons.MultiFloor.FloorCleared.Text".Translate(
                c.plan.LabelCap.Named("PLACE"),
                c.floor.Named("FLOOR"),
                c.plan.floorCount.Named("TOTAL"),
                DungeonRankUtil.ColoredRankString(rank).Named("RANK"),
                xpEach.ToString("N0").Named("XP"),
                onward.Named("ONWARD"));
            if (c.withholdLoot)
                text += "\n\n" + "IsekaiDungeons.MultiFloor.FloorCleared.Withheld".Translate();

            Find.LetterStack.ReceiveLetter(
                "IsekaiDungeons.MultiFloor.FloorCleared.Label".Translate(
                    c.floor.Named("FLOOR"), c.plan.floorCount.Named("TOTAL")),
                text, LetterDefOf.PositiveEvent, look);
        }

        /// <summary>
        /// Banks the floor in the record the moment it is won, rather than when
        /// the party walks off it, and runs the ending on the last floor.
        /// </summary>
        public static void Finish(Clear c, DungeonMapComponent comp, IntVec3 lootCenter, MobRankTier rank)
        {
            MultiFloorRecord rec = c.dungeon.EnsureRecord(c.floor);
            rec.cleared = true;
            MultiFloorQuest.Refresh(c.dungeon);

            if (c.last)
                Complete(c.dungeon, c.floor, comp, lootCenter, rank);
        }

        /// <summary>
        /// The run is won. Idempotent: the completed latch is checked first, so
        /// the retire-time fallback in MultiFloorFloors.HarvestRecord can call
        /// this too without paying twice. With no map (that fallback) only the
        /// bookkeeping runs.
        /// </summary>
        public static void Complete(WorldObject_MultiFloorDungeon d, int floor, DungeonMapComponent comp,
                                    IntVec3 lootCenter, MobRankTier rank)
        {
            if (d == null || d.completed || d.Def == null)
                return;
            d.completed = true;
            MultiFloorDungeonDef plan = d.Def;
            Map map = comp?.map;

            string chestLabel = null;
            if (map != null)
            {
                // A chest one grade above the bottom floor's own, on top of the
                // floor's normal haul: the descent is worth more than its last room.
                int tier = Mathf.Min(4, DungeonMapComponent.ChestTierFor(rank) + 1);
                ThingDef chest = comp.PlaceBonusChest(tier, lootCenter);
                chestLabel = chest?.label;
            }

            MultiFloorSpineWorker spine = plan.Spine;
            if (spine != null)
            {
                try
                {
                    spine.OnRunCompleted(new MultiFloorContext(d, floor, map));
                }
                catch (Exception ex)
                {
                    Log.Error($"{IsekaiDungeonsMod.LogPrefix} {plan.defName} spine threw in OnRunCompleted. {ex}");
                }
            }

            int bounty = map != null ? PayBounty(d) : 0;
            GrantGoodwill(d);
            MultiFloorQuest.Close(d.quest, QuestEndOutcome.Success);

            if (map == null)
                return;

            string text = "IsekaiDungeons.MultiFloor.Conquered.Text".Translate(
                plan.LabelCap.Named("PLACE"),
                plan.floorCount.Named("TOTAL"),
                DungeonRankUtil.ColoredRankString(rank).Named("RANK"));
            if (chestLabel != null)
                text += "\n\n" + "IsekaiDungeons.MultiFloor.Conquered.Chest".Translate(
                    Find.ActiveLanguageWorker.WithIndefiniteArticle(chestLabel).Named("CHEST"));
            if (bounty > 0)
                text += "\n\n" + "IsekaiDungeons.MultiFloor.Conquered.Bounty".Translate(bounty.ToString("N0").Named("SILVER"));
            text += "\n\n" + "IsekaiDungeons.MultiFloor.Conquered.Spent".Translate();

            Find.LetterStack.ReceiveLetter(
                "IsekaiDungeons.MultiFloor.Conquered.Label".Translate(plan.LabelCap.Named("PLACE")),
                text, LetterDefOf.PositiveEvent, new TargetInfo(lootCenter, map));
        }

        // ── The guild's side ────────────────────────────────────────────────

        /// <summary>
        /// The guild surveyed it, so the guild pays for it being dealt with: its
        /// contract fee at the bottom floor's rank, plus a quarter again for
        /// every floor past the first. A ten-floor descent is worth far more to
        /// a guild than one room at the same rank, and the fee says so.
        /// </summary>
        public static int BountyFor(MultiFloorDungeonDef plan) =>
            plan == null ? 0
            : Mathf.RoundToInt(DungeonQuestTuning.FeeFor((int)plan.maxRank) * (1f + 0.25f * (plan.floorCount - 1)));

        private static int PayBounty(WorldObject_MultiFloorDungeon d)
        {
            int amount = BountyFor(d.Def);
            Map drop = Find.AnyPlayerHomeMap;
            if (amount <= 0 || drop == null)
                return 0;
            try
            {
                var stacks = new List<Thing>();
                int left = amount;
                while (left > 0)
                {
                    Thing silver = ThingMaker.MakeThing(ThingDefOf.Silver);
                    silver.stackCount = Mathf.Min(left, ThingDefOf.Silver.stackLimit);
                    left -= silver.stackCount;
                    stacks.Add(silver);
                }
                DropPodUtility.DropThingsNear(DropCellFinder.TradeDropSpot(drop), drop, stacks,
                                              canRoofPunch: false, forbid: false);
                return amount;
            }
            catch (Exception ex)
            {
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Could not deliver the deep dungeon bounty: {ex.Message}");
                return 0;
            }
        }

        /// <summary>Double a contract's goodwill: it is the guild's survey, and a bigger job.</summary>
        private static void GrantGoodwill(WorldObject_MultiFloorDungeon d)
        {
            try
            {
                int amount = DungeonQuestTuning.GuildGoodwillFor((int)d.Def.maxRank) * 2;
                if (amount <= 0)
                    return;
                FactionDef def = DefDatabase<FactionDef>.GetNamedSilentFail(DungeonQuestTuning.GuildFactionDefName);
                Faction guild = def == null ? null : Find.FactionManager?.FirstFactionOfDef(def);
                if (guild == null || guild.defeated || guild.HostileTo(Faction.OfPlayer))
                    return;
                guild.TryAffectGoodwillWith(Faction.OfPlayer, amount, canSendMessage: true,
                                            canSendHostilityLetter: false, reason: null, lookTarget: d);
            }
            catch (Exception ex)
            {
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Could not grant guild goodwill: {ex.Message}");
            }
        }

        // ── Spent dungeons leave the map ────────────────────────────────────

        /// <summary>
        /// A conquered dungeon goes quiet once the last party is out of it.
        ///
        /// Before this a finished dungeon stood on the planet forever, still
        /// counting against the cap of surveyed dungeons and still blocking its
        /// own plan from being surveyed again - three conquests and the guild
        /// could never report another one. Leaving destroys the floor maps
        /// anyway, so nothing of value is left behind to come back for.
        /// Polled by DungeonQuestTracker.
        /// </summary>
        public static void PollSpent()
        {
            List<WorldObject> all = Find.WorldObjects?.AllWorldObjects;
            if (all == null)
                return;
            List<WorldObject_MultiFloorDungeon> spent = null;
            for (int i = 0; i < all.Count; i++)
            {
                if (!(all[i] is WorldObject_MultiFloorDungeon d))
                    continue;
                // A dungeon surveyed before the quest entry existed gets one now.
                if (!d.completed && d.quest == null && d.Def != null)
                    MultiFloorQuest.Post(d);
                if (d.completed && MultiFloorFloors.LiveFloorOf(d) == null)
                    (spent ?? (spent = new List<WorldObject_MultiFloorDungeon>())).Add(d);
            }
            if (spent == null)
                return;
            for (int i = 0; i < spent.Count; i++)
            {
                WorldObject_MultiFloorDungeon d = spent[i];
                Messages.Message("IsekaiDungeons.MultiFloor.Spent".Translate(d.Label.Named("PLACE")),
                                 MessageTypeDefOf.NeutralEvent);
                Find.WorldObjects.Remove(d);
            }
        }
    }
}
