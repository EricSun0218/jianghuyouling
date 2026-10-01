using System;
using System.Text;
using JianghuYouling.Core.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace JianghuYouling
{
    /// <summary>Mod、开源与自愿支持的本地固定弹窗：只做排版与滚动，不读取存档、不调用模型。</summary>
    internal static class ModIntroductionWindow
    {
        const string SupportQrResourceName =
            "JianghuYouling.Frontend.Assets.workshop-detail-afdian-qr-cropped.png";
        const long MaxSupportQrBytes = 2L * 1024L * 1024L;
        // 与聊天窗、记录窗和月度纪事的外部标题色 (0.95, 0.92, 0.82) 保持一致。
        const string WarmGoldHex = "#F2EBD1";

        enum ContentMode { Mod, OpenSource, Support }

        static GameObject _root;
        static TMP_FontAsset _font;
        static ScrollRect _scroll;
        static TextMeshProUGUI _title, _subtitle, _body, _supportBody,
            _supportOverview, _supportQrCaption, _acknowledgements, _localHint;
        static GameObject _supportLayoutGo, _supportQrGo, _supportAcknowledgementsGo,
            _openGithubGo, _copyLinkGo;
        static LayoutElement _acknowledgementsLayout;
        static Texture2D _supportQrTexture;
        static Sprite _supportQrSprite;
        static bool _fontSizeSubscribed;

        public static void Open(TMP_FontAsset font)
        {
            if (font != null) _font = font;
            if (_root == null) Build();
            if (_root == null) return;
            ApplyMode(ContentMode.Mod);
            Show();
        }

        public static void OpenOpenSource(TMP_FontAsset font)
        {
            if (font != null) _font = font;
            if (_root == null) Build();
            if (_root == null) return;
            ApplyMode(ContentMode.OpenSource);
            Show();
        }

        public static void OpenSupport(TMP_FontAsset font)
        {
            if (font != null) _font = font;
            if (_root == null) Build();
            if (_root == null) return;
            ApplyMode(ContentMode.Support);
            Show();
        }

        static void Show()
        {
            _root.SetActive(true);
            _root.transform.SetAsLastSibling();
            if (_scroll != null)
            {
                Canvas.ForceUpdateCanvases();
                _scroll.verticalNormalizedPosition = 1f;
            }
        }

        public static void Hide()
        {
            if (_root != null) _root.SetActive(false);
        }

        public static void ResetForWorldExit()
        {
            if (_fontSizeSubscribed)
            {
                UiFontSizeStore.Changed -= OnFontSizeChanged;
                _fontSizeSubscribed = false;
            }
            GameObject root = _root;
            _root = null;
            _scroll = null;
            _title = _subtitle = _body = _supportBody = _supportOverview = _supportQrCaption =
                _acknowledgements = _localHint = null;
            _supportLayoutGo = _supportQrGo = _supportAcknowledgementsGo =
                _openGithubGo = _copyLinkGo = null;
            _acknowledgementsLayout = null;
            if (root != null)
            {
                try { PopupRegistry.Unregister(root); } catch { }
                try { UnityEngine.Object.Destroy(root); } catch { }
            }
            if (_supportQrSprite != null)
                try { UnityEngine.Object.Destroy(_supportQrSprite); } catch { }
            if (_supportQrTexture != null)
                try { UnityEngine.Object.Destroy(_supportQrTexture); } catch { }
            _supportQrSprite = null;
            _supportQrTexture = null;
        }

        static void Build()
        {
            if (_font == null) _font = ResolveFont();
            _root = new GameObject("JHYL_ModIntroduction", typeof(Canvas), typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            UnityEngine.Object.DontDestroyOnLoad(_root);
            Canvas canvas = _root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30055;
            CanvasScaler scaler = _root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            GameObject dim = NewImage("Dim", _root.transform, new Color(0f, 0f, 0f, 0.64f));
            Anchor(dim.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            Button dimButton = dim.AddComponent<Button>();
            dimButton.transition = Selectable.Transition.None;
            dimButton.onClick.AddListener(Hide);

            GameObject frame = NewImage("Frame", _root.transform, new Color(0.23f, 0.29f, 0.27f, 0.96f));
            RectTransform frameRt = frame.GetComponent<RectTransform>();
            frameRt.anchorMin = frameRt.anchorMax = frameRt.pivot = new Vector2(0.5f, 0.5f);
            frameRt.sizeDelta = new Vector2(1164f, 824f);

            GameObject panel = NewImage("Panel", frame.transform, new Color(0.10f, 0.11f, 0.10f, 0.97f));
            Anchor(panel.GetComponent<RectTransform>(), Vector2.zero, Vector2.one,
                new Vector2(2f, 2f), new Vector2(-2f, -2f));

            GameObject header = NewImage("Header", panel.transform, new Color(0.14f, 0.16f, 0.15f, 0.98f));
            Anchor(header.GetComponent<RectTransform>(), new Vector2(0f, 1f), Vector2.one,
                new Vector2(0f, -104f), Vector2.zero);
            DragMove drag = header.AddComponent<DragMove>();
            drag.target = frameRt;

            _title = NewText("Title", header.transform, 27f, TextAlignmentOptions.Left);
            _title.fontStyle = FontStyles.Bold;
            Anchor(_title.rectTransform, new Vector2(0f, 0.5f), new Vector2(1f, 1f),
                new Vector2(26f, -2f), new Vector2(-330f, -14f));

            _subtitle = NewText("Subtitle", header.transform, 15f, TextAlignmentOptions.Left);
            Anchor(_subtitle.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0.5f),
                new Vector2(27f, 13f), new Vector2(-330f, 2f));

            GameObject closeGo = NewButton("Close", header.transform, "X", 20f,
                new Color(0.18f, 0.22f, 0.21f, 0.98f), out Button close);
            RectTransform closeRt = closeGo.GetComponent<RectTransform>();
            closeRt.anchorMin = closeRt.anchorMax = closeRt.pivot = new Vector2(1f, 1f);
            closeRt.anchoredPosition = new Vector2(-14f, -14f);
            closeRt.sizeDelta = new Vector2(44f, 42f);
            close.onClick.AddListener(Hide);

            _openGithubGo = NewButton("OpenGithub", header.transform, "打开 GitHub", 16f,
                new Color(0.20f, 0.29f, 0.27f, 0.98f), out Button openGithub);
            RectTransform openGithubRt = _openGithubGo.GetComponent<RectTransform>();
            openGithubRt.anchorMin = openGithubRt.anchorMax = openGithubRt.pivot = new Vector2(1f, 1f);
            openGithubRt.anchoredPosition = new Vector2(-66f, -14f);
            openGithubRt.sizeDelta = new Vector2(128f, 42f);
            openGithub.onClick.AddListener(() => Application.OpenURL(OpenSourceProjectContent.Url));

            _copyLinkGo = NewButton("CopyLink", header.transform, "复制链接", 16f,
                new Color(0.18f, 0.22f, 0.21f, 0.98f), out Button copyLink);
            RectTransform copyLinkRt = _copyLinkGo.GetComponent<RectTransform>();
            copyLinkRt.anchorMin = copyLinkRt.anchorMax = copyLinkRt.pivot = new Vector2(1f, 1f);
            copyLinkRt.anchoredPosition = new Vector2(-202f, -14f);
            copyLinkRt.sizeDelta = new Vector2(112f, 42f);
            copyLink.onClick.AddListener(CopyOpenSourceLink);

            GameObject accent = NewImage("Accent", panel.transform, new Color(0.48f, 0.43f, 0.31f, 0.26f));
            Anchor(accent.GetComponent<RectTransform>(), new Vector2(0f, 1f), Vector2.one,
                new Vector2(22f, -108f), new Vector2(-22f, -105f));

            GameObject scrollGo = new GameObject("IntroductionScroll", typeof(RectTransform),
                typeof(Image), typeof(ScrollRect), typeof(RectMask2D));
            scrollGo.transform.SetParent(panel.transform, false);
            Anchor(scrollGo.GetComponent<RectTransform>(), Vector2.zero, Vector2.one,
                new Vector2(22f, 56f), new Vector2(-22f, -120f));
            scrollGo.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.22f);
            _scroll = scrollGo.GetComponent<ScrollRect>();
            _scroll.horizontal = false;
            _scroll.vertical = true;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.scrollSensitivity = 34f;

            GameObject contentGo = new GameObject("Content", typeof(RectTransform),
                typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            RectTransform content = contentGo.GetComponent<RectTransform>();
            content.SetParent(scrollGo.transform, false);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.offsetMin = Vector2.zero;
            content.offsetMax = Vector2.zero;
            VerticalLayoutGroup layout = contentGo.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(30, 42, 24, 32);
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandHeight = false;
            layout.spacing = 14f;
            contentGo.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _body = NewText("Body", content, 17f, TextAlignmentOptions.TopLeft);
            _body.enableWordWrapping = true;
            _body.richText = true;
            _body.lineSpacing = 7f;
            _body.color = new Color(0.92f, 0.90f, 0.82f, 1f);
            _body.raycastTarget = false;

            // 只有顶部自愿支持区采用双栏：左侧账号与无对价说明，右侧二维码。
            // 项目介绍与可增长的鸣谢名单都在其下恢复全宽单栏。
            _supportLayoutGo = new GameObject("SupportTwoColumn", typeof(RectTransform),
                typeof(LayoutElement));
            _supportLayoutGo.transform.SetParent(content, false);
            LayoutElement supportLayout = _supportLayoutGo.GetComponent<LayoutElement>();
            supportLayout.preferredHeight = 252f;
            supportLayout.flexibleHeight = 0f;

            _supportBody = NewText("SupportBody", _supportLayoutGo.transform, 17f,
                TextAlignmentOptions.TopLeft);
            _supportBody.enableWordWrapping = true;
            _supportBody.richText = true;
            _supportBody.lineSpacing = 7f;
            _supportBody.color = new Color(0.92f, 0.90f, 0.82f, 1f);
            _supportBody.raycastTarget = false;
            Anchor(_supportBody.rectTransform, Vector2.zero, Vector2.one,
                Vector2.zero, new Vector2(-258f, 0f));

            _supportQrGo = NewImage("SupportQrCard", _supportLayoutGo.transform,
                new Color(0.14f, 0.16f, 0.15f, 0.98f));
            ChatTab.ApplyRoundedSkin(_supportQrGo.GetComponent<Image>());
            RectTransform qrCardRt = _supportQrGo.GetComponent<RectTransform>();
            qrCardRt.anchorMin = qrCardRt.anchorMax = qrCardRt.pivot = new Vector2(1f, 1f);
            qrCardRt.anchoredPosition = Vector2.zero;
            qrCardRt.sizeDelta = new Vector2(230f, 244f);

            TextMeshProUGUI qrTitle = NewText("SupportQrTitle", _supportQrGo.transform, 17f,
                TextAlignmentOptions.Center);
            qrTitle.fontStyle = FontStyles.Bold;
            qrTitle.color = new Color(0.95f, 0.92f, 0.82f, 1f);
            qrTitle.text = "爱发电 · 自愿支持";
            qrTitle.raycastTarget = false;
            Anchor(qrTitle.rectTransform, new Vector2(0f, 1f), Vector2.one,
                new Vector2(16f, -43f), new Vector2(-16f, -11f));

            GameObject qrImageGo = NewImage("SupportQrImage", _supportQrGo.transform, Color.white);
            RectTransform qrImageRt = qrImageGo.GetComponent<RectTransform>();
            qrImageRt.anchorMin = qrImageRt.anchorMax = qrImageRt.pivot = new Vector2(0.5f, 1f);
            qrImageRt.anchoredPosition = new Vector2(0f, -46f);
            qrImageRt.sizeDelta = new Vector2(150f, 150f);
            Image qrImage = qrImageGo.GetComponent<Image>();
            qrImage.preserveAspect = true;
            qrImage.raycastTarget = false;

            _supportQrCaption = NewText("SupportQrCaption", _supportQrGo.transform, 15f,
                TextAlignmentOptions.Center);
            _supportQrCaption.color = new Color(0.61f, 0.72f, 0.64f, 0.95f);
            _supportQrCaption.raycastTarget = false;
            Anchor(_supportQrCaption.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(16f, 9f), new Vector2(-16f, 38f));
            if (TryLoadSupportQr(out Sprite supportQr))
            {
                qrImage.sprite = supportQr;
                _supportQrCaption.text = "可扫码自愿支持";
            }
            else
            {
                qrImageGo.SetActive(false);
                _supportQrCaption.text = "二维码载入失败 · 可使用爱发电账号：天选之人srf";
            }

            _supportOverview = NewText("SupportOverview", content, 17f,
                TextAlignmentOptions.TopLeft);
            _supportOverview.enableWordWrapping = true;
            _supportOverview.richText = true;
            _supportOverview.lineSpacing = 7f;
            _supportOverview.color = new Color(0.92f, 0.90f, 0.82f, 1f);
            _supportOverview.raycastTarget = false;
            _supportOverview.text = FormatBody(SupportDevelopmentContent.OverviewText);

            _supportAcknowledgementsGo = NewImage("AcknowledgementsCard", content,
                new Color(0.14f, 0.16f, 0.15f, 0.98f));
            ChatTab.ApplyRoundedSkin(_supportAcknowledgementsGo.GetComponent<Image>());
            _acknowledgementsLayout = _supportAcknowledgementsGo.AddComponent<LayoutElement>();
            _acknowledgementsLayout.minHeight = 116f;
            _acknowledgementsLayout.preferredHeight = 116f;
            _acknowledgementsLayout.flexibleHeight = 0f;

            _acknowledgements = NewText("Acknowledgements", _supportAcknowledgementsGo.transform, 17f,
                TextAlignmentOptions.TopLeft);
            _acknowledgements.enableWordWrapping = true;
            _acknowledgements.richText = true;
            _acknowledgements.lineSpacing = 7f;
            _acknowledgements.color = new Color(0.92f, 0.90f, 0.82f, 1f);
            _acknowledgements.raycastTarget = false;
            Anchor(_acknowledgements.rectTransform, Vector2.zero, Vector2.one,
                new Vector2(20f, 12f), new Vector2(-20f, -12f));
            _acknowledgements.text = FormatAcknowledgements();
            RefreshAcknowledgementsHeight();

            _scroll.content = content;
            _scroll.verticalScrollbar = UiScroll.AddVerticalForScrollRect(scrollGo, 13f);
            _scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;

            _localHint = NewText("LocalHint", panel.transform, 14f,
                TextAlignmentOptions.MidlineLeft);
            _localHint.color = new Color(0.61f, 0.68f, 0.62f, 0.82f);
            Anchor(_localHint.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(24f, 12f), new Vector2(-24f, 50f));

            try { PopupRegistry.Register(_root, Hide); } catch { }
            if (!_fontSizeSubscribed)
            {
                UiFontSizeStore.Changed += OnFontSizeChanged;
                _fontSizeSubscribed = true;
            }
            ApplyMode(ContentMode.Mod);
            Canvas.ForceUpdateCanvases();
            _scroll.verticalNormalizedPosition = 1f;
        }

        static void ApplyMode(ContentMode mode)
        {
            bool openSource = mode == ContentMode.OpenSource;
            bool support = mode == ContentMode.Support;
            if (_title != null)
            {
                _title.text = support ? "自愿支持开发 · 与江湖同行"
                    : openSource ? "江湖有灵 · MIT 开源" : "江湖有灵 · 完整介绍";
                _title.color = new Color(0.95f, 0.92f, 0.82f, 1f);
            }
            if (_subtitle != null)
            {
                _subtitle.text = support ? "完全自愿  ·  无专属权益"
                    : openSource ? "太吾绘卷 Mod  ·  MIT 许可证  ·  源码与贡献"
                    : "NPC 真行动  ·  长期记忆  ·  群聊与过月江湖";
                _subtitle.color = new Color(0.61f, 0.72f, 0.64f, 0.95f);
            }
            if (_body != null)
            {
                _body.gameObject.SetActive(!support);
                if (!support)
                    _body.text = FormatBody(openSource
                        ? OpenSourceProjectContent.FullText : ModIntroductionContent.FullText);
            }
            if (_supportBody != null)
                _supportBody.text = FormatBody(SupportDevelopmentContent.SupportText);
            if (_supportOverview != null)
            {
                _supportOverview.gameObject.SetActive(support);
                _supportOverview.text = FormatBody(SupportDevelopmentContent.OverviewText);
            }
            if (_localHint != null)
                _localHint.text = support ? "固定本地文案与二维码 · 不调用模型 · 不消耗 Token"
                    : openSource ? "由《江湖有灵》Mod 作者开源 · 查看源码并欢迎点亮 Star"
                    : "固定本地文案 · 不调用模型 · 不消耗 Token";
            // 三种介绍和“往事成书”都复用聊天窗的深墨面板，仅以标题和正文区分内容。
            if (_supportLayoutGo != null) _supportLayoutGo.SetActive(support);
            if (_supportAcknowledgementsGo != null) _supportAcknowledgementsGo.SetActive(support);
            if (support) RefreshAcknowledgementsHeight();
            if (_copyLinkGo != null) _copyLinkGo.SetActive(openSource);
            if (_openGithubGo != null) _openGithubGo.SetActive(openSource);
        }

        static bool TryLoadSupportQr(out Sprite sprite)
        {
            sprite = _supportQrSprite;
            if (sprite != null) return true;
            Texture2D texture = null;
            Sprite loadedSprite = null;
            try
            {
                var assembly = typeof(ModIntroductionWindow).Assembly;
                using (var stream = assembly.GetManifestResourceStream(SupportQrResourceName))
                {
                    if (stream == null || stream.Length <= 0 || stream.Length > MaxSupportQrBytes)
                        return false;
                    var bytes = new byte[(int)stream.Length];
                    int offset = 0;
                    while (offset < bytes.Length)
                    {
                        int read = stream.Read(bytes, offset, bytes.Length - offset);
                        if (read <= 0) return false;
                        offset += read;
                    }
                    texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                    {
                        name = "JHYL_SupportQrTexture",
                        filterMode = FilterMode.Point,
                        wrapMode = TextureWrapMode.Clamp,
                        hideFlags = HideFlags.HideAndDontSave
                    };
                    if (!ImageConversion.LoadImage(texture, bytes)) return false;
                    if (texture.width <= 0 || texture.height <= 0
                        || texture.width > 1024 || texture.height > 1024) return false;
                    loadedSprite = Sprite.Create(texture,
                        new Rect(0f, 0f, texture.width, texture.height),
                        new Vector2(0.5f, 0.5f), 100f, 0u, SpriteMeshType.FullRect);
                    if (loadedSprite == null) return false;
                    loadedSprite.name = "JHYL_SupportQrSprite";
                    loadedSprite.hideFlags = HideFlags.HideAndDontSave;
                    _supportQrTexture = texture;
                    texture = null;
                    _supportQrSprite = loadedSprite;
                    loadedSprite = null;
                    sprite = _supportQrSprite;
                    return true;
                }
            }
            catch { return false; }
            finally
            {
                if (loadedSprite != null) UnityEngine.Object.Destroy(loadedSprite);
                if (texture != null) UnityEngine.Object.Destroy(texture);
            }
        }

        static void CopyOpenSourceLink()
        {
            try
            {
                GUIUtility.systemCopyBuffer = OpenSourceProjectContent.Url;
                if (_localHint != null) _localHint.text = "项目链接已复制到剪贴板";
            }
            catch
            {
                if (_localHint != null) _localHint.text = "复制失败，请在正文末尾手动查看项目地址";
            }
        }

        static string FormatBody(string source)
        {
            string[] lines = (source ?? string.Empty).Replace("\r", string.Empty).Split('\n');
            var sb = new StringBuilder((source ?? string.Empty).Length + 1200);
            bool firstText = true;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i] ?? string.Empty;
                if (line.Length == 0)
                {
                    sb.Append('\n');
                    continue;
                }
                if (line.StartsWith("【", StringComparison.Ordinal))
                {
                    sb.Append("<size=135%><b><color=").Append(WarmGoldHex).Append(">").Append(line)
                        .Append("</color></b></size>");
                }
                else if (IsActionCategory(line))
                {
                    sb.Append("<size=112%><b><color=#8EAAA0>").Append(line)
                        .Append("</color></b></size>");
                }
                else if (line.StartsWith("- ", StringComparison.Ordinal))
                {
                    sb.Append("<color=").Append(WarmGoldHex).Append(">·</color> <color=#D9D6CB>")
                        .Append(line.Substring(2)).Append("</color>");
                }
                else if (firstText)
                {
                    sb.Append("<size=112%><color=").Append(WarmGoldHex).Append(">")
                        .Append(line).Append("</color></size>");
                }
                else
                {
                    sb.Append("<color=#C7C9BE>").Append(line).Append("</color>");
                }
                firstText = false;
                if (i < lines.Length - 1) sb.Append('\n');
            }
            return sb.ToString();
        }

        static string FormatAcknowledgements()
        {
            return "<size=118%><b><color=" + WarmGoldHex + ">【鸣谢】</color></b></size>\n"
                + "<size=106%><color=" + WarmGoldHex + ">"
                + SupportDevelopmentContent.AcknowledgementNames + "</color></size>\n"
                + "<color=#C7C9BE>" + SupportDevelopmentContent.AcknowledgementNote + "</color>";
        }

        static void RefreshAcknowledgementsHeight()
        {
            if (_acknowledgements == null || _acknowledgementsLayout == null) return;
            // 当前内容宽度约 1000px；名单增长后 TMP 会自然换行，卡片随之向下扩展。
            float textHeight = _acknowledgements.GetPreferredValues(
                _acknowledgements.text ?? string.Empty, 990f, 0f).y;
            _acknowledgementsLayout.preferredHeight = Mathf.Max(116f, textHeight + 28f);
        }

        static void OnFontSizeChanged()
        {
            RefreshAcknowledgementsHeight();
            try { Canvas.ForceUpdateCanvases(); } catch { }
        }

        static bool IsActionCategory(string line)
            => line == "物品与买卖" || line == "武学与成长" || line == "关系、情绪与名望"
                || line == "冲突与生存" || line == "行走与江湖事务";

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
            text.color = new Color(0.90f, 0.89f, 0.83f, 1f);
            return text;
        }

        static GameObject NewButton(string name, Transform parent, string label, float size,
            Color normal, out Button button)
        {
            GameObject go = NewImage(name, parent, normal);
            ChatTab.ApplyRoundedSkin(go.GetComponent<Image>());
            button = go.AddComponent<Button>();
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.08f, 1.08f, 1.05f, 1f);
            colors.pressedColor = new Color(0.78f, 0.82f, 0.79f, 1f);
            colors.disabledColor = new Color(0.45f, 0.45f, 0.45f, 0.42f);
            colors.fadeDuration = 0.08f;
            button.colors = colors;
            TextMeshProUGUI text = NewText("Label", go.transform, size, TextAlignmentOptions.Center);
            text.text = label;
            text.raycastTarget = false;
            Anchor(text.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            return go;
        }

        static void Anchor(RectTransform rt, Vector2 min, Vector2 max, Vector2 offMin, Vector2 offMax)
        {
            rt.anchorMin = min;
            rt.anchorMax = max;
            rt.offsetMin = offMin;
            rt.offsetMax = offMax;
        }

        static TMP_FontAsset ResolveFont()
        {
            try
            {
                foreach (TextMeshProUGUI text in Resources.FindObjectsOfTypeAll<TextMeshProUGUI>())
                    if (text != null && text.font != null && text.gameObject.scene.IsValid()) return text.font;
            }
            catch { }
            return null;
        }
    }
}
