using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using JianghuYouling.Core.Persona;
using JianghuYouling.Core.Prompt;

namespace JianghuYouling
{
    /// <summary>
    /// NPC 自定义人设面板:显示当前生效人设;玩家可为当前聊天对象保存一段最高权威自定义人设。
    /// 人设在世界目录内按 NPC 稳定保存，传剑换代后不会重置。清空保存=恢复默认画像。
    /// </summary>
    public static class PersonaWindow
    {
        static GameObject _root;
        static TextMeshProUGUI _title, _status;
        static TMP_InputField _input, _appendInput;
        static Button _saveButton, _resetButton, _updateButton;
        static TMP_FontAsset _font;
        static int _npcId = -1, _taiwuId = -1;
        static string _npcName;
        static int _contextVersion;
        static bool _saving;
        static bool _generating;
        static long _activeOperationToken;
        static int _operationTaiwuId, _operationNpcId;
        static long _draftVersion;
        static bool _settingInputText;

        const string OperationOwner = "persona-window";

        sealed class StoreResult
        {
            public bool Ok;
            public string Text;
            public string Error;
            public SettingsBatchTransaction Transaction;
        }

        const int MaxLen = PersonaStore.MaxCustomPersonaChars;
        const float PanelWidth = 980f;
        const float PanelHeight = 720f;

        /// <summary>打开人设面板(仅应在确认为队友后调用)。</summary>
        public static void Open(int npcId, int taiwuId, string npcName, TMP_FontAsset font)
        {
            unchecked { _contextVersion++; }
            if (font != null) _font = font;
            if (_root == null) Build();
            _root.SetActive(true);
            ArmFirstClickSelectAll();

            // A background save owns the ids and directory it captured when it started.  Always
            // bind a newly opened window to the requested character immediately; returning here
            // used to leave NPC A selected when the player opened NPC B during A's save, so the
            // next edit could silently overwrite A again.
            _npcId = npcId; _taiwuId = taiwuId;
            _npcName = string.IsNullOrEmpty(npcName) ? ("NPC#" + npcId) : npcName;
            if (_title != null) _title.text = "为「" + _npcName + "」设定人设";
            if (_saving && _operationTaiwuId == taiwuId && _operationNpcId == npcId)
            {
                if (_saveButton != null) _saveButton.interactable = false;
                if (_resetButton != null) _resetButton.interactable = false;
                if (_updateButton != null) _updateButton.interactable = false;
                SetStatus("该角色的人设仍在后台保存；当前草稿会保留，完成后即可继续保存。", false);
                if (_input != null) _input.ActivateInputField();
                return;
            }
            if (!_saving && LongTextEditorOperationGate.IsBusy)
            {
                SetInputText("");
                if (_input != null) _input.interactable = false;
                if (_saveButton != null) _saveButton.interactable = false;
                if (_resetButton != null) _resetButton.interactable = false;
                if (_updateButton != null) _updateButton.interactable = false;
                SetStatus("另一个世界书、人设或设置操作仍在进行；为避免显示旧内容，请稍候关闭后重开。", false);
                return;
            }
            if (_input != null) _input.interactable = true;
            string effective = EffectivePersonaText();
            if (_input != null)
            {
                // 旧版曾允许不超过 256 KiB 的无标记 ASCII 人设，可能略高于新版字符上限。
                // 完整显示并允许玩家删减，但保存仍执行新版上限，绝不静默截断旧内容。
                _input.characterLimit = System.Math.Max(MaxLen + 1, (effective?.Length ?? 0) + 1);
                SetInputText(effective);
            }
            RefreshAppendDisplay(taiwuId, npcId);

            if (_saving)
            {
                if (_saveButton != null) _saveButton.interactable = false;
                if (_resetButton != null) _resetButton.interactable = false;
                if (_updateButton != null) _updateButton.interactable = false;
                SetStatus("上一项人设保存仍在后台完成；已切换到「" + _npcName
                    + "」，可以继续编辑，后台保存结束后即可保存当前草稿。", false);
                if (_input != null) _input.ActivateInputField();
                return;
            }
            if (_saveButton != null) _saveButton.interactable = true;
            if (_resetButton != null) _resetButton.interactable = true;
            if (_updateButton != null) _updateButton.interactable = true;
            SetStatus((effective?.Length ?? 0) > MaxLen
                ? "旧版人设已完整载入，但超过新版 " + MaxLen + " 字上限；请删减后再保存，未保存前原文件不会改变。"
                : "(这里显示当前生效的人设。改写后点保存即保存为完整自定义人设;点还原默认则恢复自动画像。)",
                (effective?.Length ?? 0) > MaxLen);
            if (_input != null) _input.ActivateInputField();
        }

