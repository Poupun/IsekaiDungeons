using System;
using System.Collections.Generic;
using IsekaiLeveling.MobRanking;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace IsekaiDungeons
{
    /// <summary>
    /// The colony-side dungeon gate. One gate = one dungeon instance; the gate
    /// is the single owner of the dungeon's lifecycle state machine
    /// (Active → Cleared → Collapsing, or Active → Broken).
    ///
    /// Subclasses vanilla MapPortal, which supplies for free: the enter gizmo +
    /// Dialog_EnterPortal, float-menu entry, hauling loot through the portal
    /// (JobDefOf.HaulToPortal / leftToLoad), the contents ITab, and LAZY pocket
    /// map generation — the dungeon interior does not exist until the first
    /// pawn steps through, which also means an ignored gate that breaks never
    /// paid the map-generation cost (the break rolls a fresh pack instead).
    ///
    /// The pocket map is generated through our own DungeonMapParent (rather
    /// than vanilla's PocketMapParent def) so rank + archetype live on the
    /// world object itself; see DungeonMapParent for why.
    /// </summary>
    public class Building_DungeonGate : MapPortal
    {
        public DungeonKindDef kindDef;
        public MobRankTier rank = MobRankTier.F;
        public DungeonState state = DungeonState.Active;
        public bool isRedGate;

        private int openedTick;
        private int breakTick;
        private int clearedTick = -1;
        private int emptySinceTick = -1;
        private bool warnedHalf;
        private bool warnedFinal;
        private bool sentEnteredLetter;
        /// <summary>Latched when map generation threw; the gate is dead but harmless.</summary>
        private bool generationFailed;

        /// <summary>
        /// Continuous difficulty for the curves, or -1 to use the rank enum.
        /// Set by callers that need finer steps than the ten-rung ladder (the
        /// tower); untouched, every curve evaluates exactly where it always did.
        /// </summary>
        public float rankIndex = -1f;

        /// <summary>Layout seed handed to the map parent, or -1 to roll fresh.</summary>
        public int layoutSeed = -1;

        /// <summary>Continuous difficulty, falling back to the enum when unset.</summary>
        public float RankIndex => rankIndex >= 0f ? rankIndex : (int)rank;

        /// <summary>Tick the breach opened. -1 while the gate is still counting down.</summary>
        private int breachStartTick = -1;
        /// <summary>Waves already released. Doubles as the index of the next one.</summary>
        private int wavesSent;

        public int TicksUntilBreak => Mathf.Max(0, breakTick - Find.TickManager.TicksGame);

        /// <summary>Pawns step out of the gate weapons-up — the other side is hostile (PitGate precedent).</summary>
        public override bool AutoDraftOnEnter => true;

        /// <summary>
        /// Must be called BEFORE GenSpawn.Spawn (incident worker / debug tools).
        /// The break clock starts at spawn, not here, so a gate built early and
        /// spawned late doesn't lose time.
        /// </summary>
        public void Initialize(DungeonKindDef def, MobRankTier dungeonRank, bool redGate)
        {
            kindDef = def;
            rank = dungeonRank;
            isRedGate = redGate;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Defs.Look(ref kindDef, "kindDef");
            // Scribe_Defs resolves through GetNamedSilentFail, so a kind that no
            // longer exists (renamed def, uninstalled content pack, an XML file
            // that failed to parse this launch) loads as NULL. SpawnSetup's
            // re-default cannot save us: it sits behind `if (respawningAfterLoad)
            // return`, and load is the ONLY path that produces a null. Every
            // other consumer already falls back to the BreakDungeon def; the gate
            // itself did not, and dereferenced kindDef in GeneratePocketMapInt
            // and DoBreak, throwing outside their guards. Repair it here so the
            // fallback the catalogue XML promises is actually the fifth site.
            if (Scribe.mode == LoadSaveMode.PostLoadInit && kindDef == null)
            {
                kindDef = IsekaiDungeonsDefOf.IsekaiDungeons_BreakDungeon;
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Gate loaded with an unresolvable kindDef; falling back to {kindDef.defName} at rank {rank}.");
            }
            Scribe_Values.Look(ref rank, "rank", MobRankTier.F);
            Scribe_Values.Look(ref state, "state", DungeonState.Active);
            Scribe_Values.Look(ref isRedGate, "isRedGate", defaultValue: false);
            Scribe_Values.Look(ref openedTick, "openedTick");
            Scribe_Values.Look(ref breakTick, "breakTick");
            Scribe_Values.Look(ref clearedTick, "clearedTick", -1);
            Scribe_Values.Look(ref emptySinceTick, "emptySinceTick", -1);
            Scribe_Values.Look(ref warnedHalf, "warnedHalf", defaultValue: false);
            Scribe_Values.Look(ref warnedFinal, "warnedFinal", defaultValue: false);
            Scribe_Values.Look(ref sentEnteredLetter, "sentEnteredLetter", defaultValue: false);
            Scribe_Values.Look(ref generationFailed, "generationFailed", defaultValue: false);
            Scribe_Values.Look(ref rankIndex, "rankIndex", -1f);
            Scribe_Values.Look(ref layoutSeed, "layoutSeed", -1);
            Scribe_Values.Look(ref breachStartTick, "breachStartTick", -1);
            Scribe_Values.Look(ref wavesSent, "wavesSent", 0);
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);

            // Registry membership is by TYPE, so a third-party gate def works
            // exactly like ours (concurrency cap + safety sweep). Registering
            // here means the registry rebuilds itself on load for free.
            DungeonTracker.Register(this);

            // The gate lights the ground in its own colour. Done here rather
            // than in the def because one def serves every rank — the colour is
            // a property of what is BEHIND the gate, not of the building.
            ApplyGateGlow();

            if (respawningAfterLoad)
                return;

            // Only a gate that just TORE ITSELF OPEN plays the opening. One
            // coming back with a save has been standing there for days.
            spawnRealTime = Tween.Now;

            // Defensive defaulting instead of a null-ref cascade if some other
            // spawn path (dev tool, another mod) skipped Initialize (G5).
            if (kindDef == null)
            {
                kindDef = IsekaiDungeonsDefOf.IsekaiDungeons_BreakDungeon;
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Gate spawned without Initialize(); defaulting to {kindDef.defName} at rank {rank}.");
            }

            openedTick = Find.TickManager.TicksGame;
            breakTick = openedTick + (int)(DungeonTunables.BreakTimerDays(kindDef, RankIndex) * GenDate.TicksPerDay);

            EffecterDefOf.ImpactDustCloud.Spawn(Position, map).Cleanup();
            if (Find.CurrentMap == map)
                Find.CameraDriver.shaker.DoShake(0.1f, 120);
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            // Capture the map first: base.DeSpawn clears it.
            Map previousMap = Map;
            base.DeSpawn(mode);
            DungeonTracker.Deregister(this, previousMap);
        }

        // ── Pocket map creation ─────────────────────────────────────────────

        /// <summary>
        /// Same body as PocketMapUtility.GeneratePocketMap, with two changes:
        /// our DungeonMapParent def (so rank/kind ride the world object) and a
        /// rank-scaled map size instead of def.portal.pocketMapSize. The base
        /// class brackets this call with PocketMapUtility.currentlyGeneratingPortal
        /// = this — which is how the exit building and the GenSteps find us —
        /// so we must NOT touch that field ourselves.
        /// </summary>
        protected override Map GeneratePocketMapInt()
        {
            var parent = (DungeonMapParent)WorldObjectMaker.MakeWorldObject(IsekaiDungeonsDefOf.IsekaiDungeons_DungeonMapParent);
            parent.sourceMap = Map;
            // The ENVIRONMENT comes from the dungeon archetype, not from the
            // gate ThingDef: every dungeon shares one gate def, whose
            // <portal><pocketMapGenerator> is only the last-resort default.
            // Reading the ThingDef first would generate every dungeon as a
            // cavern and make the other environments dead data.
            parent.mapGenerator = kindDef.mapGenerator
                ?? def.portal.pocketMapGenerator
                ?? IsekaiDungeonsDefOf.IsekaiDungeons_Dungeon;
            parent.rank = rank;
            parent.kindDef = kindDef;
            // Hand the recipe down. Both are -1 for an ordinary gate, which is
            // what makes this change invisible to the shipped dungeons.
            parent.rankIndex = rankIndex;
            parent.layoutSeed = layoutSeed;

            // CRITICAL: give the pocket map a real planet tile.
            //
            // WorldObjectMaker never assigns one, so a fresh parent carries
            // PlanetTile.Invalid (tileId -1) — and vanilla's own
            // PocketMapUtility.GeneratePocketMap leaves it that way too. That is
            // survivable ONLY for underground biomes: Map.FinalizeInit calls
            // MapPlantGrowthRateCalculator.BuildFor(map), whose
            // CalculateDailyFertility loops over biome.AllWildPlants — an
            // underground biome has none, so the loop body never runs. A SURFACE
            // biome does have plants, so it reaches
            // TileTemperaturesComp.OutdoorTemperatureAt(tile) →
            // RetrieveCachedData → cache[layer][-1] → IndexOutOfRangeException,
            // thrown out of FinalizeInit. The map is already registered in
            // Current.Game.Maps by then, so the game is left ticking a
            // half-initialized map that NREs every frame forever.
            //
            // The colony's own tile is the right answer: the dungeon hangs off
            // that location, and it makes temperature/growth resolve normally.
            parent.Tile = Map.Tile;

            // Upper bound is a TPS guard, not a design limit: it must stay above
            // the top of mapSizeByRank or it silently caps high-rank dungeons and
            // a Nation gate generates no larger than an S one. 320 leaves the
            // shipped curve (150 at F -> 300 at Nation) room to breathe while
            // still refusing a pathological modded curve that would try to build
            // a 1000-cell map.
            int size = Mathf.Clamp(DungeonTunables.ScaledMapSize(kindDef.mapSizeByRank.Evaluate(RankIndex)), 120, 320);

            Map map;
            try
            {
                map = MapGenerator.GenerateMap(new IntVec3(size, 1, size), parent, parent.mapGenerator, null, null, isPocketMap: true);
            }
            catch (System.Exception ex)
            {
                // GenerateMap registers the Map in Current.Game.Maps BEFORE
                // FinalizeInit, so a throw anywhere in generation strands a
                // half-built map that ticks (and errors) forever. Tear it down
                // rather than poisoning the save, and tell the player why the
                // gate did nothing instead of leaving them staring at an
                // unresponsive portal.
                Log.Error($"{IsekaiDungeonsMod.LogPrefix} Dungeon map generation failed for {kindDef.defName} " +
                          $"({parent.mapGenerator?.defName}) at rank {rank}; cleaning up the partial map. {ex}");
                CleanUpFailedMap(parent);
                generationFailed = true;
                Messages.Message(
                    "IsekaiDungeons.Message.GenerationFailed".Translate(),
                    new TargetInfo(Position, Map), MessageTypeDefOf.NegativeEvent);
                return null;
            }

            Find.World.pocketMaps.Add(parent);
            return map;
        }

        /// <summary>
        /// Removes a partially generated pocket map from the game. Mirrors
        /// PocketMapUtility.DestroyPocketMap, but works on the parent alone
        /// because the throw means we never got a Map reference back.
        /// </summary>
        private static void CleanUpFailedMap(DungeonMapParent parent)
        {
            try
            {
                List<Map> maps = Current.Game.Maps;
                for (int i = maps.Count - 1; i >= 0; i--)
                {
                    if (maps[i].Parent == parent)
                    {
                        Current.Game.DeinitAndRemoveMap(maps[i], notifyPlayer: false);
                        break;
                    }
                }
                Find.World.pocketMaps.Remove(parent);
            }
            catch (System.Exception ex)
            {
                Log.Error($"{IsekaiDungeonsMod.LogPrefix} Failed to clean up a broken dungeon map: {ex}");
            }
        }

        public override void OnEntered(Pawn pawn)
        {
            base.OnEntered(pawn);
            // Rank-aware first-entry letter (def.portal.enteredLetter* is static
            // text, so it stays empty in XML and we do it here instead).
            if (!sentEnteredLetter && pawn.Faction == Faction.OfPlayer)
            {
                sentEnteredLetter = true;

                // Composed rather than a fixed paragraph: the letter has to
                // describe THIS dungeon — cave or open field, and how badly it
                // outmatches this particular colony. See DungeonNarrative.
                DungeonMapComponent dungeon = SafePocketMap?.GetComponent<DungeonMapComponent>();
                Find.LetterStack.ReceiveLetter(
                    DungeonNarrative.EntryLetterLabel(kindDef, rank),
                    DungeonNarrative.EntryLetterText(dungeon, this, kindDef, rank, TicksUntilBreak),
                    LetterDefOf.NeutralEvent,
                    exit != null ? (LookTargets)exit : (LookTargets)this);
            }
        }

        // ── State machine ───────────────────────────────────────────────────

        // ── Appearance ──────────────────────────────────────────────────────

        /// <summary>Arc animation state. Runtime only — a flicker is not worth saving.</summary>
        private GateArcState arcState = GateArcState.Fresh;

        /// <summary>
        /// Realtime stamp of the moment this appeared, or -1 for "was already
        /// here". NOT scribed on purpose: a gate restored from a save has
        /// always existed as far as the player is concerned, and replaying the
        /// opening on every load would be a lie about what just happened.
        /// </summary>
        private float spawnRealTime = -1f;


        /// <summary>
        /// Draws the animated portal instead of the def's static graphic.
        ///
        /// base.DrawAt is deliberately NOT called on the success path: the
        /// sequence replaces the flat texture rather than layering over it, so
        /// there is no still image showing through the animation. If the frames
        /// are missing (a partial install, art not yet shipped for a colour),
        /// the base call happens instead and the gate falls back to looking the
        /// way it always did.
        /// </summary>
        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            bool animated = false;
            try
            {
                animated = PortalDrawer.Draw(this, drawLoc, rank, GateVisuals.SizeFor(rank),
                                             ref arcState, withArcs: true,
                                             age: PortalDrawer.AgeOf(spawnRealTime));
            }
            catch (Exception ex)
            {
                Log.ErrorOnce($"{IsekaiDungeonsMod.LogPrefix} Gate visuals failed: {ex}", 0x60D42);
            }

            if (!animated)
                base.DrawAt(drawLoc, flip);
        }

        /// <summary>Points the glower at this gate's rank colour.</summary>
        private void ApplyGateGlow()
        {
            try
            {
                CompGlower glower = GetComp<CompGlower>();
                if (glower != null)
                    glower.GlowColor = GateVisuals.GlowFor(GateVisuals.ColorFor(rank));
            }
            catch (Exception ex)
            {
                Log.WarningOnce($"{IsekaiDungeonsMod.LogPrefix} Could not colour the gate glow: {ex.Message}", 0x60D43);
            }
        }

        protected override void Tick()
        {
            base.Tick();
            if (!Spawned || !this.IsHashIntervalTick(DungeonTunables.GateStateInterval))
                return;

            switch (state)
            {
                case DungeonState.Active:
                    TickActive();
                    break;
                case DungeonState.Cleared:
                    TickCleared();
                    break;
                case DungeonState.Breaching:
                    TickBreaching();
                    break;
                case DungeonState.Collapsing:
                    // Only reachable when a save happened mid-teardown; finish it.
                    Collapse();
                    break;
                case DungeonState.Broken:
                    // Terminal; the gate destroys itself in DoBreak. Reaching
                    // here means that destroy failed — retry.
                    DestroySelf();
                    break;
            }
        }

        private void TickActive()
        {
            int now = Find.TickManager.TicksGame;

            // The latches close whether or not the letter goes out, so turning
            // the letters back on mid-countdown does not replay a warning whose
            // moment has already passed.
            if (!warnedHalf && now >= openedTick + (int)((breakTick - openedTick) * DungeonTunables.WarnHalfFraction))
            {
                warnedHalf = true;
                if (DungeonTunables.CountdownLetters)
                    SendCountdownLetter("IsekaiDungeons.Letter.HalfTime", LetterDefOf.NeutralEvent);
            }

            if (!warnedFinal && TicksUntilBreak <= DungeonTunables.FinalWarningTicks)
            {
                warnedFinal = true;
                if (DungeonTunables.CountdownLetters && DungeonTunables.FinalWarningTicks > 0)
                    SendCountdownLetter("IsekaiDungeons.Letter.FinalWarning", LetterDefOf.ThreatSmall);
            }

            if (now >= breakTick)
                BeginBreach();
        }

        private void SendCountdownLetter(string keyBase, LetterDef letterDef)
        {
            Find.LetterStack.ReceiveLetter(
                (keyBase + ".Label").Translate(MobRankUtility.GetRankString(rank).Named("RANK")),
                (keyBase + ".Text").Translate(
                    DungeonRankUtil.ColoredRankString(rank).Named("RANK"),
                    TicksUntilBreak.ToStringTicksToPeriod().Named("TIME")),
                letterDef, this);
        }

        /// <summary>
        /// A cleared dungeon closes two hours after the last player pawn walks
        /// out — not on a calendar.
        ///
        /// There used to be a flat clearedLingerDays wait (2 days, 3 for
        /// Nation) before this timer was even allowed to start, so a gate the
        /// player had emptied and abandoned still sat there for another two
        /// days. The dungeon should close when the player is done with it,
        /// which is what leaving means.
        /// </summary>
        private void TickCleared()
        {
            // The map counts as "in use" while any player pawn stands on it or
            // portal-hauling is still queued/running — collapsing then would
            // strand pawns or eat the loot train.
            // NOT LoadInProgress: that is just `leftToLoad.Any()`, and the list is
            // only drained by a successful delivery or the player's cancel gizmo.
            // A queued item whose carrier died leaves a permanent entry, which
            // would keep a cleared gate from ever collapsing - and the gate is
            // undeconstructible, so the pocket map would tick forever with no way
            // out. AnyPawnCanLoadAnythingNow is strictly stronger anyway: it
            // already early-outs on !LoadInProgress and additionally requires a
            // pawn to actually be working the portal. The two-hour window
            // absorbs the transient gaps (haulers asleep, repathing).
            bool inUse = AnyPawnCanLoadAnythingNow || AnyPlayerPawnInside();
            if (inUse)
            {
                emptySinceTick = -1;
                return;
            }

            if (emptySinceTick < 0)
            {
                emptySinceTick = Find.TickManager.TicksGame;
                return;
            }

            if (Find.TickManager.TicksGame - emptySinceTick >= DungeonTunables.ClearedEmptyCollapseTicks)
                Collapse();
        }

        private bool AnyPlayerPawnInside()
        {
            Map pm = SafePocketMap;
            if (pm == null)
                return false;
            IReadOnlyList<Pawn> pawns = pm.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                if (pawns[i].Faction == Faction.OfPlayer)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Called exactly once by DungeonMapComponent when the boss goes down.
        /// Valid during a BREACH too: storming a draining dungeon and killing
        /// its boss before the last wave is a real clear, and cancels every
        /// wave that had not gone out yet.
        /// </summary>
        public void Notify_Cleared()
        {
            if (state != DungeonState.Active && state != DungeonState.Breaching)
                return;
            state = DungeonState.Cleared;
            clearedTick = Find.TickManager.TicksGame;
            kindDef?.Worker?.OnCleared(this);

            // Relief for the colonists who stayed home. The party inside gets
            // the clear spike from the map component; this is for everyone who
            // has been living next to a humming gate for a week and can now
            // stop looking at it.
            DungeonMoods.GainGateSealed(Map);

            // The celebration letter (XP + loot LookTargets) is sent by the map
            // component, which owns the reward data.
        }

        // ── The breach ──────────────────────────────────────────────────────
        //
        // The countdown expiring is NOT the end of the dungeon. The gate tears
        // open and the dungeon starts draining itself onto the colony map, one
        // wave a day, weakest first — and it stays OPEN the whole time. A
        // player who ran out of time can still storm in and race the dungeon to
        // its own boss; killing it inside is an ordinary clear and cancels
        // every remaining wave. Otherwise the boss walks out with the last
        // wave carrying its hoard as a bounty (see Hediff_GateBound), and has
        // to be beaten on the player's own ground instead.
        //
        // Waves take a share of what is ACTUALLY still in there, so partial
        // progress shrinks the breach exactly as it always did.

        /// <summary>Tick the wave for index <paramref name="i"/> is due on. Wave 0 fires the instant the gate breaks.</summary>
        private int WaveTick(int i) => breachStartTick + DungeonTunables.BreachWaveIntervalTicks * i;

        public int TicksUntilNextWave =>
            Mathf.Max(0, WaveTick(wavesSent) - Find.TickManager.TicksGame);

        /// <summary>
        /// Starts this gate ALREADY mid-breach, for a guild contract that told
        /// the player the gate was torn open before they got there.
        ///
        /// This exists because setting state = Breaching from outside is not
        /// enough and fails in two ways that both look like different bugs.
        /// breachStartTick is the zero point for every breach timer, and it is
        /// only ever set inside BeginBreach; left at its -1 default,
        /// TicksUntilNextWave evaluates to Max(0, -1 - now) and the inspect
        /// pane shows a wave countdown frozen at zero forever. Worse,
        /// SafetySweep force-finishes any breach older than
        /// breachStartTick + BreachTotalTicks + one day, which against -1 is
        /// just "later than tick 239999" -- true for any game past day four.
        /// The gate therefore called FinishBreach on the first sweep after
        /// arrival and destroyed itself within seconds.
        ///
        /// The clock starts NOW rather than being backdated, because this runs
        /// during map generation, which is the moment the player arrives. Every
        /// downstream timer measures from it, so backdating to fake an
        /// already-elapsed breach would reintroduce the same bug in a subtler
        /// form: waves due in the past and a safety sweep already expired.
        /// </summary>
        /// <param name="wavesAlreadyOut">
        /// How many waves are considered to have come out before the player
        /// arrived. The site part spawns that much of the dungeon on the
        /// surface, so counting them here is what stops the gate sending them
        /// a second time.
        /// </param>
        public void StageAlreadyBreaching(int wavesAlreadyOut)
        {
            state = DungeonState.Breaching;
            breachStartTick = Find.TickManager.TicksGame;
            wavesSent = Mathf.Clamp(wavesAlreadyOut, 0, DungeonTunables.BreachWaveCount);
        }

        /// <summary>Countdown expired. Opens the breach and sends the first wave immediately.</summary>
        public void BeginBreach()
        {
            if (state != DungeonState.Active)
                return;

            state = DungeonState.Breaching;
            breachStartTick = Find.TickManager.TicksGame;
            wavesSent = 0;
            kindDef?.Worker?.OnBreak(this);

            // Colony-wide dread. Fired here AND in DoBreak, which are two
            // separate moments on the same gate; the def carries stackLimit 1
            // so the second refreshes the first rather than doubling it, and
            // neither site needs a latch.
            DungeonMoods.GainGateBroke(Map, rank);

            Find.LetterStack.ReceiveLetter(
                "IsekaiDungeons.Letter.Breach.Label".Translate(MobRankUtility.GetRankString(rank).Named("RANK")),
                "IsekaiDungeons.Letter.Breach.Text".Translate(
                    DungeonRankUtil.ColoredRankString(rank).Named("RANK"),
                    DungeonTunables.BreachWaveCount.Named("WAVES")),
                LetterDefOf.ThreatBig,
                new TargetInfo(Position, Map));

            if (PocketMapAlive)
            {
                SendWave();
                return;
            }

            // A gate nobody entered has no dungeon yet, and the waves have to
            // drain something REAL — so it generates here, once. That is a
            // multi-second job, and doing it inline would be an unexplained
            // freeze at a moment the player did not ask for. A long event makes
            // it a named, visible pause instead, which is how the rest of the
            // game handles map generation.
            LongEventHandler.QueueLongEvent(
                delegate
                {
                    // Re-check: the gate can be gone or cleared by the time the
                    // event pumps.
                    if (Destroyed || state != DungeonState.Breaching)
                        return;
                    Map generated = generationFailed ? null : GetOtherMap();
                    if (generated == null)
                    {
                        // No dungeon to drain and none to storm. Fall back to
                        // the flat eruption rather than silently doing nothing.
                        // (LegacyEruption sets the terminal state itself.)
                        LegacyEruption();
                        return;
                    }
                    SendWave();
                },
                "IsekaiDungeons.LongEvent.OpeningBreach", false, null);
        }

        private void TickBreaching()
        {
            // Nothing to drain yet: the map is still being generated by the
            // long event BeginBreach queued. Waves resume on their own clock
            // once it lands.
            if (!PocketMapAlive && wavesSent == 0)
                return;

            if (wavesSent >= DungeonTunables.BreachWaveCount)
            {
                FinishBreach();
                return;
            }
            if (Find.TickManager.TicksGame < WaveTick(wavesSent))
                return;

            // A gate staged as ALREADY breaching (a broken or rescue contract)
            // starts with wavesSent > 0 and no dungeon behind it, because
            // pocket maps generate lazily on first entry. SendWave cannot
            // drain a map that does not exist, so every later wave released
            // nobody and the final letter announced a boss that never came.
            // Generate it now, the same way BeginBreach does.
            if (!PocketMapAlive && !generationFailed)
            {
                QueueGenerateThenWave();
                return;
            }
            SendWave();
        }

        /// <summary>Set while a generate-then-wave long event is pending, so the tick does not queue a second one.</summary>
        [Unsaved] private bool waveGenerationQueued;

        private void QueueGenerateThenWave()
        {
            if (waveGenerationQueued)
                return;
            waveGenerationQueued = true;
            LongEventHandler.QueueLongEvent(
                delegate
                {
                    waveGenerationQueued = false;
                    if (Destroyed || state != DungeonState.Breaching)
                        return;
                    if (!PocketMapAlive && !generationFailed)
                        GetOtherMap();
                    // Generation failing still sends the wave: an empty wave
                    // advances the clock, so the breach always finishes.
                    SendWave();
                },
                "IsekaiDungeons.LongEvent.OpeningBreach", false, null);
        }

        /// <summary>
        /// Moves one wave's worth of the dungeon onto the colony map. The final
        /// wave takes everything left standing and the boss with it.
        /// </summary>
        private void SendWave()
        {
            int index = wavesSent;
            wavesSent++;
            bool last = index >= DungeonTunables.BreachWaveCount - 1;

            Map colonyMap = Map;
            Map pocket = SafePocketMap;
            DungeonMapComponent comp = pocket?.GetComponent<DungeonMapComponent>();
            Pawn boss = comp?.boss;

            int released = 0;
            if (pocket != null)
            {
                // OUR PEOPLE COME OUT FIRST, and this ordering is load-bearing.
                // The final wave lands around the gate; ejecting the party
                // afterwards would drop them into the middle of half a dungeon
                // and its boss, which would execute precisely the player who
                // took the counterattack this whole feature exists for.
                if (last)
                    EvacuatePlayerPawns(pocket, colonyMap);

                List<Pawn> pool = BreachWaveOrder(pocket, boss);
                float fraction = DungeonTunables.BreachWaveFractions[
                    Mathf.Clamp(index, 0, DungeonTunables.BreachWaveFractions.Length - 1)];
                int take = last
                    ? pool.Count
                    : Mathf.Clamp(Mathf.RoundToInt(pool.Count * fraction), pool.Count > 0 ? 1 : 0, pool.Count);

                // One doorway for the whole wave: they come OUT of the arch,
                // in a stream, rather than materialising in a ring around it.
                List<IntVec3> mouth = GateMouth(colonyMap);
                var arrived = new List<Pawn>(take + (last ? 1 : 0));
                int slot = 0;
                for (int i = 0; i < take; i++)
                {
                    if (ReleaseIntoColony(pool[i], colonyMap, mouth, slot++))
                        arrived.Add(pool[i]);
                }

                // A downed boss stays inside, like every downed creature in the
                // wave pool: the player beat it, so it gets no second go at
                // the colony on its feet.
                if (last && boss != null && !boss.Dead && boss.Spawned && !boss.Downed
                    && boss.Faction != Faction.OfPlayer)
                {
                    if (ReleaseIntoColony(boss, colonyMap, mouth, slot++))
                    {
                        // The hoard follows its owner: killing it out here now
                        // pays what clearing the dungeon would have.
                        Hediff_GateBound.Bind(boss, kindDef, rank);
                        arrived.Add(boss);
                    }
                }

                released = arrived.Count;
                DungeonHostility.Wake(arrived, colonyMap);

                // Something visibly comes through, rather than creatures simply
                // appearing next to the building.
                if (released > 0)
                    EffecterDefOf.ImpactDustCloud.Spawn(Position, colonyMap).Cleanup();

                // Tell anyone still inside why the place is emptying around
                // them — otherwise creatures simply vanish with no explanation.
                if (pocket.mapPawns.FreeColonistsSpawnedCount > 0)
                {
                    Messages.Message(
                        "IsekaiDungeons.Message.WaveDeparting".Translate(),
                        new TargetInfo(comp?.exit?.Position ?? pocket.Center, pocket),
                        MessageTypeDefOf.ThreatBig);
                }
            }

            string keyBase = last ? "IsekaiDungeons.Letter.BreachFinal" : "IsekaiDungeons.Letter.BreachWave";
            // The final wave always announces itself: the boss is out, and that
            // is not something a setting should let the player miss.
            if (last || DungeonTunables.BreachWaveLetters)
            {
                Find.LetterStack.ReceiveLetter(
                    (keyBase + ".Label").Translate(
                        (index + 1).Named("WAVE"),
                        DungeonTunables.BreachWaveCount.Named("WAVES")),
                    (keyBase + ".Text").Translate(
                        DungeonRankUtil.ColoredRankString(rank).Named("RANK"),
                        released.Named("COUNT"),
                        (index + 1).Named("WAVE"),
                        DungeonTunables.BreachWaveCount.Named("WAVES")),
                    LetterDefOf.ThreatBig,
                    new TargetInfo(Position, colonyMap));
            }

            if (last)
                FinishBreach();
        }

        /// <summary>
        /// Who leaves next, weakest first.
        ///
        /// Rooms and open-field zones are both laid out in a chain from the way
        /// in to the boss, in ascending power — so DISTANCE FROM THE EXIT
        /// PORTAL is the difficulty ordering, already computed by the
        /// generator. Sorting on it rather than on room membership also
        /// catches everything a room event spawned, which never went into
        /// room.creatures and would otherwise be invisible to the breach.
        /// </summary>
        private List<Pawn> BreachWaveOrder(Map pocket, Pawn boss)
        {
            DungeonMapComponent comp = pocket.GetComponent<DungeonMapComponent>();
            IntVec3 mouth = comp?.exit?.Position ?? pocket.Center;

            var pool = new List<Pawn>();
            System.Collections.Generic.IReadOnlyList<Pawn> all = pocket.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < all.Count; i++)
            {
                Pawn p = all[i];
                // Downed creatures stay: the player beat them, so they do not
                // get a second go at the colony. They die with the map.
                if (p.Dead || p.Downed || p == boss)
                    continue;
                if (p.Faction == Faction.OfPlayer)
                    continue;
                pool.Add(p);
            }
            // Weakest first. Distance from the way out was a fine proxy while
            // every dungeon was a straight chain, but a branching layout can put
            // a deep wing physically close to the entrance. Sort by the room's
            // AUTHORED depth where we have it, keeping distance as the tiebreak
            // and as the whole answer for pre-branching saves, whose depthFrac
            // was only ever written for a third of their rooms.
            var depthOf = new Dictionary<Pawn, float>(pool.Count);
            bool authored = false;
            if (comp?.rooms != null)
            {
                for (int r = 0; r < comp.rooms.Count; r++)
                {
                    DungeonRoom room = comp.rooms[r];
                    if (!room.depthAuthored)
                        continue;
                    authored = true;
                    for (int p = 0; p < pool.Count; p++)
                    {
                        if (room.rect.Contains(pool[p].Position))
                            depthOf[pool[p]] = room.depthFrac;
                    }
                }
            }

            if (authored)
            {
                pool.SortBy(p => depthOf.TryGetValue(p, out float d) ? d : 1f,
                            p => p.Position.DistanceToSquared(mouth));
            }
            else
            {
                pool.SortBy(p => p.Position.DistanceToSquared(mouth));
            }
            return pool;
        }

        private bool ReleaseIntoColony(Pawn pawn, Map colonyMap, List<IntVec3> mouth, int slot)
        {
            // Cut every tie to the dungeon first. DeSpawn does not clear a
            // pawn's lord, and the pocket-map lord dies with the pocket map at
            // FinishBreach, leaving the creature with a dead lord (and Wake
            // skips anything that already has one). The room registry kept it
            // too, so the autopilot, aggro and leash sweeps chased a pawn that
            // was now standing on the colony map.
            pawn.GetLord()?.Notify_PawnLost(pawn, PawnLostCondition.ExitedMap);
            SafePocketMap?.GetComponent<DungeonMapComponent>()?.ForgetCreature(pawn);

            if (pawn.Spawned)
                pawn.DeSpawn();
            if (!SpawnFromGate(pawn, colonyMap, mouth, slot))
            {
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Could not place breach wave pawn {pawn.LabelShort} near the gate; passing to world pawns.");
                if (!pawn.Dead && !pawn.Destroyed && pawn.holdingOwner == null)
                    Find.WorldPawns.PassToWorld(pawn);
                return false;
            }
            return true;
        }

        /// <summary>
        /// The gate's doorway: every standable cell touching its own footprint.
        ///
        /// The gate is a 5x5 impassable building, so "out of the gate" can only
        /// mean the ring of cells against it — pawns cannot stand on the arch
        /// itself. Built once per wave rather than per pawn.
        /// </summary>
        private List<IntVec3> GateMouth(Map map)
        {
            var cells = new List<IntVec3>();
            if (map == null)
                return cells;
            foreach (IntVec3 c in this.OccupiedRect().ExpandedBy(1).EdgeCells)
            {
                if (c.InBounds(map) && c.Standable(map))
                    cells.Add(c);
            }
            return cells;
        }

        /// <summary>
        /// Puts a pawn on the gate's doorstep. <paramref name="slot"/> walks the
        /// doorway cells in order, so a wave emerges as a stream of neighbours
        /// pushing out of the arch instead of a scatter around the building.
        /// Cells repeat once the wave is bigger than the doorway; RimWorld
        /// separates co-located pawns on its own, which reads exactly like a
        /// crowd forcing its way through.
        /// </summary>
        private bool SpawnFromGate(Pawn pawn, Map colonyMap, List<IntVec3> mouth, int slot)
        {
            IntVec3 cell = IntVec3.Invalid;
            if (mouth != null && mouth.Count > 0)
                cell = mouth[Mathf.Abs(slot) % mouth.Count];
            // Gate walled in on every side (a player has built around it, or it
            // landed in a crevice): widen rather than drop the pawn onto the
            // impassable footprint.
            if (!cell.IsValid)
                cell = CellFinder.StandableCellNear(Position, colonyMap, 16);
            if (!cell.IsValid)
                return false;
            GenSpawn.Spawn(pawn, cell, colonyMap);
            return pawn.Spawned;
        }

        // GiveArrivalsAPurpose MOVED to DungeonHostility.Wake. It had the right
        // answer for humanlikes (one assault lord per faction) but only ever
        // ran on the wave path, and it skipped any pawn whose Faction was null
        // — which was every dungeon dweller, since they all spawned wild. Now
        // that humanlikes are generated into AncientsHostile, the same logic
        // covers the wave, the terminal break AND in-dungeon room aggro from
        // one place.

        /// <summary>
        /// Gets player pawns out of the dungeon. No stun: they walked in under
        /// their own power and are about to need every tick they have. The stun
        /// belongs to Collapse(), where nothing hostile is arriving with them.
        /// </summary>
        private void EvacuatePlayerPawns(Map pocket, Map colonyMap)
        {
            List<IntVec3> mouth = GateMouth(colonyMap);
            List<Pawn> inside = new List<Pawn>(pocket.mapPawns.AllPawnsSpawned);
            int slot = 0;
            for (int i = 0; i < inside.Count; i++)
            {
                Pawn p = inside[i];
                if (!p.Dead && p.Faction == Faction.OfPlayer)
                    EjectPawn(p, colonyMap, stun: false, mouth, slot++);
            }
        }

        /// <summary>The dungeon is empty: eject anyone still inside and tear it down.</summary>
        private void FinishBreach()
        {
            state = DungeonState.Broken;
            Map colonyMap = Map;

            if (PocketMapAlive)
            {
                // Normally a no-op: SendWave already evacuated before releasing
                // the final wave. This catches the safety-net path, where
                // FinishBreach runs without a wave ever having fired.
                EvacuatePlayerPawns(SafePocketMap, colonyMap);
                PocketMapUtility.DestroyPocketMap(SafePocketMap);
            }

            Messages.Message(
                "IsekaiDungeons.Message.BreachSpent".Translate(MobRankUtility.GetRankString(rank).Named("RANK")),
                new TargetInfo(Position, colonyMap), MessageTypeDefOf.NeutralEvent);

            DestroySelf();
        }

        /// <summary>
        /// The pre-wave behaviour, kept for the one case that cannot have a
        /// breach: a dungeon whose map will not generate. Rolls the roster and
        /// dumps it in one go, exactly as the gate used to.
        /// </summary>
        private void LegacyEruption()
        {
            state = DungeonState.Broken;
            Map colonyMap = Map;
            int raiders = 0;

            float budget = kindDef.encounterPointsByRank.Evaluate(RankIndex)
                           * Mathf.Max(1f, DungeonTunables.RoomCountFor(kindDef, RankIndex))
                           * DungeonTunables.EncounterMultiplierFor(colonyMap);
            List<PawnKindDef> pack = DungeonRoster.RollRosterPack(kindDef, rank, budget);
            if (pack.Count == 0)
                pack = DungeonSpawnResolver.RollPack(rank, budget);
            List<IntVec3> mouth = GateMouth(colonyMap);
            var burst = new List<Pawn>(pack.Count);
            for (int i = 0; i < pack.Count; i++)
            {
                Pawn p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(pack[i],
                    DungeonHostility.FactionFor(pack[i]),
                    PawnGenerationContext.NonPlayer, forceGenerateNewPawn: true));
                if (SpawnFromGate(p, colonyMap, mouth, i))
                {
                    DungeonSpawnResolver.NormalizeRank(p, rank);
                    burst.Add(p);
                    raiders++;
                }
            }

            // As ONE group. This path used to wake each pawn individually and
            // never built a lord at all, so a humanlike pack burst out of the
            // gate leaderless — the exact failure GiveArrivalsAPurpose was
            // written for on the wave path but was never wired to here.
            DungeonHostility.Wake(burst, colonyMap);

            kindDef?.Worker?.OnBreak(this);

            DungeonMoods.GainGateBroke(colonyMap, rank);

            Find.LetterStack.ReceiveLetter(
                "IsekaiDungeons.Letter.Break.Label".Translate(MobRankUtility.GetRankString(rank).Named("RANK")),
                "IsekaiDungeons.Letter.Break.Text".Translate(
                    DungeonRankUtil.ColoredRankString(rank).Named("RANK"),
                    raiders.Named("COUNT")),
                LetterDefOf.ThreatBig,
                new TargetInfo(Position, colonyMap));

            DestroySelf();
        }

        private void EjectPawn(Pawn pawn, Map colonyMap, bool stun, List<IntVec3> mouth = null, int slot = 0)
        {
            // DeSpawn+Spawn keeps inventory/equipment intact — the pawn itself
            // is the container. Loose items left on the dungeon floor are lost
            // with the map (documented, intentional).
            if (pawn.Spawned)
                pawn.DeSpawn();
            if (!SpawnFromGate(pawn, colonyMap, mouth ?? GateMouth(colonyMap), slot))
            {
                // Never delete a player pawn because a cell was missing (G5):
                // world-pawn limbo keeps them alive and recoverable.
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Could not place ejected pawn {pawn.LabelShort} near gate; passing to world pawns.");
                if (!pawn.Dead && !pawn.Destroyed && pawn.holdingOwner == null)
                    Find.WorldPawns.PassToWorld(pawn);
                return;
            }
            if (stun)
                pawn.stances?.stunner?.StunFor(DungeonTunables.EjectStunTicks, this);
        }

        // TryMakeManhunter REMOVED. It forced ManhunterPermanent one pawn at a
        // time, which is right for animals and actively harmful for humanlikes:
        // an aggro mental state makes a pawn hostile to everything on the map,
        // including the rest of its own squad. Both breach paths now hand their
        // whole group to DungeonHostility.Wake, which splits by race and is the
        // single place that decides how a dungeon dweller turns hostile.

        // ── Teardown ────────────────────────────────────────────────────────

        /// <summary>Post-clear teardown: eject stragglers (belt and braces), destroy the map, close the gate.</summary>
        public void Collapse()
        {
            state = DungeonState.Collapsing;
            Map colonyMap = Map;

            if (PocketMapAlive)
            {
                List<Pawn> pocketPawns = new List<Pawn>(SafePocketMap.mapPawns.AllPawnsSpawned);
                for (int i = 0; i < pocketPawns.Count; i++)
                {
                    Pawn p = pocketPawns[i];
                    if (!p.Dead && p.Faction == Faction.OfPlayer)
                        EjectPawn(p, colonyMap, stun: true);
                }
                PocketMapUtility.DestroyPocketMap(SafePocketMap);
            }

            Messages.Message(
                "IsekaiDungeons.Message.Collapsed".Translate(MobRankUtility.GetRankString(rank).Named("RANK")),
                new TargetInfo(Position, colonyMap), MessageTypeDefOf.NeutralEvent);

            DestroySelf();
        }

        private void DestroySelf()
        {
            if (Destroyed)
                return;
            Map map = Map;
            EffecterDefOf.ImpactDustCloud.Spawn(Position, map).Cleanup();
            // destroyable=false in XML keeps players/raids from wrecking the
            // gate; we lift that only for our own teardown (PitGate pattern).
            Thing.allowDestroyNonDestroyable = true;
            Destroy(DestroyMode.Deconstruct);
            Thing.allowDestroyNonDestroyable = false;
        }

        /// <summary>
        /// Called by DungeonTracker every ~2500 ticks. Repairs states that a
        /// crash/kill/save-corruption left dangling so a gate can never wedge
        /// the colony map permanently.
        /// </summary>
        /// <summary>
        /// Whether this gate's pocket map is still a real, live map.
        ///
        /// MapPortal.PocketMap reads map.Parent, and Map.Parent is a bare
        /// `info.parent` with no null guard — so a reference to a map that was
        /// already torn down throws rather than reporting "gone". Every call
        /// site here wants "is there a dungeon behind me", and a destroyed map
        /// is a no, not an exception.
        /// </summary>
        private Map SafePocketMap
        {
            get
            {
                try
                {
                    Map pocket = PocketMap;
                    return pocket != null && Find.Maps.Contains(pocket) ? pocket : null;
                }
                catch
                {
                    return null;
                }
            }
        }

        private bool PocketMapAlive => SafePocketMap != null;

        public void SafetySweep()
        {
            // A gate whose map has been removed is not ours to tidy: despawning
            // it walks into a torn-down ListerThings and throws. The tracker
            // prunes those entries; this is the belt to its braces.
            if (!Spawned || Map == null || !Find.Maps.Contains(Map))
                return;
            if (state == DungeonState.Active && Find.TickManager.TicksGame > breakTick + DungeonTunables.GateStateInterval * 4)
                BeginBreach();
            else if (state == DungeonState.Breaching
                     && Find.TickManager.TicksGame > breachStartTick + DungeonTunables.BreachTotalTicks + GenDate.TicksPerDay)
                FinishBreach();
            // Belt to TickCleared's braces. Normally a cleared gate closes two
            // hours after the last pawn leaves; if one is still sitting here a
            // full day later with nobody inside, the normal path did not run
            // and the gate would otherwise tick forever with no way to remove
            // it (it is undeconstructible). Deliberately checks only for pawns,
            // not for hauling jobs — a job queue stuck on a dead carrier is
            // exactly the failure this is here to escape.
            else if (state == DungeonState.Cleared
                     && clearedTick >= 0
                     && Find.TickManager.TicksGame > clearedTick + GenDate.TicksPerDay
                     && !AnyPlayerPawnInside())
                Collapse();
            else if (state == DungeonState.Collapsing || state == DungeonState.Broken)
                DestroySelf();
        }

        // ── UI ──────────────────────────────────────────────────────────────

        public override bool IsEnterable(out string reason)
        {
            // Breaching is deliberately NOT blocked. A gate that has broken
            // open is still a way in, and storming a draining dungeon to kill
            // its boss before the last wave is the whole counterattack.
            if (state == DungeonState.Collapsing || state == DungeonState.Broken)
            {
                reason = "IsekaiDungeons.Gate.CollapsingReason".Translate();
                return false;
            }
            // Generation already failed once. Retrying would just throw again
            // (and vanilla's JobDriver_EnterPortal would NRE on the null map),
            // so the gate stays shut instead of trapping the player in a loop.
            if (generationFailed)
            {
                reason = "IsekaiDungeons.Gate.GenerationFailedReason".Translate();
                return false;
            }
            return base.IsEnterable(out reason);
        }

        public override string GetInspectString()
        {
            string baseStr = base.GetInspectString();
            var sb = new System.Text.StringBuilder(baseStr);
            if (sb.Length > 0)
                sb.AppendLine();

            sb.AppendLine("IsekaiDungeons.Inspect.Rank".Translate(
                DungeonRankUtil.ColoredRankString(rank).Named("RANK")));

            switch (state)
            {
                case DungeonState.Active:
                    sb.Append("IsekaiDungeons.Inspect.BreakIn".Translate(
                        TicksUntilBreak.ToStringTicksToPeriod().Named("TIME")));
                    break;
                case DungeonState.Breaching:
                    sb.AppendLine("IsekaiDungeons.Inspect.Breaching".Translate(
                        wavesSent.Named("SENT"),
                        DungeonTunables.BreachWaveCount.Named("WAVES")));
                    sb.Append(wavesSent >= DungeonTunables.BreachWaveCount
                        ? "IsekaiDungeons.Inspect.BreachSpent".Translate()
                        : "IsekaiDungeons.Inspect.NextWaveIn".Translate(
                            TicksUntilNextWave.ToStringTicksToPeriod().Named("TIME")));
                    break;
                case DungeonState.Cleared:
                    // Two readings, and the player needs to be able to tell
                    // them apart: either the countdown to collapse is actually
                    // running (nobody inside), or it is not because someone is
                    // still down there. Showing a timer in the second case
                    // would be a lie — it does not tick while the map is in use.
                    if (emptySinceTick >= 0)
                        sb.Append("IsekaiDungeons.Inspect.ClearedLinger".Translate(
                            Mathf.Max(0, DungeonTunables.ClearedEmptyCollapseTicks
                                         - (Find.TickManager.TicksGame - emptySinceTick))
                                .ToStringTicksToPeriod().Named("TIME")));
                    else
                        sb.Append("IsekaiDungeons.Inspect.WaitingEmpty".Translate());
                    break;
            }
            return sb.ToString().TrimEndNewlines();
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo gizmo in base.GetGizmos())
                yield return gizmo;

            if (!DebugSettings.ShowDevGizmos)
                yield break;

            yield return new Command_Action
            {
                defaultLabel = "DEV: Clear dungeon",
                icon = def.uiIcon,   // no icon = vanilla's red X, dev gizmos included
                action = delegate
                {
                    var comp = SafePocketMap?.GetComponent<DungeonMapComponent>();
                    if (comp != null)
                        comp.DebugForceClear();
                    else
                        Notify_Cleared();
                }
            };
            yield return new Command_Action
            {
                defaultLabel = "DEV: Break now (start breach)",
                icon = def.uiIcon,
                action = BeginBreach
            };
            yield return new Command_Action
            {
                defaultLabel = "DEV: Next breach wave",
                icon = def.uiIcon,
                action = delegate
                {
                    if (state == DungeonState.Breaching)
                        SendWave();
                }
            };
            yield return new Command_Action
            {
                defaultLabel = "DEV: Collapse now",
                icon = def.uiIcon,
                action = Collapse
            };
        }
    }
}
