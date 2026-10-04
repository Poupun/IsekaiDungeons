using IsekaiLeveling.MobRanking;
using RimWorld;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// The guild's survey of one multi-floor dungeon, as an entry in the
    /// Quests tab.
    ///
    /// Same shape as TowerQuest and for the same reason: the dungeon's own
    /// state already lives on its world object, and a letter is a poor place
    /// to keep something the player may come back to across seasons. The quest
    /// is thin (Quest.MakeRaw, no QuestParts) and the world object drives it:
    /// the description is rewritten as floors are reached and cleared, and the
    /// conquest closes it as a success.
    ///
    /// NO DEADLINE, on purpose. MultiFloorReports explains why a deep dungeon
    /// is a report rather than a contract: it is a place, not a countdown. The
    /// quest says what is there, what the guild pays for clearing it, and how
    /// far the colony has got.
    /// </summary>
    public static class MultiFloorQuest
    {
        public static Quest Post(WorldObject_MultiFloorDungeon d)
        {
            if (d?.Def == null)
                return null;
            try
            {
                Quest quest = Quest.MakeRaw();
                // Rootless quests are discarded on load. See TowerQuest.
                quest.root = IsekaiDungeonsDefOf.IsekaiDungeons_SurveyQuestScript;
                quest.SetInitiallyAccepted();
                Find.QuestManager.Add(quest);
                d.quest = quest;
                Refresh(d);
                return quest;
            }
            catch (System.Exception ex)
            {
                // Cosmetic: the dungeon works without its entry.
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Could not post the deep dungeon survey quest. "
                          + $"{ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>Rewrites name and description from the live state and the current keys.</summary>
        public static void Refresh(WorldObject_MultiFloorDungeon d)
        {
            Quest quest = d?.quest;
            MultiFloorDungeonDef plan = d?.Def;
            if (quest == null || plan == null || quest.Historical)
                return;

            int cleared = 0;
            for (int f = 1; f <= plan.floorCount; f++)
            {
                if (d.RecordIfSeen(f)?.cleared ?? false)
                    cleared++;
            }

            quest.name = "IsekaiDungeons.Quest.Deep.Name".Translate(plan.LabelCap.Named("PLACE"));
            string desc = "IsekaiDungeons.Quest.Deep.Description".Translate(
                plan.LabelCap.Named("PLACE"),
                plan.description.Named("WHAT"),
                plan.floorCount.Named("TOTAL"),
                MobRankUtility.GetRankString(plan.minRank).Named("TOPRANK"),
                MobRankUtility.GetRankString(plan.maxRank).Named("DEEPRANK"),
                MultiFloorRun.BountyFor(plan).ToString("N0").Named("SILVER"));
            desc += "\n\n" + (d.deepestReached <= 0
                ? "IsekaiDungeons.Quest.Deep.Unvisited".Translate().ToString()
                : "IsekaiDungeons.Quest.Deep.Progress".Translate(
                    cleared.Named("CLEARED"), plan.floorCount.Named("TOTAL"),
                    d.deepestReached.Named("DEEPEST")).ToString());
            quest.description = desc;
        }

        /// <summary>Silent close: the conquest letter is the ending, and saying it twice is noise.</summary>
        public static void Close(Quest quest, QuestEndOutcome outcome)
        {
            if (quest == null || quest.Historical || quest.State != QuestState.Ongoing)
                return;
            try
            {
                quest.End(outcome, sendLetter: false, playSound: outcome == QuestEndOutcome.Success);
            }
            catch (System.Exception ex)
            {
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Could not close the deep dungeon survey quest. "
                          + $"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
