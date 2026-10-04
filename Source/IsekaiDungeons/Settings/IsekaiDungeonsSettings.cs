using System;
using System.Globalization;
using IsekaiLeveling.MobRanking;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Player-facing tuning: every number the add-on is balanced on.
    ///
    /// THE FIELDS ARE THE SETTINGS. A field's name is its save key, its label
    /// key (IsekaiDungeons.Settings.FieldName) and the name the registry lists
    /// it under, so a setting is declared exactly twice: here with its
    /// default, and once in <see cref="SettingsRegistry"/> with its tab, range
    /// and unit. Saving, loading, clamping, drawing, search, resets and presets
    /// all walk the registry.
    ///
    /// EVERY DEFAULT IS THE VALUE THE GAME SHIPPED WITH before this window
    /// existed, and gameplay code still reads the tunables classes
    /// (DungeonTunables, DungeonQuestTuning, TowerTunables), which now forward
    /// here. The design notes explaining WHY each default is what it is stay
    /// on those classes, next to the property that exposes it.
    ///
    /// The three settings that predate this window (gatesPerSeason,
    /// questsPerSeason, godhoodChangesAppearance) kept their field names, so
    /// existing config files load unchanged.
    /// </summary>
    public class IsekaiDungeonsSettings : ModSettings
    {
        /// <summary>The live settings, or the defaults before the mod constructor has run.</summary>
        public static IsekaiDungeonsSettings Current => IsekaiDungeonsMod.Settings ?? Defaults;

        /// <summary>A pristine instance: what every reset goes back to and what "changed" is measured against.</summary>
        public static readonly IsekaiDungeonsSettings Defaults = new IsekaiDungeonsSettings();

        /// <summary>Days in a RimWorld quadrum. The unit the rate sliders are expressed in.</summary>
        public const float DaysPerSeason = 15f;

        /// <summary>
        /// Bumped whenever any value changes, so derived data (the curves built
        /// from tables) can cache and know when to rebuild. Not saved.
        /// </summary>
        public int Revision { get; private set; }

        public void MarkChanged() => Revision++;

        // ═══ General ═══════════════════════════════════════════════════════

        public bool localGatesEnabled = true;
        public bool guildContractsEnabled = true;
        public bool towerEnabled = true;
        public bool deepDungeonsEnabled = true;

        public float globalEncounterMultiplier = 1f;
        public float globalLootMultiplier = 1f;
        public float globalXpMultiplier = 1f;

        /// <summary>Window preference: descriptions under every row.</summary>
        public bool showDescriptions = true;

        // ═══ Local gates ═══════════════════════════════════════════════════

        /// <summary>
        /// LOCAL dungeon gates opened per season. ONE by default: local gates
        /// are the quiet source, always well below the colony's rank, and the
        /// guild contracts are the loud one. Zero switches scheduled gates off.
        /// </summary>
        public float gatesPerSeason = 1f;
        public float firstGateDay = 3f;
        public float gateIntervalJitter = 0.4f;
        public int maxActiveGatesPerMap = 2;
        public int localRankShift = 0;
        public int localRankWindowSpan = 2;
        public float breakTimerMultiplier = 1f;
        public float warnHalfFraction = 0.5f;
        public float finalWarningHours = 24f;
        public bool gateCountdownLetters = true;
        public float breachWaveIntervalHours = 24f;
        public bool breachWaveLetters = true;
        public float breachXpRadius = 32f;
        public float clearedCollapseHours = 2f;
        public float ejectStunSeconds = 5f;

        // ═══ Guild contracts ═══════════════════════════════════════════════

        /// <summary>
        /// Guild dungeon CONTRACTS posted per season. Separate from the local
        /// gate rate on purpose: a player who wants a calm map and a busy world
        /// sets the first low and this high.
        /// </summary>
        public float questsPerSeason = 2f;
        public float firstQuestDay = 6f;
        public float questIntervalJitter = 0.35f;
        public int maxActiveQuests = 2;
        public int contractRankShift = 0;
        public float ceilingChanceSolo = 0.14f;
        public float ceilingChanceNormal = 0.20f;
        public float ceilingChanceDeep = 0.26f;
        public float atCeilingShare = 0.60f;
        public int deepBenchCount = 5;
        public float chancePostedBroken = 0.20f;
        public float chancePostedRescue = 0.20f;
        public float contractDistanceMultiplier = 1f;
        public float daysBeforeBreak = 12f;
        public float brokenSiteDays = 10f;
        public float contractFeeMultiplier = 1f;
        public float brokenFeeMultiplier = 1.6f;
        public int rescueGoodwill = 18;
        public int rescueFailureGoodwill = -12;
        public float brokenSurfaceShare = 0.45f;
        public int brokenScatterRadius = 22;
        public float defenderRingRadius = 14f;

        // ═══ Dungeons ══════════════════════════════════════════════════════

        public float mapSizeMultiplier = 1f;
        public float roomCountMultiplier = 1f;
        public int treasureRoomsShift = 0;
        public float shortcutMultiplier = 1f;
        public int wingLengthShift = 0;
        public int maxWingRoomsPerDungeon = 8;

        public float firstRoomBudgetFactor = 0.6f;
        public float lastRoomBudgetFactor = 1.4f;
        public float offPathBudgetFactor = 0.85f;
        public float championBudgetFactor = 1.15f;
        public int roomTierRampDepth = 2;
        public int maxCreaturesPerRoom = 30;
        public int bossMinionsMin = 2;
        public int bossMinionsMax = 4;
        public float leashSlackCells = 8f;

        public float eventRoomFraction = 0.34f;
        public int minPlainRooms = 1;

        public MobRankTier beastVaultMinRank = MobRankTier.D;
        public float beastVaultChanceAtMinRank = 0.08f;
        public float beastVaultChanceAtMaxRank = 0.22f;

        public bool dungeonDreadEnabled = true;

        public bool decorEnabled = true;
        public float decorDensityMultiplier = 1f;
        public float decorEntryFactor = 0.6f;
        public float decorCombatFactor = 1f;
        public float decorTreasureFactor = 1.1f;
        public float decorBossFactor = 1.8f;
        public float decorDepthStart = 0.75f;
        public float decorDepthEnd = 1.25f;
        public bool decorRefineFloors = true;
        public bool decorSmoothWalls = true;
        public bool decorCorridorTorches = true;
        public float corridorTorchSpacingMultiplier = 1f;
        public float decorFireSpacing = 3.5f;
        public float decorPlantFireDistance = 12f;
        public int decorAttemptsPerRoom = 60;

        // ═══ Rewards ═══════════════════════════════════════════════════════

        public float treasureLootFraction = 0.22f;
        public float wingLootBonusPerRoom = 0.35f;
        public float gearShare = 0.55f;
        public float coinShare = 0.12f;
        public float relicShare = 0.20f;
        public float maxRelicShare = 0.40f;
        public float elixirChance = 0.35f;
        public float treasureGearChance = 0.35f;
        public float gateScrollClearChance = 0.25f;
        public float gateScrollTreasureChance = 0.10f;
        public float gateScrollQuestChance = 0.15f;
        public float extraChestChanceMultiplier = 1f;
        public float chestCommonWeight = 1f;
        public float chestRareWeight = 1f;
        public float chestEpicWeight = 1f;
        public float chestLegendaryWeight = 1f;
        public bool chestHaulMessage = true;
        public float clearXpMultiplier = 1.75f;

        // ═══ Tower ═════════════════════════════════════════════════════════

        public float towerRaiseAfterSeconds = 10f;
        public float towerEncounterMultiplier = 1f;
        public float towerLootMultiplier = 1f;
        public float towerXpMultiplier = 1f;
        public int towerVillagersMin = 4;
        public int towerVillagersMax = 7;

        /// <summary>
        /// Whether ascending to godhood repaints the pawn (marble skin, white
        /// hair). OFF IS A FULL UNDO: the body comp restores the captured
        /// original colours, so turning it off mid-save gives colonists their
        /// own look back rather than only affecting future ascensions.
        /// </summary>
        public bool godhoodChangesAppearance = true;
        public bool godhoodHaloEnabled = true;

        public float autopilotFoodPauseBelow = 0.25f;
        public float autopilotRestPauseBelow = 0.12f;
        public float autopilotFoodResumeAt = 0.85f;
        public float autopilotRestResumeAt = 0.85f;
        public float autopilotRangedHoldFraction = 0.9f;
        public float autopilotIdleTimeoutHours = 1f;
        public float autopilotSnoozeHours = 6f;
        public float autopilotDeferSeconds = 3f;

        // ═══ Deep dungeons ═════════════════════════════════════════════════

        public float chancePostedDeepReport = 0.15f;
        public int maxDeepDungeonsOnPlanet = 3;
        public float deepEncounterMultiplier = 1f;
        public float deepLootMultiplier = 1f;
        public float deepXpMultiplier = 1f;
        public int risingDeadMax = 12;
        public int retreatMax = 14;
        public float regrowthMinShare = 0.20f;
        public float regrowthMaxShare = 0.60f;
        public float regrowthFullAtDepth = 3f;
        public float alertPerFloor = 0.05f;
        public float alertPerDay = 0.10f;
        public float alertMaxShare = 0.75f;
        public float coldFirstFloorTemp = 0f;
        public float coldDeepestTemp = -45f;
        public float heatFirstFloorTemp = 28f;
        public float heatDeepestTemp = 58f;

        // ═══ Interface ═════════════════════════════════════════════════════

        public bool titleCardEnabled = true;
        public bool gateLabelsEnabled = true;
        public float gateLabelScale = 1f;
        public float gateEffectsStrength = 1f;
        public float reelSpinSeconds = 6.4f;
        public bool dungeonMusicEnabled = true;
        public float musicFadeOutSeconds = 2.5f;
        public float musicGapSeconds = 1.2f;
        public float musicFadeInSeconds = 3f;
        public int towerMusicSplitFloor = 50;

        // ═══ Advanced: rank tables (index = (int)MobRankTier, F..Nation) ═══

        public int[] localRankTopByColonyRank = { 0, 1, 2, 2, 3, 4, 5, 5, 6, 7 };
        public int[] contractCentreOffsetByBestRank = { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 };
        public int[] contractMinTilesByRank = { 1, 1, 2, 2, 3, 3, 4, 4, 5, 6 };
        public int[] contractMaxTilesByRank = { 2, 3, 3, 4, 5, 6, 7, 8, 8, 8 };
        public int[] contractSilverByRank = { 150, 250, 400, 700, 1200, 2000, 3200, 5000, 8000, 13000 };
        /// <summary>Same scale GuildFactionAddon pays for an Isekai hunt of the same tier, so a contract and a hunt feel equal.</summary>
        public int[] contractGuildGoodwillByRank = { 3, 3, 5, 5, 8, 8, 12, 12, 15, 15 };
        public int[] clearXpBaseByRank = { 200, 500, 1250, 3000, 8000, 20000, 100000, 360000, 1500000, 3000000 };
        public int[] shortcutBudgetByRank = { 0, 0, 1, 1, 2, 2, 3, 3, 4, 4 };
        public int[] maxWingLengthByRank = { 1, 1, 2, 2, 3, 3, 3, 4, 4, 4 };
        public float[] relicDrawsByRank = { 0f, 0f, 0.5f, 0.8f, 1.1f, 1.4f, 1.8f, 2.3f, 2.9f, 3.6f };
        public int[] geneCountByRank = { 1, 1, 2, 2, 3, 3, 4, 4, 5, 6 };

        // ═══ Advanced: curves ══════════════════════════════════════════════

        /// <summary>Decor points at rank index 0, 2, 4, 6, 8 and 9 (see <see cref="SettingsRegistry.DecorCurveRanks"/>).</summary>
        public float[] decorPointsByRank = { 1.5f, 4f, 7f, 11f, 15f, 18f };

        /// <summary>Continuous rank at floors 1, 4, 10, 25, 50, 70, 81 and 100 (see <see cref="SettingsRegistry.TowerRankFloors"/>).</summary>
        public float[] towerRankByFloor = { 0f, 1f, 2f, 3.5f, 5.5f, 7f, 8f, 8f };

        /// <summary>Map edge length at floors 1, 25, 50 and 100 (see <see cref="SettingsRegistry.TowerSizeFloors"/>).</summary>
        public float[] towerMapSizeByFloor = { 150f, 180f, 210f, 250f };

        /// <summary>Cells between corridor torches by decor register (Hole..Wonder). Zero means none.</summary>
        public int[] corridorTorchSpacingByRegister = { 0, 0, 16, 12, 9 };

        // ═══ Advanced: chest tables ════════════════════════════════════════

        /// <summary>Extra chest rolls below the headline, one step down the grade ladder per entry.</summary>
        public float[] extraChestChancesBronze = { 0.20f };
        public float[] extraChestChancesSilver = { 0.45f };
        public float[] extraChestChancesGold = { 0.55f, 0.30f };
        public float[] extraChestChancesJade = { 0.65f, 0.40f, 0.20f };

        /// <summary>Common / Rare / Epic / Legendary weights per chest grade.</summary>
        public float[] rarityWeightsBronze = { 33f, 36f, 23f, 8f };
        public float[] rarityWeightsSilver = { 22f, 33f, 30f, 15f };
        public float[] rarityWeightsGold = { 12f, 26f, 37f, 25f };
        public float[] rarityWeightsJade = { 4f, 16f, 38f, 42f };

        // ═══ Advanced: breach ══════════════════════════════════════════════

        /// <summary>Share of the REMAINING population each wave takes. The last wave always takes everything.</summary>
        public float[] breachWaveShares = { 0.25f, 0.35f, 1f };

        // ═══ Advanced: internals ═══════════════════════════════════════════

        public int maxShortcutSkip = 2;
        public int maxShortcutSkipHighRank = 3;
        public float wingDepthStepFrac = 1f;
        public float openPackingEfficiency = 0.45f;
        public float decorReferenceArea = 110f;
        public float decorAreaFactorMin = 0.6f;
        public float decorAreaFactorMax = 3f;
        public int beastVaultPickAttempts = 6;
        public int rosterRankMargin = 1;
        public int rosterRankFloorMargin = 2;
        public float backboneWeightFactor = 0.35f;
        public float lootFallbackMultiplier = 2.7f;
        public float relicSoftFloorShare = 0.08f;
        public int gateStateIntervalTicks = 250;
        public int mapStateIntervalTicks = 60;
        public bool suppressDungeonWildlife = true;
        public bool logGenerationSummaries = true;

        // ═══ Derived ═══════════════════════════════════════════════════════

        /// <summary>Mean days between scheduled local gates, or -1 when scheduling is off.</summary>
        public float MeanDaysBetweenGates =>
            gatesPerSeason <= 0.01f ? -1f : DaysPerSeason / gatesPerSeason;

        // ═══ Saving and loading ════════════════════════════════════════════

        public override void ExposeData()
        {
            base.ExposeData();
            foreach (SettingEntry entry in SettingsRegistry.All)
                entry.Scribe(this);

            // A window preference, not a setting: no tab, no reset, no preset.
            Scribe_Values.Look(ref showDescriptions, "showDescriptions", true);

            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                Normalize();
                MarkChanged();
            }
        }

        /// <summary>
        /// Puts every value back inside its slider range and every table back
        /// to its proper length, then repairs the pairs that must stay ordered.
        /// A hand-edited config is the only way to reach a bad state, and a
        /// bad state here would otherwise reach gameplay code as, say, a boss
        /// entourage of min 5 max 2.
        /// </summary>
        public void Normalize()
        {
            foreach (SettingEntry entry in SettingsRegistry.All)
                entry.Clamp(this);

            if (bossMinionsMax < bossMinionsMin) bossMinionsMax = bossMinionsMin;
            if (towerVillagersMax < towerVillagersMin) towerVillagersMax = towerVillagersMin;
            if (regrowthMaxShare < regrowthMinShare) regrowthMaxShare = regrowthMinShare;
            if (decorAreaFactorMax < decorAreaFactorMin) decorAreaFactorMax = decorAreaFactorMin;
            if (maxShortcutSkipHighRank < maxShortcutSkip) maxShortcutSkipHighRank = maxShortcutSkip;

            // A resume threshold at or under its pause threshold resumes the
            // instant it pauses, and the autopilot thrashes between the two.
            autopilotFoodResumeAt = Mathf.Max(autopilotFoodResumeAt, Mathf.Min(1f, autopilotFoodPauseBelow + 0.05f));
            autopilotRestResumeAt = Mathf.Max(autopilotRestResumeAt, Mathf.Min(1f, autopilotRestPauseBelow + 0.05f));

            for (int i = 0; i < contractMaxTilesByRank.Length && i < contractMinTilesByRank.Length; i++)
            {
                if (contractMaxTilesByRank[i] < contractMinTilesByRank[i])
                    contractMaxTilesByRank[i] = contractMinTilesByRank[i];
            }
        }

        /// <summary>Every setting back to its default.</summary>
        public void ResetAll()
        {
            foreach (SettingEntry entry in SettingsRegistry.All)
                entry.Reset(this);
            MarkChanged();
        }

        /// <summary>Every setting in one tab back to its default.</summary>
        public void ResetTab(SettingsTab tab)
        {
            foreach (SettingEntry entry in SettingsRegistry.All)
            {
                if (entry.tab == tab)
                    entry.Reset(this);
            }
            Normalize();
            MarkChanged();
        }

        // ── Table text format ──
        // Tables save as one comma-separated line in the invariant culture, so
        // a config written on a French machine loads on an English one.

        internal static string Join(int[] values) =>
            values == null ? "" : string.Join(",", Array.ConvertAll(values, v => v.ToString(CultureInfo.InvariantCulture)));

        internal static string Join(float[] values) =>
            values == null ? "" : string.Join(",", Array.ConvertAll(values, v => v.ToString("R", CultureInfo.InvariantCulture)));

        internal static int[] ParseInts(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;
            string[] parts = text.Split(',');
            var result = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result[i]))
                    return null;
            }
            return result;
        }

        internal static float[] ParseFloats(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;
            string[] parts = text.Split(',');
            var result = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result[i]))
                    return null;
            }
            return result;
        }
    }
}
