using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using JianghuYouling.Core.Commission;
using JianghuYouling.Effects;

namespace JianghuYouling
{
    /// <summary>右侧“委”入口打开的全局委托簿。读取列表与进度不调用模型，领奖由后端权威结算。</summary>
    public static class CommissionWindow
    {
        private static GameObject _root;
        private static RectTransform _content;
        private static TMP_FontAsset _font;
        private static int _taiwuId;
        private static int _currentDate;
        private static int _worldGeneration = -1;
        private static uint _worldId;
        private static long _renderVersion;
        private static readonly HashSet<string> ConfirmAbandon =
            new HashSet<string>(StringComparer.Ordinal);

        private static readonly Color Panel = new Color(0.09f, 0.105f, 0.095f, 0.985f);
        private static readonly Color Card = new Color(0.15f, 0.165f, 0.15f, 0.98f);
        private static readonly Color CardDone = new Color(0.12f, 0.14f, 0.13f, 0.94f);
        private static readonly Color Text = new Color(0.94f, 0.91f, 0.82f, 1f);
        private static readonly Color Muted = new Color(0.66f, 0.69f, 0.65f, 1f);
        private static readonly Color ButtonColor = new Color(0.26f, 0.39f, 0.36f, 0.98f);
        private static readonly Color Danger = new Color(0.48f, 0.25f, 0.22f, 0.96f);

        public static bool IsOpen => _root != null && _root.activeSelf;

        public static void Open(int taiwuId, TMP_FontAsset font)
        {
            if (taiwuId <= 0 || !WorldLifecycle.HasWorldIdentity) return;
            _taiwuId = taiwuId;
            _worldGeneration = WorldLifecycle.Generation;
            _worldId = WorldLifecycle.WorldId;
            try { _currentDate = SingletonObject.getInstance<BasicGameData>().CurrDate; }
            catch { _currentDate = -1; }
            if (_currentDate < 0) return;
            if (font != null) _font = font;
            if (_font == null) _font = FindFont();
            if (_root == null) Build();
            _root.SetActive(true);
            _root.transform.SetAsLastSibling();
            Render();
        }

        public static void RefreshIfOpen()
        {
            if (!IsOpen) return;
            try { _currentDate = SingletonObject.getInstance<BasicGameData>().CurrDate; }
            catch { }
            Render();
        }

        public static void Hide()
        {
            unchecked { _renderVersion++; }
            ConfirmAbandon.Clear();
            if (_root != null) _root.SetActive(false);
        }

        public static void ResetForWorldExit()
        {
            Hide();
            _taiwuId = 0; _currentDate = -1; _worldGeneration = -1; _worldId = 0;
            ClearChildren(_content);
        }

        private static bool Current(long version)
            => version == _renderVersion && IsOpen && _worldId > 0
                && WorldLifecycle.IsSameWorld(_worldGeneration)
                && WorldLifecycle.WorldId == _worldId;

        private static void Render()
        {
            if (!WorldLifecycle.IsSameWorld(_worldGeneration)
                || WorldLifecycle.WorldId != _worldId) { Hide(); return; }
            unchecked { _renderVersion++; }
            long version = _renderVersion;
            ClearChildren(_content);
            List<CommissionRecord> records = CommissionStore.AllForTaiwu(_taiwuId,
                _currentDate);
            if (records.Count == 0)
            {
                AddInfo("尚无人物向太吾发布委托。\n\nNPC 会在对话中自主提出；灵儿会按设置的现实时间间隔用代码随机生成委托，允许多项同时进行，品级概率随本体世界进度变化且各品级始终有机会。全部进行中任务只作为她的对话上下文，不写入长期记忆。右侧【委】负责查看记录、进度和领取奖励。");
                return;
            }
            int active = 0;
            foreach (CommissionRecord record in records) if (record.IsActive) active++;
            AddInfo("<color=#E7B958><b>进行中 " + active + "</b></color>　"
                + "完成后可直接在右侧委托簿领取；普通任务固定获得2项组合奖励，击杀任务固定获得更丰厚的3至4项组合奖励。",
                16f, Text);
            foreach (CommissionRecord record in records)
                AddRecord(record, version);
        }

