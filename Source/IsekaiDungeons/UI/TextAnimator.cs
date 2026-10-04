using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace IsekaiDungeons
{
    /// <summary>What a single animated piece of a text run is.</summary>
    public enum TextGranularity
    {
        /// <summary>One piece per glyph. Use for short, hero text.</summary>
        Character,
        /// <summary>One piece per word. Use for sentences — per-glyph on a long line reads as a stutter, not a reveal.</summary>
        Word,
    }

    /// <summary>
    /// How a run's pieces arrive. Tuning only — no state.
    /// </summary>
    public struct TextReveal
    {
        /// <summary>Seconds before the FIRST piece begins.</summary>
        public float delay;
        /// <summary>Seconds between consecutive pieces. This is the whole effect.</summary>
        public float stagger;
        /// <summary>Pixels each piece travels upward into place. Negative drops it in from above.</summary>
        public float rise;
        /// <summary>Seconds of alpha ramp per piece. Shorter than the spring, so pieces are visible while still moving.</summary>
        public float fade;
        /// <summary>Scale each piece starts at. 1 disables scaling.</summary>
        public float scaleFrom;
        /// <summary>Spring frequency — bigger is snappier.</summary>
        public float omega;
        /// <summary>Spring damping — below 1 overshoots.</summary>
        public float zeta;

        public static TextReveal Make(float delay, float stagger, float rise, float scaleFrom,
                                      float omega, float zeta, float fade)
        {
            return new TextReveal
            {
                delay = delay,
                stagger = stagger,
                rise = rise,
                scaleFrom = scaleFrom,
                omega = omega,
                zeta = zeta,
                fade = fade,
            };
        }
    }

    /// <summary>
    /// One line (or wrapped block) of text, measured once and then drawn
    /// piece-by-piece with an independent animation per piece.
    ///
    /// WHY MEASURE INSTEAD OF HANDING THE STRING TO A RENDERER: to animate a
    /// glyph on its own it has to be drawn on its own, and drawing "D", "e",
    /// "m"… one at a time throws away every kerning pair the typeface defines.
    /// That is not a subtlety — these faces carry six to ten thousand pairs,
    /// worth up to 20px on a 132px title. So each piece's position comes from
    /// the difference between consecutive cumulative advances, which puts it
    /// exactly where a shaper would have, while leaving it free to move on its
    /// own.
    ///
    /// Measuring happens once per built run — a card trigger, or a resolution
    /// change — never per frame.
    /// </summary>
    public sealed class AnimatedTextRun
    {
        private struct Piece
        {
            public string text;
            /// <summary>Pen start, relative to the run's horizontal centre.</summary>
            public float x;
            /// <summary>This piece's baseline, relative to the run's FIRST baseline.</summary>
            public float baseline;
            public float w;
            /// <summary>Stagger slot. Runs count across wrapped lines so the reveal reads left-to-right, top-to-bottom.</summary>
            public int order;
        }

        private static readonly List<string> lineBuffer = new List<string>();
        private static readonly StringBuilder wrapBuilder = new StringBuilder();

        private Piece[] pieces;

        public ITextFace Face { get; private set; }
        public int FontSize { get; private set; }
        /// <summary>Widest laid-out line, in pixels.</summary>
        public float Width { get; private set; }
        public int LineCount { get; private set; }
        public float LineHeight { get; private set; }
        /// <summary>Total block height, in pixels.</summary>
        public float Height => LineHeight * LineCount;
        /// <summary>Number of stagger slots — one more than the last piece's order.</summary>
        public int OrderCount { get; private set; }

        public bool Valid => pieces != null && pieces.Length > 0 && Face != null;

        // ── Building ────────────────────────────────────────────────────────

        /// <summary>
        /// Lays out <paramref name="text"/> at the largest size that fits
        /// <paramref name="maxWidth"/> in at most <paramref name="maxLines"/>
        /// lines, shrinking no further than <paramref name="minFontSize"/>.
        ///
        /// Auto-fitting is not a nicety: dungeon names run from "Demon Gate" to
        /// "Mechanoid Command Node" and taglines from 36 to 141 characters, so a
        /// fixed size would either clip the long ones or waste the frame on the
        /// short ones.
        ///
        /// A face that cannot render the string — a translation outside the
        /// baked character set — is swapped for RimWorld's font rather than
        /// drawing holes.
        /// </summary>
        public static AnimatedTextRun Build(string text, ITextFace face, int maxFontSize, int minFontSize,
                                            TextGranularity granularity, float maxWidth, int maxLines,
                                            float lineSpacing = 1.24f)
        {
            var run = new AnimatedTextRun();
            if (string.IsNullOrEmpty(text) || face == null)
            {
                run.pieces = new Piece[0];
                return run;
            }

            text = text.Trim();
            if (!face.Supports(text))
                face = DungeonFonts.Fallback;

            maxLines = Mathf.Max(1, maxLines);
            minFontSize = Mathf.Clamp(minFontSize, 6, maxFontSize);

            int size = maxFontSize;
            List<string> lines = Wrap(text, face, size, maxWidth, maxLines);

            // Shrink until it fits. The first step jumps straight to the size
            // the width ratio implies, so this normally takes one or two passes
            // rather than a descent from 132 one pixel at a time.
            for (int guard = 0; guard < 24; guard++)
            {
                float widest = WidestLine(lines, face, size);
                bool tooWide = widest > maxWidth + 0.5f;
                bool tooTall = lines.Count > maxLines;
                if (!tooWide && !tooTall)
                    break;
                if (size <= minFontSize)
                    break;

                int next = tooTall
                    ? Mathf.FloorToInt(size * 0.93f)                    // fewer lines: shave and re-wrap
                    : Mathf.FloorToInt(size * maxWidth / widest);

                size = Mathf.Clamp(Mathf.Min(next, size - 1), minFontSize, maxFontSize);
                lines = Wrap(text, face, size, maxWidth, maxLines);
            }

            run.Face = face;
            run.FontSize = size;
            run.LineCount = lines.Count;
            run.LineHeight = size * lineSpacing;
            run.Width = WidestLine(lines, face, size);
            run.BuildPieces(lines, face, size, granularity);
            return run;
        }

        private void BuildPieces(List<string> lines, ITextFace face, int size, TextGranularity granularity)
        {
            var list = new List<Piece>(64);
            int order = 0;

            for (int li = 0; li < lines.Count; li++)
            {
                string line = lines[li];
                float left = -face.Measure(line, size) * 0.5f;    // runs are centre-anchored
                float baseline = li * LineHeight;
                float pen = 0f;

                if (granularity == TextGranularity.Character)
                {
                    for (int i = 0; i < line.Length; i++)
                    {
                        float advance = face.Advance(line, i, size);
                        if (line[i] != ' ')
                        {
                            list.Add(new Piece
                            {
                                text = line[i].ToString(),
                                x = left + pen,
                                baseline = baseline,
                                w = advance,
                                order = order,
                            });
                        }
                        // Spaces still take a slot: the pause at a word boundary
                        // is what makes a per-character reveal read as words
                        // rather than as ticker tape.
                        order++;
                        pen += advance;
                    }
                }
                else
                {
                    int cursor = 0;
                    while (cursor < line.Length)
                    {
                        if (line[cursor] == ' ')
                        {
                            pen += face.Advance(line, cursor, size);
                            cursor++;
                            continue;
                        }

                        int end = line.IndexOf(' ', cursor);
                        if (end < 0)
                            end = line.Length;

                        float wordStart = pen;
                        for (int i = cursor; i < end; i++)
                            pen += face.Advance(line, i, size);

                        list.Add(new Piece
                        {
                            text = line.Substring(cursor, end - cursor),
                            x = left + wordStart,
                            baseline = baseline,
                            w = pen - wordStart,
                            order = order++,
                        });
                        cursor = end;
                    }
                }
            }

            pieces = list.ToArray();
            OrderCount = order;
        }

        // ── Drawing ─────────────────────────────────────────────────────────

        /// <summary>
        /// Seconds from the run's own t=0 until every piece has visually
        /// arrived. Derived from the tuning rather than hard-coded, so retuning
        /// a spring cannot leave the card cutting itself off mid-reveal.
        /// </summary>
        public float RevealDuration(TextReveal reveal)
        {
            float perPiece = Mathf.Max(reveal.fade, ArriveTime(reveal.omega, reveal.zeta));
            return reveal.delay + reveal.stagger * Mathf.Max(0, OrderCount - 1) + perPiece;
        }

        /// <summary>
        /// Draws the run centred on <paramref name="centerX"/> with its first
        /// line sitting on <paramref name="firstBaselineY"/>.
        ///
        /// A zero <paramref name="shadowOffset"/> skips the shadow pass. The
        /// shadow matters: this card floats over whatever terrain the dungeon
        /// generated, and white text on snow or pale marble is unreadable
        /// without one.
        /// </summary>
        public void Draw(float centerX, float firstBaselineY, float t, TextReveal reveal, Color color,
                         float masterAlpha, Color shadowColor, Vector2 shadowOffset)
        {
            if (!Valid || masterAlpha <= 0.002f)
                return;

            Matrix4x4 baseMatrix = GUI.matrix;
            bool drawShadow = shadowOffset != Vector2.zero && shadowColor.a > 0f;
            // Caps sit roughly this far above the baseline, so scaling about it
            // grows a glyph from its middle instead of from its feet.
            float inkCentre = Face.AscentPx(FontSize) * 0.42f;

            try
            {
                for (int i = 0; i < pieces.Length; i++)
                {
                    Piece p = pieces[i];
                    float local = Tween.LocalTime(t, reveal.delay, reveal.stagger, p.order);
                    if (local <= 0f)
                        continue;

                    float progress = Tween.Spring01(local, reveal.omega, reveal.zeta);
                    float alpha = Tween.EaseOutCubic(reveal.fade > 0f ? local / reveal.fade : 1f) * masterAlpha;
                    if (alpha <= 0.002f)
                        continue;

                    // progress overshoots past 1, so rise goes briefly negative
                    // and the glyph settles back down onto its mark.
                    float x = centerX + p.x;
                    float y = firstBaselineY + p.baseline + (1f - progress) * reveal.rise;

                    GUI.matrix = baseMatrix;
                    if (reveal.scaleFrom != 1f)
                    {
                        float scale = Mathf.Max(0.01f, Mathf.LerpUnclamped(reveal.scaleFrom, 1f, progress));
                        GUIUtility.ScaleAroundPivot(new Vector2(scale, scale),
                                                    new Vector2(x + p.w * 0.5f, y - inkCentre));
                    }

                    if (drawShadow)
                    {
                        Face.Draw(p.text, x + shadowOffset.x, y + shadowOffset.y, FontSize,
                                  new Color(shadowColor.r, shadowColor.g, shadowColor.b, shadowColor.a * alpha));
                    }

                    Face.Draw(p.text, x, y, FontSize, new Color(color.r, color.g, color.b, color.a * alpha));
                }
            }
            finally
            {
                GUI.matrix = baseMatrix;
            }
        }

        // ── Layout helpers ──────────────────────────────────────────────────

        private static float WidestLine(List<string> lines, ITextFace face, int size)
        {
            float widest = 0f;
            for (int i = 0; i < lines.Count; i++)
                widest = Mathf.Max(widest, face.Measure(lines[i], size));
            return widest;
        }

        /// <summary>
        /// Greedy word wrap. Once <paramref name="maxLines"/> is reached the
        /// remainder is packed onto the last line rather than dropped — the fit
        /// loop shrinks the size on the next pass, and a visibly over-wide line
        /// is a better failure than a silently truncated sentence.
        /// </summary>
        private static List<string> Wrap(string text, ITextFace face, int size, float maxWidth, int maxLines)
        {
            lineBuffer.Clear();
            wrapBuilder.Length = 0;

            string[] words = text.Split(' ');
            for (int i = 0; i < words.Length; i++)
            {
                string word = words[i];
                if (word.Length == 0)
                    continue;

                if (wrapBuilder.Length == 0)
                {
                    wrapBuilder.Append(word);
                    continue;
                }

                int mark = wrapBuilder.Length;
                wrapBuilder.Append(' ').Append(word);
                if (face.Measure(wrapBuilder.ToString(), size) > maxWidth && lineBuffer.Count < maxLines - 1)
                {
                    wrapBuilder.Length = mark;
                    lineBuffer.Add(wrapBuilder.ToString());
                    wrapBuilder.Length = 0;
                    wrapBuilder.Append(word);
                }
            }

            if (wrapBuilder.Length > 0)
                lineBuffer.Add(wrapBuilder.ToString());
            if (lineBuffer.Count == 0)
                lineBuffer.Add(text);

            return lineBuffer;
        }

        /// <summary>
        /// When a spring is close enough to its target to look stopped. Not the
        /// mathematical settle time — that is several times longer and would
        /// stretch every intro by a second of nothing visibly happening.
        /// </summary>
        private static float ArriveTime(float omega, float zeta)
        {
            omega = Mathf.Max(0.01f, omega);
            zeta = Mathf.Clamp(zeta, 0.01f, 1f);
            return 3.9f / (zeta * omega);   // residual envelope down to ~2%
        }
    }
}
