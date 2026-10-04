using IsekaiLeveling.MobRanking;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace IsekaiDungeons
{
    public class CompProperties_UseEffectSummonGate : CompProperties_UseEffect
    {
        /// <summary>Rank of the gate this scroll opens. One ThingDef per rank.</summary>
        public MobRankTier rank = MobRankTier.F;

        /// <summary>How far from the reader the gate may land, in cells.</summary>
        public float searchRadius = 14f;

        public CompProperties_UseEffectSummonGate()
        {
            compClass = typeof(CompUseEffect_SummonGate);
        }
    }

    /// <summary>
    /// A gate summoning scroll: reading it tears a gate of the scroll's rank
    /// open next to the reader.
    ///
    /// The gate is an ordinary one. It follows the same kind selection, break
    /// clock and red-gate rule as a storyteller gate, and is placed with the
    /// same footprint rule (IncidentWorker_DungeonBreak.IsFootprintFree), so
    /// it never lands on a zone, a blueprint or anything the player owns. The
    /// home area IS allowed here: the player chose where to read it.
    ///
    /// The scroll consumes ITSELF, and only after the gate is standing. Vanilla
    /// CompUseEffect_DestroySelf would eat it even when placement fails.
    ///
    /// Refused on any pocket map or dungeon map: no gate inside a gate.
    ///
    /// Refused on anything that is not a player home, too. A gate's dungeon is
    /// a pocket map parented to the map the gate stands on, and vanilla
    /// removes temporary maps (caravan ambushes, camps, most world sites) as
    /// soon as no colonist stands on THEM - it does not count pawns inside
    /// child pocket maps. A party that walked through a gate read on a
    /// battlefield lost the battlefield, and the dungeon with them inside it.
    /// </summary>
    public class CompUseEffect_SummonGate : CompUseEffect
    {
        public CompProperties_UseEffectSummonGate Props => (CompProperties_UseEffectSummonGate)props;

        public override AcceptanceReport CanBeUsedBy(Pawn p)
        {
            Map map = p.MapHeld;
            if (map == null)
                return false;
            if (IsInsideDungeon(map))
                return "IsekaiDungeons.GateScroll.CannotInDungeon".Translate();
            if (!map.IsPlayerHome)
                return "IsekaiDungeons.GateScroll.HomeOnly".Translate();
            if (!TryFindGateSpot(map, parent.PositionHeld, IsekaiDungeonsDefOf.IsekaiDungeons_DungeonGate, null, out _))
                return "IsekaiDungeons.GateScroll.NoRoom".Translate();
            return true;
        }

        /// <summary>A red gate is summoned deliberately, so ask once before it happens.</summary>
        public override TaggedString ConfirmMessage(Pawn p)
        {
            Map map = p.MapHeld;
            if (map == null || !IsRed(map))
                return null;
            return "IsekaiDungeons.GateScroll.ConfirmRed".Translate(
                DungeonRankUtil.ColoredRankString(Props.rank).Named("RANK"));
        }

        public override void DoEffect(Pawn usedBy)
        {
            base.DoEffect(usedBy);

            Map map = usedBy.Map;
            if (map == null || IsInsideDungeon(map) || !map.IsPlayerHome)
                return;

            DungeonKindDef kind = DungeonKindSelector.SelectForRank(Props.rank);
            if (kind == null)
            {
                Messages.Message("IsekaiDungeons.GateScroll.Fizzled".Translate(), usedBy, MessageTypeDefOf.RejectInput, false);
                return;
            }

            if (!TryFindGateSpot(map, usedBy.Position, kind.gateDef, usedBy, out IntVec3 cell))
            {
                Messages.Message("IsekaiDungeons.GateScroll.NoRoom".Translate(), usedBy, MessageTypeDefOf.RejectInput, false);
                return;
            }

            bool red = IsRed(map);
            var gate = (Building_DungeonGate)ThingMaker.MakeThing(kind.gateDef);
            gate.Initialize(kind, Props.rank, red);
            GenSpawn.Spawn(gate, cell, map, Rot4.North, WipeMode.FullRefund);

            TaggedString label = "IsekaiDungeons.Letter.GateSummoned.Label".Translate(
                MobRankUtility.GetRankString(Props.rank).Named("RANK"));
            TaggedString text = "IsekaiDungeons.Letter.GateSummoned.Text".Translate(
                usedBy.Named("PAWN"),
                DungeonRankUtil.ColoredRankString(Props.rank).Named("RANK"),
                DungeonTunables.BreakTimerDays(kind, (int)Props.rank).ToString("0.#").Named("DAYS"));
            if (red)
                text += "\n\n" + "IsekaiDungeons.Letter.RedGate.Suffix".Translate();
            Find.LetterStack.ReceiveLetter(label, text,
                red ? LetterDefOf.ThreatBig : LetterDefOf.NeutralEvent, gate);

            // Consumed last, and only once the gate stands.
            parent.SplitOff(1).Destroy();
        }

        private bool IsRed(Map map) => Props.rank > RaidRankSystem.GetColonyBestRank(map);

        internal static bool IsInsideDungeon(Map map) =>
            map.IsPocketMap || map.Parent is DungeonMapParent;

        /// <summary>
        /// Nearest gate root to <paramref name="near"/>, within the search
        /// radius, whose footprint is free by the storyteller gate's rule,
        /// holds no pawn (an impassable gate must not spawn on anyone), and,
        /// when a reader is given, is reachable by them.
        /// </summary>
        private bool TryFindGateSpot(Map map, IntVec3 near, ThingDef gateDef, Pawn reader, out IntVec3 cell)
        {
            IntVec2 size = gateDef.size;
            int count = GenRadial.NumCellsInRadius(Mathf.Min(Props.searchRadius, GenRadial.MaxRadialPatternRadius - 1f));
            for (int i = 0; i < count; i++)
            {
                IntVec3 c = near + GenRadial.RadialPattern[i];
                if (!c.InBounds(map) || c.DistanceToEdge(map) <= size.x + 1)
                    continue;
                if (!IncidentWorker_DungeonBreak.IsFootprintFree(map, c, size, allowHomeArea: true))
                    continue;
                if (AnyPawnIn(map, GenAdj.OccupiedRect(c, Rot4.North, size)))
                    continue;
                if (reader != null && !reader.CanReach(c, PathEndMode.Touch, Danger.Deadly))
                    continue;
                cell = c;
                return true;
            }
            cell = IntVec3.Invalid;
            return false;
        }

        private static bool AnyPawnIn(Map map, CellRect rect)
        {
            foreach (IntVec3 c in rect)
            {
                if (c.GetFirstPawn(map) != null)
                    return true;
            }
            return false;
        }
    }
}
