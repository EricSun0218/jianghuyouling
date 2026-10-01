using JianghuYouling.Core.Prompt;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace JianghuYouling
{
    /// <summary>隐藏思考表达提示词编辑窗。</summary>
    internal static class ThinkingPromptEditorWindow
    {
        static readonly Color Panel = new Color(0.095f, 0.105f, 0.098f, 0.99f);
        static readonly Color Header = new Color(0.14f, 0.16f, 0.15f, 0.98f);
        static readonly Color Surface = new Color(0.045f, 0.052f, 0.048f, 1f);
        static readonly Color Primary = new Color(0.22f, 0.34f, 0.31f, 1f);
        static readonly Color Secondary = new Color(0.18f, 0.22f, 0.21f, 0.98f);
        static readonly Color Text = new Color(0.91f, 0.89f, 0.82f, 1f);
        static readonly Color Muted = new Color(0.62f, 0.68f, 0.62f, 1f);
        static readonly Color Error = new Color(0.96f, 0.52f, 0.42f, 1f);

        static GameObject _root;
        static TMP_InputField _input;
        static TextMeshProUGUI _status;
        static TMP_FontAsset _font;

        internal static void Show(TMP_FontAsset font)
        {
            Hide();
            _font = font ?? ResolveFont();
            Build();
            LoadCurrentText();
            _input?.ActivateInputField();
            _input?.MoveTextEnd(false);
        }

        internal static void Hide()
        {
            GameObject root = _root;
            _root = null;
            _input = null;
            _status = null;
            if (root == null) return;
            try { PopupRegistry.Unregister(root); } catch { }
            try { Object.Destroy(root); } catch { }
        }

        static void LoadCurrentText()
        {
            if (_input == null) return;
            string value;
            try { value = ThinkingPromptRuleStore.Load(); }
            catch { value = ThinkingPromptRule.DefaultPrompt; }
            _input.SetTextWithoutNotify(string.IsNullOrWhiteSpace(value)
                ? ThinkingPromptRule.DefaultPrompt : value);
            RefreshStatus("当前显示实际生效的提示词；修改后点击保存。", false);
        }

        static void Save()
        {
            string value = (_input?.text ?? string.Empty).Trim();
            if (!ThinkingPromptRule.TryValidate(value, out string error))
            {
                RefreshStatus(string.IsNullOrWhiteSpace(error)
                    ? "提示词为空或不符合保存要求。" : error, true);
                return;
            }
            bool saved;
            try { saved = ThinkingPromptRuleStore.Save(value); }
            catch { saved = false; }
            RefreshStatus(saved ? "提示词已保存并立即生效。" : "提示词保存失败，原设置未改变。",
                !saved);
        }

        static void RestoreDefault()
        {
            bool reset;
            try { reset = ThinkingPromptRuleStore.Reset(); }
            catch { reset = false; }
            if (!reset)
            {
                RefreshStatus("恢复默认失败，原设置未改变。", true);
                return;
            }
            if (_input != null)
            {
                _input.SetTextWithoutNotify(ThinkingPromptRule.DefaultPrompt);
                _input.ActivateInputField();
                _input.MoveTextEnd(false);
            }
            RefreshStatus("已恢复默认并立即生效。", false);
        }

        static void RefreshStatus(string message, bool error)
        {
            if (_status == null) return;
            _status.text = (_input?.text ?? string.Empty).Length + " 字　" + (message ?? string.Empty);
            _status.color = error ? Error : Muted;
        }

        static void Build()
        {
            _root = new GameObject("JHYL_ThinkingPromptEditor", typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster));
            Object.DontDestroyOnLoad(_root);
            Canvas canvas = _root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30060;
            CanvasScaler scaler = _root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            GameObject dim = NewImage("Dim", _root.transform, new Color(0f, 0f, 0f, 0.64f));
            Anchor(dim.GetComponent<RectTransform>(), Vector2.zero, Vector2.one,
                Vector2.zero, Vector2.zero);
            Button dimButton = dim.AddComponent<Button>();
            dimButton.transition = Selectable.Transition.None;
            dimButton.onClick.AddListener(Hide);

            GameObject frame = NewImage("Frame", _root.transform,
                new Color(0.23f, 0.29f, 0.27f, 0.96f));
            RectTransform frameRt = frame.GetComponent<RectTransform>();
            frameRt.anchorMin = frameRt.anchorMax = frameRt.pivot = new Vector2(0.5f, 0.5f);
            frameRt.sizeDelta = new Vector2(1120f, 720f);

            GameObject panel = NewImage("Panel", frame.transform, Panel);
            Anchor(panel.GetComponent<RectTransform>(), Vector2.zero, Vector2.one,
                new Vector2(2f, 2f), new Vector2(-2f, -2f));

            GameObject header = NewImage("Header", panel.transform, Header);
            // 顶部条使用正高度；旧版 offsetMin/offsetMax 颠倒会令按钮落入正文输入层。
            Anchor(header.GetComponent<RectTransform>(), new Vector2(0f, 1f), Vector2.one,
                new Vector2(0f, -94f), Vector2.zero);
            header.AddComponent<DragMove>().target = frameRt;

            TextMeshProUGUI title = NewText("Title", header.transform, 27f,
                TextAlignmentOptions.Left);
            title.fontStyle = FontStyles.Bold;
            title.text = "思考表达提示词";
            Anchor(title.rectTransform, new Vector2(0f, 0.48f), new Vector2(1f, 1f),
                new Vector2(24f, -2f), new Vector2(-330f, -9f));

            TextMeshProUGUI subtitle = NewText("Subtitle", header.transform, 15f,
                TextAlignmentOptions.Left);
            subtitle.color = Muted;
            subtitle.text = "规定模型隐藏思考怎样表达；不会改变 NPC 最终回复的人称与格式。";
            Anchor(subtitle.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0.52f),
                new Vector2(25f, 10f), new Vector2(-330f, 0f));

            GameObject closeGo = NewButton("Close", header.transform, "X", 20f, Secondary,
                out Button close);
            PlaceHeaderButton(closeGo, -14f, 44f);
            close.onClick.AddListener(Hide);

            GameObject saveGo = NewButton("Save", header.transform, "保存", 17f, Primary,
                out Button save);
            PlaceHeaderButton(saveGo, -66f, 96f);
            save.onClick.AddListener(Save);

            GameObject resetGo = NewButton("Reset", header.transform, "恢复默认", 16f,
                Secondary, out Button reset);
            PlaceHeaderButton(resetGo, -170f, 122f);
            reset.onClick.AddListener(RestoreDefault);

            GameObject accent = NewImage("Accent", panel.transform,
                new Color(0.48f, 0.43f, 0.31f, 0.26f));
            Anchor(accent.GetComponent<RectTransform>(), new Vector2(0f, 1f), Vector2.one,
                new Vector2(18f, -99f), new Vector2(-18f, -96f));

            GameObject inputGo = NewImage("PromptInput", panel.transform, Surface);
            Anchor(inputGo.GetComponent<RectTransform>(), Vector2.zero, Vector2.one,
                new Vector2(22f, 68f), new Vector2(-22f, -116f));
            _input = UiInput.Setup(inputGo, _font, 17f, true,
                TMP_InputField.LineType.MultiLineNewline, false, "请输入思考表达提示词",
                ThinkingPromptRule.MaxPromptChars + 1, true, out TextMeshProUGUI textArea);
            if (textArea != null) textArea.richText = false;
            _input.verticalScrollbar = UiScroll.AddVertical(inputGo);
            _input.onValueChanged.AddListener(value =>
                RefreshStatus("尚未保存。", !ThinkingPromptRule.TryValidate(
                    (_input?.text ?? string.Empty).Trim(), out _)));

            _status = NewText("Status", panel.transform, 15f, TextAlignmentOptions.Left);
            _status.color = Muted;
            Anchor(_status.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(24f, 20f), new Vector2(-24f, 56f));

            // 输入框按创建顺序位于顶部条之后，必须把顶部条重新提到同级最上层。
            header.transform.SetAsLastSibling();
            PopupRegistry.Register(_root, Hide);
        }

        static void PlaceHeaderButton(GameObject go, float right, float width)
        {
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 1f);
            rt.anchoredPosition = new Vector2(right, -14f);
            rt.sizeDelta = new Vector2(width, 42f);
        }

        static GameObject NewImage(string name, Transform parent, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = color;
            return go;
        }

        static TextMeshProUGUI NewText(string name, Transform parent, float size,
            TextAlignmentOptions alignment)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            TextMeshProUGUI text = go.GetComponent<TextMeshProUGUI>();
            if (_font != null) text.font = _font;
            UiFontSizeStore.Bind(text, size);
            text.alignment = alignment;
            text.richText = true;
            text.color = Text;
            return text;
        }

        static GameObject NewButton(string name, Transform parent, string label, float size,
            Color color, out Button button)
        {
            GameObject go = NewImage(name, parent, color);
            ChatTab.ApplyRoundedSkin(go.GetComponent<Image>());
            button = go.AddComponent<Button>();
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.08f, 1.08f, 1.05f, 1f);
            colors.pressedColor = new Color(0.78f, 0.82f, 0.79f, 1f);
            colors.disabledColor = new Color(0.58f, 0.62f, 0.59f, 0.72f);
            colors.fadeDuration = 0.08f;
            button.colors = colors;
            TextMeshProUGUI text = NewText("Label", go.transform, size,
                TextAlignmentOptions.Center);
            text.text = label;
            text.raycastTarget = false;
            Anchor(text.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            return go;
        }

        static void Anchor(RectTransform rt, Vector2 min, Vector2 max,
            Vector2 offsetMin, Vector2 offsetMax)
        {
            rt.anchorMin = min;
            rt.anchorMax = max;
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
        }

        static TMP_FontAsset ResolveFont()
        {
            try
            {
                foreach (TextMeshProUGUI text in Resources.FindObjectsOfTypeAll<TextMeshProUGUI>())
                    if (text != null && text.font != null && text.gameObject.scene.IsValid())
                        return text.font;
            }
            catch { }
            return null;
        }
    }
}
