using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GameData.Domains.Item;
using JianghuYouling.Effects;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace JianghuYouling
{
    /// <summary>
    /// 太吾主动行动的纯代码选择窗。群聊先选收件人，随后再从太吾当下真实持有/所学
    /// 中选择；这里不让模型猜物品、功法或技艺，也不提前执行任何副作用。
    /// </summary>
    internal static class TaiwuDirectActionWindow
    {
        private sealed class Choice
        {
            internal GiftableItem Item;
            internal LearnableSkill Skill;
            internal string SkillKind;
            internal string Name;
            internal string Meta;
            internal string Icon;
            internal string Category;
            internal string Sect;
            internal int CategoryIndex;
            internal int Grade = -1;
            internal bool IsLifeSkill;
        }

        private static GameObject _root;
        private static TMP_FontAsset _font;
        private static RectTransform _body;
        private static RectTransform _list;
        private static TextMeshProUGUI _title;
        private static TextMeshProUGUI _subtitle;
        private static TextMeshProUGUI _empty;
        private static TextMeshProUGUI _selectionSummary;
        private static TMP_InputField _search;
        private static TMP_InputField _noteInput;
        private static Button _confirmButton;
        private static Button _selectAllButton;
        private static TextMeshProUGUI _confirmLabel;
        private static Button _backButton;
        private static readonly List<KeyValuePair<int, string>> Targets =
            new List<KeyValuePair<int, string>>();
        private static readonly List<Choice> Choices = new List<Choice>();
        private static readonly List<GameObject> ChoiceRows = new List<GameObject>();
        private static readonly List<Image> ChoiceBackgrounds = new List<Image>();
        private static readonly List<Image> ChoiceMarkers = new List<Image>();
        private static readonly List<Image> ChoiceMarkerFills = new List<Image>();
        private static readonly List<TextMeshProUGUI> ChoiceLabels = new List<TextMeshProUGUI>();
        private static readonly List<TMP_InputField> ChoiceAmountInputs = new List<TMP_InputField>();
        private static readonly List<int> VisibleChoiceIndices = new List<int>();
        private static readonly HashSet<int> SelectedChoices = new HashSet<int>();
        private static readonly Dictionary<int, int> ChoiceAmounts = new Dictionary<int, int>();
        private static readonly List<Image> TargetBackgrounds = new List<Image>();
        private static readonly List<Image> TargetMarkers = new List<Image>();
        private static readonly List<Image> TargetMarkerFills = new List<Image>();
        private static readonly HashSet<int> SelectedTargetIndices = new HashSet<int>();
        private static readonly List<int> ActiveTargetIndices = new List<int>();
        private static readonly List<Button> FilterButtons = new List<Button>();
        private static readonly List<Button> SectButtons = new List<Button>();
        private static readonly List<Button> SkillTabButtons = new List<Button>();
        private static readonly List<Button> GiftSortButtons = new List<Button>();
        private static readonly List<string> AvailableSects = new List<string>();
        private static readonly List<TaiwuDirectActionSelection> BatchSelections =
            new List<TaiwuDirectActionSelection>();
        private static int _taiwuId;
        private static TaiwuDirectActionKind _kind;
        private static bool _requireTargetSelection;
        private static int _targetId;
        private static string _targetName;
        private static int _targetCursor;
        private static long _requestVersion;
        private static int _worldGeneration;
        private static uint _worldId;
        private static Action<IList<TaiwuDirectActionSelection>, string> _onConfirm;
        private static Action<string> _onFailure;
        private static string _accompanyingText = string.Empty;
        private static string _searchText = string.Empty;
        private static int _giftCategoryFilter;
        private static int _giftSortMode;
        private static int _skillTab;
        private static int _skillCategoryFilter;
        private static int _skillSectFilter;

        private static readonly Color PanelColor = new Color(0.085f, 0.098f, 0.092f, 0.995f);
        private static readonly Color Jade = new Color(0.23f, 0.33f, 0.30f, 0.98f);
        private static readonly Color JadeHover = new Color(0.31f, 0.43f, 0.38f, 1f);
        private static readonly Color Bronze = new Color(0.67f, 0.55f, 0.31f, 1f);
        private static readonly Color RowNormal = new Color(0.12f, 0.145f, 0.135f, 0.99f);
        private static readonly Color RowSelected = new Color(0.25f, 0.36f, 0.32f, 1f);
        private static readonly Color MarkerNormal = new Color(0.20f, 0.27f, 0.25f, 1f);
        private static readonly Color Paper = new Color(0.94f, 0.90f, 0.80f, 1f);
        private static readonly Color Muted = new Color(0.62f, 0.69f, 0.63f, 0.92f);

        private static readonly string[] GiftCategories =
        {
            "全部", "食物", "丹药", "装备", "书籍", "工具", "材料", "资源", "杂物",
        };

        private static readonly string[] CombatCategories =
        {
            "全部", "内功", "摧破", "轻灵", "护体", "奇窍",
        };

        private static readonly string[] LifeCategories =
        {
            "全部", "音律", "弈棋", "诗书", "绘画", "术数", "品鉴", "锻造", "制木",
            "医术", "毒术", "织锦", "巧匠", "道法", "佛学", "厨艺", "杂学",
        };

        internal static void Open(TMP_FontAsset font, int taiwuId,
            IList<KeyValuePair<int, string>> targets, bool requireTargetSelection,
            TaiwuDirectActionKind kind,
            Action<IList<TaiwuDirectActionSelection>, string> onConfirm,
            Action<string> onFailure)
        {
            Close(false);
            _font = font != null ? font : ResolveFont();
            _taiwuId = taiwuId;
            _kind = kind;
            _requireTargetSelection = requireTargetSelection;
            _onConfirm = onConfirm;
            _onFailure = onFailure;
            _accompanyingText = string.Empty;
            _giftCategoryFilter = 0;
            _giftSortMode = 0;
            _skillTab = 0;
            _skillCategoryFilter = 0;
            _skillSectFilter = 0;
            _worldGeneration = WorldLifecycle.Generation;
            _worldId = WorldLifecycle.WorldId;
            unchecked { _requestVersion++; }
            Targets.Clear();
            if (targets != null)
                foreach (KeyValuePair<int, string> target in targets)
                    if (target.Key > 0 && target.Key != taiwuId
                        && !Targets.Any(x => x.Key == target.Key))
                        Targets.Add(new KeyValuePair<int, string>(target.Key,
                            string.IsNullOrWhiteSpace(target.Value)
                                ? "江湖人#" + target.Key : target.Value.Trim()));
            if (_taiwuId <= 0 || Targets.Count == 0 || _worldId == 0
                || !WorldLifecycle.IsSameWorld(_worldGeneration))
            {
                Fail("当前没有可执行该行动的对象");
                return;
            }

            BuildShell();
            if (_requireTargetSelection) ShowTargetStage();
            else
            {
                ActiveTargetIndices.Clear();
                ActiveTargetIndices.Add(0);
                _targetCursor = 0;
                _targetId = Targets[0].Key;
                _targetName = Targets[0].Value;
                BeginLoadChoices();
            }
        }

        private static void BuildShell()
        {
            _root = new GameObject("JYL_TaiwuDirectAction", typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster));
            UnityEngine.Object.DontDestroyOnLoad(_root);
            Canvas canvas = _root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30020;
            CanvasScaler scaler = _root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);

            GameObject dim = new GameObject("Dim", typeof(RectTransform), typeof(Image), typeof(Button));
            dim.transform.SetParent(_root.transform, false);
            Anchor(dim.GetComponent<RectTransform>(), Vector2.zero, Vector2.one,
                Vector2.zero, Vector2.zero);
            dim.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.62f);
            dim.GetComponent<Button>().onClick.AddListener(() => Close(false));

            GameObject panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(_root.transform, false);
            RectTransform panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = panelRect.anchorMax = panelRect.pivot =
                new Vector2(0.5f, 0.5f);
            panelRect.sizeDelta = new Vector2(1000f, 820f);
            panelRect.anchoredPosition = Vector2.zero;
            Image panelImage = panel.GetComponent<Image>();
            ChatTab.ApplyRoundedSkin(panelImage);
            panelImage.color = PanelColor;

            GameObject titleAccent = new GameObject("TitleAccent", typeof(RectTransform),
                typeof(Image));
            titleAccent.transform.SetParent(panel.transform, false);
            Anchor(titleAccent.GetComponent<RectTransform>(), new Vector2(0, 1),
                new Vector2(0, 1), new Vector2(24, -60), new Vector2(28, -20));
            titleAccent.GetComponent<Image>().color = Bronze;

            _title = NewText("Title", panel.transform, 27, TextAlignmentOptions.Left);
            Anchor(_title.rectTransform, new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(42, -54), new Vector2(-72, -14));
            _title.color = new Color(0.95f, 0.91f, 0.80f, 1f);
            _title.text = ActionTitle(_kind);

            _subtitle = NewText("Subtitle", panel.transform, 14, TextAlignmentOptions.Left);
            Anchor(_subtitle.rectTransform, new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(42, -80), new Vector2(-72, -54));
            _subtitle.color = new Color(0.63f, 0.72f, 0.66f, 0.95f);

            GameObject close = NewButton("Close", panel.transform, "X", 20, out Button closeButton);
            Anchor(close.GetComponent<RectTransform>(), new Vector2(1, 1), new Vector2(1, 1),
                new Vector2(-50, -52), new Vector2(-14, -16));
            closeButton.onClick.AddListener(() => Close(false));

            GameObject divider = new GameObject("HeaderDivider", typeof(RectTransform), typeof(Image));
            divider.transform.SetParent(panel.transform, false);
            Anchor(divider.GetComponent<RectTransform>(), new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(24, -94), new Vector2(-24, -92));
            divider.GetComponent<Image>().color = new Color(Bronze.r, Bronze.g, Bronze.b, 0.45f);

            GameObject body = new GameObject("Body", typeof(RectTransform));
            body.transform.SetParent(panel.transform, false);
            _body = body.GetComponent<RectTransform>();
            Anchor(_body, Vector2.zero, Vector2.one, new Vector2(24, 22), new Vector2(-24, -104));

            try { PopupRegistry.Register(_root, () => Close(false)); } catch { }
        }

        private static void ShowTargetStage()
        {
            ClearBody();
            _targetId = 0;
            _targetName = null;
            _targetCursor = -1;
            SelectedChoices.Clear();
            ChoiceAmounts.Clear();
            ActiveTargetIndices.Clear();
            BatchSelections.Clear();
            _subtitle.text = "可一次多选；随后为每个人分别配置";

            TextMeshProUGUI hint = NewText("Hint", _body, 16, TextAlignmentOptions.Left);
            hint.text = "勾选多人后逐人选择内容；代码仍会分别实时核对能否当面行动。";
            hint.color = new Color(0.76f, 0.76f, 0.67f, 0.95f);
            Anchor(hint.rectTransform, new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(6, -42), new Vector2(-6, -4));

            TargetBackgrounds.Clear();
            TargetMarkers.Clear();
            TargetMarkerFills.Clear();
            BuildScroll(new Vector2(0, 90), new Vector2(0, -52));
            for (int i = 0; i < Targets.Count; i++)
            {
                KeyValuePair<int, string> target = Targets[i];
                GameObject row = NewChoiceRow(target.Value,
                    "<color=#87B9A1>群聊成员</color>", _list);
                int captured = i;
                TargetBackgrounds.Add(row.GetComponent<Image>());
                TargetMarkers.Add(row.transform.Find("SelectedMark")?.GetComponent<Image>());
                TargetMarkerFills.Add(row.transform.Find("SelectedMark/Check")
                    ?.GetComponent<Image>());
                row.GetComponent<Button>().onClick.AddListener(() =>
                {
                    if (!RequestCurrent()) return;
                    if (!SelectedTargetIndices.Add(captured))
                        SelectedTargetIndices.Remove(captured);
                    RefreshTargetSelection();
                });
            }

            _selectionSummary = NewText("SelectionSummary", _body, 14,
                TextAlignmentOptions.MidlineLeft);
            _selectionSummary.color = new Color(0.67f, 0.72f, 0.65f, 0.92f);
            Anchor(_selectionSummary.rectTransform, new Vector2(0, 0), new Vector2(1, 0),
                new Vector2(12, 56), new Vector2(-12, 84));

            GameObject cancel = NewButton("Cancel", _body, "取消", 18, out Button cancelButton);
            Anchor(cancel.GetComponent<RectTransform>(), new Vector2(0, 0), new Vector2(0, 0),
                new Vector2(0, 4), new Vector2(130, 48));
            StyleButton(cancel, false);
            cancelButton.onClick.AddListener(() => Close(false));

            GameObject confirm = NewButton("ConfirmTargets", _body, "选择行动对象", 18,
                out _confirmButton);
            _confirmLabel = confirm.GetComponentInChildren<TextMeshProUGUI>();
            Anchor(confirm.GetComponent<RectTransform>(), new Vector2(0, 0), new Vector2(1, 0),
                new Vector2(140, 4), new Vector2(0, 48));
            StyleButton(confirm, true);
            _confirmButton.onClick.AddListener(BeginSelectedTargets);
            RefreshTargetSelection();
        }

        private static void RefreshTargetSelection()
        {
            for (int index = 0; index < Targets.Count; index++)
            {
                bool selected = SelectedTargetIndices.Contains(index);
                if (index < TargetBackgrounds.Count && TargetBackgrounds[index] != null)
                    TargetBackgrounds[index].color = selected ? RowSelected : RowNormal;
                if (index < TargetMarkers.Count && TargetMarkers[index] != null)
                    TargetMarkers[index].color = selected ? Bronze : MarkerNormal;
                if (index < TargetMarkerFills.Count && TargetMarkerFills[index] != null)
                    TargetMarkerFills[index].color = new Color(Paper.r, Paper.g, Paper.b,
                        selected ? 1f : 0f);
            }
            int count = SelectedTargetIndices.Count;
            if (_selectionSummary != null)
                _selectionSummary.text = count > 0
                    ? "已选择 " + count + " 人，将按顺序分别配置"
                    : "可勾选多名群聊成员";
            if (_confirmButton != null) _confirmButton.interactable = count > 0;
            if (_confirmLabel != null)
                _confirmLabel.text = count > 0 ? "为 " + count + " 人分别配置" : "选择行动对象";
        }

        private static void BeginSelectedTargets()
        {
            if (!RequestCurrent() || SelectedTargetIndices.Count == 0) return;
            ActiveTargetIndices.Clear();
            foreach (int index in SelectedTargetIndices.OrderBy(value => value))
                if (index >= 0 && index < Targets.Count)
                    ActiveTargetIndices.Add(index);
            if (ActiveTargetIndices.Count == 0) return;
            BatchSelections.Clear();
            _targetCursor = 0;
            SetCurrentTarget();
            BeginLoadChoices();
        }

        private static void SetCurrentTarget()
        {
            if (_targetCursor < 0 || _targetCursor >= ActiveTargetIndices.Count) return;
            int index = ActiveTargetIndices[_targetCursor];
            if (index < 0 || index >= Targets.Count) return;
            _targetId = Targets[index].Key;
            _targetName = Targets[index].Value;
        }

        private static void BeginLoadChoices()
        {
            if (!RequestCurrent()) return;
            _searchText = string.Empty;
            _giftCategoryFilter = 0;
            _giftSortMode = 0;
            _skillTab = 0;
            _skillCategoryFilter = 0;
            _skillSectFilter = 0;
            ClearBody();
            _subtitle.text = TargetProgressLabel();
            TextMeshProUGUI loading = NewText("Loading", _body, 19, TextAlignmentOptions.Center);
            loading.text = "正在读取太吾此刻的" + (_kind == TaiwuDirectActionKind.GiveItem
                ? "随身物与银钱……" : "武学与技艺……");
            loading.color = new Color(0.76f, 0.82f, 0.74f, 1f);
            Anchor(loading.rectTransform, Vector2.zero, Vector2.one,
                new Vector2(0, 40), new Vector2(0, -40));
            ConfigHost host = ConfigHost.Instance;
            if (host == null)
            {
                Fail("选择窗口宿主缺失");
                return;
            }
            long version = _requestVersion;
            host.StartCoroutine(LoadChoices(version));
        }

        private static IEnumerator LoadChoices(long version)
        {
            TaiwuHoldings holdings = null;
            int money = -1;
            bool moneyDone = _kind != TaiwuDirectActionKind.GiveItem;
            if (!moneyDone)
                EffectHandler.QueryTaiwuMoney(_taiwuId,
                    value => { money = value; moneyDone = true; });
            yield return NpcSnapshotReader.FetchTaiwuHoldings(_taiwuId, value => holdings = value);
            float deadline = Time.unscaledTime + EffectHandler.ReadOnlyQueryWaitSeconds;
            while (!moneyDone && Time.unscaledTime < deadline && RequestCurrent(version))
                yield return null;
            if (!RequestCurrent(version)) yield break;

            Choices.Clear();
            if (_kind == TaiwuDirectActionKind.GiveItem)
            {
                ItemKey moneyKey = new ItemKey((sbyte)12, (byte)0, (short)6, 0);
                int availableMoney = Math.Max(0, money - ReservedGiftCount(moneyKey));
                if (availableMoney > 0)
                {
                    var moneyChoice = new Choice
                    {
                        Name = "银钱",
                        Item = new GiftableItem
                        {
                            Name = "银钱",
                            Key = moneyKey,
                            Count = availableMoney,
                        },
                    };
                    PopulateGiftChoice(moneyChoice, availableMoney);
                    Choices.Add(moneyChoice);
                }
                foreach (GiftableItem item in holdings?.Items ?? new List<GiftableItem>())
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.Name)) continue;
                    // 太吾当前装备只保留在自然语言查询上下文中，不进入点击赠礼清单。
                    // 这样列表源头就无法选中已装备实体，避免后端被要求转移装备槽中的物品。
                    if (item.EquippedCount > 0 && item.NonEquippedCount <= 0) continue;
                    int count = item.Count > 0 ? item.Count : 1;
                    int available = Math.Max(0, count - ReservedGiftCount(item.Key));
                    if (available <= 0) continue;
                    var choice = new Choice
                    {
                        Name = item.Name.Trim(),
                        Item = CloneGiftableItem(item, available),
                    };
                    PopulateGiftChoice(choice, available);
                    Choices.Add(choice);
                }
            }
            else
            {
                foreach (LearnableSkill skill in holdings?.CombatSkills ?? new List<LearnableSkill>())
                    if (skill != null && !string.IsNullOrWhiteSpace(skill.Name))
                    {
                        var choice = new Choice
                        {
                            Name = skill.Name.Trim(),
                            Skill = skill,
                            SkillKind = "combat",
                        };
                        PopulateSkillChoice(choice, false);
                        Choices.Add(choice);
                    }
                foreach (LearnableSkill skill in holdings?.LifeSkills ?? new List<LearnableSkill>())
                    if (skill != null && !string.IsNullOrWhiteSpace(skill.Name))
                    {
                        var choice = new Choice
                        {
                            Name = skill.Name.Trim(),
                            Skill = skill,
                            SkillKind = "life",
                        };
                        PopulateSkillChoice(choice, true);
                        Choices.Add(choice);
                    }
            }
            RebuildAvailableSects();
            ShowChoiceStage();
        }

        private static void ShowChoiceStage(bool resetSelection = true)
        {
            ClearBody();
            if (resetSelection)
            {
                SelectedChoices.Clear();
                ChoiceAmounts.Clear();
            }
            _subtitle.text = TargetProgressLabel() + " · "
                + (_kind == TaiwuDirectActionKind.GiveItem ? "选择赠物"
                    : _kind == TaiwuDirectActionKind.Teach ? "选择要传授的武学或技艺"
                    : "选择要回忆成书的武学或技艺");

            if (_requireTargetSelection)
            {
                GameObject back = NewButton("Back", _body, "← 重选多人", 15, out _backButton);
                Anchor(back.GetComponent<RectTransform>(), new Vector2(0, 1), new Vector2(0, 1),
                    new Vector2(0, -42), new Vector2(116, -4));
                StyleButton(back, false);
                _backButton.onClick.AddListener(ShowTargetStage);
            }

            GameObject searchGo = new GameObject("Search", typeof(RectTransform), typeof(Image));
            searchGo.transform.SetParent(_body, false);
            Anchor(searchGo.GetComponent<RectTransform>(), new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(_requireTargetSelection ? 126 : 0, -44), new Vector2(0, 0));
            Image searchImage = searchGo.GetComponent<Image>();
            ChatTab.ApplyRoundedSkin(searchImage);
            searchImage.color = new Color(1f, 1f, 1f, 0.085f);
            _search = UiInput.Setup(searchGo, _font, 16, false,
                TMP_InputField.LineType.SingleLine, false, SearchPlaceholder(), 80, false, out _);
            _search.SetTextWithoutNotify(_searchText);
            _search.onValueChanged.AddListener(value =>
            {
                _searchText = value ?? string.Empty;
                RenderChoices();
            });

            if (_kind == TaiwuDirectActionKind.GiveItem)
            {
                Anchor(searchGo.GetComponent<RectTransform>(), new Vector2(0, 1),
                    new Vector2(1, 1), new Vector2(_requireTargetSelection ? 126 : 0, -44),
                    new Vector2(-274, 0));
                BuildGiftSortButtons();
                BuildFilterStrip("GiftCategories", GiftCategories, _giftCategoryFilter,
                    -52f, value =>
                    {
                        _giftCategoryFilter = value;
                        RefreshFilterStyles(FilterButtons, value);
                        RenderChoices();
                    }, FilterButtons);
            }
            else
            {
                Anchor(searchGo.GetComponent<RectTransform>(), new Vector2(0, 1),
                    new Vector2(1, 1), new Vector2(_requireTargetSelection ? 126 : 0, -44),
                    new Vector2(-206, 0));
                BuildSkillTabButtons();
                string[] categories = _skillTab == 0 ? CombatCategories : LifeCategories;
                BuildFilterStrip("SkillCategories", categories, _skillCategoryFilter,
                    -52f, value =>
                    {
                        _skillCategoryFilter = value;
                        RefreshFilterStyles(FilterButtons, value);
                        RenderChoices();
                    }, FilterButtons);
                if (_skillTab == 0)
                    BuildFilterStrip("SkillSects", AvailableSects.ToArray(), _skillSectFilter,
                        -96f, value =>
                        {
                            _skillSectFilter = value;
                            RefreshFilterStyles(SectButtons, value);
                            RenderChoices();
                        }, SectButtons);
            }

            if (_kind != TaiwuDirectActionKind.GiveItem)
            {
                GameObject selectAll = NewButton("SelectAll", _body, "全选", 16,
                    out _selectAllButton);
                Anchor(selectAll.GetComponent<RectTransform>(), new Vector2(1, 0),
                    new Vector2(1, 0), new Vector2(-124, 54), new Vector2(0, 84));
                StyleButton(selectAll, false);
                _selectAllButton.onClick.AddListener(ToggleSelectAllVisible);
            }

            const float footerTop = 182f;
            float listTop = _kind == TaiwuDirectActionKind.GiveItem
                ? -96f : (_skillTab == 0 ? -140f : -96f);
            BuildScroll(new Vector2(0, footerTop), new Vector2(0, listTop),
                _kind != TaiwuDirectActionKind.GiveItem);
            _empty = NewText("Empty", _body, 17, TextAlignmentOptions.Center);
            _empty.color = new Color(0.68f, 0.68f, 0.62f, 1f);
            Anchor(_empty.rectTransform, new Vector2(0, 0), new Vector2(1, 1),
                new Vector2(12, footerTop + 20), new Vector2(-12, listTop - 18));

            BuildAccompanyingInput();

            _selectionSummary = NewText("SelectionSummary", _body, 14,
                TextAlignmentOptions.MidlineLeft);
            _selectionSummary.color = new Color(0.67f, 0.72f, 0.65f, 0.92f);
            Anchor(_selectionSummary.rectTransform, new Vector2(0, 0), new Vector2(1, 0),
                new Vector2(12, 54), new Vector2(-136, 84));

            GameObject cancel = NewButton("Cancel", _body, "取消", 18, out Button cancelButton);
            Anchor(cancel.GetComponent<RectTransform>(), new Vector2(0, 0), new Vector2(0, 0),
                new Vector2(0, 4), new Vector2(120, 48));
            StyleButton(cancel, false);
            cancelButton.onClick.AddListener(() => Close(false));

            GameObject confirm = NewButton("Confirm", _body,
                _kind == TaiwuDirectActionKind.GiveItem ? "确认赠送"
                    : _kind == TaiwuDirectActionKind.Teach ? "确认传授" : "写书并赠送",
                18, out _confirmButton);
            _confirmLabel = confirm.GetComponentInChildren<TextMeshProUGUI>();
            Anchor(confirm.GetComponent<RectTransform>(), new Vector2(0, 0), new Vector2(1, 0),
                new Vector2(128, 4), new Vector2(0, 48));
            StyleButton(confirm, true);
            _confirmButton.interactable = false;
            _confirmButton.onClick.AddListener(Confirm);
            RenderChoices();
        }

        private static void BuildGiftSortButtons()
        {
            GiftSortButtons.Clear();
            string[] labels = { "品级", "数量", "名称" };
            const float width = 82f;
            const float gap = 6f;
            float start = -(width * labels.Length + gap * (labels.Length - 1));
            for (int index = 0; index < labels.Length; index++)
            {
                int captured = index;
                GameObject go = NewButton("GiftSort" + index, _body, labels[index], 14,
                    out Button button);
                float left = start + index * (width + gap);
                Anchor(go.GetComponent<RectTransform>(), new Vector2(1, 1), new Vector2(1, 1),
                    new Vector2(left, -44), new Vector2(left + width, 0));
                GiftSortButtons.Add(button);
                StyleFilterButton(button, index == _giftSortMode);
                button.onClick.AddListener(() =>
                {
                    _giftSortMode = captured;
                    RefreshFilterStyles(GiftSortButtons, captured);
                    RenderChoices();
                });
            }
        }

        private static void BuildSkillTabButtons()
        {
            SkillTabButtons.Clear();
            string[] labels = { "功法", "技艺" };
            const float width = 96f;
            const float gap = 6f;
            float start = -(width * labels.Length + gap);
            for (int index = 0; index < labels.Length; index++)
            {
                int captured = index;
                GameObject go = NewButton("SkillTab" + index, _body, labels[index], 15,
                    out Button button);
                float left = start + index * (width + gap);
                Anchor(go.GetComponent<RectTransform>(), new Vector2(1, 1), new Vector2(1, 1),
                    new Vector2(left, -44), new Vector2(left + width, 0));
                SkillTabButtons.Add(button);
                StyleFilterButton(button, index == _skillTab);
                button.onClick.AddListener(() =>
                {
                    if (_skillTab == captured) return;
                    _skillTab = captured;
                    _skillCategoryFilter = 0;
                    _skillSectFilter = 0;
                    ShowChoiceStage(false);
                });
            }
        }

        private static void BuildFilterStrip(string name, string[] labels, int selected,
            float top, Action<int> onSelect, List<Button> buttons)
        {
            buttons.Clear();
            GameObject scrollGo = new GameObject(name, typeof(RectTransform), typeof(Image),
                typeof(ScrollRect));
            scrollGo.transform.SetParent(_body, false);
            Anchor(scrollGo.GetComponent<RectTransform>(), new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(0, top - 36f), new Vector2(0, top));
            Image stripBackground = scrollGo.GetComponent<Image>();
            ChatTab.ApplyRoundedSkin(stripBackground);
            stripBackground.color = new Color(0f, 0f, 0f, 0.18f);

            ScrollRect scroll = scrollGo.GetComponent<ScrollRect>();
            scroll.horizontal = true;
            scroll.vertical = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 24f;

            GameObject viewport = new GameObject("Viewport", typeof(RectTransform),
                typeof(RectMask2D));
            viewport.transform.SetParent(scrollGo.transform, false);
            Anchor(viewport.GetComponent<RectTransform>(), Vector2.zero, Vector2.one,
                new Vector2(4, 3), new Vector2(-4, -3));
            scroll.viewport = viewport.GetComponent<RectTransform>();

            GameObject content = new GameObject("Content", typeof(RectTransform),
                typeof(HorizontalLayoutGroup), typeof(ContentSizeFitter));
            content.transform.SetParent(viewport.transform, false);
            RectTransform contentRect = content.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0, 0);
            contentRect.anchorMax = new Vector2(0, 1);
            contentRect.pivot = new Vector2(0, 0.5f);
            contentRect.anchoredPosition = Vector2.zero;
            contentRect.sizeDelta = Vector2.zero;
            HorizontalLayoutGroup layout = content.GetComponent<HorizontalLayoutGroup>();
            layout.spacing = 6f;
            layout.padding = new RectOffset(0, 0, 0, 0);
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;
            ContentSizeFitter fitter = content.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.Unconstrained;
            scroll.content = contentRect;

            string[] safeLabels = labels == null || labels.Length == 0
                ? new[] { "全部" } : labels;
            for (int index = 0; index < safeLabels.Length; index++)
            {
                int captured = index;
                GameObject chip = NewButton("Chip" + index, content.transform,
                    safeLabels[index], 14, out Button button);
                LayoutElement element = chip.AddComponent<LayoutElement>();
                element.preferredWidth = Mathf.Max(72f, 32f + safeLabels[index].Length * 16f);
                element.minWidth = element.preferredWidth;
                buttons.Add(button);
                StyleFilterButton(button, index == selected);
                button.onClick.AddListener(() => onSelect?.Invoke(captured));
            }
        }

        private static void BuildAccompanyingInput()
        {
            TextMeshProUGUI label = NewText("NoteLabel", _body, 14,
                TextAlignmentOptions.MidlineLeft);
            label.text = "随行动说的话 <color=#889B8F>· 可不填，将与本批行动作为同一轮发送</color>";
            label.color = new Color(0.88f, 0.78f, 0.52f, 1f);
            Anchor(label.rectTransform, new Vector2(0, 0), new Vector2(1, 0),
                new Vector2(4, 148), new Vector2(-4, 176));

            GameObject inputGo = new GameObject("AccompanyingText", typeof(RectTransform),
                typeof(Image));
            inputGo.transform.SetParent(_body, false);
            Anchor(inputGo.GetComponent<RectTransform>(), new Vector2(0, 0), new Vector2(1, 0),
                new Vector2(0, 88), new Vector2(0, 148));
            Image image = inputGo.GetComponent<Image>();
            ChatTab.ApplyRoundedSkin(image);
            image.color = new Color(0.055f, 0.067f, 0.061f, 0.99f);
            _noteInput = UiInput.Setup(inputGo, _font, 16, true,
                TMP_InputField.LineType.MultiLineNewline, false,
                "写一句想同时对对方说的话……", 2000, false,
                out TextMeshProUGUI noteText);
            noteText.alignment = TextAlignmentOptions.TopLeft;
            noteText.color = Paper;
            // Dynamic popup input omitted the stable caret used by the main chat composer;
            // the game's native TMP caret is not reliably rebuilt after pointer focus here.
            _noteInput.customCaretColor = true;
            _noteInput.caretColor = new Color(Paper.r, Paper.g, Paper.b, 0f);
            _noteInput.caretWidth = 1;
            _noteInput.caretBlinkRate = 0f;
            ChatVisibleCaret visibleCaret = inputGo.AddComponent<ChatVisibleCaret>();
            visibleCaret.Input = _noteInput;
            visibleCaret.Viewport = _noteInput.textViewport;
            visibleCaret.Text = noteText;
            _noteInput.SetTextWithoutNotify(_accompanyingText);
            _noteInput.onValueChanged.AddListener(value =>
                _accompanyingText = value ?? string.Empty);
        }

        private static void RefreshFilterStyles(IList<Button> buttons, int selected)
        {
            if (buttons == null) return;
            for (int index = 0; index < buttons.Count; index++)
                StyleFilterButton(buttons[index], index == selected);
        }

        private static void StyleFilterButton(Button button, bool selected)
        {
            if (button == null) return;
            Image image = button.targetGraphic as Image;
            if (image != null) image.color = Color.white;
            ColorBlock colors = button.colors;
            colors.normalColor = selected ? Bronze : Jade;
            colors.highlightedColor = selected
                ? new Color(0.77f, 0.66f, 0.40f, 1f) : JadeHover;
            colors.pressedColor = selected
                ? new Color(0.56f, 0.45f, 0.25f, 1f)
                : new Color(0.16f, 0.23f, 0.21f, 1f);
            colors.selectedColor = colors.highlightedColor;
            colors.disabledColor = new Color(colors.normalColor.r, colors.normalColor.g,
                colors.normalColor.b, 0.34f);
            colors.fadeDuration = 0.08f;
            ApplyButtonColorsNow(button, colors);
        }

        private static void BuildScroll(Vector2 offsetMin, Vector2 offsetMax,
            bool useGrid = false)
        {
            GameObject scrollGo = new GameObject("Scroll", typeof(RectTransform), typeof(Image),
                typeof(ScrollRect));
            scrollGo.transform.SetParent(_body, false);
            Anchor(scrollGo.GetComponent<RectTransform>(), Vector2.zero, Vector2.one,
                offsetMin, offsetMax);
            Image scrollImage = scrollGo.GetComponent<Image>();
            ChatTab.ApplyRoundedSkin(scrollImage);
            scrollImage.color = new Color(0f, 0f, 0f, 0.24f);
            ScrollRect scroll = scrollGo.GetComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 24f;

            GameObject viewport = new GameObject("Viewport", typeof(RectTransform),
                typeof(RectMask2D));
            viewport.transform.SetParent(scrollGo.transform, false);
            Anchor(viewport.GetComponent<RectTransform>(), Vector2.zero, Vector2.one,
                new Vector2(4, 4), new Vector2(-18, -4));
            scroll.viewport = viewport.GetComponent<RectTransform>();

            GameObject listGo = new GameObject("List", typeof(RectTransform),
                typeof(ContentSizeFitter));
            listGo.transform.SetParent(viewport.transform, false);
            _list = listGo.GetComponent<RectTransform>();
            _list.anchorMin = new Vector2(0, 1);
            _list.anchorMax = new Vector2(1, 1);
            _list.pivot = new Vector2(0.5f, 1f);
            _list.anchoredPosition = Vector2.zero;
            _list.sizeDelta = Vector2.zero;
            if (useGrid)
            {
                GridLayoutGroup grid = listGo.AddComponent<GridLayoutGroup>();
                grid.padding = new RectOffset(8, 8, 8, 8);
                grid.spacing = new Vector2(8f, 8f);
                grid.cellSize = new Vector2(166f, 154f);
                grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
                grid.constraintCount = 5;
                grid.childAlignment = TextAnchor.UpperLeft;
            }
            else
            {
                VerticalLayoutGroup layout = listGo.AddComponent<VerticalLayoutGroup>();
                layout.padding = new RectOffset(6, 6, 6, 6);
                layout.spacing = 6f;
                layout.childForceExpandWidth = true;
                layout.childForceExpandHeight = false;
                layout.childControlWidth = true;
                layout.childControlHeight = true;
            }
            listGo.GetComponent<ContentSizeFitter>().verticalFit =
                ContentSizeFitter.FitMode.PreferredSize;
            scroll.content = _list;
            scroll.verticalScrollbar = UiScroll.AddVerticalForScrollRect(scrollGo);
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHideAndExpandViewport;
        }

        private static void RenderChoices()
        {
            if (_list == null) return;
            ClearList(_list);
            ChoiceRows.Clear();
            ChoiceBackgrounds.Clear();
            ChoiceMarkers.Clear();
            ChoiceMarkerFills.Clear();
            ChoiceLabels.Clear();
            ChoiceAmountInputs.Clear();
            VisibleChoiceIndices.Clear();
            List<int> ordered = FilteredChoiceIndices();
            int visible = 0;
            foreach (int index in ordered)
            {
                Choice choice = Choices[index];
                GameObject row = _kind == TaiwuDirectActionKind.GiveItem
                    ? NewGiftChoiceRow(choice, _list)
                    : NewSkillChoiceCard(choice, _list);
                int captured = index;
                row.GetComponent<Button>().onClick.AddListener(() => ToggleChoice(captured));
                TextMeshProUGUI label = row.transform.Find("Name")?.GetComponent<TextMeshProUGUI>();
                Image marker = row.transform.Find("SelectedMark")?.GetComponent<Image>();
                Image markerFill = row.transform.Find("SelectedMark/Check")
                    ?.GetComponent<Image>();
                if (_kind == TaiwuDirectActionKind.GiveItem)
                    AddGiftAmountInput(row.transform, captured);
                ChoiceRows.Add(row);
                ChoiceBackgrounds.Add(row.GetComponent<Image>());
                ChoiceMarkers.Add(marker);
                ChoiceMarkerFills.Add(markerFill);
                ChoiceLabels.Add(label);
                VisibleChoiceIndices.Add(index);
                visible++;
            }
            if (_empty != null)
            {
                _empty.gameObject.SetActive(visible == 0);
                _empty.text = Choices.Count == 0
                    ? (_kind == TaiwuDirectActionKind.GiveItem
                        ? "太吾此刻没有可赠之物"
                        : "太吾此刻没有可选的武学或技艺")
                    : "当前筛选下没有可选内容";
            }
            RefreshChoiceSelection();
        }

        private static List<int> FilteredChoiceIndices()
        {
            string query = (_search?.text ?? _searchText ?? string.Empty).Trim();
            var result = new List<int>();
            for (int index = 0; index < Choices.Count; index++)
            {
                Choice choice = Choices[index];
                if (choice == null) continue;
                if (query.Length > 0
                    && !ContainsIgnoreCase(choice.Name, query)
                    && !ContainsIgnoreCase(choice.Meta, query)
                    && !ContainsIgnoreCase(choice.Category, query)
                    && !ContainsIgnoreCase(choice.Sect, query))
                    continue;
                if (_kind == TaiwuDirectActionKind.GiveItem)
                {
                    if (_giftCategoryFilter > 0
                        && choice.CategoryIndex != _giftCategoryFilter) continue;
                }
                else
                {
                    if (choice.IsLifeSkill != (_skillTab == 1)) continue;
                    if (_skillCategoryFilter > 0
                        && choice.CategoryIndex != _skillCategoryFilter) continue;
                    if (_skillTab == 0 && _skillSectFilter > 0
                        && (_skillSectFilter >= AvailableSects.Count
                            || !string.Equals(choice.Sect,
                                AvailableSects[_skillSectFilter], StringComparison.Ordinal)))
                        continue;
                }
                result.Add(index);
            }
            result.Sort((left, right) => CompareChoices(Choices[left], Choices[right]));
            return result;
        }

        private static int CompareChoices(Choice left, Choice right)
        {
            if (_kind == TaiwuDirectActionKind.GiveItem)
            {
                if (_giftSortMode == 1)
                {
                    int count = (right?.Item?.Count ?? 0).CompareTo(left?.Item?.Count ?? 0);
                    if (count != 0) return count;
                }
                else if (_giftSortMode == 0)
                {
                    int grade = (right?.Grade ?? -1).CompareTo(left?.Grade ?? -1);
                    if (grade != 0) return grade;
                }
            }
            else
            {
                int grade = (right?.Grade ?? -1).CompareTo(left?.Grade ?? -1);
                if (grade != 0) return grade;
            }
            return string.Compare(left?.Name, right?.Name, StringComparison.Ordinal);
        }

        private static bool ContainsIgnoreCase(string value, string query)
            => !string.IsNullOrEmpty(value)
                && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

        private static void PopulateGiftChoice(Choice choice, int available)
        {
            if (choice == null || choice.Item == null) return;
            ItemKey key = choice.Item.Key;
            choice.CategoryIndex = GiftCategoryIndex(key);
            choice.Category = choice.CategoryIndex >= 0
                && choice.CategoryIndex < GiftCategories.Length
                    ? GiftCategories[choice.CategoryIndex] : "杂物";
            string type = GiftTypeLabel(key);
            choice.Meta = type + " · 可分配 " + Math.Max(1, available)
                + (key.ItemType == (sbyte)12 ? " 份" : " 件");
            try
            {
                choice.Grade = ItemTemplateHelper.GetGrade(key.ItemType, key.TemplateId);
            }
            catch { choice.Grade = -1; }
            try
            {
                choice.Icon = ItemTemplateHelper.GetIcon(key.ItemType, key.TemplateId);
            }
            catch { choice.Icon = string.Empty; }
        }

        private static void PopulateSkillChoice(Choice choice, bool life)
        {
            if (choice == null || choice.Skill == null) return;
            choice.IsLifeSkill = life;
            choice.Sect = string.Empty;
            choice.Icon = string.Empty;
            try
            {
                if (life)
                {
                    Config.LifeSkillItem cfg =
                        Config.LifeSkill.Instance[choice.Skill.TemplateId];
                    choice.Grade = cfg.Grade;
                    choice.CategoryIndex = Mathf.Clamp(cfg.Type + 1, 1,
                        LifeCategories.Length - 1);
                    choice.Category = LifeCategories[choice.CategoryIndex];
                    try
                    {
                        Config.LifeSkillTypeItem type = Config.LifeSkillType.Instance[cfg.Type];
                        if (type != null && !string.IsNullOrWhiteSpace(type.Name))
                            choice.Category = type.Name.Trim();
                    }
                    catch { }
                    try
                    {
                        Config.SkillBookItem book = Config.SkillBook.Instance[cfg.SkillBookId];
                        if (book != null) choice.Icon = book.Icon ?? string.Empty;
                    }
                    catch { }
                    choice.Meta = choice.Category + " · " + GradeText(choice.Grade);
                    return;
                }

                Config.CombatSkillItem combat =
                    Config.CombatSkill.Instance[choice.Skill.TemplateId];
                choice.Grade = combat.Grade;
                choice.CategoryIndex = Mathf.Clamp(combat.EquipType + 1, 1,
                    CombatCategories.Length - 1);
                choice.Category = CombatCategories[choice.CategoryIndex];
                try
                {
                    Config.CombatSkillTypeItem type =
                        Config.CombatSkillType.Instance[combat.Type];
                    if (type != null && !string.IsNullOrWhiteSpace(type.Name))
                        choice.Category = type.Name.Trim();
                }
                catch { }
                try
                {
                    if (combat.SectId > 0)
                    {
                        Config.OrganizationItem organization =
                            Config.Organization.Instance[combat.SectId];
                        if (organization != null)
                            choice.Sect = (organization.Name ?? string.Empty).Trim();
                    }
                }
                catch { }
                if (string.IsNullOrWhiteSpace(choice.Sect)) choice.Sect = "无门无派";
                try
                {
                    Config.SkillBookItem book = Config.SkillBook.Instance[combat.BookId];
                    if (book != null) choice.Icon = book.Icon ?? string.Empty;
                }
                catch { }
                choice.Meta = choice.Category + " · " + choice.Sect;
            }
            catch
            {
                choice.CategoryIndex = 0;
                choice.Category = life ? "技艺" : "功法";
                choice.Meta = choice.Category;
            }
        }

        private static void RebuildAvailableSects()
        {
            AvailableSects.Clear();
            AvailableSects.Add("全部");
            foreach (string sect in Choices.Where(choice => choice != null
                         && !choice.IsLifeSkill && !string.IsNullOrWhiteSpace(choice.Sect))
                         .Select(choice => choice.Sect).Distinct(StringComparer.Ordinal)
                         .OrderBy(value => value, StringComparer.Ordinal))
                AvailableSects.Add(sect);
        }

        private static int GiftCategoryIndex(ItemKey key)
        {
            try
            {
                if (ItemTemplateHelper.IsMiscResource(key.ItemType, key.TemplateId)) return 7;
            }
            catch { }
            switch (key.ItemType)
            {
                case 7:
                case 9: return 1;
                case 8: return 2;
                case 0:
                case 1:
                case 2:
                case 3:
                case 4: return 3;
                case 10: return 4;
                case 6: return 5;
                case 5: return 6;
                default: return 8;
            }
        }

        private static string GiftTypeLabel(ItemKey key)
        {
            try
            {
                if (ItemTemplateHelper.IsMiscResource(key.ItemType, key.TemplateId))
                    return "资源";
            }
            catch { }
            switch (key.ItemType)
            {
                case 0: return "武器";
                case 1: return "防具";
                case 2: return "饰品";
                case 3: return "衣物";
                case 4: return "坐骑";
                case 5: return "材料";
                case 6: return "工具";
                case 7: return "食物";
                case 8: return "丹药";
                case 9: return "茶酒";
                case 10: return "书籍";
                case 11: return "蟋蟀";
                default: return "杂物";
            }
        }

        private static string GradeText(int grade)
        {
            string[] names = { "九品", "八品", "七品", "六品", "五品", "四品", "三品", "二品", "一品" };
            return grade >= 0 && grade < names.Length ? names[grade] : "无品级";
        }

        private static Color GradeColor(int grade)
        {
            if (grade < 0) return Paper;
            try
            {
                if (Colors.Instance != null && Colors.Instance.GradeColors != null
                    && Colors.Instance.GradeColors.Length > 0)
                    return Colors.Instance.GradeColors[Mathf.Clamp(grade, 0,
                        Colors.Instance.GradeColors.Length - 1)];
            }
            catch { }
            return Paper;
        }

        private static GameObject NewGiftChoiceRow(Choice choice, Transform parent)
        {
            GameObject row = NewChoiceRow(choice?.Name, string.Empty, parent);
            row.name = "GiftChoice";
            row.GetComponent<LayoutElement>().preferredHeight = 66f;
            TextMeshProUGUI name = row.transform.Find("Name")?.GetComponent<TextMeshProUGUI>();
            if (name != null)
            {
                UiFontSizeStore.Bind(name, 16f);
                name.color = GradeColor(choice?.Grade ?? -1);
                name.text = choice?.Name ?? "未命名";
                Anchor(name.rectTransform, Vector2.zero, Vector2.one,
                    new Vector2(104, 30), new Vector2(-348, -5));
            }

            TextMeshProUGUI meta = NewText("Meta", row.transform, 13,
                TextAlignmentOptions.MidlineLeft);
            meta.text = (choice?.Category ?? "杂物") + " · "
                + (choice?.Meta ?? "可分配数量未知");
            meta.color = Muted;
            meta.enableWordWrapping = false;
            meta.overflowMode = TextOverflowModes.Ellipsis;
            Anchor(meta.rectTransform, Vector2.zero, Vector2.one,
                new Vector2(104, 5), new Vector2(-348, -33));

            TextMeshProUGUI stock = NewText("Stock", row.transform, 13,
                TextAlignmentOptions.Center);
            stock.text = "持有 " + Math.Max(1, choice?.Item?.Count ?? 1);
            stock.color = Muted;
            Anchor(stock.rectTransform, new Vector2(1, 0), new Vector2(1, 1),
                new Vector2(-338, 7), new Vector2(-252, -7));

            TextMeshProUGUI grade = NewText("Grade", row.transform, 14,
                TextAlignmentOptions.Center);
            grade.text = GradeText(choice?.Grade ?? -1);
            grade.color = GradeColor(choice?.Grade ?? -1);
            Anchor(grade.rectTransform, new Vector2(1, 0), new Vector2(1, 1),
                new Vector2(-252, 7), new Vector2(-174, -7));
            TryAddIcon(row.transform, choice?.Icon, new Vector2(54, 11), new Vector2(94, -11),
                new Vector2(0, 0), new Vector2(0, 1));
            return row;
        }

        private static GameObject NewSkillChoiceCard(Choice choice, Transform parent)
        {
            GameObject card = new GameObject("SkillChoice", typeof(RectTransform), typeof(Image),
                typeof(Button));
            card.transform.SetParent(parent, false);
            Image image = card.GetComponent<Image>();
            ChatTab.ApplyRoundedSkin(image);
            image.color = RowNormal;
            Button button = card.GetComponent<Button>();
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(0.90f, 0.98f, 0.93f, 1f);
            colors.pressedColor = new Color(0.76f, 0.86f, 0.80f, 1f);
            colors.selectedColor = colors.highlightedColor;
            colors.fadeDuration = 0.08f;
            ApplyButtonColorsNow(button, colors);

            GameObject markerGo = new GameObject("SelectedMark", typeof(RectTransform), typeof(Image));
            markerGo.transform.SetParent(card.transform, false);
            Anchor(markerGo.GetComponent<RectTransform>(), new Vector2(1, 1), new Vector2(1, 1),
                new Vector2(-36, -34), new Vector2(-10, -8));
            Image markerImage = markerGo.GetComponent<Image>();
            ChatTab.ApplyRoundedSkin(markerImage);
            markerImage.color = MarkerNormal;
            GameObject checkGo = new GameObject("Check", typeof(RectTransform), typeof(Image));
            checkGo.transform.SetParent(markerGo.transform, false);
            Anchor(checkGo.GetComponent<RectTransform>(), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), new Vector2(-5, -5), new Vector2(5, 5));
            Image checkImage = checkGo.GetComponent<Image>();
            ChatTab.ApplyRoundedSkin(checkImage);
            checkImage.color = new Color(Paper.r, Paper.g, Paper.b, 0f);
            checkImage.raycastTarget = false;

            TryAddIcon(card.transform, choice?.Icon, new Vector2(-25, -63),
                new Vector2(25, -13), new Vector2(0.5f, 1), new Vector2(0.5f, 1));

            TextMeshProUGUI name = NewText("Name", card.transform, 14,
                TextAlignmentOptions.Center);
            name.text = choice?.Name ?? "未命名";
            name.color = GradeColor(choice?.Grade ?? -1);
            name.enableWordWrapping = true;
            name.overflowMode = TextOverflowModes.Ellipsis;
            Anchor(name.rectTransform, new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(8, -111), new Vector2(-8, -67));

            TextMeshProUGUI meta = NewText("Meta", card.transform, 11,
                TextAlignmentOptions.Center);
            meta.text = choice?.Meta ?? string.Empty;
            meta.color = Muted;
            meta.enableWordWrapping = false;
            meta.overflowMode = TextOverflowModes.Ellipsis;
            Anchor(meta.rectTransform, new Vector2(0, 0), new Vector2(1, 0),
                new Vector2(7, 15), new Vector2(-7, 38));

            GameObject bar = new GameObject("GradeBar", typeof(RectTransform), typeof(Image));
            bar.transform.SetParent(card.transform, false);
            Anchor(bar.GetComponent<RectTransform>(), new Vector2(0, 0), new Vector2(1, 0),
                new Vector2(8, 0), new Vector2(-8, 5));
            Image barImage = bar.GetComponent<Image>();
            ChatTab.ApplyRoundedSkin(barImage);
            barImage.color = GradeColor(choice?.Grade ?? -1);
            barImage.raycastTarget = false;
            return card;
        }

        private static void TryAddIcon(Transform parent, string icon, Vector2 offsetMin,
            Vector2 offsetMax, Vector2 anchorMin, Vector2 anchorMax)
        {
            if (parent == null || string.IsNullOrWhiteSpace(icon)) return;
            try
            {
                GameObject iconGo = new GameObject("Icon", typeof(RectTransform), typeof(CImage));
                iconGo.transform.SetParent(parent, false);
                Anchor(iconGo.GetComponent<RectTransform>(), anchorMin, anchorMax,
                    offsetMin, offsetMax);
                CImage image = iconGo.GetComponent<CImage>();
                image.raycastTarget = false;
                image.SetSprite(icon);
            }
            catch { }
        }

        private static GameObject NewChoiceRow(string name, string meta, Transform parent)
        {
            GameObject row = new GameObject("Choice", typeof(RectTransform), typeof(Image),
                typeof(Button), typeof(LayoutElement));
            row.transform.SetParent(parent, false);
            Image image = row.GetComponent<Image>();
            ChatTab.ApplyRoundedSkin(image);
            image.color = RowNormal;
            row.GetComponent<LayoutElement>().preferredHeight = 60f;
            Button button = row.GetComponent<Button>();
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(0.90f, 0.98f, 0.93f, 1f);
            colors.pressedColor = new Color(0.76f, 0.86f, 0.80f, 1f);
            colors.selectedColor = colors.highlightedColor;
            colors.fadeDuration = 0.08f;
            ApplyButtonColorsNow(button, colors);
            GameObject markerGo = new GameObject("SelectedMark", typeof(RectTransform), typeof(Image));
            markerGo.transform.SetParent(row.transform, false);
            Anchor(markerGo.GetComponent<RectTransform>(), new Vector2(0, 0.5f),
                new Vector2(0, 0.5f), new Vector2(14, -13), new Vector2(40, 13));
            Image markerImage = markerGo.GetComponent<Image>();
            ChatTab.ApplyRoundedSkin(markerImage);
            markerImage.color = MarkerNormal;
            GameObject checkGo = new GameObject("Check", typeof(RectTransform), typeof(Image));
            checkGo.transform.SetParent(markerGo.transform, false);
            Anchor(checkGo.GetComponent<RectTransform>(), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), new Vector2(-5, -5), new Vector2(5, 5));
            Image checkImage = checkGo.GetComponent<Image>();
            ChatTab.ApplyRoundedSkin(checkImage);
            checkImage.color = new Color(Paper.r, Paper.g, Paper.b, 0f);
            checkImage.raycastTarget = false;
            TextMeshProUGUI label = NewText("Name", row.transform, 17,
                TextAlignmentOptions.Left);
            label.text = (name ?? "未命名") + "\n<size=76%>" + (meta ?? "") + "</size>";
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Ellipsis;
            Anchor(label.rectTransform, Vector2.zero, Vector2.one,
                new Vector2(54, 5), new Vector2(-16, -5));
            return row;
        }

        private static void AddGiftAmountInput(Transform row, int choiceIndex)
        {
            if (row == null || choiceIndex < 0 || choiceIndex >= Choices.Count) return;
            TextMeshProUGUI amountLabel = NewText("AmountLabel", row, 13,
                TextAlignmentOptions.MidlineRight);
            amountLabel.text = "数量";
            amountLabel.color = new Color(0.62f, 0.69f, 0.63f, 0.9f);
            Anchor(amountLabel.rectTransform, new Vector2(1, 0), new Vector2(1, 1),
                new Vector2(-170, 8), new Vector2(-106, -8));
            GameObject inputGo = new GameObject("AmountInput", typeof(RectTransform),
                typeof(Image));
            inputGo.transform.SetParent(row, false);
            Anchor(inputGo.GetComponent<RectTransform>(), new Vector2(1, 0),
                new Vector2(1, 1), new Vector2(-98, 10), new Vector2(-14, -10));
            Image inputImage = inputGo.GetComponent<Image>();
            ChatTab.ApplyRoundedSkin(inputImage);
            inputImage.color = new Color(0.20f, 0.29f, 0.27f, 0.98f);
            SafeChatInputField amountInput = UiInput.Setup(inputGo, _font, 16, false,
                TMP_InputField.LineType.SingleLine, false, null, 8, false,
                out TextMeshProUGUI amountText);
            amountText.alignment = TextAlignmentOptions.Center;
            amountText.color = new Color(0.94f, 0.88f, 0.70f, 1f);
            amountInput.contentType = TMP_InputField.ContentType.IntegerNumber;
            amountInput.characterValidation = TMP_InputField.CharacterValidation.Integer;
            amountInput.SetTextWithoutNotify("1");
            amountInput.onValueChanged.AddListener(value =>
                UpdateAmountFromInput(choiceIndex, amountInput, value, false));
            amountInput.onEndEdit.AddListener(value =>
                UpdateAmountFromInput(choiceIndex, amountInput, value, true));
            ChoiceAmountInputs.Add(amountInput);
        }

        private static void ToggleChoice(int index)
        {
            if (index < 0 || index >= Choices.Count) return;
            if (!SelectedChoices.Add(index)) SelectedChoices.Remove(index);
            if (!ChoiceAmounts.ContainsKey(index)) ChoiceAmounts[index] = 1;
            RefreshChoiceSelection();
        }

        private static void ToggleSelectAllVisible()
        {
            bool allSelected = VisibleChoiceIndices.Count > 0
                && VisibleChoiceIndices.All(index => SelectedChoices.Contains(index));
            foreach (int index in VisibleChoiceIndices)
            {
                if (index < 0 || index >= Choices.Count) continue;
                if (allSelected) SelectedChoices.Remove(index);
                else
                {
                    SelectedChoices.Add(index);
                    if (!ChoiceAmounts.ContainsKey(index)) ChoiceAmounts[index] = 1;
                }
            }
            RefreshChoiceSelection();
        }

        private static void UpdateAmountFromInput(int index, TMP_InputField input,
            string value, bool commit)
        {
            if (index < 0 || index >= Choices.Count) return;
            string normalized = (value ?? string.Empty).Trim();
            if (normalized.Length == 0 && !commit) return;
            int max = Math.Max(1, Choices[index].Item?.Count ?? 1);
            int parsed;
            if (!int.TryParse(normalized, out parsed)) parsed = 1;
            parsed = Mathf.Clamp(parsed, 1, max);
            ChoiceAmounts[index] = parsed;
            SelectedChoices.Add(index);
            if (input != null && input.text != parsed.ToString())
                input.SetTextWithoutNotify(parsed.ToString());
            RefreshChoiceSelection();
        }

        private static void RefreshChoiceSelection()
        {
            for (int visibleIndex = 0; visibleIndex < VisibleChoiceIndices.Count; visibleIndex++)
            {
                int index = VisibleChoiceIndices[visibleIndex];
                Choice choice = index >= 0 && index < Choices.Count ? Choices[index] : null;
                bool selected = SelectedChoices.Contains(index);
                if (visibleIndex < ChoiceBackgrounds.Count)
                    ChoiceBackgrounds[visibleIndex].color =
                        selected ? RowSelected : RowNormal;
                if (visibleIndex < ChoiceMarkers.Count && ChoiceMarkers[visibleIndex] != null)
                    ChoiceMarkers[visibleIndex].color = selected ? Bronze : MarkerNormal;
                if (visibleIndex < ChoiceMarkerFills.Count
                    && ChoiceMarkerFills[visibleIndex] != null)
                    ChoiceMarkerFills[visibleIndex].color =
                        new Color(Paper.r, Paper.g, Paper.b, selected ? 1f : 0f);
                if (_kind == TaiwuDirectActionKind.GiveItem
                    && visibleIndex < ChoiceAmountInputs.Count
                    && ChoiceAmountInputs[visibleIndex] != null)
                {
                    string amount = (ChoiceAmounts.TryGetValue(index, out int stored)
                        ? stored : 1).ToString();
                    if (ChoiceAmountInputs[visibleIndex].text != amount)
                        ChoiceAmountInputs[visibleIndex].SetTextWithoutNotify(amount);
                }
            }
            if (_selectionSummary != null)
                _selectionSummary.text = SelectedChoices.Count > 0
                    ? "已选择 " + SelectedChoices.Count + " 项"
                    : SelectionHint();
            if (_selectAllButton != null)
            {
                bool allSelected = VisibleChoiceIndices.Count > 0
                    && VisibleChoiceIndices.All(index => SelectedChoices.Contains(index));
                TextMeshProUGUI label = _selectAllButton.GetComponentInChildren<TextMeshProUGUI>();
                if (label != null) label.text = allSelected ? "取消全选" : "全选";
                _selectAllButton.interactable = VisibleChoiceIndices.Count > 0;
            }
            if (_confirmButton != null)
                _confirmButton.interactable = SelectedChoices.Count > 0;
            if (_confirmLabel != null)
                _confirmLabel.text = HasNextTarget()
                    ? (SelectedChoices.Count == 0 ? "保存后配置下一人"
                        : "保存 " + SelectedChoices.Count + " 项并配置下一人")
                    : SelectedChoices.Count == 0
                        ? (_kind == TaiwuDirectActionKind.GiveItem ? "确认赠送"
                            : _kind == TaiwuDirectActionKind.Teach ? "确认传授" : "写书并赠送")
                        : (_kind == TaiwuDirectActionKind.GiveItem
                            ? "赠送 " + SelectedChoices.Count + " 种"
                            : _kind == TaiwuDirectActionKind.Teach
                                ? "传授 " + SelectedChoices.Count + " 门"
                                : "赠书 " + SelectedChoices.Count + " 本");
        }

        private static void Confirm()
        {
            if (!RequestCurrent() || SelectedChoices.Count == 0)
                return;
            foreach (int index in SelectedChoices.OrderBy(value => value))
            {
                if (index < 0 || index >= Choices.Count) continue;
                Choice choice = Choices[index];
                int amount = ChoiceAmounts.TryGetValue(index, out int stored) ? stored : 1;
                if (_kind == TaiwuDirectActionKind.GiveItem)
                    amount = Mathf.Clamp(amount, 1, Math.Max(1, choice.Item?.Count ?? 1));
                BatchSelections.Add(new TaiwuDirectActionSelection
                {
                    Kind = _kind,
                    TargetId = _targetId,
                    TargetName = _targetName,
                    Item = choice.Item,
                    Skill = choice.Skill,
                    SkillKind = choice.SkillKind,
                    Amount = amount,
                });
            }
            if (HasNextTarget())
            {
                _targetCursor++;
                SetCurrentTarget();
                BeginLoadChoices();
                return;
            }
            var selections = new List<TaiwuDirectActionSelection>(BatchSelections);
            string accompanyingText = (_accompanyingText ?? string.Empty).Trim();
            Action<IList<TaiwuDirectActionSelection>, string> callback = _onConfirm;
            _onConfirm = null;
            Close(false);
            try { callback?.Invoke(selections, accompanyingText); }
            catch (Exception exception)
            {
                Debug.LogWarning("[江湖有灵] 太吾行动选择回调异常："
                    + exception.GetType().Name);
            }
        }

        private static bool HasNextTarget()
            => _targetCursor >= 0 && _targetCursor + 1 < ActiveTargetIndices.Count;

        private static string TargetProgressLabel()
        {
            int total = Math.Max(1, ActiveTargetIndices.Count);
            int current = Mathf.Clamp(_targetCursor + 1, 1, total);
            return total > 1
                ? "对象 " + current + "/" + total + "：" + _targetName
                : "对象：" + _targetName;
        }

        private static int ReservedGiftCount(ItemKey key)
        {
            int total = 0;
            foreach (TaiwuDirectActionSelection selection in BatchSelections)
                if (selection != null && selection.Kind == TaiwuDirectActionKind.GiveItem
                    && selection.Item != null && Equals(selection.Item.Key, key))
                    total += Math.Max(1, selection.Amount);
            return total;
        }

        private static GiftableItem CloneGiftableItem(GiftableItem source, int available)
        {
            return new GiftableItem
            {
                Name = source.Name,
                Key = source.Key,
                Count = available,
                NonEquippedCount = Math.Min(
                    source.NonEquippedCount > 0 ? source.NonEquippedCount : available, available),
                EquippedCount = source.EquippedCount,
                MerchantOwnerType = source.MerchantOwnerType,
                MerchantOwnerId = source.MerchantOwnerId,
            };
        }

        private static bool RequestCurrent(long version = -1)
            => _root != null && _root.activeSelf
                && (version < 0 || version == _requestVersion)
                && _worldId > 0 && WorldLifecycle.WorldId == _worldId
                && WorldLifecycle.IsSameWorld(_worldGeneration);

        private static void Fail(string reason)
        {
            Action<string> callback = _onFailure;
            _onFailure = null;
            Close(false);
            try { callback?.Invoke(reason); } catch { }
        }

        internal static void CloseForWorldExit() => Close(false);

        private static void Close(bool notifyFailure)
        {
            GameObject root = _root;
            _root = null;
            unchecked { _requestVersion++; }
            if (root != null)
            {
                try { PopupRegistry.Unregister(root); } catch { }
                try { UnityEngine.Object.Destroy(root); } catch { }
            }
            _body = null;
            _list = null;
            _title = null;
            _subtitle = null;
            _empty = null;
            _selectionSummary = null;
            _search = null;
            _noteInput = null;
            _confirmButton = null;
            _confirmLabel = null;
            _backButton = null;
            _targetId = 0;
            _targetName = null;
            _targetCursor = -1;
            Targets.Clear();
            Choices.Clear();
            ChoiceRows.Clear();
            ChoiceBackgrounds.Clear();
            ChoiceMarkers.Clear();
            ChoiceMarkerFills.Clear();
            ChoiceLabels.Clear();
            ChoiceAmountInputs.Clear();
            VisibleChoiceIndices.Clear();
            SelectedChoices.Clear();
            ChoiceAmounts.Clear();
            TargetBackgrounds.Clear();
            TargetMarkers.Clear();
            TargetMarkerFills.Clear();
            SelectedTargetIndices.Clear();
            ActiveTargetIndices.Clear();
            FilterButtons.Clear();
            SectButtons.Clear();
            SkillTabButtons.Clear();
            GiftSortButtons.Clear();
            AvailableSects.Clear();
            BatchSelections.Clear();
            _accompanyingText = string.Empty;
            _searchText = string.Empty;
            _giftCategoryFilter = 0;
            _giftSortMode = 0;
            _skillTab = 0;
            _skillCategoryFilter = 0;
            _skillSectFilter = 0;
            _onConfirm = null;
            if (notifyFailure)
            {
                Action<string> failure = _onFailure;
                _onFailure = null;
                try { failure?.Invoke("已取消"); } catch { }
            }
            else _onFailure = null;
        }

        private static void ClearBody()
        {
            if (_body == null) return;
            for (int i = _body.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_body.GetChild(i).gameObject);
            _list = null;
            _empty = null;
            _selectionSummary = null;
            _search = null;
            _noteInput = null;
            _confirmButton = null;
            _confirmLabel = null;
            _backButton = null;
            ChoiceRows.Clear();
            ChoiceBackgrounds.Clear();
            ChoiceMarkers.Clear();
            ChoiceMarkerFills.Clear();
            ChoiceLabels.Clear();
            ChoiceAmountInputs.Clear();
            VisibleChoiceIndices.Clear();
            TargetBackgrounds.Clear();
            TargetMarkers.Clear();
            TargetMarkerFills.Clear();
            FilterButtons.Clear();
            SectButtons.Clear();
            SkillTabButtons.Clear();
            GiftSortButtons.Clear();
        }

        private static void ClearList(RectTransform list)
        {
            if (list == null) return;
            for (int i = list.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(list.GetChild(i).gameObject);
        }

        private static GameObject NewButton(string name, Transform parent, string label,
            float size, out Button button)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(Image),
                typeof(Button));
            go.transform.SetParent(parent, false);
            button = go.GetComponent<Button>();
            Image image = go.GetComponent<Image>();
            ChatTab.ApplyRoundedSkin(image);
            image.color = new Color(1f, 1f, 1f, 0.10f);
            TextMeshProUGUI text = NewText("Label", go.transform, size,
                TextAlignmentOptions.Center);
            text.text = label;
            Anchor(text.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            return go;
        }

        private static void StyleButton(GameObject go, bool primary)
        {
            if (go == null) return;
            Image image = go.GetComponent<Image>();
            Button button = go.GetComponent<Button>();
            if (image != null) image.color = Color.white;
            if (button != null)
            {
                ColorBlock colors = button.colors;
                colors.normalColor = primary ? Bronze : Jade;
                colors.highlightedColor = primary
                    ? new Color(0.77f, 0.66f, 0.40f, 1f) : JadeHover;
                colors.pressedColor = primary
                    ? new Color(0.56f, 0.45f, 0.25f, 1f)
                    : new Color(0.16f, 0.23f, 0.21f, 1f);
                colors.selectedColor = colors.highlightedColor;
                colors.disabledColor = new Color(colors.normalColor.r,
                    colors.normalColor.g, colors.normalColor.b, 0.34f);
                colors.fadeDuration = 0.08f;
                ApplyButtonColorsNow(button, colors);
            }
        }

        private static void ApplyButtonColorsNow(Button button, ColorBlock colors)
        {
            if (button == null) return;
            colors.colorMultiplier = 1f;
            button.transition = Selectable.Transition.ColorTint;
            button.colors = colors;
            // 动态按钮的 Button 组件会先以默认白色启用；设置新 ColorBlock 本身不会
            // 重新执行当前状态。零时长同步一次，首帧即使用墨绿/铜金，不再闪白。
            if (button.targetGraphic != null)
            {
                Color current = button.IsInteractable()
                    ? colors.normalColor : colors.disabledColor;
                button.targetGraphic.CrossFadeColor(
                    current * colors.colorMultiplier, 0f, true, true);
            }
        }

        private static TextMeshProUGUI NewText(string name, Transform parent, float size,
            TextAlignmentOptions alignment)
        {
            GameObject go = new GameObject(name, typeof(RectTransform),
                typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            TextMeshProUGUI text = go.GetComponent<TextMeshProUGUI>();
            TMP_FontAsset font = _font != null ? _font : ResolveFont();
            if (font != null) text.font = font;
            UiFontSizeStore.Bind(text, size);
            text.alignment = alignment;
            text.richText = true;
            text.extraPadding = true;
            text.raycastTarget = false;
            text.color = new Color(0.92f, 0.90f, 0.82f, 1f);
            return text;
        }

        private static void Anchor(RectTransform rect, Vector2 min, Vector2 max,
            Vector2 offsetMin, Vector2 offsetMax)
        {
            rect.anchorMin = min;
            rect.anchorMax = max;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }

        private static string ActionTitle(TaiwuDirectActionKind kind)
            => kind == TaiwuDirectActionKind.GiveItem ? "主动赠物"
                : kind == TaiwuDirectActionKind.Teach ? "亲自传授"
                : "写书赠送";

        private static string SearchPlaceholder()
            => _kind == TaiwuDirectActionKind.GiveItem
                ? "搜索物品或银钱……"
                : "搜索武学或技艺……";

        private static string SelectionHint()
            => _kind == TaiwuDirectActionKind.GiveItem
                ? "勾选物品并填写数量"
                : _kind == TaiwuDirectActionKind.Teach
                    ? "可同时勾选多门武学或技艺"
                    : "可同时勾选多门内容并分别写成书";

        private static TMP_FontAsset ResolveFont()
        {
            try
            {
                foreach (TextMeshProUGUI text in
                    Resources.FindObjectsOfTypeAll<TextMeshProUGUI>())
                    if (text != null && text.font != null
                        && text.gameObject.scene.IsValid())
                        return text.font;
            }
            catch { }
            return null;
        }
    }
}
