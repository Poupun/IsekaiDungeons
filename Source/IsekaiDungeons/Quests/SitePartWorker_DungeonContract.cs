using System.Collections.Generic;
using IsekaiLeveling.MobRanking;
using RimWorld;
using UnityEngine;
using RimWorld.Planet;
using Verse.AI.Group;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Stages the surface map a guild contract points at.
    ///
    /// The contract does NOT invent a new kind of dungeon. It places an
    /// ordinary Building_DungeonGate on an ordinary map and lets the entire
    /// existing lifecycle take over from there: the same pocket map, the same
    /// rooms, the same boss, the same loot, the same collapse-two-hours-after-
    /// you-leave. Everything the quest layer does is decide WHERE the gate is
    /// and WHY the player was told about it.
    ///
    /// That is deliberate and it is what makes the second and third contract
    /// types cheap: a break contract is this same staging with the gate already
    /// breaching and its first waves standing on the grass, and a rescue is
    /// that scene on somebody's settlement map. None of it needs a parallel
    /// copy of the gate.
    /// </summary>
    public class SitePartWorker_DungeonContract : SitePartWorker
    {
        public override void PostMapGenerate(Map map)
        {
            base.PostMapGenerate(map);
            TryStage(map);
        }

        /// <summary>
        /// Public so the retry net can call it. Idempotent per MAP, not per
        /// site: a map that already carries a gate is left alone, so a map that
        /// is entered, left, and re-entered does not accumulate gates.
        ///
        /// It must NOT be keyed on <see cref="WorldObjectComp_DungeonContract.gatePlaced"/>.
        /// That flag lives on the site, and the site outlives its map: when the
        /// party walks home to prepare, vanilla removes the empty site map (and
        /// the gate with it) while DungeonContractSite keeps the site. The next
        /// visit generates a fresh map, and a site-level latch would leave it
        /// with no gate, making the contract impossible to complete.
        /// </summary>
        public static bool TryStage(Map map)
        {
            WorldObjectComp_DungeonContract comp = map?.Parent?.GetComponent<WorldObjectComp_DungeonContract>();
            if (comp == null || comp.contractResolved)
                return false;
            List<Building_DungeonGate> existing = DungeonTracker.GatesOn(map);
            if (existing != null && existing.Count > 0)
                return false;

            DungeonKindDef kind = comp.kindDef ?? IsekaiDungeonsDefOf.IsekaiDungeons_BreakDungeon;
            ThingDef gateDef = kind.gateDef ?? IsekaiDungeonsDefOf.IsekaiDungeons_DungeonGate;

            if (!TryFindGateCell(map, gateDef, out IntVec3 cell))
            {
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} No room for a contract gate on {map}; "
                          + "the site will be empty. This should not happen on a generated site map.");
                return false;
            }

            var gate = (Building_DungeonGate)ThingMaker.MakeThing(gateDef);
            gate.Initialize(kind, comp.rank, redGate: false);
            // FullRefund rather than Vanish. TryFindGateCell already refuses
            // occupied ground, so nothing should ever be displaced; this is the
            // fallback for when it is. It matters most on a rescue contract,
            // where the map is a faction settlement and a wipe would delete the
            // buildings of the people the player came to save.
            GenSpawn.Spawn(gate, cell, map, Rot4.North, WipeMode.FullRefund);
            comp.gatePlaced = true;

            if (comp.questKind != DungeonQuestKind.Located)
                StageBrokenSurface(map, gate, comp);

            return true;
        }

        /// <summary>
        /// A broken contract is a scene the player walks into, not a countdown
        /// they watch. The gate is already torn open and what came out is
        /// already standing on the map, hostile and waiting.
        ///
        /// The pack is rolled and spawned DIRECTLY rather than by calling
        /// BeginBreach, because the breach machinery drains a live pocket map
        /// one wave a day onto a colony. Here there is no pocket map yet (they
        /// generate lazily on first entry) and no drip to simulate: the whole
        /// point is that the damage is done and visible on arrival.
        /// </summary>
        private static void StageBrokenSurface(Map map, Building_DungeonGate gate, WorldObjectComp_DungeonContract comp)
        {
            // The pack scattered below counts as the first wave that already
            // came out, so the gate resumes from wave one rather than sending
            // it again. StageAlreadyBreaching also starts the breach clock,
            // which is the whole reason this is a method on the gate instead of
            // an assignment here.
            gate.StageAlreadyBreaching(wavesAlreadyOut: 1);

            DungeonKindDef kind = comp.kindDef ?? IsekaiDungeonsDefOf.IsekaiDungeons_BreakDungeon;
            float budget = kind.encounterPointsByRank.Evaluate((int)comp.rank)
                           * Mathf.Max(1f, DungeonTunables.RoomCountFor(kind, (int)comp.rank))
                           * DungeonQuestTuning.BrokenSurfaceBudgetFraction
                           * DungeonTunables.EncounterMultiplierFor(map);

            List<PawnKindDef> pack = DungeonRoster.RollRosterPack(kind, comp.rank, budget);
            if (pack == null || pack.Count == 0)
                pack = DungeonSpawnResolver.RollPack(comp.rank, budget);
            if (pack == null || pack.Count == 0)
                return;

            var loose = new List<Pawn>(pack.Count);
            for (int i = 0; i < pack.Count; i++)
            {
                Pawn p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(
                    pack[i], DungeonHostility.FactionFor(pack[i]),
                    PawnGenerationContext.NonPlayer, forceGenerateNewPawn: true));
                if (p == null)
                    continue;

                if (!CellFinder.TryFindRandomCellNear(gate.Position, map,
                        DungeonQuestTuning.BrokenScatterRadius,
                        c => c.Standable(map) && !c.Fogged(map), out IntVec3 at))
                    at = CellFinder.RandomSpawnCellForPawnNear(gate.Position, map);

                GenSpawn.Spawn(p, at, map, Rot4.Random);
                DungeonSpawnResolver.NormalizeRank(p, comp.rank);
                loose.Add(p);
            }

            // One group, race-split, exactly as an in-dungeon room aggro does.
            // Animals go manhunter; humanlikes get a shared assault lord, which
            // is what stops a garrison-flavoured roster fighting itself.
            DungeonHostility.Wake(loose, map);

            if (comp.questKind == DungeonQuestKind.FactionRescue)
                StageDefenders(map, gate, comp);
        }

        /// <summary>
        /// The people the contract is actually about.
        ///
        /// A rescue where the rescued are already dead is just a break with a
        /// faction name on the letter, so the defenders have to be alive and
        /// visibly losing when the player walks in. They are spawned as a real
        /// defend lord rather than left idle: a group holding a line reads as a
        /// last stand, while the same pawns standing still read as scenery.
        ///
        /// They are NOT counted by the contract's completion check. Whether
        /// they live is up to the player and the fight, and tying the reward to
        /// keeping strangers alive through a wave fight would make the contract
        /// fail for reasons nobody can control.
        /// </summary>
        private static void StageDefenders(Map map, Building_DungeonGate gate, WorldObjectComp_DungeonContract comp)
        {
            Faction faction = comp.rescueFaction;
            if (faction == null || faction.HostileTo(Faction.OfPlayer))
                return;

            int count = Mathf.Clamp(2 + (int)comp.rank / 2, 3, 8);
            var defenders = new List<Pawn>(count);

            for (int i = 0; i < count; i++)
            {
                PawnKindDef kind = faction.RandomPawnKind();
                if (kind == null)
                    break;

                Pawn p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(
                    kind, faction, PawnGenerationContext.NonPlayer,
                    forceGenerateNewPawn: true, mustBeCapableOfViolence: true));
                if (p == null)
                    continue;

                if (!CellFinder.TryFindRandomCellNear(gate.Position, map,
                        Mathf.RoundToInt(DungeonQuestTuning.DefenderRingRadius),
                        c => c.Standable(map) && !c.Fogged(map), out IntVec3 at))
                    at = CellFinder.RandomSpawnCellForPawnNear(gate.Position, map);

                GenSpawn.Spawn(p, at, map, Rot4.Random);
                defenders.Add(p);
            }

            if (defenders.Count == 0)
                return;

            try
            {
                LordMaker.MakeNewLord(faction,
                    new LordJob_DefendPoint(gate.Position, DungeonQuestTuning.DefenderRingRadius),
                    map, defenders);
            }
            catch (System.Exception e)
            {
                // A failed lord leaves them leaderless but still a hostile-to-
                // the-dungeon faction standing on the map, so the scene survives.
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Could not give {defenders.Count} rescue defenders "
                          + $"a defend lord: {e.Message}");
            }
        }

        /// <summary>
        /// Somewhere the gate actually fits. The gate is a 5x5 impassable
        /// building, so a merely standable cell is not enough: the whole
        /// footprint has to be clear, or GenSpawn wipes whatever was there and
        /// the gate ends up embedded in terrain it cannot be approached from.
        /// </summary>
        private static bool TryFindGateCell(Map map, ThingDef gateDef, out IntVec3 cell)
        {
            IntVec2 size = gateDef.size;
            bool Fits(IntVec3 c)
            {
                CellRect r = GenAdj.OccupiedRect(c, Rot4.North, size).ExpandedBy(1);
                if (!r.InBounds(map))
                    return false;
                foreach (IntVec3 x in r)
                {
                    if (!x.Standable(map) || x.GetEdifice(map) != null || x.Roofed(map))
                        return false;
                }
                return true;
            }

            if (CellFinderLoose.TryGetRandomCellWith(Fits, map, 300, out cell))
                return true;

            // Relaxed: drop the roof and margin requirement before giving up.
            bool Loose(IntVec3 c)
            {
                CellRect r = GenAdj.OccupiedRect(c, Rot4.North, size);
                if (!r.InBounds(map))
                    return false;
                foreach (IntVec3 x in r)
                    if (!x.Standable(map))
                        return false;
                return true;
            }
            return CellFinderLoose.TryGetRandomCellWith(Loose, map, 300, out cell);
        }
    }
}
