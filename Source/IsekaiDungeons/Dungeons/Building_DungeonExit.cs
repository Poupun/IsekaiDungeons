using System;
using IsekaiLeveling.MobRanking;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// The return portal spawned in the dungeon's entry room.
    ///
    /// Vanilla PocketMapExit already does everything the v1 design needs:
    /// during map generation its SpawnSetup wires itself to
    /// PocketMapUtility.currentlyGeneratingPortal (our gate) and back-links
    /// gate.exit; entering it returns pawns to the gate's position; the
    /// "view other side" gizmo comes free. Fleeing a dungeon is therefore
    /// always possible by design — you keep what you carry.
    ///
    /// The subclass exists as the seam for the roadmap's
    /// sealExitUntilCleared "red gate hard mode": that feature will override
    /// IsEnterable here and nothing else has to change.
    /// </summary>
    public class Building_DungeonExit : PocketMapExit
    {
        /// <summary>Smaller than the gate on purpose: a door, not the hole.</summary>
        private const float ExitDrawSize = 3.4f;

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            // Generated with the map counts as opening: the player is watching
            // the place resolve around them as they arrive.
            if (!respawningAfterLoad)
                spawnRealTime = Tween.Now;
        }

        // ── Appearance ──────────────────────────────────────────────────────

        /// <summary>Arc animation state. Runtime only — a flicker is not worth saving.</summary>
        private GateArcState arcState = GateArcState.Fresh;

        /// <summary>
        /// Realtime stamp of the moment this appeared, or -1 for "was already
        /// here". NOT scribed on purpose: a gate restored from a save has
        /// always existed as far as the player is concerned, and replaying the
        /// opening on every load would be a lie about what just happened.
        /// </summary>
        private float spawnRealTime = -1f;


        /// <summary>
        /// The way out is the same kind of thing as the way in.
        ///
        /// It draws the same animated portal as the gate, coloured by the rank
        /// of the place it leads out of, just smaller — a door rather than the
        /// hole that made the door. base.DrawAt runs only when the frames are
        /// missing, so a partial install falls back to the static texture
        /// instead of drawing nothing.
        /// </summary>
        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            bool animated = false;
            try
            {
                MobRankTier rank = PortalDrawer.RankOf(Map);
                animated = PortalDrawer.Draw(this, drawLoc, rank, ExitDrawSize, ref arcState,
                                             age: PortalDrawer.AgeOf(spawnRealTime));
            }
            catch (Exception ex)
            {
                Log.ErrorOnce($"{IsekaiDungeonsMod.LogPrefix} Exit visuals failed: {ex}", 0x60D44);
            }

            if (!animated)
                base.DrawAt(drawLoc, flip);
        }
    }
}
