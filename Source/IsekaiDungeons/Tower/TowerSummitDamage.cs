using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Who actually fought the thing at the top.
    ///
    /// Godhood is not handed to whoever happens to be holding an item when the
    /// dust settles — it goes to whoever put the most damage into the last boss
    /// in the tower. That makes the reward a RECORD of the fight rather than a
    /// prize the player allocates afterwards, and it means the person who
    /// earned it is the person who gets it even if they were the one who nearly
    /// died doing it.
    ///
    /// Tracking is deliberately narrow: it only accumulates while a summit
    /// floor is live and only for damage landing on that floor's boss. Every
    /// other fight in the game is untouched, so this costs one reference
    /// comparison on the damage path and nothing else.
    /// </summary>
    public class TowerSummitDamage : IExposable
    {
        private List<Pawn> contenders = new List<Pawn>();
        private List<float> damage = new List<float>();

        public bool Any => contenders.Count > 0;

        public void Add(Pawn attacker, float amount)
        {
            if (attacker == null || amount <= 0f)
                return;
            for (int i = 0; i < contenders.Count; i++)
            {
                if (contenders[i] == attacker)
                {
                    damage[i] += amount;
                    return;
                }
            }
            contenders.Add(attacker);
            damage.Add(amount);
        }

        /// <summary>
        /// The best claimant: the humanlike player pawn that dealt the most
        /// damage and is still alive to receive it.
        ///
        /// HUMANLIKE because a trait needs a story to live in — a pet can top
        /// the damage table and still not be able to hold Godhood. ALIVE
        /// because granting a trait to a corpse is a silent no-op that would
        /// look exactly like the feature being broken.
        /// </summary>
        public Pawn BestClaimant()
        {
            Pawn best = null;
            float bestDamage = -1f;
            for (int i = 0; i < contenders.Count; i++)
            {
                Pawn p = contenders[i];
                if (p == null || p.Dead || p.Destroyed)
                    continue;
                if (!p.RaceProps.Humanlike || p.story?.traits == null)
                    continue;
                if (p.Faction != Faction.OfPlayer)
                    continue;
                if (damage[i] > bestDamage)
                {
                    bestDamage = damage[i];
                    best = p;
                }
            }
            return best;
        }

        public float DamageBy(Pawn p)
        {
            for (int i = 0; i < contenders.Count; i++)
            {
                if (contenders[i] == p)
                    return damage[i];
            }
            return 0f;
        }

        public void Clear()
        {
            contenders.Clear();
            damage.Clear();
        }

        public void ExposeData()
        {
            Scribe_Collections.Look(ref contenders, "contenders", LookMode.Reference);
            Scribe_Collections.Look(ref damage, "damage", LookMode.Value);
            if (Scribe.mode != LoadSaveMode.PostLoadInit)
                return;

            if (contenders == null) contenders = new List<Pawn>();
            if (damage == null) damage = new List<float>();

            // A reference that failed to resolve comes back null, and the two
            // lists are index-paired — so a hole in one must take the matching
            // entry out of the other or every tally after it shifts onto the
            // wrong pawn.
            for (int i = contenders.Count - 1; i >= 0; i--)
            {
                if (contenders[i] == null || i >= damage.Count)
                {
                    contenders.RemoveAt(i);
                    if (i < damage.Count)
                        damage.RemoveAt(i);
                }
            }
            while (damage.Count > contenders.Count)
                damage.RemoveAt(damage.Count - 1);
        }
    }

    /// <summary>
    /// Credits damage dealt to the summit boss.
    ///
    /// PreApplyDamage rather than a kill hook: "who killed it" is one lucky
    /// swing, and the point is to reward the person who actually carried the
    /// fight. The last hit is often whoever happened to be closest.
    /// </summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.PreApplyDamage))]
    public static class Pawn_PreApplyDamage_SummitCreditPatch
    {
        [HarmonyPostfix]
        public static void Postfix(Pawn __instance, ref DamageInfo dinfo, bool absorbed)
        {
            try
            {
                if (absorbed || dinfo.Amount <= 0f)
                    return;
                if (!(dinfo.Instigator is Pawn attacker) || attacker.Faction != Faction.OfPlayer)
                    return;

                // Cheapest possible gate: almost every hit in the game exits
                // here, on a null check against a field that is only non-null
                // while somebody is standing on the top floor.
                TowerComponent comp = TowerComponent.Get;
                if (comp == null || comp.summitClaimed)
                    return;

                Map map = __instance.Map;
                if (map == null || !(map.Parent is TowerFloorMapParent floorParent))
                    return;
                if (floorParent.floor != TowerTunables.FloorCount)
                    return;
                if (map.GetComponent<DungeonMapComponent>()?.boss != __instance)
                    return;

                comp.SummitDamage.Add(attacker, dinfo.Amount);
            }
            catch (System.Exception ex)
            {
                Log.ErrorOnce($"{IsekaiDungeonsMod.LogPrefix} Summit damage credit failed: {ex}", 0x60D41);
            }
        }
    }
}
