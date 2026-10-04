using System;
using System.Collections.Generic;
using System.Reflection;
using IsekaiLeveling.MobRanking;
using UnityEngine;
using Verse;
using Set = IsekaiDungeons.IsekaiDungeonsSettings;

namespace IsekaiDungeons
{
    public enum SettingsTab { General, Gates, Contracts, Dungeons, Rewards, Tower, Deep, Interface, Advanced }

    public enum SettingKind { Toggle, Float, Int, Rank, IntTable, FloatTable }

    /// <summary>How a value reads in the window. Storage is always the raw number.</summary>
    public enum SettingUnit
    {
        Plain, Signed, Multiplier, Percent,
        /// <summary>A duration in days.</summary>
        Days,
        /// <summary>A calendar day of the playthrough.</summary>
        Day,
        Hours, Seconds, Cells, Temperature, Silver, PerSeason, Floor
    }

    /// <summary>What the columns of a table stand for.</summary>
    public enum TableColumns { Ranks, DecorCurveRanks, TowerRankFloors, TowerSizeFloors, Registers, Rarities, ExtraRolls, Waves }

    /// <summary>
    /// One setting: a field on <see cref="IsekaiDungeonsSettings"/> plus
    /// everything the window, the scribe and the clamp need to know about it.
    /// Built only by <see cref="SettingsRegistry"/>.
    /// </summary>
    public sealed class SettingEntry
    {
        public readonly string field;
        public readonly SettingsTab tab;
        public readonly string section;
        public readonly SettingKind kind;
        public readonly FieldInfo info;

        public float min;
        public float max;
        public float step = -1f;
        public SettingUnit unit;
        public string format = "0.##";
        public bool zeroIsOff;
        public TableColumns columns;
        public int minLength;
        public int maxLength;

        /// <summary>Chest grade (1 bronze .. 4 jade) an extra-roll table belongs to, for its column labels.</summary>
        public int tier;

        internal SettingEntry(string field, SettingsTab tab, string section, SettingKind kind)
        {
            this.field = field;
            this.tab = tab;
            this.section = section;
            this.kind = kind;
            info = typeof(Set).GetField(field, BindingFlags.Public | BindingFlags.Instance);
            if (info == null)
                throw new ArgumentException($"IsekaiDungeonsSettings has no public field '{field}'.");
        }

        public string LabelKey => "IsekaiDungeons.Settings." + char.ToUpperInvariant(field[0]) + field.Substring(1);
        public string DescKey => LabelKey + ".Desc";
        public string SectionKey => "IsekaiDungeons.Settings.Section." + section;

        public bool IsTable => kind == SettingKind.IntTable || kind == SettingKind.FloatTable;
        public bool FixedLength => minLength == maxLength;

        /// <summary>"Off" makes this entry chainable in the registry: zero reads as Off.</summary>
        public SettingEntry Off()
        {
            zeroIsOff = true;
            return this;
        }

        public SettingEntry Format(string f)
        {
            format = f;
            return this;
        }

        public SettingEntry Tier(int grade)
        {
            tier = grade;
            return this;
        }

        // ── Values ──

        public bool GetBool(Set s) => (bool)info.GetValue(s);
        public void SetBool(Set s, bool v) => info.SetValue(s, v);

        public float GetFloat(Set s)
        {
            object o = info.GetValue(s);
            if (o is int i) return i;
            if (o is MobRankTier r) return (int)r;
            return (float)o;
        }

        public void SetFloat(Set s, float v)
        {
            switch (kind)
            {
                case SettingKind.Int: info.SetValue(s, Mathf.RoundToInt(v)); break;
                case SettingKind.Rank: info.SetValue(s, (MobRankTier)Mathf.Clamp(Mathf.RoundToInt(v), 0, 9)); break;
                default: info.SetValue(s, v); break;
            }
        }

        public int[] GetInts(Set s) => (int[])info.GetValue(s);
        public float[] GetFloats(Set s) => (float[])info.GetValue(s);

        /// <summary>A float as the stored type would round it; lets presets hand ints and ranks in as numbers.</summary>
        public void SetFromObject(Set s, object value)
        {
            switch (kind)
            {
                case SettingKind.Toggle: SetBool(s, Convert.ToBoolean(value)); break;
                case SettingKind.IntTable: info.SetValue(s, ((int[])value).Clone()); break;
                case SettingKind.FloatTable: info.SetValue(s, ((float[])value).Clone()); break;
                default: SetFloat(s, Convert.ToSingle(value)); break;
            }
        }