        public static void Hide() { unchecked { _contextVersion++; } if (_root != null) _root.SetActive(false); }

        public static void ResetForWorldExit()
        {
            Hide();
            if (_saving) { _activeOperationToken = 0; _saving = false; }
            _generating = false;
            _operationTaiwuId = 0; _operationNpcId = 0;
            _npcId = -1; _taiwuId = -1; _npcName = null;
            SetInputText("");
            if (_appendInput != null) _appendInput.SetTextWithoutNotify("");
        }

        static string EffectivePersonaText()
        {
            string custom = PersonaStore.Load(JianghuYoulingPaths.Personas, _taiwuId.ToString(), _npcId.ToString());
            if (!string.IsNullOrWhiteSpace(custom)) return custom.Trim();
            string portrait = PortraitStore.GetPortrait(_taiwuId, _npcId);
            if (!string.IsNullOrWhiteSpace(portrait)
                && !PortraitDistiller.IsCustomPersonaAppendLayer(portrait)) return portrait.Trim();
            return "（默认人设由该角色的身份、关系、经历、特性和自动画像生成。首次对话生成画像后,这里会显示自动画像；你也可以直接改写本框内容并保存。）";
        }

        static void Save()
        {
            if (_saving) { SetStatus("上一项人设仍在保存，请稍候再保存当前草稿。", false); return; }
            if (_npcId < 0 || _taiwuId <= 0) { SetStatus("(角色信息缺失,无法保存)", true); return; }
            string text = _input != null ? (_input.text ?? "") : "";
            if (text.Length > MaxLen)
            {
                SetStatus("角色人设超过 " + MaxLen + " 字上限，请删减后再保存。", true);
                return;
            }
            string trimmed = (text ?? "").Trim();
            if (trimmed.StartsWith("（默认人设由该角色的身份", System.StringComparison.Ordinal))
            {
                SetStatus("这是默认提示文本,不会保存为空自定义。若要恢复自动画像,请点还原默认。", true);
                return;
            }
            string portrait = PortraitStore.GetPortrait(_taiwuId, _npcId);
            bool clear = string.IsNullOrWhiteSpace(trimmed)
                || (!string.IsNullOrWhiteSpace(portrait) && string.Equals(trimmed, portrait.Trim(), System.StringComparison.Ordinal));
            int taiwuId = _taiwuId, npcId = _npcId;
            string personasDirectory = JianghuYoulingPaths.Personas;
            BeginStoreOperation(new[] { PersonaPath(personasDirectory, taiwuId, npcId) },
                taiwuId, npcId, () => new StoreResult
            {
                Ok = PersonaStore.Save(personasDirectory, taiwuId.ToString(), npcId.ToString(),
                    clear ? "" : trimmed, "replace")
            }, value =>
            {
                if (!value.Ok) { SetStatus("(保存失败)", true); return; }
                PortraitService.Invalidate(taiwuId, npcId);
                if (_input != null) _input.characterLimit = MaxLen + 1;
                RefreshAppendDisplay(taiwuId, npcId);
                SetStatus(clear ? "内容等同自动画像,已还原默认。" : "已保存当前人设。", false);
            });
        }

