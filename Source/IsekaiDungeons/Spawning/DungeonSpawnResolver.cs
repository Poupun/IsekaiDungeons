using System;
using System.Collections.Generic;
using IsekaiLeveling;
using IsekaiLeveling.Quests;
using IsekaiLeveling.MobRanking;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Static facade over the spawn-provider chain. All dungeon code picks
    /// creatures through here and never touches a provider directly, so the
    /// soft-dep fallback logic lives in exactly one place.
    ///
    /// Provider priority (probed in order, first hit wins):
    ///   1. SpawnProvider_IsekaiCreatures — themed add-on bestiary (reflection
    ///      soft dep; Available latches false when the add-on is absent).
    ///   2. SpawnProvider_BaseMod — the base mod's rank-banded vanilla-DefDatabase
    ///      selection; always available.
    ///
    /// Robustness contract: NO public member of this class ever throws. Every
    /// call site is either a GenStep (a throw aborts the whole map-generation
    /// pass and strands the player in a half-built pocket map) or the gate's
    /// break path (a throw kills the gate's state machine tick). Failures
    /// degrade: providers fall through, NormalizeRank silently skips, RollPack
    /// returns what it has. Each failure class logs once per session.
    /// </summary>
    public static class DungeonSpawnResolver
    {
        /// <summary>
        /// Probe order — IsekaiCreatures first (see class remarks). Static init
        /// is cheap: providers resolve their reflection lazily on first
        /// Available read, not at class load (defs aren't ready at load time).
        /// </summary>
        /// <summary>
        /// Probe chain, highest priority first. A LIST rather than an array so
        /// third-party bestiaries can register themselves — see
        /// <see cref="RegisterProvider"/>. The base-mod provider always stays
        /// last: it is the guaranteed-available fallback, so nothing may sit
        /// behind it or it would never be reached.
        /// </summary>
        private static readonly List<IDungeonSpawnProvider> providers = new List<IDungeonSpawnProvider>
        {
            new SpawnProvider_IsekaiCreatures(),
            new SpawnProvider_BaseMod(),
        };

        /// <summary>
        /// PUBLIC API for bestiary mods: make your creatures generically
        /// eligible for dungeon population, not just usable via an explicit
        /// per-dungeon roster.
        ///
        /// Call once from a [StaticConstructorOnStartup] type. Registered
        /// providers are probed BEFORE the built-in ones (so a themed bestiary
        /// wins over generic rank-band selection) but always AFTER nothing —
        /// the base-mod fallback is pinned last so the chain can never end up
        /// with no usable provider.
        /// </summary>
        public static void RegisterProvider(IDungeonSpawnProvider provider)
        {
            if (provider == null || providers.Contains(provider))
                return;
            // Insert ahead of the pinned base-mod fallback.
            providers.Insert(Mathf.Max(0, providers.Count - 1), provider);
            Log.Message($"{IsekaiDungeonsMod.LogPrefix} Registered spawn provider {provider.GetType().Name}.");
        }

        // One-shot log guards — a systemic failure (main-mod version mismatch,
        // broken provider) would otherwise spam once per creature per room.
        private static bool loggedPickFailure;
        private static bool loggedNormalizeFailure;

        /// <summary>Hard cap on kinds returned by RollPack — a Nation-rank break
        /// with a low-cost bestiary must not erupt into hundreds of pawns.</summary>
        private const int MaxPackKinds = 40;

        /// <summary>
        /// Effective encounter cost of a kind. Floor of 20: some modded/misc
        /// kinds declare combatPower 0-5, and dividing a room budget by a
        /// near-zero cost would flood the room (G3's "sanity-check the smallest
        /// entity the formula sees"). Null tolerated for caller convenience.
        /// </summary>
        public static float CombatPowerOf(PawnKindDef kind)
        {
            if (kind == null)
                return 20f;
            return Mathf.Max(20f, kind.combatPower);
        }

        /// <summary>
        /// Picks one creature kind fighting at roughly the given tier, preferring
        /// kinds whose cost fits budgetLeft. NOTE the provider contract: when the
        /// tier has candidates but none fit the budget, providers return their
        /// cheapest at-tier kind and still report true — the CALLER owns the
        /// decision to spawn it anyway (guaranteed-first-creature, G3) or stop.
        /// </summary>
        public static bool TryPickCreature(MobRankTier tier, float budgetLeft, out PawnKindDef kind)
        {
            kind = null;
            for (int i = 0; i < providers.Count; i++)
            {
                try
                {
                    if (!providers[i].Available)
                        continue;
                    if (providers[i].TryPickCreature(tier, budgetLeft, out kind) && kind != null)
                        return true;
                }
                catch (Exception ex)
                {
                    if (!loggedPickFailure)
                    {
                        loggedPickFailure = true;
                        Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Spawn provider {providers[i].GetType().Name} threw during creature pick; continuing with the next provider. {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Picks a single boss-worthy kind at exactly the gate rank. Null only
        /// when EVERY provider comes up empty — the population GenStep then
        /// promotes a spawned creature instead (never a bossless dungeon, G5).
        /// </summary>
        public static PawnKindDef PickBoss(MobRankTier rank)
        {
            for (int i = 0; i < providers.Count; i++)
            {
                try
                {
                    if (!providers[i].Available)
                        continue;
                    PawnKindDef kind = providers[i].PickBoss(rank);
                    if (kind != null)
                        return kind;
                }
                catch (Exception ex)
                {
                    if (!loggedPickFailure)
                    {
                        loggedPickFailure = true;
                        Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Spawn provider {providers[i].GetType().Name} threw during boss pick; continuing with the next provider. {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Brings a freshly spawned pawn onto the dungeon's rank ladder — for
        /// the pawns that have no rank of their own.
        ///
        /// AN AUTHORED CREATURE IS LEFT ALONE. Isekai Creatures declares what
        /// every creature in it IS, and a valkyrie is SSS whether it is standing
        /// on floor 80 or floor 100. Promoting it to the floor's rank produced
        /// exactly the nonsense it sounds like — Nation-rank valkyries crowding
        /// the top of the tower — and it also hid the real bug, which was that
        /// selection could not tell an SSS creature from an S one (see
        /// CreatureRankRegistry). Difficulty is now set by CHOOSING the right
        /// creature for the floor, which is the only way the rank the player
        /// reads can ever mean anything.
        ///
        /// Two kinds of pawn genuinely have no natural rank, and both still get
        /// normalized:
        ///
        ///  - HUMANLIKES. A rival adventurer's rank is derived from a randomly
        ///    rolled level and stat spread; there is nothing authored to respect,
        ///    and without this a Nation floor fills with F-rank bandits.
        ///  - UNRANKED CREATURES — a vanilla animal, or any bestiary with no
        ///    rank table. Their rank is a roll, not a statement, and on an
        ///    install without Isekai Creatures they are the only stock the high
        ///    floors have.
        ///
        /// Never throws (see class remarks): the comp lookup and both setters
        /// touch main-mod internals that could shift under a stale Workshop DLL
        /// (the CompForceMobRank incident), so the whole body is guarded.
        /// </summary>
        public static void NormalizeRank(Pawn pawn, MobRankTier tier, bool elite = false, bool raiseAuthored = false)
        {
            if (pawn == null)
                return;
            try
            {
                // TWO RANK SYSTEMS, AND HUMANLIKES ARE NOT IN THE ONE THIS USED
                // TO ASK. MobRankInjector.ShouldHaveMobRank returns false for
                // every humanlike ("EXCLUDE all humanlike pawns - they use
                // Isekai leveling system instead"), so GetMobRank is null for a
                // rival adventurer, a bandit party or a rescued villager, and
                // the fail-open below returned without doing anything at all.
                // The call site looked correct and the result was a B-rank
                // dungeon full of F-rank rivals.
                if (pawn.RaceProps != null && pawn.RaceProps.Humanlike)
                {
                    NormalizeHumanlikeRank(pawn, tier);
                    return;
                }

                MobRankComponent comp = pawn.GetMobRank();
                if (comp == null)
                    return; // rank injection disabled for this category in main-mod settings — fail open

                // Authored rank: respect it, and only decorate. Elite is a
                // modifier rather than a rank, so a boss keeps its crown.
                // raiseAuthored (bosses only) still lifts an authored rank
                // that sits BELOW the gate; it never lowers one.
                bool authored = CreatureRankRegistry.TryGetAuthored(pawn, out _);
                if ((!authored || raiseAuthored) && comp.Rank < tier)
                    comp.SetRankOverride(tier);
                if (elite)
                    comp.SetEliteOverride(true);
            }
            catch (Exception ex)
            {
                if (!loggedNormalizeFailure)
                {
                    loggedNormalizeFailure = true;
                    Log.Warning($"{IsekaiDungeonsMod.LogPrefix} NormalizeRank failed (likely a main-mod version mismatch); dungeon creatures keep their rolled ranks. {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Gives a humanlike the level and stats of the requested rank, and then
        /// defends that rank from the raid system.
        ///
        /// A humanlike's rank is DERIVED from its Isekai level and stat spread,
        /// so there is no override to set: the stats have to be regenerated at
        /// the target rank and the level recomputed from them. This mirrors what
        /// the base mod does for bounty targets, including the two details that
        /// are easy to miss:
        ///
        ///  - Pass the pawn to GenerateStatsForRank. The reset wipes the role
        ///    shaping the generator postfix applied, and a rival swordsman
        ///    rerolled as a pure WIS build is a scarecrow with a sword.
        ///  - Set statsInitialized, or PostSpawnSetup rerolls the whole thing at
        ///    a random rank the moment the pawn spawns.
        ///
        /// THE SECOND HALF IS THE REGISTRATION, and without it the rest is
        /// pointless. The base mod patches LordMaker.MakeNewLord and re-ranks
        /// anything it finds under a LordJob_AssaultColony against COLONY WEALTH.
        /// Every hostile group in a dungeon gets exactly such a lord moments
        /// after spawning (the rival party makes its own; room aggro and breach
        /// waves go through DungeonHostility), so a forced rank set at spawn was
        /// being overwritten seconds later with a wealth-derived one. The base
        /// mod hit this first with bounty targets and left the exit hatch public:
        /// AssignRaidPawnRank early-outs on IsBountyPawn. Registering here is
        /// what makes the forced rank stick.
        ///
        /// The registry is named for bounties for historical reasons; what it
        /// actually means is "this pawn's rank was set deliberately, leave it
        /// alone", which is exactly our case.
        /// </summary>
        private static void NormalizeHumanlikeRank(Pawn pawn, MobRankTier tier)
        {
            IsekaiComponent comp = pawn.GetComp<IsekaiComponent>();
            if (comp == null)
                return;   // base mod not tracking this pawn; nothing to raise

            string rankStr = DungeonRankUtil.ToQuestRank(tier).ToString();
            int minLevel = PawnStatGenerator.GetMinLevelForRank(rankStr);

            // REGISTER FIRST, AND ALWAYS. This is what stops the raid system
            // re-ranking the pawn against colony wealth a moment later, and it
            // has to happen even on the early-out below: a pawn who is ALREADY
            // strong enough needs protecting from being dragged down just as
            // much as a weak one needs raising.
            IncidentWorker_IsekaiHunt.RegisterBountyPawn(pawn);

            if (comp.stats == null)
                comp.stats = new IsekaiStatAllocation();

            // Upward only, matching what this method does for creatures and what
            // GenStep_DungeonPopulation's own comment promises ("NormalizeRank
            // only raises"). A faction that happens to roll something fiercer
            // than the dungeon keeps it; being sent into a B-rank gate is not a
            // reason to nerf a veteran.
            if (comp.statsInitialized && comp.currentLevel >= minLevel)
                return;

            PawnStatGenerator.GenerateStatsForRank(rankStr, comp.stats, pawn);
            comp.currentLevel = PawnStatGenerator.CalculateLevelFromStats(comp.stats);
            comp.currentXP = 0;

            // Rounding and stat caps can land the computed level just under the
            // rank's own threshold, which would display a B-rank rival as an A.
            if (comp.currentLevel < minLevel)
                comp.currentLevel = minLevel;

            comp.statsInitialized = true;
            PawnStatGenerator.AssignRankTrait(pawn, rankStr);
        }

        /// <summary>
        /// Rolls a full spawn table for the given rank and budget WITHOUT a map —
        /// used by the gate's break path when the countdown expires before the
        /// pocket map was ever generated (nothing entered the dungeon, so there
        /// are no actual survivors to evacuate; the break erupts a fresh roll).
        ///
        /// Composition: repeated picks with a mild downward tier drift —
        /// rank, rank, rank-1, rank-1, rank-2… (clamped at F) — so the wave reads
        /// as an at-rank vanguard trailed by lesser dungeon denizens, mirroring
        /// the room ramp the dungeon itself would have had.
        ///
        /// Guarantees: at least 1 kind whenever any provider can pick at all
        /// (the first pick is accepted even if it exceeds the budget — a break
        /// must never fizzle into zero spawns, G3/G5); hard cap of
        /// <see cref="MaxPackKinds"/> kinds regardless of budget.
        /// </summary>
        public static List<PawnKindDef> RollPack(MobRankTier rank, float budget)
        {
            var pack = new List<PawnKindDef>();
            float budgetLeft = budget;

            for (int pickIdx = 0; pack.Count < MaxPackKinds; pickIdx++)
            {
                // Drift: two picks per tier step down. ClampTier floors at F.
                MobRankTier tier = DungeonRankUtil.ClampTier((int)rank - pickIdx / 2);

                if (!TryPickCreature(tier, budgetLeft, out PawnKindDef kind))
                    break; // no provider can serve this tier — stop rather than loop forever

                float cost = CombatPowerOf(kind);

                // Over-budget picks (the provider's cheapest-at-tier fallback) are
                // accepted only as the guaranteed FIRST entry; afterwards they end
                // the roll — the budget is genuinely spent.
                if (pack.Count > 0 && cost > budgetLeft)
                    break;

                pack.Add(kind);
                budgetLeft -= cost;

                if (budgetLeft <= 0f)
                    break;
            }

            if (pack.Count == 0)
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} RollPack produced no creatures for rank {rank} (budget {budget:F0}) — no spawn provider returned a kind. The dungeon break will have no eruption wave.");

            return pack;
        }
    }
}
