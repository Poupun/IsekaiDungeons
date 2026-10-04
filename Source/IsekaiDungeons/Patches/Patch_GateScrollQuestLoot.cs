using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using IsekaiLeveling.MobRanking;
using IsekaiLeveling.Quests;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Gate summoning scrolls as quest rewards: base-mod hunts and bounties.
    ///
    /// GenerateLootRewards builds the Thing list that becomes BOTH the quest
    /// part's deep-saved loot and the choice's Reward_Items (shared
    /// references), so adding to the returned list is enough for the scroll
    /// to show on the offer and be delivered. No extension point exists on
    /// the base mod's side for this, hence a patch.
    /// </summary>
    [HarmonyPatch(typeof(IncidentWorker_IsekaiHunt), "GenerateLootRewards")]
    public static class Patch_GateScrollHuntLoot
    {
        [HarmonyPostfix]
        public static void Postfix(List<Thing> __result, QuestRank rank)
        {
            // QuestRank F..SSS shares MobRankTier's indices.
            GateScrolls.TryAdd(__result, (MobRankTier)(int)rank,
                IsekaiDungeonsSettings.Current.gateScrollQuestChance);
        }
    }

    /// <summary>
    /// Same, for Isekai Creatures quests (IsekaiQuestNarrative.GenerateLoot,
    /// tier as a rank string "F".."Nation"). Soft dependency: Prepare skips
    /// the patch entirely when Creatures is not loaded.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_GateScrollCreatureQuestLoot
    {
        private static MethodBase target;

        public static bool Prepare()
        {
            Type t = AccessTools.TypeByName("IsekaiCreatures.Quests.IsekaiQuestNarrative");
            target = t == null ? null : AccessTools.Method(t, "GenerateLoot", new[] { typeof(string) });
            return target != null;
        }

        public static MethodBase TargetMethod() => target;

        [HarmonyPostfix]
        public static void Postfix(List<Thing> __result, string tier)
        {
            if (string.IsNullOrEmpty(tier) || !Enum.TryParse(tier, out MobRankTier rank))
                return;
            GateScrolls.TryAdd(__result, rank, IsekaiDungeonsSettings.Current.gateScrollQuestChance);
        }
    }
}