        static void ResetDefault()
        {
            if (_saving) { SetStatus("上一项人设仍在保存，请稍候再还原当前角色。", false); return; }
            if (_npcId < 0 || _taiwuId <= 0) { SetStatus("(角色信息缺失,无法还原)", true); return; }
            int taiwuId = _taiwuId, npcId = _npcId;
            string personasDirectory = JianghuYoulingPaths.Personas;
            string resetEffective = PortraitStore.GetPortrait(taiwuId, npcId);
            if (string.IsNullOrWhiteSpace(resetEffective)
                || PortraitDistiller.IsCustomPersonaAppendLayer(resetEffective))
                resetEffective = "（默认人设由该角色的身份、关系、经历、特性和自动画像生成。首次对话生成画像后,这里会显示自动画像；你也可以直接改写本框内容并保存。）";
            BeginStoreOperation(new[] { PersonaPath(personasDirectory, taiwuId, npcId) },
                taiwuId, npcId, () =>
            {
                bool ok = PersonaStore.Save(personasDirectory, taiwuId.ToString(), npcId.ToString(), "", "replace");
                return new StoreResult { Ok = ok, Text = ok ? resetEffective : null };
            }, value =>
            {
                if (value.Ok)
                {
                    PortraitService.Invalidate(taiwuId, npcId);
                    if (_input != null) { _input.characterLimit = MaxLen + 1; SetInputText(value.Text ?? ""); }
                    RefreshAppendDisplay(taiwuId, npcId);
                }
                SetStatus(value.Ok ? "人设已还原为自动画像。" : "还原失败,请检查文件权限或日志。", !value.Ok);
            });
        }

        static string PersonaPath(string personasDirectory, int taiwuId, int npcId)
            => PersonaStore.CanonicalPath(personasDirectory, npcId.ToString());

