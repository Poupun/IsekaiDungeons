using IsekaiLeveling.MobRanking;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    [StaticConstructorOnStartup]
    internal static class DungeonCardArt
    {
        /// <summary>Ribbon behind the rank. 215x122, with the opaque body at texture rows 21-100 and soft shadow around it.</summary>
        public static readonly Texture2D Banner = ContentFinder<Texture2D>.Get("UI/Dungeon_Rank_Container", false);
        public static readonly Texture2D Sparkle = ContentFinder<Texture2D>.Get("UI/Dungeon_Rank_Sparkle", false);
    }

    /// <summary>
    /// The name card that plays when the player arrives in a dungeon.
    ///
    /// This is a direct build of the ISEKAI RPG LEVELING title design: a
    /// chevron ribbon carrying the rank, a sparkle riding each of its edges,
    /// the dungeon's name below it in Americaine Condensed, and a line of the
    /// dungeon's own description under that in Outfit.
    ///
    /// LAYOUT IS EXPRESSED IN THE DESIGN'S OWN COORDINATES. Every constant
    /// below is a pixel position in the 688-wide reference frame, measured off
    /// the design render, and <see cref="RefX"/>/<see cref="RefY"/> map that
    /// frame onto whatever resolution the player is running. Writing the
    /// numbers this way means the design can be re-measured and pasted in
    /// directly; converting them to fractions-of-screen at authoring time
    /// would make every future tweak a guess.
    ///
    /// Presentational and self-contained: it owns a timestamp and three laid-out
    /// text runs, draws from MapComponentOnGUI, and never consumes an input
    /// event — no Widgets that respond to clicks, so the player can keep issuing
    /// orders while it plays. Timing runs on real seconds rather than game
    /// ticks, so the card plays at one speed whether the game is paused or at
    /// 3x. Nothing here is scribed: a card mid-fade is a sub-second visual
    /// state, and replaying it on load would announce a dungeon the player is
    /// already standing in.
    /// </summary>
    public class DungeonTitleCard
    {
        // ── Reference frame (the Figma design, 688 px wide) ─────────────────

        private const float RefWidth = 688f;
        private const float FrameCenterX = 344f;

        /// <summary>Ribbon texture box. Drawn whole; its opaque body lands on rows 21-100 of the frame.</summary>
        private const float BannerX = 237f, BannerY = 0f, BannerW = 215f, BannerH = 122f;
        private const float BannerCenterX = 344.5f;
        private const float BannerTopEdge = 21f, BannerBottomEdge = 100f;
        private const float BannerCenterY = 60.5f;

        /// <summary>Both sparkles sit on the ribbon's centre line, straddling its top and bottom edges.</summary>
        private const float SparkleSize = 24f;

        // Text is positioned by BASELINE, which is what a typeface is actually
        // set by and what these numbers were measured as in the design render.
        // Placing by box centre instead would need the line-box-versus-ink
        // guesswork that baselines make unnecessary.
        private const int RankFontSize = 58;
        private const float RankMaxWidth = 168f;      // stays inside the chevron notches
        /// <summary>Measured: the 'C' sits on y 81 in the design.</summary>
        private const float RankBaseline = 81f;

        private const int TitleFontSize = 132;
        private const int TitleMinFontSize = 64;
        private const float TitleMaxWidth = 656f;
        /// <summary>Measured: title ink runs y 119..213, and "Demon Gate" has no descenders.</summary>
        private const float TitleBaseline = 213f;

        private const int DescFontSize = 35;
        private const int DescMinFontSize = 22;
        private const float DescMaxWidth = 664f;
        /// <summary>Measured: the description's cap 'A' spans y 287..315.</summary>
        private const float DescBaseline = 315f;
        /// <summary>
        /// The design's own line is 43 characters and fits on one. Real
        /// taglines run 36 to 141, so the slot has to wrap. FOUR lines rather
        /// than three because at four, every dungeon in the catalogue renders
        /// at the full design size — at three, the longest quarter had to
        /// shrink, which made the description a visibly different size from
        /// one dungeon to the next.
        /// </summary>
        private const int DescMaxLines = 4;

        // ── Screen placement ────────────────────────────────────────────────

        /// <summary>
        /// Card width is derived from screen HEIGHT, not width: height is what
        /// governs how much of the view the card eats, and it does not change
        /// with the player's aspect ratio. Clamped so the card is neither a
        /// postage stamp on 4K nor half the screen on 1366x768.
        /// </summary>
        private const float ScreenHeightFraction = 0.32f;
        private const float MinCardWidth = 340f;
        private const float MaxCardWidth = 540f;
        /// <summary>Frame top, as a share of screen height. Upper-middle: clear of the top bar, the right-hand alert stack and the bottom UI.</summary>
        private const float TopFraction = 0.15f;
        /// <summary>
        /// Absolute readability floor, in screen pixels. The reference-unit
        /// floors are expressed in the design's 688-wide frame, so once the
        /// card scales down they stop meaning anything — 22 reference units is
        /// 14 real pixels on a scaled-down card.
        /// </summary>
        private const int MinReadablePx = 15;

        // ── Timing (real seconds) ───────────────────────────────────────────

        private const float BannerFade = 0.22f;
        private const float BannerOmega = 15f, BannerZeta = 0.55f;
        private const float SparkleDelay = 0.34f, SparkleStagger = 0.09f;
        private const float SparkleOmega = 19f, SparkleZeta = 0.42f;

        private const float Hold = 2.0f;
        private const float FadeOut = 0.95f;
        /// <summary>Used until the runs have been laid out, which can only happen inside a GUI pass.</summary>
        private const float AssumedTotal = 5.5f;
        /// <summary>Ignore repeat triggers inside this window, so flicking between maps cannot stack cards.</summary>
        private const float RetriggerCooldown = 8f;
        /// <summary>Whole-card lift during the exit, in reference pixels.</summary>
        private const float ExitDrift = 14f;

        // Per-run reveal tuning. The title is the hero: slowest stagger, biggest
        // travel, loosest spring. The description is a supporting line and is
        // revealed per WORD — per-character across 190 characters reads as a
        // stutter, not a reveal.
        private static readonly TextReveal RankReveal =
            TextReveal.Make(delay: 0.18f, stagger: 0.035f, rise: 10f, scaleFrom: 0.55f, omega: 20f, zeta: 0.55f, fade: 0.16f);
        private static readonly TextReveal TitleReveal =
            TextReveal.Make(delay: 0.42f, stagger: 0.042f, rise: 34f, scaleFrom: 0.86f, omega: 15f, zeta: 0.42f, fade: 0.22f);
        private static readonly TextReveal DescReveal =
            TextReveal.Make(delay: 0.92f, stagger: 0.055f, rise: 14f, scaleFrom: 1f, omega: 18f, zeta: 0.75f, fade: 0.26f);

        // ── State ───────────────────────────────────────────────────────────

        private float startTime = -999f;
        private string titleText;
        private string rankText;
        private string descText;
        private Color sparkleColor = Color.white;

        private AnimatedTextRun titleRun;
        private AnimatedTextRun rankRun;
        private AnimatedTextRun descRun;

        /// <summary>Runs are measured in screen pixels, so a resolution change invalidates them.</summary>
        private float builtScale = -1f;
        private bool needsBuild;

        private float introEnd = AssumedTotal - Hold - FadeOut;
        private float totalDuration = AssumedTotal;

        private float scale;
        private float originX, originY;

        public bool Active => Tween.Now - startTime < totalDuration;

        /// <summary>
        /// Starts the card. Safe to call on every map switch — the cooldown
        /// absorbs repeats. Deliberately does NOT lay out the text: this runs
        /// from MapComponentTick, and IMGUI measurement is only valid inside a
        /// GUI pass.
        /// </summary>
        public void Trigger(DungeonKindDef kind, MobRankTier rank) =>
            Trigger(DungeonNarrative.TitleName(kind),
                    DungeonNarrative.TitleRankLine(rank),
                    DungeonNarrative.TitleTagline(kind),
                    rank);

        /// <summary>
        /// The same card with the three lines supplied directly, so a caller
        /// with its own idea of what the place is called — the tower, whose
        /// floors are named "Floor 24 - Village" — can say so without this
        /// class growing a concept of floors.
        /// </summary>
        public void Trigger(string title, string rankLine, string desc, MobRankTier rank)
        {
            if (Tween.Now - startTime < RetriggerCooldown)
                return;

            titleText = title;
            rankText = rankLine;
            descText = desc;

            // The design's ribbon text is white, and it stays white. The rank
            // signal rides the sparkles instead, kept well toward white so they
            // still read as sparkles rather than as coloured blobs.
            sparkleColor = Color.Lerp(MobRankUtility.GetRankColor(rank), Color.white, 0.35f);

            needsBuild = true;
            startTime = Tween.Now;
        }

        /// <summary>
        /// Draws the card. Call from MapComponentOnGUI. Restores GUI.color and
        /// GUI.matrix on every path — leaking either tints or skews unrelated
        /// UI drawn later in the same frame.
        /// </summary>
        public void OnGUI()
        {
            if (!Active || titleText.NullOrEmpty())
                return;

            float t = Tween.Now - startTime;

            float cardWidth = Mathf.Clamp(UI.screenHeight * ScreenHeightFraction, MinCardWidth, MaxCardWidth);
            scale = cardWidth / RefWidth;
            originX = (UI.screenWidth - cardWidth) * 0.5f;
            originY = UI.screenHeight * TopFraction;

            if (needsBuild || !Mathf.Approximately(scale, builtScale))
                BuildRuns();

            // Master envelope: full until the intro has landed plus a hold, then
            // out. The whole card lifts slightly as it goes, so the exit reads
            // as the card leaving rather than the card being switched off.
            float holdEnd = introEnd + Hold;
            float masterAlpha = 1f;
            if (t > holdEnd)
            {
                float k = Mathf.Clamp01((t - holdEnd) / FadeOut);
                masterAlpha = 1f - Tween.EaseInOutCubic(k);
                originY -= ExitDrift * scale * Tween.EaseInCubic(k);
            }
            if (masterAlpha <= 0.002f)
                return;

            Matrix4x4 baseMatrix = GUI.matrix;
            Color enterColor = GUI.color;

            try
            {
                DrawBanner(t, masterAlpha, baseMatrix);
                DrawSparkles(t, masterAlpha, baseMatrix);

                if (rankRun != null && rankRun.Valid)
                {
                    // No shadow: this one sits on the ribbon, which is already
                    // a dark plate behind it.
                    rankRun.Draw(RefX(BannerCenterX), RefY(RankBaseline), t,
                                 Scaled(RankReveal), Color.white, masterAlpha, Color.clear, Vector2.zero);
                }

                if (titleRun != null && titleRun.Valid)
                {
                    // Shadow: the card floats over whatever terrain the dungeon
                    // generated — snow and pale marble among them — and white
                    // text on those is unreadable without one.
                    titleRun.Draw(RefX(FrameCenterX), RefY(TitleBaseline), t,
                                  Scaled(TitleReveal), Color.white, masterAlpha,
                                  new Color(0f, 0f, 0f, 0.55f), new Vector2(0f, 3f * scale));
                }

                if (descRun != null && descRun.Valid)
                {
                    descRun.Draw(RefX(FrameCenterX), RefY(DescBaseline), t,
                                 Scaled(DescReveal), Color.white, masterAlpha,
                                 new Color(0f, 0f, 0f, 0.5f), new Vector2(0f, 2f * scale));
                }
            }
            finally
            {
                GUI.matrix = baseMatrix;
                GUI.color = enterColor;
            }
        }

        // ── Pieces ──────────────────────────────────────────────────────────

        private void BuildRuns()
        {
            needsBuild = false;
            builtScale = scale;

            rankRun = AnimatedTextRun.Build(rankText, DungeonFonts.Display,
                                            Px(RankFontSize), Floor(RankFontSize / 2),
                                            TextGranularity.Character, RankMaxWidth * scale, 1, 1.15f);

            titleRun = AnimatedTextRun.Build(titleText, DungeonFonts.Display,
                                             Px(TitleFontSize), Floor(TitleMinFontSize),
                                             TextGranularity.Character, TitleMaxWidth * scale, 1, 1.10f);

            descRun = AnimatedTextRun.Build(descText, DungeonFonts.Body,
                                            Px(DescFontSize), Floor(DescMinFontSize),
                                            TextGranularity.Word, DescMaxWidth * scale, DescMaxLines, 1.30f);

            // The intro ends when the SLOWEST element has landed, computed from
            // the tuning rather than assumed. Retuning a spring or a stagger
            // then cannot leave the card cutting itself off mid-reveal.
            float end = Tween.SettleTime(BannerOmega, BannerZeta);
            end = Mathf.Max(end, SparkleDelay + SparkleStagger + Tween.SettleTime(SparkleOmega, SparkleZeta));
            if (rankRun != null) end = Mathf.Max(end, rankRun.RevealDuration(RankReveal));
            if (titleRun != null) end = Mathf.Max(end, titleRun.RevealDuration(TitleReveal));
            if (descRun != null) end = Mathf.Max(end, descRun.RevealDuration(DescReveal));

            introEnd = end;
            totalDuration = introEnd + Hold + FadeOut;
        }

        private void DrawBanner(float t, float masterAlpha, Matrix4x4 baseMatrix)
        {
            if (DungeonCardArt.Banner == null)
                return;

            // The ribbon unfurls: it snaps open horizontally with a spring while
            // fading up, which is what a banner dropping into place looks like.
            float p = Tween.Spring01(t, BannerOmega, BannerZeta);
            float sx = Mathf.LerpUnclamped(0.60f, 1f, p);
            float sy = Mathf.LerpUnclamped(0.80f, 1f, p);
            float alpha = Mathf.Clamp01(t / BannerFade) * masterAlpha;
            if (alpha <= 0.002f)
                return;

            var rect = RefRect(BannerX, BannerY, BannerW, BannerH);
            GUI.matrix = baseMatrix;
            GUIUtility.ScaleAroundPivot(new Vector2(sx, sy), new Vector2(RefX(BannerCenterX), RefY(BannerCenterY)));
            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUI.DrawTexture(rect, DungeonCardArt.Banner);
            GUI.matrix = baseMatrix;
        }

        private void DrawSparkles(float t, float masterAlpha, Matrix4x4 baseMatrix)
        {
            if (DungeonCardArt.Sparkle == null)
                return;

            for (int i = 0; i < 2; i++)
            {
                float local = t - (SparkleDelay + SparkleStagger * i);
                if (local <= 0f)
                    continue;

                float p = Tween.Spring01(local, SparkleOmega, SparkleZeta);
                // Idle twinkle and a slow sway, offset per sparkle so the pair
                // never breathes in lockstep — synchronised decoration reads as
                // a repeating loop, which is exactly what it is.
                float twinkle = 1f + 0.07f * Mathf.Sin((t + i * 0.9f) * 1.9f);
                float s = Mathf.Max(0.01f, p * twinkle);
                float sway = 10f * Mathf.Sin((t + i * 1.3f) * 0.55f);
                float alpha = Mathf.Clamp01(local / 0.2f) * masterAlpha;

                float edgeY = i == 0 ? BannerTopEdge : BannerBottomEdge;
                var rect = RefRect(BannerCenterX - SparkleSize * 0.5f, edgeY - SparkleSize * 0.5f,
                                   SparkleSize, SparkleSize);

                GUI.matrix = baseMatrix;
                GUIUtility.ScaleAroundPivot(new Vector2(s, s), rect.center);
                GUIUtility.RotateAroundPivot(sway, rect.center);
                GUI.color = new Color(sparkleColor.r, sparkleColor.g, sparkleColor.b, alpha);
                GUI.DrawTexture(rect, DungeonCardArt.Sparkle);
            }
            GUI.matrix = baseMatrix;
        }

        /// <summary>Reveal travel is authored in reference pixels; only the distance needs converting.</summary>
        private TextReveal Scaled(TextReveal reveal)
        {
            reveal.rise *= scale;
            return reveal;
        }

        private int Px(int refSize) => Mathf.Max(6, Mathf.RoundToInt(refSize * scale));

        /// <summary>
        /// Shrink floor in screen pixels: the design's own floor, but never
        /// below what a person can read. Build() clamps this against the
        /// starting size, so a floor above it simply disables shrinking.
        /// </summary>
        private int Floor(int refSize) => Mathf.Max(Px(refSize), MinReadablePx);
        private float RefX(float x) => originX + x * scale;
        private float RefY(float y) => originY + y * scale;
        private Rect RefRect(float x, float y, float w, float h) =>
            new Rect(originX + x * scale, originY + y * scale, w * scale, h * scale);
    }
}
