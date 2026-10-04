using System.Collections.Generic;
using IsekaiLeveling.MobRanking;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// The dungeon's name over the gate on hover — as a MINIATURE OF THE ENTRY
    /// CARD, not a separate piece of UI.
    ///
    /// Same rank container art, same Display face, same character-by-character
    /// spring reveal, same banner unfurl, and the same layout: rank on the
    /// ribbon, name beneath it. The proportions below are the card's own
    /// numbers scaled by one factor, so the two cannot drift apart — retuning
    /// the card's geometry moves this too.
    ///
    /// That consistency is the point. A player sees this label a second before
    /// the card plays, and they should read as the same object arriving twice
    /// rather than as two designers who never spoke.
    ///
    /// WHY A MAP COMPONENT. Map.FillComponents instantiates every MapComponent
    /// subclass on every map, so this runs on the colony map where gates
    /// actually stand. Thing.DrawGUIOverlay was the obvious hook but is not
    /// called for buildings at every zoom, and the name needs to work when you
    /// are looking at the whole base.
    /// </summary>
    public class MapComponent_GateLabels : MapComponent
    {
        public MapComponent_GateLabels(Map map) : base(map)
        {
        }

        // NO STATIC TEXTURE CREATION HERE, deliberately. A static field
        // initialiser runs when Map.FillComponents first touches this type,
        // which on a save load happens on the LOADING THREAD — and Unity
        // refuses to create a texture off the main thread. The banner comes
        // from DungeonCardArt, which is [StaticConstructorOnStartup] and
        // therefore already loaded on the main thread before any map exists.

        // ── Geometry, all derived from the entry card ───────────────────────

        /// <summary>
        /// How much smaller than the entry card this is. Every dimension below
        /// is a card constant times this, so there is exactly one number to
        /// change if the label wants to be bigger: 0.30, times the player's
        /// label-size setting (Interface tab).
        /// </summary>
        private static float Mini => 0.30f * IsekaiDungeonsSettings.Current.gateLabelScale;

        // The card's own banner metrics (DungeonTitleCard: 215x122 native, rank
        // baseline at 81, banner centre at 60.5).
        private const float CardBannerW = 215f;
        private const float CardBannerH = 122f;
        private const float CardRankBaseline = 81f;
        private const int CardRankFontSize = 58;
        private const int CardTitleFontSize = 132;
        private const float CardRankMaxWidth = 168f;

        private static float BannerW => CardBannerW * Mini;
        private static float BannerH => CardBannerH * Mini;
        private static int RankFontSize => Mathf.Max(8, Mathf.RoundToInt(CardRankFontSize * Mini));
        // The title is scaled down harder than the banner: at card proportions
        // a dungeon name would be wider than the gate it labels.
        private static int NameFontSize => Mathf.Max(10, Mathf.RoundToInt(CardTitleFontSize * Mini * 0.62f));

        /// <summary>Gap between the bottom of the ribbon and the name's baseline.</summary>
        private const float NameGap = 6f;

        /// <summary>
        /// Clear space between the top edge of the drawn portal and the label,
        /// IN WORLD CELLS.
        ///
        /// World units, not pixels: GenMapUI.LabelDrawPosFor takes a world Z
        /// offset and projects it, so the gap survives zooming and tracks the
        /// gate's DRAWN SIZE. A Nation gate renders at 11.5 cells, and a fixed
        /// pixel offset put the name inside the portal.
        /// </summary>
        private const float ClearanceCells = 0.55f;

        // ── Timing, also the card's ─────────────────────────────────────────

        private const float FadeSeconds = 0.16f;
        private const float BannerOmega = 15f, BannerZeta = 0.55f;
        private const float BannerFade = 0.22f;

        /// <summary>Character reveal, quicker than the card's because it chases the cursor.</summary>
        private static readonly TextReveal Reveal =
            TextReveal.Make(delay: 0f, stagger: 0.014f, rise: 5f, scaleFrom: 0.72f,
                            omega: 20f, zeta: 0.7f, fade: 0.09f);

        private sealed class HoverState
        {
            public float progress;        // 0..1, how faded in the label is
            public float hoverStart;      // realtime the reveal began
            public AnimatedTextRun nameRun;
            public AnimatedTextRun rankRun;
            public string builtName;
            public string builtRank;
            public int builtNameSize;
            public int builtRankSize;
        }

        /// <summary>
        /// The frame the fade last advanced on. OnGUI runs several times per
        /// frame (layout, repaint, every input event), so integrating dt on
        /// each pass ran the fade 2-4x fast, faster still while the mouse
        /// moved. It now advances once per rendered frame.
        /// </summary>
        private int lastFadeFrame = -1;

        private readonly Dictionary<int, HoverState> states = new Dictionary<int, HoverState>();

        public override void MapComponentOnGUI()
        {
            if (Find.CurrentMap != map)
                return;
            if (Find.WindowStack.WindowsForcePause)
                return;
            if (!IsekaiDungeonsSettings.Current.gateLabelsEnabled)
                return;

            List<Building_DungeonGate> gates = DungeonTracker.GatesOn(map);
            if (gates == null || gates.Count == 0)
            {
                if (states.Count > 0)
                    states.Clear();
                return;
            }

            IntVec3 mouse = UI.MouseCell();
            bool advance = Time.frameCount != lastFadeFrame;
            lastFadeFrame = Time.frameCount;
            float dt = advance ? Mathf.Min(Time.deltaTime, 0.1f) : 0f;   // clamp so an alt-tab does not snap it
            float now = Tween.Now;

            for (int i = 0; i < gates.Count; i++)
            {
                Building_DungeonGate gate = gates[i];
                if (gate == null || !gate.Spawned)
                    continue;

                if (!states.TryGetValue(gate.thingIDNumber, out HoverState st))
                {
                    st = new HoverState();
                    states[gate.thingIDNumber] = st;
                }

                bool hovered = gate.OccupiedRect().Contains(mouse);
                if (hovered && st.progress <= 0.001f)
                    st.hoverStart = now;      // restart the reveal on each entry

                st.progress = Mathf.Clamp01(st.progress + (hovered ? dt : -dt) / FadeSeconds);
                if (st.progress > 0.001f)
                    DrawLabel(gate, st, now);
            }
        }

        private static void DrawLabel(Building_DungeonGate gate, HoverState st, float now)
        {
            string name = gate.kindDef?.label?.CapitalizeFirst() ?? "dungeon";
            string rank = MobRankUtility.GetRankString(gate.rank);

            // Rebuild only when the words change — Build lays out every glyph.
            // ...or when the label-scale setting changed the font size.
            if (st.nameRun == null || st.builtName != name || st.builtNameSize != NameFontSize)
            {
                st.builtName = name;
                st.builtNameSize = NameFontSize;
                st.nameRun = AnimatedTextRun.Build(name, DungeonFonts.Display, NameFontSize,
                                                   Mathf.Max(9, NameFontSize - 6),
                                                   TextGranularity.Character, 320f, 1, 1.1f);
            }
            if (st.rankRun == null || st.builtRank != rank || st.builtRankSize != RankFontSize)
            {
                st.builtRank = rank;
                st.builtRankSize = RankFontSize;
                st.rankRun = AnimatedTextRun.Build(rank, DungeonFonts.Display, RankFontSize,
                                                   Mathf.Max(7, RankFontSize / 2),
                                                   TextGranularity.Character,
                                                   CardRankMaxWidth * Mini, 1, 1.15f);
            }
            if (st.nameRun == null || !st.nameRun.Valid)
                return;

            float alpha = Tween.EaseOutCubic(st.progress);
            float t = now - st.hoverStart;

            // Half the drawn portal, plus clearance — so the label clears the
            // art at every rank instead of sitting inside a big gate.
            float worldOffsetZ = GateVisuals.SizeFor(gate.rank) * 0.5f + ClearanceCells;
            Vector2 anchor = GenMapUI.LabelDrawPosFor(gate, worldOffsetZ);

            Color oldColor = GUI.color;
            TextAnchor oldAnchor = Text.Anchor;
            GameFont oldFont = Text.Font;
            Matrix4x4 baseMatrix = GUI.matrix;

            // The whole label sits above the anchor: name at the bottom, ribbon
            // above it, exactly as the card stacks them.
            float nameBaseline = anchor.y - NameGap;
            float bannerBottom = nameBaseline - NameFontSize;
            var bannerRect = new Rect(anchor.x - BannerW / 2f, bannerBottom - BannerH, BannerW, BannerH);

            // ── the ribbon ──────────────────────────────────────────────────
            if (DungeonCardArt.Banner != null)
            {
                // Same unfurl as the card: snaps open horizontally on a spring
                // while fading up.
                float p = Tween.Spring01(t, BannerOmega, BannerZeta);
                float sx = Mathf.LerpUnclamped(0.60f, 1f, p);
                float sy = Mathf.LerpUnclamped(0.80f, 1f, p);
                float bannerAlpha = Mathf.Clamp01(t / BannerFade) * alpha;

                GUI.matrix = baseMatrix;
                GUIUtility.ScaleAroundPivot(new Vector2(sx, sy), bannerRect.center);
                GUI.color = new Color(1f, 1f, 1f, bannerAlpha);
                GUI.DrawTexture(bannerRect, DungeonCardArt.Banner);
                GUI.matrix = baseMatrix;
            }

            // ── the rank, on the ribbon ─────────────────────────────────────
            if (st.rankRun != null && st.rankRun.Valid)
            {
                // The card puts the rank baseline 81px down a 122px banner;
                // keeping that ratio is what makes this read as the same art.
                float rankBaseline = bannerRect.y + BannerH * (CardRankBaseline / CardBannerH);
                st.rankRun.Draw(bannerRect.center.x, rankBaseline, t, Reveal,
                                Color.white, alpha, Color.clear, Vector2.zero);
            }

            // ── the name, beneath ───────────────────────────────────────────
            st.nameRun.Draw(anchor.x, nameBaseline, t, Reveal,
                            Color.white, alpha,
                            new Color(0f, 0f, 0f, 0.85f), new Vector2(1.5f, 1.5f));

            // Restore everything. Leaking GUI.color, the matrix or the anchor
            // tints, skews or misaligns every widget drawn after this one.
            GUI.matrix = baseMatrix;
            GUI.color = oldColor;
            Text.Anchor = oldAnchor;
            Text.Font = oldFont;
        }
    }
}
