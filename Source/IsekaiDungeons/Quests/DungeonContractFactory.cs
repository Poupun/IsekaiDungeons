using System.Collections.Generic;
using IsekaiLeveling;
using IsekaiLeveling.MobRanking;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Builds a guild dungeon contract: picks the rank, finds a tile at a
    /// distance that rank has earned, plants the site, and posts a real
    /// RimWorld quest with the guild letter attached.
    ///
    /// The quest is hand-built with Quest.MakeRaw rather than generated from a
    /// QuestScriptDef graph. That is the house pattern, established by the base
    /// mod's hunt and world boss families and followed by the Creatures add-on:
    /// the quest is a real Quest object so it appears in the Quests tab with
    /// native state and reward icons, but its content is decided in C# where
    /// rank, distance and roster can all be read from the mod's own systems.
    /// The XML QuestNode route cannot express any of that, and the vanilla
    /// site-generating utility node does not even forward a custom
    /// WorldObjectDef, so it could not place this site if we wanted it to.
    /// </summary>
    public static class DungeonContractFactory
    {
        /// <summary>
        /// Posts a contract for the given colony. Returns the quest, or null if
        /// no legal tile or dungeon could be found (which is normal on a
        /// crowded or tiny world and must never throw).
        /// </summary>
        public static Quest TryPost(Map colonyMap, DungeonQuestKind kind)
        {
            if (colonyMap == null)
                return null;

            MobRankTier rank = RollContractRank(colonyMap);
            DungeonKindDef dungeon = DungeonKindSelector.SelectForRank(rank);
            if (dungeon == null)
            {
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} No dungeon kind available at rank {rank}; contract skipped.");
                return null;
            }

            if (!TryFindContractTile(colonyMap.Tile, rank, out PlanetTile tile))
                return null;

            Faction rescueFaction = kind == DungeonQuestKind.FactionRescue ? PickRescueFaction() : null;
            if (kind == DungeonQuestKind.FactionRescue && rescueFaction == null)
                kind = DungeonQuestKind.Broken;   // nobody to rescue; stage it as a plain break

            Site site = SiteMaker.MakeSite(
                IsekaiDungeonsDefOf.IsekaiDungeons_DungeonContractPart,
                tile,
                rescueFaction,
                ifHostileThenMustRemainHostile: false,
                threatPoints: null,
                worldObjectDef: IsekaiDungeonsDefOf.IsekaiDungeons_DungeonContractSite);
            if (site == null)
                return null;

            WorldObjectComp_DungeonContract comp = site.GetComponent<WorldObjectComp_DungeonContract>();
            if (comp == null)
            {
                Log.Error($"{IsekaiDungeonsMod.LogPrefix} Contract site def is missing WorldObjectComp_DungeonContract; "
                        + "the site would generate empty. Aborting the contract.");
                return null;
            }
            comp.rank = rank;
            comp.kindDef = dungeon;
            comp.questKind = kind;
            comp.rescueFaction = rescueFaction;
            comp.postedTick = Find.TickManager.TicksGame;

            Find.WorldObjects.Add(site);

            Quest quest = Quest.MakeRaw();

            // ROOT IS NOT OPTIONAL. Quest.CleanupQuestParts dereferences
            // root.hideOnCleanup with no null check, so ending a rootless quest
            // throws inside Quest.End; and the quest manager discards rootless
            // quests on load, so a save would quietly lose every contract. The
            // script itself is deliberately empty; see its def comment.
            quest.root = IsekaiDungeonsDefOf.IsekaiDungeons_ContractQuestScript;

            // A contract is information, not an offer to haggle over: the guild
            // is telling the player a gate exists. Left un-accepted it would sit
            // in the Quests tab as a permanent unanswered offer that can never
            // expire, because MakeRaw leaves acceptanceExpireTick at -1.
            quest.SetInitiallyAccepted();

            quest.name = DungeonContractNarrative.Title(kind, rank, dungeon);
            quest.description = DungeonContractNarrative.Description(kind, rank, dungeon, site, colonyMap);

            var part = new QuestPart_DungeonContract
            {
                site = site,
                rank = rank,
                kind = kind,
                rescueFaction = rescueFaction,
                quest = quest,
                postedTick = Find.TickManager.TicksGame,
            };
            quest.AddPart(part);
            Find.QuestManager.Add(quest);

            DungeonContractNarrative.SendLetter(kind, rank, dungeon, site, quest);
            return quest;
        }

        /// <summary>
        /// Contract rank, measured against the PARTY rather than the champion.
        ///
        /// A dungeon is a multi-room crawl, not a duel, so a rank-X dungeon is
        /// not a fair fight for a single rank-X pawn. The old roll centred on
        /// the colony's best rank and went one above it 18% of the time, which
        /// meant a colony with one S-rank colonist was routinely sent at SS
        /// dungeons. Three things changed:
        ///
        ///  - The centre sits BELOW the best pawn, by a margin that widens from
        ///    S upward, because dungeon content grows faster per tier than a
        ///    party's ability to absorb it.
        ///  - Bench depth moves the centre. GetColonyBestRank reads one pawn,
        ///    so a lone champion and a full expedition roster were previously
        ///    indistinguishable; counting how many colonists are within a rank
        ///    of the best is what separates them.
        ///  - Nothing is ever offered above the colony's own ceiling.
        ///
        /// A separate ceiling roll can still reach that ceiling, and it exists
        /// so the top of the ladder stays reachable: a colony with one Nation
        /// pawn should see Nation work, or the mod's Nation dungeons would be
        /// content that ships and is never offered. Rarely for a lone champion
        /// (about one contract in sixteen), often for a Nation roster.
        ///
        /// A NOTE FOR ANYONE RETUNING THIS. The stat, damage and health tables
        /// in MobRank are NOT a guide to how strong a player's colonists are:
        /// MobRankComponent is attached only to non-player pawns and returns
        /// early on Faction.OfPlayer. Player power comes from the RPG systems
        /// (levels, stats, forge refinement, runes) on a different curve
        /// entirely, so tune this against play, not against those tables.
        /// </summary>
        public static MobRankTier RollContractRank(Map colonyMap)
        {
            int best = DungeonQuestTuning.ClampRank((int)RaidRankSystem.GetColonyBestRank(colonyMap));
            int depth = BenchDepth(colonyMap, best);

            int centre = best - DungeonQuestTuning.CentreOffsetByBestRank[best] + DungeonQuestTuning.ContractRankShift;
            if (depth <= 1)
                centre -= 1;                                   // one pawn is not a party
            else if (depth >= DungeonQuestTuning.DeepBenchCount)
                centre += 1;                                   // a real expedition roster
            centre = Mathf.Clamp(centre, 0, best);             // never above the best pawn, whatever the shift

            // Ceiling roll: the rare offer of work above the centre, up to (and
            // usually at) the colony's own ceiling.
            float ceilingChance = depth <= 1
                ? DungeonQuestTuning.CeilingChanceSolo
                : (depth >= DungeonQuestTuning.DeepBenchCount
                    ? DungeonQuestTuning.CeilingChanceDeep
                    : DungeonQuestTuning.CeilingChanceNormal);

            if (best > centre && Rand.Chance(ceilingChance))
            {
                if (best == centre + 1 || Rand.Chance(DungeonQuestTuning.AtCeilingShare))
                    return (MobRankTier)best;
                return (MobRankTier)Rand.RangeInclusive(centre + 1, best - 1);
            }

            // Otherwise cluster on the centre, trailing off downward so the
            // board is never all one number.
            float r = Rand.Value;
            int pick = r < 0.50f ? centre : (r < 0.82f ? centre - 1 : centre - 2);
            return (MobRankTier)Mathf.Clamp(pick, 0, best);
        }

        /// <summary>
        /// How many free colonists are within one rank of the best.
        ///
        /// The point is to tell a lone champion apart from an expedition. Rank
        /// comes from the base mod's IsekaiComponent, the same source
        /// GetColonyBestRank reads, so the two can never disagree about what a
        /// colonist is worth.
        /// </summary>
        private static int BenchDepth(Map colonyMap, int best)
        {
            if (colonyMap == null)
                return 1;

            int n = 0;
            List<Pawn> colonists = colonyMap.mapPawns.FreeColonistsSpawned;
            for (int i = 0; i < colonists.Count; i++)
            {
                IsekaiComponent comp = IsekaiComponent.GetCached(colonists[i]);
                if (comp == null)
                    continue;
                if ((int)comp.GetRank() >= best - 1)
                    n++;
            }
            return Mathf.Max(1, n);
        }

        /// <summary>
        /// A tile the rank has earned the trip to. Low ranks are sent next door
        /// because a low-rank party has no travel capacity and no reason to
        /// spend a quadrum walking; Nation work is genuinely far away, and being
        /// sent to the edge of the map is part of what makes it read as the
        /// guild's last resort.
        /// </summary>
        public static bool TryFindContractTile(PlanetTile near, MobRankTier rank, out PlanetTile tile)
        {
            int i = DungeonQuestTuning.ClampRank((int)rank);
            int min = DungeonQuestTuning.MinTilesFor(i);
            int max = DungeonQuestTuning.MaxTilesFor(i);

            if (TileFinder.TryFindNewSiteTile(out tile, near, min, max, allowCaravans: false))
                return true;

            // Widen once before giving up: a coastal or island colony can have
            // very few legal tiles in a tight band, and silently posting no
            // contracts at all would look like the feature is broken.
            if (TileFinder.TryFindNewSiteTile(out tile, near, 1, Mathf.Max(max + 4, 8), allowCaravans: false))
                return true;

            tile = PlanetTile.Invalid;
            return false;
        }

        /// <summary>
        /// Somebody worth saving: a non-player, non-hostile, humanlike faction
        /// that actually has settlements. Hostiles are excluded because a
        /// rescue contract for people currently shooting at the colony is not a
        /// story the guild would post.
        /// </summary>
        private static Faction PickRescueFaction()
        {
            var candidates = new List<Faction>();
            List<Faction> all = Find.FactionManager.AllFactionsListForReading;
            for (int i = 0; i < all.Count; i++)
            {
                Faction f = all[i];
                if (f == null || f.IsPlayer || f.defeated || f.temporary)
                    continue;
                if (f.def == null || !f.def.humanlikeFaction || f.def.hidden)
                    continue;
                if (f.HostileTo(Faction.OfPlayer))
                    continue;
                candidates.Add(f);
            }
            return candidates.Count == 0 ? null : candidates.RandomElement();
        }
    }
}
