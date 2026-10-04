using System.Collections.Generic;
using IsekaiLeveling.MobRanking;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Drives everything about guild dungeon contracts that needs a heartbeat:
    /// posting them, polling the live ones, and converting the ones nobody took.
    ///
    /// WHY A GAMECOMPONENT AND NOT THE STORYTELLER. Contracts are the shape of a
    /// playthrough rather than an event inside it, and a storyteller roll gives
    /// no control over how many are live at once. Every quest family in this
    /// ecosystem is scheduled the same way for the same reason: the base mod
    /// neuters its own IncidentDef to a baseChance of 0.1 and drives hunts from
    /// IsekaiHuntTracker instead.
    ///
    /// WHY IT POLLS THE QUEST PARTS. QuestPart has no tick of its own. The parts
    /// need a heartbeat to notice the dungeon was cleared and, more importantly,
    /// to keep retrying deferred site teardown, so the component walks them once
    /// a second.
    /// </summary>
    public class DungeonQuestTracker : GameComponent
    {
        /// <summary>Once a second is plenty: nothing here is frame-sensitive.</summary>
        private const int PollInterval = 60;

        private int nextContractTick = -1;

        /// <summary>
        /// The contracts-per-season rate nextContractTick was scheduled with.
        /// When the setting changes, the pending slot is rescheduled at once;
        /// otherwise raising the rate still waited out a slot drawn at the old
        /// one (up to ~39 days at 0.5 per season).
        /// </summary>
        private float scheduledPerSeason = -1f;

        public DungeonQuestTracker(Game game)
        {
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref nextContractTick, "nextContractTick", -1);
            Scribe_Values.Look(ref scheduledPerSeason, "scheduledPerSeason", -1f);
        }

        public override void GameComponentTick()
        {
            if (Find.TickManager.TicksGame % PollInterval != 0)
                return;

            PollContracts();
            MultiFloorRun.PollSpent();
            TickSchedule();
        }

        // ── Polling ─────────────────────────────────────────────────────────

        /// <summary>
        /// Walks every quest looking for our parts.
        ///
        /// ENDED QUESTS ARE STILL POLLED when they have pending site cleanup.
        /// That is the whole decoupling: the contract can be finished, banked
        /// and greyed out in the Quests tab while a caravan is still standing on
        /// the site hauling loot, and the site must not be removed until that
        /// caravan is gone. Skipping ended quests here is exactly how a party
        /// gets stranded.
        /// </summary>
        private static void PollContracts()
        {
            List<Quest> quests = Find.QuestManager?.QuestsListForReading;
            if (quests == null)
                return;

            for (int i = 0; i < quests.Count; i++)
            {
                Quest q = quests[i];
                List<QuestPart> parts = q?.PartsListForReading;
                if (parts == null)
                    continue;

                bool ended = q.State == QuestState.EndedSuccess
                          || q.State == QuestState.EndedFailed
                          || q.State == QuestState.EndedOfferExpired
                          || q.State == QuestState.EndedInvalid;

                for (int j = 0; j < parts.Count; j++)
                {
                    if (!(parts[j] is QuestPart_DungeonContract part))
                        continue;
                    if (ended && !part.HasPendingSiteCleanup)
                        continue;

                    // Isolated per contract. Without this a single throwing
                    // contract aborts the whole walk, silently stopping every
                    // OTHER contract's deferred site teardown, which is exactly
                    // the class of failure that leaves sites orphaned.
                    try
                    {
                        part.PolledTick();
                    }
                    catch (System.Exception e)
                    {
                        Log.WarningOnce(IsekaiDungeonsMod.LogPrefix
                                        + " A dungeon contract threw while polling: " + e,
                                        part.GetHashCode() ^ 0x5D19);
                    }
                }
            }
        }

        // ── Scheduling ──────────────────────────────────────────────────────

        private void TickSchedule()
        {
            IsekaiDungeonsSettings settings = IsekaiDungeonsSettings.Current;
            float perSeason = settings.questsPerSeason;
            if (perSeason <= 0.01f)
                return;
            // Both guild products share this scheduler. With contracts off the
            // cycle still runs for surveys, and with both off it stops.
            if (!settings.guildContractsEnabled && !settings.deepDungeonsEnabled)
                return;
            if (GenDate.DaysPassedFloat < DungeonQuestTuning.FirstQuestDay)
                return;

            if (nextContractTick < 0 || (scheduledPerSeason >= 0f && !Mathf.Approximately(scheduledPerSeason, perSeason)))
            {
                ScheduleNext(perSeason);
                return;
            }
            if (scheduledPerSeason < 0f)
                scheduledPerSeason = perSeason;   // save from before the rate was recorded
            if (Find.TickManager.TicksGame < nextContractTick)
                return;

            // Reschedule FIRST, so an early return below cannot wedge the
            // scheduler on a tick it keeps failing.
            ScheduleNext(perSeason);

            if (CountLiveContracts() >= DungeonQuestTuning.MaxActiveQuests)
                return;

            Map colony = PickColonyMap();
            if (colony == null)
                return;

            // A survey gets first refusal when the roll calls for one, but it
            // never wastes the cycle: if there is nothing left to report, or
            // the planet already holds its limit of them, the guild posts an
            // ordinary contract instead of going quiet for a season.
            // With contracts switched off every cycle is a survey cycle, so the
            // guild still reports deep dungeons at a sensible pace.
            bool surveyRoll = !settings.guildContractsEnabled || Rand.Value < DungeonQuestTuning.ChancePostedDeepReport;
            if (settings.deepDungeonsEnabled && surveyRoll && MultiFloorReports.TryPost(colony))
                return;

            if (settings.guildContractsEnabled)
                DungeonContractFactory.TryPost(colony, RollKind());
        }

        /// <summary>
        /// What kind of work the guild has this time. Weighted toward the
        /// located contract because that is the one with a countdown to beat,
        /// and a countdown is the only version where the player has real
        /// agency. Breaks and rescues are what the world does back.
        /// </summary>
        private static DungeonQuestKind RollKind()
        {
            float r = Rand.Value;
            if (r < DungeonQuestTuning.ChancePostedRescue)
                return DungeonQuestKind.FactionRescue;
            if (r < DungeonQuestTuning.ChancePostedRescue + DungeonQuestTuning.ChancePostedBroken)
                return DungeonQuestKind.Broken;
            return DungeonQuestKind.Located;
        }

        private void ScheduleNext(float perSeason)
        {
            float meanDays = IsekaiDungeonsSettings.DaysPerSeason / Mathf.Max(0.01f, perSeason);
            float days = meanDays * Rand.Range(1f - DungeonQuestTuning.IntervalJitter,
                                               1f + DungeonQuestTuning.IntervalJitter);
            nextContractTick = Find.TickManager.TicksGame + Mathf.Max(1, (int)(days * GenDate.TicksPerDay));
            scheduledPerSeason = perSeason;
        }

        /// <summary>
        /// Live means "still occupying a slot on the world map". A contract that
        /// has ended but is waiting for a caravan to leave does NOT count: its
        /// site is on borrowed time and blocking new work behind it would punish
        /// the player for taking their time hauling loot out.
        /// </summary>
        private static int CountLiveContracts()
        {
            List<Quest> quests = Find.QuestManager?.QuestsListForReading;
            if (quests == null)
                return 0;

            int n = 0;
            for (int i = 0; i < quests.Count; i++)
            {
                Quest q = quests[i];
                if (q == null || q.State == QuestState.EndedSuccess || q.State == QuestState.EndedFailed
                    || q.State == QuestState.EndedOfferExpired || q.State == QuestState.EndedInvalid)
                    continue;
                List<QuestPart> parts = q.PartsListForReading;
                if (parts == null)
                    continue;
                for (int j = 0; j < parts.Count; j++)
                {
                    if (parts[j] is QuestPart_DungeonContract p && !p.Resolved)
                    {
                        n++;
                        break;
                    }
                }
            }
            return n;
        }

        /// <summary>
        /// Why a contract is or is not about to post, in the order TickSchedule
        /// actually checks.
        ///
        /// EVERY GATE HERE IS OTHERWISE SILENT. TickSchedule returns early on
        /// five separate conditions and none of them logs, because none of them
        /// is an error - a full board, a young colony and a disabled slider are
        /// all correct behaviour. The consequence is that "no contracts are
        /// arriving" has five indistinguishable causes and no way to tell them
        /// apart from inside the game, which is exactly the question this
        /// answers.
        /// </summary>
        public string StatusReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("── Dungeon contracts ──");

            float perSeason = IsekaiDungeonsMod.Settings?.questsPerSeason
                              ?? DungeonQuestTuning.DefaultQuestsPerSeason;
            float mean = IsekaiDungeonsSettings.DaysPerSeason / Mathf.Max(0.01f, perSeason);
            sb.AppendLine($"contracts/season  : {perSeason:0.##}"
                        + (perSeason <= 0.01f ? "   <-- OFF, nothing will ever post"
                                              : $"   (mean {mean:0.#} days, +/-{DungeonQuestTuning.IntervalJitter:P0})"));

            float day = GenDate.DaysPassedFloat;
            sb.AppendLine($"day               : {day:0.#} / first at {DungeonQuestTuning.FirstQuestDay:0.#}"
                        + (day < DungeonQuestTuning.FirstQuestDay ? "   <-- too early" : ""));

            if (nextContractTick < 0)
            {
                sb.AppendLine("next contract     : not scheduled yet (schedules on the next poll)");
            }
            else
            {
                float days = (nextContractTick - Find.TickManager.TicksGame) / (float)GenDate.TicksPerDay;
                sb.AppendLine($"next contract     : {(days <= 0f ? "due now" : $"in {days:0.#} days")}");
            }

            int live = CountLiveContracts();
            sb.AppendLine($"live contracts    : {live} / {DungeonQuestTuning.MaxActiveQuests}"
                        + (live >= DungeonQuestTuning.MaxActiveQuests
                           ? "   <-- AT CAP. Nothing new posts until one is cleared."
                           : ""));

            Map colony = PickColonyMap();
            sb.AppendLine($"colony map        : {(colony == null ? "NONE with a spawned free colonist  <-- blocks posting" : "ok")}");

            if (colony != null)
            {
                MobRankTier best = RaidRankSystem.GetColonyBestRank(colony);
                sb.AppendLine($"colony best rank  : {MobRankUtility.GetRankString(best)}"
                            + "   (drives both the contract rank and the local gate window)");
            }
            return sb.ToString();
        }

        private static Map PickColonyMap()
        {
            List<Map> maps = Find.Maps;
            for (int i = 0; i < maps.Count; i++)
            {
                Map m = maps[i];
                if (m != null && m.IsPlayerHome && m.mapPawns.FreeColonistsSpawnedCount > 0)
                    return m;
            }
            return null;
        }
    }
}
