using System.Collections.Generic;
using Set = IsekaiDungeons.IsekaiDungeonsSettings;

namespace IsekaiDungeons
{
    /// <summary>
    /// A named starting point: every setting back to default, then a short
    /// list of overrides. Presets never stack. Applying one always starts from
    /// defaults, so what a preset does is exactly what its list says.
    /// </summary>
    public sealed class SettingsPreset
    {
        public readonly string id;
        public readonly Dictionary<string, object> overrides;

        public SettingsPreset(string id, Dictionary<string, object> overrides)
        {
            this.id = id;
            this.overrides = overrides;
        }

        public string LabelKey => "IsekaiDungeons.Settings.Preset." + id;
        public string DescKey => LabelKey + ".Desc";
    }

    public static class SettingsPresets
    {
        public static readonly List<SettingsPreset> All = new List<SettingsPreset>
        {
            new SettingsPreset("Default", new Dictionary<string, object>()),

            new SettingsPreset("Relaxed", new Dictionary<string, object>
            {
                { nameof(Set.gatesPerSeason), 0.5f },
                { nameof(Set.breakTimerMultiplier), 1.5f },
                { nameof(Set.questsPerSeason), 1.5f },
                { nameof(Set.daysBeforeBreak), 18f },
                { nameof(Set.brokenSiteDays), 14f },
                { nameof(Set.globalEncounterMultiplier), 0.75f },
                { nameof(Set.maxCreaturesPerRoom), 24 },
                { nameof(Set.bossMinionsMax), 3 },
                { nameof(Set.eventRoomFraction), 0.25f },
                { nameof(Set.globalLootMultiplier), 1.15f },
            }),

            new SettingsPreset("Challenging", new Dictionary<string, object>
            {
                { nameof(Set.gatesPerSeason), 1.5f },
                { nameof(Set.maxActiveGatesPerMap), 3 },
                { nameof(Set.breakTimerMultiplier), 0.8f },
                { nameof(Set.questsPerSeason), 2.5f },
                { nameof(Set.daysBeforeBreak), 10f },
                { nameof(Set.globalEncounterMultiplier), 1.3f },
                { nameof(Set.roomCountMultiplier), 1.15f },
                { nameof(Set.eventRoomFraction), 0.4f },
                { nameof(Set.bossMinionsMin), 3 },
                { nameof(Set.bossMinionsMax), 5 },
                { nameof(Set.localRankShift), 1 },
                { nameof(Set.globalLootMultiplier), 1.15f },
            }),

            new SettingsPreset("Brutal", new Dictionary<string, object>
            {
                { nameof(Set.gatesPerSeason), 3f },
                { nameof(Set.maxActiveGatesPerMap), 4 },
                { nameof(Set.breakTimerMultiplier), 0.5f },
                { nameof(Set.breachWaveIntervalHours), 12f },
                { nameof(Set.questsPerSeason), 4f },
                { nameof(Set.maxActiveQuests), 3 },
                { nameof(Set.daysBeforeBreak), 7f },
                { nameof(Set.brokenSiteDays), 7f },
                { nameof(Set.globalEncounterMultiplier), 1.8f },
                { nameof(Set.roomCountMultiplier), 1.3f },
                { nameof(Set.maxCreaturesPerRoom), 45 },
                { nameof(Set.bossMinionsMin), 4 },
                { nameof(Set.bossMinionsMax), 7 },
                { nameof(Set.eventRoomFraction), 0.5f },
                { nameof(Set.roomTierRampDepth), 1 },
                { nameof(Set.localRankShift), 1 },
                { nameof(Set.contractRankShift), 1 },
                { nameof(Set.globalLootMultiplier), 1.3f },
                { nameof(Set.clearXpMultiplier), 2.25f },
            }),

            new SettingsPreset("TreasureHunter", new Dictionary<string, object>
            {
                { nameof(Set.globalLootMultiplier), 1.75f },
                { nameof(Set.treasureRoomsShift), 1 },
                { nameof(Set.extraChestChanceMultiplier), 1.5f },
                { nameof(Set.chestEpicWeight), 1.5f },
                { nameof(Set.chestLegendaryWeight), 2f },
                { nameof(Set.beastVaultChanceAtMinRank), 0.15f },
                { nameof(Set.beastVaultChanceAtMaxRank), 0.35f },
                { nameof(Set.globalEncounterMultiplier), 1.2f },
            }),

            new SettingsPreset("TowerOnly", new Dictionary<string, object>
            {
                { nameof(Set.localGatesEnabled), false },
                { nameof(Set.guildContractsEnabled), false },
                { nameof(Set.deepDungeonsEnabled), false },
            }),
        };

        /// <summary>Defaults first, then the preset's list. Window preferences survive.</summary>
        public static void Apply(Set s, SettingsPreset preset)
        {
            bool descriptions = s.showDescriptions;
            s.ResetAll();
            foreach (KeyValuePair<string, object> kv in preset.overrides)
                SettingsRegistry.Find(kv.Key)?.SetFromObject(s, kv.Value);
            s.showDescriptions = descriptions;
            s.Normalize();
            s.MarkChanged();
        }
    }
}
