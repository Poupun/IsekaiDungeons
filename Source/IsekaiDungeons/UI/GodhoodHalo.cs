using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// The halo that floats over anyone carrying Godhood.
    ///
    /// Drawn rather than equipped: it is not apparel, it does not occupy a
    /// layer, it cannot be stripped, and it costs the pawn nothing. It is a
    /// mark, and a mark should behave like one.
    ///
    /// SIZED TO THE PAWN, AND SELF-NORMALISING. Two different things can make a
    /// pawn big, and a halo that ignores either sits wrong:
    ///
    ///   1. BODY SIZE — RaceProps.baseBodySize times the life stage factor.
    ///      A human is exactly 1.0, so sqrt(BodySize) is a linear scale that
    ///      needs no magic baseline and is defined for every modded race.
    ///   2. GRAPHIC SIZE — some mods (this ecosystem's own world-boss patch
    ///      included) scale the rendered graphic at runtime without touching
    ///      body size. So the render tree's actual drawSize is compared against
    ///      the race def's OWN declared drawSize; an unmodified pawn gives a
    ///      ratio of exactly 1 and the term vanishes.
    ///
    /// Multiplying the two means a 3x-drawn thrumbo-sized humanlike gets a halo
    /// that looks right without this file knowing anything about that mod.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class GodhoodHalo
    {
        private const string TexPath = "Godhood_Halo";

        /// <summary>Halo width in cells for an ordinary human. Deliberately modest.</summary>
        private const float BaseWidth = 0.52f;

        /// <summary>Source art is 119x43; keeping the aspect stops it reading as a squashed ring.</summary>
        private const float Aspect = 119f / 43f;

        /// <summary>Cells above the head, before bobbing.</summary>
        private const float HeadClearance = 0.30f;

        private const float BobAmplitude = 0.03f;
        private const float BobSpeed = 1.6f;
        private const float TiltDegrees = 4f;
        private const float TiltSpeed = 0.9f;

        // Transparent, NOT MoteGlow. MoteGlow blends ADDITIVELY, so over a
        // bright background — an aura, a fire, a lit floor — the halo stops
        // being an object and becomes a smear of light on top of one. That is
        // exactly what made it look see-through. Transparent respects the
        // texture's own alpha and leaves solid pixels solid.
        private static readonly Material HaloMat =
            MaterialPool.MatFrom(TexPath, ShaderDatabase.Transparent);

        /// <summary>Reused so drawing a halo allocates nothing per frame.</summary>
        private static readonly MaterialPropertyBlock PropBlock = new MaterialPropertyBlock();

        private static TraitDef godhoodDefCached;
        private static bool godhoodResolved;

        private static HediffDef ascendantCached;
        private static bool ascendantResolved;

        /// <summary>Carriers of this draw their own halo in their body art.</summary>
        private static HediffDef AscendantDef
        {
            get
            {
                if (!ascendantResolved)
                {
                    ascendantCached = DefDatabase<HediffDef>.GetNamedSilentFail("IsekaiDungeons_Ascendant");
                    ascendantResolved = true;
                }
                return ascendantCached;
            }
        }

        private static TraitDef GodhoodDef
        {
            get
            {
                if (!godhoodResolved)
                {
                    godhoodDefCached = DefDatabase<TraitDef>.GetNamedSilentFail("Isekai_Godhood");
                    godhoodResolved = true;
                }
                return godhoodDefCached;
            }
        }

        public static bool ShouldDraw(Pawn pawn)
        {
            // Cheapest, most selective checks first — this runs per pawn per
            // frame, so anything that touches a list belongs at the bottom.
            if (pawn == null || !pawn.Spawned || pawn.Dead)
                return false;
            if (!IsekaiDungeonsSettings.Current.godhoodHaloEnabled)
                return false;
            if (!pawn.RaceProps.Humanlike)
                return false;
            if (pawn.story?.traits == null)
                return false;

            TraitDef def = GodhoodDef;
            if (def == null || !pawn.story.traits.HasTrait(def))
                return false;

            // ELIJAH DRAWS HER OWN. Her body art already carries a halo, so the
            // trait's floating one would sit above it as a second ring. Keyed
            // on the ascendant hediff rather than on her name or def so any
            // future authored character with its own halo in the art gets the
            // same treatment by carrying the same hediff.
            HediffDef ascendant = AscendantDef;
            if (ascendant != null && pawn.health?.hediffSet != null
                && pawn.health.hediffSet.HasHediff(ascendant))
                return false;

            return true;
        }

        public static void Draw(Pawn pawn, Vector3 drawLoc)
        {
            if (HaloMat == null)
                return;

            float scale = RenderScaleFor(pawn);
            float width = BaseWidth * scale;
            float height = width / Aspect;

            // Per-pawn phase, so a room full of ascended colonists does not bob
            // in lockstep like a chorus line.
            float phase = pawn.thingIDNumber * 0.6180339f;
            float t = Tween.Now;
            float bob = Mathf.Sin(t * BobSpeed + phase * 6.283f) * BobAmplitude * scale;

            // HORIZONTALLY PINNED TO THE PAWN'S CENTRELINE, always.
            //
            // BaseHeadOffsetAt carries an X component that comes from the body
            // type, and a modded race can put the head anywhere it likes — which
            // is how the halo ended up sitting off to one side of the face. Only
            // the vertical part of that offset is worth having; the horizontal
            // part is a guess about someone else's art.
            //
            // A halo is a symbol, not a hat. It belongs on the axis of the pawn.
            // pos.x is left exactly as drawLoc gives it — that IS the centreline.
            Vector3 pos = drawLoc;
            pos.z += HeadOffsetFor(pawn).z + HeadClearance * scale + bob;
            // Above everything the pawn itself draws, so hair and headgear
            // cannot clip through it.
            pos.y = AltitudeLayer.MoteOverhead.AltitudeFor();

            // A slow tilt reads as "floating" far better than a spin does: the
            // art is an ellipse already, and spinning a flat ellipse looks like
            // wobbling rather than turning.
            float tilt = Mathf.Sin(t * TiltSpeed + phase * 6.283f) * TiltDegrees;

            var matrix = default(Matrix4x4);
            matrix.SetTRS(pos, Quaternion.AngleAxis(tilt, Vector3.up), new Vector3(width, 1f, height));

            // Fully opaque. The art already carries its own soft edges; fading
            // it on top of that is what read as "transparent".
            PropBlock.SetColor(ShaderPropertyIDs.Color, Color.white);

            Graphics.DrawMesh(MeshPool.plane10, matrix, HaloMat, 0, null, 0, PropBlock);
        }

        /// <summary>
        /// Where the head is. Vanilla knows, but only for pawns that have a
        /// body type — a modded humanlike without one would throw, and a
        /// missing halo is a far better outcome than an exception every frame.
        /// </summary>
        /// <summary>
        /// Head height only — the caller discards the horizontal component on
        /// purpose (see Draw).
        /// </summary>
        private static Vector3 HeadOffsetFor(Pawn pawn)
        {
            try
            {
                if (pawn.story?.bodyType != null && pawn.Drawer?.renderer != null)
                    return pawn.Drawer.renderer.BaseHeadOffsetAt(pawn.Rotation);
            }
            catch
            {
                // Fall through to the plain body-centre offset below.
            }
            return Vector3.zero;
        }

        /// <summary>See the class comment: body size times graphic deviation.</summary>
        private static float RenderScaleFor(Pawn pawn)
        {
            // sqrt because BodySize behaves like an area and we want a length.
            float scale = Mathf.Sqrt(Mathf.Max(0.05f, pawn.BodySize));

            Graphic body = pawn.Drawer?.renderer?.BodyGraphic;
            Vector2 declared = pawn.def?.graphicData?.drawSize ?? Vector2.zero;
            if (body != null && body.drawSize.y > 0.01f && declared.y > 0.01f)
                scale *= Mathf.Clamp(body.drawSize.y / declared.y, 0.4f, 8f);

            return Mathf.Clamp(scale, 0.45f, 8f);
        }
    }

    /// <summary>
    /// Draws the halo on the same hook the base mod uses for auras, as a
    /// SEPARATE patch — so neither can break the other, and removing this
    /// add-on takes the halo with it and nothing else.
    /// </summary>
    // Pawn.DrawAt is PROTECTED, so nameof() cannot reach it from here — the
    // string form is the only way to name it, and it is also the form this
    // mod's Harmony bootstrap is known to pick up (it scans types for patch
    // attributes rather than calling PatchAll).
    [HarmonyPatch(typeof(Pawn), "DrawAt")]
    public static class Pawn_DrawAt_GodhoodHaloPatch
    {
        static bool Prepare()
        {
            if (AccessTools.Method(typeof(Pawn), "DrawAt") != null)
                return true;
            Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Pawn.DrawAt not found — the Godhood halo will not render.");
            return false;
        }

        [HarmonyPostfix]
        public static void Postfix(Pawn __instance, Vector3 drawLoc)
        {
            try
            {
                if (GodhoodHalo.ShouldDraw(__instance))
                    GodhoodHalo.Draw(__instance, drawLoc);
            }
            catch (System.Exception ex)
            {
                // One bad pawn must never take the whole render loop down.
                Log.ErrorOnce($"{IsekaiDungeonsMod.LogPrefix} Godhood halo draw failed: {ex}", 0x60D40);
            }
        }
    }
}
