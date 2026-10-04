using System.Collections.Generic;
using IsekaiLeveling.MobRanking;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace IsekaiDungeons
{
    // ═══════════════════════════════════════════════════════════════════════
    // RIVAL ADVENTURER PARTY
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Another delving party got here first — and they are not sharing.
    ///
    /// Humanlike raiders from a real hostile faction, given an assault lord so
    /// they behave like a competent party rather than milling about, and rank-
    /// normalised so they are a fair fight for the gate's rank. They are hostile
    /// to the player by faction, which means the dungeon's own creatures (wild,
    /// factionless, and manhunter once woken) will fight them too — the room can
    /// genuinely turn into a three-way, which is the whole appeal.
    ///
    /// Their gear is the reward: killing a C-rank party for its weapons is a
    /// real incentive, so this event pays nothing extra on top.
    /// </summary>
    public class RoomEventWorker_RivalParty : DungeonRoomEventWorker
    {
        /// <summary>
        /// A few draws from the faction's own kinds; the first that fits the
        /// per-member budget wins, otherwise the cheapest one drawn.
        /// </summary>
        private static PawnKindDef PickAffordableKind(Faction faction, float perPawn)
        {
            PawnKindDef cheapest = null;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                PawnKindDef k = faction.RandomPawnKind();
                if (k == null)
                    continue;
                if (k.combatPower <= perPawn)
                    return k;
                if (cheapest == null || k.combatPower < cheapest.combatPower)
                    cheapest = k;
            }
            return cheapest;
        }

        public override bool CanPlace(Map map, DungeonRoom room, DungeonKindDef kind, MobRankTier rank)
        {
            // Needs a hostile humanlike faction to recruit from. On a world with
            // none (permanent peace, heavy modding) the room quietly stays an
            // ordinary fight instead of spawning factionless "raiders".
            return PickFaction() != null;
        }

        public override void Populate(DungeonRoomEventContext ctx)
        {
            Faction faction = PickFaction();
            if (faction == null)
                return;

            // Party size scales with the room budget but stays a PARTY — a
            // handful of dangerous individuals, not a raid.
            int count = Mathf.Clamp(Mathf.RoundToInt(ctx.budget / 260f) + 2, 2, 6);
            var spawned = new List<Pawn>();

            // Each member's KIND fits a share of the room budget. Without this
            // any kind in the faction's group makers was fair game, so an E-rank
            // room could field a grenadier, a heavy gunner or an Imperial
            // cataphract. Rank normalisation rerolls stats, not gear.
            float perPawn = ctx.budget / count;

            for (int i = 0; i < count; i++)
            {
                PawnKindDef kind = PickAffordableKind(faction, perPawn);
                if (kind == null)
                    break;

                Pawn pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(
                    kind, faction, PawnGenerationContext.NonPlayer,
                    forceGenerateNewPawn: true, mustBeCapableOfViolence: true));
                if (pawn == null)
                    continue;

                IntVec3 cell = RandomCellIn(ctx.map, ctx.room);
                GenSpawn.Spawn(pawn, cell, ctx.map, Rot4.Random);

                // ctx.rank, the GATE rank, not ctx.tier. The room tier is the
                // difficulty ramp for the dungeon's own inhabitants, which sits
                // up to two tiers below the gate in the early rooms. A rival
                // party is not an inhabitant: they are another guild's crew who
                // came here for the same dungeon the player did, and they
                // equipped for the gate on the sign, not for whichever corridor
                // they happen to be standing in.
                DungeonSpawnResolver.NormalizeRank(pawn, ctx.rank);
                ctx.component.RegisterCreature(ctx.room, pawn);
                spawned.Add(pawn);
            }

            if (spawned.Count == 0)
                return;

            // A lord, because humanlikes ignore the manhunter state the aggro
            // system uses for creatures. Without this they would stand around
            // being shot. canTimeoutOrFlee false: they are here to hold the
            // room, not to wander off the map.
            LordMaker.MakeNewLord(faction,
                new LordJob_AssaultColony(faction, canKidnap: false, canTimeoutOrFlee: false, canSteal: false),
                ctx.map, spawned);
        }

        private static Faction PickFaction()
        {
            return Find.FactionManager?.RandomEnemyFaction(
                allowHidden: false, allowDefeated: false, allowNonHumanlike: false);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // TRAP
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The room itself is the enemy. Nothing visible until someone walks in,
    /// then the floor goes up.
    ///
    /// Deliberately TELEGRAPHED and delayed rather than instant: an unavoidable
    /// one-shot that deletes a colonist the moment they cross a threshold is
    /// not difficulty, it is a coin flip. The party gets a warning message and
    /// roughly two seconds to scatter before the blasts land, so reacting well
    /// is rewarded and standing still is punished.
    ///
    /// Layered on top of the normal encounter (replacesEncounter false in XML):
    /// the trap is nastiest when it goes off mid-fight.
    /// </summary>
    public class RoomEventWorker_Trap : DungeonRoomEventWorker
    {
        private const int FuseTicks = 120;

        public override void OnEntered(DungeonRoomEventContext ctx)
        {
            Message(ctx, "IsekaiDungeons.Event.Trap.Triggered", MessageTypeDefOf.ThreatSmall);

            // Pick the blast cells now (so the telegraph and the blast agree),
            // then fire after the fuse. Cells are biased toward the room centre,
            // leaving the edges as the reward for reacting.
            int blasts = Mathf.Clamp(Mathf.RoundToInt(ctx.room.rect.Width / 4f), 2, 6);
            var cells = new List<IntVec3>();
            for (int i = 0; i < blasts; i++)
            {
                if (CellFinder.TryFindRandomCellNear(ctx.room.rect.CenterCell, ctx.map,
                        Mathf.Max(2, ctx.room.rect.Width / 3),
                        c => c.InBounds(ctx.map) && ctx.room.rect.Contains(c), out IntVec3 c2))
                    cells.Add(c2);
            }
            if (cells.Count == 0)
                cells.Add(ctx.room.rect.CenterCell);

            // Telegraph where it will land.
            for (int i = 0; i < cells.Count; i++)
                FleckMaker.Static(cells[i], ctx.map, FleckDefOf.ExplosionFlash, 3f);

            Map map = ctx.map;
            MobRankTier rank = ctx.rank;
            // Damage scales with rank so a Nation trap is not a firecracker,
            // but stays survivable for a party geared for that rank.
            int damage = Mathf.Clamp(12 + (int)rank * 6, 12, 70);

            map.GetComponent<DungeonMapComponent>()?.QueueDelayed(FuseTicks, () =>
            {
                for (int i = 0; i < cells.Count; i++)
                {
                    GenExplosion.DoExplosion(cells[i], map, 2.4f, DamageDefOf.Bomb, null,
                        damAmount: damage, armorPenetration: 0.1f);
                }
            });
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // AMBUSH
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A room that reads as empty and safe, until the party is standing in the
    /// middle of it. Everything spawns AROUND them, already awake.
    ///
    /// The value of this event is entirely in the contrast: a dungeon where
    /// every room is visibly occupied teaches the player to clear from the
    /// doorway. One empty-looking room that isn't punishes that habit once and
    /// makes every subsequent quiet room tense.
    /// </summary>
    public class RoomEventWorker_Ambush : DungeonRoomEventWorker
    {
        public override void Populate(DungeonRoomEventContext ctx)
        {
            // Nothing at generation — that is the point. The room is empty on
            // arrival and the encounter is created when it triggers.
        }

        public override void OnEntered(DungeonRoomEventContext ctx)
        {
            Message(ctx, "IsekaiDungeons.Event.Ambush.Sprung", MessageTypeDefOf.ThreatBig);

            // Ambushers appear at the room's EDGES, surrounding whoever walked
            // in, and are awake immediately (the room is flagged aggroed by the
            // caller right after this).
            int spawned = 0;
            float left = ctx.budget * 1.15f; // slightly richer: it is a surprise
            var edge = new List<IntVec3>();
            foreach (IntVec3 c in ctx.room.rect.EdgeCells)
            {
                if (c.InBounds(ctx.map) && c.Standable(ctx.map))
                    edge.Add(c);
            }
            if (edge.Count == 0)
                edge.Add(ctx.room.rect.CenterCell);

            while (spawned < 20 && left > 0f)
            {
                if (!DungeonRoster.TryPickFromRoster(ctx.kind, ctx.depth, left, out PawnKindDef pick, ctx.tier)
                    && !DungeonSpawnResolver.TryPickCreature(ctx.tier, left, out pick))
                    break;
                if (pick == null)
                    break;
                float cost = DungeonSpawnResolver.CombatPowerOf(pick);
                if (spawned > 0 && cost > left)
                    break;

                Pawn pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(
                    pick, DungeonHostility.FactionFor(pick), PawnGenerationContext.NonPlayer,
                    forceGenerateNewPawn: true));
                if (pawn == null)
                    break;

                GenSpawn.Spawn(pawn, edge.RandomElement(), ctx.map, Rot4.Random);
                DungeonSpawnResolver.NormalizeRank(pawn, ctx.tier);
                ctx.component.RegisterCreature(ctx.room, pawn);
                spawned++;
                left -= cost;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // GUARDED HOARD
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A treasure room with something sitting on the treasure.
    ///
    /// The plain treasure room already exists as a role and is free loot; this
    /// is its risk-bearing sibling — a much bigger pile, and an elite guardian
    /// that is a tier above the room it stands in. It gives the player a real
    /// decision (take the fight or walk past) instead of a pickup.
    /// </summary>
    public class RoomEventWorker_GuardedHoard : DungeonRoomEventWorker
    {
        public override void Populate(DungeonRoomEventContext ctx)
        {
            IntVec3 center = ctx.room.rect.CenterCell;

            // The hoard: a treasure roll at the DUNGEON's rank rather than the
            // room's, so a hoard found early is genuinely worth the detour.
            List<Thing> loot = DungeonLootGenerator.WithValueScale(DungeonTunables.LootMultiplierFor(ctx.map),
                                                                   () => ctx.kind.LootWorker.GenerateTreasureLoot(ctx.rank, ctx.kind));
            ScatterLoot(ctx.map, ctx.room, center, loot);

            // The guardian: one creature, a tier above the room, marked elite.
            MobRankTier guardTier = DungeonRankUtil.ClampTier((int)ctx.tier + 1);
            if (guardTier > ctx.rank)
                guardTier = ctx.rank;

            PawnKindDef kind = DungeonRoster.PickBossFromRoster(ctx.kind, guardTier)
                               ?? DungeonSpawnResolver.PickBoss(guardTier);
            if (kind == null)
                return;

            Pawn guard = PawnGenerator.GeneratePawn(new PawnGenerationRequest(
                kind, DungeonHostility.FactionFor(kind), PawnGenerationContext.NonPlayer,
                forceGenerateNewPawn: true));
            if (guard == null)
                return;

            GenSpawn.Spawn(guard, RandomCellIn(ctx.map, ctx.room), ctx.map, Rot4.Random);
            DungeonSpawnResolver.NormalizeRank(guard, guardTier, elite: true);
            ctx.component.RegisterCreature(ctx.room, guard);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // CHAMPION
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A single elite standing alone — a mini-boss on the way to the real one.
    /// Trades the usual crowd for one much harder target, which changes how the
    /// party has to fight (focus fire and positioning instead of area control).
    /// </summary>
    public class RoomEventWorker_Champion : DungeonRoomEventWorker
    {
        public override void Populate(DungeonRoomEventContext ctx)
        {
            MobRankTier tier = DungeonRankUtil.ClampTier((int)ctx.tier + 1);
            if (tier > ctx.rank)
                tier = ctx.rank;

            PawnKindDef kind = DungeonRoster.PickBossFromRoster(ctx.kind, tier)
                               ?? DungeonSpawnResolver.PickBoss(tier);
            if (kind == null)
                return;

            Pawn champ = PawnGenerator.GeneratePawn(new PawnGenerationRequest(
                kind, DungeonHostility.FactionFor(kind), PawnGenerationContext.NonPlayer,
                forceGenerateNewPawn: true));
            if (champ == null)
                return;

            GenSpawn.Spawn(champ, ctx.room.rect.CenterCell, ctx.map, Rot4.Random);
            DungeonSpawnResolver.NormalizeRank(champ, tier, elite: true);
            if (champ.Name == null)
                champ.Name = new NameSingle(MobRankUtility.GenerateEliteName(champ, tier));
            ctx.component.RegisterCreature(ctx.room, champ);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // SURVIVOR
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A downed member of an earlier party, still alive. Rescuable — carry them
    /// out through the portal and they are yours.
    ///
    /// The only non-combat event in the set, and the reason it exists: a dungeon
    /// made entirely of fights teaches the player that every room is the same
    /// decision. One room that asks a different question (spend the time and the
    /// stretcher, or leave them) is worth more than another fight.
    ///
    /// Downed rather than a prisoner: no cage to build, no faction to negotiate
    /// with, and the vanilla rescue job already does exactly the right thing.
    /// </summary>
    public class RoomEventWorker_Survivor : DungeonRoomEventWorker
    {
        public override bool CanPlace(Map map, DungeonRoom room, DungeonKindDef kind, MobRankTier rank)
        {
            return PawnKindDefOf.SpaceRefugee != null || Find.FactionManager?.RandomEnemyFaction() != null;
        }

        public override void Populate(DungeonRoomEventContext ctx)
        {
            PawnKindDef kind = PawnKindDefOf.SpaceRefugee ?? PawnKindDefOf.Villager;
            if (kind == null)
                return;

            // Factionless so they can simply be rescued and recruited, with no
            // diplomatic side effects for the player's colony.
            Pawn survivor = PawnGenerator.GeneratePawn(new PawnGenerationRequest(
                kind, null, PawnGenerationContext.NonPlayer, forceGenerateNewPawn: true,
                canGeneratePawnRelations: false));
            if (survivor == null)
                return;

            GenSpawn.Spawn(survivor, RandomCellIn(ctx.map, ctx.room), ctx.map, Rot4.Random);

            // NORMALISED TO THE FLOOR'S OWN RANK, and this was the one pawn
            // event that never was. Somebody found alive on floor 90 got this
            // deep somehow — a plain refugee there reads as a bug, not as a
            // rescue, and recruiting them gave you a starting-tier colonist out
            // of the hardest place in the game.
            //
            // ctx.rank, NOT ctx.tier: the room tier is the front-to-back
            // encounter ramp, and a survivor is not part of the encounter. What
            // matters is how deep into the tower they were, which is the floor.
            DungeonSpawnResolver.NormalizeRank(survivor, ctx.rank);

            // Beaten down but stable: hurt enough to be unable to walk out on
            // their own, not so hurt that they die before anyone reaches them.
            // AFTER normalising, so the downing threshold is measured against
            // the health they actually have.
            HealthUtility.DamageUntilDowned(survivor, allowBleedingWounds: false);

            Find.LetterStack.ReceiveLetter(
                "IsekaiDungeons.Event.Survivor.Label".Translate(),
                "IsekaiDungeons.Event.Survivor.Text".Translate(survivor.LabelShortCap.Named("PAWN")),
                LetterDefOf.PositiveEvent, survivor);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // MANA SPRING
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A vein of raw mana. Free cores lying in the open — and the dungeon
    /// notices when you take them.
    ///
    /// A greed check: the cores are visible and grabbable from the doorway, but
    /// entering the room wakes a wave sized to the room's budget. Taking the
    /// loot and running is a legitimate play; so is clearing first.
    /// </summary>
    public class RoomEventWorker_ManaSpring : DungeonRoomEventWorker
    {
        public override void Populate(DungeonRoomEventContext ctx)
        {
            // The prize, laid out in the open where it can be seen.
            var cores = new List<Thing>();
            AddCore(cores, ctx.rank);
            ScatterLoot(ctx.map, ctx.room, ctx.room.rect.CenterCell, cores);
        }

        public override void OnEntered(DungeonRoomEventContext ctx)
        {
            Message(ctx, "IsekaiDungeons.Event.ManaSpring.Stirs", MessageTypeDefOf.ThreatSmall);
            SpawnGroup(ctx, ctx.tier, ctx.budget * 1.25f, ctx.room.rect.CenterCell);
        }

        private static void AddCore(List<Thing> into, MobRankTier rank)
        {
            string defName;
            int count;
            if ((int)rank >= (int)MobRankTier.S) { defName = "Isekai_HugeManaCore"; count = Rand.RangeInclusive(2, 4); }
            else if ((int)rank >= (int)MobRankTier.B) { defName = "Isekai_BigManaCore"; count = Rand.RangeInclusive(3, 5); }
            else if ((int)rank >= (int)MobRankTier.D) { defName = "Isekai_ManaCore"; count = Rand.RangeInclusive(3, 6); }
            else { defName = "Isekai_SmallManaCore"; count = Rand.RangeInclusive(4, 8); }

            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            if (def == null)
                return;
            Thing t = ThingMaker.MakeThing(def);
            t.stackCount = Mathf.Min(count, def.stackLimit);
            into.Add(t);
        }
    }
}
