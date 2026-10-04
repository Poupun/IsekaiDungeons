using IsekaiLeveling.MobRanking;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Central tuning. Every gameplay number still has its name HERE, never
    /// inline in lifecycle code, so balancing passes touch one file.
    ///
    /// The values now come from the mod settings (IsekaiDungeonsSettings,
    /// shown in the settings window, defaults equal to what used to be
    /// hardcoded). The properties keep their old names so gameplay code did
    /// not have to change, and the notes below keep explaining WHY each
    /// default is what it is. A property that converts units (hours to ticks,
    /// seconds to ticks) does so here, so the settings stay in units a player
    /// reads and the game keeps receiving ticks.
    /// </summary>
    public static class DungeonTunables
    {
        private static IsekaiDungeonsSettings S => IsekaiDungeonsSettings.Current;

        // ── Context multipliers ─────────────────────────────────────────────
        //
        // The global multipliers apply everywhere; the tower and deep-dungeon
        // ones stack on top for maps that belong to those features. A map with
        // no dungeon parent (the colony map, where a breach or an escaped boss
        // lands) gets the global value alone.

        /// <summary>Creature budget scale for a map.</summary>
        public static float EncounterMultiplierFor(Map map) =>
            S.globalEncounterMultiplier * ContextScale(map, S.towerEncounterMultiplier, S.deepEncounterMultiplier);

        /// <summary>Loot value scale for a map.</summary>
        public static float LootMultiplierFor(Map map) =>
            S.globalLootMultiplier * ContextScale(map, S.towerLootMultiplier, S.deepLootMultiplier);

        /// <summary>Clear XP scale for a map.</summary>
        public static float XpMultiplierFor(Map map) =>
            S.globalXpMultiplier * ContextScale(map, S.towerXpMultiplier, S.deepXpMultiplier);

        private static float ContextScale(Map map, float tower, float deep)
        {
            MapParent parent = map?.Parent;
            if (parent is TowerFloorMapParent)
                return tower;
            if (parent is MultiFloorMapParent)
                return deep;
            return 1f;
        }

        /// <summary>
        /// Largest XP a single award may reach. Casting a float at or past
        /// int.MaxValue to int wraps negative, so awards clamp well short of it.
        /// </summary>
        private const float MaxXpAward = 2000000000f;

        /// <summary>An XP award scaled for its map, safe against overflow at the top of the table.</summary>
        public static int ScaleXp(int xp, Map map) =>
            (int)Mathf.Min(MaxXpAward, Mathf.Round(xp * XpMultiplierFor(map)));

        /// <summary>A dungeon's combat-room count estimate, as the layout would roll it.</summary>
        public static float RoomCountFor(DungeonKindDef def, float rankIndex) =>
            def?.roomCountByRank == null ? 1f : def.roomCountByRank.Evaluate(rankIndex) * S.roomCountMultiplier;

        /// <summary>A map edge length scaled by the map-size setting.</summary>
        public static int ScaledMapSize(float size) => Mathf.RoundToInt(size * S.mapSizeMultiplier);

        /// <summary>Gate countdown in days for a kind and rank, with the countdown setting applied.</summary>
        public static float BreakTimerDays(DungeonKindDef def, float rankIndex) =>
            def.breakTimerDaysByRank.Evaluate(rankIndex) * S.breakTimerMultiplier;

        // ── Rewards ─────────────────────────────────────────────────────────

        /// <summary>
        /// Clear-loot value premium over a same-rank guild contract. Dungeons
        /// demand more effort (travel-free but a full multi-room crawl with a
        /// countdown), so they pay better than the quest board by design.
        /// Only the fallback ladder uses it: the XML curves already carry it.
        /// </summary>
        public static float LootValueMultiplier => S.lootFallbackMultiplier;

        /// <summary>Premium applied on top of the base-mod quest XP curve for a dungeon clear.</summary>
        public static float ClearXpMultiplier => S.clearXpMultiplier;

        /// <summary>Treasure side-room value as a fraction of the clear-loot value.</summary>
        public static float TreasureLootFraction => S.treasureLootFraction;

        // ── Local gates ─────────────────────────────────────────────────────

        /// <summary>
        /// Hard cap on concurrent LOCAL gates per colony map.
        ///
        /// TWO. It was four, sized for an era when local gates were the mod's
        /// only source of dungeons and were meant to be a standing queue. They
        /// are not that any more: they are the quiet, low-rank source, and the
        /// guild quests carry everything the player is supposed to travel for.
        /// Four slots of background texture sitting on the home map at once is
        /// simply clutter.
        ///
        /// Two still leaves room for the case the old comment was protecting:
        /// a BREACHING gate holds a slot too (see DungeonTracker.ActiveGateCount)
        /// and a breach adds about two days to a gate's life, so one gate can be
        /// draining while another is still counting down.
        /// </summary>
        public static int MaxActiveGatesPerMap => S.maxActiveGatesPerMap;

        /// <summary>
        /// Ceiling of the LOCAL gate rank window, indexed by (int)MobRankTier of
        /// the colony's best rank. The window is this value down to
        /// LocalRankWindowSpan below it, clamped at F.
        ///
        /// The gap widens as the colony climbs, and that is the whole point. A
        /// fixed gap does not work: three ranks below an SSS colony is still a
        /// serious expedition, while three ranks below a C colony is nothing at
        /// all, so a constant offset either fails to fix the annoyance at the
        /// top or empties the early game at the bottom. Early colonies have no
        /// annoyance problem to solve, so their local gates sit near their own
        /// rank; a late colony's sit far below it and the guild carries
        /// everything worth travelling for.
        ///
        /// BUMPED ONE STEP from { 0, 0, 1, 2, 2, 3, 4, 4, 5, 6 }. The old
        /// ceiling put an SSS colony's best local gate at A and a Nation
        /// colony's at S, which read as the map having stopped keeping up long
        /// before the colony had. Local gates are still deliberately BELOW the
        /// colony's rank - that part was right and is not being undone - but the
        /// gap was one rank wider than it needed to be.
        ///
        ///   colony    SS -> A ceiling   SSS -> S ceiling   Nation -> SS ceiling
        /// </summary>
        public static int[] LocalRankTopByColonyRank => S.localRankTopByColonyRank;

        /// <summary>Added to the window top from the table above (the "rank shift" slider). Still capped at SSS.</summary>
        public static int LocalRankShift => S.localRankShift;

        /// <summary>How many ranks the local window spans below its ceiling.</summary>
        public static int LocalRankWindowSpan => S.localRankWindowSpan;

        /// <summary>Stun applied to pawns force-ejected from a collapsing/breaking dungeon.</summary>
        public static int EjectStunTicks => Mathf.RoundToInt(S.ejectStunSeconds * 60f);

        // ── Beast vault ─────────────────────────────────────────────────────
        //
        // A treasure side-room that holds a live creature instead of only a
        // pile. The creature is WILD and idle like everything else the
        // population step places, so it is not a fight the player is forced
        // into — it stands over the hoard and the party decides whether to
        // take it, tame it, or leave the room alone.
        //
        // It exists because the best "loot" vanilla owns is not an item at
        // all: a thrumbo is priced at 4000 silver as a ThingDef, and the item
        // filter correctly refuses to drop one in a chest. Spawning the pawn
        // instead pays out that value in the one form the mod already has a
        // whole feature for — Isekai Creatures' taming and bonding.

        /// <summary>
        /// Lowest gate rank that may hold a beast vault.
        ///
        /// D. Below it the rank-banded creature pools are small animals a
        /// colony can already tame off its own map, so a "beast vault" holding
        /// a boomalope is a joke rather than a find.
        /// </summary>
        public static MobRankTier BeastVaultMinRank => S.beastVaultMinRank;

        /// <summary>
        /// Chance a qualifying treasure room holds one, at BeastVaultMinRank.
        ///
        /// Deliberately low. A beast vault is a story the player tells about
        /// one dungeon, and it stops being that the moment it is a fixture.
        /// </summary>
        public static float BeastVaultChanceAtMinRank => S.beastVaultChanceAtMinRank;

        /// <summary>Chance at Nation rank; interpolated linearly from the floor above.</summary>
        public static float BeastVaultChanceAtMaxRank => S.beastVaultChanceAtMaxRank;

        /// <summary>
        /// How many times to re-roll the rank-banded creature pick looking for
        /// something actually tameable.
        ///
        /// The base mod's selector is a HUNT selector: its bands are drawn over
        /// every non-humanlike creature, mechanoids and insects included, and
        /// most of what it returns cannot be tamed. Six tries is enough to find
        /// an animal in every band that has one and cheap enough not to matter
        /// in a step that already generates dozens of pawns.
        /// </summary>
        public static int BeastVaultPickAttempts => S.beastVaultPickAttempts;

        /// <summary>
        /// After a clear, the pocket map must be continuously empty of player
        /// pawns (and free of portal-hauling jobs) for this long before the
        /// gate collapses. TWO HOURS.
        ///
        /// This is now the ONLY thing that decides when a cleared dungeon
        /// closes. It used to be a mere debounce on top of a flat
        /// clearedLingerDays wait (two days, three for Nation gates), which
        /// meant a gate the player had finished with and walked away from sat
        /// on the map for another two days doing nothing — a dead marker, an
        /// open pocket map still ticking, and a "is that one done?" question
        /// every time the player looked at it.
        ///
        /// Tying it to departure instead of to a calendar means the dungeon
        /// closes when the player is actually finished with it, however long
        /// that took. Two hours rather than a few seconds because leaving is
        /// rarely one trip: it absorbs a pawn going back for a second load,
        /// a straggler repathing, and the gap between the last hauler
        /// delivering and the player deciding they are done.
        /// </summary>
        public static int ClearedEmptyCollapseTicks =>
            Mathf.Max(1, Mathf.RoundToInt(S.clearedCollapseHours * GenDate.TicksPerHour));

        /// <summary>Fraction of the countdown at which the mid-way warning letter is sent.</summary>
        public static float WarnHalfFraction => S.warnHalfFraction;

        /// <summary>Remaining-time threshold for the final warning letter (1 in-game day). Zero sends none.</summary>
        public static int FinalWarningTicks => Mathf.RoundToInt(S.finalWarningHours * GenDate.TicksPerHour);

        /// <summary>Whether the countdown letters (half-way and final warning) are sent at all.</summary>
        public static bool CountdownLetters => S.gateCountdownLetters;

        // ── The breach ──────────────────────────────────────────────────────
        //
        // When the countdown expires the dungeon does NOT empty in one tick.
        // It drains, a wave a day, and the gate stays open the whole time —
        // so a player who ran out of time can still storm in and race the
        // dungeon to its own boss. See Building_DungeonGate's breach section.

        /// <summary>
        /// How close to a slain escaped boss a colonist has to be to be counted
        /// as having fought it.
        ///
        /// Without this the outside kill paid XP to EVERY colonist on the map
        /// including ones asleep across the base, while an in-dungeon clear
        /// only ever paid the handful who actually went in — so beating the
        /// boss on your doorstep was worth several times the XP of beating it
        /// in its own hall, which inverts the whole point of diving.
        /// </summary>
        public static float BreachXpRadius => S.breachXpRadius;

        /// <summary>Real time between waves. One in-game day by default.</summary>
        public static int BreachWaveIntervalTicks =>
            Mathf.Max(1, Mathf.RoundToInt(S.breachWaveIntervalHours * GenDate.TicksPerHour));

        /// <summary>
        /// Share of the dungeon's REMAINING population each wave takes, in
        /// order. The last wave always takes everything still in there, plus
        /// the boss, whatever its entry says — which is what makes it the
        /// climax rather than just another wave.
        ///
        /// Creatures leave weakest-first (see BreachWaveOrder), so equal-ish
        /// counts still escalate hard: the last wave is the back half of the
        /// dungeon arriving at once.
        /// </summary>
        public static float[] BreachWaveFractions => S.breachWaveShares;

        /// <summary>Number of waves. Derived so the two can never disagree.</summary>
        public static int BreachWaveCount => Mathf.Max(1, BreachWaveFractions.Length);

        /// <summary>Total length of a breach, for letters and the inspect pane.</summary>
        public static int BreachTotalTicks => BreachWaveIntervalTicks * BreachWaveCount;

        /// <summary>Whether each wave before the last sends its own letter. The final wave always does.</summary>
        public static bool BreachWaveLetters => S.breachWaveLetters;

        /// <summary>
        /// How far outside its zone an un-aggroed creature may wander in an
        /// OPEN dungeon before it is walked back. Generous on purpose: the
        /// point is to preserve the progression ORDER, not to pin creatures to
        /// a spot, and a group milling around its own patch looks alive.
        /// </summary>
        public static float LeashSlackCells => S.leashSlackCells;

        /// <summary>Gate/mapcomp state machines poll on this interval (ticks).</summary>
        public static int GateStateInterval => Mathf.Max(1, S.gateStateIntervalTicks);
        public static int MapStateInterval => Mathf.Max(1, S.mapStateIntervalTicks);

        /// <summary>Room encounter ramp: budget scalar at the first / last combat room.</summary>
        public static float FirstRoomBudgetFactor => S.firstRoomBudgetFactor;
        public static float LastRoomBudgetFactor => S.lastRoomBudgetFactor;

        /// <summary>How far below the gate rank the FIRST combat room's creatures sit.</summary>
        public static int RoomTierRampDepth => S.roomTierRampDepth;

        /// <summary>
        /// Per-room creature-count ceiling, independent of budget. At Nation rank
        /// the budget divided by the 20-power floor could otherwise legalize
        /// hundreds of chaff spawns in one room, which is both a TPS liability
        /// and unreadable gameplay (G3: sanity-cap what the formula can produce).
        /// </summary>
        public static int MaxCreaturesPerRoom => S.maxCreaturesPerRoom;

        /// <summary>Boss entourage size (inclusive roll).</summary>
        public static int MinBossMinions => S.bossMinionsMin;
        public static int MaxBossMinions => Mathf.Max(S.bossMinionsMin, S.bossMinionsMax);

        // ── Layout complexity ───────────────────────────────────────────────
        //
        // Rank used to buy LENGTH and nothing else: an F dungeon and a Nation
        // dungeon were the same shape — a path graph — with more beads on the
        // string. These two tables are what rank buys instead, and both are
        // keyed in code so none of the 53 shipped defs needs editing.
        //
        // WINGS are optional branches hanging off the spine: 1 room is the old
        // treasure stub, 2+ rooms is a side route with its own fights ending in
        // a guarded prize. SHORTCUTS are extra corridors between non-adjacent
        // spine rooms, which turn the path into a graph with real loops —
        // alternative routes, flanking, and somewhere to retreat to.

        /// <summary>Loops the layout may add, indexed by (int)MobRankTier.</summary>
        public static int[] CrossLinkBudgetByRank => S.shortcutBudgetByRank;

        /// <summary>Loops actually allowed at a rank: the table times the shortcut setting.</summary>
        public static int CrossLinkBudgetFor(MobRankTier rank)
        {
            int[] table = CrossLinkBudgetByRank;
            int baseBudget = table[Mathf.Clamp((int)rank, 0, table.Length - 1)];
            return Mathf.Max(0, Mathf.RoundToInt(baseBudget * S.shortcutMultiplier));
        }

        /// <summary>Longest optional branch, indexed by (int)MobRankTier. 1 == today's treasure stub.</summary>
        public static int[] MaxWingLengthByRank => S.maxWingLengthByRank;

        /// <summary>Longest branch actually allowed at a rank: the table plus the wing-length setting, never under 1.</summary>
        public static int MaxWingLengthFor(MobRankTier rank)
        {
            int[] table = MaxWingLengthByRank;
            return Mathf.Max(1, table[Mathf.Clamp((int)rank, 0, table.Length - 1)] + S.wingLengthShift);
        }

        /// <summary>
        /// How many spine rooms a single shortcut may bypass. Spans are kept
        /// DISJOINT (see AddShortcuts), so N shortcuts can never chain into one
        /// route that skips most of the dungeon.
        /// </summary>
        public static int MaxShortcutSkip => S.maxShortcutSkip;
        public static int MaxShortcutSkipHighRank => S.maxShortcutSkipHighRank;

        /// <summary>Absolute ceiling on wing rooms per dungeon, whatever the def's treasure roll asks for.</summary>
        public static int MaxWingRoomsPerDungeon => S.maxWingRoomsPerDungeon;

        /// <summary>Depth a wing gains per hop, as a fraction of one spine step.</summary>
        public static float WingDepthStepFrac => S.wingDepthStepFrac;

        /// <summary>An optional room the party may walk straight past pays a smaller purse.</summary>
        public static float OffPathBudgetFactor => S.offPathBudgetFactor;

        /// <summary>A wing's terminal guard pays a bigger one.</summary>
        public static float ChampionBudgetFactor => S.championBudgetFactor;

        /// <summary>
        /// Extra treasure per room of wing length, SUB-LINEAR on purpose. Paying
        /// one full roll per wing room put ~88% of a Nation dungeon's entire
        /// clear value in an optional dead end.
        /// </summary>
        public static float WingLootBonusPerRoom => S.wingLootBonusPerRoom;

        /// <summary>Fraction of the map area an Open field's zones may cover before the count is capped.</summary>
        public static float OpenPackingEfficiency => S.openPackingEfficiency;

        /// <summary>Treasure rooms added to (or taken from) each def's own roll. Archetype overrides are left alone.</summary>
        public static int TreasureRoomsShift => S.treasureRoomsShift;

        /// <summary>Combat rooms scale.</summary>
        public static float RoomCountMultiplier => S.roomCountMultiplier;

        // ── Room events ─────────────────────────────────────────────────────

        /// <summary>
        /// Share of a dungeon's combat rooms that may become events, before the
        /// per-event caps apply. A third feels varied without diluting the
        /// ordinary fights that make the events land.
        /// </summary>
        public static float EventRoomFraction => S.eventRoomFraction;

        /// <summary>Never convert this many rooms or fewer — a 1-room dungeon keeps its fight.</summary>
        public static int MinPlainRooms => S.minPlainRooms;

        // ── Decor ───────────────────────────────────────────────────────────
        // GenStep_DungeonDecor. The shape of the curve is the design: a hole at F,
        // a wonder at Nation. The numbers are first guesses to tune in play.

        /// <summary>The register a rank sits in: one step per two ranks, F–E hole up to SSS–Nation wonder.</summary>
        public static DecorRegister DecorRegisterFor(float rankIndex) =>
            (DecorRegister)Mathf.Clamp(Mathf.FloorToInt(rankIndex / 2f), 0, 4);

        private static SimpleCurve decorCurve;
        private static IsekaiDungeonsSettings decorCurveOwner;
        private static int decorCurveRevision = -1;

        /// <summary>Decor points a reference room gets, by continuous rank index (0 = F, 9 = Nation).</summary>
        public static SimpleCurve DecorPointsByRank
        {
            get
            {
                IsekaiDungeonsSettings s = S;
                if (decorCurve == null || decorCurveOwner != s || decorCurveRevision != s.Revision)
                {
                    decorCurve = CurveFrom(SettingsRegistry.DecorCurveRanks, s.decorPointsByRank);
                    decorCurveOwner = s;
                    decorCurveRevision = s.Revision;
                }
                return decorCurve;
            }
        }

        /// <summary>A curve from fixed x positions and a settings table of y values.</summary>
        internal static SimpleCurve CurveFrom(int[] xs, float[] ys)
        {
            var curve = new SimpleCurve();
            int n = Mathf.Min(xs.Length, ys?.Length ?? 0);
            for (int i = 0; i < n; i++)
                curve.Add(new CurvePoint(xs[i], ys[i]));
            if (n == 0)
                curve.Add(new CurvePoint(0f, 0f));
            return curve;
        }

        /// <summary>Whether the decor pass runs at all.</summary>
        public static bool DecorEnabled => S.decorEnabled;

        /// <summary>Scales every room's decor budget, on top of each set's own density.</summary>
        public static float DecorDensityMultiplier => S.decorDensityMultiplier;

        /// <summary>Budget multiplier by room role. The entry stays sparse; the boss room is the most dressed room in its dungeon.</summary>
        public static float DecorEntryFactor => S.decorEntryFactor;
        public static float DecorCombatFactor => S.decorCombatFactor;
        public static float DecorTreasureFactor => S.decorTreasureFactor;
        public static float DecorBossFactor => S.decorBossFactor;

        /// <summary>Budget multiplier from the first room to the last, so a dungeon gets more dressed as you go in.</summary>
        public static float DecorDepthStart => S.decorDepthStart;
        public static float DecorDepthEnd => S.decorDepthEnd;

        /// <summary>A room this many cells gets the reference budget; bigger rooms get more, within the clamp.</summary>
        public static float DecorReferenceArea => Mathf.Max(1f, S.decorReferenceArea);
        public static float DecorAreaFactorMin => S.decorAreaFactorMin;
        public static float DecorAreaFactorMax => Mathf.Max(S.decorAreaFactorMin, S.decorAreaFactorMax);

        /// <summary>Placement attempts per room before it stops trying.</summary>
        public static int DecorAttemptsPerRoom => S.decorAttemptsPerRoom;

        /// <summary>
        /// Minimum distance between a cave plant and any fire light. Torches reach
        /// 9–10 cells, and vanilla kills cave plants in any light that is not
        /// another cave plant.
        /// </summary>
        public static float DecorPlantFireDistance => S.decorPlantFireDistance;

        /// <summary>Minimum distance between two fire lights in one room, so light is spread rather than stacked.</summary>
        public static float DecorFireSpacing => S.decorFireSpacing;

        /// <summary>Whether carved maps get their rock floors refined and walls smoothed, where the set asks for it.</summary>
        public static bool DecorRefineFloors => S.decorRefineFloors;
        public static bool DecorSmoothWalls => S.decorSmoothWalls;

        /// <summary>Cells between corridor torches, by register. Zero means no corridor torches at that register.</summary>
        public static int[] CorridorLightSpacingByRegister => S.corridorTorchSpacingByRegister;

        /// <summary>Corridor torch spacing at a register with the torch settings applied; zero means none.</summary>
        public static int CorridorLightSpacingFor(DecorRegister register)
        {
            if (!S.decorCorridorTorches)
                return 0;
            int[] table = CorridorLightSpacingByRegister;
            int spacing = table[Mathf.Clamp((int)register, 0, table.Length - 1)];
            return spacing <= 0 ? 0 : Mathf.Max(1, Mathf.RoundToInt(spacing * S.corridorTorchSpacingMultiplier));
        }

        /// <summary>
        /// Dungeon-clear XP by rank. Mirrors the base-mod quest curve
        /// (200 · 2.5^rank, raid-multiplied at the top — the same table
        /// IsekaiQuestNarrative.GetXpForTier uses: F 200 … SSS 1.5M, Nation 3M)
        /// times ClearXpMultiplier. Awarded IN FULL to every colonist inside
        /// the dungeon at clear time — no headcount split (house convention).
        /// </summary>
        public static int ClearXpForRank(MobRankTier rank)
        {
            int[] table = S.clearXpBaseByRank;
            int baseXp = table[Mathf.Clamp((int)rank, 0, table.Length - 1)];
            return (int)Mathf.Min(MaxXpAward, baseXp * ClearXpMultiplier);
        }

        // ── Mana ore ────────────────────────────────────────────────────────
        //
        // Every mined cell drops one small mana core through vanilla yield;
        // these set the chance of a full mana core on top. F sits at 10% and
        // each rank adds 5%, capped at 50%, so an F-rank vein averages about
        // 65 silver a cell (close to compacted steel's ~76) and a high-rank
        // vein is worth roughly twice that. Not settings-backed yet.

        public const string ManaOreBonusCoreDefName = "Isekai_ManaCore";
        private const float ManaOreCoreChanceBase = 0.10f;
        private const float ManaOreCoreChancePerRank = 0.05f;
        private const float ManaOreCoreChanceMax = 0.50f;

        /// <summary>Chance a mined mana ore cell also drops a full mana core, by the map's dungeon rank.</summary>
        public static float ManaOreCoreChanceFor(Map map)
        {
            MobRankTier rank = map.GetComponent<DungeonMapComponent>()?.Rank ?? MobRankTier.F;
            return Mathf.Min(ManaOreCoreChanceMax, ManaOreCoreChanceBase + ManaOreCoreChancePerRank * (int)rank);
        }
    }
}
