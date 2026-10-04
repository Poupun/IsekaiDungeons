using System;
using System.Collections.Generic;
using System.Linq;
using IsekaiLeveling.MobRanking;
using LudeonTK;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// End-to-end smoke test of the content a player can actually reach on THIS
    /// install: it generates real dungeon maps, tower floors, contracts, deep
    /// dungeon surveys and loot, and logs one PASS/FAIL line per check plus a
    /// summary. Written to answer "does the mod work without Isekai Creatures"
    /// without a manual play session, and kept for regression runs.
    ///
    /// Two ways in:
    ///  - Dev menu: Isekai Dungeons → "Self-test (generates maps, slow)".
    ///  - Unattended: launch with  -quicktest -isekaiDungeonsSelfTest  and the
    ///    test runs once the quick-test map is up. Add -isekaiDungeonsSelfTestQuit
    ///    to close the game when it finishes. Search Player.log for "[SelfTest]".
    ///
    /// It mutates the game it runs in (raises the tower, posts contracts), so
    /// run it on a throwaway quick-test colony, never on a real save.
    /// </summary>
    public static class DungeonSelfTest
    {
        private const string Tag = "[IsekaiDungeons][SelfTest]";
        private static int pass, fail;
        private static readonly List<string> failures = new List<string>();

        [DebugAction("Isekai Dungeons", "Self-test (generates maps, slow)",
                     actionType = DebugActionType.Action,
                     allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void RunFromMenu() => Run(quitAfter: false);

        [DebugAction("Isekai Dungeons", "Self-test: threat sampling (slow)",
                     actionType = DebugActionType.Action,
                     allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void RunThreatFromMenu()
        {
            threatOnly = true;
            Run(quitAfter: false);
        }

        private static bool threatOnly;

        /// <summary>
        /// The work, one entry per map. Each runs as its OWN long event, so the
        /// loading screen redraws (with progress text) between maps. Running
        /// everything inside a single long event blocked the main thread for
        /// minutes and the game window sat on a frozen black screen.
        /// </summary>
        private static readonly List<KeyValuePair<string, Action>> steps = new List<KeyValuePair<string, Action>>();

        private static void Step(string label, Action act) => steps.Add(new KeyValuePair<string, Action>(label, act));

        public static void Run(bool quitAfter)
        {
            pass = fail = 0;
            failures.Clear();
            steps.Clear();
            try { BuildSteps(); }
            catch (Exception ex) { Fail("harness", ex.ToString()); }

            int total = steps.Count;
            for (int i = 0; i < total; i++)
            {
                int index = i;
                KeyValuePair<string, Action> step = steps[i];
                LongEventHandler.QueueLongEvent(() =>
                {
                    LongEventHandler.SetCurrentEventText($"Isekai Dungeons self-test {index + 1}/{total}\n{step.Key}");
                    try { step.Value(); }
                    catch (Exception ex) { Fail(step.Key, ex.ToString()); }
                }, "LoadingLongEvent", false, null);
            }
            LongEventHandler.QueueLongEvent(() =>
            {
                Summary();
                steps.Clear();
                if (quitAfter)
                    LongEventHandler.ExecuteWhenFinished(Root.Shutdown);
            }, "LoadingLongEvent", false, null);
        }

        private static void BuildSteps()
        {

            Map home = Find.AnyPlayerHomeMap;
            bool creatures = ModsConfig.IsActive("Poupun.IsekaiCreaturesAddon");
            Log.Message($"{Tag} START. Isekai Creatures active: {creatures}. Home map: {home}");
            if (home == null)
            {
                Fail("setup", "no player home map");
                return;
            }

            Step("catalogue", CheckKindAvailability);

            if (threatOnly || GenCommandLine.CommandLineArgPassed("isekaiDungeonsSelfTestThreat"))
            {
                threatOnly = false;
                CheckThreat(home);
                return;
            }
            CheckEveryRank(home);
            if (!GenCommandLine.CommandLineArgPassed("isekaiDungeonsSelfTestQuick"))
                CheckEveryAvailableKind(home);
            Step("loot", CheckLoot);
            CheckTower(home);
            Step("contracts", () => CheckContracts(home));
            Step("deep dungeons", () => CheckDeepDungeons(home));
        }

        // ── Catalogue ───────────────────────────────────────────────────────

        private static IEnumerable<DungeonKindDef> GateKinds =>
            DefDatabase<DungeonKindDef>.AllDefsListForReading.Where(k => !k.towerOnly);

        private static void CheckKindAvailability()
        {
            var all = DefDatabase<DungeonKindDef>.AllDefsListForReading;
            int gate = all.Count(k => !k.towerOnly), gateOk = all.Count(k => !k.towerOnly && k.Available);
            int tower = all.Count(k => k.towerOnly), towerOk = all.Count(k => k.towerOnly && k.Available);
            Log.Message($"{Tag} gate kinds available {gateOk}/{gate}; tower/deep kinds available {towerOk}/{tower}");

            foreach (MobRankTier rank in Ranks)
            {
                int n = GateKinds.Count(k => k.Available && k.minRank <= rank && rank <= k.maxRank);
                Log.Message($"{Tag}   rank {rank}: {n} available gate kind(s)");
            }
        }

        // ── Gates ───────────────────────────────────────────────────────────

        private static IEnumerable<MobRankTier> Ranks =>
            Enum.GetValues(typeof(MobRankTier)).Cast<MobRankTier>().Where(r => r <= MobRankTier.Nation);

        private static void CheckEveryRank(Map home)
        {
            foreach (MobRankTier rank in Ranks)
            {
                QueueGate(home, () => DungeonKindSelector.SelectForRank(rank), rank, $"rank {rank}",
                          (map, kind) => InspectDungeonMap(map, $"rank {rank} ({kind.defName} @ {rank})", expectBoss: true));
            }
        }

        private static void CheckEveryAvailableKind(Map home)
        {
            foreach (DungeonKindDef kind in GateKinds.Where(k => k.Available))
            {
                MobRankTier rank = kind.minRank;
                QueueGate(home, () => kind, rank, $"kind {kind.defName}",
                          (map, k) => InspectDungeonMap(map, $"kind {k.defName} ({k.defName} @ {rank})", expectBoss: true));
            }
        }

        // ── Two-phase map steps ─────────────────────────────────────────────
        //
        // A map is generated in one long event and inspected and removed in
        // the NEXT one. Map.FinalizeInit defers its drawer build to the end of
        // the long event that created it; removing the map inside that same
        // event left the deferred build running on a dead map, which threw and
        // shut the game down.

        private static Building_DungeonGate pendingGate;
        private static Map pendingMap;

        private static void QueueGate(Map home, Func<DungeonKindDef> kindFn, MobRankTier rank, string label,
                                      Action<Map, DungeonKindDef> inspect)
        {
            DungeonKindDef kind = null;
            Step(label + " (generate)", () =>
            {
                kind = kindFn();
                if (kind == null)
                {
                    Fail(label, "no dungeon kind for this rank");
                    return;
                }
                ThingDef gateDef = kind.gateDef ?? IsekaiDungeonsDefOf.IsekaiDungeons_DungeonGate;
                if (!TryFindGateCell(home, gateDef, out IntVec3 cell))
                {
                    Fail(label, "no free cell for a gate on the home map");
                    return;
                }
                var gate = (Building_DungeonGate)ThingMaker.MakeThing(gateDef);
                gate.Initialize(kind, rank, redGate: false);
                GenSpawn.Spawn(gate, cell, home, Rot4.North, WipeMode.FullRefund);
                pendingGate = gate;
                pendingMap = gate.GetOtherMap();
            });
            Step(label + " (inspect)", () =>
            {
                try
                {
                    if (kind != null && pendingGate != null)
                    {
                        if (pendingMap == null)
                            Fail(label, "map generation returned null");
                        else
                            inspect(pendingMap, kind);
                    }
                }
                finally
                {
                    CleanupPending(label);
                }
            });
        }

        private static void CleanupPending(string label)
        {
            try
            {
                if (pendingMap != null && Find.Maps.Contains(pendingMap))
                    PocketMapUtility.DestroyPocketMap(pendingMap);
                // Gates are not destroyable (Destroy refuses them); despawning
                // is what removes them and deregisters them from the tracker.
                if (pendingGate != null && pendingGate.Spawned)
                    pendingGate.DeSpawn(DestroyMode.Vanish);
            }
            catch (Exception ex)
            {
                Fail(label + " cleanup", ex.ToString());
            }
            pendingGate = null;
            pendingMap = null;
        }

        private static void InspectDungeonMap(Map map, string label, bool expectBoss)
        {
            if (map == null)
            {
                Fail(label, "map generation returned null");
                return;
            }
            DungeonMapComponent comp = map.GetComponent<DungeonMapComponent>();
            if (comp == null)
            {
                Fail(label, "no DungeonMapComponent");
                return;
            }
            int rooms = comp.rooms?.Count ?? 0;
            int creatures = map.mapPawns.AllPawnsSpawned.Count(p => !p.Dead && p.Faction != Faction.OfPlayer);
            Pawn boss = comp.boss;
            string bossInfo = boss == null ? "none"
                : $"{boss.kindDef.defName} rank {boss.GetMobRank()?.Rank.ToString() ?? "-"}";

            if (rooms == 0)
                Fail(label, "no rooms");
            else if (expectBoss && boss == null && !comp.GeneratedEmpty)
                Fail(label, $"no boss (rooms {rooms}, hostiles {creatures})");
            else if (expectBoss && creatures == 0 && !comp.GeneratedEmpty)
                Fail(label, "no hostiles at all");
            else
                Pass(label, $"rooms {rooms}, hostiles {creatures}, boss {bossInfo}");
        }

        private static bool TryFindGateCell(Map map, ThingDef gateDef, out IntVec3 cell)
        {
            IntVec2 size = gateDef.size;
            foreach (IntVec3 c in map.AllCells.InRandomOrder())
            {
                if (c.DistanceToEdge(map) <= size.x + 2)
                    continue;
                if (IncidentWorker_DungeonBreak.IsFootprintFree(map, c, size, allowHomeArea: true))
                {
                    cell = c;
                    return true;
                }
            }
            cell = IntVec3.Invalid;
            return false;
        }

        // ── Threat sampling ─────────────────────────────────────────────────

        /// <summary>Same measure the parity rules spend against.</summary>
        public static float ThreatOf(Pawn p) => ThreatParity.ThreatOf(p);

        private static void CheckThreat(Map home)
        {
            DungeonKindDef generic = IsekaiDungeonsDefOf.IsekaiDungeons_BreakDungeon;
            foreach (MobRankTier rank in Ranks)
            {
                for (int i = 0; i < 2; i++)
                    QueueGate(home, () => generic, rank, $"threat generic {rank}",
                              (map, kind) => LogThreat(map, kind, rank, "generic"));
                for (int i = 0; i < 2; i++)
                    QueueGate(home, () => DungeonKindSelector.SelectForRank(rank), rank, $"threat rolled {rank}",
                              (map, kind) => LogThreat(map, kind, rank, "rolled"));
            }
        }

        private static void LogThreat(Map pocket, DungeonKindDef kind, MobRankTier rank, string mode)
        {
            DungeonMapComponent comp = pocket.GetComponent<DungeonMapComponent>();
            var hostiles = pocket.mapPawns.AllPawnsSpawned.Where(p => !p.Dead && p.Faction != Faction.OfPlayer).ToList();
            float threat = hostiles.Sum(ThreatOf);
            float bossThreat = comp?.boss != null ? ThreatOf(comp.boss) : 0f;
            float avg = hostiles.Count > 0 ? threat / hostiles.Count : 0f;
            Log.Message($"{Tag} THREAT parity={ThreatParity.Active} mode={mode} rank={rank} kind={kind.defName} hostiles={hostiles.Count} "
                      + $"threat={threat:F0} avgPerPawn={avg:F0} boss={comp?.boss?.kindDef?.defName ?? "none"} "
                      + $"bossRank={comp?.boss?.GetMobRank()?.Rank.ToString() ?? "-"} bossThreat={bossThreat:F0}");
        }

        // ── Loot ────────────────────────────────────────────────────────────

        private static void CheckLoot()
        {
            DungeonKindDef kind = IsekaiDungeonsDefOf.IsekaiDungeons_BreakDungeon;
            foreach (MobRankTier rank in Ranks)
            {
                try
                {
                    List<Thing> clear = kind.LootWorker.GenerateClearLoot(rank, kind) ?? new List<Thing>();
                    List<Thing> treasure = kind.LootWorker.GenerateTreasureLoot(rank, kind) ?? new List<Thing>();
                    float value = clear.Sum(t => t.MarketValue * t.stackCount);
                    if (clear.Count == 0)
                        Fail($"loot {rank}", "empty clear loot");
                    else
                        Pass($"loot {rank}", $"clear {clear.Count} items ~{value:N0} silver, treasure {treasure.Count} items");
                    foreach (Thing t in clear.Concat(treasure))
                        if (!t.Destroyed) t.Destroy();
                }
                catch (Exception ex)
                {
                    Fail($"loot {rank}", ex.ToString());
                }
            }
        }

        // ── Tower ───────────────────────────────────────────────────────────

        private static readonly int[] SampleFloors = { 1, 2, 5, 10, 15, 20, 30, 40, 49, 50, 51, 60, 75, 90, 99, 100 };

        private static void CheckTower(Map home)
        {
            Step("tower ladder", () => CheckTowerLadder(home));
            foreach (int f in SampleFloors)
            {
                Step($"tower floor {f} (generate)", () =>
                {
                    TowerComponent tc = TowerComponent.Get;
                    if (tc == null || !tc.Exists)
                    {
                        Fail($"tower floor {f}", "no tower");
                        return;
                    }
                    pendingMap = TowerFloors.Open(f);
                    if (pendingMap == null)
                        Fail($"tower floor {f}", "TowerFloors.Open returned null");
                });
                Step($"tower floor {f} (inspect)", () =>
                {
                    try
                    {
                        if (pendingMap != null)
                            CheckTowerFloor(f, pendingMap);
                    }
                    finally
                    {
                        CleanupPending($"tower floor {f}");
                    }
                });
            }
        }

        private static void CheckTowerLadder(Map home)
        {
            TowerComponent comp = TowerComponent.Get;
            if (comp == null)
            {
                Fail("tower", "no TowerComponent");
                return;
            }
            if (!comp.Exists)
            {
                IncidentDef def = DefDatabase<IncidentDef>.GetNamedSilentFail("IsekaiDungeons_TowerRises");
                var parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.ThreatBig, home);
                parms.forced = true;
                if (def == null || !def.Worker.TryExecute(parms) || !comp.Exists)
                {
                    Fail("tower", "could not raise the tower");
                    return;
                }
            }

            // Every floor must resolve to an available dungeon.
            int bad = 0;
            for (int f = 1; f <= TowerTunables.FloorCount; f++)
            {
                TowerFloorRecord rec = comp.Preview(f);
                if (rec?.kindDef == null || rec.kindDef.mapGenerator == null)
                {
                    bad++;
                    Fail($"tower ladder floor {f}", "no dungeon kind");
                }
                else if (!rec.kindDef.Available && string.IsNullOrEmpty(rec.kindDef.authoredBoss)
                         && !(rec.archetype?.dungeonKinds?.Contains(rec.kindDef) ?? false))
                {
                    bad++;
                    Fail($"tower ladder floor {f}", $"{rec.kindDef.defName} is not available on this install");
                }
            }
            if (bad == 0)
                Pass("tower ladder", $"all {TowerTunables.FloorCount} floors resolve to an available dungeon");
        }

        private static void CheckTowerFloor(int f, Map map)
        {
            TowerFloorRecord rec = TowerComponent.Get?.Preview(f);
            bool peaceful = rec?.archetype?.peaceful ?? false;
            bool mirror = rec?.kindDef?.mirrorArena ?? false;
            InspectDungeonMap(map, $"tower floor {f} ({rec?.kindDef?.defName}, {rec?.archetype?.defName})",
                              expectBoss: !peaceful && !mirror);
        }

        // ── Contracts and deep dungeons ─────────────────────────────────────

        private static void CheckContracts(Map home)
        {
            foreach (DungeonQuestKind kind in new[] { DungeonQuestKind.Located, DungeonQuestKind.Broken, DungeonQuestKind.FactionRescue })
            {
                try
                {
                    Quest q = DungeonContractFactory.TryPost(home, kind);
                    if (q == null)
                        Fail($"contract {kind}", "TryPost returned null (no tile, no kind, or no faction)");
                    else
                        Pass($"contract {kind}", q.name);
                }
                catch (Exception ex)
                {
                    Fail($"contract {kind}", ex.ToString());
                }
            }
        }

        private static void CheckDeepDungeons(Map home)
        {
            var plans = DefDatabase<MultiFloorDungeonDef>.AllDefsListForReading;
            foreach (MultiFloorDungeonDef plan in plans)
            {
                PawnKindDef final = plan.FinalBoss;
                Log.Message($"{Tag}   deep dungeon {plan.defName}: final boss {final?.defName ?? "NONE"}");
            }
            try
            {
                bool posted = MultiFloorReports.TryPost(home);
                if (posted)
                    Pass("deep dungeon survey", "posted");
                else
                    Fail("deep dungeon survey", "TryPost returned false");
            }
            catch (Exception ex)
            {
                Fail("deep dungeon survey", ex.ToString());
            }
        }

        // ── Reporting ───────────────────────────────────────────────────────

        private static void Pass(string what, string detail)
        {
            pass++;
            Log.Message($"{Tag} PASS {what}: {detail}");
        }

        private static void Fail(string what, string detail)
        {
            fail++;
            failures.Add($"{what}: {detail.Split('\n')[0]}");
            Log.Warning($"{Tag} FAIL {what}: {detail}");
        }

        private static void Summary()
        {
            Log.Message($"{Tag} DONE. pass {pass}, fail {fail}");
            foreach (string f in failures)
                Log.Message($"{Tag}   failed: {f}");
        }
    }

    /// <summary>Starts the self-test on a -quicktest launch when -isekaiDungeonsSelfTest is passed.</summary>
    public class DungeonSelfTestLauncher : GameComponent
    {
        private static bool ran;

        public DungeonSelfTestLauncher(Game game)
        {
        }

        public override void StartedNewGame()
        {
            if (ran || !GenCommandLine.CommandLineArgPassed("isekaiDungeonsSelfTest"))
                return;
            ran = true;
            DungeonSelfTest.Run(quitAfter: GenCommandLine.CommandLineArgPassed("isekaiDungeonsSelfTestQuit"));
        }
    }
}
