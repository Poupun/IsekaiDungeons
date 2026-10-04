using System;
using System.Collections.Generic;
using System.Text;
using IsekaiLeveling.MobRanking;
using RimWorld;
using UnityEngine;
using Verse;
using Set = IsekaiDungeons.IsekaiDungeonsSettings;

namespace IsekaiDungeons
{
    /// <summary>
    /// The mod settings window: a search field, two rows of folder tabs and a
    /// scrolling list of rows, all generated from <see cref="SettingsRegistry"/>.
    ///
    /// Nothing here knows about any particular setting except the presets
    /// block on the General tab. A row is drawn from its entry's kind and
    /// unit, so a new setting needs no drawing code at all.
    ///
    /// Only UI state lives here (tab, scroll, search text, the text-field
    /// buffers of the tables). Every value is read from and written straight
    /// to the settings object, which vanilla saves when the window closes.
    /// </summary>
    public static class SettingsWindow
    {
        private const float HeaderHeight = 30f;
        private const float RowHeight = 26f;
        private const float ResetSize = 20f;
        private const float ValueWidth = 150f;
        private const float LabelFraction = 0.36f;
        private const float TableIndent = 12f;
        private const float TableCellMaxWidth = 90f;
        private const string SearchControl = "IsekaiDungeonsSettingsSearch";

        private static readonly Color ChangedColor = new Color(1f, 0.8f, 0.42f);
        private static readonly Color MutedColor = new Color(0.63f, 0.63f, 0.67f);
        private static readonly Color WarningColor = new Color(1f, 0.72f, 0.45f);
        private static readonly Color RuleColor = new Color(1f, 1f, 1f, 0.16f);

        private static SettingsTab selected = SettingsTab.General;
        private static string search = "";
        private static readonly Dictionary<SettingsTab, Vector2> scrollByTab = new Dictionary<SettingsTab, Vector2>();
        private static readonly Dictionary<SettingsTab, float> heightByTab = new Dictionary<SettingsTab, float>();
        private static Vector2 searchScroll;
        private static float searchHeight = 800f;

        /// <summary>Text-field buffers for table cells, keyed "field#index". Cleared whenever values change from outside the field.</summary>
        private static readonly Dictionary<string, string> buffers = new Dictionary<string, string>();

        private static readonly List<TabRecord> tabs = new List<TabRecord>();

        /// <summary>Lower-cased label + description + section per entry, rebuilt when the language changes.</summary>
        private static readonly Dictionary<SettingEntry, string> haystacks = new Dictionary<SettingEntry, string>();
        private static LoadedLanguage haystackLanguage;

        // ═══ Entry point ═══════════════════════════════════════════════════

        public static void Draw(Rect inRect, Set s)
        {
            if (s == null)
                return;

            GameFont oldFont = Text.Font;
            TextAnchor oldAnchor = Text.Anchor;
            Color oldColor = GUI.color;
            try
            {
                Text.Font = GameFont.Small;
                DrawHeader(new Rect(inRect.x, inRect.y, inRect.width, HeaderHeight), s);

                float bodyTop = inRect.y + HeaderHeight + 8f + TabDrawer.TabHeight + 31f;
                var body = new Rect(inRect.x, bodyTop, inRect.width, inRect.yMax - bodyTop);
                Widgets.DrawMenuSection(body);
                DrawTabs(body, s);

                Rect inner = body.ContractedBy(12f);
                if (Searching)
                    DrawSearchResults(inner, s);
                else
                    DrawTab(inner, selected, s);
            }
            finally
            {
                Text.Font = oldFont;
                Text.Anchor = oldAnchor;
                GUI.color = oldColor;
            }
        }

        private static bool Searching => !search.NullOrEmpty() && search.Trim().Length > 0;

        // ═══ Header: search, descriptions, reset everything ════════════════

        private static void DrawHeader(Rect header, Set s)
        {
            var box = new Rect(header.x, header.y + 2f, Mathf.Min(320f, header.width * 0.38f), 26f);
            GUI.DrawTexture(new Rect(box.x + 4f, box.y + 4f, 18f, 18f), TexButton.Search);

            float clearWidth = search.NullOrEmpty() ? 0f : 22f;
            var field = new Rect(box.x + 26f, box.y, box.width - 26f - clearWidth, box.height);
            GUI.SetNextControlName(SearchControl);
            string typed = Widgets.TextField(field, search);
            if (typed != search)
            {
                search = typed;
                searchScroll = Vector2.zero;
            }

            if (search.NullOrEmpty() && GUI.GetNameOfFocusedControl() != SearchControl)
            {
                GUI.color = MutedColor;
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(field.x + 6f, field.y, field.width - 6f, field.height),
                              "IsekaiDungeons.Settings.SearchHint".Translate());
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
            }

