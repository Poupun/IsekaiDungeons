using System;
using System.Collections.Generic;
using IsekaiLeveling.MobRanking;
using RimWorld;
using UnityEngine;
using Verse;

namespace IsekaiDungeons
{
    /// <summary>
    /// Pick a floor.
    ///
    /// Two modes, and the difference is the whole point:
    ///
    ///   NORMAL — only floors already CLIMBED to. The frontier is advanced by
    ///   Building_TowerStair alone, so this can never be used to skip a boss.
    ///   It is fast travel over ground already taken, not a second way up.
    ///
    ///   GOD MODE — every floor, explored or not. Testing floor 97's generation
    ///   should not require beating ninety-six floors, and the extra columns
    ///   (rank, archetype, backing dungeon, map size) are there so a whole
    ///   ladder can be eyeballed for balance without opening a single map.
    ///
    /// A window rather than a float menu because a hundred entries in a float
    /// menu is not a menu, it is a wall.
    /// </summary>
    public class Dialog_TowerFloorPicker : Window
    {
        private readonly Action<int> onPicked;
        private readonly bool allFloors;
        private Vector2 scroll;
        private string search = string.Empty;

        private const float RowHeight = 30f;
        private const float HeaderHeight = 68f;

        public Dialog_TowerFloorPicker(Action<int> onPicked, bool allFloors)
        {
            this.onPicked = onPicked;
            this.allFloors = allFloors;
            doCloseX = true;
            doCloseButton = true;
            forcePause = true;
            absorbInputAroundWindow = true;
        }

        public override Vector2 InitialSize => new Vector2(620f, 640f);

        public override void DoWindowContents(Rect inRect)
        {
            TowerComponent comp = TowerComponent.Get;
            if (comp == null || !comp.Exists)
            {
                Close();
                return;
            }

            var title = new Rect(inRect.x, inRect.y, inRect.width, 30f);
            Text.Font = GameFont.Medium;
            Widgets.Label(title, allFloors
                ? "IsekaiDungeons.Tower.PickerTitleDev".Translate()
                : "IsekaiDungeons.Tower.PickerTitle".Translate());
            Text.Font = GameFont.Small;

            var searchRect = new Rect(inRect.x, inRect.y + 34f, 240f, 26f);
            search = Widgets.TextField(searchRect, search);
            var hint = new Rect(searchRect.xMax + 8f, inRect.y + 34f, inRect.width - searchRect.width - 8f, 26f);
            GUI.color = Color.gray;
            Widgets.Label(hint, "IsekaiDungeons.Tower.PickerHint".Translate());
            GUI.color = Color.white;

            List<TowerFloorRecord> rows = BuildRows(comp);

            var outer = new Rect(inRect.x, inRect.y + HeaderHeight,
                                 inRect.width, inRect.height - HeaderHeight - 40f);
            var view = new Rect(0f, 0f, outer.width - 20f, rows.Count * RowHeight);
            Widgets.BeginScrollView(outer, ref scroll, view);

            // The pick is RECORDED here and acted on after EndScrollView.
            // Returning from inside a scroll view skips EndScrollView and leaves
            // Unity's GUI clip stack unbalanced for the rest of the frame, which
            // corrupts every window drawn after this one — and it does it
            // silently, with no exception to point at the cause.
            int picked = -1;
            float y = 0f;
            for (int i = 0; i < rows.Count; i++)
            {
                TowerFloorRecord rec = rows[i];
                var row = new Rect(0f, y, view.width, RowHeight);
                if (Mouse.IsOver(row))
                    Widgets.DrawHighlight(row);
                if (i % 2 == 1)
                    Widgets.DrawLightHighlight(row);

                DrawRow(row, rec);

                if (Widgets.ButtonInvisible(row))
                    picked = rec.floor;

                y += RowHeight;
            }
            Widgets.EndScrollView();

            if (picked > 0)
            {
                Close();
                onPicked?.Invoke(picked);
            }
        }

        private List<TowerFloorRecord> BuildRows(TowerComponent comp)
        {
            var rows = new List<TowerFloorRecord>();
            if (allFloors)
            {
                // PREVIEW, NEVER MATERIALISE.
                //
                // EnsureRecord does not just read a floor — it FREEZES it. The
                // record is added to the save and its identity can never change
                // again, which is exactly right when you visit a floor and
                // exactly wrong when you merely look at a list.
                //
                // Calling it for all hundred floors would freeze the entire
                // ladder the first time anyone opened this window, so a save
                // that browsed once would be permanently stuck on whatever
                // archetype table happened to be loaded that day — no rebalance,
                // no new floor type, and no added archetype would ever reach it
                // again. Roll() is the same pure function without the side
                // effect: same seed, same floor, same answer.
                for (int f = 1; f <= TowerTunables.FloorCount; f++)
                    rows.Add(comp.Preview(f));
            }
            else
            {
                rows.Add(comp.Preview(1));
                List<TowerFloorRecord> discovered = comp.DiscoveredFloors();
                for (int i = 0; i < discovered.Count; i++)
                {
                    if (discovered[i].floor > 1)
                        rows.Add(discovered[i]);
                }
            }

            if (search.NullOrEmpty())
                return rows;

            string q = search.ToLowerInvariant();
            var filtered = new List<TowerFloorRecord>();
            for (int i = 0; i < rows.Count; i++)
            {
                TowerFloorRecord r = rows[i];
                string hay = $"{r.floor} {r.Label} {r.rank} {r.kindDef?.label} {r.archetype?.label}".ToLowerInvariant();
                if (hay.Contains(q))
                    filtered.Add(r);
            }
            return filtered;
        }

        private static void DrawRow(Rect row, TowerFloorRecord rec)
        {
            var num = new Rect(row.x + 4f, row.y, 44f, row.height);
            var name = new Rect(num.xMax, row.y, 210f, row.height);
            var rank = new Rect(name.xMax, row.y, 60f, row.height);
            var kind = new Rect(rank.xMax, row.y, 190f, row.height);
            var state = new Rect(kind.xMax, row.y, row.width - kind.xMax - 4f, row.height);

            Text.Anchor = TextAnchor.MiddleLeft;

            GUI.color = Color.gray;
            Widgets.Label(num, rec.floor.ToString());
            GUI.color = Color.white;

            Widgets.Label(name, rec.archetype?.floorLabel.NullOrEmpty() == false
                ? rec.archetype.floorLabel.CapitalizeFirst()
                : rec.kindDef?.label?.CapitalizeFirst() ?? "?");

            GUI.color = MobRankUtility.GetRankColor(rec.rank);
            Widgets.Label(rank, MobRankUtility.GetRankString(rec.rank));
            GUI.color = Color.white;

            GUI.color = new Color(0.72f, 0.72f, 0.72f);
            Widgets.Label(kind, rec.kindDef?.label ?? "?");

            string tag = rec.cleared ? "IsekaiDungeons.Tower.PickerCleared".Translate()
                       : rec.discovered ? "IsekaiDungeons.Tower.PickerSeen".Translate()
                       : "IsekaiDungeons.Tower.PickerUnseen".Translate();
            GUI.color = rec.cleared ? new Color(0.55f, 0.75f, 0.55f)
                      : rec.discovered ? new Color(0.75f, 0.72f, 0.55f)
                      : new Color(0.5f, 0.5f, 0.5f);
            Widgets.Label(state, tag);

            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
        }
    }
}
