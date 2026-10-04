using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// The "tower has risen" letter. A plain StandardLetter whose window carries
    /// the tower illustration as a banner over the text; choices (jump-to,
    /// close) are the vanilla ones. Bound through the IsekaiDungeons_TowerRises
    /// LetterDef's letterClass, so no Harmony is involved.
    /// </summary>
    public class Letter_TowerRises : StandardLetter
    {
        public override void OpenLetter()
        {
            DiaNode node = new DiaNode(Text);
            node.options.AddRange(Choices);
            Find.WindowStack.Add(new Dialog_TowerRisesLetter(node, title));
        }
    }

    [StaticConstructorOnStartup]
    public class Dialog_TowerRisesLetter : Dialog_NodeTree
    {
        private static readonly Texture2D Banner = ContentFinder<Texture2D>.Get("UI/Letters/TowerRises");

        private const float BannerAspect = 941f / 1672f;
        private const float BannerGap = 10f;

        public Dialog_TowerRisesLetter(DiaNode root, string title) : base(root, false, false, title)
        {
        }

        /// <summary>Vanilla size, grown by the banner so the text keeps its usual room.</summary>
        public override Vector2 InitialSize
        {
            get
            {
                Vector2 size = base.InitialSize;
                float bannerHeight = (size.x - Margin * 2f) * BannerAspect;
                size.y = Mathf.Min(size.y + bannerHeight + BannerGap, UI.screenHeight);
                return size;
            }
        }

        public override void DoWindowContents(Rect inRect)
        {
            Rect rect = inRect.AtZero();
            if (title != null)
            {
                Text.Font = GameFont.Small;
                Rect titleRect = rect;
                titleRect.height = 36f;
                rect.yMin += 53f;
                Widgets.DrawTitleBG(titleRect);
                titleRect.xMin += 9f;
                titleRect.yMin += 5f;
                Widgets.Label(titleRect, title);
            }

            Rect bannerRect = new Rect(rect.x, rect.y, rect.width, rect.width * BannerAspect);
            GUI.DrawTexture(bannerRect, Banner, ScaleMode.ScaleAndCrop);
            Widgets.DrawBox(bannerRect);
            rect.yMin = bannerRect.yMax + BannerGap;

            DrawNode(rect);
        }
    }
}
