using System;
using System.Collections.Generic;
using UnityEngine;

namespace JianghuYouling
{
    /// <summary>
    /// 所有自建弹窗在 Build() 后登记其 Canvas 根与关闭动作;右键时由 RightClickGuard 关掉**最上层**
    /// (Canvas.sortingOrder 最高;并列时后登记者在上)的一个开着的弹窗,实现"右键逐个关闭、从最上层依次关"。
    /// </summary>
    public static class PopupRegistry
    {
        sealed class Entry { public GameObject Root; public Action Close; }
        static readonly List<Entry> _entries = new List<Entry>();

        public static void Register(GameObject root, Action close)
        {
            if (root == null || close == null) return;
            foreach (var e in _entries) if (e.Root == root) { e.Close = close; return; }   // 幂等
            _entries.Add(new Entry { Root = root, Close = close });
        }

        /// <summary>注销弹窗(如多页签关闭某页签、销毁其根 Canvas 时),免登记表积累死条目。</summary>
        public static void Unregister(GameObject root)
        {
            if (root == null) return;
            for (int i = _entries.Count - 1; i >= 0; i--)
                if (_entries[i].Root == null || _entries[i].Root == root) _entries.RemoveAt(i);
        }

        public static bool AnyOpen()
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                if (e.Root != null && e.Root.activeInHierarchy) return true;
            }
            return false;
        }

        /// <summary>关掉当前最上层的一个开着的弹窗;关到了返回 true(供右键逐个关闭)。</summary>
        public static bool CloseTopmost()
        {
            Entry top = null; int topOrder = int.MinValue;
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                if (e.Root == null || !e.Root.activeInHierarchy) continue;
                int order = 0;
                var cv = e.Root.GetComponent<Canvas>();
                if (cv != null) order = cv.sortingOrder;
                if (order >= topOrder) { topOrder = order; top = e; }   // >= 让并列时后登记者(更晚弹出)优先关
            }
            if (top == null) return false;
            try { top.Close(); } catch { }
            return true;
        }
    }
}
