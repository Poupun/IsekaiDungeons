using System.Collections.Generic;
using IsekaiLeveling.MobRanking;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// How a dungeon gate looks: an animated portal, coloured and sized by the
    /// rank of what is behind it, lighting the ground around it and throwing
    /// the occasional arc.
    ///
    /// THE COLOUR IS THE WARNING. A player should be able to tell across the
    /// map whether the thing that just opened in their yard is survivable, and
    /// they should be able to tell it BEFORE reading the letter.
    ///
    ///     blue    F  E  D  C  B  A      a problem
    ///     red     S  SS  SSS            a serious problem, and the ceiling
    ///     pink    the Tower             not weather
    ///
    /// RED IS THE TOP OF THE SKY. A gate is a seep, and a seep can only carry
    /// what the world above it is able to hold; that measure stops at SSS, so
    /// a wild gate has no colour past red and a colony's whole career outside
    /// the Tower is spent under one.
    ///
    /// PINK IS NOT A RANK BAND ANY MORE. It belongs to the Tower and to the
    /// chamber underneath it, and it is used nowhere else, so a player who sees
    /// it has seen it for the first time and it can only mean one thing. Rank
    /// still resolves to it — the cell at floor 1 is Nation and comes out pink
    /// on its own — but every tower door is drawn pink regardless of what floor
    /// it stands on, because the door belongs to the building, not the floor.
    ///
    /// SIZE IS CONTINUOUS ACROSS ALL TEN RANKS, not restarted per band. A red
    /// S-gate is bigger than a blue A-gate, which is the whole point: size and
    /// colour agree, and bigger always means worse. Restarting the scale at
    /// each colour would make a low red gate LOOK safer than a high blue one.
    ///
    /// ONE SEQUENCE, TINTED, PLUS AN FX LAYER. The 193 frames are authored in
    /// greyscale and multiplied by the band's colour in the material, so the
    /// art is stored once no matter how many colours exist — adding a fourth
    /// band is a line in <see cref="TintFor"/> rather than another 193 files.
    ///
    /// A second greyscale sequence, WhiteFX, carries the bright filaments on
    /// black and is ADDED over the tinted result. That exists because a colour
    /// filter fundamentally cannot desaturate: multiplying preserves hue at
    /// every brightness, so a filtered portal is one flat hue throughout, and
    /// the white accentuation the painted art had cannot be recovered by any
    /// choice of tint. Carrying the whites in their own layer is the fix, and
    /// it stays colour-independent — one FX layer serves every band.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class GateVisuals
    {
        // ── Art ─────────────────────────────────────────────────────────────

        private const string GateFolder = "Gates/";

        /// <summary>
        /// The rate the gate sequence was AUTHORED at, not a rate chosen here.
        ///
        /// 24, from the artist. This was previously 30 on an assumption, which
        /// played the loop 1.25x too fast — the portal visibly span quicker than
        /// the source animation. If the sequence is ever re-exported at another
        /// rate, this is the one number that has to follow it.
        ///
        /// 193 frames at 24 fps is an 8.04 second loop.
        ///
        /// ONE RATE FOR THE WHOLE PORTAL. The master, the overbright pass and
        /// the WhiteFX layer are all indexed by the SAME idx computed from this
        /// constant, so they cannot drift apart: the white filaments always
        /// belong to the swirl they were drawn over. The arc clips are the one
        /// exception and are deliberately not tied to this — see ElectricFps.
        /// </summary>
        private const float GateFps = 24f;

        /// <summary>
        /// The electric clips live in the BASE mod. ContentFinder searches every
        /// active mod's content, and this add-on hard-depends on IsekaiLeveling,
        /// so borrowing them needs no copy and no duplication of the art.
        /// </summary>
        private static readonly string[] ElectricClips =
        {
            "Auras/Electric/electric1",
            "Auras/Electric/electric2",
            "Auras/Electric/electric3",
            "Auras/Electric/electric4",
        };

        /// <summary>
        /// The ARC clips are a separate sequence with a separate rate: the base
        /// mod extracts and plays them at 30 (IsekaiLeveling's ELECTRIC_FPS).
        /// Deliberately not tied to GateFps — they are different art.
        /// </summary>
        private const float ElectricFps = 30f;

        // ── Colour bands ────────────────────────────────────────────────────

        public enum GateColor { Blue, Red, Pink }

        /// <summary>
        /// The world-map icon for a gate of this rank, or null if the art is
        /// missing (callers fall back to the def's own texture).
        ///
        /// Reuses ColorFor, so the pin on the planet is the same colour as the
        /// gate the party will walk through when they get there - blue up to A,
        /// red to SSS, pink for Nation and the tower. Cached because
        /// ExpandingIcon is polled every world-map frame, per object.
        /// </summary>
        private static readonly Dictionary<GateColor, Texture2D> WorldIcons =
            new Dictionary<GateColor, Texture2D>();

        public static Texture2D WorldIcon(MobRankTier rank)
        {
            GateColor c = ColorFor(rank);
            if (!WorldIcons.TryGetValue(c, out Texture2D tex))
            {
                string name = c == GateColor.Red ? "red" : (c == GateColor.Pink ? "pink" : "blue");
                tex = ContentFinder<Texture2D>.Get("Gates/dungeongate_" + name + "--icon", false);
                WorldIcons[c] = tex;
            }
            return tex;
        }

        /// <summary>Which colour a rank opens as. See the class comment.</summary>
        public static GateColor ColorFor(MobRankTier rank)
        {
            if (rank <= MobRankTier.A)
                return GateColor.Blue;
            if (rank <= MobRankTier.SSS)
                return GateColor.Red;
            return GateColor.Pink;
        }

        /// <summary>The Tower's own colour, for doors that belong to it.</summary>
        public const GateColor TowerColor = GateColor.Pink;

        /// <summary>
        /// The greyscale master. Every gate of every colour draws these frames.
        /// </summary>
        private const string BaseSequence = GateFolder + "BaseTexture/BasePortal_";

        /// <summary>
        /// The accentuation layer: the same 193 frames carrying only the bright
        /// filaments, on black. Added over the tint, never tinted itself.
        /// </summary>
        private const string FxSequence = GateFolder + "WhiteFX/WhiteFX_";

        /// <summary>
        /// How hard the white accentuation is laid on. THE ONE NUMBER TO TURN
        /// if the portal is too flat or too hot.
        ///
        /// 1 is the layer at the strength it was drawn, which is what
        /// "superpose it" most plainly means, and it is comfortably above the
        /// level the complaint was about.
        ///
        /// For reference, if matching the old painted art is ever the goal, the
        /// measured answer is about 0.5 — fitted on filament pixels only, since
        /// a fit over the whole portal is swamped by the body and would say
        /// almost nothing about the filaments. Worth knowing that the painted
        /// art is only 0.01% pure white: its "white" filaments are bright
        /// streaks rather than blown-out ones, and this layer at 1 is brighter
        /// than they ever were.
        ///
        /// Capped at 1 because a material colour cannot exceed it —
        /// MaterialPool.MatFrom casts through Color32. Past that the layer
        /// itself has to be redrawn brighter. A setting (Interface tab), 1 by
        /// default, so the slider can only turn it down.
        /// </summary>
        private static float FxStrength => IsekaiDungeonsSettings.Current.gateEffectsStrength;

        /// <summary>
        /// What to multiply the greyscale master by, per band, to get that
        /// band's colour.
        ///
        /// MEASURED, NOT PICKED. Each gain is a clipping-aware least-squares
        /// fit of the greyscale master against the hand-coloured sequence it
        /// replaces, taken over every pixel of five frames spread across the
        /// loop. The portal therefore keeps the hue the artist actually drew
        /// rather than one that merely sounds right, and the fitted pink landed
        /// within 0.02 of the independently hand-picked arc tint below — which
        /// is the check that the method is sound.
        ///
        /// The raw gains are:
        ///
        ///     blue   0.317  0.944  1.590
        ///     red    1.188  0.510  0.514
        ///     pink   1.188  0.479  0.939
        ///
        /// One channel each exceeds 1, because the hand-coloured art has a
        /// hotter core than any multiply of the master can reach. A material
        /// colour cannot carry that: MaterialPool.MatFrom casts its colour
        /// through Color32, so anything above 1 is silently clamped. So the
        /// gain is SPLIT — see <see cref="AdditiveFor"/>.
        ///
        /// WHAT A COLOUR FILTER CANNOT DO. In the old art the bright filaments
        /// are white, not bright-blue: they are DESATURATED as they get
        /// brighter. Multiplying by a constant preserves hue at every
        /// brightness, so a filtered portal has a single hue throughout and
        /// reads flatter than the painted one. No choice of gain fixes that.
        /// The fix, if the flatness bothers you, is one-time and art-side:
        /// author the master with more contrast — filaments pushed to pure
        /// white, body darker — and the same filter then yields a deep coloured
        /// body with near-white filaments, because the highlights clip.
        /// </summary>
        private static Color TintFor(GateColor c)
        {
            switch (c)
            {
                case GateColor.Red: return new Color(1.000f, 0.510f, 0.514f);
                case GateColor.Pink: return new Color(1.000f, 0.479f, 0.939f);
                default: return new Color(0.317f, 0.944f, 1.000f);
            }
        }

        /// <summary>
        /// The part of the gain above 1, delivered as a second additive pass.
        ///
        /// multiply = min(gain, 1) and additive = max(gain - 1, 0), so the two
        /// passes sum to exactly the fitted gain and the representable range
        /// becomes 0..2 per channel with nothing clamped. It costs one extra
        /// DrawMesh on a handful of portals, and it is skipped outright when
        /// this is black.
        ///
        /// Additive is safe here because MoteGlow is ALPHA-WEIGHTED additive
        /// (Blend SrcAlpha One), not plain One One. Vanilla proves it: the
        /// projectile interceptor's shield bubble is a MoteGlow material whose
        /// only fade control is `value.a *= currentAlpha` on the material
        /// colour, and it visibly fades — which is impossible if the blend
        /// ignores alpha.
        ///
        /// That distinction matters and is not theoretical. The master carries
        /// stray colour in its fully transparent texels — up to 133/255 on
        /// frame 68, checked across all 193 frames at full resolution — so a
        /// plain One One additive pass WOULD have lit up a square of nothing
        /// around every portal. Alpha weighting multiplies that away.
        /// </summary>
        private static Color AdditiveFor(GateColor c)
        {
            switch (c)
            {
                case GateColor.Red: return new Color(0.188f, 0f, 0f);
                case GateColor.Pink: return new Color(0.188f, 0f, 0f);
                default: return new Color(0f, 0f, 0.590f);
            }
        }

        /// <summary>
        /// The light each colour casts. Deliberately more saturated than the art
        /// so the glow reads as the gate's own colour on the ground rather than
        /// washing everything to white.
        /// </summary>
        public static ColorInt GlowFor(GateColor c)
        {
            switch (c)
            {
                case GateColor.Red: return new ColorInt(255, 70, 45);
                case GateColor.Pink: return new ColorInt(255, 80, 200);
                default: return new ColorInt(70, 140, 255);
            }
        }

        /// <summary>Tint for the electric arcs, matched to the gate.</summary>
        public static Color ArcColorFor(GateColor c)
        {
            switch (c)
            {
                case GateColor.Red: return new Color(1f, 0.42f, 0.30f);
                case GateColor.Pink: return new Color(1f, 0.48f, 0.86f);
                default: return new Color(0.48f, 0.72f, 1f);
            }
        }

        // ── Size ────────────────────────────────────────────────────────────

        /// <summary>
        /// Draw size in cells, continuous across all ten ranks.
        ///
        /// The building itself is 5x5 and that never changes — only the art
        /// grows, so a Nation gate towers over its own footprint the way a hole
        /// in the world should, without becoming impassable terrain that a
        /// colony has to path around differently.
        /// </summary>
        public static readonly SimpleCurve SizeCurve = new SimpleCurve
        {
            new CurvePoint(0f, 4.2f),    // F
            new CurvePoint(2f, 5.4f),    // D
            new CurvePoint(3f, 6.0f),    // C
            new CurvePoint(5f, 7.4f),    // A — top of blue
            new CurvePoint(6f, 8.2f),    // S — bottom of red
            new CurvePoint(9f, 11.5f),   // Nation — pink, and the only one
        };

        public static float SizeFor(MobRankTier rank) => SizeCurve.Evaluate((int)rank);

        // ── Opening ─────────────────────────────────────────────────────────

        /// <summary>
        /// The spring the gate opens on.
        ///
        /// SLOW AND HEAVY, not snappy. omega 7 with zeta 0.5 takes about a
        /// second and a half and overshoots gently once — a big thing tearing
        /// open and settling, rather than a UI element popping in. A gate that
        /// simply appeared at full size read as a rendering glitch; the whole
        /// point of the curve is that the eye sees it ARRIVE.
        /// </summary>
        private const float OpenOmega = 7f;
        private const float OpenZeta = 0.5f;

        /// <summary>How long the opening lasts, derived from the spring rather than guessed.</summary>
        public static float OpenDuration => Tween.SettleTime(OpenOmega, OpenZeta);

        /// <summary>
        /// Scale multiplier for a portal <paramref name="age"/> seconds old.
        ///
        /// Returns 1 for a negative age, which is the "no intro" signal: a gate
        /// restored from a save has always existed as far as the player is
        /// concerned, and replaying its opening every time the game loads would
        /// be a lie about what just happened.
        /// </summary>
        public static float OpenScale(float age)
        {
            if (age < 0f)
                return 1f;
            // Starts at a sliver rather than nothing: a portal opening from a
            // point of light reads better than one inflating from zero area.
            return Mathf.Lerp(0.06f, 1f, Tween.Spring01(age, OpenOmega, OpenZeta));
        }

        /// <summary>
        /// Alpha for the same moment. Fades in FASTER than the scale settles, so
        /// the gate is fully solid while it is still growing — the size does the
        /// arriving, and a lingering translucency would just look unfinished.
        /// </summary>
        public static float OpenAlpha(float age)
        {
            if (age < 0f)
                return 1f;
            return Tween.EaseOutCubic(Mathf.Clamp01(age / (OpenDuration * 0.45f)));
        }

        // ── Frame cache ─────────────────────────────────────────────────────

        private static List<Texture2D> baseFrames;
        private static bool baseProbed;

        private static List<Texture2D> fxFrames;
        private static bool fxProbed;

        /// <summary>
        /// The accentuation frames, or an empty list when the layer is absent.
        ///
        /// </summary>
        private static List<Texture2D> FxFramesFor(int masterCount)
        {
            if (fxProbed)
                return fxFrames;

            fxProbed = true;
            fxFrames = LoadSequence(FxSequence);
            if (fxFrames.Count != 0 && fxFrames.Count != masterCount)
            {
                // Not fatal — the draw indexes modulo its own length — but it
                // means the two layers drift apart over the loop and the
                // filaments stop lining up with the swirl they belong to.
                Log.WarningOnce($"{IsekaiDungeonsMod.LogPrefix} Gate FX layer has {fxFrames.Count} frames but "
                              + $"the master has {masterCount}. They must match or the whites will desync.",
                                0x5EC0FF);
            }
            return fxFrames;
        }

        private static readonly Dictionary<int, List<Texture2D>> ArcFrames
            = new Dictionary<int, List<Texture2D>>();

        /// <summary>Loads a contiguous zero-padded sequence, or an empty list.</summary>
        private static List<Texture2D> LoadSequence(string prefix)
        {
            var list = new List<Texture2D>();
            for (int i = 0; i < 1000; i++)
            {
                Texture2D t = ContentFinder<Texture2D>.Get($"{prefix}{i:00000}", reportFailure: false);
                if (t == null)
                    break;
                list.Add(t);
            }

            // A frozen animation has exactly three causes and this names all of
            // them at load: too few files, or the same Texture2D handed back for
            // several indices. Both look identical in game — a still picture —
            // and neither raises an error on its own.
            if (Prefs.DevMode && list.Count > 0)
            {
                var seen = new HashSet<int>();
                for (int i = 0; i < list.Count; i++)
                    seen.Add(list[i].GetInstanceID());
                Log.Message($"{IsekaiDungeonsMod.LogPrefix} sequence '{prefix}NNNNN': "
                          + $"{list.Count} frames, {seen.Count} distinct textures"
                          + (seen.Count == list.Count ? "." : " — DUPLICATES, this sequence cannot animate."));
            }
            return list;
        }

        /// <summary>
        /// The master frames, loaded once and shared by every colour.
        ///
        /// Empty means the art is missing, and the callers fall back to the
        /// def's static graphic rather than drawing nothing.
        /// </summary>
        private static List<Texture2D> MasterFrames()
        {
            if (!baseProbed)
            {
                baseProbed = true;
                baseFrames = LoadSequence(BaseSequence);
                if (baseFrames.Count == 0)
                {
                    Log.WarningOnce($"{IsekaiDungeonsMod.LogPrefix} No gate frames at '{BaseSequence}NNNNN' — "
                                  + "falling back to the static gate texture.", BaseSequence.GetHashCode());
                }
            }
            return baseFrames;
        }

        private static List<Texture2D> ArcFramesFor(int clip)
        {
            if (clip < 0 || clip >= ElectricClips.Length)
                return null;
            if (ArcFrames.TryGetValue(clip, out List<Texture2D> cached))
                return cached;

            var list = new List<Texture2D>();
            string prefix = ElectricClips[clip];
            for (int i = 1; i <= 99; i++)
            {
                Texture2D t = ContentFinder<Texture2D>.Get($"{prefix}_{i:00}", reportFailure: false);
                if (t == null)
                    break;
                list.Add(t);
            }
            ArcFrames[clip] = list;
            return list;
        }

        /// <summary>
        /// The material for one frame.
        ///
        /// THIS USED TO KEEP ITS OWN CACHE AND THAT CACHE WAS UNSOUND. It built
        /// an int key as
        ///
        ///     tex.GetInstanceID() ^ (color.GetHashCode() * 31) ^ (shader.GetInstanceID() * 7)
        ///
        /// and used it as a Dictionary key directly — a lossy XOR of three
        /// independent values, stored with no equality check behind it. Any two
        /// (texture, colour, shader) triples that happened to XOR to the same
        /// int shared one material forever, and the first one to arrive won.
        /// With three passes per portal drawing from two 193-frame sequences
        /// through two shaders, that is a lot of chances, and a collision is
        /// invisible: the wrong frame simply persists.
        ///
        /// The justification for having it was also false. It claimed
        /// MaterialPool "allocates" per call, but MaterialRequest is a STRUCT,
        /// so constructing one costs nothing on the heap, and its GetHashCode
        /// goes through Gen.HashCombineStruct, which is constrained to structs
        /// and therefore does not box either. MaterialPool.matDictionary is
        /// keyed by the whole MaterialRequest with real equality — mainTex
        /// included — so it is both correct and already the cache this wanted.
        /// </summary>
        private static Material MatFor(Texture2D tex, Color color, Shader shader) =>
            MaterialPool.MatFrom(new MaterialRequest(tex, shader, color));

        // ── Drawing ─────────────────────────────────────────────────────────

        /// <summary>
        /// Draws the animated portal. Returns false when no frames exist, so the
        /// caller can fall back to the def's static graphic rather than drawing
        /// nothing at all.
        /// </summary>
        public static bool DrawGate(Thing gate, Vector3 drawLoc, MobRankTier rank, float sizeOverride = -1f,
                                    float age = -1f, GateColor? colorOverride = null)
        {
            GateColor col = colorOverride ?? ColorFor(rank);
            List<Texture2D> frames = MasterFrames();
            if (frames.Count == 0)
                return false;

            // ONE LOOP'S WORTH OF TIME, AND AN INTEGER PHASE.
            //
            // This used to be `Now + (thingIDNumber * 0.618f) * 13f`, multiplied
            // by the rate and truncated. The per-gate offset was the problem: at
            // a thing ID of 200,000 it is over 1.6 MILLION seconds, and
            // `t * GateFps` then lands around 4e7, where a float's ULP is larger
            // than the 0.4 the value advances per display frame. The expression
            // physically could not step every frame, so frames were held and the
            // portal ran slow — and it got worse the further into a save you
            // were, because thing IDs only ever grow. Measured: 24.0 fps at ID
            // 20,000, 16.0 at 80,000, 6.0 at 200,000.
            //
            // Reducing the clock modulo one loop first keeps every value small,
            // and moving the per-gate offset into INTEGER frames removes it from
            // the float path entirely. Measured flat at 24.0 fps for every thing
            // ID out to two million and a hundred-hour session, and the gates
            // still land on different frames so they do not pulse in lockstep.
            float loopSeconds = frames.Count / GateFps;
            int frameNow = (int)((Tween.Now % loopSeconds) * GateFps);
            // Reduced to a frame index HERE, not after the addition: the mask
            // leaves a value up to int.MaxValue, and adding frameNow to that
            // would wrap negative and index out of range.
            int offset = (unchecked(gate.thingIDNumber * 7919) & 0x7FFFFFFF) % frames.Count;
            int idx = (frameNow + offset) % frames.Count;

            float size = (sizeOverride > 0f ? sizeOverride : SizeFor(rank)) * OpenScale(age);
            Vector3 pos = drawLoc;
            pos.y = AltitudeLayer.Building.AltitudeFor();

            var m = default(Matrix4x4);
            m.SetTRS(pos, Quaternion.identity, new Vector3(size, 1f, size));

            // QUANTISED, because every distinct colour is a distinct cached
            // Material that lives until the process ends. The opening fade
            // sweeps alpha continuously, so an unrounded value minted a new
            // material on every rendered frame of every gate that ever opened —
            // and allocating one mid-draw is itself a hitch, precisely while a
            // gate is opening. Sixteen steps is finer than the eye follows on a
            // one-second fade and caps the set at one material per frame of the
            // sequence.
            float alpha = Mathf.Round(OpenAlpha(age) * 16f) / 16f;

            Color tint = TintFor(col);
            tint.a = alpha;

            // Transparent, not additive: the portal is an OBJECT with its own
            // interior, and additive would let the ground show through it.
            Graphics.DrawMesh(MeshPool.plane10, m,
                              MatFor(frames[idx], tint, ShaderDatabase.Transparent), 0);

            // The overbright half of the gain, on top.
            {
                Color add = AdditiveFor(col);
                if (add.r > 0f || add.g > 0f || add.b > 0f)
                {
                    add.a = alpha;

                    // ONE ALTITUDE INCREMENT ABOVE, not the same y as the pass
                    // below it. Order is not optional here: adding first and
                    // then alpha-blending over the result would blend part of
                    // the glow back out, and two coplanar transparent quads
                    // have no defined order at all — they would flicker as the
                    // camera moved. AltInc is a tenth of a layer, so this stays
                    // inside Building and cannot cross another layer.
                    var mAdd = default(Matrix4x4);
                    Vector3 addPos = pos;
                    addPos.y = AltitudeLayer.Building.AltitudeFor(1f);
                    mAdd.SetTRS(addPos, Quaternion.identity, new Vector3(size, 1f, size));

                    Graphics.DrawMesh(MeshPool.plane10, mAdd,
                                      MatFor(frames[idx], add, ShaderDatabase.MoteGlow), 0);
                }

                // ── The white accentuation ─────────────────────────────────
                //
                // Added, not blended. For a source that is black everywhere it
                // is not a filament, adding IS "screen, ignoring the black":
                // black contributes nothing and white contributes fully, which
                // is exactly the filter asked for. MoteGlow is the
                // alpha-weighted additive shader, so the layer's own alpha
                // still confines it to the portal's shape.
                List<Texture2D> fx = FxFramesFor(frames.Count);
                if (fx.Count > 0 && FxStrength > 0f)
                {
                    float s = Mathf.Min(FxStrength, 1f);
                    var mFx = default(Matrix4x4);
                    Vector3 fxPos = pos;
                    fxPos.y = AltitudeLayer.Building.AltitudeFor(2f);
                    mFx.SetTRS(fxPos, Quaternion.identity, new Vector3(size, 1f, size));

                    Graphics.DrawMesh(MeshPool.plane10, mFx,
                                      MatFor(fx[idx % fx.Count], new Color(s, s, s, alpha),
                                             ShaderDatabase.MoteGlow), 0);
                }
            }
            return true;
        }

        /// <summary>
        /// Occasional arcs around the gate mouth, borrowed from the base mod's
        /// aura VFX and tinted to the gate's colour.
        ///
        /// Additive on purpose here — the source frames have a BLACK background
        /// and no alpha channel, which is exactly what MoteGlow expects: black
        /// becomes transparent and the arc reads as light rather than as a card
        /// with a dark square around it.
        /// </summary>
        public static void DrawArcs(Thing gate, Vector3 drawLoc, MobRankTier rank, ref GateArcState state,
                                    float sizeOverride = -1f, GateColor? colorOverride = null)
        {
            float now = Tween.Now;
            float size = sizeOverride > 0f ? sizeOverride : SizeFor(rank);

            if (state.clip < 0)
            {
                if (now < state.nextAt)
                    return;
                state.clip = Rand.RangeInclusive(0, ElectricClips.Length - 1);
                state.start = now;
                // Around the rim, not on top of the middle — the arcs should
                // look like something escaping the edge of the hole.
                float ang = Rand.Range(0f, 360f) * Mathf.Deg2Rad;
                float r = size * Rand.Range(0.18f, 0.34f);
                state.offset = new Vector3(Mathf.Cos(ang) * r, 0f, Mathf.Sin(ang) * r * 0.6f);
                state.scale = size * Rand.Range(0.30f, 0.55f);
            }

            List<Texture2D> frames = ArcFramesFor(state.clip);
            if (frames == null || frames.Count == 0)
            {
                state.clip = -1;
                state.nextAt = now + 3f;
                return;
            }

            int idx = Mathf.FloorToInt((now - state.start) * ElectricFps);
            if (idx < 0 || idx >= frames.Count)
            {
                state.clip = -1;
                // Higher rank crackles more often; a Nation gate should never
                // look calm.
                float lull = Mathf.Lerp(2.6f, 0.5f, (int)rank / 9f);
                state.nextAt = now + Rand.Range(lull * 0.5f, lull * 1.5f);
                return;
            }

            Texture2D tex = frames[idx];
            if (tex == null)
                return;

            Vector3 pos = drawLoc + state.offset;
            pos.y = AltitudeLayer.MoteOverhead.AltitudeFor();
            var m = default(Matrix4x4);
            m.SetTRS(pos, Quaternion.identity, new Vector3(state.scale, 1f, state.scale));
            Graphics.DrawMesh(MeshPool.plane10, m,
                              MatFor(tex, ArcColorFor(colorOverride ?? ColorFor(rank)), ShaderDatabase.MoteGlow), 0);
        }
    }

    /// <summary>
    /// One call for every portal-shaped building in the mod.
    ///
    /// The entrance, the exit and the tower stair are all holes in the world
    /// and should read as the same KIND of thing — the only differences are how
    /// big they are and which way they lead. Sharing this means the exit can
    /// never quietly drift into looking like something else after the gate is
    /// retuned.
    /// </summary>
    public static class PortalDrawer
    {
        /// <summary>
        /// Draws the animation and its arcs. Returns false when frames are
        /// missing so the caller can fall back to its def's static graphic.
        /// </summary>
        public static bool Draw(Thing thing, Vector3 drawLoc, MobRankTier rank,
                                float size, ref GateArcState arcs, bool withArcs = true,
                                float age = -1f, GateVisuals.GateColor? colorOverride = null)
        {
            if (!GateVisuals.DrawGate(thing, drawLoc, rank, size, age, colorOverride))
                return false;

            // Arcs wait for the mouth to exist. Firing them while the portal is
            // still a sliver puts full-size lightning around a dot.
            bool opening = age >= 0f && age < GateVisuals.OpenDuration * 0.5f;
            if (withArcs && !opening)
                GateVisuals.DrawArcs(thing, drawLoc, rank, ref arcs, size, colorOverride);
            return true;
        }

        /// <summary>
        /// Seconds since this thing appeared, or -1 when it should not play an
        /// opening at all.
        ///
        /// A negative sentinel rather than 0, because 0 is a real age: the frame
        /// a gate spawns on. Callers store -1 for "was already here".
        /// </summary>
        public static float AgeOf(float spawnRealTime)
        {
            if (spawnRealTime < 0f)
                return -1f;
            float age = Tween.Now - spawnRealTime;
            return age >= GateVisuals.OpenDuration ? -1f : age;
        }

        /// <summary>
        /// The rank of whatever this map is, for colouring. Works for both a
        /// dungeon and a tower floor, because a tower floor's parent subclasses
        /// the dungeon's.
        /// </summary>
        public static MobRankTier RankOf(Map map)
        {
            if (map?.Parent is DungeonMapParent parent)
                return parent.rank;
            return map?.GetComponent<DungeonMapComponent>()?.Rank ?? MobRankTier.F;
        }
    }

    /// <summary>Per-gate arc animation state. A struct so it costs nothing to hold.</summary>
    public struct GateArcState
    {
        /// <summary>Index into the clip list, or -1 when no arc is playing.</summary>
        public int clip;
        public float start;
        public float nextAt;
        public float scale;
        public Vector3 offset;

        public static GateArcState Fresh => new GateArcState { clip = -1, nextAt = 0f };
    }
}
