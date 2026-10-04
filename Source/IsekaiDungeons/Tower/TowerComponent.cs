using System.Collections.Generic;
using RimWorld;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// The tower's memory: which floors exist, which have been seen, which have
    /// been beaten.
    ///
    /// A GameComponent rather than a WorldComponent because the tower belongs
    /// to a playthrough, and because GameComponent's ExposeData runs in a phase
    /// where Find.Maps is populated — the world's is not, which matters for any
    /// reconcile that has to look at live maps.
    ///
    /// Deliberately small. Floors above the frontier are not stored at all;
    /// TowerLadder recomputes them from (towerSeed, floor) on demand. Only a
    /// floor the player has actually reached gets a record, so a full 100-floor
    /// run costs on the order of ten kilobytes of save.
    /// </summary>
    public class TowerComponent : GameComponent
    {
        /// <summary>Seeds every floor's identity. Rolled once when the tower appears.</summary>
        public int towerSeed;

        /// <summary>Highest floor the player has legitimately climbed to. Gates the picker.</summary>
        public int highestReached;

        /// <summary>
        /// The summit reward has been paid, and Elijah is dead. Latched forever:
        /// a tower pays once. From this moment the tower is coming down; it
        /// only waits for the last party to walk out (TrySinkTheTower), and
        /// nobody may walk back in (WorldObject_Tower, CaravanArrivalAction_EnterTower).
        /// </summary>
        public bool summitClaimed;

        /// <summary>
        /// The Guild's open survey contract, for the quest tab. Cosmetic: every
        /// behaviour lives on this component, and a null here (an old save, or
        /// a quest the player deleted) changes nothing except that there is no
        /// entry to look at.
        /// </summary>
        public RimWorld.Quest towerQuest;

        /// <summary>
        /// The tower has collapsed. A permanent record of the world, not of the
        /// current tower.
        ///
        /// THE ONE FLAG EndTower MUST NOT CLEAR. Raising is latched by nothing
        /// except Exists, and EndTower zeroes the seed, so without this the
        /// tower became eligible to rise again on the next quarter-second poll
        /// — a fresh guild notice for a structure the player had just watched
        /// sink, and a letter that had promised there was no reason to think the
        /// entry was ever wrong. Kept separate from the raise latch so a dev
        /// teardown can still raise a replacement; only the real ending closes
        /// the door.
        /// </summary>
        public bool towerSank;

        /// <summary>Who put damage into the final boss, and how much.</summary>
        private TowerSummitDamage summitDamage = new TowerSummitDamage();

        public TowerSummitDamage SummitDamage => summitDamage ?? (summitDamage = new TowerSummitDamage());

        /// <summary>Records for floors that have been seen. Sparse and ordered by floor.</summary>
        private List<TowerFloorRecord> records = new List<TowerFloorRecord>();

        public TowerComponent(Game game)
        {
        }

        public static TowerComponent Get => Current.Game?.GetComponent<TowerComponent>();

        // ── Autopilot toggle state ──────────────────────────────────────────
        // Serves BOTH the tower climb and ordinary gate-dungeon sweeps: it is
        // one toggle, and DungeonAutopilot decides what "finished" means from
        // the map it is standing on. It lives HERE rather than in
        // DungeonAutopilot because the toggle must survive a save, this
        // component exists for every game, and DungeonTracker — the other
        // candidate — is deliberately scribe-free (its registry rebuilds from
        // SpawnSetup). A List rather than a HashSet because Scribe_Collections
        // reference-scribes lists natively.

        /// <summary>Pawns with the auto-grind toggle on. DungeonAutopilot reads this each interval.</summary>
        private List<Pawn> autopilotPawns = new List<Pawn>();

        public List<Pawn> AutopilotPawns => autopilotPawns;

        public bool AutopilotOn(Pawn pawn) =>
            pawn != null && autopilotPawns.Contains(pawn);

        public void SetAutopilot(Pawn pawn, bool on)
        {
            if (pawn == null)
            {
                autopilotPawns.RemoveAll(p => p == null);
                return;
            }
            if (on)
            {
                if (!autopilotPawns.Contains(pawn))
                    autopilotPawns.Add(pawn);
            }
            else
                autopilotPawns.Remove(pawn);
        }

        public bool Exists => towerSeed != 0;

        public IReadOnlyList<TowerFloorRecord> Records => records;

        /// <summary>Starts a tower. Idempotent — a second call while one is live is ignored.</summary>
        public void BeginTower(int seed)
        {
            if (Exists)
                return;
            towerSeed = seed == 0 ? 1 : seed;
            highestReached = 0;
            records.Clear();
            towerQuest = TowerQuest.Post();
        }

        /// <summary>Forgets everything. Called when the tower is finished or removed.</summary>
        public void EndTower()
        {
            towerSeed = 0;
            highestReached = 0;
            summitClaimed = false;
            // Whatever else removed the tower, the contract cannot stay open on
            // a building that is no longer on the map. A tower torn down any
            // other way (dev tools, a future scenario) closes the quest as a
            // failure, because the survey was never filed.
            TowerQuest.Close(towerQuest, RimWorld.QuestEndOutcome.Fail);
            towerQuest = null;
            SummitDamage.Clear();
            records.Clear();
        }

        /// <summary>
        /// The record for a floor, materialising it from the ladder on first
        /// ask. Once materialised it is frozen: a floor the player has seen can
        /// never change identity, even if the modlist does.
        /// </summary>
        public TowerFloorRecord EnsureRecord(int floor)
        {
            for (int i = 0; i < records.Count; i++)
            {
                if (records[i].floor == floor)
                    return records[i];
            }
            TowerFloorRecord rec = TowerLadder.Roll(towerSeed, floor);
            records.Add(rec);
            records.SortBy(r => r.floor);
            return rec;
        }

        /// <summary>
        /// What a floor WOULD be, without committing to it.
        ///
        /// EnsureRecord freezes: once called, that floor's identity is written
        /// to the save and can never change again. That is correct when the
        /// player actually goes there and wrong for everything else — a menu
        /// label, a picker row, a scan looking for the next village. Those all
        /// used to call EnsureRecord, which meant merely LOOKING at the ladder
        /// nailed it down, and a later rebalance could never reach that save.
        ///
        /// Roll is the same pure function of (seed, floor) with no side effect,
        /// so a preview is free and a frozen floor still wins when it exists.
        /// </summary>
        public TowerFloorRecord Preview(int floor) =>
            RecordIfSeen(floor) ?? TowerLadder.Roll(towerSeed, floor);

        /// <summary>The record if one exists, without creating it.</summary>
        public TowerFloorRecord RecordIfSeen(int floor)
        {
            for (int i = 0; i < records.Count; i++)
            {
                if (records[i].floor == floor)
                    return records[i];
            }
            return null;
        }

        /// <summary>
        /// Marks a floor reached. DISCOVERY IS A CONSEQUENCE OF CLIMBING, and
        /// that is load-bearing: if arriving on a floor were enough, a player
        /// could reach floor N, leave, and use the picker to jump to N+1 —
        /// skipping N's boss entirely. Only the stair calls this, and the stair
        /// is sealed until the floor's objective is met.
        /// </summary>
        public void NotifyReached(int floor)
        {
            TowerFloorRecord rec = EnsureRecord(floor);
            rec.discovered = true;
            if (floor > highestReached)
                highestReached = floor;
        }

        /// <summary>Floors the picker may offer: everything seen, plus the entrance.</summary>
        public List<TowerFloorRecord> DiscoveredFloors()
        {
            var list = new List<TowerFloorRecord>();
            for (int i = 0; i < records.Count; i++)
            {
                if (records[i].discovered)
                    list.Add(records[i]);
            }
            return list;
        }

        /// <summary>
        /// Drops records the player never actually touched.
        ///
        /// A record is TWO things welded together: a cached roll, and whatever
        /// the player did to that floor. When the delta is empty the record is
        /// pure cache — Roll(seed, floor) reproduces it exactly — so throwing it
        /// away loses nothing and re-rolls it against the CURRENT def database.
        ///
        /// Without this, a ladder frozen under an older archetype table is
        /// frozen forever: a floor materialised before a new floor type existed
        /// can never become that type, and no rebalance, new archetype or
        /// content addition would ever reach that save. Records carrying real
        /// progress — reached, cleared, treasure taken, a village razed — are
        /// never touched, so the climb itself is preserved exactly.
        /// </summary>
        private void PruneUnvisited()
        {
            int before = records.Count;
            records.RemoveAll(r => r != null
                                   && !r.discovered
                                   && !r.cleared
                                   && r.treasureTakenMask == 0UL
                                   && r.villageState == TowerVillageState.None);
            records.RemoveAll(r => r == null);
            int dropped = before - records.Count;
            if (dropped > 0)
            {
                Log.Message($"{IsekaiDungeonsMod.LogPrefix} Re-rolled {dropped} unvisited tower floor(s) "
                          + "against the current def database; visited floors kept as they were.");
            }
        }

        /// <summary>
        /// Watches for the top floor being beaten, and pays the summit reward.
        ///
        /// THIS LIVES ON THE GAME COMPONENT, and that is not arbitrary. The
        /// reward has to outlive the map that earned it: the moment the party
        /// leaves floor 100, that map is destroyed. It used to hang off the
        /// colony-side tower gate, which was the only other thing guaranteed to
        /// still exist — and when the gate was removed (the tower became a
        /// world destination with no building), the watch went with it and
        /// nothing granted the reward at all. A GameComponent cannot be
        /// deleted by a refactor of the map layer.
        /// </summary>
        /// <summary>
        /// Fires on both a new game and a load.
        ///
        /// The OST watch keeps its "what was playing last" state in a static,
        /// which outlives a save being closed. Without this, loading a save
        /// while the previous session's static still remembers a dungeon track
        /// would compare the new game against the old one's music.
        /// </summary>
        public override void FinalizeInit()
        {
            base.FinalizeInit();
            DungeonAutopilot.ResetRuntimeState();
            DungeonOST.Reset();
            sessionStartTick = Find.TickManager.TicksGame;
        }

        /// <summary>
        /// TicksGame when this game was started or loaded. Not scribed on
        /// purpose: the raise countdown restarts with every session, which is
        /// what makes a save that never had a tower get one a few seconds after
        /// loading. See TowerTunables.RaiseAfterTicks.
        /// </summary>
        private int sessionStartTick = -1;

        public override void GameComponentTick()
        {
            base.GameComponentTick();

            // BEFORE the early-outs below, deliberately: gods mostly exist
            // AFTER summitClaimed latches, and a save can hold one from before
            // the ascended-body system shipped even if this world's tower never
            // rose. The reconciler self-gates to every 250 ticks.
            GodhoodBodyUtility.ReconcileTick();

            // Also before them: the auto-grind loop serves gate dungeons too,
            // so it must run when no tower EXISTS at all, after the summit is
            // claimed, and while its map is not the current one. Self-gates on
            // an empty toggle list, so a game that never toggles it pays two
            // comparisons.
            DungeonAutopilot.Tick(this);

            if (!Exists)
            {
                // Once it has gone down it stays down. See towerSank.
                if (!towerSank)
                    TryRaiseOnSchedule();
                return;
            }
            // Cheap: a quarter-second poll costs nothing next to map ticking.
            // IsHashIntervalTick is a Map extension, so a GameComponent does the
            // modulo itself.
            if (Find.TickManager.TicksGame % 250 != 0)
                return;

            if (!summitClaimed)
            {
                CheckSummitReward();
                return;
            }

            // Elijah is dead and the tower is coming down. It waits only for
            // the last party to be clear of it.
            TrySinkTheTower();
        }

        /// <summary>
        /// Brings the tower down, once there is nobody left inside it.
        ///
        /// THE WAIT IS THE POINT, and it is also the rule that keeps this safe:
        /// a floor is a pocket map, and destroying the world object under an
        /// open one would strand whoever is standing on it. Holding until the
        /// last party has walked out means the letter can promise they watched
        /// it go down, and the code honours that instead of contradicting it.
        ///
        /// Order matters. The letter is sent while the tower still exists, so
        /// its target is still a real world object and the camera can find it;
        /// the quest closes silently underneath it; the object goes last.
        /// </summary>
        private void TrySinkTheTower()
        {
            if (!TowerQuest.SafeToSink())
                return;

            // LATCHED FIRST, and before anything that can throw. This runs on
            // the game tick, so a failure partway through would otherwise be
            // retried every quarter second forever (regression ledger, guard
            // 15). Setting it here means the worst case is a tower that stays
            // on the map with its contract closed, which is inspectable and
            // fixable, rather than an error every 250 ticks for the rest of the
            // save.
            towerSank = true;

            try
            {
                WorldObject_Tower tower = TowerFloors.Tower;

                Find.LetterStack.ReceiveLetter(
                    "IsekaiDungeons.Tower.Sank.Label".Translate(),
                    "IsekaiDungeons.Tower.Sank.Text".Translate(
                        TowerTunables.FloorCount.Named("FLOORS")),
                    LetterDefOf.PositiveEvent,
                    tower != null && tower.Spawned ? (LookTargets)tower : LookTargets.Invalid);

                TowerQuest.Close(towerQuest, RimWorld.QuestEndOutcome.Success);
                towerQuest = null;

                // EndTower would close the contract as a failure, so it is
                // cleared above first. It also wipes the records and the seed,
                // which is what makes the world forget: nothing is left that
                // could name the place. towerSank deliberately survives it.
                EndTower();

                if (tower != null && !tower.Destroyed)
                    tower.Destroy();
            }
            catch (System.Exception ex)
            {
                Log.Error($"{IsekaiDungeonsMod.LogPrefix} The tower failed to sink cleanly. It will not "
                        + $"retry, and it will not rise again. {ex}");
            }
        }

        /// <summary>
        /// Raises the tower a few seconds into the session, on a new game and on
        /// an old save that never had one alike.
        ///
        /// A SCHEDULE, not a roll. The tower is the shape of a playthrough
        /// rather than an event inside one, so it should not depend on winning
        /// a 0.6-weight lottery against every raid in the game. Checked a few
        /// times a second and latched by BeginTower, so it can only ever
        /// happen once.
        /// </summary>
        private void TryRaiseOnSchedule()
        {
            if (Find.TickManager.TicksGame % 250 != 0)
                return;
            // The switch only stops a tower RISING. One that already stands is
            // a place with colonists' history in it and is never removed by a
            // setting.
            if (!TowerTunables.Enabled)
                return;
            int now = Find.TickManager.TicksGame;
            if (sessionStartTick < 0 || sessionStartTick > now)
                sessionStartTick = now;   // FinalizeInit missed; start counting here
            if (now - sessionStartTick < TowerTunables.RaiseAfterTicks)
                return;

            // Needs somewhere for the letter to point at and a colony to read
            // it. Without a home map the tile finder has no anchor either.
            Map home = Find.AnyPlayerHomeMap;
            if (home == null || home.mapPawns.FreeColonistsCount == 0)
                return;

            WorldObject_Tower tower = WorldObject_Tower.TryRaise(Rand.Int);
            if (tower == null)
                return;   // no reachable tile yet; try again shortly

            Find.LetterStack.ReceiveLetter(
                "IsekaiDungeons.Letter.TowerRises.Label".Translate(),
                "IsekaiDungeons.Letter.TowerRises.Text".Translate(TowerTunables.FloorCount.Named("FLOORS")),
                IsekaiDungeonsDefOf.IsekaiDungeons_TowerRises, tower);
        }

        private void CheckSummitReward()
        {
            Map floorMap = TowerFloors.LiveFloor;
            bool onSummit = floorMap != null
                            && (floorMap.Parent as TowerFloorMapParent)?.floor == TowerTunables.FloorCount;

            DungeonMapComponent dungeon = onSummit ? floorMap.GetComponent<DungeonMapComponent>() : null;
            bool wonNow = dungeon != null && dungeon.IsCleared;

            // RECOVERY PATH. The record is the durable memory of the climb, and
            // it is the only thing left once the summit map is destroyed. A
            // player who beat floor 100 and walked out — or who beat it while
            // no code was watching, which is exactly what happened when the
            // watch lived on a building that was later deleted — is still owed
            // the reward. A regenerated summit comes back deliberately empty,
            // so waiting for IsCleared again would wait forever.
            TowerFloorRecord summit = RecordIfSeen(TowerTunables.FloorCount);
            bool wonEarlier = summit != null && summit.cleared;

            if (!wonNow && !wonEarlier)
                return;

            Map dropMap = onSummit ? floorMap : Find.AnyPlayerHomeMap;
            if (dropMap == null)
                return;   // nowhere to put it yet; try again next tick

            Pawn winner = SummitDamage.BestClaimant();
            bool byCombat = winner != null;

            if (winner == null)
            {
                // No damage record — a summit beaten before this was tracked,
                // or one where every contender has since died. Fall back to the
                // most accomplished colonist rather than dropping the reward on
                // the floor, and say which it was in the letter.
                winner = HighestLevelColonist(dropMap);
            }
            if (winner == null)
                return;   // nobody to give it to yet; try again next tick

            summitClaimed = true;

            bool granted = false;
            try
            {
                granted = IsekaiLeveling.IsekaiTraitHelper.AddIsekaiTrait(winner, "Isekai_Godhood");
            }
            catch (System.Exception ex)
            {
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Could not grant Godhood to {winner.LabelShort}: {ex.Message}");
            }

            if (!granted)
            {
                Log.Error($"{IsekaiDungeonsMod.LogPrefix} Godhood could not be granted to {winner.LabelShort} — "
                        + "the trait def may be missing.");
                summitClaimed = false;
                return;
            }

            // The body changes in the same beat as the letter. The reconciler
            // below would catch it within 250 ticks anyway; doing it here means
            // the player who clicks through to the pawn sees the white hair
            // already there, not four seconds later.
            GodhoodBodyUtility.EnsureBody(winner);

            // Map-wide rather than colony-map-wide: the party is standing on
            // floor 100, in a pocket map, and the people who should feel this
            // most are the ones back home who watched them leave.
            DungeonMoods.GainSummit();

            Find.LetterStack.ReceiveLetter(
                "IsekaiDungeons.Tower.Summit.Label".Translate(),
                (byCombat ? "IsekaiDungeons.Tower.Summit.Text" : "IsekaiDungeons.Tower.Summit.Late")
                    .Translate(TowerTunables.FloorCount.Named("FLOORS"),
                               winner.LabelShortCap.Named("PAWN")),
                LetterDefOf.PositiveEvent, winner);

            SummitDamage.Clear();
        }

        /// <summary>Fallback claimant when no record of the fight survives.</summary>
        private static Pawn HighestLevelColonist(Map map)
        {
            if (map == null)
                return null;
            Pawn best = null;
            int bestLevel = -1;
            var colonists = map.mapPawns.FreeColonistsSpawned;
            for (int i = 0; i < colonists.Count; i++)
            {
                Pawn p = colonists[i];
                if (p.story?.traits == null)
                    continue;
                int level = 0;
                try
                {
                    level = IsekaiLeveling.IsekaiComponent.GetCached(p)?.Level ?? 0;
                }
                catch
                {
                    level = 0;
                }
                if (level > bestLevel)
                {
                    bestLevel = level;
                    best = p;
                }
            }
            return best;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref towerSeed, "towerSeed", 0);
            Scribe_Values.Look(ref highestReached, "highestReached", 0);
            Scribe_Values.Look(ref summitClaimed, "summitClaimed", defaultValue: false);
            Scribe_Values.Look(ref towerSank, "towerSank", defaultValue: false);
            Scribe_References.Look(ref towerQuest, "towerQuest");
            Scribe_Deep.Look(ref summitDamage, "summitDamage");
            if (Scribe.mode == LoadSaveMode.PostLoadInit && summitDamage == null)
                summitDamage = new TowerSummitDamage();
            Scribe_Collections.Look(ref records, "records", LookMode.Deep);
            Scribe_Collections.Look(ref autopilotPawns, "autopilotPawns", LookMode.Reference);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (records == null)
                    records = new List<TowerFloorRecord>();
                if (autopilotPawns == null)
                    autopilotPawns = new List<Pawn>();
                // A pawn who died or was destroyed while the save sat on disk
                // reference-resolves to null; keep the list clean of them.
                autopilotPawns.RemoveAll(p => p == null || p.Dead);
                PruneUnvisited();
                TowerQuest.RefreshText(towerQuest);

                // A save from before the contract had a root lost it on load
                // (the quest manager drops rootless quests). Post it again for a
                // tower that is still standing. Deferred: the quest manager is
                // not a safe thing to add to from inside the load itself.
                if (towerQuest == null && Exists && !summitClaimed && !towerSank)
                    LongEventHandler.ExecuteWhenFinished(() =>
                    {
                        if (towerQuest == null && Exists)
                            towerQuest = TowerQuest.Post();
                    });
            }
        }
    }
}
