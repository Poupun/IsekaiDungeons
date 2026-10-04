using IsekaiLeveling.MobRanking;
using UnityEngine;

namespace IsekaiDungeons
{
    /// <summary>
    /// Every number the guild's dungeon contracts are balanced on.
    ///
    /// The mod now has TWO sources of dungeons and they exist to feel different.
    /// A LOCAL gate is background texture: it arrives unasked, on the colony's
    /// own map, and always well below the colony's rank (see
    /// DungeonTunables.LocalRankTopByColonyRank). A GUILD CONTRACT is the
    /// opposite in every respect: the player is told about it, chooses to take
    /// it, travels to it, and it is pitched AT their rank or above.
    ///
    /// That split is the fix for the complaint this system was built for. High
    /// rank dungeons were not the problem; high rank dungeons landing on the
    /// home map every few days without being asked for was.
    ///
    /// The values come from the mod settings, whose defaults are the numbers
    /// this was balanced with; the notes stay here, next to the names code reads.
    /// </summary>
    public static class DungeonQuestTuning
    {
        private static IsekaiDungeonsSettings S => IsekaiDungeonsSettings.Current;

        // ── Cadence ─────────────────────────────────────────────────────────

        /// <summary>Contracts posted per season (quadrum, 15 days). Settings slider default and range.</summary>
        public const float DefaultQuestsPerSeason = 2f;
        public const float MinQuestsPerSeason = 0f;
        public const float MaxQuestsPerSeason = 8f;

        /// <summary>Days before the guild starts posting dungeon work at all.</summary>
        public static float FirstQuestDay => S.firstQuestDay;

        /// <summary>Random spread on the interval, so contracts do not arrive like clockwork.</summary>
        public static float IntervalJitter => S.questIntervalJitter;

        /// <summary>Most contracts live on the world map at once. Beyond this the guild waits.</summary>
        public static int MaxActiveQuests => S.maxActiveQuests;

        // ── Distance ────────────────────────────────────────────────────────
        //
        // Indexed by (int)MobRankTier. A rank-F contract is practically next
        // door because a rank-F party has no travel capacity and no reason to
        // spend a quadrum walking; a Nation contract is genuinely far because
        // being sent to the edge of the known world IS part of what makes it
        // read as the guild's last resort.

        public static int[] MinTilesByRank => S.contractMinTilesByRank;
        public static int[] MaxTilesByRank => S.contractMaxTilesByRank;

        /// <summary>Nearest a contract of this rank may be, with the travel-distance setting applied.</summary>
        public static int MinTilesFor(int rankIndex) =>
            Mathf.Max(1, Mathf.RoundToInt(MinTilesByRank[ClampRank(rankIndex)] * S.contractDistanceMultiplier));

        /// <summary>Farthest a contract of this rank may be, never nearer than the minimum.</summary>
        public static int MaxTilesFor(int rankIndex) =>
            Mathf.Max(MinTilesFor(rankIndex),
                      Mathf.RoundToInt(MaxTilesByRank[ClampRank(rankIndex)] * S.contractDistanceMultiplier));

        // ── Rank ────────────────────────────────────────────────────────────

        // A CONTRACT IS NEVER PITCHED ABOVE THE COLONY'S BEST PAWN.
        //
        // It used to be, at 18%, and that was the complaint this model was
        // rebuilt to answer: a colony whose best was S was being offered SS
        // work. A dungeon is a whole multi-room crawl rather than a duel, so a
        // rank-X dungeon is not a fair fight for one rank-X pawn, and offering
        // above the ceiling on top of that was compounding the mistake.
        //
        // Two things drive the new roll. First, the ceiling is measured from
        // ONE pawn: RaidRankSystem.GetColonyBestRank returns the single highest
        // ranked colonist, so a lone champion carrying five C-rank colonists
        // reads exactly like six champions. Second, dungeon content grows about
        // 1.64x per rank tier, faster than a party's ability to absorb it, so
        // the sensible offset widens as the colony climbs.

