using System;
using HarmonyLib;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// A god whose brain is gone dies at the state check, before vanilla can
    /// act on the missing brain.
    ///
    /// THE CRASH THIS PREVENTS ("Tried to restore null body part", then an NRE
    /// in Hediff_MissingPart.PostAdd). When a non-player pawn goes down,
    /// Pawn_HealthTracker.CheckForStateChange rolls death-on-downed. For a pawn
    /// with preventsDeath it cannot Kill, so it removes the BRAIN instead:
    /// AddHediff(MissingBodyPart, GetBrain()). That AddHediff calls
    /// CheckForStateChange again, and nothing has changed from its point of
    /// view: ShouldBeDead still answers no (preventsDeath comes first),
    /// Downed is still false (MakeDowned has not run yet), ShouldBeDowned
    /// still answers yes. It rolls again, GetBrain() now returns null, and a
    /// missing part is added with a null Part.
    ///
    /// HediffComp_GodhoodBody's brain watch could not catch this: it runs on
    /// Notify_PawnPostApplyDamage, which comes after the damage finishes, and
    /// the crash happens inside it. This runs at the re-entry point, which is
    /// also where vanilla itself calls Kill for an ordinary death.
    ///
    /// Scope: pawns carrying IsekaiDungeons_GodhoodBody, gated behind
    /// HasPreventsDeath so every other pawn pays one bool read.
    /// </summary>
    [HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.CheckForStateChange))]
    public static class Pawn_HealthTracker_CheckForStateChange_GodhoodBrainPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Pawn_HealthTracker __instance, Pawn ___pawn, DamageInfo? dinfo, Hediff hediff)
        {
            try
            {
                if (___pawn == null || ___pawn.Destroyed || __instance.Dead || __instance.isBeingKilled)
                    return true;
                HediffSet set = __instance.hediffSet;
                if (set == null || !set.HasPreventsDeath || set.GetBrain() != null)
                    return true;
                Hediff body = set.GetFirstHediffOfDef(IsekaiDungeonsDefOf.IsekaiDungeons_GodhoodBody);
                if (body == null)
                    return true;

                ___pawn.Kill(dinfo, hediff ?? body);
                return false;
            }
            catch (Exception ex)
            {
                Log.ErrorOnce($"{IsekaiDungeonsMod.LogPrefix} Godhood brain-death check failed: {ex}", 0x60D81);
                return true;
            }
        }
    }
}
