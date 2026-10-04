using System;
using System.Collections.Generic;
using IsekaiLeveling.CharacterForge;
using IsekaiLeveling.MobRanking;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Third dungeon GenStep (order 430): fills the carved layout with life and
    /// treasure. Runs after GenStep_DungeonLayout stored the rooms in
    /// DungeonMapComponent and GenStep_DungeonEntrance placed the exit portal.
    ///
    ///   - Combat rooms get a rank-ramped creature encounter: early rooms sit
    ///     RoomTierRampDepth tiers below the gate rank on a smaller budget, the
    ///     final rooms are at-rank on the biggest budget (§6 of the contract).
    ///   - The boss room gets a single at-rank boss (elite when the archetype
    ///     says so) plus a small entourage of at-rank minions.
    ///   - Treasure side-rooms get DungeonLootGenerator's treasure roll
    ///     scattered on the floor.
    ///
    /// Everything spawns WILD (faction null) and idle: room-scoped aggro when a
    /// player pawn steps in is DungeonMapComponent's job, not this step's. No
    /// mental states, no lords — a freshly generated dungeon is a silent tomb
    /// until someone walks into a room.
    ///
    /// RNG note: vanilla MapGenerator.GenerateContentsIntoMap brackets every
    /// GenStep.Generate call with Rand.PushState + a per-step seed derived from
    /// the map seed and this step's SeedPart (verified against decompiled
    /// source). Plain Rand.* is therefore deterministic-per-map here and no
    /// explicit PushState/PopState is needed — the same assumption the vanilla
    /// GenSteps make.
    /// </summary>
    public class GenStep_DungeonPopulation : GenStep
    {
        /// <summary>Unique across all GenSteps in the generator (vanilla convention: a
        /// hardcoded arbitrary int; MapGenerator hash-combines it into the step seed).</summary>
        public override int SeedPart => 897361402;

        // Per-room creature ceiling and boss entourage size are settings now:
        // see DungeonTunables.MaxCreaturesPerRoom / MinBossMinions / MaxBossMinions.
        private static int MaxCreaturesPerRoom => DungeonTunables.MaxCreaturesPerRoom;
        private static int MinBossMinions => DungeonTunables.MinBossMinions;
        private static int MaxBossMinions => DungeonTunables.MaxBossMinions;

        public override void Generate(Map map, GenStepParams parms)
        {
            DungeonMapComponent comp = map.GetComponent<DungeonMapComponent>();
            if (comp == null)
            {
                // The MapGeneratorDef is supposed to list DungeonMapComponent in
                // customMapComponents — its absence means broken XML wiring, and
                // without the component there is nowhere to register creatures.
                Log.Error($"{IsekaiDungeonsMod.LogPrefix} GenStep_DungeonPopulation: DungeonMapComponent missing on the dungeon map — check the MapGeneratorDef's customMapComponents. Population skipped.");
                return;
            }

            ResolveRankAndKind(map, out MobRankTier rank, out DungeonKindDef def);

            List<DungeonRoom> rooms = comp.rooms; // contract: never null
            if (rooms.Count == 0)
            {
                Log.Error($"{IsekaiDungeonsMod.LogPrefix} GenStep_DungeonPopulation: no rooms registered — GenStep_DungeonLayout produced nothing? Population skipped.");
                return;
            }

            // Split by role up front. Generation-time allocations are fine (hard
            // rule: only per-tick code is allocation-restricted).
            var combatRooms = new List<DungeonRoom>();
            var treasureRooms = new List<DungeonRoom>();
            DungeonRoom bossRoom = null;
            for (int i = 0; i < rooms.Count; i++)
            {
                switch (rooms[i].role)
                {
                    case DungeonRoomRole.Combat:
                        combatRooms.Add(rooms[i]);
                        break;
                    case DungeonRoomRole.Treasure:
                        treasureRooms.Add(rooms[i]);
                        break;
                    case DungeonRoomRole.Boss:
                        if (bossRoom == null)
                            bossRoom = rooms[i]; // layout contract is exactly one; first wins defensively
                        break;
                }
            }

            // Track the strongest kind actually spawned in combat rooms — it is
            // the promotion candidate if every provider fails to name a boss.
            PawnKindDef strongestSpawnedKind = null;

            // ── Room events ─────────────────────────────────────────────────
            // Assigned BEFORE population so an event can replace the fight it
            // would otherwise have had. Tier/budget are computed here once and
            // reused, so an event sees exactly the encounter it displaced.
            // DEPTH COMES FROM THE ROOM, NOT FROM ITS LIST POSITION.
            //
            // This used to be `i / (n - 1)` in four places, which is only depth
            // while the dungeon is a straight chain. Once layouts branch, a room
            // on a side wing would inherit whatever depth its list slot implied —
            // silently putting late-game creatures in an alcove near the door.
            // DungeonLayoutPlan authors depthFrac instead, and for a chain it
            // writes exactly the number this expression produced.
            //
            // Tier and budget now have ONE definition each (TierFor/BudgetFor)
            // used by both the pre-pass and the population loop. They used to be
            // duplicated and had to be kept in lockstep by hand.
            // Continuous difficulty, when the caller pinned one. Ordinary
            // dungeons leave it unset and every curve lands exactly where it
            // always did.
            var dungeonParent = map.Parent as DungeonMapParent;
            float rankIndex = dungeonParent?.RankIndex ?? (int)rank;

            // A CLEARED TOWER FLOOR comes back as the place without its
            // population: no creatures, no boss, no treasure. It is a corridor
            // you have already taken.
            //
            // This is the anti-farm property, and it is structural rather than a
            // timer. The mod pays rewards per KILL — kill XP and corpse loot —
            // so ANY repopulation, at any fraction, makes re-entering a cleared
            // floor an income stream. Nothing to kill is the only version that
            // cannot be farmed.
            if (dungeonParent != null && dungeonParent.suppressPopulation)
            {
                comp.Notify_GeneratedEmpty();
                return;
            }

            // ── A PEACEFUL FLOOR ────────────────────────────────────────────
            // Not a beaten floor — this one was never a fight. Nothing hostile
            // is generated: no roster creatures, no boss, no room events. The
            // treasure loop below still runs, and everything spawned OUTSIDE
            // this step (the village, the vanilla Animals pass at order 1200)
            // is untouched, so a plains floor still has deer on it.
            //
            // Notify_GeneratedEmpty is load-bearing twice: it opens the stair
            // via Building_TowerStair.FloorBeaten, and it silences the
            // "generated without a boss" error in DungeonMapComponent.
            // bossOnly empties the floor of everyone except the boss. Folded in
            // alongside `peaceful` rather than given its own branch because the
            // two want the same three things suppressed — combat rooms, their
            // budgets and their events — and differ only in whether a boss is
            // still spawned at the end.
            bool bossOnly = def.bossOnly;   // DungeonContext.Resolve never returns null
            bool peaceful = dungeonParent != null && dungeonParent.peaceful;
            if (peaceful)
                comp.Notify_GeneratedEmpty();

            int n = (peaceful || bossOnly) ? 0 : combatRooms.Count;
            var roomTiers = new List<MobRankTier>();
            var roomBudgets = new List<float>();
            for (int i = 0; i < n; i++)
            {
                roomTiers.Add(TierFor(combatRooms[i], rank));
                roomBudgets.Add(BudgetFor(combatRooms[i], rank, def, rankIndex,
                                          (dungeonParent?.encounterFactor ?? 1f) * DungeonTunables.EncounterMultiplierFor(map)));
            }
            if (!peaceful && !bossOnly)
                DungeonRoomEventSelector.AssignEvents(combatRooms, def, rank, roomBudgets, roomTiers);

            // Rooms fought out on an earlier visit of this floor come back
            // empty and event-less (runtime events like the Ambush trigger on
            // entry, so the event must go, not just the population).
            ulong spent = MasksApply(dungeonParent, rooms) ? dungeonParent.roomSpentMask : 0UL;
            var spentRoom = new bool[n];
            for (int i = 0; i < n; i++)
            {
                int idx = combatRooms[i].index;
                if (idx >= 0 && idx < 64 && (spent & (1UL << idx)) != 0UL)
                {
                    spentRoom[i] = true;
                    combatRooms[i].eventDef = null;
                }
            }

            // ── Combat rooms: ramped encounters ─────────────────────────────
            for (int i = 0; i < n; i++)
            {
                // Tier ramp: room 0 sits RoomTierRampDepth tiers below the gate,
                // the last room is back at-rank; intermediate rooms round to the
                // nearest step. Clamped into [F, Nation] by ClampTier and never
                // above the gate rank (the ramp is a warm-up, not an escalation
                // past what the letter advertised — G3).
                if (spentRoom[i])
                    continue;

                MobRankTier tier = roomTiers[i];
                float budget = roomBudgets[i];

                // Depth fraction drives the roster's own minDepth gating, so a
                // dungeon escalates in composition as well as in raw rank.
                float depthFrac = combatRooms[i].depthFrac;

                // An event may take this room over entirely; if it does, skip
                // the ordinary encounter (it already spent the budget, or the
                // room is meant to look empty until it triggers).
                bool eventTookRoom = DungeonRoomEventSelector.PopulateRoomEvent(
                    map, comp, combatRooms[i], def, rank, tier, depthFrac, budget);
                if (eventTookRoom)
                    continue;

                PawnKindDef strongestInRoom = PopulateCombatRoom(map, comp, combatRooms[i], tier, budget, def, depthFrac, rank);
                // Explicit null-first check: CombatPowerOf(null) floors to 20,
                // which would tie with (and thus reject) a room of 20-power
                // chaff, leaving the promotion candidate permanently null.
                if (strongestInRoom != null
                    && (strongestSpawnedKind == null
                        || DungeonSpawnResolver.CombatPowerOf(strongestInRoom) > DungeonSpawnResolver.CombatPowerOf(strongestSpawnedKind)))
                {
                    strongestSpawnedKind = strongestInRoom;
                }
            }

            // ── Boss room ────────────────────────────────────────────────────
            if (bossRoom == null && !peaceful)
            {
                // Layout bug — but a dungeon MUST have a boss or it can never be
                // cleared (clear detection polls the boss pawn). Repurpose the
                // deepest combat room rather than dropping the feature (G5).
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} GenStep_DungeonPopulation: no Boss-role room in the layout; using the last combat room as the boss arena.");
                bossRoom = n > 0 ? combatRooms[n - 1] : rooms[rooms.Count - 1];
            }

            if (!peaceful)
                SpawnBoss(map, comp, bossRoom, rank, def, strongestSpawnedKind);

            // ── Treasure rooms ───────────────────────────────────────────────
            // A room whose bit is set was emptied on a previous visit. Room
            // index is a stable key across regenerations precisely because the
            // layout is a pure function of the pinned seed, which is what makes
            // one bit per room a sufficient record.
            ulong taken = MasksApply(dungeonParent, rooms) ? dungeonParent.treasureTakenMask : 0UL;
            for (int i = 0; i < treasureRooms.Count; i++)
            {
                int idx = treasureRooms[i].index;
                if (idx >= 0 && idx < 64 && (taken & (1UL << idx)) != 0UL)
                    continue;
                PlaceTreasure(map, treasureRooms[i], rank, def);
                TryPlaceBeastVault(map, treasureRooms[i], rank);
            }
        }

        /// <summary>
        /// The per-room masks belong to the layout they were recorded on. If
        /// the regenerated layout differs (a room-count or map-size setting
        /// changed), the bits would land on other rooms, so they are ignored;
        /// the record resets itself when the floor is next harvested.
        /// </summary>
        private static bool MasksApply(DungeonMapParent parent, List<DungeonRoom> rooms)
        {
            if (parent == null)
                return false;
            return parent.layoutSignature == 0
                   || parent.layoutSignature == TowerFloors.LayoutSignature(rooms);
        }

        /// <summary>
        /// Sometimes a treasure room holds something alive.
        ///
        /// The beast is spawned exactly like every other creature this step
        /// places — wild, factionless, idle — with one difference: it is NOT
        /// added to room.creatures, so the room-aggro wake in
        /// DungeonMapComponent never touches it and it never counts toward the
        /// dungeon's objective. It is scenery with teeth. Walk in, take the
        /// pile off the floor, and it watches you do it; provoke it and it is a
        /// rank-matched animal, which at S and up is a serious mistake.
        ///
        /// NormalizeRank is the whole reason this is worth doing. It puts the
        /// creature at the gate's rank through the same path the encounter
        /// pawns take, so a beast tamed out of an SS vault is an SS animal —
        /// which is a real reward and the exact thing the taming and bonding
        /// features in Isekai Creatures were built to receive.
        ///
        /// Placement failures are silent on purpose: this is a garnish, and a
        /// crowded room that cannot fit one more pawn should simply not have a
        /// beast rather than log about it.
        /// </summary>
        private static void TryPlaceBeastVault(Map map, DungeonRoom room, MobRankTier rank)
        {
            if ((int)rank < (int)DungeonTunables.BeastVaultMinRank)
                return;

            float t = Mathf.InverseLerp(
                (int)DungeonTunables.BeastVaultMinRank, (int)MobRankTier.Nation, (int)rank);
            if (!Rand.Chance(Mathf.Lerp(
                    DungeonTunables.BeastVaultChanceAtMinRank,
                    DungeonTunables.BeastVaultChanceAtMaxRank, t)))
                return;

            PawnKindDef kind = PickTameableBeast(rank);
            if (kind == null || !TryFindSpawnCell(map, room, out IntVec3 cell))
                return;

            // FactionFor rather than a hardcoded null: the filter above already
            // limits this to animals, which it resolves to null anyway, but
            // routing through the shared policy means a future change to how
            // dungeon creatures get factions does not silently skip the beast.
            Pawn beast = PawnGenerator.GeneratePawn(new PawnGenerationRequest(
                kind, DungeonHostility.FactionFor(kind), PawnGenerationContext.NonPlayer,
                forceGenerateNewPawn: true));
            if (beast == null)
                return;

            GenSpawn.Spawn(beast, cell, map, Rot4.Random);
            DungeonSpawnResolver.NormalizeRank(beast, rank);
        }

        /// <summary>
        /// A rank-banded creature the colony could actually keep.
        ///
        /// The resolver's pools are drawn for FIGHTS, so most of what they
        /// return — mechanoids, insects, anything with wildness 1 — is not a
        /// candidate. Rather than build a second bestiary we re-roll the same
        /// banded pick and keep the first tameable answer, which means the beast
        /// vault automatically follows whatever creature mods the player has
        /// installed instead of pinning a vanilla list.
        ///
        /// Null when the band has no tameable animal at all (a mechanoid-only
        /// modlist, say) — the caller treats that as "no beast this time".
        /// </summary>
        private static PawnKindDef PickTameableBeast(MobRankTier rank)
        {
            for (int i = 0; i < DungeonTunables.BeastVaultPickAttempts; i++)
            {
                if (!DungeonSpawnResolver.TryPickCreature(rank, float.MaxValue, out PawnKindDef kind)
                    || kind?.RaceProps == null)
                    continue;

                RaceProperties race = kind.RaceProps;
                if (!race.Animal || race.IsMechanoid || race.Humanlike)
                    continue;

                // Wildness 1 is the practical ceiling — vanilla's insects sit
                // there and a caged megaspider is not the fantasy. Thrumbos are
                // 0.97: punishing, possible, and exactly the reward this is
                // meant to be.
                if (kind.race.GetStatValueAbstract(StatDefOf.Wildness) >= 1f)
                    continue;

                return kind;
            }
            return null;
        }

        /// <summary>
        /// Resolves the dungeon's rank + archetype for this generation pass.
        /// Primary: the DungeonMapParent world object — its fields are assigned by
        /// Building_DungeonGate.GeneratePocketMapInt BEFORE MapGenerator.GenerateMap
        /// runs, so during GenSteps they are already valid. Secondary: the portal
        /// static vanilla MapPortal.GeneratePocketMap sets around generation.
        /// Final fallback: rank F + the break archetype, LOUDLY — a wrong-but-
        /// playable dungeon beats an aborted generation or a silent no-op (G5).
        /// </summary>
        /// <summary>Forwards to the public resolver (see DungeonContext) — no duplicated fallback chain.</summary>
        private static void ResolveRankAndKind(Map map, out MobRankTier rank, out DungeonKindDef def)
        {
            DungeonContext.Resolve(map, out def, out rank);
        }

        /// <summary>
        /// Fills one combat room: repeated budgeted picks at the room tier.
        /// Returns the strongest kind spawned (boss-promotion candidate), or
        /// null when nothing spawned at all.
        ///
        /// G3 guarantee: the FIRST pick spawns even when it exceeds the budget
        /// (providers fall back to their cheapest at-tier kind rather than
        /// failing) — an empty combat room breaks the dungeon's pacing and
        /// makes the room-aggro mechanic a no-op for that room.
        /// </summary>
        /// <summary>
        /// Creature tier for one room: the rank ramp evaluated at the room's own
        /// authored depth, plus its tierOffset (a wing's champion). The offset is
        /// added INSIDE the clamp, so a champion is at worst at-rank and never
        /// above what the arrival letter advertised.
        /// </summary>
        private static MobRankTier TierFor(DungeonRoom room, MobRankTier rank)
        {
            double d = Mathf.Clamp01(room.depthFrac);
            MobRankTier t = DungeonRankUtil.ClampTier(
                (int)rank - DungeonTunables.RoomTierRampDepth
                + (int)Math.Round(DungeonTunables.RoomTierRampDepth * d)
                + room.tierOffset);
            return t > rank ? rank : t;
        }

        /// <summary>
        /// Encounter purse for one room. Deeper rooms pay more; OPTIONAL rooms
        /// pay a little less (the party may walk straight past them) and a wing's
        /// champion a little more. The final multiplier is clamped to the
        /// deepest mandatory room's, so no side room can ever out-fat the last
        /// room of the critical path.
        /// </summary>
        private static float BudgetFor(DungeonRoom room, MobRankTier rank, DungeonKindDef def,
                                      float rankIndex = -1f, float encounterFactor = 1f)
        {
            float f = Mathf.Lerp(DungeonTunables.FirstRoomBudgetFactor,
                                 DungeonTunables.LastRoomBudgetFactor,
                                 Mathf.Clamp01(room.depthFrac));
            if (!room.onCriticalPath)
                f *= DungeonTunables.OffPathBudgetFactor;
            if (room.tierOffset > 0)
                f *= DungeonTunables.ChampionBudgetFactor;
            f = Mathf.Min(f, DungeonTunables.LastRoomBudgetFactor);
            return def.encounterPointsByRank.Evaluate(rankIndex >= 0f ? rankIndex : (int)rank) * f * encounterFactor;
        }

        /// <summary>
        /// Spawns the def's authored character as the boss. False on any
        /// failure, which sends SpawnBoss back to the ordinary rolled path —
        /// a floor with the wrong boss is playable; a floor with no boss is a
        /// dead end.
        ///
        /// NO entourage and no NormalizeRank: an authored character arrives
        /// finished. Registering it in the boss room is what wires it into
        /// aggro, the leash and the clear objective exactly like any other
        /// boss.
        ///
        /// Under bossOnly the placement changes: the map CENTRE rather than a
        /// cell inside the boss room's rect. On an Open-layout floor the room
        /// rects are abstract regions of a field nobody walled, so a boss room's
        /// centre is an arbitrary spot on open ground — which reads as the warden
        /// having wandered off, when the whole image is one figure standing in
        /// the middle of the roof.
        /// </summary>
        private static bool TrySpawnAuthoredBoss(Map map, DungeonMapComponent comp, DungeonRoom bossRoom, DungeonKindDef def)
        {
            try
            {
                Pawn boss = def.authoredBoss == ElijahWarden.CharacterDefName
                    ? ElijahWarden.Generate(map)
                    : IsekaiCharacterForge.Generate(def.authoredBoss, map.Tile, locked: true);
                if (boss == null)
                    return false;

                IntVec3 cell;
                if (def.bossOnly)
                {
                    // Walk out from dead centre only as far as needed — on open
                    // ground the first cell is almost always standable, and the
                    // fallback keeps a blocked centre from dropping the boss.
                    cell = map.Center;
                    if (!cell.Standable(map)
                        && !CellFinder.TryFindRandomCellNear(map.Center, map, 12,
                                c => c.Standable(map), out cell))
                        cell = map.Center;
                }
                else if (!TryFindSpawnCell(map, bossRoom, out cell))
                {
                    cell = bossRoom.rect.CenterCell;
                }

                GenSpawn.Spawn(boss, cell, map, Rot4.South);
                comp.RegisterBoss(boss);
                comp.RegisterCreature(bossRoom, boss);
                return true;
            }
            catch (System.Exception ex)
            {
                Log.Error($"{IsekaiDungeonsMod.LogPrefix} Authored boss '{def.authoredBoss}' failed to spawn; falling back to a rolled boss. {ex}");
                return false;
            }
        }

        private static PawnKindDef PopulateCombatRoom(Map map, DungeonMapComponent comp, DungeonRoom room, MobRankTier tier, float budget, DungeonKindDef def, float depthFrac, MobRankTier dungeonRank)
        {
            PawnKindDef strongest = null;
            if (ThreatParity.AppliesTo(def))
                budget *= ThreatParity.BudgetFactor(dungeonRank);
            float budgetLeft = budget;
            int spawned = 0;

            // Without Isekai Creatures the generic pool is mostly cheap vanilla
            // kinds: fewer, stronger bodies, then the rest of the budget as
            // upgrades (see ThreatParity). Off with the add-on installed.
            bool parity = ThreatParity.AppliesTo(def);
            var placed = parity ? new List<Pawn>() : null;
            int cap = parity ? Mathf.Min(MaxCreaturesPerRoom, ThreatParity.SoftCapPerRoom) : MaxCreaturesPerRoom;
            bool upgraded = false;

            while (true)
            {
                if (spawned >= cap)
                {
                    // Soft cap reached with budget left: spend it on the room's
                    // creatures first, and only if upgrades cannot absorb it
                    // resume adding bodies up to the real cap.
                    if (!parity || upgraded || budgetLeft <= 0f || cap >= MaxCreaturesPerRoom)
                        break;
                    MobRankTier ceiling = (MobRankTier)Mathf.Min((int)MobRankTier.Nation,
                                                                 (int)dungeonRank + ThreatParity.UpgradeRankHeadroom);
                    budgetLeft = ThreatParity.SpendOnUpgrades(placed, tier, ceiling, budgetLeft);
                    upgraded = true;
                    cap = MaxCreaturesPerRoom;
                    if (budgetLeft <= 0f)
                        break;
                    continue;
                }

                // Roster first (dungeon identity), generic rank providers second.
                // The ceiling is the DUNGEON's rank, not the room's tier: room
                // tiers deliberately ramp downward toward the door, and capping
                // at the room tier would strip a high dungeon's own roster out
                // of its early rooms. The band guard only exists to stop picks
                // ABOVE what the whole dungeon promised.
                PawnKindDef kind;
                bool picked = DungeonRoster.TryPickFromRoster(def, depthFrac, budgetLeft, out kind, dungeonRank)
                              || (parity
                                  ? ThreatParity.TryPickStrong(tier, budgetLeft, out kind)
                                  : DungeonSpawnResolver.TryPickCreature(tier, budgetLeft, out kind));
                if (!picked)
                {
                    // No provider can serve this tier at all. With the base-mod
                    // provider in the chain this means a pathologically empty
                    // DefDatabase — worth a loud line if it left the room empty.
                    if (spawned == 0)
                        Log.Warning($"{IsekaiDungeonsMod.LogPrefix} No spawn provider could pick a creature at tier {tier}; combat room {room.index} left empty.");
                    break;
                }

                float cost = DungeonSpawnResolver.CombatPowerOf(kind);
                if (spawned > 0 && cost > budgetLeft)
                    break; // budget genuinely spent; only the first creature may overshoot

                Pawn pawn = SpawnCreatureInRoom(map, room, kind);
                if (pawn == null)
                    break; // no standable cell / generation failure — logged inside, stop the room

                DungeonSpawnResolver.NormalizeRank(pawn, tier);
                comp.RegisterCreature(room, pawn);
                placed?.Add(pawn);

                spawned++;
                budgetLeft -= cost;
                if (strongest == null || cost > DungeonSpawnResolver.CombatPowerOf(strongest))
                    strongest = kind;

                if (budgetLeft <= 0f)
                    break;
            }

            return strongest;
        }

        /// <summary>
        /// Spawns the boss (single big at-rank creature, elite per archetype) and
        /// its minion entourage, and registers both with the map component. When
        /// every provider fails to name a boss kind, the strongest kind already
        /// spawned in the combat rooms is promoted — clear detection polls the
        /// boss pawn, so a bossless dungeon would be permanently unclearable (G5).
        /// </summary>
        private static void SpawnBoss(Map map, DungeonMapComponent comp, DungeonRoom bossRoom, MobRankTier rank, DungeonKindDef def, PawnKindDef promotionCandidate)
        {
            // ── An authored boss wins over any roll ─────────────────────────
            // A named character is the whole identity of the floor it stands
            // on, so it is tried FIRST and the rolled path becomes its fallback
            // rather than its competition.
            if (!string.IsNullOrEmpty(def.authoredBoss) && TrySpawnAuthoredBoss(map, comp, bossRoom, def))
                return;

            PawnKindDef bossKind = PickMultiFloorBoss(map, def, rank, out bool multiFloor);
            if (!multiFloor)
            {
                bossKind = DungeonRoster.PickBossFromRoster(def, rank)
                           ?? DungeonRoster.ThemedStandInBoss(def, rank)
                           ?? (ThreatParity.AppliesTo(def) ? ThreatParity.PickStrongBoss(rank) : DungeonSpawnResolver.PickBoss(rank));
            }

            if (bossKind == null)
            {
                Log.Error($"{IsekaiDungeonsMod.LogPrefix} No spawn provider produced a boss kind for rank {rank}; promoting the strongest room-spawn kind instead.");
                bossKind = promotionCandidate;
            }
            if (bossKind == null)
            {
                // Truly nothing spawned anywhere (empty bestiary AND empty
                // DefDatabase pool). Absolute last resort: a vanilla core-game
                // kind that always exists, so the dungeon stays clearable.
                Log.Error($"{IsekaiDungeonsMod.LogPrefix} No creatures spawned at all; falling back to a vanilla Thrumbo boss so the dungeon can still be cleared.");
                bossKind = PawnKindDefOf.Thrumbo;
            }

            Pawn boss = SpawnCreatureInRoom(map, bossRoom, bossKind);
            if (boss == null)
            {
                // SpawnCreatureInRoom already warned about the cell/gen failure;
                // escalate, because an absent boss pawn means an unclearable map.
                Log.Error($"{IsekaiDungeonsMod.LogPrefix} Boss spawn failed in room {bossRoom.index} — the dungeon cannot be cleared. This should never happen with a carved boss room.");
                return;
            }

            // Boss is ALWAYS forced to at least the gate rank, elite per the
            // archetype def. raiseAuthored: room creatures keep an authored
            // rank, but a boss below its own gate (a B troll crowning an S
            // dungeon whose rooms field S) is raised, never lowered.
            DungeonSpawnResolver.NormalizeRank(boss, rank, elite: def.bossIsElite, raiseAuthored: true);

            // Without Isekai Creatures every rolled boss is held to the boss
            // threat the Creatures-installed game puts in a room of this rank.
            // That includes vanilla-roster dungeons: without the add-on they
            // are what a player draws at this rank, so their boss must not be
            // the easy option. Named authored bosses returned earlier, and
            // humanlikes (no mob rank) are left alone by LiftBoss.
            if (ThreatParity.AppliesTo(def))
                ThreatParity.LiftBoss(boss, rank);

            // Elite naming already happens inside SetEliteOverride for wild pawns,
            // but non-elite archetypes (bossIsElite false) would leave the boss
            // nameless — the clear letter and the boss's inspect line both read
            // much better with "Ironhide the Megasloth" than "megasloth".
            if (boss.Name == null)
                boss.Name = new NameSingle(MobRankUtility.GenerateEliteName(boss, rank));

            comp.RegisterBoss(boss);
            // ALSO register the boss as a member of its own zone. Both AggroRoom
            // and LeashSweep iterate room.creatures exclusively, so a boss that
            // is only in the `boss` field is never woken with its room and -
            // fatally for an Open dungeon - is never leashed. It would be the
            // one creature free to wander a wall-less field, and could be
            // standing at the exit portal by the time the party arrives.
            comp.RegisterCreature(bossRoom, boss);

            // Entourage: a few at-rank minions sharing the boss room, registered
            // as ordinary room creatures so they aggro with the room (the boss
            // never fights alone — an S-rank boss room with one pawn kites too
            // easily). Generous per-pick budget: entourage size is count-capped,
            // not points-capped.
            // bossOnly means alone. The entourage exists so a boss cannot be
            // kited by one colonist, which is a trade this flag deliberately
            // gives up: a floor built around a single duel should contain a
            // single duel.
            int minionCount = def.bossOnly ? 0 : Rand.RangeInclusive(MinBossMinions, MaxBossMinions);
            for (int i = 0; i < minionCount; i++)
            {
                if (!DungeonRoster.TryPickFromRoster(def, 1f, float.MaxValue, out PawnKindDef minionKind, rank)
                    && !(ThreatParity.AppliesTo(def)
                         ? ThreatParity.TryPickStrong(rank, float.MaxValue, out minionKind)
                         : DungeonSpawnResolver.TryPickCreature(rank, float.MaxValue, out minionKind)))
                    break;

                Pawn minion = SpawnCreatureInRoom(map, bossRoom, minionKind);
                if (minion == null)
                    break;

                DungeonSpawnResolver.NormalizeRank(minion, rank);
                comp.RegisterCreature(bossRoom, minion);
            }
        }

        /// <summary>
        /// A multi-floor descent picks its bosses by POSITION, not just by rank.
        ///
        /// The bottom floor gets the plan's final boss and nothing else may: a
        /// dungeon whose whole premise is "the queen is at the bottom" cannot
        /// serve her on floor 1 because she happened to fit the band (the Titan
        /// Pit used to field Corcera on all ten floors). The floors above draw a
        /// lieutenant from the kind's list with the final boss removed, then the
        /// strongest of the dungeon's own creatures as an elite, and only then
        /// the generic pick - so a floor's boss is always one of the place's own.
        /// </summary>
        private static PawnKindDef PickMultiFloorBoss(Map map, DungeonKindDef def, MobRankTier rank, out bool handled)
        {
            handled = false;
            if (!(map?.Parent is MultiFloorMapParent mf) || mf.Plan == null)
                return null;
            handled = true;

            MultiFloorDungeonDef plan = mf.Plan;
            PawnKindDef final = plan.FinalBoss;
            if (plan.IsLastFloor(mf.floor) && final != null)
                return final;

            var exclude = final != null ? new List<PawnKindDef> { final } : null;
            PawnKindDef pick = DungeonRoster.PickBossFromRoster(def, rank, exclude);
            if (pick == null && def.stayOnTheme)
            {
                pick = DungeonRoster.StrongestThemedKind(def, rank);
                if (pick == final)
                    pick = null;
            }
            return pick ?? DungeonSpawnResolver.PickBoss(rank);
        }

        /// <summary>
        /// Generates and spawns one wild creature at a random standable cell of
        /// the room. Cell is found BEFORE the pawn is generated so a full room
        /// never leaks generated-but-unspawned pawns. Returns null (with a log)
        /// when no cell or the generator fails — callers stop filling that room.
        /// </summary>
        private static Pawn SpawnCreatureInRoom(Map map, DungeonRoom room, PawnKindDef kind)
        {
            // Group anchor: one point per zone, chosen on first use and reused
            // for every later member. Scattering uniformly across an Open zone
            // (20-40 cells wide) makes a "group" arrive strung out over 20
            // seconds and get defeated in detail; clustering makes the zone read
            // as a warband holding a patch of ground. Carve rooms are small
            // enough that this is a no-op there.
            if (!room.groupAnchor.IsValid || !room.groupAnchor.InBounds(map))
            {
                if (!CellFinder.TryFindRandomCellInsideWith(room.rect, c => c.Standable(map), out IntVec3 anchor))
                    anchor = room.rect.CenterCell;
                room.groupAnchor = anchor;
            }

            int clusterRadius = Mathf.Max(3, Mathf.Min(8, room.rect.Width / 3));
            if (!CellFinder.TryFindRandomCellNear(room.groupAnchor, map, clusterRadius,
                    c => c.Standable(map) && room.rect.Contains(c), out IntVec3 cell))
            {
                // Cluster is full or blocked - fall back to anywhere in the zone.
                if (!CellFinder.TryFindRandomCellInsideWith(room.rect, c => c.Standable(map), out cell))
                {
                    Log.Warning($"{IsekaiDungeonsMod.LogPrefix} No standable cell in zone {room.index} for {kind.defName}; skipping this spawn.");
                    return null;
                }
            }

            // Humanlike dwellers need a real faction or they brawl once woken
            // (see DungeonHostility). Animals still resolve to null = wild.
            Pawn pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(
                kind, DungeonHostility.FactionFor(kind), PawnGenerationContext.NonPlayer,
                forceGenerateNewPawn: true));
            if (pawn == null)
            {
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} PawnGenerator returned null for {kind.defName}.");
                return null;
            }

            GenSpawn.Spawn(pawn, cell, map, Rot4.Random);
            return pawn;
        }

        /// <summary>
        /// Random standable cell inside the room rect, with a relaxed linear-scan
        /// fallback before giving up (G5: constrained finders degrade, never
        /// silently drop). The random pass can miss in a crowded room even when
        /// free cells remain — it samples, it doesn't enumerate.
        /// </summary>
        private static bool TryFindSpawnCell(Map map, DungeonRoom room, out IntVec3 cell)
        {
            if (CellFinder.TryFindRandomCellInsideWith(room.rect,
                    c => c.Standable(map), out cell))
                return true;

            // Relaxed fallback: deterministic scan for ANY standable cell.
            foreach (IntVec3 c in room.rect)
            {
                if (c.Standable(map))
                {
                    cell = c;
                    return true;
                }
            }

            cell = IntVec3.Invalid;
            return false;
        }

        /// <summary>
        /// Scatters the treasure roll across a side-room's floor. Items place
        /// with ThingPlaceMode.Near so stacks merge/shift gracefully when the
        /// random cell is occupied by a previous drop.
        /// </summary>
        private static void PlaceTreasure(Map map, DungeonRoom room, MobRankTier rank, DungeonKindDef def)
        {
            List<Thing> loot = DungeonLootGenerator.WithValueScale(DungeonTunables.LootMultiplierFor(map),
                                                                   () => def.LootWorker.GenerateTreasureLoot(rank, def));
            if (loot == null || loot.Count == 0)
                return;

            // A longer wing pays more, SUB-LINEARLY. Stacking a full treasure
            // roll per wing room put the majority of a Nation dungeon's value in
            // an optional dead end; scaling one roll keeps a length-4 wing worth
            // about twice a stub instead of four times it.
            if (room.lootScale > 1.001f)
            {
                for (int i = 0; i < loot.Count; i++)
                {
                    if (loot[i] != null && loot[i].def.stackLimit > 1)
                        loot[i].stackCount = Mathf.Clamp(
                            Mathf.RoundToInt(loot[i].stackCount * room.lootScale), 1, loot[i].def.stackLimit);
                }
            }

            for (int i = 0; i < loot.Count; i++)
            {
                Thing thing = loot[i];
                if (thing == null)
                    continue;

                if (!TryFindSpawnCell(map, room, out IntVec3 cell))
                    cell = room.rect.CenterCell; // Near-mode radiates outward from any anchor

                if (!GenPlace.TryPlaceThing(thing, cell, map, ThingPlaceMode.Near))
                    Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Failed to place treasure {thing.LabelCap} in room {room.index}; item dropped from the roll.");
            }
        }
    }
}