        /// <summary>
        /// Ranks below the colony's best that a contract centres on, indexed by
        /// (int)MobRankTier of that best pawn.
        ///
        /// FLATTENED TO 1 from { ..., 2, 2, 2, 2 } at S and above. The extra
        /// step meant an SSS colony's contracts centred on S and a Nation
        /// colony's on SS - two ranks of content the player had outgrown, on the
        /// board that is supposed to be the one pitched AT them. One rank below
        /// best is enough of a margin; the ceiling roll below still reaches the
        /// colony's own rank.
        /// </summary>
        public static int[] CentreOffsetByBestRank => S.contractCentreOffsetByBestRank;

        /// <summary>Added to the centre (the "rank shift" slider). The centre is still clamped at the colony's best.</summary>
        public static int ContractRankShift => S.contractRankShift;

        /// <summary>Colonists within one rank of the best, at or above which the colony counts as a real expedition roster.</summary>
        public static int DeepBenchCount => S.deepBenchCount;

        /// <summary>
        /// Chance the guild offers work ABOVE the centre, up to the colony's own
        /// ceiling, indexed by bench depth: solo champion, ordinary, deep bench.
        ///
        /// This roll is what keeps the top of the ladder reachable. Without it
        /// the offset would put a Nation dungeon permanently out of reach of a
        /// colony with one Nation pawn, which would make the mod's three Nation
        /// dungeons content that exists and is never offered.
        ///
        /// RAISED from 0.10/0.14/0.18. This roll is the only route to work AT
        /// the colony's own rank, so at the old rates a player sat at their
        /// ceiling for a long time before being offered anything that met them
        /// there. Still a minority of the board by design.
        /// </summary>
        public static float CeilingChanceSolo => S.ceilingChanceSolo;
        public static float CeilingChanceNormal => S.ceilingChanceNormal;
        public static float CeilingChanceDeep => S.ceilingChanceDeep;

        /// <summary>Of a ceiling roll, how often it lands EXACTLY on the colony's best rank rather than between.</summary>
        public static float AtCeilingShare => S.atCeilingShare;

        // ── The mix ─────────────────────────────────────────────────────────
        //
        // Most contracts are a gate somebody found before it opened, because
        // that is the version where the player has agency: a countdown they can
        // beat. Breaks and rescues are the consequences, and they are posted
        // directly as well as arising from ignored work, so a colony that never
        // ignores anything still meets them.

        /// <summary>Chance a freshly posted contract is a break that already happened.</summary>
        public static float ChancePostedBroken => S.chancePostedBroken;

        /// <summary>Chance it is a break on inhabited ground. Falls back to a plain break if nobody qualifies.</summary>
        public static float ChancePostedRescue => S.chancePostedRescue;

        /// <summary>
        /// Chance a guild cycle is spent surveying a multi-floor dungeon
        /// instead of posting a gate contract.
        ///
        /// Modest, and it costs nothing when it misses: a survey that cannot
        /// post (nothing left to find, or the planet already holds its limit)
        /// falls back to an ordinary contract in the same cycle rather than
        /// wasting the guild's turn. These are permanent landmarks, so they
        /// only need to arrive a few times in a campaign.
        /// </summary>
        public static float ChancePostedDeepReport => S.chancePostedDeepReport;

        // ── Lifetime ────────────────────────────────────────────────────────

        /// <summary>
        /// Days a posted contract stays on the board before the dungeon behind
        /// it tears itself open. This is NOT a fail timer: when it expires the
        /// site converts into a break contract (waves already loose, harder,
        /// paid better), so ignoring guild work escalates rather than simply
        /// vanishing.
        /// </summary>
        public static float DaysBeforeBreak => S.daysBeforeBreak;

        /// <summary>Days a BROKEN site lingers before the guild writes it off.</summary>
        public static float BrokenSiteDays => S.brokenSiteDays;

