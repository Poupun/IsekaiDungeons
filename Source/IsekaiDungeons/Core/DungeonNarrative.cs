using System.Text;
using IsekaiLeveling.MobRanking;
using UnityEngine;
using RimWorld;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Builds the words the player actually reads when they step through a gate.
    ///
    /// The entry letter used to be one fixed paragraph that described "carved
    /// halls of dead stone" — which was written when every dungeon was a cave
    /// and became a lie the moment surface dungeons existed. Telling a player
    /// they are in a stone corridor while they stand on an open snowfield is
    /// worse than saying nothing.
    ///
    /// So the letter is assembled from four parts, each answering a question the
    /// player actually has on arrival:
    ///   1. WHERE am I        — from the environment (DungeonEnvironmentExtension)
    ///   2. HOW BIG is it     — from the room/zone count
    ///   3. HOW BAD is it     — the gate's rank against the colony's own strength
    ///   4. WHAT do I do      — objective and the countdown
    ///
    /// Part 3 is the one that carries the tension: the same S-rank gate should
    /// read as a reasonable job to an S-rank colony and as a suicide note to a
    /// C-rank one, and the letter is where that lands.
    /// </summary>
    public static class DungeonNarrative
    {
        /// <summary>How far above the colony's own rank a gate has to be before it reads as dangerous.</summary>
        private const int OverMatchedBy = 1;

        // ── Entry letter ────────────────────────────────────────────────────

        public static TaggedString EntryLetterLabel(DungeonKindDef kind, MobRankTier rank)
        {
            return "IsekaiDungeons.Letter.Entered.Label".Translate(
                MobRankUtility.GetRankString(rank).Named("RANK"),
                (kind?.label ?? "dungeon").Named("DUNGEON"));
        }

        /// <summary>
        /// The composed arrival letter. Every part degrades independently: a
        /// missing environment key falls back to the layout mode, a missing
        /// component just drops its paragraph. The letter can never come out
        /// empty or half-translated.
        /// </summary>
        public static TaggedString EntryLetterText(DungeonMapComponent dungeon, Building_DungeonGate gate,
                                                   DungeonKindDef kind, MobRankTier rank, int ticksUntilBreak)
        {
            var sb = new StringBuilder();
            string coloredRank = DungeonRankUtil.ColoredRankString(rank);

            // 1. WHERE — the environment speaks for itself.
            sb.Append(FlavorLine(kind).Translate(coloredRank.Named("RANK")));

            // 2. HOW BIG — only when we actually know the layout (the letter can
            //    fire before generation in odd cases).
            int rooms = CountEncounters(dungeon);
            if (rooms > 0)
            {
                sb.Append("\n\n");
                sb.Append("IsekaiDungeons.Narrative.Scale".Translate(
                    rooms.Named("COUNT"),
                    RoomNoun(kind, rooms).Named("ROOMS")));
            }

            // 3. HOW BAD — measured against this colony, not in the abstract.
            sb.Append("\n\n");
            sb.Append(DangerLine(gate, rank).Translate(coloredRank.Named("RANK")));

            // 4. WHAT NOW — objective, then the clock.
            string objective = kind?.ObjectiveWorker?.ObjectiveDescription(kind, rank);
            sb.Append("\n\n");
            sb.Append(objective.NullOrEmpty()
                ? "IsekaiDungeons.Narrative.Objective.Boss".Translate()
                : (TaggedString)objective);

            if (ticksUntilBreak > 0)
            {
                sb.Append(" ");
                sb.Append("IsekaiDungeons.Narrative.Countdown".Translate(
                    ticksUntilBreak.ToStringTicksToPeriod().Named("TIME")));
            }

            return sb.ToString();
        }

        // ── Title card ──────────────────────────────────────────────────────

        /// <summary>Hero line: the dungeon's own name, set as a title rather than a sentence.</summary>
        public static string TitleName(DungeonKindDef kind) =>
            GenText.CapitalizeAsTitle(kind?.label ?? "dungeon");

        /// <summary>
        /// Hero line for a tower floor: "Floor 24 - Village".
        ///
        /// The name is already composed by TowerLadder.NameFor, which prefers
        /// the archetype's own word ("village", "gauntlet", "the summit") and
        /// falls back to the borrowed dungeon's label — so a plain crawl reads
        /// as the place it actually is rather than as a category.
        /// </summary>
        public static string TitleFloorName(TowerFloorMapParent parent)
        {
            if (parent == null)
                return string.Empty;

            // RecordIfSeen, not EnsureRecord: this runs on every map-focus edge
            // and must never write to the save. The record is guaranteed to
            // exist anyway — TowerFloors.Open materialises it before generating.
            TowerFloorRecord rec = TowerComponent.Get?.RecordIfSeen(parent.floor);
            string name = rec?.Label;
            if (name.NullOrEmpty())
            {
                name = "IsekaiDungeons.Tower.FloorName".Translate(
                    parent.floor.Named("FLOOR"),
                    (parent.kindDef?.label ?? "floor").CapitalizeFirst().Named("WHAT"));
            }
            return GenText.CapitalizeAsTitle(name);
        }

        /// <summary>The rank, as it reads on the ribbon: "C Rank", "SSS Rank", "Nation Rank".</summary>
        public static string TitleRankLine(MobRankTier rank) =>
            "IsekaiDungeons.Title.RankLine".Translate(
                MobRankUtility.GetRankString(rank).Named("RANK"));

        /// <summary>
        /// The line under the name. This is the dungeon's OWN first sentence,
        /// not a generated one — every dungeon already opens its description
        /// with a single image of the place ("The heat hits before the light
        /// does."), which is exactly what this slot wants and is already
        /// translated wherever the def is. A dungeon with no description at all
        /// (a third-party one, most likely) falls back to its environment's
        /// subtitle so the card still says something true about where the
        /// player is standing.
        /// </summary>
        public static string TitleTagline(DungeonKindDef kind)
        {
            string first = FirstClause(kind?.description);
            if (!first.NullOrEmpty())
                return first;

            DungeonEnvironmentExtension ext = EnvironmentOf(kind);
            if (ext != null && !ext.titleSubtitleKey.NullOrEmpty())
                return ext.titleSubtitleKey.Translate();
            return string.Empty;
        }

        /// <summary>
        /// First clause of a description, with all whitespace flattened to
        /// single spaces (XML descriptions may be written across several lines,
        /// and the card lays out its own line breaks).
        ///
        /// A SEMICOLON COUNTS AS AN END, which is not pedantry: the longest
        /// first sentences in the catalogue are two images joined by one, and
        /// stopping at the join turns a 194-character paragraph into a
        /// 68-character card line without losing the picture. Everything up to
        /// the first ., !, ? or ; is kept.
        ///
        /// An ellipsis is not an end: a terminator only counts when what
        /// follows it is whitespace or nothing.
        /// </summary>
        private static string FirstClause(string text)
        {
            if (text.NullOrEmpty())
                return string.Empty;

            var sb = new StringBuilder(text.Length);
            bool lastWasSpace = true;   // also trims the leading whitespace
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c))
                {
                    if (!lastWasSpace)
                        sb.Append(' ');
                    lastWasSpace = true;
                    continue;
                }

                bool terminator = c == '.' || c == '!' || c == '?' || c == ';';
                lastWasSpace = false;

                if (terminator && (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1])))
                {
                    // A semicolon is a join, not a stop — the clause reads as a
                    // sentence only if it is punctuated as one.
                    sb.Append(c == ';' ? '.' : c);
                    break;
                }

                sb.Append(c);
            }
            return sb.ToString().TrimEnd();
        }

        // ── Pieces ──────────────────────────────────────────────────────────

        private static DungeonEnvironmentExtension EnvironmentOf(DungeonKindDef kind) =>
            kind?.mapGenerator?.GetModExtension<DungeonEnvironmentExtension>();

        /// <summary>
        /// Environment description. Prefers the generator's own key so a modded
        /// environment narrates itself; falls back to a layout-mode generic so
        /// an environment with no extension still reads correctly rather than
        /// claiming to be a cave.
        /// </summary>
        private static string FlavorLine(DungeonKindDef kind)
        {
            DungeonEnvironmentExtension ext = EnvironmentOf(kind);
            if (ext != null && !ext.entryFlavorKey.NullOrEmpty())
                return ext.entryFlavorKey;

            switch (kind?.layoutMode ?? DungeonLayoutMode.Carve)
            {
                case DungeonLayoutMode.Open:
                    return "IsekaiDungeons.Narrative.Where.Open";
                case DungeonLayoutMode.Build:
                    return "IsekaiDungeons.Narrative.Where.Built";
                default:
                    return "IsekaiDungeons.Narrative.Where.Carve";
            }
        }

        private static string RoomNoun(DungeonKindDef kind, int count)
        {
            DungeonEnvironmentExtension ext = EnvironmentOf(kind);
            if (ext != null && !ext.roomNounKey.NullOrEmpty())
                return ext.roomNounKey.Translate();

            return (kind?.layoutMode == DungeonLayoutMode.Open
                ? "IsekaiDungeons.Narrative.Noun.Zones"
                : "IsekaiDungeons.Narrative.Noun.Rooms").Translate();
        }

        /// <summary>
        /// Danger relative to the COLONY, which is the only comparison that
        /// means anything to the player. A red gate says so outright; otherwise
        /// the gap between the gate's rank and the colony's best fighter picks
        /// the line.
        /// </summary>
        private static string DangerLine(Building_DungeonGate gate, MobRankTier rank)
        {
            if (gate != null && gate.isRedGate)
                return "IsekaiDungeons.Narrative.Danger.Red";

            MobRankTier colony = MobRankTier.F;
            try
            {
                Map colonyMap = gate?.Map ?? Find.AnyPlayerHomeMap;
                if (colonyMap != null)
                    colony = RaidRankSystem.GetColonyBestRank(colonyMap);
            }
            catch
            {
                // Never let a narrative flourish break the arrival letter.
            }

            int gap = (int)rank - (int)colony;
            if (gap >= OverMatchedBy)
                return "IsekaiDungeons.Narrative.Danger.Above";
            if (gap <= -2)
                return "IsekaiDungeons.Narrative.Danger.Beneath";
            return "IsekaiDungeons.Narrative.Danger.Even";
        }

        /// <summary>Combat rooms/zones plus the boss room — what the player will actually walk through.</summary>
        private static int CountEncounters(DungeonMapComponent dungeon)
        {
            if (dungeon?.rooms == null)
                return 0;
            int n = 0;
            for (int i = 0; i < dungeon.rooms.Count; i++)
            {
                DungeonRoom room = dungeon.rooms[i];
                // Only the critical path: the arrival letter promises how many
                // fights lie between here and the far end, and a wing is one the
                // party can walk straight past.
                if (!room.onCriticalPath)
                    continue;
                if (room.role == DungeonRoomRole.Combat || room.role == DungeonRoomRole.Boss)
                    n++;
            }
            return n;
        }
    }
}
