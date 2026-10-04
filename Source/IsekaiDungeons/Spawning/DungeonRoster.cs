using System.Collections.Generic;
using IsekaiLeveling.MobRanking;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Roster-first creature selection — the layer that turns a generic
    /// rank-appropriate crawl into a NAMED dungeon.
    ///
    /// A DungeonKindDef's roster is what makes "Slime Ranch" full of slimes and
    /// "Mechanoid Foundry" full of mechs. When a dungeon declares a roster we
    /// pick from it; when it declares none (or its bestiary mod is absent and
    /// every entry dropped out), we fall through to DungeonSpawnResolver's
    /// generic rank-banded providers, so a rosterless dungeon still populates.
    ///
    /// Depth gating: entries carry minDepth (0 = first combat room, 1 = last),
    /// so one roster can ramp itself — chaff up front, the nasty kinds gated to
    /// the back half. Combined with the room-tier ramp in GenStep_DungeonPopulation,
    /// a dungeon escalates in BOTH composition and raw rank as you go deeper.
    ///
    /// Every method here is non-throwing (GenStep contract — see
    /// DungeonSpawnResolver's class remarks).
    /// </summary>
    public static class DungeonRoster
    {
        private static readonly List<DungeonSpawnEntry> tmpEligible = new List<DungeonSpawnEntry>();

        /// <summary>
        /// Picks one creature for this dungeon at the given depth. Returns false
        /// only when the dungeon has no usable roster — callers then fall back
        /// to the generic providers.
        /// </summary>
        /// <summary>
        /// How far above the dungeon's rank a roster or boss kind may sit.
        ///
        /// ONE tier. The margin exists because a themed dungeon's crown is
        /// allowed to be scary — the Lich Catacombs at B are crowned by an
        /// A-rank lich, and that is the point of going there. What the margin
        /// refuses is the bug it was written for: the ossuary floors run D to
        /// B, their boss list opened with the A-rank lich, and
        /// PickBossFromRoster rolled it BLIND — so the first stretch of the
        /// tower could serve a boss three tiers over the floor, twice in a row.
        /// A setting (Advanced tab).
        /// </summary>
        private static int RankMargin => IsekaiDungeonsSettings.Current.rosterRankMargin;

        /// <summary>
        /// How far BELOW the dungeon's rank a kind may sit.
        ///
        /// There was no lower bound at all until creatures stopped being
        /// promoted, and without one the band is meaningless in the direction
        /// that matters most: an F-rank slime was a legal pick for a Nation
        /// floor, and the only thing that stopped it being a joke was the
        /// promotion that has now been removed. Two tiers is wide enough that
        /// thin tiers still have company — a floor at SS can draw on S, SS and
        /// SSS, which is ten kinds rather than the single true dragon that sits
        /// at SS alone — and narrow enough that nothing on a floor is a
        /// pushover. A setting (Advanced tab).
        /// </summary>
        private static int RankFloorMargin => IsekaiDungeonsSettings.Current.rosterRankFloorMargin;

        /// <summary>
        /// The rank this kind naturally IS, by the base mod's own arithmetic:
        /// MobRankCalculator floors a creature's threat score at
        /// combatPower x 2.5 (its "most reliable mod-friendly metric"), and
        /// MobRank.CalculateBaseRankFromThreatScore turns score into tier.
        /// Using the same two steps here means selection and the rank label
        /// the player eventually sees can never disagree about what a kind is.
        /// </summary>
        /// <summary>
        /// The rank this kind IS.
        ///
        /// An authored rank wins outright — see CreatureRankRegistry. The
        /// combat-power arithmetic below is only the estimate for creatures
        /// nobody ranked, and it is important to know that it TOPS OUT AT S
        /// (CalculateBaseRankFromThreatScore returns S for every score at or
        /// above 1500). That ceiling is precisely why the estimate cannot be
        /// trusted for the bestiary: it rates a valkyrie, an archangel and a
        /// goblin king identically.
        /// </summary>
        internal static MobRankTier NaturalTierOf(PawnKindDef kind)
        {
            if (CreatureRankRegistry.TryGetAuthored(kind, out MobRankTier authored))
                return authored;
            return MobRankUtility.CalculateBaseRankFromThreatScore((kind?.combatPower ?? 0f) * 2.5f);
        }

        /// <summary>
        /// Whether this kind belongs on a floor of this rank — a real band now,
        /// with a bottom as well as a top.
        ///
        /// The bottom is only enforced for kinds that HAVE an authored rank.
        /// Everything else is an estimate that cannot express anything above S,
        /// so rejecting a vanilla thrumbo from an SSS floor for being "too weak"
        /// would empty the pool on an install without the bestiary and leave the
        /// floor with nothing at all.
        /// </summary>
        private static bool FitsBand(PawnKindDef kind, MobRankTier rank)
        {
            if (kind == null)
                return false;

            int natural = (int)NaturalTierOf(kind);
            if (natural > (int)rank + RankMargin)
                return false;

            return !CreatureRankRegistry.TryGetAuthored(kind, out _)
                   || natural >= (int)rank - RankFloorMargin;
        }

        public static bool TryPickFromRoster(DungeonKindDef def, float depthFrac, float budgetLeft, out PawnKindDef kind, MobRankTier? maxRank = null)
        {
            kind = null;
            if (def == null)
                return false;

            List<DungeonSpawnEntry> resolved = def.ResolvedRoster;
            if (resolved == null || resolved.Count == 0)
                return false;

            // Eligible = depth gate satisfied AND, when the caller supplied the
            // dungeon rank, inside the band (rank + margin). Collected into a
            // scratch list so the weighted roll never allocates per call.
            tmpEligible.Clear();
            for (int i = 0; i < resolved.Count; i++)
            {
                if (depthFrac < resolved[i].minDepth)
                    continue;
                if (maxRank.HasValue
                    && !FitsBand(DefDatabase<PawnKindDef>.GetNamedSilentFail(resolved[i].kind), maxRank.Value))
                    continue;
                tmpEligible.Add(resolved[i]);
            }
            // Nothing unlocked this early (author gated every entry): use the
            // shallowest BAND-LEGAL entries rather than returning nothing; if
            // the band filter is what emptied the list, drop the depth gate
            // but keep the band — an over-rank kind is the one thing this
            // method must never hand back.
            if (tmpEligible.Count == 0)
            {
                float shallowest = float.MaxValue;
                for (int i = 0; i < resolved.Count; i++)
                {
                    if (maxRank.HasValue
                        && !FitsBand(DefDatabase<PawnKindDef>.GetNamedSilentFail(resolved[i].kind), maxRank.Value))
                        continue;
                    if (resolved[i].minDepth < shallowest)
                        shallowest = resolved[i].minDepth;
                }
                for (int i = 0; i < resolved.Count; i++)
                {
                    if (resolved[i].minDepth > shallowest)
                        continue;
                    if (maxRank.HasValue
                        && !FitsBand(DefDatabase<PawnKindDef>.GetNamedSilentFail(resolved[i].kind), maxRank.Value))
                        continue;
                    tmpEligible.Add(resolved[i]);
                }
            }

            // STAY ON THEME. A multi-floor descent is one place all the way
            // down, so when its own creatures sit below a floor's band (the
            // family runs out before the ladder does) it fields the strongest
            // of them rather than inviting strangers from the generic pool.
            // Only the bottom of the band is relaxed; the top never is.
            if (tmpEligible.Count == 0 && def.stayOnTheme && maxRank.HasValue)
                AddStrongestUnderCeiling(resolved, maxRank.Value);

            // Prefer entries that fit the remaining budget; if none fit, take
            // the cheapest eligible kind and let the caller decide whether to
            // spawn it (same contract as the generic providers).
            PawnKindDef cheapest = null;
            float cheapestCost = float.MaxValue;
            float affordableWeight = 0f;
            for (int i = 0; i < tmpEligible.Count; i++)
            {
                PawnKindDef k = DefDatabase<PawnKindDef>.GetNamedSilentFail(tmpEligible[i].kind);
                if (k == null)
                    continue;
                float cost = DungeonSpawnResolver.CombatPowerOf(k);
                if (cost < cheapestCost)
                {
                    cheapestCost = cost;
                    cheapest = k;
                }
                if (cost <= budgetLeft)
                    affordableWeight += tmpEligible[i].weight;
            }

            if (affordableWeight > 0f)
            {
                float roll = Rand.Range(0f, affordableWeight);
                for (int i = 0; i < tmpEligible.Count; i++)
                {
                    PawnKindDef k = DefDatabase<PawnKindDef>.GetNamedSilentFail(tmpEligible[i].kind);
                    if (k == null || DungeonSpawnResolver.CombatPowerOf(k) > budgetLeft)
                        continue;
                    roll -= tmpEligible[i].weight;
                    if (roll <= 0f)
                    {
                        kind = k;
                        tmpEligible.Clear();
                        return true;
                    }
                }
            }

            tmpEligible.Clear();
            kind = cheapest;
            return kind != null;
        }

        /// <summary>
        /// Picks this dungeon's boss. Prefers the roster's declared bosses
        /// (that is the dungeon's identity — the Great Slime, the Warqueen);
        /// returns null so the caller can fall through to generic selection.
        /// </summary>
        public static PawnKindDef PickBossFromRoster(DungeonKindDef def, MobRankTier rank,
                                                     ICollection<PawnKindDef> exclude = null)
        {
            if (def == null)
                return null;
            List<PawnKindDef> bosses = def.ResolvedBosses;
            if (bosses == null || bosses.Count == 0)
                return null;

            // Only bosses the rank can carry. The rank parameter was ALWAYS in
            // this signature and was never read — RandomElement rolled blind
            // across the list, which is how a D-rank ossuary floor served its
            // A-rank lich. Filtering rather than clamping keeps the def's
            // ladder intact: the ossuary's boss list is [lich, skeleton orc],
            // so at D the skeleton orc leads and at B — lich in margin — the
            // lich takes the crown, exactly the escalation the list implies.
            tmpBosses.Clear();
            for (int i = 0; i < bosses.Count; i++)
            {
                if (exclude != null && exclude.Contains(bosses[i]))
                    continue;
                if (FitsBand(bosses[i], rank))
                    tmpBosses.Add(bosses[i]);
            }

            // Every declared boss outranks this dungeon: hand the decision back
            // (null falls through to the generic at-rank boss pick), and say so
            // once in dev mode, because that is a def worth fixing.
            if (tmpBosses.Count == 0)
            {
                if (Prefs.DevMode)
                    Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Every boss on '{def.defName}' outranks {rank}+{RankMargin}; using a generic at-rank boss instead.");
                return null;
            }

            // The crown, not a blind roll across the band. The band reaches two
            // tiers BELOW the dungeon, so a random pick could crown an S-rank
            // dungeon with a B-rank boss while its rooms field S. Prefer the
            // bosses at or above the dungeon's rank (random among them keeps
            // the variety); when none reach it, take the highest tier present.
            int bestTier = -1;
            for (int i = 0; i < tmpBosses.Count; i++)
                bestTier = Mathf.Max(bestTier, (int)NaturalTierOf(tmpBosses[i]));
            int floorTier = Mathf.Min((int)rank, bestTier);
            for (int i = tmpBosses.Count - 1; i >= 0; i--)
                if ((int)NaturalTierOf(tmpBosses[i]) < floorTier)
                    tmpBosses.RemoveAt(i);

            PawnKindDef picked = tmpBosses.RandomElement();
            tmpBosses.Clear();
            return picked;
        }

        /// <summary>
        /// A boss for this dungeon when none of its declared bosses fits the
        /// rank: the strongest of the place's own creatures, as long as it sits
        /// inside the band. An elite of the dungeon's own kind reads as that
        /// dungeon; a generic at-rank pick from the whole bestiary does not
        /// (a C-rank Goblin King's Warren crowned by an off-theme beast).
        /// Null when even the roster has nothing in band.
        /// </summary>
        public static PawnKindDef ThemedStandInBoss(DungeonKindDef def, MobRankTier rank)
        {
            PawnKindDef k = StrongestThemedKind(def, rank);
            return FitsBand(k, rank) ? k : null;
        }

        /// <summary>
        /// The dungeon's own strongest creature this floor can carry: the
        /// highest natural rank in the roster at or under rank + margin,
        /// heaviest combat power breaking ties. A themed stand-in for a boss
        /// when none of the declared bosses fits - an elite of the place's own
        /// kind reads as that place, a generic at-rank pick does not.
        /// </summary>
        public static PawnKindDef StrongestThemedKind(DungeonKindDef def, MobRankTier rank)
        {
            List<DungeonSpawnEntry> resolved = def?.ResolvedRoster;
            if (resolved == null)
                return null;
            PawnKindDef best = null;
            int bestTier = -1;
            float bestPower = -1f;
            for (int i = 0; i < resolved.Count; i++)
            {
                PawnKindDef k = DefDatabase<PawnKindDef>.GetNamedSilentFail(resolved[i].kind);
                if (k == null)
                    continue;
                int t = (int)NaturalTierOf(k);
                if (t > (int)rank + RankMargin)
                    continue;
                float power = DungeonSpawnResolver.CombatPowerOf(k);
                if (t > bestTier || (t == bestTier && power > bestPower))
                {
                    best = k;
                    bestTier = t;
                    bestPower = power;
                }
            }
            return best;
        }

        /// <summary>
        /// Fills tmpEligible with the roster entries of the highest natural
        /// rank that does not exceed rank + margin. Used only by stayOnTheme.
        /// </summary>
        private static void AddStrongestUnderCeiling(List<DungeonSpawnEntry> resolved, MobRankTier rank)
        {
            int top = -1;
            for (int i = 0; i < resolved.Count; i++)
            {
                PawnKindDef k = DefDatabase<PawnKindDef>.GetNamedSilentFail(resolved[i].kind);
                if (k == null)
                    continue;
                int t = (int)NaturalTierOf(k);
                if (t <= (int)rank + RankMargin && t > top)
                    top = t;
            }
            if (top < 0)
                return;
            // The top tier and the one beneath it, so a thin family still has
            // two kinds to mix rather than one creature repeated.
            for (int i = 0; i < resolved.Count; i++)
            {
                PawnKindDef k = DefDatabase<PawnKindDef>.GetNamedSilentFail(resolved[i].kind);
                if (k == null)
                    continue;
                int t = (int)NaturalTierOf(k);
                if (t <= top && t >= top - 1)
                    tmpEligible.Add(resolved[i]);
            }
        }

        /// <summary>Scratch for the boss band filter — same no-alloc convention as tmpEligible.</summary>
        private static readonly List<PawnKindDef> tmpBosses = new List<PawnKindDef>();

        /// <summary>
        /// Full-roster pack for a break raid on a dungeon that was never
        /// entered — the monsters that WOULD have been inside. Roster-driven so
        /// a Slime Ranch break floods the colony with slimes, not with whatever
        /// the generic rank band happened to pick.
        /// </summary>
        public static List<PawnKindDef> RollRosterPack(DungeonKindDef def, MobRankTier rank, float budget)
        {
            var pack = new List<PawnKindDef>();
            if (def == null)
                return pack;

            float remaining = budget;
            int guard = 0;
            while (remaining > 0f && guard++ < 200 && pack.Count < 40)
            {
                float depth = Rand.Value; // spread across the dungeon's depth range
                if (!TryPickFromRoster(def, depth, remaining, out PawnKindDef kind, rank) || kind == null)
                    break;
                pack.Add(kind);
                remaining -= DungeonSpawnResolver.CombatPowerOf(kind);
            }

            // The boss comes out too — it was the thing holding the gate shut.
            PawnKindDef boss = PickBossFromRoster(def, rank);
            if (boss != null)
                pack.Add(boss);

            return pack;
        }
    }
}