        // ── Rewards ─────────────────────────────────────────────────────────
        //
        // The dungeon's own loot is the real payment and is untouched. These
        // are the guild's finder's fee, on top, and exist so accepting a
        // contract is worth more than stumbling on the same dungeon by luck.

        /// <summary>Silver per rank index, paid on completion, before the fee setting.</summary>
        public static int[] SilverByRank => S.contractSilverByRank;

        /// <summary>The finder's fee at a rank with the fee setting applied.</summary>
        public static int FeeFor(int rankIndex) =>
            Mathf.RoundToInt(SilverByRank[ClampRank(rankIndex)] * S.contractFeeMultiplier);

        /// <summary>
        /// The fee a contract of this kind actually pays: broken and rescue
        /// contracts carry the broken-site multiplier. The ONE figure both the
        /// letters and PayFinderFee use, so the quote can never drift from
        /// the payment again.
        /// </summary>
        public static int FeeFor(DungeonQuestKind kind, int rankIndex)
        {
            int amount = FeeFor(rankIndex);
            if (kind != DungeonQuestKind.Located)
                amount = Mathf.RoundToInt(amount * BrokenFeeMultiplier);
            return amount;
        }

        /// <summary>Extra multiplier on the finder's fee for clearing a broken site.</summary>
        public static float BrokenFeeMultiplier => S.brokenFeeMultiplier;

        /// <summary>Goodwill granted to the rescued faction when a break on their land is cleared.</summary>
        public static int RescueGoodwill => S.rescueGoodwill;

        /// <summary>Goodwill lost when a break on a faction's land is left to run its course.</summary>
        public static int RescueFailureGoodwill => S.rescueFailureGoodwill;

        /// <summary>Goodwill with the Adventurers' Guild (GuildFactionAddon) for a completed contract at a rank.</summary>
        public static int GuildGoodwillFor(int rankIndex) => S.contractGuildGoodwillByRank[ClampRank(rankIndex)];

        /// <summary>
        /// GuildFactionAddon's faction, looked up by defName so this mod keeps
        /// no compile-time dependency on the add-on. Null when it is not
        /// installed, which turns the guild payout into a no-op.
        /// </summary>
        public const string GuildFactionDefName = "GuildFaction_AdventurersGuild";

        // ── Safety ──────────────────────────────────────────────────────────

        /// <summary>
        /// Consecutive one-second polls a site's map must be ABSENT before the
        /// site is treated as abandoned.
        ///
        /// Never remove on the first absent poll. HasMap flickers for a beat
        /// while a shuttle, gravship or modded vehicle is landing, and acting
        /// on that flicker is exactly the bug that lost caravans in the
        /// Creatures add-on in July. Six seconds of continuous absence is a
        /// real departure; one poll is a loading frame. NOT a setting: this
        /// guards against losing caravans, and there is no good value to pick
        /// other than this one.
        /// </summary>
        public const int AbandonPollsToFail = 6;

        // ── Broken-site staging ─────────────────────────────────────────────

        /// <summary>
        /// Share of a full dungeon's population that is already loose on the
        /// surface when the player arrives at a broken site.
        ///
        /// Well under half on purpose. What came out is meant to read as the
        /// front of the dungeon rather than all of it, so walking in still
        /// means walking into the rest. It also keeps the surface fight
        /// survivable for a party that arrived expecting a countdown.
        /// </summary>
        public static float BrokenSurfaceBudgetFraction => S.brokenSurfaceShare;

        /// <summary>How far from the torn gate the loose creatures are scattered.</summary>
        public static int BrokenScatterRadius => S.brokenScatterRadius;

        /// <summary>How tight a ring the rescued faction is holding around the gate.</summary>
        public static float DefenderRingRadius => S.defenderRingRadius;

        public static int ClampRank(int i) => i < 0 ? 0 : (i > (int)MobRankTier.Nation ? (int)MobRankTier.Nation : i);
    }
}