        static void BeginStoreOperation(IEnumerable<string> transactionPaths, int operationTaiwuId,
            int operationNpcId, Func<StoreResult> work, Action<StoreResult> complete)
        {
            if (_saving || work == null) return;
            if (!LongTextEditorOperationGate.TryAcquire(OperationOwner, out long operationToken))
            {
                SetStatus("另一个世界书、人设或设置保存仍在进行，请稍候再试。", true);
                return;
            }
            _activeOperationToken = operationToken;
            _operationTaiwuId = operationTaiwuId;
            _operationNpcId = operationNpcId;
            _saving = true;
            RefreshButtons();
            SetStatus("保存中……长文本会在后台写入，界面仍可正常响应。", false);
            int version = _contextVersion;
            long draftVersion = _draftVersion;
            int worldGeneration = WorldLifecycle.Generation;
            uint worldId = WorldLifecycle.WorldId;
            var host = ConfigHost.Instance;
            if (host == null)
            {
                FinishOwnedOperation(operationToken);
                RefreshButtons();
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
            RefreshButtons();
            if (_draftVersion != draftVersion)
            {
                SetStatus(result.Ok
                    ? "后台保存已完成；已保留你在保存期间继续编辑的未保存草稿。"
                    : "后台保存失败；已保留当前草稿，请检查日志后重试。", !result.Ok);
                yield break;
            }
            if (version != _contextVersion)
            {
                if (_root != null && _root.activeSelf && _taiwuId > 0 && _npcId >= 0 && _input != null)
                {
                    if (_draftVersion == draftVersion && result.Ok)
                    {
                        string effective = EffectivePersonaText();
                        _input.characterLimit = System.Math.Max(MaxLen + 1, (effective?.Length ?? 0) + 1);
                        SetInputText(effective);
                        SetStatus("后台保存已结束，已刷新当前人设。", false);
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
            _operationTaiwuId = 0;
            _operationNpcId = 0;
            _saving = false;
        }

        static void SetInputText(string text)
        {
            if (_input == null) return;
            _settingInputText = true;
            try { _input.SetTextWithoutNotify(text ?? ""); }
            finally { _settingInputText = false; }
            ArmFirstClickSelectAll();
        }

        static void UpdateWithAi()
        {
            if (_saving || _generating)
            {
                SetStatus(_generating ? "AI 正在更新该角色人设，请稍候。"
                    : "人设仍在保存，请保存完成后再更新。", false);
                return;
            }
            if (_npcId < 0 || _taiwuId <= 0)
            { SetStatus("角色信息缺失，无法更新人设。", true); return; }
            string savedCustom = PersonaStore.Load(JianghuYoulingPaths.Personas,
                _taiwuId.ToString(), _npcId.ToString());
            if (!string.IsNullOrWhiteSpace(savedCustom) && _input != null
                && !string.Equals((_input.text ?? "").Trim(), savedCustom.Trim(),
                    StringComparison.Ordinal))
            {
                SetStatus("请先保存玩家人设的当前修改，再让 AI 追加；未保存草稿不会交给模型。", true);
                return;
            }
            ConfigHost host = ConfigHost.Instance;
            if (host == null)
            { SetStatus("人设更新宿主尚未就绪，请稍后重试。", true); return; }
            _generating = true;
            RefreshButtons();
            SetStatus(string.IsNullOrWhiteSpace(savedCustom)
                ? "正在读取人物现况并生成新的人设……"
                : "正在读取人物现况；玩家原文保持不动，AI 只生成相容的新增补充……", false);
            host.StartCoroutine(UpdateWithAiCoroutine(_contextVersion, _taiwuId, _npcId,
                WorldLifecycle.Generation, WorldLifecycle.WorldId));
        }

        static IEnumerator UpdateWithAiCoroutine(int version, int taiwuId, int npcId,
            int worldGeneration, uint worldId)
        {
            NpcSnapshot snap = null;
            yield return NpcSnapshotReader.Fetch(npcId, value => snap = value, false,
                () => version == _contextVersion
                    && WorldLifecycle.IsSameWorld(worldGeneration)
                    && WorldLifecycle.WorldId == worldId);
            if (version != _contextVersion || !WorldLifecycle.IsSameWorld(worldGeneration)
                || WorldLifecycle.WorldId != worldId)
            { _generating = false; RefreshButtons(); yield break; }
            if (snap == null || snap.NpcId != npcId || snap.TaiwuId != taiwuId)
            {
                _generating = false; RefreshButtons();
                SetStatus("人物现况读取失败，未改动现有人设。", true);
                yield break;
            }
            string savedCustom = PersonaStore.Load(JianghuYoulingPaths.Personas,
                taiwuId.ToString(), npcId.ToString());
            string beforeAppend = PortraitStore.GetPortrait(taiwuId, npcId);
            string generated = null;
            yield return PortraitService.RegenerateReady(snap, value => generated = value);
            if (version != _contextVersion || !WorldLifecycle.IsSameWorld(worldGeneration)
                || WorldLifecycle.WorldId != worldId)
            { _generating = false; RefreshButtons(); yield break; }
            _generating = false;
            RefreshButtons();
            if (string.IsNullOrWhiteSpace(generated))
            {
                SetStatus("AI 未返回可用人设，现有内容未改动。", true);
                yield break;
            }
            if (!string.IsNullOrWhiteSpace(savedCustom))
            {
                RefreshAppendDisplay(taiwuId, npcId);
                bool appended = PortraitDistiller.IsCustomPersonaAppendLayer(generated)
                    && !string.Equals((beforeAppend ?? "").Trim(), generated.Trim(),
                        StringComparison.Ordinal);
                SetStatus(appended
                    ? "玩家设定原文未改动；新的相容内容已追加，并显示在下方“自动追加”区域。"
                    : "玩家设定原文未改动；本次没有发现可安全追加且不矛盾的新内容。", false);
                yield break;
            }
            _input.characterLimit = System.Math.Max(MaxLen + 1, generated.Length + 1);
            SetInputText(generated.Trim());
            unchecked { _draftVersion++; }
            SetStatus("AI 人设已更新。可继续修改，点击保存后作为完整自定义人设应用。", false);
        }

        static void RefreshAppendDisplay(int taiwuId, int npcId)
        {
            if (_appendInput == null) return;
            string custom = PersonaStore.Load(JianghuYoulingPaths.Personas,
                taiwuId.ToString(), npcId.ToString());
            string text;
            if (string.IsNullOrWhiteSpace(custom))
                text = "（保存玩家自定义人设后，后续 AI 更新会只在这里追加相容的经历与关系变化。）";
            else
            {
                string append = PortraitStore.GetPortrait(taiwuId, npcId);
                text = PortraitDistiller.IsCustomPersonaAppendLayer(append)
                    ? append.Trim()
                    : PortraitDistiller.CustomPersonaAppendFallback(null);
            }
            _appendInput.SetTextWithoutNotify(text);
        }

        static void RefreshButtons()
        {
            bool enabled = !_saving && !_generating;
            if (_saveButton != null) _saveButton.interactable = enabled;
            if (_resetButton != null) _resetButton.interactable = enabled;
            if (_updateButton != null) _updateButton.interactable = enabled;
            if (_input != null) _input.interactable = !_generating;
        }

        static void ArmFirstClickSelectAll()
        {
            var longInput = _input as LongTextInputField;
            if (longInput != null)
                longInput.ArmSelectAllOnNextPointerDown(true);
        }

        static void SetStatus(string text, bool error)
        {
            if (_status == null) return;
            _status.text = text;
            _status.color = error ? new Color(0.92f, 0.55f, 0.45f, 1f) : new Color(0.70f, 0.84f, 0.66f, 1f);
        }

        static void Build()
        {
            _root = new GameObject("JHYL_PersonaCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            UnityEngine.Object.DontDestroyOnLoad(_root);
            var cv = _root.GetComponent<Canvas>();
            cv.renderMode = RenderMode.ScreenSpaceOverlay;
            cv.sortingOrder = 30011;   // 略高于配置窗,避免被遮挡
            PopupRegistry.Register(_root, Hide);   // 右键逐个关闭(从最上层依次)
            var sc = _root.GetComponent<CanvasScaler>();
            sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            sc.referenceResolution = new Vector2(1920, 1080);

            var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(_root.transform, false);
            var prt = panel.GetComponent<RectTransform>();
            prt.sizeDelta = new Vector2(PanelWidth, PanelHeight);
            prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0.5f, 0.5f);
            prt.anchoredPosition = Vector2.zero;
            panel.GetComponent<Image>().color = new Color(0.10f, 0.11f, 0.10f, 0.97f);

            _title = NewText("Title", panel.transform, 24, TextAlignmentOptions.Center);
            Anchor(_title.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(56, -10), new Vector2(-56, -56));
            _title.text = "自定义人设";

            var close = NewButton("Close", panel.transform, "X", 22, out var closeBtn);
            var crt = close.GetComponent<RectTransform>();
            crt.anchorMin = crt.anchorMax = crt.pivot = new Vector2(1, 1);
            crt.anchoredPosition = new Vector2(-8, -8); crt.sizeDelta = new Vector2(36, 36);
            closeBtn.onClick.AddListener(Hide);

            var playerLabel = NewText("PlayerPersonaLabel", panel.transform, 16,
                TextAlignmentOptions.Left);
            Anchor(playerLabel.rectTransform, new Vector2(0, 0), new Vector2(1, 0),
                new Vector2(22, 622), new Vector2(-22, 654));
            playerLabel.text = "玩家设定（可编辑，AI 永不改写）";
            playerLabel.color = new Color(0.93f, 0.79f, 0.44f, 1f);

            // 玩家原文编辑区
            var inputGo = new GameObject("PersonaInput", typeof(RectTransform), typeof(Image));
            inputGo.transform.SetParent(panel.transform, false);
            Anchor(inputGo.GetComponent<RectTransform>(), new Vector2(0, 0), new Vector2(1, 1),
                new Vector2(22, 350), new Vector2(-22, -98));
            inputGo.GetComponent<Image>().color = new Color(1, 1, 1, 0.10f);

            _input = inputGo.AddComponent<LongTextInputField>();   // 长文整篇粘贴只替换/通知/排版一次
            // 视口(RectMask2D 裁剪)+ 内层文本:长内容可超出视口高度,随光标/滚轮上下滚动
            var viewport = new GameObject("TextArea", typeof(RectTransform), typeof(RectMask2D));
            viewport.transform.SetParent(inputGo.transform, false);
            var vrt = viewport.GetComponent<RectTransform>();
            Anchor(vrt, Vector2.zero, Vector2.one, new Vector2(10, 8), new Vector2(-24, -8));   // 右侧留滚动条位
            var textArea = NewText("Text", viewport.transform, 19, TextAlignmentOptions.TopLeft);
            textArea.richText = false;   // 超长可编辑正文不做富文本解析，避免每次输入都重扫全部标签
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

            var appendLabel = NewText("AutoAppendLabel", panel.transform, 16,
                TextAlignmentOptions.Left);
            Anchor(appendLabel.rectTransform, new Vector2(0, 0), new Vector2(1, 0),
                new Vector2(22, 314), new Vector2(-22, 346));
            appendLabel.text = "自动追加（只读；只收录不与玩家设定矛盾的长期新增内容）";
            appendLabel.color = new Color(0.55f, 0.82f, 0.72f, 1f);

            var appendGo = new GameObject("PersonaAutoAppend", typeof(RectTransform), typeof(Image));
            appendGo.transform.SetParent(panel.transform, false);
            Anchor(appendGo.GetComponent<RectTransform>(), new Vector2(0, 0), new Vector2(1, 0),
                new Vector2(22, 124), new Vector2(-22, 310));
            appendGo.GetComponent<Image>().color = new Color(0.20f, 0.30f, 0.27f, 0.45f);
            _appendInput = appendGo.AddComponent<TMP_InputField>();
            var appendViewport = new GameObject("TextArea", typeof(RectTransform), typeof(RectMask2D));
            appendViewport.transform.SetParent(appendGo.transform, false);
            var appendVrt = appendViewport.GetComponent<RectTransform>();
            Anchor(appendVrt, Vector2.zero, Vector2.one, new Vector2(10, 8), new Vector2(-24, -8));
            var appendText = NewText("Text", appendViewport.transform, 17,
                TextAlignmentOptions.TopLeft);
            appendText.richText = false;
            Anchor(appendText.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            appendText.enableWordWrapping = true;
            _appendInput.textViewport = appendVrt;
            _appendInput.textComponent = appendText;
            _appendInput.lineType = TMP_InputField.LineType.MultiLineNewline;
            _appendInput.contentType = TMP_InputField.ContentType.Standard;
            _appendInput.richText = false;
            _appendInput.readOnly = true;
            _appendInput.characterLimit = 5001;
            _appendInput.verticalScrollbar = UiScroll.AddVertical(appendGo);

            // 状态行
            _status = NewText("Status", panel.transform, 16, TextAlignmentOptions.Left);
            Anchor(_status.rectTransform, new Vector2(0, 0), new Vector2(1, 0), new Vector2(22, 72), new Vector2(-22, 116));
            _status.enableWordWrapping = true;

            var saveGo = NewButton("Save", panel.transform, "保存", 19, out _saveButton);
            Anchor(saveGo.GetComponent<RectTransform>(), new Vector2(0, 0), new Vector2(0, 0), new Vector2(22, 18), new Vector2(176, 60));
            _saveButton.onClick.AddListener(Save);

            var resetGo = NewButton("ResetDefault", panel.transform, "还原默认", 19, out _resetButton);
            Anchor(resetGo.GetComponent<RectTransform>(), new Vector2(0, 0), new Vector2(0, 0), new Vector2(188, 18), new Vector2(358, 60));
            _resetButton.onClick.AddListener(ResetDefault);

            var updateGo = NewButton("UpdateWithAi", panel.transform, "AI 更新", 19,
                out _updateButton);
            Anchor(updateGo.GetComponent<RectTransform>(), new Vector2(0, 0),
                new Vector2(0, 0), new Vector2(370, 18), new Vector2(540, 60));
            _updateButton.onClick.AddListener(UpdateWithAi);

            var closeBottomGo = NewButton("CloseBottom", panel.transform, "关闭", 20, out var closeBottomBtn);
            Anchor(closeBottomGo.GetComponent<RectTransform>(), new Vector2(1, 0), new Vector2(1, 0), new Vector2(-176, 18), new Vector2(-22, 60));
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
