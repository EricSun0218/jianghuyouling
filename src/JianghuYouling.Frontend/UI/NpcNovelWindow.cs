using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JianghuYouling.Core.Llm;
using JianghuYouling.Core.Prompt;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace JianghuYouling
{
    /// <summary>从人物详情页打开的“往事成书”窗口：只使用当前人物的聊天与记忆写小说。</summary>
    public static class NpcNovelWindow
    {
        static GameObject _root;
        static TMP_FontAsset _font;
        static TextMeshProUGUI _title, _subtitle, _sourceHint, _status, _output;
        static TMP_InputField _promptInput;
        static ScrollRect _outputScroll;
        static Button _generateButton, _exportButton, _savePromptButton, _restoreButton;
        static TextMeshProUGUI _generateLabel;
        static int _taiwuId, _npcId;
        static string _npcName;
        static NpcNovelDataService.Scope _scope;
        static CancellationTokenSource _cancellation;
        static long _requestEpoch;

        // 与聊天窗口共用同一套克制的深墨色视觉语汇；写作窗口不再另起一套高饱和金色皮肤。
        static readonly Color Panel = new Color(0.10f, 0.11f, 0.10f, 0.97f);
        static readonly Color Header = new Color(0.14f, 0.16f, 0.15f, 0.98f);
        static readonly Color Surface = new Color(0.072f, 0.080f, 0.075f, 0.99f);
        static readonly Color Ink = new Color(0f, 0f, 0f, 0.22f);
        static readonly Color Jade = new Color(0.20f, 0.29f, 0.27f, 0.98f);
        static readonly Color Primary = new Color(0.68f, 0.57f, 0.34f, 0.98f);
        static readonly Color Text = new Color(0.92f, 0.90f, 0.82f, 1f);
        static readonly Color Muted = new Color(0.61f, 0.68f, 0.62f, 0.82f);

        public static bool IsOpen => _root != null && _root.activeSelf;

        public static void Open(int npcId, int taiwuId, string npcName, TMP_FontAsset font)
        {
            if (font != null) _font = font;
            if (_font == null) _font = ResolveFont();
            if (_root == null) Build();
            if (_root == null) return;

            CancelCurrent();
            _npcId = npcId;
            _taiwuId = taiwuId;
            _npcName = string.IsNullOrWhiteSpace(npcName) ? ("人物" + npcId) : npcName.Trim();
            _scope = NpcNovelDataService.Capture(taiwuId, npcId, _npcName);
            _title.text = "往事成书 · " + _npcName;
            _subtitle.text = "只取你与此人的聊天、此人参与的群聊，以及此人的长期记忆";
            _promptInput.text = NovelPromptStore.Load();
            _promptInput.caretPosition = 0;
            _promptInput.selectionAnchorPosition = 0;
            _promptInput.selectionFocusPosition = 0;

            NpcNovelDataService.Draft draft = _scope == null ? null
                : NpcNovelDataService.LoadDraft(_scope);
            if (draft != null && !string.IsNullOrWhiteSpace(draft.Content))
            {
                SetOutput(draft.Content);
                SetStatus("已载入上次书稿 · " + (draft.GeneratedAt ?? "时间未详"), Muted);
            }
            else
            {
                SetOutput("尚未落笔。\n\n调整左侧写作要求后，点击“开始写作”。");
                SetStatus(_scope == null ? "当前人物或存档状态无效" : "书稿只会使用当前人物的资料",
                    _scope == null ? new Color(0.86f, 0.54f, 0.44f, 1f) : Muted);
            }
            _sourceHint.text = "素材范围\n<color=#D2B46C>当前人物</color> · 单聊 · 参与群聊 · 长期记忆\n"
                + "不读取其他人物的无关对话，不包含思考过程与内部提示。";
            SetBusy(false);
            _root.SetActive(true);
            _root.transform.SetAsLastSibling();
            Canvas.ForceUpdateCanvases();
            if (_outputScroll != null) _outputScroll.verticalNormalizedPosition = 1f;
        }

        public static void Hide()
        {
            CancelCurrent();
            if (_root != null) _root.SetActive(false);
        }

        public static void ResetForWorldExit()
        {
            CancelCurrent();
            GameObject root = _root;
            _root = null;
            _title = _subtitle = _sourceHint = _status = _output = _generateLabel = null;
            _promptInput = null;
            _outputScroll = null;
            _generateButton = _exportButton = _savePromptButton = _restoreButton = null;
            _scope = null;
            _taiwuId = _npcId = 0;
            _npcName = null;
            if (root == null) return;
            try { PopupRegistry.Unregister(root); } catch { }
            try { UnityEngine.Object.Destroy(root); } catch { }
        }

        static void StartGeneration()
        {
            // 每次真正落笔前重新冻结一次素材；窗口打开后可能又收到主动消息或新增记忆，
            // 不能继续使用刚开窗时的旧快照。
            _scope = NpcNovelDataService.Capture(_taiwuId, _npcId, _npcName);
            if (_scope == null) { SetStatus("当前人物或存档状态无效", ErrorColor()); return; }
            string prompt = (_promptInput?.text ?? string.Empty).Trim();
            if (!NpcNovelPromptBuilder.IsValidCustomPrompt(prompt))
            {
                SetStatus("写作要求不能为空，且不能超过 "
                    + NpcNovelPromptBuilder.MaxCustomPromptChars + " 字", ErrorColor());
                return;
            }
            if (!NovelPromptStore.Save(prompt))
            {
                SetStatus("写作要求保存失败，请检查日志目录是否可写", ErrorColor());
                return;
            }
            OpenAiCompatibleClient client = LlmService.GetBackgroundClient();
            if (client == null || !string.IsNullOrWhiteSpace(client.ConfigurationError))
            {
                SetStatus("尚未配置可用的大模型接口", ErrorColor());
                try { ConfigWindow.Open(_font); } catch { }
                return;
            }
            CancelCurrent();
            long epoch = unchecked(++_requestEpoch);
            _cancellation = new CancellationTokenSource();
            SetBusy(true);
            SetStatus("正在整理与 " + _npcName + " 有关的聊天和记忆……", Muted);
            TalkEntryHost host = TalkEntryHost.Instance;
            if (host == null)
            {
                SetBusy(false);
                SetStatus("写作宿主尚未就绪，请稍后重试", ErrorColor());
                return;
            }
            host.StartCoroutine(GenerateRoutine(client, prompt, epoch, _cancellation.Token));
        }

        static IEnumerator GenerateRoutine(OpenAiCompatibleClient client, string prompt,
            long epoch, CancellationToken cancellationToken)
        {
            int maxOutput = NovelMaxOutput(client);
            int inputBudget = client.EffectiveInputBudget(maxOutput, 1024);
            if (inputBudget < 2400)
            {
                FinishError(epoch, "当前模型的上下文上限过小，无法容纳人物素材与小说正文");
                yield break;
            }
            int sourceChars = Math.Max(3000, Math.Min(300000, inputBudget * 2));
            NpcNovelDataService.Scope captured = _scope;
            Task<NpcNovelDataService.SourceResult> sourceTask = Task.Run(
                () => NpcNovelDataService.BuildSource(captured, sourceChars), cancellationToken);
            yield return new WaitUntil(() => sourceTask.IsCompleted || !RequestCurrent(epoch, captured)
                || cancellationToken.IsCancellationRequested);
            if (!RequestCurrent(epoch, captured) || cancellationToken.IsCancellationRequested) yield break;

            NpcNovelDataService.SourceResult source;
            try { source = sourceTask.Result; }
            catch (Exception ex) { FinishError(epoch, "素材整理失败（" + ex.GetType().Name + "）"); yield break; }
            if (source == null || !source.Ok)
            {
                FinishError(epoch, source?.Error ?? "素材整理失败");
                yield break;
            }

            List<LlmMessage> messages = NpcNovelPromptBuilder.Build(_npcName, prompt, source.Source);
            int estimate = PromptBudgeter.Estimate(messages, null);
            if (estimate > inputBudget)
            {
                int reduced = Math.Max(3000,
                    (int)(source.Source.Length * (inputBudget / (double)Math.Max(1, estimate)) * 0.86));
                Task<NpcNovelDataService.SourceResult> reducedTask = Task.Run(
                    () => NpcNovelDataService.BuildSource(captured, reduced), cancellationToken);
                yield return new WaitUntil(() => reducedTask.IsCompleted || !RequestCurrent(epoch, captured)
                    || cancellationToken.IsCancellationRequested);
                if (!RequestCurrent(epoch, captured) || cancellationToken.IsCancellationRequested) yield break;
                try { source = reducedTask.Result; }
                catch (Exception ex) { FinishError(epoch, "素材压缩失败（" + ex.GetType().Name + "）"); yield break; }
                if (source == null || !source.Ok) { FinishError(epoch, source?.Error ?? "素材压缩失败"); yield break; }
                messages = NpcNovelPromptBuilder.Build(_npcName, prompt, source.Source);
                if (PromptBudgeter.Estimate(messages, null) > inputBudget)
                {
                    FinishError(epoch, "当前模型上下文不足，请缩短自定义写作要求或调大上下文设置");
                    yield break;
                }
            }

            SetStatus("素材已就绪：" + source.ChatLines + " 段对话、" + source.MemoryCount
                + " 条记忆。正在写作……", Muted);
            Task<LlmResult> task = client.SendAsync(messages, maxOutput, 0.82,
                cancellationToken, 300, false, "人物小说·" + _npcName, LlmReasoningPolicy.Off);
            yield return new WaitUntil(() => task.IsCompleted || !RequestCurrent(epoch, captured)
                || cancellationToken.IsCancellationRequested);
            if (!RequestCurrent(epoch, captured) || cancellationToken.IsCancellationRequested) yield break;

            LlmResult result;
            try { result = task.Result; }
            catch (Exception ex) { FinishError(epoch, "小说生成失败（" + ex.GetType().Name + "）"); yield break; }
            if (result == null || !result.Ok || string.IsNullOrWhiteSpace(result.Content))
            {
                FinishError(epoch, result?.Error ?? "模型没有返回小说正文");
                yield break;
            }
            string content;
            try { content = NpcNovelDataService.CleanNovel(result.Content,
                ConfiguredSecretSnapshot.LoadOrThrow()); }
            catch (Exception ex) { FinishError(epoch, "正文安全处理失败（" + ex.GetType().Name + "）"); yield break; }
            if (string.IsNullOrWhiteSpace(content))
            {
                FinishError(epoch, "模型没有返回可显示的小说正文");
                yield break;
            }
            SetOutput(content);
            bool saved = NpcNovelDataService.SaveDraft(captured, prompt, content);
            SetBusy(false);
            SetStatus((saved ? "书稿已完成并保存" : "书稿已完成，但本地草稿保存失败")
                + " · " + content.Length + " 字"
                + (source.Truncated ? " · 素材已按重要性与新近度收束" : string.Empty),
                saved ? new Color(0.66f, 0.82f, 0.66f, 1f) : ErrorColor());
            Canvas.ForceUpdateCanvases();
            if (_outputScroll != null) _outputScroll.verticalNormalizedPosition = 1f;
        }

        static void SavePrompt()
        {
            string prompt = (_promptInput?.text ?? string.Empty).Trim();
            bool ok = NovelPromptStore.Save(prompt);
            SetStatus(ok ? "写作要求已保存，之后所有人物共用此设置"
                : "保存失败：写作要求不能为空或内容过长", ok ? Muted : ErrorColor());
        }

        static void RestoreDefault()
        {
            string value = NpcNovelPromptBuilder.DefaultPrompt;
            _promptInput.text = value;
            NovelPromptStore.Save(value);
            SetStatus("已恢复默认的金庸式武侠小说风格", Muted);
        }

        static void StartExport()
        {
            string content = _output?.text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(content) || content.StartsWith("尚未落笔", StringComparison.Ordinal))
            {
                SetStatus("还没有可导出的小说正文", ErrorColor());
                return;
            }
            NpcNovelDataService.Scope captured = _scope;
            long epoch = _requestEpoch;
            SetStatus("正在导出书稿……", Muted);
            TalkEntryHost host = TalkEntryHost.Instance;
            if (host != null) host.StartCoroutine(ExportRoutine(captured, content, epoch));
        }

        static IEnumerator ExportRoutine(NpcNovelDataService.Scope captured, string content, long epoch)
        {
            Task<NpcNovelDataService.ExportResult> task = Task.Run(
                () => NpcNovelDataService.Export(captured, content));
            yield return new WaitUntil(() => task.IsCompleted || !RequestCurrent(epoch, captured));
            if (!RequestCurrent(epoch, captured)) yield break;
            NpcNovelDataService.ExportResult result;
            try { result = task.Result; }
            catch (Exception ex) { SetStatus("小说导出失败（" + ex.GetType().Name + "）", ErrorColor()); yield break; }
            if (result == null || !result.Ok)
            {
                SetStatus(result?.Error ?? "小说导出失败", ErrorColor());
                yield break;
            }
            try { GUIUtility.systemCopyBuffer = result.Path; } catch { }
            SetStatus("小说已导出，文件路径已复制：" + result.Path,
                new Color(0.66f, 0.82f, 0.66f, 1f));
        }

        static void Build()
        {
            _root = new GameObject("JHYL_NpcNovelCanvas", typeof(Canvas), typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            UnityEngine.Object.DontDestroyOnLoad(_root);
            Canvas canvas = _root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30056;
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
            frameRt.sizeDelta = new Vector2(1240f, 800f);
            GameObject panel = NewImage("Panel", frame.transform, Panel);
            Anchor(panel.GetComponent<RectTransform>(), Vector2.zero, Vector2.one,
                new Vector2(2f, 2f), new Vector2(-2f, -2f));

            GameObject header = NewImage("Header", panel.transform, Header);
            Anchor(header.GetComponent<RectTransform>(), new Vector2(0f, 1f), Vector2.one,
                Vector2.zero, new Vector2(0f, -88f));
            DragMove drag = header.AddComponent<DragMove>();
            drag.target = frameRt;

            _title = NewText("Title", header.transform, 27f, TextAlignmentOptions.Left);
            _title.fontStyle = FontStyles.Bold;
            _title.color = new Color(0.95f, 0.92f, 0.82f, 1f);
            Anchor(_title.rectTransform, new Vector2(0f, 0.48f), new Vector2(1f, 1f),
                new Vector2(24f, -2f), new Vector2(-70f, -9f));
            _subtitle = NewText("Subtitle", header.transform, 15f, TextAlignmentOptions.Left);
            _subtitle.color = Muted;
            Anchor(_subtitle.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0.52f),
                new Vector2(25f, 10f), new Vector2(-70f, 0f));

            GameObject closeGo = NewButton("Close", header.transform, "X", 20f,
                new Color(0.18f, 0.22f, 0.21f, 0.98f), out Button close);
            RectTransform closeRt = closeGo.GetComponent<RectTransform>();
            closeRt.anchorMin = closeRt.anchorMax = closeRt.pivot = new Vector2(1f, 1f);
            closeRt.anchoredPosition = new Vector2(-13f, -13f);
            closeRt.sizeDelta = new Vector2(43f, 42f);
            close.onClick.AddListener(Hide);

            GameObject accent = NewImage("Accent", panel.transform, new Color(0.48f, 0.43f, 0.31f, 0.26f));
            Anchor(accent.GetComponent<RectTransform>(), new Vector2(0f, 1f), Vector2.one,
                new Vector2(18f, -93f), new Vector2(-18f, -90f));

            GameObject left = NewImage("WritingDesk", panel.transform, Surface);
            Anchor(left.GetComponent<RectTransform>(), Vector2.zero, Vector2.one,
                new Vector2(18f, 76f), new Vector2(-824f, -106f));
            TextMeshProUGUI sourceTitle = NewText("SourceTitle", left.transform, 19f, TextAlignmentOptions.Left);
            sourceTitle.text = "写作依据";
            sourceTitle.fontStyle = FontStyles.Bold;
            sourceTitle.color = new Color(0.85f, 0.87f, 0.82f, 1f);
            Anchor(sourceTitle.rectTransform, new Vector2(0f, 1f), Vector2.one,
                new Vector2(18f, -44f), new Vector2(-18f, -12f));

            GameObject sourceCard = NewImage("SourceCard", left.transform, Ink);
            Anchor(sourceCard.GetComponent<RectTransform>(), new Vector2(0f, 1f), Vector2.one,
                new Vector2(16f, -150f), new Vector2(-16f, -50f));
            _sourceHint = NewText("SourceHint", sourceCard.transform, 14f, TextAlignmentOptions.TopLeft);
            _sourceHint.enableWordWrapping = true;
            _sourceHint.lineSpacing = 5f;
            _sourceHint.color = new Color(0.61f, 0.68f, 0.62f, 0.92f);
            Anchor(_sourceHint.rectTransform, Vector2.zero, Vector2.one,
                new Vector2(13f, 10f), new Vector2(-13f, -10f));

            TextMeshProUGUI promptTitle = NewText("PromptTitle", left.transform, 18f, TextAlignmentOptions.Left);
            promptTitle.text = "小说风格与写作要求";
            promptTitle.fontStyle = FontStyles.Bold;
            Anchor(promptTitle.rectTransform, new Vector2(0f, 1f), Vector2.one,
                new Vector2(18f, -193f), new Vector2(-18f, -160f));
            GameObject promptGo = NewImage("PromptInput", left.transform, Ink);
            Anchor(promptGo.GetComponent<RectTransform>(), Vector2.zero, Vector2.one,
                new Vector2(16f, 68f), new Vector2(-16f, -198f));
            _promptInput = UiInput.Setup(promptGo, _font, 16f, true,
                TMP_InputField.LineType.MultiLineNewline, false,
                "写下希望采用的文风、篇幅与叙事重点……",
                NpcNovelPromptBuilder.MaxCustomPromptChars + 1, true, out _);

            GameObject restoreGo = NewButton("RestorePrompt", left.transform, "恢复金庸风格", 15f,
                new Color(0.18f, 0.22f, 0.21f, 0.98f), out _restoreButton);
            Anchor(restoreGo.GetComponent<RectTransform>(), new Vector2(0f, 0f), new Vector2(0.5f, 0f),
                new Vector2(16f, 14f), new Vector2(-4f, 56f));
            _restoreButton.onClick.AddListener(RestoreDefault);
            GameObject saveGo = NewButton("SavePrompt", left.transform, "保存写作要求", 15f,
                Jade, out _savePromptButton);
            Anchor(saveGo.GetComponent<RectTransform>(), new Vector2(0.5f, 0f), new Vector2(1f, 0f),
                new Vector2(4f, 14f), new Vector2(-16f, 56f));
            _savePromptButton.onClick.AddListener(SavePrompt);

            GameObject divider = NewImage("Spine", panel.transform, new Color(0.48f, 0.43f, 0.31f, 0.26f));
            Anchor(divider.GetComponent<RectTransform>(), Vector2.zero, Vector2.one,
                new Vector2(424f, 76f), new Vector2(-812f, -106f));

            GameObject right = NewImage("Manuscript", panel.transform, Surface);
            Anchor(right.GetComponent<RectTransform>(), Vector2.zero, Vector2.one,
                new Vector2(438f, 76f), new Vector2(-18f, -106f));
            TextMeshProUGUI outputTitle = NewText("OutputTitle", right.transform, 19f, TextAlignmentOptions.Left);
            outputTitle.text = "往事成书";
            outputTitle.fontStyle = FontStyles.Bold;
            outputTitle.color = new Color(0.85f, 0.87f, 0.82f, 1f);
            Anchor(outputTitle.rectTransform, new Vector2(0f, 1f), Vector2.one,
                new Vector2(20f, -44f), new Vector2(-20f, -12f));

            GameObject scrollGo = new GameObject("NovelScroll", typeof(RectTransform), typeof(Image),
                typeof(ScrollRect), typeof(RectMask2D));
            scrollGo.transform.SetParent(right.transform, false);
            Anchor(scrollGo.GetComponent<RectTransform>(), Vector2.zero, Vector2.one,
                new Vector2(14f, 14f), new Vector2(-14f, -52f));
            scrollGo.GetComponent<Image>().color = Ink;
            _outputScroll = scrollGo.GetComponent<ScrollRect>();
            _outputScroll.horizontal = false;
            _outputScroll.vertical = true;
            _outputScroll.movementType = ScrollRect.MovementType.Clamped;
            _outputScroll.scrollSensitivity = 34f;

            GameObject content = new GameObject("Content", typeof(RectTransform),
                typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            RectTransform contentRt = content.GetComponent<RectTransform>();
            contentRt.SetParent(scrollGo.transform, false);
            contentRt.anchorMin = new Vector2(0f, 1f);
            contentRt.anchorMax = new Vector2(1f, 1f);
            contentRt.pivot = new Vector2(0.5f, 1f);
            contentRt.offsetMin = contentRt.offsetMax = Vector2.zero;
            VerticalLayoutGroup layout = content.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(24, 34, 22, 28);
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandHeight = false;
            content.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _output = NewText("NovelText", content.transform, 18f, TextAlignmentOptions.TopLeft);
            _output.enableWordWrapping = true;
            _output.richText = false;
            _output.lineSpacing = 8f;
            _output.color = Text;
            SelectableChatText.Attach(_output, () => SetStatus("已复制选中的书稿文字", Muted));
            _outputScroll.content = contentRt;
            _outputScroll.verticalScrollbar = UiScroll.AddVerticalForScrollRect(scrollGo, 13f);
            _outputScroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;

            _status = NewText("Status", panel.transform, 14f, TextAlignmentOptions.MidlineLeft);
            _status.enableWordWrapping = false;
            _status.color = Muted;
            Anchor(_status.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(22f, 20f), new Vector2(-490f, 62f));

            GameObject exportGo = NewButton("ExportNovel", panel.transform, "导出书稿", 17f,
                new Color(0.18f, 0.22f, 0.21f, 0.98f), out _exportButton);
            Anchor(exportGo.GetComponent<RectTransform>(), new Vector2(1f, 0f), new Vector2(1f, 0f),
                new Vector2(-466f, 14f), new Vector2(-322f, 62f));
            _exportButton.onClick.AddListener(StartExport);

            GameObject generateGo = NewButton("GenerateNovel", panel.transform, "开始写作", 18f,
                Primary, out _generateButton);
            Anchor(generateGo.GetComponent<RectTransform>(), new Vector2(1f, 0f), new Vector2(1f, 0f),
                new Vector2(-314f, 14f), new Vector2(-154f, 62f));
            _generateLabel = generateGo.GetComponentInChildren<TextMeshProUGUI>();
            _generateButton.onClick.AddListener(StartGeneration);

            GameObject footerCloseGo = NewButton("FooterClose", panel.transform, "关闭", 18f,
                new Color(0.18f, 0.22f, 0.21f, 0.98f), out Button footerClose);
            Anchor(footerCloseGo.GetComponent<RectTransform>(), new Vector2(1f, 0f), new Vector2(1f, 0f),
                new Vector2(-146f, 14f), new Vector2(-18f, 62f));
            footerClose.onClick.AddListener(Hide);

            try { PopupRegistry.Register(_root, Hide); } catch { }
            _root.SetActive(false);
        }

        static void SetBusy(bool busy)
        {
            if (_generateButton != null) _generateButton.interactable = !busy;
            if (_exportButton != null) _exportButton.interactable = !busy;
            if (_savePromptButton != null) _savePromptButton.interactable = !busy;
            if (_restoreButton != null) _restoreButton.interactable = !busy;
            if (_promptInput != null) _promptInput.interactable = !busy;
            if (_generateLabel != null) _generateLabel.text = busy ? "正在写作…" : "开始写作";
        }

        static void SetOutput(string value)
        {
            if (_output != null) _output.text = GlyphSanitizer.Clean(value ?? string.Empty);
        }

        static void SetStatus(string value, Color color)
        {
            if (_status == null) return;
            _status.text = GlyphSanitizer.Clean(value ?? string.Empty);
            _status.color = color;
        }

        static void FinishError(long epoch, string error)
        {
            if (epoch != _requestEpoch) return;
            SetBusy(false);
            SetStatus(error, ErrorColor());
        }

        static bool RequestCurrent(long epoch, NpcNovelDataService.Scope scope)
            => epoch == _requestEpoch && _root != null && _root.activeSelf
                && ReferenceEquals(scope, _scope) && scope != null && scope.IsCurrent;

        static void CancelCurrent()
        {
            unchecked { _requestEpoch++; }
            CancellationTokenSource cancellation = _cancellation;
            _cancellation = null;
            if (cancellation == null) return;
            try { cancellation.Cancel(); } catch { }
            try { cancellation.Dispose(); } catch { }
        }

        static int NovelMaxOutput(OpenAiCompatibleClient client)
        {
            int configured = client?.DefaultMaxTokens ?? 0;
            int output = configured > 0 ? Math.Min(configured, 16384) : 8192;
            int context = client?.ContextWindowTokens ?? 8192;
            if (context < output + 4096) output = Math.Max(1024, context / 4);
            return Math.Max(1024, output);
        }

        static Color ErrorColor() => new Color(0.88f, 0.55f, 0.43f, 1f);

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
            colors.disabledColor = new Color(0.45f, 0.45f, 0.45f, 0.42f);
            colors.fadeDuration = 0.08f;
            button.colors = colors;
            TextMeshProUGUI text = NewText("Label", go.transform, size, TextAlignmentOptions.Center);
            text.text = label;
            text.raycastTarget = false;
            Anchor(text.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            return go;
        }

        static void Anchor(RectTransform rt, Vector2 min, Vector2 max,
            Vector2 offMin, Vector2 offMax)
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