            if (!search.NullOrEmpty()
                && Widgets.ButtonImage(new Rect(field.xMax + 4f, box.y + 5f, 16f, 16f), TexButton.CloseXSmall))
            {
                search = "";
                UI.UnfocusCurrentControl();
            }

            var resetAll = new Rect(header.xMax - 170f, header.y, 170f, 28f);
            if (Widgets.ButtonText(resetAll, "IsekaiDungeons.Settings.ResetAll".Translate()))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    "IsekaiDungeons.Settings.ResetAll.Confirm".Translate(),
                    () =>
                    {
                        bool descriptions = s.showDescriptions;
                        s.ResetAll();
                        s.showDescriptions = descriptions;
                        buffers.Clear();
                    },
                    destructive: true));
            }

            var descriptions = new Rect(resetAll.x - 16f - 230f, header.y + 2f, 230f, 24f);
            Widgets.CheckboxLabeled(descriptions, "IsekaiDungeons.Settings.ShowDescriptions".Translate(), ref s.showDescriptions);
        }

        // ═══ Tabs ══════════════════════════════════════════════════════════

        private static void DrawTabs(Rect body, Set s)
        {
            tabs.Clear();
            foreach (SettingsTab t in Enum.GetValues(typeof(SettingsTab)))
            {
                SettingsTab tab = t;
                tabs.Add(new TabRecord(TabLabel(tab, s), () =>
                {
                    selected = tab;
                    search = "";
                    UI.UnfocusCurrentControl();
                }, !Searching && selected == tab));
            }
            TabDrawer.DrawTabs(body, tabs, 2, null);
        }

        private static string TabLabel(SettingsTab tab, Set s)
        {
            string label = ("IsekaiDungeons.Settings.Tab." + tab).Translate();
            int changed = ChangedCount(tab, s);
            return changed > 0 ? label + " (" + changed + ")" : label;
        }

        /// <summary>
        /// Changed settings per tab, counted once per frame. IMGUI calls the
        /// window several times a frame and every tab label asks, and each
        /// check is two reflection reads, so counting on demand was thousands
        /// of reads per frame for a number that only moves when a value does.
        /// </summary>
        private static readonly int[] changedByTab = new int[Enum.GetValues(typeof(SettingsTab)).Length];
        private static int changedFrame = -1;
        private static int changedRevision = -1;

        private static int ChangedCount(SettingsTab tab, Set s)
        {
            if (changedFrame != Time.frameCount || changedRevision != s.Revision)
            {
                Array.Clear(changedByTab, 0, changedByTab.Length);
                List<SettingEntry> all = SettingsRegistry.All;
                for (int i = 0; i < all.Count; i++)
                {
                    if (all[i].IsChanged(s))
                        changedByTab[(int)all[i].tab]++;
                }
                changedFrame = Time.frameCount;
                changedRevision = s.Revision;
            }
            return changedByTab[(int)tab];
        }

        // ═══ One tab ═══════════════════════════════════════════════════════

        private static void DrawTab(Rect inner, SettingsTab tab, Set s)
        {
            scrollByTab.TryGetValue(tab, out Vector2 scroll);
            if (!heightByTab.TryGetValue(tab, out float height) || height <= 0f)
                height = 3000f;

            var view = new Rect(0f, 0f, inner.width - 16f, height);
            Widgets.BeginScrollView(inner, ref scroll, view);
            var list = new Listing_Standard { maxOneColumn = true };
            list.Begin(new Rect(0f, 0f, view.width, 100000f));

            DrawTabIntro(list, tab, s);
            if (tab == SettingsTab.General)
                DrawPresets(list, s);

            string section = null;
            foreach (SettingEntry e in SettingsRegistry.InTab(tab))
            {
                if (e.section != section)
                {
                    section = e.section;
                    DrawSectionHeader(list, section, withNote: true);
                }
                DrawEntry(list, e, s);
            }

            float used = list.CurHeight;
            list.End();
            Widgets.EndScrollView();

            scrollByTab[tab] = scroll;
            heightByTab[tab] = used + 24f;
        }

        private static void DrawTabIntro(Listing_Standard list, SettingsTab tab, Set s)
        {
            Rect title = list.GetRect(34f);
            Text.Font = GameFont.Medium;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(title.x, title.y, title.width - 190f, title.height),
                          ("IsekaiDungeons.Settings.Tab." + tab).Translate());
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;

            if (ChangedCount(tab, s) > 0
                && Widgets.ButtonText(new Rect(title.xMax - 180f, title.y + 3f, 180f, 28f),
                                      "IsekaiDungeons.Settings.ResetTab".Translate()))
            {
                s.ResetTab(tab);
                buffers.Clear();
            }

            string introKey = "IsekaiDungeons.Settings.Tab." + tab + ".Intro";
            if (introKey.CanTranslate())
            {
                GUI.color = tab == SettingsTab.Advanced ? WarningColor : MutedColor;
                list.Label(introKey.Translate());
                GUI.color = Color.white;
            }
            list.Gap(4f);
        }

        private static void DrawSectionHeader(Listing_Standard list, string section, bool withNote)
        {
            list.Gap(10f);
            Rect r = list.GetRect(26f);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(r, "<b>" + ("IsekaiDungeons.Settings.Section." + section).Translate() + "</b>");
            Text.Anchor = TextAnchor.UpperLeft;
            Widgets.DrawLineHorizontal(r.x, r.yMax, r.width, RuleColor);
            list.Gap(4f);

            string noteKey = "IsekaiDungeons.Settings.Section." + section + ".Note";
            if (withNote && noteKey.CanTranslate())
            {
                Text.Font = GameFont.Tiny;
                GUI.color = MutedColor;
                list.Label(noteKey.Translate());
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                list.Gap(2f);
            }
        }

        // ═══ Search ════════════════════════════════════════════════════════

        private static void DrawSearchResults(Rect inner, Set s)
        {
            EnsureHaystacks();
            string query = search.Trim().ToLowerInvariant();

            var view = new Rect(0f, 0f, inner.width - 16f, searchHeight);
            Widgets.BeginScrollView(inner, ref searchScroll, view);
            var list = new Listing_Standard { maxOneColumn = true };
            list.Begin(new Rect(0f, 0f, view.width, 100000f));

            int matches = 0;
            foreach (SettingsTab tab in Enum.GetValues(typeof(SettingsTab)))
            {
                bool tabShown = false;
                string section = null;
                foreach (SettingEntry e in SettingsRegistry.InTab(tab))
                {
                    if (!haystacks.TryGetValue(e, out string hay) || !hay.Contains(query))
                        continue;

                    if (!tabShown)
                    {
                        tabShown = true;
                        list.Gap(matches == 0 ? 0f : 14f);
                        Text.Font = GameFont.Medium;
                        list.Label(("IsekaiDungeons.Settings.Tab." + tab).Translate());
                        Text.Font = GameFont.Small;
                    }
                    if (e.section != section)
                    {
                        section = e.section;
                        DrawSectionHeader(list, section, withNote: false);
                    }
                    DrawEntry(list, e, s);
                    matches++;
                }
            }

            if (matches == 0)
            {
                GUI.color = MutedColor;
                list.Label("IsekaiDungeons.Settings.NoMatches".Translate(search.Trim().Named("QUERY")));
                GUI.color = Color.white;
            }

            float used = list.CurHeight;
            list.End();
            Widgets.EndScrollView();
            searchHeight = used + 24f;
        }

        private static void EnsureHaystacks()
        {
            if (haystackLanguage == LanguageDatabase.activeLanguage && haystacks.Count == SettingsRegistry.All.Count)
                return;
            haystacks.Clear();
            haystackLanguage = LanguageDatabase.activeLanguage;
            foreach (SettingEntry e in SettingsRegistry.All)
            {
                string hay = e.LabelKey.Translate().Resolve() + "\n" + e.DescKey.Translate().Resolve() + "\n"
                             + e.SectionKey.Translate().Resolve() + "\n" + e.field;
                haystacks[e] = hay.ToLowerInvariant();
            }
        }

        // ═══ Presets ═══════════════════════════════════════════════════════

        private static void DrawPresets(Listing_Standard list, Set s)
        {
            DrawSectionHeader(list, "General.Presets", withNote: true);

            const int columns = 3;
            const float gap = 10f;
            float width = (list.ColumnWidth - gap * (columns - 1)) / columns;
            List<SettingsPreset> presets = SettingsPresets.All;

            for (int start = 0; start < presets.Count; start += columns)
            {
                float descHeight = 0f;
                if (s.showDescriptions)
                {
                    Text.Font = GameFont.Tiny;
                    for (int i = start; i < Mathf.Min(start + columns, presets.Count); i++)
                        descHeight = Mathf.Max(descHeight, Text.CalcHeight(presets[i].DescKey.Translate(), width));
                    Text.Font = GameFont.Small;
                }

                Rect row = list.GetRect(30f + (descHeight > 0f ? descHeight + 4f : 0f));
                for (int i = start; i < Mathf.Min(start + columns, presets.Count); i++)
                {
                    SettingsPreset preset = presets[i];
                    float x = row.x + (i - start) * (width + gap);
                    var button = new Rect(x, row.y, width, 30f);
                    if (Widgets.ButtonText(button, preset.LabelKey.Translate()))
                        ConfirmPreset(preset, s);
                    TooltipHandler.TipRegion(button, preset.DescKey.Translate());

                    if (descHeight > 0f)
                    {
                        Text.Font = GameFont.Tiny;
                        GUI.color = MutedColor;
                        Widgets.Label(new Rect(x, button.yMax + 4f, width, descHeight), preset.DescKey.Translate());
                        GUI.color = Color.white;
                        Text.Font = GameFont.Small;
                    }
                }
                list.Gap(8f);
            }
        }

        private static void ConfirmPreset(SettingsPreset preset, Set s)
        {
            var lines = new StringBuilder();
            foreach (KeyValuePair<string, object> kv in preset.overrides)
            {
                SettingEntry e = SettingsRegistry.Find(kv.Key);
                if (e == null)
                    continue;
                string value = e.kind == SettingKind.Toggle
                    ? (Convert.ToBoolean(kv.Value) ? "IsekaiDungeons.Settings.On" : "IsekaiDungeons.Settings.Off").Translate().ToString()
                    : FormatValue(e, Convert.ToSingle(kv.Value));
                lines.AppendLine("  - " + e.LabelKey.Translate() + ": " + value);
            }

            string text = preset.overrides.Count == 0
                ? "IsekaiDungeons.Settings.ResetAll.Confirm".Translate().ToString()
                : "IsekaiDungeons.Settings.Preset.Confirm".Translate(
                      preset.LabelKey.Translate().Named("PRESET"),
                      lines.ToString().TrimEnd().Named("CHANGES")).ToString();

            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(text, () =>
            {
                SettingsPresets.Apply(s, preset);
                buffers.Clear();
            }));
        }

        // ═══ Rows ══════════════════════════════════════════════════════════

        private static void DrawEntry(Listing_Standard list, SettingEntry e, Set s)
        {
            switch (e.kind)
            {
                case SettingKind.Toggle: DrawToggle(list, e, s); break;
                case SettingKind.Float:
                case SettingKind.Int: DrawSlider(list, e, s); break;
                case SettingKind.Rank: DrawRank(list, e, s); break;
                default: DrawTable(list, e, s); break;
            }

            if (s.showDescriptions)
                DrawDescription(list, e);
            list.Gap(3f);
        }

        private static void DrawToggle(Listing_Standard list, SettingEntry e, Set s)
        {
            Rect row = list.GetRect(RowHeight);
            Widgets.DrawHighlightIfMouseover(row);
            bool changed = e.IsChanged(s);
            Rect reset = ResetRect(row);

            bool value = e.GetBool(s);
            bool before = value;
            if (changed)
                GUI.color = ChangedColor;
            Widgets.CheckboxLabeled(new Rect(row.x, row.y, reset.x - 10f - row.x, row.height),
                                    e.LabelKey.Translate(), ref value);
            GUI.color = Color.white;
            if (value != before)
            {
                e.SetBool(s, value);
                Commit(s);
            }

            DrawReset(reset, e, s, changed);
            TooltipHandler.TipRegion(row, Tip(e));
        }

        private static void DrawSlider(Listing_Standard list, SettingEntry e, Set s)
        {
            Rect row = list.GetRect(RowHeight);
            Widgets.DrawHighlightIfMouseover(row);
            bool changed = e.IsChanged(s);
            Rect reset = ResetRect(row);
            var value = new Rect(reset.x - 6f - ValueWidth, row.y, ValueWidth, row.height);
            var label = new Rect(row.x, row.y, row.width * LabelFraction, row.height);
            var slider = new Rect(label.xMax + 10f, row.y, value.x - 10f - (label.xMax + 10f), row.height);

            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(label, e.LabelKey.Translate().Resolve().Truncate(label.width));

            float current = e.GetFloat(s);
            float next = Widgets.HorizontalSlider(slider, current, e.min, e.max, middleAlignment: true, roundTo: e.step);
            if (Mathf.Abs(next - current) > 1e-5f)
            {
                e.SetFloat(s, next);
                Commit(s);
            }

            GUI.color = changed ? ChangedColor : Color.white;
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(value, FormatValue(e, e.GetFloat(s)));
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;

            DrawReset(reset, e, s, changed);
            TooltipHandler.TipRegion(row, Tip(e));
        }

        private static void DrawRank(Listing_Standard list, SettingEntry e, Set s)
        {
            Rect row = list.GetRect(RowHeight);
            Widgets.DrawHighlightIfMouseover(row);
            bool changed = e.IsChanged(s);
            Rect reset = ResetRect(row);
            var label = new Rect(row.x, row.y, row.width * LabelFraction, row.height);

            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(label, e.LabelKey.Translate().Resolve().Truncate(label.width));
            Text.Anchor = TextAnchor.UpperLeft;

            var tier = (MobRankTier)Mathf.RoundToInt(e.GetFloat(s));
            var button = new Rect(label.xMax + 10f, row.y + 1f, 140f, row.height - 2f);
            if (changed)
                GUI.color = ChangedColor;
            if (Widgets.ButtonText(button, DungeonRankUtil.ColoredRankString(tier)))
            {
                var options = new List<FloatMenuOption>();
                for (int i = 0; i <= (int)MobRankTier.Nation; i++)
                {
                    int index = i;
                    options.Add(new FloatMenuOption(MobRankUtility.GetRankString((MobRankTier)index), () =>
                    {
                        e.SetFloat(s, index);
                        Commit(s);
                    }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            GUI.color = Color.white;

            DrawReset(reset, e, s, changed);
            TooltipHandler.TipRegion(row, Tip(e));
        }

        private static void DrawTable(Listing_Standard list, SettingEntry e, Set s)
        {
            Rect row = list.GetRect(RowHeight);
            Widgets.DrawHighlightIfMouseover(row);
            bool changed = e.IsChanged(s);
            Rect reset = ResetRect(row);
            int length = e.kind == SettingKind.IntTable ? e.GetInts(s).Length : e.GetFloats(s).Length;

            float buttons = e.FixedLength ? 0f : 2f * 22f + 8f;
            var label = new Rect(row.x, row.y, reset.x - 10f - buttons - row.x, row.height);
            GUI.color = changed ? ChangedColor : Color.white;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(label, e.LabelKey.Translate().Resolve().Truncate(label.width));
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;

            if (!e.FixedLength)
            {
                var plus = new Rect(label.xMax + 4f, row.y + 3f, 20f, 20f);
                var minus = new Rect(plus.xMax + 4f, row.y + 3f, 20f, 20f);
                if (length < e.maxLength && Widgets.ButtonImage(plus, TexButton.Plus))
                {
                    ResizeList(e, s, length + 1);
                    length++;
                }
                if (length > e.minLength && Widgets.ButtonImage(minus, TexButton.Minus))
                {
                    ResizeList(e, s, length - 1);
                    length--;
                }
            }

            DrawReset(reset, e, s, changed);
            TooltipHandler.TipRegion(row, Tip(e));

            if (length == 0)
            {
                Rect none = list.GetRect(20f);
                Text.Font = GameFont.Tiny;
                GUI.color = MutedColor;
                Widgets.Label(new Rect(none.x + TableIndent, none.y, none.width - TableIndent, none.height),
                              "IsekaiDungeons.Settings.Table.Empty".Translate());
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                return;
            }

            int slots = Mathf.Max(length, e.FixedLength ? length : Mathf.Max(e.maxLength, 1));
            float cell = Mathf.Min(TableCellMaxWidth, (row.width - TableIndent) / slots);

            Rect header = list.GetRect(18f);
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleCenter;
            for (int i = 0; i < length; i++)
            {
                var r = new Rect(header.x + TableIndent + i * cell, header.y, cell - 4f, header.height);
                Widgets.Label(r, ColumnLabel(e, i));
            }
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;

            Rect cells = list.GetRect(26f);
            for (int i = 0; i < length; i++)
            {
                var r = new Rect(cells.x + TableIndent + i * cell, cells.y + 1f, cell - 4f, 24f);
                string key = e.field + "#" + i;
                buffers.TryGetValue(key, out string buffer);

                if (e.kind == SettingKind.IntTable)
                {
                    int[] values = e.GetInts(s);
                    int v = values[i];
                    Widgets.TextFieldNumeric(r, ref v, ref buffer, e.min, e.max);
                    if (v != values[i])
                    {
                        values[i] = v;
                        Commit(s, clearBuffers: false);
                    }
                }
                else
                {
                    float[] values = e.GetFloats(s);
                    float v = values[i];
                    Widgets.TextFieldNumeric(r, ref v, ref buffer, e.min, e.max);
                    if (Mathf.Abs(v - values[i]) > 1e-6f)
                    {
                        values[i] = v;
                        Commit(s, clearBuffers: false);
                    }
                }
                buffers[key] = buffer;
            }
        }

        private static void ResizeList(SettingEntry e, Set s, int length)
        {
            float[] old = e.GetFloats(s);
            var resized = new float[length];
            for (int i = 0; i < length; i++)
                resized[i] = i < old.Length ? old[i] : (old.Length > 0 ? old[old.Length - 1] : e.max);
            e.info.SetValue(s, resized);
            Commit(s);
        }

        private static void DrawDescription(Listing_Standard list, SettingEntry e)
        {
            string desc = e.DescKey.Translate();
            Text.Font = GameFont.Tiny;
            float width = list.ColumnWidth - TableIndent;
            float height = Text.CalcHeight(desc, width);
            Rect r = list.GetRect(height);
            GUI.color = MutedColor;
            Widgets.Label(new Rect(r.x + TableIndent, r.y, width, height), desc);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        private static Rect ResetRect(Rect row) =>
            new Rect(row.xMax - ResetSize, row.y + (row.height - ResetSize) / 2f, ResetSize, ResetSize);

        private static void DrawReset(Rect rect, SettingEntry e, Set s, bool changed)
        {
            if (!changed)
                return;
            if (Widgets.ButtonImage(rect, TexButton.Reload, true, "IsekaiDungeons.Settings.ResetOne".Translate()))
            {
                e.Reset(s);
                Commit(s);
            }
        }

        /// <summary>After any edit: keep pairs ordered, invalidate caches, and drop stale table buffers.</summary>
        private static void Commit(Set s, bool clearBuffers = true)
        {
            s.Normalize();
            s.MarkChanged();
            if (clearBuffers)
                buffers.Clear();
        }

        // ═══ Text ══════════════════════════════════════════════════════════

        private static TipSignal Tip(SettingEntry e) => new TipSignal(() =>
        {
            var sb = new StringBuilder();
            sb.AppendLine("<b>" + e.LabelKey.Translate() + "</b>");
            sb.AppendLine(e.DescKey.Translate());
            sb.AppendLine();
            sb.Append("IsekaiDungeons.Settings.Tip.Default".Translate(DefaultText(e).Named("VALUE")));
            if (e.kind == SettingKind.Float || e.kind == SettingKind.Int)
            {
                sb.AppendLine();
                sb.Append("IsekaiDungeons.Settings.Tip.Range".Translate(
                    FormatValue(e, e.min).Named("MIN"), FormatValue(e, e.max).Named("MAX")));
            }
            return sb.ToString();
        }, e.field.GetHashCode());

        private static string DefaultText(SettingEntry e)
        {
            Set d = Set.Defaults;
            switch (e.kind)
            {
                case SettingKind.Toggle:
                    return (e.GetBool(d) ? "IsekaiDungeons.Settings.On" : "IsekaiDungeons.Settings.Off").Translate();
                case SettingKind.Rank:
                    return MobRankUtility.GetRankString((MobRankTier)Mathf.RoundToInt(e.GetFloat(d)));
                case SettingKind.IntTable:
                    return string.Join("  ", Array.ConvertAll(e.GetInts(d), v => v.ToString("N0")));
                case SettingKind.FloatTable:
                    float[] values = e.GetFloats(d);
                    return values.Length == 0
                        ? (string)"IsekaiDungeons.Settings.Table.Empty".Translate()
                        : string.Join("  ", Array.ConvertAll(values, v => v.ToString(e.format)));
                default:
                    return FormatValue(e, e.GetFloat(d));
            }
        }

        public static string FormatValue(SettingEntry e, float v)
        {
            if (e.kind == SettingKind.Rank)
                return MobRankUtility.GetRankString((MobRankTier)Mathf.RoundToInt(v));
            if (e.zeroIsOff && Mathf.Abs(v) < 1e-4f)
                return "IsekaiDungeons.Settings.Off".Translate();

            switch (e.unit)
            {
                case SettingUnit.Signed:
                    return (v > 0f ? "+" : "") + v.ToString(e.format);
                case SettingUnit.Multiplier:
                    return "x" + v.ToString("0.##");
                case SettingUnit.Percent:
                    return (v * 100f).ToString("0.#") + "%";
                case SettingUnit.Days:
                    return Mathf.Approximately(v, 1f)
                        ? (string)"IsekaiDungeons.Settings.Unit.OneDay".Translate()
                        : "IsekaiDungeons.Settings.Unit.Days".Translate(v.ToString("0.#").Named("VALUE"));
                case SettingUnit.Day:
                    return "IsekaiDungeons.Settings.Unit.Day".Translate(v.ToString("0.#").Named("VALUE"));
                case SettingUnit.Hours:
                    return "IsekaiDungeons.Settings.Unit.Hours".Translate(v.ToString("0.#").Named("VALUE"));
                case SettingUnit.Seconds:
                    return "IsekaiDungeons.Settings.Unit.Seconds".Translate(v.ToString("0.#").Named("VALUE"));
                case SettingUnit.Cells:
                    return Mathf.Approximately(v, 1f)
                        ? (string)"IsekaiDungeons.Settings.Unit.OneCell".Translate()
                        : "IsekaiDungeons.Settings.Unit.Cells".Translate(v.ToString("0.#").Named("VALUE"));
                case SettingUnit.Temperature:
                    return v.ToStringTemperature("F0");
                case SettingUnit.Silver:
                    return v.ToString("N0");
                case SettingUnit.Floor:
                    return "IsekaiDungeons.Settings.Unit.Floor".Translate(v.ToString("0").Named("VALUE"));
                case SettingUnit.PerSeason:
                    return v <= 0.01f
                        ? (string)"IsekaiDungeons.Settings.Off".Translate()
                        : "IsekaiDungeons.Settings.Unit.PerSeason".Translate(
                              v.ToString("0.#").Named("COUNT"),
                              (Set.DaysPerSeason / v).ToString("0.#").Named("DAYS"));
                default:
                    return v.ToString(e.format);
            }
        }

        private static string ColumnLabel(SettingEntry e, int i)
        {
            switch (e.columns)
            {
                case TableColumns.Ranks:
                    return DungeonRankUtil.ColoredRankString((MobRankTier)Mathf.Clamp(i, 0, 9));
                case TableColumns.DecorCurveRanks:
                    return DungeonRankUtil.ColoredRankString((MobRankTier)SettingsRegistry.DecorCurveRanks[Mathf.Clamp(i, 0, 5)]);
                case TableColumns.TowerRankFloors:
                    return "IsekaiDungeons.Settings.Unit.Floor".Translate(
                        SettingsRegistry.TowerRankFloors[Mathf.Clamp(i, 0, 7)].Named("VALUE"));
                case TableColumns.TowerSizeFloors:
                    return "IsekaiDungeons.Settings.Unit.Floor".Translate(
                        SettingsRegistry.TowerSizeFloors[Mathf.Clamp(i, 0, 3)].Named("VALUE"));
                case TableColumns.Registers:
                    return ("IsekaiDungeons.Settings.Column.Register." + (DecorRegister)Mathf.Clamp(i, 0, 4)).Translate();
                case TableColumns.Rarities:
                    return ("IsekaiDungeons.Treasure.Rarity." + (TreasureRarity)Mathf.Clamp(i, 0, 3)).Translate();
                case TableColumns.ExtraRolls:
                    int grade = Mathf.Max(1, e.tier - (i + 1));
                    return ("IsekaiDungeons.Settings.Column.Grade." + grade).Translate();
                case TableColumns.Waves:
                    return "IsekaiDungeons.Settings.Column.Wave".Translate((i + 1).Named("VALUE"));
                default:
                    return (i + 1).ToString();
            }
        }
    }
}
