using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Something the text animator can measure and draw one piece at a time.
    ///
    /// Two implementations: the mod's baked glyph atlas, and RimWorld's own
    /// font. The abstraction exists so the fallback is a real, equally
    /// supported path rather than a degraded special case — the card lays out
    /// and animates identically either way, and only the letterforms differ.
    ///
    /// Everything is expressed in BASELINES rather than boxes. A baseline is
    /// what a typeface is actually positioned by, it is what the design was
    /// measured in, and it sidesteps the line-box-versus-ink guesswork that
    /// box-based placement needs.
    /// </summary>
    public interface ITextFace
    {
        /// <summary>False when a character is missing, so the caller can pick another face instead of drawing gaps.</summary>
        bool Supports(string s);

        /// <summary>Total advance width in pixels, kerning included.</summary>
        float Measure(string s, int pixelSize);

        /// <summary>Advance of one character, including its kern against the next.</summary>
        float Advance(string s, int index, int pixelSize);

        /// <summary>Baseline to the top of the em box, in pixels.</summary>
        float AscentPx(int pixelSize);

        /// <summary>Draws with the pen at <paramref name="x"/> and the baseline at <paramref name="baselineY"/>.</summary>
        void Draw(string s, float x, float baselineY, int pixelSize, Color color);
    }

    /// <summary>The mod's own typeface, baked to an atlas. See <see cref="BitmapFont"/> for why this is not a Unity Font.</summary>
    public sealed class BitmapTextFace : ITextFace
    {
        private readonly BitmapFont font;

        public BitmapTextFace(BitmapFont font)
        {
            this.font = font;
        }

        public bool Supports(string s) => font.Supports(s);
        public float Measure(string s, int pixelSize) => font.Measure(s, pixelSize);
        public float Advance(string s, int index, int pixelSize) => font.Advance(s, index, pixelSize);
        public float AscentPx(int pixelSize) => font.Ascent * pixelSize;

        public void Draw(string s, float x, float baselineY, int pixelSize, Color color) =>
            font.Draw(s, x, baselineY, pixelSize, color);
    }

    /// <summary>
    /// RimWorld's own font, used when an atlas is missing or cannot render a
    /// string (a translation outside the baked character set, most likely).
    ///
    /// Measures CUMULATIVE PREFIXES rather than individual characters: drawing
    /// "D", "e", "m" separately would throw away every kerning pair, so each
    /// character's box is the difference between consecutive prefix widths and
    /// therefore lands exactly where the shaper would have put it.
    /// </summary>
    public sealed class GuiTextFace : ITextFace
    {
        /// <summary>
        /// Unity drops a trailing space when measuring, which would silently
        /// collapse every inter-word gap in a prefix measurement. Appending a
        /// narrow glyph no font kerns against — and subtracting its width —
        /// makes any prefix measurable.
        /// </summary>
        private const string Sentinel = "|";

        private static readonly GUIContent measure = new GUIContent();

        private readonly Font font;

        public GuiTextFace(Font font)
        {
            this.font = font;
        }

        /// <summary>RimWorld's font covers every language the game ships, so nothing is out of range here.</summary>
        public bool Supports(string s) => true;

        public float Measure(string s, int pixelSize) => Width(s, pixelSize);

        public float Advance(string s, int index, int pixelSize)
        {
            if (s == null || index < 0 || index >= s.Length)
                return 0f;
            return Width(s.Substring(0, index + 1), pixelSize) - Width(s.Substring(0, index), pixelSize);
        }

        public float AscentPx(int pixelSize)
        {
            // Font.ascent is reported for the size the Font was created at, so
            // it has to be turned into a ratio before it means anything at ours.
            if (font != null && font.fontSize > 0 && font.ascent > 0)
                return font.ascent / (float)font.fontSize * pixelSize;
            return pixelSize * 0.8f;
        }

        public void Draw(string s, float x, float baselineY, int pixelSize, Color color)
        {
            if (string.IsNullOrEmpty(s))
                return;
            GUIStyle style = DungeonFonts.Style(font, pixelSize, TextAnchor.UpperLeft);
            Color enter = GUI.color;
            GUI.color = color;
            GUI.Label(new Rect(x, baselineY - AscentPx(pixelSize), Width(s, pixelSize) + pixelSize, pixelSize * 2f),
                      s, style);
            GUI.color = enter;
        }

        private float Width(string s, int pixelSize)
        {
            if (string.IsNullOrEmpty(s))
                return 0f;
            GUIStyle style = DungeonFonts.Style(font, pixelSize, TextAnchor.UpperLeft);
            measure.text = s + Sentinel;
            float withSentinel = style.CalcSize(measure).x;
            measure.text = Sentinel;
            float sentinelOnly = style.CalcSize(measure).x;
            measure.text = string.Empty;
            return withSentinel - sentinelOnly;
        }
    }
}
