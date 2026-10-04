using IsekaiLeveling.MobRanking;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// The guild's voice for dungeon work.
    ///
    /// Deliberately the same letterhead as the base mod's hunt and bounty
    /// contracts, because in the fiction it is the same guild. A player who has
    /// been reading gold-ruled ADVENTURER'S GUILD letters for a hundred hours
    /// should recognise this as more of the same institution, not as a second
    /// mod introducing itself.
    ///
    /// Text lives in Keyed XML with NAMED placeholders, which is this mod's
    /// convention rather than the base mod's positional one. Named arguments
    /// survive translators reordering a sentence; positional ones silently
    /// swap the rank and the distance when somebody moves a clause.
    /// </summary>
    public static class DungeonContractNarrative
    {
        private static string Key(DungeonQuestKind kind, string suffix)
        {
            switch (kind)
            {
                case DungeonQuestKind.Broken: return "IsekaiDungeons.Contract.Broken." + suffix;
                case DungeonQuestKind.FactionRescue: return "IsekaiDungeons.Contract.Rescue." + suffix;
                default: return "IsekaiDungeons.Contract.Located." + suffix;
            }
        }

        public static string Title(DungeonQuestKind kind, MobRankTier rank, DungeonKindDef dungeon)
        {
            return Key(kind, "Title").Translate(
                MobRankUtility.GetRankString(rank).Named("RANK"),
                (dungeon?.label ?? "dungeon").Named("DUNGEON"));
        }

        public static string Description(DungeonQuestKind kind, MobRankTier rank, DungeonKindDef dungeon,
                                         Site site, Map colonyMap)
        {
            int tiles = DistanceTiles(colonyMap, site);
            return Key(kind, "Desc").Translate(
                DungeonRankUtil.ColoredRankString(rank).Named("RANK"),
                (dungeon?.label ?? "dungeon").Named("DUNGEON"),
                (dungeon?.description ?? "").Named("FLAVOUR"),
                tiles.Named("TILES"),
                (site?.Faction?.Name ?? "").Named("FACTION"),
                DungeonQuestTuning.FeeFor(kind, (int)rank).ToString("N0").Named("SILVER"));
        }

        public static void SendLetter(DungeonQuestKind kind, MobRankTier rank, DungeonKindDef dungeon,
                                      Site site, Quest quest)
        {
            LetterDef def = kind == DungeonQuestKind.Located
                ? LetterDefOf.PositiveEvent
                : LetterDefOf.ThreatBig;

            Find.LetterStack.ReceiveLetter(
                Key(kind, "Letter.Label").Translate(
                    MobRankUtility.GetRankString(rank).Named("RANK"),
                    (site?.Faction?.Name ?? "").Named("FACTION")),
                Key(kind, "Letter.Text").Translate(
                    DungeonRankUtil.ColoredRankString(rank).Named("RANK"),
                    (dungeon?.label ?? "dungeon").Named("DUNGEON"),
                    (site?.Faction?.Name ?? "").Named("FACTION"),
                    DungeonQuestTuning.FeeFor(kind, (int)rank).ToString("N0").Named("SILVER")),
                def,
                site != null ? new LookTargets(site) : LookTargets.Invalid,
                relatedFaction: null,
                quest: quest);
        }

        /// <summary>
        /// The break letter, sent when a located contract nobody took runs its
        /// clock out. It is the same site, so the player who ignored the first
        /// letter should be able to feel the connection to this one.
        /// </summary>
        public static void SendBreakConversionLetter(MobRankTier rank, DungeonKindDef dungeon, Site site, Quest quest)
        {
            Find.LetterStack.ReceiveLetter(
                "IsekaiDungeons.Contract.Converted.Letter.Label".Translate(
                    MobRankUtility.GetRankString(rank).Named("RANK")),
                "IsekaiDungeons.Contract.Converted.Letter.Text".Translate(
                    DungeonRankUtil.ColoredRankString(rank).Named("RANK"),
                    (dungeon?.label ?? "dungeon").Named("DUNGEON")),
                LetterDefOf.ThreatBig,
                site != null ? new LookTargets(site) : LookTargets.Invalid,
                relatedFaction: null,
                quest: quest);
        }

        private static int DistanceTiles(Map colonyMap, Site site)
        {
            if (colonyMap == null || site == null)
                return 0;
            return Find.WorldGrid.TraversalDistanceBetween(colonyMap.Tile, site.Tile, passImpassable: true, maxDist: 60);
        }
    }
}
