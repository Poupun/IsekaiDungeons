using System.Collections.Generic;
using IsekaiLeveling.MobRanking;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Keeps a dungeon generated WITHOUT Isekai Creatures at least as dangerous
    /// as the same dungeon with it.
    ///
    /// THE PROBLEM. Room budgets are spent in combat power. The bestiary's
    /// creatures are expensive, so a room fills with two or three of them; the
    /// generic pool that stands in when the add-on is absent is mostly cheap
    /// vanilla animals, so the same budget became a crowd that hit the
    /// per-room cap with budget still unspent (300+ pawns on a Nation map, a
    /// TPS problem AND an easier fight than the budget promised). Bosses were
    /// worse: the generic pick was a random kind from the rank band, so an
    /// F-rank boss could be a rat, and nothing in vanilla approaches the
    /// bestiary's dragons and archangels at the top.
    ///
    /// THE RULE. Same budget, fewer and stronger bodies, nothing left unspent:
    ///  - picks lean to the strongest affordable kind,
    ///  - room budgets are scaled per rank (<see cref="BudgetFactor"/>) by the
    ///    measured gap: the bestiary's creatures often carry a rank above their
    ///    room, so the same combat power buys them more threat,
    ///  - a room stops adding bodies at <see cref="SoftCapPerRoom"/> and turns
    ///    the rest of its budget into elite status and rank-ups (up to one
    ///    above the dungeon), priced by the base mod's rank health x damage
    ///    multipliers,
    ///  - if upgrades run out first, spawning resumes up to the normal cap, so
    ///    a room never ends with budget left that it could have spent,
    ///  - every rolled boss is held to a floor measured from the Creatures-
    ///    installed game: elite, then the gate sovereign hediff
    ///    (Hediff_GateSovereign) for the missing multiplier.
    ///
    /// Verified 2026-10-04 with the self-test's threat sampling (four runs):
    /// generic gates without the add-on measured 1.00-1.75x the total threat
    /// and 1.00-2.99x the boss threat of the add-on game at every rank.
    ///
    /// Everything here is OFF when Isekai Creatures is installed: that game is
    /// the reference these numbers were measured against.
    /// </summary>
    public static class ThreatParity
    {
        /// <summary>Bodies per room before the remaining budget becomes upgrades.</summary>
        public const int SoftCapPerRoom = 6;

        /// <summary>Picks sampled per slot; the strongest affordable one wins.</summary>
        private const int PickSamples = 4;

        /// <summary>Elite multiplies health and damage by 1.2 each in the base mod.</summary>
        private const float EliteFactor = 1.44f;

        /// <summary>
        /// Room budget multiplier per dungeon rank F..Nation. The bestiary's
        /// creatures often carry an authored rank ABOVE their room's tier, so
        /// the same combat-power budget buys more threat with the add-on than
        /// the generic pool does. Measured 2026-10-04 (generic gate, with vs
        /// without Creatures, total map threat): without/with = F 1.26,
        /// E 0.84, D 0.70, C 0.82, B 0.69, A 0.92, S 0.76, SS 0.53, SSS 0.67,
        /// Nation 0.67. Factor = 1.1 / ratio, never below 1. A second
        /// measurement with these factors left D, S, SS and SSS at 0.91-0.99,
        /// so those four were raised again by that shortfall plus 10%. A third
        /// run averaged with the second did the same for C, SS and Nation, and
        /// lifted F (rolled F rooms measured 0.78).
        /// </summary>
        private static readonly float[] BudgetFactorByRank = { 1.3f, 1.31f, 1.75f, 1.5f, 1.59f, 1.2f, 1.75f, 2.75f, 1.98f, 2.05f };

        public static float BudgetFactor(MobRankTier dungeonRank) =>
            BudgetFactorByRank[Mathf.Clamp((int)dungeonRank, 0, BudgetFactorByRank.Length - 1)];

        /// <summary>How far above the dungeon's rank room upgrades may push a creature (the bestiary does the same).</summary>
        public const int UpgradeRankHeadroom = 1;

        /// <summary>
        /// Boss threat floor per gate rank F..Nation, in combat power of an ELITE
        /// creature AT the gate rank. Measured 2026-10-04 in the Creatures-
        /// installed game, taking the HIGHER of the generic gate's boss and the
        /// average rolled dungeon's boss (boss threat / rank factor / elite):
        /// F 59, E 233, D 377, C 692, B 1253, A 3708, S 16118, SS 24676,
        /// SSS 64260, Nation 246758 threat.
        /// </summary>
        private static readonly float[] BossFloorCP = { 84f, 224f, 262f, 364f, 497f, 1082f, 2993f, 3060f, 5100f, 5100f };

        private static bool? creaturesActive;

        /// <summary>True when the Isekai Creatures provider is live, i.e. parity is NOT needed.</summary>
        public static bool CreaturesInstalled
        {
            get
            {
                if (creaturesActive == null)
                    creaturesActive = ModsConfig.IsActive("Poupun.IsekaiCreaturesAddon");
                return creaturesActive.Value;
            }
        }

        public static bool Active => !CreaturesInstalled;

        /// <summary>
        /// Parity is calibrated against THIS mod's dungeons and the Isekai
        /// Creatures bestiary they were built for, so it only touches dungeons
        /// Isekai Dungeons ships. A third-party dungeon keeps exactly the
        /// tuning its author wrote, with or without the add-on.
        /// </summary>
        public static bool AppliesTo(DungeonKindDef def)
        {
            if (!Active || def == null)
                return false;
            string pack = def.modContentPack?.PackageId;
            return pack == null || string.Equals(pack, "Poupun.IsekaiDungeonsAddon", System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The base mod's rank power: health multiplier x damage multiplier.</summary>
        public static float RankFactor(MobRankTier tier) =>
            MobRankUtility.GetRankHealthMultiplier(tier) * MobRankUtility.GetRankDamageMultiplier(tier);

        /// <summary>Combat power x rank factor (x elite). Humanlikes carry no mob rank and count at combat power.</summary>
        public static float ThreatOf(Pawn p)
        {
            if (p == null)
                return 0f;
            float cp = DungeonSpawnResolver.CombatPowerOf(p.kindDef);
            MobRankComponent rc = p.GetMobRank();
            if (rc == null)
                return cp;
            float t = cp * RankFactor(rc.Rank);
            if (rc.IsElite)
                t *= EliteFactor;
            if (p.health?.hediffSet?.GetFirstHediff<Hediff_GateSovereign>() is Hediff_GateSovereign sov)
                t *= sov.power;
            return t;
        }

        // ── Rooms ───────────────────────────────────────────────────────────

        /// <summary>
        /// The strongest of a few generic picks that the budget can afford;
        /// when none is affordable, the cheapest drawn (the caller decides
        /// whether an over-budget first creature still spawns, as before).
        /// </summary>
        public static bool TryPickStrong(MobRankTier tier, float budgetLeft, out PawnKindDef kind)
        {
            kind = null;
            PawnKindDef cheapest = null;
            for (int i = 0; i < PickSamples; i++)
            {
                if (!DungeonSpawnResolver.TryPickCreature(tier, budgetLeft, out PawnKindDef k) || k == null)
                    continue;
                float cost = DungeonSpawnResolver.CombatPowerOf(k);
                if (cost <= budgetLeft && (kind == null || cost > DungeonSpawnResolver.CombatPowerOf(kind)))
                    kind = k;
                if (cheapest == null || cost < DungeonSpawnResolver.CombatPowerOf(cheapest))
                    cheapest = k;
            }
            if (kind == null)
                kind = cheapest;
            return kind != null;
        }

        /// <summary>
        /// Spends leftover room budget on the room's creatures, strongest first:
        /// elite, then one rank at a time up to <paramref name="ceiling"/>. Each
        /// step costs the threat it adds, expressed in combat power at the room
        /// tier, so a spent budget is the same threat whether it bought bodies
        /// or upgrades. Returns what could not be spent.
        /// </summary>
        public static float SpendOnUpgrades(List<Pawn> pawns, MobRankTier roomTier, MobRankTier ceiling, float budgetLeft)
        {
            if (pawns == null || pawns.Count == 0 || budgetLeft <= 0f)
                return budgetLeft;

            float tierFactor = Mathf.Max(0.01f, RankFactor(roomTier));
            var order = new List<Pawn>(pawns);
            order.SortByDescending(p => DungeonSpawnResolver.CombatPowerOf(p.kindDef));

            bool progressed = true;
            while (budgetLeft > 0f && progressed)
            {
                progressed = false;
                for (int i = 0; i < order.Count && budgetLeft > 0f; i++)
                {
                    MobRankComponent rc = order[i].GetMobRank();
                    if (rc == null)
                        continue;
                    float cp = DungeonSpawnResolver.CombatPowerOf(order[i].kindDef);
                    float now = cp * RankFactor(rc.Rank) * (rc.IsElite ? EliteFactor : 1f);

                    if (!rc.IsElite)
                    {
                        float cost = now * (EliteFactor - 1f) / tierFactor;
                        if (cost > budgetLeft)
                            continue;
                        rc.SetEliteOverride(true);
                        budgetLeft -= cost;
                        progressed = true;
                        continue;
                    }

                    if (rc.Rank >= ceiling || rc.Rank >= MobRankTier.Nation)
                        continue;
                    MobRankTier next = rc.Rank + 1;
                    float after = cp * RankFactor(next) * EliteFactor;
                    float rankCost = (after - now) / tierFactor;
                    if (rankCost > budgetLeft)
                        continue;
                    rc.SetRankOverride(next);
                    budgetLeft -= rankCost;
                    progressed = true;
                }
            }
            return budgetLeft;
        }

        // ── Bosses ──────────────────────────────────────────────────────────

        /// <summary>
        /// The strongest generic kind from the gate's band and the two above
        /// it. A stand-in boss starts from a heavier body this way, so less of
        /// its power has to come from <see cref="LiftBoss"/>.
        /// </summary>
        public static PawnKindDef PickStrongBoss(MobRankTier rank)
        {
            PawnKindDef best = null;
            for (int up = 0; up <= 2; up++)
            {
                MobRankTier r = (MobRankTier)Mathf.Min((int)MobRankTier.Nation, (int)rank + up);
                PawnKindDef k = DungeonSpawnResolver.PickBoss(r);
                if (k != null && (best == null || DungeonSpawnResolver.CombatPowerOf(k) > DungeonSpawnResolver.CombatPowerOf(best)))
                    best = k;
            }
            return best;
        }

        /// <summary>
        /// Lifts a stand-in boss to the measured floor for its gate rank: elite,
        /// then the gate sovereign hediff for whatever multiplier is still
        /// missing. The boss keeps the gate's rank on its label; the power is
        /// on the hediff, where the player can see it.
        /// </summary>
        public static void LiftBoss(Pawn boss, MobRankTier gateRank)
        {
            MobRankComponent rc = boss?.GetMobRank();
            if (boss == null || rc == null)
                return;

            if (!rc.IsElite)
                rc.SetEliteOverride(true);

            float floor = BossFloorCP[Mathf.Clamp((int)gateRank, 0, BossFloorCP.Length - 1)]
                          * RankFactor(gateRank) * EliteFactor;
            float now = ThreatOf(boss);
            if (now <= 0f || now >= floor)
                return;

            HediffDef def = DefDatabase<HediffDef>.GetNamedSilentFail("IsekaiDungeons_GateSovereign");
            if (def == null)
                return;
            var sov = (Hediff_GateSovereign)HediffMaker.MakeHediff(def, boss);
            Verb verb = boss.TryGetAttackVerb(null);
            sov.Configure(floor / now, isRanged: verb != null && !verb.IsMeleeAttack);
            boss.health.AddHediff(sov);
        }
    }
}