        /// <summary>Tolerance for "is this still the default": floats drift through slider rounding.</summary>
        private float Epsilon => step > 0f ? step * 0.01f : 1e-4f;

        public bool IsChanged(Set s)
        {
            Set d = Set.Defaults;
            switch (kind)
            {
                case SettingKind.Toggle:
                    return GetBool(s) != GetBool(d);
                case SettingKind.IntTable:
                {
                    int[] a = GetInts(s), b = GetInts(d);
                    if (a == null || a.Length != b.Length) return true;
                    for (int i = 0; i < a.Length; i++)
                        if (a[i] != b[i]) return true;
                    return false;
                }
                case SettingKind.FloatTable:
                {
                    float[] a = GetFloats(s), b = GetFloats(d);
                    if (a == null || a.Length != b.Length) return true;
                    for (int i = 0; i < a.Length; i++)
                        if (Mathf.Abs(a[i] - b[i]) > 1e-4f) return true;
                    return false;
                }
                default:
                    return Mathf.Abs(GetFloat(s) - GetFloat(d)) > Epsilon;
            }
        }

        public void Reset(Set s)
        {
            Set d = Set.Defaults;
            switch (kind)
            {
                case SettingKind.IntTable: info.SetValue(s, GetInts(d).Clone()); break;
                case SettingKind.FloatTable: info.SetValue(s, GetFloats(d).Clone()); break;
                default: info.SetValue(s, info.GetValue(d)); break;
            }
        }

        public void Clamp(Set s)
        {
            switch (kind)
            {
                case SettingKind.Toggle:
                    return;
                case SettingKind.Float:
                    SetFloat(s, Mathf.Clamp(GetFloat(s), min, max));
                    return;
                case SettingKind.Int:
                case SettingKind.Rank:
                    SetFloat(s, Mathf.Clamp(Mathf.Round(GetFloat(s)), min, max));
                    return;
                case SettingKind.IntTable:
                {
                    int[] v = GetInts(s);
                    if (v == null || v.Length < minLength || v.Length > maxLength)
                    {
                        Reset(s);
                        return;
                    }
                    for (int i = 0; i < v.Length; i++)
                        v[i] = Mathf.Clamp(v[i], Mathf.RoundToInt(min), Mathf.RoundToInt(max));
                    return;
                }
                case SettingKind.FloatTable:
                {
                    float[] v = GetFloats(s);
                    if (v == null || v.Length < minLength || v.Length > maxLength)
                    {
                        Reset(s);
                        return;
                    }
                    for (int i = 0; i < v.Length; i++)
                        v[i] = float.IsNaN(v[i]) ? min : Mathf.Clamp(v[i], min, max);
                    return;
                }
            }
        }

