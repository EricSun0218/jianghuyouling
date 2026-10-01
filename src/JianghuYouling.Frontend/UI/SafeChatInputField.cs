using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using JianghuYouling.Core.Text;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace JianghuYouling
{
    /// <summary>
    /// 聊天/配置输入框:在游戏 DisableHotkeyInputField(聚焦时屏蔽游戏快捷键)之上,
    /// 兜住 TMP_InputField 在中文输入法合成串下的光标越界 bug——
    /// OnUpdateSelected → KeyPressed → Append → Insert 里 string.Insert(startIndex, …) 在
    /// startIndex(光标位置)> 文本长度时抛 ArgumentOutOfRangeException。该方法每帧对选中对象调用,
    /// 一旦进入坏状态会每帧重复抛,刷爆日志并令游戏卡死(线上:输入法打字后按数字选词即卡死)。
    /// 这里接住异常并把光标夹回文本末尾、丢弃这次坏事件,输入框随即恢复,不影响正常输入。
    /// </summary>
    public class SafeChatInputField : DisableHotkeyInputField
    {
        private bool _deferredScrollbarUpdate;
        private bool _resetEmptyAfterActivation;
        private Coroutine _postRenderEmptyReset;
        private int _emptyActivationVersion;
        private static readonly FieldInfo TmpWarningsDisabledField =
            AccessTools.Field(typeof(TMP_Settings), "m_warningsDisabled");

        /// <summary>
        /// TMP_InputField.ActivateInputField only schedules the real activation for its next
        /// LateUpdate. Resetting an empty multiline field before that point can be overwritten
        /// by TMP's retained caret/viewport state, which makes the next message start on line 2.
        /// Arm a post-base-LateUpdate reset so it runs after TMP has actually become focused.
        /// </summary>
        public void ActivateAtTextEnd()
        {
            bool empty = string.IsNullOrEmpty(text);
            _resetEmptyAfterActivation = empty;
            if (empty) ResetEmptyVisualState();
            ActivateInputField();
            MoveCaretToTextEnd();
            unchecked { _emptyActivationVersion++; }
            if (empty && isActiveAndEnabled)
            {
                if (_postRenderEmptyReset != null) StopCoroutine(_postRenderEmptyReset);
                _postRenderEmptyReset = StartCoroutine(
                    ResetEmptyAfterCanvasRebuild(_emptyActivationVersion));
            }
        }

        protected override void LateUpdate()
        {
            base.LateUpdate();
            if (!_resetEmptyAfterActivation || !isFocused) return;

            _resetEmptyAfterActivation = false;
            // Do not erase text entered between the activation request and TMP's deferred focus.
            if (!string.IsNullOrEmpty(text)) return;
            ResetEmptyVisualState();
            MoveCaretToTextEnd();
        }

        /// <summary>
        /// Normal Enter submission can leave a CR/LF-only edit event behind after the submit
        /// callback has cleared the field. It is not a user draft and would make the next real
        /// character start on line 2. Never discard spaces or any visible character here.
        /// </summary>
        public bool DiscardSubmitLineBreakResidue()
        {
            string value = text;
            if (string.IsNullOrEmpty(value)) return false;
            for (int index = 0; index < value.Length; index++)
                if (value[index] != '\r' && value[index] != '\n') return false;
            ResetEmptyVisualState();
            return true;
        }

        private IEnumerator ResetEmptyAfterCanvasRebuild(int version)
        {
            int remainingFocusFrames = 4;
            while (version == _emptyActivationVersion && isActiveAndEnabled
                && !isFocused && remainingFocusFrames-- > 0)
                yield return null;

            if (version != _emptyActivationVersion || !isActiveAndEnabled || !isFocused)
            {
                if (version == _emptyActivationVersion) _postRenderEmptyReset = null;
                yield break;
            }

            // TMP's native caret adjusts the text RectTransform during Canvas LatePreRender,
            // which is later than every MonoBehaviour.LateUpdate. End-of-frame is the first
            // point where that final adjustment can be authoritatively undone.
            yield return new WaitForEndOfFrame();
            if (version == _emptyActivationVersion && isActiveAndEnabled && isFocused
                && string.IsNullOrEmpty(text))
            {
                ResetEmptyVisualState();
                MoveCaretToTextEnd();
            }
            if (version == _emptyActivationVersion) _postRenderEmptyReset = null;
        }

        /// <summary>
        /// TMP 会把多行文本组件向上滚动来追随旧光标；仅把 text 设为空不会总是把该位移复原，
        /// 下一轮首字因此可能画在第二行。清空后同时归零字符串/光标位置和文本视口位移。
        /// </summary>
        public void ResetEmptyVisualState()
        {
            try
            {
                if (!string.IsNullOrEmpty(text)) text = string.Empty;
                m_StringPosition = 0;
                m_StringSelectPosition = 0;
                m_CaretPosition = 0;
                m_CaretSelectPosition = 0;
                if (m_TextComponent != null)
                {
                    m_TextComponent.rectTransform.anchoredPosition = Vector2.zero;
                    m_TextComponent.ForceMeshUpdate(false, true);
                }
                ForceLabelUpdate();
            }
            catch { }
        }

        public void MoveCaretToTextEnd()
        {
            try
            {
                int len = text != null ? text.Length : 0;
                ForceLabelUpdate();
                m_StringPosition = len;
                m_StringSelectPosition = len;
                int caretMax = len;
                if (m_TextComponent != null && m_TextComponent.textInfo != null)
                {
                    // TMP_InputField.UpdateLabel always appends a zero-width sentinel (U+200B)
                    // to the rendered text.  Its own MoveTextEnd therefore uses characterCount-1.
                    // Counting the sentinel put an empty field at visual caret 1 and the next
                    // activation started input on the second line.
                    caretMax = Mathf.Max(0, m_TextComponent.textInfo.characterCount - 1);
                }
                m_CaretPosition = caretMax;
                m_CaretSelectPosition = caretMax;
                if (len == 0 && m_TextComponent != null)
                    m_TextComponent.rectTransform.anchoredPosition = Vector2.zero;
                ForceLabelUpdate();
            }
            catch { }
        }

        public override void OnUpdateSelected(BaseEventData eventData)
        {
            // 玩家可把任意 Unicode 文本粘进对话/人设输入框。游戏的静态 GB2312 字体对
            // 缺字会在每一帧 UpdateLabel 都打印完整堆栈，单个字符即可把 Player.log 刷到
            // 数十 MiB。只在本输入框这次同步重绘期间关闭 TMP 缺字诊断，文本本身保持原样，
            // 其它游戏 UI 的字体警告不受影响。
            bool previousWarningsDisabled = TMP_Settings.warningsDisabled;
            object tmpSettings = TMP_Settings.instance;
            try
            {
                if (!previousWarningsDisabled && TmpWarningsDisabledField != null
                    && tmpSettings != null)
                    TmpWarningsDisabledField.SetValue(tmpSettings, true);
                base.OnUpdateSelected(eventData);
            }
            catch (ArgumentOutOfRangeException)
            {
                try
                {
                    int len = text != null ? text.Length : 0;
                    // TMP 的 public position setter 在 compositionLength > 0 时会拒绝写入，
                    // 因此必须直接修复四个 protected 基础位置，不能再靠 public setter 假修复。
                    m_StringPosition = Mathf.Clamp(m_StringPosition, 0, len);
                    m_StringSelectPosition = Mathf.Clamp(m_StringSelectPosition, 0, len);

                    int caretMax = len;
                    try
                    {
                        if (m_TextComponent != null && m_TextComponent.textInfo != null)
                            caretMax = Mathf.Min(len,
                                Mathf.Max(0, m_TextComponent.textInfo.characterCount - 1));
                    }
                    catch { }
                    m_CaretPosition = Mathf.Clamp(m_CaretPosition, 0, caretMax);
                    m_CaretSelectPosition = Mathf.Clamp(m_CaretSelectPosition, 0, caretMax);
                }
                catch { }
            }
            finally
            {
                if (!previousWarningsDisabled && TmpWarningsDisabledField != null
                    && tmpSettings != null)
                {
                    try { TmpWarningsDisabledField.SetValue(tmpSettings, false); } catch { }
                }
            }
        }

        // JHYL_INPUT_SCROLL_NATIVE_DIRECTION:世界书/人设等长文本框的滚轮方向交给 TMP 原生处理。
        // 之前这里额外取反 scrollDelta.y,会让设置页多行输入框滚轮方向反过来。
        public override void OnScroll(PointerEventData eventData)
        {
            base.OnScroll(eventData);
        }

        internal void DeferScrollbarUpdate()
        {
            if (_deferredScrollbarUpdate || !isActiveAndEnabled) return;
            _deferredScrollbarUpdate = true;
            StartCoroutine(FlushDeferredScrollbarUpdate());
        }

        private IEnumerator FlushDeferredScrollbarUpdate()
        {
            // TMP raises TEXT_CHANGED while TextMeshProUGUI is already rebuilding its graphic.
            // Moving the scrollbar handle in that callback attempts to enqueue its Image into
            // the same rebuild loop. Resume on the next frame and mirror TMP 1.5.3's calculation.
            yield return null;
            _deferredScrollbarUpdate = false;
            if (!isActiveAndEnabled || m_VerticalScrollbar == null
                || m_TextViewport == null || m_TextComponent == null)
                yield break;

            float viewportHeight = m_TextViewport.rect.height;
            float preferredHeight = m_TextComponent.preferredHeight;
            m_VerticalScrollbar.size = preferredHeight > 0.001f
                ? Mathf.Clamp01(viewportHeight / preferredHeight) : 1f;

            float position = 0f;
            TMP_TextInfo info = m_TextComponent.textInfo;
            if (preferredHeight > viewportHeight + 0.001f && info != null
                && info.lineInfo != null && info.lineCount > 0)
            {
                Rect viewport = m_TextViewport.rect;
                position = (info.lineInfo[0].ascender - viewport.yMax
                    + m_TextComponent.rectTransform.anchoredPosition.y)
                    / (preferredHeight - viewportHeight);
                position = Mathf.Round(position * 1000f) / 1000f;
            }
            m_VerticalScrollbar.value = Mathf.Clamp01(position);
        }

        protected override void OnDisable()
        {
            _deferredScrollbarUpdate = false;
            _resetEmptyAfterActivation = false;
            unchecked { _emptyActivationVersion++; }
            _postRenderEmptyReset = null;
            base.OnDisable();
        }
    }

    /// <summary>
    /// TMP 1.5.3 synchronously resizes a vertical scrollbar from its text pre-render callback.
    /// Unity rejects the handle Image becoming dirty while the graphic rebuild loop is active,
    /// so only our input fields defer that one update to the next frame.
    /// </summary>
    [HarmonyPatch(typeof(TMP_InputField), "UpdateScrollbar")]
    internal static class SafeChatInputFieldDeferredScrollbarPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(TMP_InputField __instance)
        {
            var field = __instance as SafeChatInputField;
            if (field == null || !CanvasUpdateRegistry.IsRebuildingGraphics()) return true;
            field.DeferScrollbarUpdate();
            return false;
        }
    }

    /// <summary>
    /// Large-document editor used by world books, personas and long writing-style fields.
    /// TMP's stock Append(string) loops through the clipboard and performs one immutable string
    /// insertion plus one onValueChanged callback per character.  A full-document paste must be
    /// committed as one edit so the main thread only rebuilds TextMeshPro once.
    /// </summary>
    public sealed class LongTextInputField : SafeChatInputField
    {
        internal const int BulkPasteThreshold = 512;
        private bool _selectAllOnNextPointerDown;

        public bool PreserveViewportOnPointerSelection { get; set; }

        /// <summary>
        /// Arms one user pointer click to select the complete loaded document.  TMP's stock
        /// onFocusSelectAll is disabled because windows may activate an input programmatically
        /// before the user clicks it; consuming selection here makes the first real click the
        /// replacement gesture and later clicks ordinary caret positioning.
        /// </summary>
        public void ArmSelectAllOnNextPointerDown(bool preserveViewport)
        {
            _selectAllOnNextPointerDown = true;
            PreserveViewportOnPointerSelection = preserveViewport;
            onFocusSelectAll = false;
        }

        public override void OnPointerDown(PointerEventData eventData)
        {
            bool selectAll = _selectAllOnNextPointerDown
                && eventData != null
                && eventData.button == PointerEventData.InputButton.Left
                && IsActive() && IsInteractable();
            bool preserveViewport = PreserveViewportOnPointerSelection;
            if (!selectAll && !preserveViewport)
            {
                base.OnPointerDown(eventData);
                return;
            }

            RectTransform textRect = m_TextComponent == null ? null : m_TextComponent.rectTransform;
            Vector2 anchoredPosition = textRect == null ? Vector2.zero : textRect.anchoredPosition;
            bool hasScrollbar = verticalScrollbar != null;
            float scrollbarValue = hasScrollbar ? verticalScrollbar.value : 0f;
            base.OnPointerDown(eventData);

            if (selectAll)
            {
                _selectAllOnNextPointerDown = false;
                SelectAllKeepingVisibleEndpoint(hasScrollbar, scrollbarValue);
                UpdateLabel();
            }

            // TMP may recalculate the long document after resolving the clicked caret and reset
            // its viewport to the first line. Keep the user's current visible page while leaving
            // the newly selected caret/selection untouched.
            if (preserveViewport)
            {
                RestoreViewport(textRect, anchoredPosition, hasScrollbar, scrollbarValue);
                StartCoroutine(RestoreViewportNextFrame(textRect, anchoredPosition,
                    hasScrollbar, scrollbarValue));
            }
        }

        private void SelectAllKeepingVisibleEndpoint(bool hasScrollbar, float scrollbarValue)
        {
            int length = text == null ? 0 : text.Length;
            m_isSelectAll = true;
            if (hasScrollbar && scrollbarValue >= 0.5f)
            {
                // TMP's stock SelectAll focuses position 0, so a document viewed near its end
                // jumps to the first line. Reverse the selection when the visible endpoint is
                // nearer the end; either orientation still replaces the whole document on input.
                stringPositionInternal = 0;
                stringSelectPositionInternal = length;
            }
            else
            {
                stringPositionInternal = length;
                stringSelectPositionInternal = 0;
            }
        }

        private IEnumerator RestoreViewportNextFrame(RectTransform textRect,
            Vector2 anchoredPosition, bool hasScrollbar, float scrollbarValue)
        {
            yield return null;
            RestoreViewport(textRect, anchoredPosition, hasScrollbar, scrollbarValue);
        }

        private void RestoreViewport(RectTransform textRect, Vector2 anchoredPosition,
            bool hasScrollbar, float scrollbarValue)
        {
            if (hasScrollbar && verticalScrollbar != null)
                verticalScrollbar.value = Mathf.Clamp01(scrollbarValue);
            if (textRect != null) textRect.anchoredPosition = anchoredPosition;
        }

        protected override void Append(string input)
        {
            if (string.IsNullOrEmpty(input)) return;
            if (input.Length < BulkPasteThreshold || readOnly
                || characterValidation != CharacterValidation.None || onValidateInput != null)
            {
                base.Append(input);
                return;
            }

            LongTextBulkEdit.Result edit = LongTextBulkEdit.Apply(m_Text,
                m_StringPosition, m_StringSelectPosition, input, characterLimit);
            if (!edit.Changed) return;

            m_Text = edit.Text;
            m_StringPosition = edit.Caret;
            m_StringSelectPosition = edit.Caret;
            m_CaretPosition = edit.Caret;
            m_CaretSelectPosition = edit.Caret;
            m_isSelectAll = false;
            if (m_SoftKeyboard != null) m_SoftKeyboard.text = m_Text;

            // The string-position setters mark TMP's caret mapping dirty.  UpdateLabel then
            // rebuilds the new document and resolves the caret against the new text in one pass.
            selectionStringAnchorPosition = edit.Caret;
            selectionStringFocusPosition = edit.Caret;
            UpdateLabel();
            onValueChanged?.Invoke(m_Text);
            Debug.Log("[JHYL_LONG_TEXT_PASTE] inserted=" + edit.InsertedCharacters
                + " total=" + m_Text.Length + " callbacks=1");
        }
    }

    /// <summary>
    /// TMP_InputField.KeyPressed is protected and non-virtual, so a subclass cannot inspect
    /// the exact buffered Event that base.OnUpdateSelected is about to process.  Frame-wide
    /// Input.GetKey is not equivalent: under a stalled frame Ctrl may be released after the
    /// Enter event was queued.  This narrowly-scoped patch uses that event's own modifier and
    /// temporarily selects TMP's native multiline path only for SafeChatInputField instances.
    /// Append therefore retains native forward/reverse selection replacement and IME handling.
    /// </summary>
    [HarmonyPatch(typeof(TMP_InputField), "KeyPressed", new[] { typeof(Event) })]
    internal static class SafeChatInputFieldCtrlEnterPatch
    {
        internal sealed class PatchState
        {
            public SafeChatInputField Field;
            public TMP_InputField.LineType Original;
            public bool Changed;
        }

        [HarmonyPrefix]
        private static void Prefix(TMP_InputField __instance, Event evt, out PatchState __state)
        {
            __state = null;
            var field = __instance as SafeChatInputField;
            if (field == null || evt == null || field.lineType != TMP_InputField.LineType.MultiLineSubmit) return;
            if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter) return;
            if ((evt.modifiers & EventModifiers.Control) == 0) return; // covers left and right Ctrl on Windows
            __state = new PatchState { Field = field, Original = field.lineType, Changed = true };
            field.lineType = TMP_InputField.LineType.MultiLineNewline;
        }

        [HarmonyPostfix]
        private static void Postfix(PatchState __state) => Restore(__state);

        [HarmonyFinalizer]
        private static Exception Finalizer(Exception __exception, PatchState __state)
        {
            Restore(__state);
            return __exception;
        }

        private static void Restore(PatchState state)
        {
            if (state == null || !state.Changed || state.Field == null) return;
            state.Field.lineType = state.Original;
            state.Changed = false;
        }
    }
}
