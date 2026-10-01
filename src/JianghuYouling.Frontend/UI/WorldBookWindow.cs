using System.Collections;
using System.Collections.Generic;
using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace JianghuYouling
{
    /// <summary>
    /// 世界书设定面板:显示当前生效世界书,玩家可直接改写为完整自定义文本。
    /// 一局一文件(Worldbook_{taiwu}.txt);还原默认=恢复内置太吾世界观。复用 PersonaWindow 的画布/字体/控件写法。
    /// </summary>
    public static class WorldBookWindow
    {
        static GameObject _root;
        static TextMeshProUGUI _title, _status;
        static TMP_InputField _input;
        static Button _saveButton, _resetButton;
        static TMP_FontAsset _font;
        static int _taiwuId;
        static int _contextVersion;
        static bool _saving;
        static long _activeOperationToken;
        static long _draftVersion;
        static bool _settingInputText;

        const string OperationOwner = "worldbook-window";

        sealed class StoreResult
        {
            public bool Ok;
            public string Text;
            public string Error;
            public SettingsBatchTransaction Transaction;
        }

        const int MaxLen = WorldBookStore.MaxCustomWorldBookChars;

        public static void Open(int taiwuId, TMP_FontAsset font)
        {
            unchecked { _contextVersion++; }
            if (font != null) _font = font;
            if (_root == null) Build();
            _root.SetActive(true);
            if (_saving)
            {
                if (_saveButton != null) _saveButton.interactable = false;
                if (_resetButton != null) _resetButton.interactable = false;
                SetStatus("上一项世界书保存仍在后台完成；当前草稿会保留，完成后即可继续保存。", false);
                return;
            }
            _taiwuId = taiwuId;
            if (LongTextEditorOperationGate.IsBusy)
            {
                SetInputText("");
                if (_input != null) _input.interactable = false;
                if (_saveButton != null) _saveButton.interactable = false;
                if (_resetButton != null) _resetButton.interactable = false;
                SetStatus("另一个世界书、人设或设置操作仍在进行；为避免显示旧内容，请稍候关闭后重开。", false);
                return;
            }
            if (_input != null) _input.interactable = true;
            if (_saveButton != null) _saveButton.interactable = !_saving;
            if (_resetButton != null) _resetButton.interactable = !_saving;
            SetInputText(WorldBookStore.EffectiveWorldBookText(_taiwuId));
            MoveInputToEnd(_input);
            SetStatus("(这里显示当前生效的世界书。改写后点保存即保存为完整自定义文本;点还原默认则回到内置太吾世界观。)\n" +
                "进阶:普通行=常驻背景;「@关键词,关键词2:内容」=只在对话提到该关键词时才登场的词条;「!内容」=临场铁令,出话前必守。", false);
            if (_input != null) _input.ActivateInputField();
        }

        public static void Hide() { unchecked { _contextVersion++; } if (_root != null) _root.SetActive(false); }

        public static void ResetForWorldExit()
        {
            Hide();
            // Do not release the shared lease here: the old worker may still be writing/rolling
            // back its captured old-world path. Invalidate only this window's UI ownership; the
            // completion coroutine will release the lease without touching the new window.
            if (_saving) { _activeOperationToken = 0; _saving = false; }
            _taiwuId = 0;
            SetInputText("");
        }

        static void Save()
        {
            if (_taiwuId <= 0) { SetStatus("(太吾信息缺失,无法保存)", true); return; }
            string text = _input != null ? (_input.text ?? "") : "";
            if (text.Length > MaxLen)
            {
                SetStatus("世界书超过 " + MaxLen + " 字上限，请删减后再保存。", true);
                return;
            }
            int taiwuId = _taiwuId;
            string personasDirectory = JianghuYoulingPaths.Personas;
            BeginStoreOperation(new[] { WorldBookPath(personasDirectory, taiwuId),
                StructuredWorldBookStore.LayoutPath(personasDirectory, taiwuId) }, () =>
                {
                    bool ok = WorldBookStore.SaveEffectiveFromDirectory(personasDirectory, taiwuId, text);
                    if (ok) ok = StructuredWorldBookStore.InvalidateFromDirectory(personasDirectory, taiwuId);
                    return new StoreResult { Ok = ok };
                },
                value => SetStatus(!value.Ok ? "保存失败,请检查文件权限或日志。"
                    : "已保存当前世界书。若内容等同默认文本,则已自动还原默认。", !value.Ok));
        }

        static void ResetDefault()
        {
            if (_taiwuId <= 0) { SetStatus("(太吾信息缺失,无法还原)", true); return; }
            int taiwuId = _taiwuId;
            string personasDirectory = JianghuYoulingPaths.Personas;
            BeginStoreOperation(new[] { WorldBookPath(personasDirectory, taiwuId),
                StructuredWorldBookStore.LayoutPath(personasDirectory, taiwuId) }, () =>
            {
                bool ok = WorldBookStore.ClearFromDirectory(personasDirectory, taiwuId);
                if (ok) ok = StructuredWorldBookStore.InvalidateFromDirectory(personasDirectory, taiwuId);
                return new StoreResult { Ok = ok, Text = ok
                    ? WorldBookStore.EffectiveWorldBookTextFromDirectory(personasDirectory, taiwuId) : null };
            }, value =>
            {
                if (value.Ok) SetInputText(value.Text ?? "");
                if (value.Ok) MoveInputToEnd(_input);
                SetStatus(value.Ok ? "世界书已还原默认太吾世界观。" : "还原失败,请检查文件权限或日志。", !value.Ok);
            });
        }

        static string WorldBookPath(string personasDirectory, int taiwuId)
            => Path.Combine(personasDirectory, "Worldbook_" + taiwuId + ".txt");

        static void BeginStoreOperation(IEnumerable<string> transactionPaths,
            Func<StoreResult> work, Action<StoreResult> complete)
        {
            if (_saving || work == null) return;
            if (!LongTextEditorOperationGate.TryAcquire(OperationOwner, out long operationToken))
            {
                SetStatus("另一个世界书、人设或设置保存仍在进行，请稍候再试。", true);
                return;
            }
            _activeOperationToken = operationToken;
            _saving = true;
            if (_saveButton != null) _saveButton.interactable = false;
            if (_resetButton != null) _resetButton.interactable = false;
            SetStatus("保存中……长文本会在后台写入，界面仍可正常响应。", false);
            int version = _contextVersion;
            long draftVersion = _draftVersion;
            int worldGeneration = WorldLifecycle.Generation;
            uint worldId = WorldLifecycle.WorldId;
            var host = ConfigHost.Instance;
            if (host == null)
            {
                FinishOwnedOperation(operationToken);
                if (_saveButton != null) _saveButton.interactable = true;
                if (_resetButton != null) _resetButton.interactable = true;
                SetStatus("保存任务无法启动：界面宿主缺失。", true);
                return;
            }
            Task<StoreResult> task = Task.Run(() =>
            {
                SettingsBatchTransaction tx = null;
                bool handedOff = false;
                try
                {
                    tx = SettingsBatchTransaction.Capture(transactionPaths);
                    StoreResult result = work() ?? new StoreResult { Ok = false, Error = "没有写入结果" };
                    if (!result.Ok) return result;
                    result.Transaction = tx;
                    handedOff = true;
                    return result;
                }
                catch (Exception ex) { return new StoreResult { Ok = false, Error = ex.GetType().Name }; }
                finally { if (!handedOff) tx?.Dispose(); }
            });
            host.StartCoroutine(WaitStoreOperation(task, version, operationToken, draftVersion,
                worldGeneration, worldId, complete));
        }

        static IEnumerator WaitStoreOperation(Task<StoreResult> task, int version, long operationToken,
            long draftVersion, int worldGeneration, uint worldId, Action<StoreResult> complete)
        {
            while (task != null && !task.IsCompleted) yield return null;
            if (_activeOperationToken != operationToken
                || !WorldLifecycle.IsSameWorld(worldGeneration) || WorldLifecycle.WorldId != worldId)
            {
                // A save/reset that finishes after leaving its world must not become the new
                // world's initial value even when character ids happen to be reused.
                if (task != null && !task.IsFaulted) task.Result?.Transaction?.Dispose();
                LongTextEditorOperationGate.Release(OperationOwner, operationToken);
                yield break;
            }
            if (!LongTextEditorOperationGate.IsOwner(OperationOwner, operationToken)) yield break;
            StoreResult result = task == null || task.IsFaulted
                ? new StoreResult { Ok = false, Error = "后台保存任务失败" }
                : task.Result;
            if (result.Ok) result.Transaction?.Commit();
            else result.Transaction?.Dispose();
            FinishOwnedOperation(operationToken);
            if (_saveButton != null) _saveButton.interactable = true;
            if (_resetButton != null) _resetButton.interactable = true;
            if (_draftVersion != draftVersion)
            {
                SetStatus(result.Ok
                    ? "后台保存已完成；已保留你在保存期间继续编辑的未保存草稿。"
                    : "后台保存失败；已保留当前草稿，请检查日志后重试。", !result.Ok);
                yield break;
            }
            if (version != _contextVersion)
            {
                if (_root != null && _root.activeSelf && _taiwuId > 0 && _input != null)
                {
                    if (_draftVersion == draftVersion && result.Ok)
                    {
                        SetInputText(WorldBookStore.EffectiveWorldBookText(_taiwuId));
                        MoveInputToEnd(_input);
                        SetStatus("后台保存已结束，已刷新当前世界书。", false);
                    }
                    else
                        SetStatus(result.Ok
                            ? "后台保存已完成；已保留你重开窗口后继续编辑的未保存草稿。"
                            : "后台保存失败；已保留当前草稿，请检查日志后重试。", !result.Ok);
                }
                yield break;
            }
            if (_root == null || !_root.activeSelf) yield break;
            complete?.Invoke(result);
        }

        static void FinishOwnedOperation(long operationToken)
        {
            if (_activeOperationToken != operationToken
                || !LongTextEditorOperationGate.Release(OperationOwner, operationToken)) return;
            _activeOperationToken = 0;
            _saving = false;
        }

        static void SetInputText(string text)
        {
            if (_input == null) return;
            _settingInputText = true;
            try { _input.SetTextWithoutNotify(text ?? ""); }
            finally { _settingInputText = false; }
        }

        static void MoveInputToEnd(TMP_InputField input)
        {
            // JHYL_WORLDBOOK_OPEN_AT_END:世界书默认打开显示末尾,方便玩家直接追加。
            if (input == null) return;
            int len = input.text == null ? 0 : input.text.Length;
            try
            {
                input.caretPosition = len;
                input.stringPosition = len;
                input.selectionStringAnchorPosition = len;
                input.selectionStringFocusPosition = len;
                input.ForceLabelUpdate();
                if (input.verticalScrollbar != null) input.verticalScrollbar.value = 1f;   // TopToBottom 语义:1=文末
            }
            catch { }
            var host = ConfigHost.Instance;
            if (host != null) host.StartCoroutine(MoveInputToEndNextFrame(input));
        }

        static IEnumerator MoveInputToEndNextFrame(TMP_InputField input)
        {
            yield return null;
            if (input == null) yield break;
            int len = input.text == null ? 0 : input.text.Length;
            try
            {
                input.caretPosition = len;
                input.stringPosition = len;
                input.selectionStringAnchorPosition = len;
                input.selectionStringFocusPosition = len;
                input.ForceLabelUpdate();
                if (input.verticalScrollbar != null) input.verticalScrollbar.value = 1f;   // TopToBottom 语义:1=文末
            }
            catch { }
        }

        static void SetStatus(string text, bool error)
        {
            if (_status == null) return;
            _status.text = text;
            _status.color = error ? new Color(0.92f, 0.55f, 0.45f, 1f) : new Color(0.70f, 0.84f, 0.66f, 1f);
        }

        static void Build()
        {
            _root = new GameObject("JHYL_WorldBookCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            UnityEngine.Object.DontDestroyOnLoad(_root);
            var cv = _root.GetComponent<Canvas>();
            cv.renderMode = RenderMode.ScreenSpaceOverlay;
            cv.sortingOrder = 30012;
            PopupRegistry.Register(_root, Hide);   // 右键逐个关闭(从最上层依次)
            var sc = _root.GetComponent<CanvasScaler>();
            sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            sc.referenceResolution = new Vector2(1920, 1080);

            var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(_root.transform, false);
            var prt = panel.GetComponent<RectTransform>();
            prt.sizeDelta = new Vector2(680, 520);
            prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0.5f, 0.5f);
            prt.anchoredPosition = Vector2.zero;
            panel.GetComponent<Image>().color = new Color(0.10f, 0.11f, 0.10f, 0.97f);

            _title = NewText("Title", panel.transform, 24, TextAlignmentOptions.Center);
            Anchor(_title.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(48, -8), new Vector2(-48, -48));
            _title.text = "世界书 · 自定义世界设定";

            var close = NewButton("Close", panel.transform, "X", 22, out var closeBtn);
            var crt = close.GetComponent<RectTransform>();
            crt.anchorMin = crt.anchorMax = crt.pivot = new Vector2(1, 1);
            crt.anchoredPosition = new Vector2(-8, -8); crt.sizeDelta = new Vector2(36, 36);
            closeBtn.onClick.AddListener(Hide);

            var inputGo = new GameObject("WorldBookInput", typeof(RectTransform), typeof(Image));
            inputGo.transform.SetParent(panel.transform, false);
            Anchor(inputGo.GetComponent<RectTransform>(), new Vector2(0, 0), new Vector2(1, 1),
                new Vector2(16, 110), new Vector2(-16, -56));
            inputGo.GetComponent<Image>().color = new Color(1, 1, 1, 0.10f);

            _input = inputGo.AddComponent<LongTextInputField>();   // 长文整篇粘贴只替换/通知/排版一次
            // 视口(RectMask2D 裁剪)+ 内层文本:长内容可超出视口高度,随光标/滚轮上下滚动
            var viewport = new GameObject("TextArea", typeof(RectTransform), typeof(RectMask2D));
            viewport.transform.SetParent(inputGo.transform, false);
            var vrt = viewport.GetComponent<RectTransform>();
            Anchor(vrt, Vector2.zero, Vector2.one, new Vector2(10, 8), new Vector2(-24, -8));   // 右侧留滚动条位
            var textArea = NewText("Text", viewport.transform, 18, TextAlignmentOptions.TopLeft);
            textArea.richText = false;   // 超长可编辑正文不做富文本解析，避免每次输入都重扫几十万字符标签
            Anchor(textArea.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            textArea.enableWordWrapping = true;
            _input.textViewport = vrt;
            _input.textComponent = textArea;
            _input.lineType = TMP_InputField.LineType.MultiLineNewline;
            _input.contentType = TMP_InputField.ContentType.Standard;
            _input.richText = false;
            _input.characterLimit = MaxLen + 1;
            _input.verticalScrollbar = UiScroll.AddVertical(inputGo);   // 长文可滚(滚轮+拖条)
            _input.onValueChanged.AddListener(_ => { if (!_settingInputText) unchecked { _draftVersion++; } });

            _status = NewText("Status", panel.transform, 16, TextAlignmentOptions.Left);
            Anchor(_status.rectTransform, new Vector2(0, 0), new Vector2(1, 0), new Vector2(16, 64), new Vector2(-16, 104));
            _status.enableWordWrapping = true;

            var saveGo = NewButton("Save", panel.transform, "保存", 19, out _saveButton);
            Anchor(saveGo.GetComponent<RectTransform>(), new Vector2(0, 0), new Vector2(0, 0), new Vector2(16, 14), new Vector2(150, 54));
            _saveButton.onClick.AddListener(Save);

            var resetGo = NewButton("ResetDefault", panel.transform, "还原默认", 19, out _resetButton);
            Anchor(resetGo.GetComponent<RectTransform>(), new Vector2(0, 0), new Vector2(0, 0), new Vector2(166, 14), new Vector2(320, 54));
            _resetButton.onClick.AddListener(ResetDefault);

            var closeBottomGo = NewButton("CloseBottom", panel.transform, "关闭", 20, out var closeBottomBtn);
            Anchor(closeBottomGo.GetComponent<RectTransform>(), new Vector2(1, 0), new Vector2(1, 0), new Vector2(-150, 14), new Vector2(-16, 54));
            closeBottomBtn.onClick.AddListener(Hide);
        }

        static TextMeshProUGUI NewText(string name, Transform parent, float size, TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<TextMeshProUGUI>();
            if (_font != null) t.font = _font;
            UiFontSizeStore.Bind(t, size); t.alignment = align; t.richText = true;
            t.color = new Color(0.92f, 0.90f, 0.82f, 1f);
            return t;
        }

        static GameObject NewButton(string name, Transform parent, string label, float size, out Button btn)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = new Color(0.30f, 0.38f, 0.36f, 0.95f);
            btn = go.GetComponent<Button>();
            var t = NewText("L", go.transform, size, TextAlignmentOptions.Center);
            Anchor(t.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            t.text = label; t.raycastTarget = false;
            return go;
        }

        static void Anchor(RectTransform rt, Vector2 min, Vector2 max, Vector2 offMin, Vector2 offMax)
        {
            rt.anchorMin = min; rt.anchorMax = max; rt.offsetMin = offMin; rt.offsetMax = offMax;
        }
    }
}
