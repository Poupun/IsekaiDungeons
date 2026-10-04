using IsekaiLeveling.MobRanking;
using RimWorld;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// WHAT "CLEARING" THIS DUNGEON MEANS.
    ///
    /// The framework used to have exactly one win condition, hardcoded and
    /// unreachable: kill the boss. Every alternative a dungeon designer might
    /// want — survive a siege, reach a point, wipe every zone, hold ground for
    /// N days — was impossible without editing the mod. This is the seam that
    /// makes objectives pluggable, chosen per archetype via
    /// <see cref="DungeonKindDef.objectiveWorkerClass"/>.
    ///
    /// Workers are stateless and cached on the def. Anything that must survive a
    /// save belongs on DungeonMapComponent / the gate / the map parent, all of
    /// which are properly scribed — never on the worker.
    ///
    /// Contract: <see cref="IsComplete"/> is polled on the map component's
    /// interval (default every 60 ticks) and must be cheap and side-effect free.
    /// Do the side effects in <see cref="OnCompleted"/>, which fires exactly once.
    /// </summary>
    public class DungeonObjectiveWorker
    {
        /// <summary>
        /// Default objective: the boss is defeated. "Defeated" deliberately
        /// includes removed-from-play (!Spawned), because a boss sealed into a
        /// Sealing Cube is beaten as far as the player is concerned — the same
        /// convention the Creatures add-on uses for its quest targets.
        ///
        /// But NOT every despawn is a removal. A leap (jump packs, VPE leaps,
        /// anything built on PawnFlyer) despawns the pawn for the whole arc, and
        /// paying the clear mid-leap left a live boss in a collapsing dungeon -
        /// the bug the base mod's title duels already hit. A flying boss is
        /// still in the fight, and any other absence must persist for two
        /// consecutive checks (DungeonMapComponent.BossAbsentChecks), which
        /// forgives a teleport that holds the pawn nowhere for a few ticks.
        /// </summary>
        public virtual bool IsComplete(DungeonMapComponent dungeon)
        {
            Pawn boss = dungeon?.boss;
            if (boss == null)
                return false;
            if (boss.Dead || boss.Destroyed)
                return true;
            if (boss.Spawned || boss.ParentHolder is PawnFlyer)
                return false;
            return dungeon.BossAbsentChecks >= 2;
        }

        /// <summary>
        /// Fires once, immediately before rewards are granted. Override to add
        /// archetype-specific consequences (a collapse, a world event, a
        /// follow-up gate). Rewards themselves are the loot worker's job.
        /// </summary>
        public virtual void OnCompleted(DungeonMapComponent dungeon)
        {
        }

        /// <summary>
        /// Player-facing one-liner for the gate's inspect pane, so a custom
        /// objective can tell the player what it actually wants. Null keeps the
        /// default wording.
        /// </summary>
        public virtual string ObjectiveDescription(DungeonKindDef kind, MobRankTier rank) => null;
    }
}
