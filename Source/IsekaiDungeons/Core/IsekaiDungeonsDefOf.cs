using RimWorld;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Static def references for everything this add-on ships itself. All defs live
    /// in this mod's own Defs/ folder, so hard [DefOf] resolution is safe — a missing
    /// entry here means the mod's XML is broken and we WANT the loud startup error.
    /// Base-mod item defs (mana cores etc.) are looked up with GetNamedSilentFail at
    /// the point of use instead, following the loot-generation convention.
    /// </summary>
    [DefOf]
    public static class IsekaiDungeonsDefOf
    {
        public static DungeonKindDef IsekaiDungeons_BreakDungeon;

        public static ThingDef IsekaiDungeons_DungeonGate;
        public static ThingDef IsekaiDungeons_DungeonExit;

        /// <summary>Default room door — the fog boundary that makes exploration work.</summary>
        public static ThingDef IsekaiDungeons_DungeonDoor;

        public static WorldObjectDef IsekaiDungeons_DungeonMapParent;
        public static MapGeneratorDef IsekaiDungeons_Dungeon;
        public static IncidentDef IsekaiDungeons_DungeonBreak;

        /// <summary>Marks a boss that escaped with the final breach wave; killing it still pays the clear reward.</summary>
        public static HediffDef IsekaiDungeons_GateBound;

        /// <summary>The physiology of Godhood: ageless, undiseased, deathless-but-for-the-brain, marble-skinned. Paired to the trait by GodhoodBodyUtility.</summary>
        public static HediffDef IsekaiDungeons_GodhoodBody;

        // ── The tower ───────────────────────────────────────────────────────
        /// <summary>The way out of a floor: everyone leaves together as a caravan on the tower's tile.</summary>
        public static ThingDef IsekaiDungeons_TowerExit;
        /// <summary>The white rock every carved tower floor is cut from.</summary>
        public static ThingDef IsekaiDungeons_PaleStone;

        /// <summary>The way up, standing in each floor's boss room.</summary>
        public static ThingDef IsekaiDungeons_TowerStair;
        /// <summary>World object owning one live floor; its ID is pinned so the floor reproduces.</summary>
        public static WorldObjectDef IsekaiDungeons_TowerFloorParent;

        /// <summary>Floor parent for the multi-floor dungeons. See MultiFloorFloors.</summary>
        public static WorldObjectDef IsekaiDungeons_MultiFloorParent;

        /// <summary>The multi-floor dungeon itself, as a place on the planet.</summary>
        public static WorldObjectDef IsekaiDungeons_MultiFloorDungeon;

        /// <summary>The stair onward inside a multi-floor dungeon.</summary>
        public static ThingDef IsekaiDungeons_MultiFloorStair;
        public static ThingDef IsekaiDungeons_MultiFloorExit;

        /// <summary>
        /// Coldreach Deep's and the Hell Hole's climate ramps. Both are the
        /// VANILLA temperature-offset condition; the per-floor value is written
        /// onto the live instance by SpineWorker_Climate, so these defs only
        /// carry the fallback offset and the labelling.
        /// </summary>
        public static GameConditionDef IsekaiDungeons_DeepCold;
        public static GameConditionDef IsekaiDungeons_DeepHeat;
        /// <summary>The tower's own world tile. The only user of the tower icon art.</summary>
        public static WorldObjectDef IsekaiDungeons_Tower;
        /// <summary>The tower's arrival letter: PositiveEvent look, opens with the tower banner.</summary>
        public static LetterDef IsekaiDungeons_TowerRises;

        // ── Guild dungeon contracts ─────────────────────────────────────────

        /// <summary>The world object a posted contract plants on its tile.</summary>
        public static WorldObjectDef IsekaiDungeons_DungeonContractSite;

        /// <summary>Site part that stages the gate (and, for a break, the loose pack) on arrival.</summary>
        public static SitePartDef IsekaiDungeons_DungeonContractPart;

        /// <summary>
        /// Empty script whose only job is to be a non-null Quest.root. RimWorld
        /// throws inside Quest.End and discards the quest on load without one.
        /// </summary>
        public static QuestScriptDef IsekaiDungeons_ContractQuestScript;

        /// <summary>Root for the hand-built surveys (Tower, multi-floor). Same reason as the contract's.</summary>
        public static QuestScriptDef IsekaiDungeons_SurveyQuestScript;

        static IsekaiDungeonsDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(IsekaiDungeonsDefOf));
        }
    }
}
