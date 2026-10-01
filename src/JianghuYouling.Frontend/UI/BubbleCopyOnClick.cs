using UnityEngine;
using UnityEngine.EventSystems;

namespace JianghuYouling
{
    /// <summary>#17 点一条聊天消息即把其文字复制到剪贴板,并弹「已复制」提示。
    /// 只处理"点击"(未拖动)——拖动仍归 ScrollRect 滚动列表,互不抢。比 TMP 拖选整段更稳、更省事。</summary>
    public sealed class BubbleCopyOnClick : MonoBehaviour, IPointerClickHandler
    {
        public System.Func<string> getText;       // 取本条消息的纯文本(剥掉富文本标签)
        public System.Action<string> onCopied;    // 复制后回调(弹「已复制」)

        public void OnPointerClick(PointerEventData e)
        {
            if (e != null && e.dragging) return;   // 拖动=滚动,不当作复制
            string s = null;
            try { s = getText != null ? getText() : null; } catch { }
            if (string.IsNullOrEmpty(s)) return;
            SetClipboard(s);
            try { onCopied?.Invoke(s); } catch { }
        }

        // 剪贴板:本工程未引用 UnityEngine.IMGUIModule(GUIUtility 在其中),改用反射在运行时写入(游戏已加载该模块 DLL)。
        static System.Reflection.PropertyInfo _copyBuf; static bool _resolved;
        static void SetClipboard(string s)
        {
            try
            {
                if (!_resolved)
                {
                    _resolved = true;
                    var ty = System.Type.GetType("UnityEngine.GUIUtility, UnityEngine.IMGUIModule")
                          ?? System.Type.GetType("UnityEngine.GUIUtility, UnityEngine");
                    _copyBuf = ty?.GetProperty("systemCopyBuffer", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                }
                _copyBuf?.SetValue(null, s);
            }
            catch { }
        }
    }
}