        public void Scribe(Set s)
        {
            Set d = Set.Defaults;
            switch (kind)
            {
                case SettingKind.Toggle:
                {
                    bool v = GetBool(s);
                    Scribe_Values.Look(ref v, field, GetBool(d));
                    SetBool(s, v);
                    return;
                }
                case SettingKind.Float:
                {
                    float v = (float)info.GetValue(s);
                    Scribe_Values.Look(ref v, field, (float)info.GetValue(d));
                    info.SetValue(s, v);
                    return;
                }
                case SettingKind.Int:
                {
                    int v = (int)info.GetValue(s);
                    Scribe_Values.Look(ref v, field, (int)info.GetValue(d));
                    info.SetValue(s, v);
                    return;
                }
                case SettingKind.Rank:
                {
                    MobRankTier v = (MobRankTier)info.GetValue(s);
                    Scribe_Values.Look(ref v, field, (MobRankTier)info.GetValue(d));
                    info.SetValue(s, v);
                    return;
                }
                case SettingKind.IntTable:
                {
                    string text = Set.Join(GetInts(s));
                    Scribe_Values.Look(ref text, field, Set.Join(GetInts(d)));
                    if (Verse.Scribe.mode == LoadSaveMode.LoadingVars)
                        info.SetValue(s, Set.ParseInts(text) ?? (int[])GetInts(d).Clone());
                    return;
                }
                case SettingKind.FloatTable:
                {
                    string text = Set.Join(GetFloats(s));
                    Scribe_Values.Look(ref text, field, Set.Join(GetFloats(d)));
                    if (Verse.Scribe.mode == LoadSaveMode.LoadingVars)
                        info.SetValue(s, Set.ParseFloats(text) ?? (float[])GetFloats(d).Clone());
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Every setting, in the order the window shows them.
    ///
    /// Registration order IS layout order: tabs in enum order, sections in the
    /// order they first appear, entries in the order they are added. A new
    /// setting is a field on IsekaiDungeonsSettings, one line here, and two
    /// keys in Languages/English/Keyed/IsekaiDungeons_Settings.xml.
    /// </summary>
    public static class SettingsRegistry
    {
        public static readonly List<SettingEntry> All = new List<SettingEntry>();

        /// <summary>Rank indices the decor curve's six points sit at.</summary>
        public static readonly int[] DecorCurveRanks = { 0, 2, 4, 6, 8, 9 };

        /// <summary>Floors the tower rank curve's eight points sit at.</summary>
        public static readonly int[] TowerRankFloors = { 1, 4, 10, 25, 50, 70, 81, 100 };

        /// <summary>Floors the tower map-size curve's four points sit at.</summary>
        public static readonly int[] TowerSizeFloors = { 1, 25, 50, 100 };

        private static SettingsTab tab;
        private static string section;

        static SettingsRegistry()
        {
            Build();
        }

        /// <summary>Entries of one tab, in order.</summary>
        public static IEnumerable<SettingEntry> InTab(SettingsTab t)
        {
            for (int i = 0; i < All.Count; i++)
            {
                if (All[i].tab == t)
                    yield return All[i];
            }
        }

        public static SettingEntry Find(string field)
        {
            for (int i = 0; i < All.Count; i++)
            {
                if (All[i].field == field)
                    return All[i];
            }
            return null;
        }

        // ── Builders ──

        private static void Tab(SettingsTab t) => tab = t;
        private static void Section(string id) => section = tab + "." + id;

        private static SettingEntry Add(string field, SettingKind kind)
        {
            var e = new SettingEntry(field, tab, section, kind);
            All.Add(e);
            return e;
        }

        private static SettingEntry Toggle(string field) => Add(field, SettingKind.Toggle);

        private static SettingEntry Float(string field, float min, float max, float step, SettingUnit unit = SettingUnit.Plain)
        {
            SettingEntry e = Add(field, SettingKind.Float);
            e.min = min;
            e.max = max;
            e.step = step;
            e.unit = unit;
            return e;
        }

        private static SettingEntry Int(string field, int min, int max, SettingUnit unit = SettingUnit.Plain)
        {
            SettingEntry e = Add(field, SettingKind.Int);
            e.min = min;
            e.max = max;
            e.step = 1f;
            e.unit = unit;
            e.format = "0";
            return e;
        }

        private static SettingEntry Rank(string field)
        {
            SettingEntry e = Add(field, SettingKind.Rank);
            e.min = 0;
            e.max = 9;
            e.step = 1f;
            return e;
        }

        private static SettingEntry Ints(string field, TableColumns columns, int length, int min, int max)
        {
            SettingEntry e = Add(field, SettingKind.IntTable);
            e.columns = columns;
            e.minLength = e.maxLength = length;
            e.min = min;
            e.max = max;
            e.format = "0";
            return e;
        }

        private static SettingEntry Floats(string field, TableColumns columns, int minLength, int maxLength,
                                           float min, float max, string format = "0.##")
        {
            SettingEntry e = Add(field, SettingKind.FloatTable);
            e.columns = columns;
            e.minLength = minLength;
            e.maxLength = maxLength;
            e.min = min;
            e.max = max;
            e.format = format;
            return e;
        }

        private static void Build()
        {
            // ═══ General ═══
            Tab(SettingsTab.General);
            Section("Features");
            Toggle(nameof(Set.localGatesEnabled));
            Toggle(nameof(Set.guildContractsEnabled));
            Toggle(nameof(Set.towerEnabled));
            Toggle(nameof(Set.deepDungeonsEnabled));
            Section("Scaling");
            Float(nameof(Set.globalEncounterMultiplier), 0.25f, 4f, 0.05f, SettingUnit.Multiplier);
            Float(nameof(Set.globalLootMultiplier), 0f, 5f, 0.05f, SettingUnit.Multiplier);
            Float(nameof(Set.globalXpMultiplier), 0f, 5f, 0.05f, SettingUnit.Multiplier);

            // ═══ Local gates ═══
            Tab(SettingsTab.Gates);
            Section("Cadence");
            Float(nameof(Set.gatesPerSeason), 0f, 12f, 0.5f, SettingUnit.PerSeason);
            Float(nameof(Set.firstGateDay), 0f, 60f, 0.5f, SettingUnit.Day);
            Float(nameof(Set.gateIntervalJitter), 0f, 0.9f, 0.05f, SettingUnit.Percent);
            Int(nameof(Set.maxActiveGatesPerMap), 1, 8);
            Section("Rank");
            Int(nameof(Set.localRankShift), -5, 3, SettingUnit.Signed);
            Int(nameof(Set.localRankWindowSpan), 0, 5);
            Section("Countdown");
            Float(nameof(Set.breakTimerMultiplier), 0.25f, 4f, 0.05f, SettingUnit.Multiplier);
            Float(nameof(Set.warnHalfFraction), 0.1f, 0.9f, 0.05f, SettingUnit.Percent);
            Float(nameof(Set.finalWarningHours), 0f, 72f, 1f, SettingUnit.Hours).Off();
            Toggle(nameof(Set.gateCountdownLetters));
            Section("Breach");
            Float(nameof(Set.breachWaveIntervalHours), 1f, 120f, 1f, SettingUnit.Hours);
            Toggle(nameof(Set.breachWaveLetters));
            Float(nameof(Set.breachXpRadius), 0f, 100f, 1f, SettingUnit.Cells);
            Section("AfterClear");
            Float(nameof(Set.clearedCollapseHours), 0.1f, 48f, 0.1f, SettingUnit.Hours);
            Float(nameof(Set.ejectStunSeconds), 0f, 30f, 0.5f, SettingUnit.Seconds).Off();

            // ═══ Guild contracts ═══
            Tab(SettingsTab.Contracts);
            Section("Cadence");
            Float(nameof(Set.questsPerSeason), DungeonQuestTuning.MinQuestsPerSeason, DungeonQuestTuning.MaxQuestsPerSeason, 0.5f, SettingUnit.PerSeason);
            Float(nameof(Set.firstQuestDay), 0f, 60f, 0.5f, SettingUnit.Day);
            Float(nameof(Set.questIntervalJitter), 0f, 0.9f, 0.05f, SettingUnit.Percent);
            Int(nameof(Set.maxActiveQuests), 1, 10);
            Section("Rank");
            Int(nameof(Set.contractRankShift), -3, 3, SettingUnit.Signed);
            Float(nameof(Set.ceilingChanceSolo), 0f, 1f, 0.01f, SettingUnit.Percent);
            Float(nameof(Set.ceilingChanceNormal), 0f, 1f, 0.01f, SettingUnit.Percent);
            Float(nameof(Set.ceilingChanceDeep), 0f, 1f, 0.01f, SettingUnit.Percent);
            Float(nameof(Set.atCeilingShare), 0f, 1f, 0.05f, SettingUnit.Percent);
            Int(nameof(Set.deepBenchCount), 1, 20);
            Section("Mix");
            Float(nameof(Set.chancePostedBroken), 0f, 1f, 0.01f, SettingUnit.Percent);
            Float(nameof(Set.chancePostedRescue), 0f, 1f, 0.01f, SettingUnit.Percent);
            Section("Travel");
            Float(nameof(Set.contractDistanceMultiplier), 0.25f, 4f, 0.05f, SettingUnit.Multiplier);
            Section("Lifetime");
            Float(nameof(Set.daysBeforeBreak), 1f, 60f, 0.5f, SettingUnit.Days);
            Float(nameof(Set.brokenSiteDays), 1f, 60f, 0.5f, SettingUnit.Days);
            Section("Rewards");
            Float(nameof(Set.contractFeeMultiplier), 0f, 10f, 0.05f, SettingUnit.Multiplier);
            Float(nameof(Set.brokenFeeMultiplier), 1f, 5f, 0.05f, SettingUnit.Multiplier);
            Int(nameof(Set.rescueGoodwill), 0, 100, SettingUnit.Signed);
            Int(nameof(Set.rescueFailureGoodwill), -100, 0, SettingUnit.Signed);
            Section("BrokenSites");
            Float(nameof(Set.brokenSurfaceShare), 0f, 1f, 0.05f, SettingUnit.Percent);
            Int(nameof(Set.brokenScatterRadius), 5, 60, SettingUnit.Cells);
            Float(nameof(Set.defenderRingRadius), 4f, 40f, 1f, SettingUnit.Cells);

            // ═══ Dungeons ═══
            Tab(SettingsTab.Dungeons);
            Section("Layout");
            Float(nameof(Set.mapSizeMultiplier), 0.75f, 1.5f, 0.05f, SettingUnit.Multiplier);
            Float(nameof(Set.roomCountMultiplier), 0.5f, 2.5f, 0.05f, SettingUnit.Multiplier);
            Int(nameof(Set.treasureRoomsShift), -2, 4, SettingUnit.Signed);
            Float(nameof(Set.shortcutMultiplier), 0f, 3f, 0.1f, SettingUnit.Multiplier);
            Int(nameof(Set.wingLengthShift), -3, 3, SettingUnit.Signed);
            Int(nameof(Set.maxWingRoomsPerDungeon), 0, 20);
            Section("Encounters");
            Float(nameof(Set.firstRoomBudgetFactor), 0.1f, 3f, 0.05f, SettingUnit.Multiplier);
            Float(nameof(Set.lastRoomBudgetFactor), 0.1f, 4f, 0.05f, SettingUnit.Multiplier);
            Float(nameof(Set.offPathBudgetFactor), 0f, 3f, 0.05f, SettingUnit.Multiplier);
            Float(nameof(Set.championBudgetFactor), 0.5f, 3f, 0.05f, SettingUnit.Multiplier);
            Int(nameof(Set.roomTierRampDepth), 0, 5);
            Int(nameof(Set.maxCreaturesPerRoom), 1, 100);
            Int(nameof(Set.bossMinionsMin), 0, 12);
            Int(nameof(Set.bossMinionsMax), 0, 12);
            Float(nameof(Set.leashSlackCells), 0f, 50f, 1f, SettingUnit.Cells);
            Section("Events");
            Float(nameof(Set.eventRoomFraction), 0f, 1f, 0.01f, SettingUnit.Percent);
            Int(nameof(Set.minPlainRooms), 0, 10);
            Section("BeastVaults");
            Rank(nameof(Set.beastVaultMinRank));
            Float(nameof(Set.beastVaultChanceAtMinRank), 0f, 1f, 0.01f, SettingUnit.Percent);
            Float(nameof(Set.beastVaultChanceAtMaxRank), 0f, 1f, 0.01f, SettingUnit.Percent);
            Section("Moods");
            Toggle(nameof(Set.dungeonDreadEnabled));
            Section("Decor");
            Toggle(nameof(Set.decorEnabled));
            Float(nameof(Set.decorDensityMultiplier), 0f, 4f, 0.05f, SettingUnit.Multiplier);
            Float(nameof(Set.decorEntryFactor), 0f, 5f, 0.05f, SettingUnit.Multiplier);
            Float(nameof(Set.decorCombatFactor), 0f, 5f, 0.05f, SettingUnit.Multiplier);
            Float(nameof(Set.decorTreasureFactor), 0f, 5f, 0.05f, SettingUnit.Multiplier);
            Float(nameof(Set.decorBossFactor), 0f, 5f, 0.05f, SettingUnit.Multiplier);
            Float(nameof(Set.decorDepthStart), 0f, 3f, 0.05f, SettingUnit.Multiplier);
            Float(nameof(Set.decorDepthEnd), 0f, 3f, 0.05f, SettingUnit.Multiplier);
            Toggle(nameof(Set.decorRefineFloors));
            Toggle(nameof(Set.decorSmoothWalls));
            Toggle(nameof(Set.decorCorridorTorches));
            Float(nameof(Set.corridorTorchSpacingMultiplier), 0.5f, 3f, 0.05f, SettingUnit.Multiplier);
            Float(nameof(Set.decorFireSpacing), 1f, 12f, 0.5f, SettingUnit.Cells);
            Float(nameof(Set.decorPlantFireDistance), 0f, 30f, 0.5f, SettingUnit.Cells);
            Int(nameof(Set.decorAttemptsPerRoom), 10, 400);

            // ═══ Rewards ═══
            Tab(SettingsTab.Rewards);
            Section("Value");
            Float(nameof(Set.treasureLootFraction), 0f, 2f, 0.01f, SettingUnit.Percent);
            Float(nameof(Set.wingLootBonusPerRoom), 0f, 2f, 0.05f, SettingUnit.Percent);
            Section("Composition");
            Float(nameof(Set.gearShare), 0f, 1f, 0.05f, SettingUnit.Percent);
            Float(nameof(Set.coinShare), 0f, 1f, 0.01f, SettingUnit.Percent);
            Float(nameof(Set.relicShare), 0f, 1f, 0.05f, SettingUnit.Percent);
            Float(nameof(Set.maxRelicShare), 0f, 1f, 0.05f, SettingUnit.Percent);
            Float(nameof(Set.elixirChance), 0f, 1f, 0.05f, SettingUnit.Percent);
            Float(nameof(Set.treasureGearChance), 0f, 1f, 0.05f, SettingUnit.Percent);
            Float(nameof(Set.gateScrollClearChance), 0f, 1f, 0.05f, SettingUnit.Percent);
            Float(nameof(Set.gateScrollTreasureChance), 0f, 1f, 0.05f, SettingUnit.Percent);
            Float(nameof(Set.gateScrollQuestChance), 0f, 1f, 0.05f, SettingUnit.Percent);
            Section("Chests");
            Float(nameof(Set.extraChestChanceMultiplier), 0f, 3f, 0.05f, SettingUnit.Multiplier);
            Float(nameof(Set.chestCommonWeight), 0f, 5f, 0.05f, SettingUnit.Multiplier);
            Float(nameof(Set.chestRareWeight), 0f, 5f, 0.05f, SettingUnit.Multiplier);
            Float(nameof(Set.chestEpicWeight), 0f, 5f, 0.05f, SettingUnit.Multiplier);
            Float(nameof(Set.chestLegendaryWeight), 0f, 5f, 0.05f, SettingUnit.Multiplier);
            Toggle(nameof(Set.chestHaulMessage));
            Section("Experience");
            Float(nameof(Set.clearXpMultiplier), 0f, 20f, 0.05f, SettingUnit.Multiplier);

            // ═══ Tower ═══
            Tab(SettingsTab.Tower);
            Section("Tower");
            Float(nameof(Set.towerRaiseAfterSeconds), 0f, 300f, 1f, SettingUnit.Seconds);
            Float(nameof(Set.towerEncounterMultiplier), 0.25f, 4f, 0.05f, SettingUnit.Multiplier);
            Float(nameof(Set.towerLootMultiplier), 0f, 10f, 0.05f, SettingUnit.Multiplier);
            Float(nameof(Set.towerXpMultiplier), 0f, 10f, 0.05f, SettingUnit.Multiplier);
            Int(nameof(Set.towerVillagersMin), 0, 20);
            Int(nameof(Set.towerVillagersMax), 0, 20);
            Section("Summit");
            Toggle(nameof(Set.godhoodChangesAppearance));
            Toggle(nameof(Set.godhoodHaloEnabled));
            Section("Autopilot");
            Float(nameof(Set.autopilotFoodPauseBelow), 0f, 0.9f, 0.01f, SettingUnit.Percent);
            Float(nameof(Set.autopilotRestPauseBelow), 0f, 0.9f, 0.01f, SettingUnit.Percent);
            Float(nameof(Set.autopilotFoodResumeAt), 0.1f, 1f, 0.01f, SettingUnit.Percent);
            Float(nameof(Set.autopilotRestResumeAt), 0.1f, 1f, 0.01f, SettingUnit.Percent);
            Float(nameof(Set.autopilotRangedHoldFraction), 0.3f, 1f, 0.05f, SettingUnit.Percent);
            Float(nameof(Set.autopilotIdleTimeoutHours), 0.1f, 12f, 0.1f, SettingUnit.Hours);
            Float(nameof(Set.autopilotSnoozeHours), 0f, 24f, 0.5f, SettingUnit.Hours).Off();
            Float(nameof(Set.autopilotDeferSeconds), 0f, 30f, 0.5f, SettingUnit.Seconds).Off();

            // ═══ Deep dungeons ═══
            Tab(SettingsTab.Deep);
            Section("Surveys");
            Float(nameof(Set.chancePostedDeepReport), 0f, 1f, 0.01f, SettingUnit.Percent);
            Int(nameof(Set.maxDeepDungeonsOnPlanet), 1, 10);
            Section("Scaling");
            Float(nameof(Set.deepEncounterMultiplier), 0.25f, 4f, 0.05f, SettingUnit.Multiplier);
            Float(nameof(Set.deepLootMultiplier), 0f, 10f, 0.05f, SettingUnit.Multiplier);
            Float(nameof(Set.deepXpMultiplier), 0f, 10f, 0.05f, SettingUnit.Multiplier);
            Section("Behaviours");
            Int(nameof(Set.risingDeadMax), 0, 60);
            Int(nameof(Set.retreatMax), 0, 60);
            Float(nameof(Set.regrowthMinShare), 0f, 1f, 0.05f, SettingUnit.Percent);
            Float(nameof(Set.regrowthMaxShare), 0f, 1f, 0.05f, SettingUnit.Percent);
            Float(nameof(Set.regrowthFullAtDepth), 1f, 10f, 0.5f).Format("0.#");
            Float(nameof(Set.alertPerFloor), 0f, 0.5f, 0.01f, SettingUnit.Percent);
            Float(nameof(Set.alertPerDay), 0f, 1f, 0.01f, SettingUnit.Percent);
            Float(nameof(Set.alertMaxShare), 0f, 2f, 0.05f, SettingUnit.Percent);
            Float(nameof(Set.coldFirstFloorTemp), -50f, 30f, 1f, SettingUnit.Temperature);
            Float(nameof(Set.coldDeepestTemp), -120f, 0f, 1f, SettingUnit.Temperature);
            Float(nameof(Set.heatFirstFloorTemp), 0f, 60f, 1f, SettingUnit.Temperature);
            Float(nameof(Set.heatDeepestTemp), 20f, 150f, 1f, SettingUnit.Temperature);

            // ═══ Interface ═══
            Tab(SettingsTab.Interface);
            Section("Arrival");
            Toggle(nameof(Set.titleCardEnabled));
            Section("Gates");
            Toggle(nameof(Set.gateLabelsEnabled));
            Float(nameof(Set.gateLabelScale), 0.5f, 2.5f, 0.05f, SettingUnit.Multiplier);
            Float(nameof(Set.gateEffectsStrength), 0f, 1f, 0.05f, SettingUnit.Percent);
            Section("Treasure");
            Float(nameof(Set.reelSpinSeconds), 1f, 15f, 0.1f, SettingUnit.Seconds);
            Section("Music");
            Toggle(nameof(Set.dungeonMusicEnabled));
            Float(nameof(Set.musicFadeOutSeconds), 0.1f, 10f, 0.1f, SettingUnit.Seconds);
            Float(nameof(Set.musicGapSeconds), 0f, 10f, 0.1f, SettingUnit.Seconds);
            Float(nameof(Set.musicFadeInSeconds), 0.1f, 10f, 0.1f, SettingUnit.Seconds);
            Int(nameof(Set.towerMusicSplitFloor), 1, 100, SettingUnit.Floor);

            // ═══ Advanced ═══
            Tab(SettingsTab.Advanced);
            Section("RankTables");
            Ints(nameof(Set.localRankTopByColonyRank), TableColumns.Ranks, 10, 0, 8);
            Ints(nameof(Set.contractCentreOffsetByBestRank), TableColumns.Ranks, 10, 0, 9);
            Ints(nameof(Set.contractMinTilesByRank), TableColumns.Ranks, 10, 1, 100);
            Ints(nameof(Set.contractMaxTilesByRank), TableColumns.Ranks, 10, 1, 100);
            Ints(nameof(Set.contractSilverByRank), TableColumns.Ranks, 10, 0, 1000000);
            Ints(nameof(Set.contractGuildGoodwillByRank), TableColumns.Ranks, 10, 0, 100);
            Ints(nameof(Set.clearXpBaseByRank), TableColumns.Ranks, 10, 0, 100000000);
            Ints(nameof(Set.shortcutBudgetByRank), TableColumns.Ranks, 10, 0, 20);
            Ints(nameof(Set.maxWingLengthByRank), TableColumns.Ranks, 10, 1, 10);
            Floats(nameof(Set.relicDrawsByRank), TableColumns.Ranks, 10, 10, 0f, 20f);
            Ints(nameof(Set.geneCountByRank), TableColumns.Ranks, 10, 0, 20);
            Section("Curves");
            Floats(nameof(Set.decorPointsByRank), TableColumns.DecorCurveRanks, 6, 6, 0f, 200f);
            Floats(nameof(Set.towerRankByFloor), TableColumns.TowerRankFloors, 8, 8, 0f, 8f);
            Floats(nameof(Set.towerMapSizeByFloor), TableColumns.TowerSizeFloors, 4, 4, 120f, 300f, "0");
            Ints(nameof(Set.corridorTorchSpacingByRegister), TableColumns.Registers, 5, 0, 60);
            Section("Chests");
            Floats(nameof(Set.extraChestChancesBronze), TableColumns.ExtraRolls, 0, 6, 0f, 1f).Tier(1);
            Floats(nameof(Set.extraChestChancesSilver), TableColumns.ExtraRolls, 0, 6, 0f, 1f).Tier(2);
            Floats(nameof(Set.extraChestChancesGold), TableColumns.ExtraRolls, 0, 6, 0f, 1f).Tier(3);
            Floats(nameof(Set.extraChestChancesJade), TableColumns.ExtraRolls, 0, 6, 0f, 1f).Tier(4);
            Floats(nameof(Set.rarityWeightsBronze), TableColumns.Rarities, 4, 4, 0f, 1000f, "0.#");
            Floats(nameof(Set.rarityWeightsSilver), TableColumns.Rarities, 4, 4, 0f, 1000f, "0.#");
            Floats(nameof(Set.rarityWeightsGold), TableColumns.Rarities, 4, 4, 0f, 1000f, "0.#");
            Floats(nameof(Set.rarityWeightsJade), TableColumns.Rarities, 4, 4, 0f, 1000f, "0.#");
            Section("Breach");
            Floats(nameof(Set.breachWaveShares), TableColumns.Waves, 1, 6, 0f, 1f);
            Section("Internals");
            Int(nameof(Set.maxShortcutSkip), 2, 6);
            Int(nameof(Set.maxShortcutSkipHighRank), 2, 8);
            Float(nameof(Set.wingDepthStepFrac), 0f, 3f, 0.05f);
            Float(nameof(Set.openPackingEfficiency), 0.1f, 1f, 0.01f, SettingUnit.Percent);
            Float(nameof(Set.decorReferenceArea), 20f, 500f, 5f).Format("0");
            Float(nameof(Set.decorAreaFactorMin), 0f, 3f, 0.05f, SettingUnit.Multiplier);
            Float(nameof(Set.decorAreaFactorMax), 0.5f, 10f, 0.05f, SettingUnit.Multiplier);
            Int(nameof(Set.beastVaultPickAttempts), 1, 30);
            Int(nameof(Set.rosterRankMargin), 0, 5);
            Int(nameof(Set.rosterRankFloorMargin), 0, 9);
            Float(nameof(Set.backboneWeightFactor), 0f, 2f, 0.05f, SettingUnit.Multiplier);
            Float(nameof(Set.lootFallbackMultiplier), 0f, 10f, 0.05f, SettingUnit.Multiplier);
            Float(nameof(Set.relicSoftFloorShare), 0f, 1f, 0.01f, SettingUnit.Percent);
            Section("Performance");
            Int(nameof(Set.gateStateIntervalTicks), 60, 2500);
            Int(nameof(Set.mapStateIntervalTicks), 15, 600);
            Toggle(nameof(Set.suppressDungeonWildlife));
            Section("Diagnostics");
            Toggle(nameof(Set.logGenerationSummaries));
        }
    }
}
