using System.Collections.Generic;
using System.Text;
using IsekaiLeveling.MobRanking;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// A multi-floor dungeon as a place on the planet.
    ///
    /// Two jobs, and no more: a marker you can caravan to, and the record book
    /// for its own floors.
    ///
    /// THE RECORD BOOK LIVES HERE rather than in a GameComponent, which is the
    /// one real structural difference from the tower. TowerComponent can hold
    /// the tower's hundred floors because there is exactly one tower; there are
    /// many of these, they come and go, and a book that outlived its dungeon
    /// would leak floors nobody can reach. Hanging the records off the world
    /// object means deleting the dungeon deletes its floors, for free.
    /// </summary>
    public class WorldObject_MultiFloorDungeon : WorldObject
    {
        public MultiFloorDungeonDef planDef;

        /// <summary>How deep the party has actually been. 0 until first entry.</summary>
        public int deepestReached;

        /// <summary>
        /// Set when the last floor is beaten. The dungeon stays until the last
        /// party is out, then MultiFloorRun.PollSpent removes it.
        /// </summary>
        public bool completed;

        /// <summary>The guild's survey in the Quests tab. Cosmetic: null is fine.</summary>
        public Quest quest;

        private List<MultiFloorRecord> records = new List<MultiFloorRecord>();

        /// <summary>Scratch state for the spine worker, so a spine needs no storage of its own.</summary>
        public int spineCounter;
        public float spineValue;

        public MultiFloorDungeonDef Def => planDef;

        public IReadOnlyList<MultiFloorRecord> Records => records;

        public override string Label => planDef?.LabelCap ?? base.Label;

        /// <summary>The floor record, created on demand. Ids are derived, never allocated.</summary>
        public MultiFloorRecord EnsureRecord(int floor)
        {
            for (int i = 0; i < records.Count; i++)
            {
                if (records[i].floor == floor)
                    return records[i];
            }
            var rec = new MultiFloorRecord
            {
                floor = floor,
                parentId = MultiFloorIds.For(ID, floor),
                layoutSeed = Gen.HashCombineInt(ID, floor),
            };
            records.Add(rec);
            records.SortBy(r => r.floor);
            return rec;
        }

        public MultiFloorRecord RecordIfSeen(int floor)
        {
            for (int i = 0; i < records.Count; i++)
            {
                if (records[i].floor == floor)
                    return records[i];
            }
            return null;
        }

        /// <summary>The deepest floor the party may walk into: one past the deepest cleared.</summary>
        public int NextOpenFloor
        {
            get
            {
                int f = 1;
                for (int i = 0; i < records.Count; i++)
                {
                    if (records[i].cleared && records[i].floor >= f)
                        f = records[i].floor + 1;
                }
                return Mathf.Clamp(f, 1, planDef?.floorCount ?? 1);
            }
        }

        public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Caravan caravan)
        {
            foreach (FloatMenuOption o in base.GetFloatMenuOptions(caravan))
                yield return o;

            if (planDef == null)
                yield break;

            foreach (FloatMenuOption o in CaravanArrivalAction_EnterMultiFloor.GetFloatMenuOptions(caravan, this))
                yield return o;
        }

        public override string GetInspectString()
        {
            var sb = new StringBuilder();
            string basic = base.GetInspectString();
            if (!basic.NullOrEmpty())
                sb.AppendLine(basic);

            if (planDef == null)
                return sb.ToString().TrimEndNewlines();

            sb.AppendLine("IsekaiDungeons.MultiFloor.Inspect.Depth".Translate(
                planDef.floorCount.Named("TOTAL"),
                MobRankUtility.GetRankString(planDef.minRank).Named("MINRANK"),
                MobRankUtility.GetRankString(planDef.maxRank).Named("MAXRANK")));

            if (completed)
                sb.AppendLine("IsekaiDungeons.MultiFloor.Inspect.Completed".Translate());
            else if (deepestReached > 0)
                sb.AppendLine("IsekaiDungeons.MultiFloor.Inspect.Progress".Translate(
                    deepestReached.Named("DEEPEST"), planDef.floorCount.Named("TOTAL")));

            return sb.ToString().TrimEndNewlines();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Defs.Look(ref planDef, "planDef");
            Scribe_Values.Look(ref deepestReached, "deepestReached", 0);
            Scribe_Values.Look(ref completed, "completed", defaultValue: false);
            Scribe_Values.Look(ref spineCounter, "spineCounter", 0);
            Scribe_Values.Look(ref spineValue, "spineValue", 0f);
            Scribe_Collections.Look(ref records, "records", LookMode.Deep);
            Scribe_References.Look(ref quest, "quest");
            if (Scribe.mode == LoadSaveMode.PostLoadInit && records == null)
                records = new List<MultiFloorRecord>();
        }

        /// <summary>
        /// Tearing the dungeon down takes its floors with it.
        ///
        /// A pocket map whose owner is gone is unreachable and un-deletable by
        /// any ordinary path, so it would tick forever inside the save. The
        /// tower never had to solve this because the tower is never destroyed.
        /// </summary>
        public override void PostRemove()
        {
            MultiFloorFloors.CloseAllFloorsOf(this);
            // Removed before it was beaten (dev tools, or a mod pulling it):
            // the survey has nothing left to point at.
            if (!completed)
                MultiFloorQuest.Close(quest, QuestEndOutcome.Unknown);
            base.PostRemove();
        }
    }
}
