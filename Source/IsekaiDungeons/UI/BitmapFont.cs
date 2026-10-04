using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// A typeface baked to a glyph atlas by Tools/make_font_atlas.py, drawn as
    /// textured quads.
    ///
    /// WHY NOT JUST GIVE UNITY THE FONT FILE: in a RimWorld (player) build you
    /// cannot. <c>Font.CreateDynamicFontFromOSFont</c> resolves a family name
    /// against a list Unity builds at startup, which a mod's file is never on —
    /// and it still "succeeds", returning a Font named after whatever you asked
    /// for that renders in a substitute face. <c>new Font(path)</c> reaches
    /// <c>Internal_CreateFontFromPath</c>, which is editor-only: in a player it
    /// hands back a hollow object with dynamic=false, no material, ascent 0 and
    /// uninitialised garbage in lineHeight. That was verified against a known
    /// Windows TTF as a control, so it is the API and not the file. AssetBundles
    /// work but need the matching Unity Editor to build.
    ///
    /// So the glyphs are rasterised offline and drawn here. Nothing about this
    /// depends on Unity's font system, it behaves identically on every
    /// platform, and the metrics can be checked against the design before the
    /// game ever runs.
    ///
    /// EVERY METRIC IS IN EM UNITS — multiply by the pixel size to use it. One
    /// bake therefore serves every size the card draws at. The atlas is baked
    /// above the largest size in use so it is only ever downscaled.
    /// </summary>
    public sealed class BitmapFont
    {
        public struct Glyph
        {
            /// <summary>Where the glyph sits in the atlas, in Unity's bottom-left texture coordinates.</summary>
            public Rect uv;
            /// <summary>Quad size in em.</summary>
            public float width, height;
            /// <summary>Left side bearing, em.</summary>
            public float bearingX;
            /// <summary>Baseline to ink top, em, positive up.</summary>
            public float bearingY;
            /// <summary>Pen advance, em.</summary>
            public float advance;
            public bool HasInk => width > 0f && height > 0f;
        }

        private readonly Dictionary<char, Glyph> glyphs = new Dictionary<char, Glyph>(256);
        /// <summary>Key is (first &lt;&lt; 16) | second. Adjustment in em.</summary>
        private readonly Dictionary<int, float> kerning = new Dictionary<int, float>(8192);

        public Texture2D Atlas { get; private set; }
        public string Face { get; private set; }
        /// <summary>Baseline to the top of the em box, em.</summary>
        public float Ascent { get; private set; }
        /// <summary>Baseline to the bottom, em, negative.</summary>
        public float Descent { get; private set; }
        public float LineHeight { get; private set; }

        public bool Valid => Atlas != null && glyphs.Count > 0;

        /// <summary>
        /// Loads "<paramref name="name"/>.fnt" and "<paramref name="name"/>.png"
        /// from a directory. Returns null and logs rather than throwing — a
        /// missing atlas must degrade to RimWorld's font, not break the UI pass.
        /// </summary>
        public static BitmapFont Load(string dir, string name)
        {
            string fnt = Path.Combine(dir, name + ".fnt");
            string png = Path.Combine(dir, name + ".png");
            if (!File.Exists(fnt) || !File.Exists(png))
            {
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Font atlas \"{name}\" is incomplete " +
                            $"(need both {name}.fnt and {name}.png in {dir}).");
                return null;
            }

            var font = new BitmapFont { Face = name };
            try
            {
                if (!font.ParseMetrics(fnt))
                    return null;
                if (!font.LoadAtlas(png))
                    return null;
            }
            catch (Exception ex)
            {
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Failed to load font atlas \"{name}\": {ex}");
                return null;
            }

            Log.Message($"{IsekaiDungeonsMod.LogPrefix} Loaded font atlas \"{name}\" " +
                        $"({font.glyphs.Count} glyphs, {font.kerning.Count} kern pairs, " +
                        $"{font.Atlas.width}x{font.Atlas.height}).");
            return font;
        }

        private bool ParseMetrics(string path)
        {
            int atlasW = 0, atlasH = 0;
            float bake = 0f;
            var pending = new List<int[]>();          // glyph atlas rects, resolved once the size is known
            var pendingChars = new List<char>();

            foreach (string raw in File.ReadAllLines(path))
            {
                if (raw.Length == 0 || raw[0] == '#')
                    continue;
                string[] p = raw.Split(' ');
                switch (p[0])
                {
                    case "bake": bake = F(p[1]); break;
                    case "atlas": atlasW = I(p[1]); atlasH = I(p[2]); break;
                    case "ascent": Ascent = F(p[1]); break;
                    case "descent": Descent = F(p[1]); break;
                    case "lineheight": LineHeight = F(p[1]); break;

                    case "g":
                        {
                            int code = I(p[1]);
                            if (code < 0 || code > char.MaxValue)
                                break;
                            var g = new Glyph
                            {
                                bearingX = F(p[6]),
                                bearingY = F(p[7]),
                                advance = F(p[8]),
                            };
                            glyphs[(char)code] = g;
                            pendingChars.Add((char)code);
                            pending.Add(new[] { I(p[2]), I(p[3]), I(p[4]), I(p[5]) });
                            break;
                        }

                    case "k":
                        {
                            int a = I(p[1]), b = I(p[2]);
                            if (a >= 0 && a <= char.MaxValue && b >= 0 && b <= char.MaxValue)
                                kerning[a << 16 | b] = F(p[3]);
                            break;
                        }
                }
            }

            if (atlasW <= 0 || atlasH <= 0 || bake <= 0f || glyphs.Count == 0)
            {
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Font metrics {Path.GetFileName(path)} are missing a header.");
                return false;
            }

            for (int i = 0; i < pendingChars.Count; i++)
            {
                int[] r = pending[i];
                char c = pendingChars[i];
                Glyph g = glyphs[c];
                g.width = r[2] / bake;
                g.height = r[3] / bake;
                // The generator writes top-left pixel rows; Unity's texture
                // origin is bottom-left, so the row range flips here.
                g.uv = new Rect(r[0] / (float)atlasW,
                                1f - (r[1] + r[3]) / (float)atlasH,
                                r[2] / (float)atlasW,
                                r[3] / (float)atlasH);
                glyphs[c] = g;
            }
            return true;
        }

        private bool LoadAtlas(string png)
        {
            // Loaded directly rather than through ContentFinder so nothing else
            // decides the filtering or compression: a glyph atlas must not be
            // DXT-compressed, and it must not join RimWorld's own texture atlas.
            //
            // MIPMAPPED on purpose. The card draws between roughly 0.4x and
            // 0.95x of the bake size depending on the player's resolution, and
            // plain bilinear minification of a glyph edge aliases badly at the
            // low end. The generator leaves a six-pixel gutter around every
            // glyph so the lower mips cannot bleed neighbours into each other,
            // and the negative bias keeps Unity on the sharper mip when it is
            // undecided.
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true)
            {
                filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Clamp,
                mipMapBias = -0.3f,
                anisoLevel = 0,
                // Runtime-created assets are fair game for
                // Resources.UnloadUnusedAssets, which RimWorld calls on game
                // and map transitions.
                hideFlags = HideFlags.DontUnloadUnusedAsset,
                name = "IsekaiDungeons_Font_" + Face,
            };
            if (!tex.LoadImage(File.ReadAllBytes(png)))
            {
                Log.Warning($"{IsekaiDungeonsMod.LogPrefix} Could not decode font atlas {Path.GetFileName(png)}.");
                return false;
            }
            Atlas = tex;
            return true;
        }

        // Invariant culture on purpose: the metrics files use '.' as the decimal
        // separator, and on a machine with a comma locale the current culture
        // would parse "0.5" as 5 and every glyph would land in the wrong place.
        private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
        private static int I(string s) => int.Parse(s, CultureInfo.InvariantCulture);

        // ── Measurement ─────────────────────────────────────────────────────

        public bool Has(char c) => glyphs.ContainsKey(c);

        /// <summary>True when every character can be drawn — a run with a hole in it should use a different face, not draw gaps.</summary>
        public bool Supports(string s)
        {
            if (s == null)
                return true;
            for (int i = 0; i < s.Length; i++)
            {
                if (!glyphs.ContainsKey(s[i]))
                    return false;
            }
            return true;
        }

        public float Kern(char a, char b)
        {
            return kerning.TryGetValue(a << 16 | b, out float v) ? v : 0f;
        }

        /// <summary>Advance of <paramref name="index"/> including its kern against the next character, in pixels.</summary>
        public float Advance(string s, int index, float pixelSize)
        {
            if (s == null || index < 0 || index >= s.Length)
                return 0f;
            if (!glyphs.TryGetValue(s[index], out Glyph g))
                return 0f;
            float adv = g.advance;
            if (index + 1 < s.Length)
                adv += Kern(s[index], s[index + 1]);
            return adv * pixelSize;
        }

        public float Measure(string s, float pixelSize)
        {
            if (string.IsNullOrEmpty(s))
                return 0f;
            float total = 0f;
            for (int i = 0; i < s.Length; i++)
                total += Advance(s, i, pixelSize);
            return total;
        }

        // ── Drawing ─────────────────────────────────────────────────────────

        /// <summary>
        /// Draws a run with its pen starting at <paramref name="x"/> and its
        /// baseline at <paramref name="baselineY"/>. Colour comes from GUI.color
        /// — the atlas is white with coverage in alpha, so tinting is a plain
        /// multiply and needs no material of its own.
        /// </summary>
        public void Draw(string s, float x, float baselineY, float pixelSize, Color color)
        {
            if (Atlas == null || string.IsNullOrEmpty(s))
                return;

            Color enter = GUI.color;
            GUI.color = color;
            float pen = x;
            for (int i = 0; i < s.Length; i++)
            {
                if (glyphs.TryGetValue(s[i], out Glyph g) && g.HasInk)
                {
                    var rect = new Rect(pen + g.bearingX * pixelSize,
                                        baselineY - g.bearingY * pixelSize,
                                        g.width * pixelSize,
                                        g.height * pixelSize);
                    GUI.DrawTextureWithTexCoords(rect, Atlas, g.uv);
                }
                pen += Advance(s, i, pixelSize);
            }
            GUI.color = enter;
        }
    }
}
