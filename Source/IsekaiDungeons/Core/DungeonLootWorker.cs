using System.Collections.Generic;
using IsekaiLeveling.MobRanking;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// WHAT THIS DUNGEON PAYS OUT.
    ///
    /// The built-in generator is deliberately Isekai-flavoured: it hardcodes the
    /// base mod's mana cores, star fragments and runes, because that is the
    /// economy this add-on plugs into. That is correct for our dungeons and
    /// wrong for anyone else's — a mechanoid foundry should be able to drop
    /// mech parts, and a mod with no Isekai items at all should be able to pay
    /// in its own currency.
    ///
    /// Selected per archetype via <see cref="DungeonKindDef.lootWorkerClass"/>.
    /// The default simply delegates to <see cref="DungeonLootGenerator"/>, so
    /// existing dungeons are unaffected and the field is purely additive.
    ///
    /// Stateless and cached on the def, like the other workers.
    /// </summary>
    public class DungeonLootWorker
    {
        /// <summary>
        /// The hoard placed in the boss room on a clear. Return an empty list
        /// for a dungeon that pays nothing (XP-only content is legitimate).
        /// </summary>
        public virtual List<Thing> GenerateClearLoot(MobRankTier rank, DungeonKindDef kind)
        {
            return DungeonLootGenerator.GenerateClearLoot(rank, kind);
        }

        /// <summary>Contents of an optional treasure zone/room.</summary>
        public virtual List<Thing> GenerateTreasureLoot(MobRankTier rank, DungeonKindDef kind)
        {
            return DungeonLootGenerator.GenerateTreasureLoot(rank, kind);
        }

        /// <summary>
        /// XP awarded IN FULL to every colonist inside at clear time (no
        /// headcount split — house convention). Override to decouple a
        /// dungeon's XP from the base mod's quest curve.
        /// </summary>
        public virtual int ClearXpPerColonist(MobRankTier rank, DungeonKindDef kind)
        {
            return DungeonTunables.ClearXpForRank(rank);
        }
    }
}