        private static void AddRecord(CommissionRecord record, long version)
        {
            var card = new GameObject("CommissionCard", typeof(RectTransform), typeof(Image),
                typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            card.transform.SetParent(_content, false);
            card.GetComponent<Image>().color = record.IsActive ? Card : CardDone;
            var layout = card.GetComponent<VerticalLayoutGroup>();
            layout.childForceExpandWidth = true; layout.childForceExpandHeight = false;
            layout.childControlWidth = true; layout.childControlHeight = true;
            layout.spacing = 9f; layout.padding = new RectOffset(18, 18, 15, 17);
            card.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var header = new GameObject("Header", typeof(RectTransform),
                typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            header.transform.SetParent(card.transform, false);
            var headerLayout = header.GetComponent<HorizontalLayoutGroup>();
            headerLayout.childForceExpandWidth = false; headerLayout.childForceExpandHeight = false;
            headerLayout.childControlWidth = true; headerLayout.childControlHeight = true;
            headerLayout.childAlignment = TextAnchor.MiddleLeft; headerLayout.spacing = 10f;
            header.GetComponent<LayoutElement>().minHeight = 38f;

            TextMeshProUGUI title = NewText("Title", header.transform, 19f,
                TextAlignmentOptions.Left);
            title.text = GlyphSanitizer.Clean("<color=#E7B958><b>" + Escape(record.NpcName)
                + "</b></color>　<size=78%><color=#8FA099>"
                + (record.IsActive ? "进行中" : record.Status == CommissionStore.Claimed
                    ? "已完成" : "已放弃") + "</color></size>");
            title.raycastTarget = false;
            var titleLayout = title.gameObject.AddComponent<LayoutElement>();
            titleLayout.flexibleWidth = 1f; titleLayout.minWidth = 260f;

            TextMeshProUGUI body = NewText("Body", card.transform, 17f,
                TextAlignmentOptions.TopLeft);
            body.enableWordWrapping = true; body.extraPadding = true; body.raycastTarget = false;
            body.color = Text;

            if (!record.IsActive)
            {
                string result = record.Status == CommissionStore.Claimed
                    ? "<color=#84C9A9>获得：</color>" + Escape(record.ActualRewardName ?? "奖励")
                        + (record.ActualRewardAmount > 1 ? " ×" + record.ActualRewardAmount : "")
                    : "<color=#8F9993>这项委托已经放弃。</color>";
                string completion = record.Status == CommissionStore.Claimed
                    ? "\n<color=#CDB87D>委托人：</color>" + Escape(record.CompletionText)
                    : string.Empty;
                body.text = GlyphSanitizer.Clean("<color=#CDB87D>任务：</color>"
                    + Escape(record.Objective) + "\n<color=#CDB87D>承诺：</color>"
                    + Escape(record.RewardLabel) + "\n" + result + completion);
                return;
            }

            body.text = GlyphSanitizer.Clean("<color=#CDB87D>请求：</color>"
                + Escape(record.OfferText) + "\n<color=#CDB87D>任务：</color>"
                + Escape(record.Objective) + "\n<color=#CDB87D>奖励：</color>"
                + Escape(record.RewardLabel) + "\n<color=#8F9993>正在核验进度……</color>");

            var actions = new GameObject("Actions", typeof(RectTransform),
                typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            actions.transform.SetParent(card.transform, false);
            var actionsLayout = actions.GetComponent<HorizontalLayoutGroup>();
            actionsLayout.childForceExpandWidth = false; actionsLayout.childForceExpandHeight = false;
            actionsLayout.childControlWidth = true; actionsLayout.childControlHeight = true;
            actionsLayout.childAlignment = TextAnchor.MiddleRight; actionsLayout.spacing = 10f;
            actions.GetComponent<LayoutElement>().minHeight = 42f;

            Button abandon = null;
            abandon = AddButton(actions.transform, "放弃", 92f, Danger, () =>
            {
                if (!ConfirmAbandon.Add(record.Id))
                {
                    if (CommissionStore.Abandon(_taiwuId, record.Id, _currentDate))
                    {
                        ConfirmAbandon.Remove(record.Id);
                        AssistantWidget.NotifyCommissionChanged();
                        Render();
                    }
                }
                else SetButtonLabel(abandon, "确认放弃");
            });
            Button claim = AddButton(actions.transform, "核验中", 132f, ButtonColor, null);
            claim.interactable = false;

            EffectHandler.QueryCommissionSnapshot(record.TaiwuId, record.NpcId,
                record.TargetNpcId, snapshot =>
            {
                if (!Current(version) || body == null || claim == null) return;
                if (snapshot == null || !snapshot.Ok)
                {
                    body.text = GlyphSanitizer.Clean("<color=#CDB87D>请求：</color>"
                        + Escape(record.OfferText) + "\n<color=#CDB87D>任务：</color>"
                        + Escape(record.Objective) + "\n<color=#CDB87D>奖励：</color>"
                        + Escape(record.RewardLabel) + "\n<color=#D28B76>当前无法读取进度，请稍后重开。</color>");
                    SetButtonLabel(claim, "暂不可用");
                    return;
                }
                int progress = snapshot.ProgressValue(record);
                bool completed = progress >= record.Amount;
                body.text = GlyphSanitizer.Clean("<color=#CDB87D>请求：</color>"
                    + Escape(record.OfferText) + "\n<color=#CDB87D>任务：</color>"
                    + Escape(record.Objective) + "\n<color=#CDB87D>奖励：</color>"
                    + Escape(record.RewardLabel) + "\n<color=#84C9A9>进度："
                    + Math.Min(progress, record.Amount) + " / " + record.Amount
                    + "</color>　<color=#8F9993>完成后可直接领取</color>");
                claim.interactable = completed;
                SetButtonLabel(claim, !completed ? "尚未完成" : "领取奖励");
                claim.onClick.RemoveAllListeners();
                if (claim.interactable)
                    claim.onClick.AddListener(() => Claim(record, claim, body));
            });
        }

        private static void Claim(CommissionRecord record, Button button, TextMeshProUGUI body)
        {
            if (record == null || button == null) return;
            button.interactable = false;
            SetButtonLabel(button, "领取中");
            EffectHandler.ApplyCommissionClaim(record, result =>
            {
                if (!IsOpen || result == null) return;
                if (!result.Success)
                {
                    SetButtonLabel(button, "重新核验");
                    button.interactable = true;
                    if (body != null)
                        body.text += GlyphSanitizer.Clean("\n<color=#D28B76>"
                            + Escape(result?.Message ?? "领奖失败") + "</color>");
                    return;
                }
                if (!CommissionStore.MarkClaimed(record.TaiwuId, record.Id, _currentDate,
                        result.RewardKind, result.RewardName, result.RewardAmount,
                        result.RewardGrade, out CommissionRecord claimed))
                {
                    // 后端稳定 OperationId 已保存奖励回执；再次点击只会重放同一结果，不会重复发奖。
                    SetButtonLabel(button, "保存记录失败，重试");
                    button.interactable = true;
                    return;
                }
                AssistantWidget.NotifyCommissionChanged();
                Render();
            });
        }

        private static void Build()
        {
            _root = new GameObject("JHYL_CommissionCanvas", typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster));
            UnityEngine.Object.DontDestroyOnLoad(_root);
            var canvas = _root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 30062;
            var scaler = _root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            PopupRegistry.Register(_root, Hide);

            var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(_root.transform, false);
            var panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = panelRect.anchorMax = panelRect.pivot = new Vector2(0.5f, 0.5f);
            panelRect.sizeDelta = new Vector2(1120f, 790f);
            panel.GetComponent<Image>().color = Panel;
            panel.AddComponent<DragMove>().target = panelRect;

            TextMeshProUGUI title = NewText("Title", panel.transform, 26f,
                TextAlignmentOptions.Left);
            Anchor(title.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(28f, -62f), new Vector2(-80f, -16f));
            title.text = "人物委托";

            TextMeshProUGUI subtitle = NewText("Subtitle", panel.transform, 14f,
                TextAlignmentOptions.Left);
            Anchor(subtitle.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(30f, -88f), new Vector2(-80f, -62f));
            subtitle.color = Muted;
            subtitle.text = "NPC 在正常交谈中自主发布 · 代码核验完成条件 · 右侧直接领取随机奖励";

            Button close = AddButton(panel.transform, "X", 42f,
                new Color(0.22f, 0.30f, 0.28f, 0.98f), Hide);
            var closeRect = close.GetComponent<RectTransform>();
            closeRect.anchorMin = closeRect.anchorMax = closeRect.pivot = new Vector2(1f, 1f);
            closeRect.anchoredPosition = new Vector2(-12f, -12f);
            closeRect.sizeDelta = new Vector2(42f, 42f);

            var scrollGo = new GameObject("Scroll", typeof(RectTransform), typeof(Image),
                typeof(ScrollRect), typeof(RectMask2D));
            scrollGo.transform.SetParent(panel.transform, false);
            Anchor(scrollGo.GetComponent<RectTransform>(), Vector2.zero, Vector2.one,
                new Vector2(18f, 18f), new Vector2(-18f, -102f));
            scrollGo.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.23f);
            var scroll = scrollGo.GetComponent<ScrollRect>();
            scroll.horizontal = false; scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 30f;

            var contentGo = new GameObject("Content", typeof(RectTransform),
                typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            contentGo.transform.SetParent(scrollGo.transform, false);
            _content = contentGo.GetComponent<RectTransform>();
            _content.anchorMin = new Vector2(0f, 1f); _content.anchorMax = new Vector2(1f, 1f);
            _content.pivot = new Vector2(0.5f, 1f); _content.anchoredPosition = Vector2.zero;
            _content.sizeDelta = Vector2.zero;
            var layout = contentGo.GetComponent<VerticalLayoutGroup>();
            layout.childForceExpandWidth = true; layout.childForceExpandHeight = false;
            layout.childControlWidth = true; layout.childControlHeight = true;
            layout.spacing = 12f; layout.padding = new RectOffset(12, 12, 12, 14);
            contentGo.GetComponent<ContentSizeFitter>().verticalFit =
                ContentSizeFitter.FitMode.PreferredSize;
            scroll.content = _content;
        }

        private static void AddInfo(string value, float size = 17f, Color? color = null)
        {
            TextMeshProUGUI text = NewText("Info", _content, size,
                TextAlignmentOptions.TopLeft);
            text.color = color ?? Muted; text.enableWordWrapping = true;
            text.extraPadding = true; text.margin = new Vector4(12f, 6f, 12f, 8f);
            text.raycastTarget = false; text.text = GlyphSanitizer.Clean(value ?? string.Empty);
        }

        private static Button AddButton(Transform parent, string label, float width,
            Color color, UnityEngine.Events.UnityAction action)
        {
            var go = new GameObject("Button", typeof(RectTransform), typeof(Image),
                typeof(Button), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = color;
            var layout = go.GetComponent<LayoutElement>();
            layout.preferredWidth = width; layout.minWidth = width; layout.preferredHeight = 38f;
            var button = go.GetComponent<Button>();
            if (action != null) button.onClick.AddListener(action);
            TextMeshProUGUI text = NewText("Label", go.transform, 15f,
                TextAlignmentOptions.Center);
            Anchor(text.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            text.text = label; text.raycastTarget = false;
            return button;
        }

        private static void SetButtonLabel(Button button, string value)
        {
            if (button == null) return;
            TextMeshProUGUI label = button.GetComponentInChildren<TextMeshProUGUI>(true);
            if (label != null) label.text = value ?? string.Empty;
        }

        private static TextMeshProUGUI NewText(string name, Transform parent, float size,
            TextAlignmentOptions alignment)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var text = go.GetComponent<TextMeshProUGUI>();
            if (_font == null) _font = FindFont();
            if (_font != null) text.font = _font;
            UiFontSizeStore.Bind(text, size);
            text.alignment = alignment; text.richText = true; text.color = Text;
            return text;
        }

        private static TMP_FontAsset FindFont()
        {
            try
            {
                foreach (TextMeshProUGUI text in Resources.FindObjectsOfTypeAll<TextMeshProUGUI>())
                    if (text != null && text.font != null) return text.font;
            }
            catch { }
            return null;
        }

        private static string Escape(string value)
            => (value ?? string.Empty).Replace("&", "&amp;").Replace("<", "&lt;")
                .Replace(">", "&gt;");

        private static void ClearChildren(RectTransform parent)
        {
            if (parent == null) return;
            for (int i = parent.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(parent.GetChild(i).gameObject);
        }

        private static void Anchor(RectTransform rect, Vector2 min, Vector2 max,
            Vector2 offsetMin, Vector2 offsetMax)
        {
            rect.anchorMin = min; rect.anchorMax = max;
            rect.offsetMin = offsetMin; rect.offsetMax = offsetMax;
        }
    }
}
