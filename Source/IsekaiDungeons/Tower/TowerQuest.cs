using RimWorld;
using RimWorld.Planet;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// The Guild's survey contract on the Tower, from posting to the day it
    /// comes down.
    ///
    /// GUILD-STYLE STATE, VANILLA-STYLE ENTRY. The whole state machine already
    /// exists on TowerComponent — it owns the seed, the floor records, the
    /// summit latch and a tick — so nothing here duplicates it. What was
    /// missing was somewhere for the player to SEE that the contract is still
    /// open across the fifty hours it takes to climb a hundred floors, and a
    /// letter is a poor place to keep something that long.
    ///
    /// So the quest object is deliberately thin: Quest.MakeRaw gives a real
    /// entry in the Quests tab with no QuestGen plumbing behind it, no
    /// QuestScriptDef, and no QuestParts to clean up. It carries a name, a
    /// description and a state, and TowerComponent drives it. quest.root stays
    /// null, which the quest tab already guards for
    /// (MainTabWindow_Quests checks `selected.root != null`).
    ///
    /// THE CONTRACT ENDS WITH THE TOWER. Elijah's death on the summit is the
    /// objective; the tower collapses once the last party is out, and the
    /// collapse letter closes the contract as a success.
    /// </summary>
    public static class TowerQuest
    {
        /// <summary>
        /// Posts the contract. Returns the quest so the caller can hold a
        /// reference for the save.
        /// </summary>
        public static Quest Post()
        {
            try
            {
                Quest quest = Quest.MakeRaw();
                // NEVER LEAVE ROOT NULL: the quest manager discards rootless
                // quests on load, which is how this contract used to vanish
                // from every save and leave TowerComponent pointing at nothing.
                quest.root = IsekaiDungeonsDefOf.IsekaiDungeons_SurveyQuestScript;
                quest.name = "IsekaiDungeons.Quest.Tower.Name".Translate();
                quest.description = "IsekaiDungeons.Quest.Tower.Description".Translate();

                // Accepted on arrival rather than offered. The Guild is not
                // asking whether the player wants the job — the letter is plain
                // that it went to the nearest charter because contracts go to
                // the nearest charter. An offer with a decline button would also
                // need a story for what happens to a tower nobody accepted, and
                // the tower does not care either way; it stands regardless.
                quest.SetInitiallyAccepted();

                Find.QuestManager.Add(quest);
                return quest;
            }
            catch (System.Exception ex)
            {
                // A missing quest entry is cosmetic. The tower itself, its
                // floors and its letters all work without one, so this must
                // never be able to stop the tower rising.
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Could not post the Tower survey contract; "
                          + $"the tower will still rise. {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Re-reads the contract's name and description from the current keys.
        /// A quest stores its text in the save, so without this a contract
        /// posted under older wording (the "Nation-Rank" survey) would keep it
        /// for the rest of the climb.
        /// </summary>
        public static void RefreshText(Quest quest)
        {
            if (quest == null || quest.Historical)
                return;
            quest.name = "IsekaiDungeons.Quest.Tower.Name".Translate();
            quest.description = "IsekaiDungeons.Quest.Tower.Description".Translate();
        }

        /// <summary>
        /// Closes the contract. Silent by design: the sinking letter is the
        /// ending, and vanilla's "quest completed" letter on top of it would
        /// report the same moment twice in two different voices.
        /// </summary>
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
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Could not close the Tower survey contract. "
                          + $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Whether it is safe to bring the tower down.
        ///
        /// THE GROUND MUST WAIT. Destroying a world object whose map is open
        /// strands whoever is standing on it, and a tower floor is a pocket map
        /// that only closes when the party walks out. The letter promises the
        /// tower goes down while they watch from outside, so the code has to
        /// keep it standing until they are. Nobody can walk back in meanwhile, so
        /// the wait always ends.
        /// </summary>
        public static bool SafeToSink() => TowerFloors.LiveFloor == null;
    }
}
