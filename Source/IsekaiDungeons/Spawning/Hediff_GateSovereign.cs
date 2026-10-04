using RimWorld;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// "Gate sovereign": the dungeon's own power sustaining a boss that would
    /// otherwise be too weak for its gate. Only ever applied by
    /// <see cref="ThreatParity.LiftBoss"/>, i.e. only when Isekai Creatures is
    /// absent and the stand-in boss falls short of the measured boss threat.
    ///
    /// <see cref="power"/> is the threat multiplier it must supply. It is
    /// split between toughness (IncomingDamageFactor) and melee damage
    /// (MeleeDamageFactor) as a square root each, so health x damage gains
    /// exactly <see cref="power"/>. A ranged boss cannot have its gun scaled by
    /// a stat, so it takes the whole multiplier as toughness instead.
    /// </summary>
    public class Hediff_GateSovereign : HediffWithComps
    {
        public float power = 1f;
        public bool ranged;

        [Unsaved] private HediffStage stage;

        public void Configure(float multiplier, bool isRanged)
        {
            power = Mathf.Max(1f, multiplier);
            ranged = isRanged;
            stage = null;
        }

        public float ToughnessFactor => ranged ? power : Mathf.Sqrt(power);
        public float MeleeFactor => ranged ? 1f : Mathf.Sqrt(power);

        public override HediffStage CurStage
        {
            get
            {
                if (stage == null)
                {
                    stage = new HediffStage
                    {
                        statFactors = new System.Collections.Generic.List<StatModifier>
                        {
                            new StatModifier { stat = StatDefOf.IncomingDamageFactor, value = 1f / ToughnessFactor },
                        },
                    };
                    if (MeleeFactor > 1.001f)
                        stage.statFactors.Add(new StatModifier { stat = StatDefOf.MeleeDamageFactor, value = MeleeFactor });
                }
                return stage;
            }
        }

        public override string LabelInBrackets => "x" + power.ToString("0.#");

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref power, "power", 1f);
            Scribe_Values.Look(ref ranged, "ranged", false);
        }
    }
}
