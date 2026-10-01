using System;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace JianghuYouling
{
    /// <summary>
    /// 让普通玩家/NPC 对话正文支持拖选；选区完成后立即复制并通知聊天窗。
    /// Ctrl+A 仍可全选并自动复制，Ctrl+C 仍作为显式重复制快捷键保留。
    /// 不能在正文对象上挂 TMP_InputField：它实现了 ILayoutElement，会与 TMP_Text
    /// 争夺 VerticalLayoutGroup 的首选高度，令长消息按单行高度排版并彼此重叠。
    /// </summary>
    public sealed class SelectableChatText : MonoBehaviour, IPointerDownHandler,
        IDragHandler, IPointerUpHandler, ISelectHandler, IDeselectHandler
    {
        static readonly Color32 SelectionColor = new Color32(111, 153, 190, 255);

        TextMeshProUGUI _text;
        string _lastText;
        int _anchorCharacter = -1;
        int _focusCharacter = -1;
        bool _dragging;
        bool _hasSelection;
        Action _onCopied;

        public static void Attach(TextMeshProUGUI text, Action onCopied = null)
        {
            if (text == null) return;
            SelectableChatText existing = text.GetComponent<SelectableChatText>();
            if (existing != null)
            {
                existing._onCopied = onCopied;
                return;
            }
            SelectableChatText selectable = text.gameObject.AddComponent<SelectableChatText>();
            selectable._onCopied = onCopied;
            selectable.Initialize(text);
        }

        void Initialize(TextMeshProUGUI text)
        {
            _text = text;
            _text.raycastTarget = true;
            _lastText = _text.text ?? string.Empty;
        }

        void LateUpdate()
        {
            if (_text == null) return;
            string rendered = _text.text ?? string.Empty;
            if (string.Equals(rendered, _lastText, StringComparison.Ordinal)) return;

            // 流式正文变化时 TMP 会自行重建网格；只丢弃已失效的字符索引，
            // 不再添加第二个布局元素，也不在每帧反复改写正文。
            _lastText = rendered;
            _anchorCharacter = -1;
            _focusCharacter = -1;
            _dragging = false;
            _hasSelection = false;
        }

        void Update()
        {
            if (_text == null || EventSystem.current == null
                || EventSystem.current.currentSelectedGameObject != gameObject)
                return;

            bool control = Input.GetKey(KeyCode.LeftControl)
                || Input.GetKey(KeyCode.RightControl)
                || Input.GetKey(KeyCode.LeftCommand)
                || Input.GetKey(KeyCode.RightCommand);
            if (!control) return;
            if (Input.GetKeyDown(KeyCode.A))
            {
                SelectAll();
            }
            else if (Input.GetKeyDown(KeyCode.C))
            {
                CopySelection();
            }
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (_text == null || eventData == null) return;
            EventSystem.current?.SetSelectedGameObject(gameObject, eventData);
            RestoreTextColors();
            _anchorCharacter = FindCharacter(eventData);
            _focusCharacter = _anchorCharacter;
            _dragging = _anchorCharacter >= 0;
            _hasSelection = false;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!_dragging || _text == null || eventData == null) return;
            int next = FindCharacter(eventData);
            if (next < 0 || next == _focusCharacter) return;
            _focusCharacter = next;
            _hasSelection = _focusCharacter != _anchorCharacter;
            if (_hasSelection) ApplySelectionColors();
            else RestoreTextColors();
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (_dragging && eventData != null)
            {
                int next = FindCharacter(eventData);
                if (next >= 0 && next != _focusCharacter)
                {
                    _focusCharacter = next;
                    _hasSelection = _focusCharacter != _anchorCharacter;
                    if (_hasSelection) ApplySelectionColors();
                    else RestoreTextColors();
                }
            }
            _dragging = false;
            if (_hasSelection) CopySelection();
            else ClearSelection(true);
        }

        public void OnSelect(BaseEventData eventData)
        {
        }

        public void OnDeselect(BaseEventData eventData)
        {
            ClearSelection(true);
        }

        int FindCharacter(PointerEventData eventData)
        {
            Camera camera = eventData.pressEventCamera;
            int index = TMP_TextUtilities.FindIntersectingCharacter(
                _text, eventData.position, camera, true);
            if (index < 0)
                index = TMP_TextUtilities.FindNearestCharacter(
                    _text, eventData.position, camera, true);
            int count = _text.textInfo != null ? _text.textInfo.characterCount : 0;
            return count <= 0 ? -1 : Mathf.Clamp(index, 0, count - 1);
        }

        void SelectAll()
        {
            if (_text == null) return;
            _text.ForceMeshUpdate(false, true);
            int count = _text.textInfo != null ? _text.textInfo.characterCount : 0;
            if (count <= 0)
            {
                ClearSelection(false);
                return;
            }
            _anchorCharacter = 0;
            _focusCharacter = count - 1;
            _hasSelection = true;
            ApplySelectionColors(false);
            CopySelection();
        }

        void CopySelection()
        {
            if (!HasSelection() || _text == null || _text.textInfo == null) return;
            int first = Math.Min(_anchorCharacter, _focusCharacter);
            int last = Math.Max(_anchorCharacter, _focusCharacter);
            int count = _text.textInfo.characterCount;
            first = Mathf.Clamp(first, 0, Math.Max(0, count - 1));
            last = Mathf.Clamp(last, first, Math.Max(first, count - 1));

            var copied = new StringBuilder(last - first + 1);
            for (int i = first; i <= last; i++)
            {
                char value = _text.textInfo.characterInfo[i].character;
                if (value != '\u200B') copied.Append(value);
            }
            if (copied.Length <= 0) return;
            GUIUtility.systemCopyBuffer = copied.ToString();
            try { _onCopied?.Invoke(); } catch { }
        }

        bool HasSelection()
        {
            return _hasSelection && _anchorCharacter >= 0 && _focusCharacter >= 0;
        }

        void ClearSelection(bool restoreColors)
        {
            bool hadSelection = HasSelection();
            _anchorCharacter = -1;
            _focusCharacter = -1;
            _dragging = false;
            _hasSelection = false;
            if (restoreColors && hadSelection) RestoreTextColors();
        }

        void RestoreTextColors()
        {
            if (_text == null) return;
            _text.ForceMeshUpdate(false, true);
            _text.UpdateVertexData(TMP_VertexDataUpdateFlags.Colors32);
        }

        void ApplySelectionColors(bool rebuildText = true)
        {
            if (_text == null || !HasSelection()) return;
            if (rebuildText) _text.ForceMeshUpdate(false, true);
            TMP_TextInfo info = _text.textInfo;
            if (info == null || info.characterInfo == null || info.meshInfo == null) return;

            int first = Math.Min(_anchorCharacter, _focusCharacter);
            int last = Math.Max(_anchorCharacter, _focusCharacter);
            int count = info.characterCount;
            first = Mathf.Clamp(first, 0, Math.Max(0, count - 1));
            last = Mathf.Clamp(last, first, Math.Max(first, count - 1));
            for (int i = first; i <= last; i++)
            {
                TMP_CharacterInfo character = info.characterInfo[i];
                if (!character.isVisible) continue;
                int material = character.materialReferenceIndex;
                int vertex = character.vertexIndex;
                if (material < 0 || material >= info.meshInfo.Length) continue;
                Color32[] colors = info.meshInfo[material].colors32;
                if (colors == null || vertex < 0 || vertex + 3 >= colors.Length) continue;
                colors[vertex] = SelectionColor;
                colors[vertex + 1] = SelectionColor;
                colors[vertex + 2] = SelectionColor;
                colors[vertex + 3] = SelectionColor;
            }
            _text.UpdateVertexData(TMP_VertexDataUpdateFlags.Colors32);
        }
    }
}
