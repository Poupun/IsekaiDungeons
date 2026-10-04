using RimWorld;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// The reel's art, resolved once. Every texture is optional by design: a
    /// missing file falls back to the code-drawn look it replaced, so the UI
    /// can be reskinned one piece at a time and a broken path never blanks the
    /// ceremony.
    ///
    /// LAYOUT CONSTANTS LIVE HERE, not scattered through the window, because
    /// they are mirrored 1:1 by the HTML preview harness (UIPreview/reel.html)
    /// - same names, same values. Change a number here, change it there, and
    /// the browser shows what the game will show. The harness is the mirror;
    /// this file is the truth.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class TreasureReelArt
    {
        // ── Textures (all optional) ─────────────────────────────────────────

        public static readonly Texture2D Track =
            ContentFinder<Texture2D>.Get("Treasures/UI_ReelTrack", false);

        public static readonly Texture2D Pointer =
            ContentFinder<Texture2D>.Get("Treasures/UI_ReelPointer", false);

        public static readonly Texture2D ResultBG =
            ContentFinder<Texture2D>.Get("Treasures/UI_ResultBG", false);

        /// <summary>Indexed by TreasureRarity. Any miss falls back to the neutral code card.</summary>
        public static readonly Texture2D[] Cards =
        {
            ContentFinder<Texture2D>.Get("Treasures/UI_ReelCard_Common", false),
            ContentFinder<Texture2D>.Get("Treasures/UI_ReelCard_Rare", false),
            ContentFinder<Texture2D>.Get("Treasures/UI_ReelCard_Epic", false),
            ContentFinder<Texture2D>.Get("Treasures/UI_ReelCard_Legendary", false),
        };

        /// <summary>The dungeon-title ribbon, shared with DungeonTitleCard.</summary>
        public static readonly Texture2D TitleBanner =
            ContentFinder<Texture2D>.Get("UI/Dungeon_Rank_Container", false);

        // ── Layout (mirrored by UIPreview/reel.html - keep the names) ───────

        public const float WindowW = 770f;
        public const float WindowH = 444f;

        // The title ribbon at NATIVE size: the 215x122 source drawn 1:1, which
        // is both the smallest sensible reading of "much smaller" and the
        // sharpest, since no resample happens at all. Its opaque band is rows
        // 21..100 of the source.
        public const float BannerW = 200f;
        public const float BannerH = BannerW / 215f * 122f;          // 113.5
        public const float BannerTop = 0f;
        public const float BannerBandTopFrac = 21f / 122f;
        public const float BannerBandBotFrac = 100f / 122f;
        // Dropped with the ribbon: the longest name ("Legendary Chest") has to
        // clear the band's width, and at 34 it overran the native ribbon.
        // The title is the LARGEST text in the window - it was reading as the
        // smallest, which inverted the hierarchy against a 24px result line.
        // Americaine is condensed enough that "Legendary Chest" is only 109px
        // wide at 30, well inside the ribbon band's 164px budget.
        public const int TitlePx = 30;

        // The track: 724x147 source, drawn native. Cards clip to its inner
        // span so they vanish under the chevron ends rather than past them.
        public const float TrackW = 724f;
        public const float TrackH = 163f;
        public const float TrackTop = 124f;
        /// <summary>Measured: the rail's solid body spans columns 15..708.</summary>
        public const float TrackInnerInset = 15f;
        /// <summary>
        /// Measured: the rail's SOLID body begins at row 25 of 163 - above that
        /// is the sparkle cluster and the chevron tips, which are not rail.
        /// Everything inside the track is placed from HERE rather than from the
        /// texture's top edge, which is what makes the contents survive the art
        /// being resized: re-measure these two numbers and the cards and the
        /// pointer both re-seat themselves.
        /// </summary>
        public const float TrackBodyTopFrac = 25f / 163f;

        /// <summary>Height of the rail's solid body, in drawn pixels.</summary>
        public static float TrackBodyH => TrackH * (1f - TrackBodyTopFrac);

        public const float CardW = 88f;
        public const float CardH = 118f;
        public const float SlotPitch = 96f;

        // Cards are seated by CardTop below, derived from the rail's own body.
        // The icon sits in the chequered body, clear of the frame and the
        // rarity star that rides the card's top edge.
        public const float CardIconTop = 16f;
        public const float CardIconSide = 12f;
        public const float CardIconBottom = 18f;

        public const float PointerW = 3f;
        /// <summary>Gap between the pointer's ends and the rail's body edges.</summary>
        public const float PointerInset = 8f;
        /// <summary>
        /// Spans the rail's body rather than carrying a fixed height. It used
        /// to be a hard 122, which only centred correctly on the old 147px
        /// texture by coincidence.
        /// </summary>
        public static float PointerH => TrackBodyH - 2f * PointerInset;

        // The result plate: 516x78 source, drawn down to 380 wide - same
        // aspect, so the rune ends keep their proportions. Name in Americaine
        // at the rolled rarity's colour.
        public const float ResultW = 380f;
        public const float ResultH = ResultW / 516f * 78f;           // 57.4
        public const float ResultTop = 300f;
        public const int ResultPx = 24;
        public const int ResultMinPx = 15;
        public const float ResultTextMaxW = 300f;   // the rune ends take the rest

        /// <summary>
        /// Measured: the plate's solid body begins at row 11 of 78. Its upper
        /// sliver is the spark that rides the top edge, so centring text in the
        /// whole texture put it high - the same mistake the cards were making.
        /// </summary>
        public const float ResultBodyTopFrac = 11f / 78f;

        public const float ButtonW = 180f;
        public const float ButtonH = 34f;

        // ── Animation ───────────────────────────────────────────────────────
        //
        // Springs rather than eases wherever something should ARRIVE: a spring
        // overshoots and settles, which is what reads as weight. Tween solves
        // them analytically, so all of this is a pure function of elapsed time
        // and immune to OnGUI running several times a frame.

        /// <summary>The ribbon drops in from above and settles.</summary>
        public const float BannerOmega = 13f;
        public const float BannerZeta = 0.52f;
        public const float BannerDropPx = 96f;

        /// <summary>The rail rises into place, a beat after the ribbon.</summary>
        public const float TrackDelay = 0.10f;
        public const float TrackOmega = 15f;
        public const float TrackZeta = 0.62f;
        public const float TrackRisePx = 54f;

        /// <summary>Cards fade up as the rail arrives; no travel of their own.</summary>
        public const float CardsFadeDelay = 0.22f;
        public const float CardsFadeSeconds = 0.34f;

        /// <summary>
        /// The landing. The plate springs open from a squashed scale, and the
        /// winning card gets one pulse - the two together are the "win", and
        /// they are deliberately louder than anything in the entrance.
        /// </summary>
        public const float ResultDelay = 0.06f;
        public const float ResultOmega = 11f;
        public const float ResultZeta = 0.42f;
        public const float ResultScaleFrom = 0.55f;

        public const float WinPulseOmega = 9f;
        public const float WinPulseZeta = 0.30f;
        public const float WinPulseAmount = 0.16f;   // peak extra scale on the card

        /// <summary>Button fades in last, once the plate has stopped moving.</summary>
        public const float ButtonDelay = 0.45f;
        public const float ButtonFadeSeconds = 0.3f;

        // ── The dismissal ───────────────────────────────────────────────────
        //
        // AN EXIT IS NOT AN ENTRANCE PLAYED BACKWARDS. Everything above springs,
        // because a spring overshoots and settles and that is what arriving
        // reads as. Leaving is the opposite: it should ACCELERATE away and be
        // gone, so this is a plain ease-in and it is less than half the length
        // of the entrance. A window the player has finished with must not make
        // them wait.
        //
        // Scale and fade together rather than a slide, because the reel has
        // already spent six seconds moving horizontally - one more translation
        // would read as another spin starting rather than as the end.
        public const float ExitSeconds = 0.28f;

        /// <summary>Shrunk this far by the time it is fully transparent.</summary>
        public const float ExitScaleTo = 0.62f;

        /// <summary>
        /// Scales a rect about its own centre. The plate and the winning card
        /// both grow from the middle rather than the top-left, which is the
        /// difference between a pop and a slide.
        /// </summary>
        public static Rect ScaledAboutCentre(Rect r, float scale)
        {
            float w = r.width * scale, h = r.height * scale;
            return new Rect(r.x + (r.width - w) / 2f, r.y + (r.height - h) / 2f, w, h);
        }

        /// <summary>
        /// Centred banner rect plus the baseline the title sits on: the middle
        /// of the ribbon's opaque band, dropped by a third of the ascent - the
        /// optical centring the title card uses.
        /// </summary>
        public static Rect BannerRect(float innerWidth) =>
            new Rect((innerWidth - BannerW) / 2f, BannerTop, BannerW, BannerH);

        /// <summary>
        /// Card top within the track, CENTRED IN THE RAIL'S SOLID BODY rather
        /// than in the texture. The body is 120px of a 147px texture and a card
        /// is 118, so they very nearly fill it - which is plainly what the art
        /// was drawn for, and why any guess at an inset looked wrong.
        /// </summary>
        public static float CardTop =>
            TrackH * TrackBodyTopFrac + (TrackBodyH - CardH) / 2f;

        /// <summary>Pointer top within the track, centred in the rail's body.</summary>
        public static float PointerTop =>
            TrackH * TrackBodyTopFrac + (TrackBodyH - PointerH) / 2f;

        public static float TitleBaseline(Rect banner, float ascentPx)
        {
            float bandTop = banner.y + banner.height * BannerBandTopFrac;
            float bandBot = banner.y + banner.height * BannerBandBotFrac;
            return (bandTop + bandBot) / 2f + ascentPx * 0.35f;
        }

        /// <summary>Baseline centred in the plate's solid body, same rule as the ribbon.</summary>
        public static float ResultBaseline(Rect plate, float ascentPx)
        {
            float bodyTop = plate.y + plate.height * ResultBodyTopFrac;
            return (bodyTop + plate.yMax) / 2f + ascentPx * 0.35f;
        }
    }
}
