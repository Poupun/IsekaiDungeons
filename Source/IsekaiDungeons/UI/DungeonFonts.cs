using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// The mod's typefaces, and the GUIStyle cache the fallback face draws with.
    ///
    /// The faces are glyph atlases baked by Tools/make_font_atlas.py rather than
    /// Unity Fonts, because a RimWorld player build cannot be handed a font file
    /// at all — see <see cref="BitmapFont"/> for the three routes that were
    /// tried and why each one fails. Anything that cannot be loaded, or any
    /// string the atlas has no glyphs for, falls back to RimWorld's own font;
    /// the card lays out and animates identically either way.
    /// </summary>
    public static class DungeonFonts
    {
        /// <summary>Display face — the rank ribbon and the dungeon name.</summary>
        private const string DisplayAtlas = "Display";
        /// <summary>Body face — the description line.</summary>
        private const string BodyAtlas = "Body";

        private static ITextFace display;
        private static ITextFace body;
        private static ITextFace fallback;
        private static bool initialized;

        /// <summary>Styles are cached per (font, size, anchor) — rebuilding one per frame allocates for nothing.</summary>
        private static readonly Dictionary<int, GUIStyle> styleCache = new Dictionary<int, GUIStyle>();

        public static ITextFace Display { get { EnsureInit(); return display; } }
        public static ITextFace Body { get { EnsureInit(); return body; } }

        /// <summary>RimWorld's own font. Always available, and always able to render anything the game can.</summary>
        public static ITextFace Fallback { get { EnsureInit(); return fallback; } }

        /// <summary>True when both atlases loaded. False means the card is drawing in RimWorld's font.</summary>
        public static bool CustomFontsActive =>
            Display is BitmapTextFace && Body is BitmapTextFace;

        private static void EnsureInit()
        {
            if (initialized)
                return;
            initialized = true;

            fallback = new GuiTextFace(Text.fontStyles[(int)GameFont.Medium].font);

            string dir = FontDirectory();
            if (dir == null)
            {
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Could not locate the mod's Fonts folder; " +
                            $"dungeon titles will use RimWorld's default font.");
                display = fallback;
                body = fallback;
                return;
            }

            display = Wrap(BitmapFont.Load(dir, DisplayAtlas));
            body = Wrap(BitmapFont.Load(dir, BodyAtlas));
        }

        private static ITextFace Wrap(BitmapFont font) =>
            font != null && font.Valid ? (ITextFace)new BitmapTextFace(font) : fallback;

        private static string FontDirectory()
        {
            ModContentPack content = LoadedModManager.GetMod<IsekaiDungeonsMod>()?.Content;
            if (content == null)
                return null;
            string dir = Path.Combine(content.RootDir, "Fonts");
            return Directory.Exists(dir) ? dir : null;
        }

        /// <summary>
        /// A cached, padding-free GUIStyle at a pixel size, used only by the
        /// fallback face.
        ///
        /// Built from scratch rather than cloned from Verse.Text.fontStyles on
        /// purpose: those carry RimWorld's own padding, wrapping and rich-text
        /// settings, and mutating them would restyle every label in the game
        /// including other mods'.
        /// </summary>
        public static GUIStyle Style(Font font, int pixelSize, TextAnchor anchor = TextAnchor.UpperLeft)
        {
            int key = (font != null ? font.GetHashCode() : 0) * 397 ^ pixelSize * 31 ^ (int)anchor << 20;
            if (styleCache.TryGetValue(key, out GUIStyle cached))
                return cached;

            var style = new GUIStyle
            {
                font = font ?? Text.fontStyles[(int)GameFont.Medium].font,
                fontSize = pixelSize,
                alignment = anchor,
                wordWrap = false,
                richText = false,
                // Pieces are placed by measurement, so padding or clipping of
                // our own would fight the layout instead of helping it.
                clipping = TextClipping.Overflow,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
                border = new RectOffset(0, 0, 0, 0),
                overflow = new RectOffset(0, 0, 0, 0),
                contentOffset = Vector2.zero,
            };
            style.normal.textColor = Color.white;

            styleCache[key] = style;
            return style;
        }
    }
}
