using System;
using System.IO;
using TMPro;
using UnityEngine;

namespace JianghuYouling
{
    /// <summary>
    /// 《江湖有灵》自有界面的全局字体档位。当前既有字号即小号，保证升级后默认观感不变；
    /// 中号与大号只缩放由本 Mod 显式绑定的 TMP 文本，不影响游戏本体界面。
    /// </summary>
    public static class UiFontSizeStore
    {
        public const int Small = 0;
        public const int Medium = 1;
        public const int Large = 2;
        public const int Default = Small;

        static int _current = Default;

        public static int Current => _current;

        internal static event Action Changed;

        static string PathFor()
            => Path.Combine(JianghuYoulingPaths.Settings, "ui_font_size.txt");

        public static void Initialize()
        {
            SetCurrent(Load());
        }

        public static int Load()
        {
            try
            {
                string path = PathFor();
                if (DurableSettingsStore.TryLoad(path, DurableSettingsStore.Small, IsValid,
                    out string raw) && int.TryParse(raw.Trim(), out int value))
                    return Normalize(value);
            }
            catch { }
            return Default;
        }

        public static bool Save(int value)
        {
            value = Normalize(value);
            try
            {
                if (!DurableSettingsStore.Save(PathFor(), value.ToString(),
                    DurableSettingsStore.Small, IsValid)) return false;
                SetCurrent(value);
                return true;
            }
            catch { return false; }
        }

        public static string Label(int value)
        {
            switch (Normalize(value))
            {
                case Medium: return "中";
                case Large: return "大";
                default: return "小";
            }
        }

        public static float Scale(float authoredSize)
        {
            float factor = _current == Medium ? 1.15f : _current == Large ? 1.30f : 1f;
            return Mathf.Round(authoredSize * factor * 10f) / 10f;
        }

        public static void Bind(TextMeshProUGUI text, float authoredSize)
        {
            if (text == null) return;
            UiFontSizeBinding binding = text.GetComponent<UiFontSizeBinding>();
            if (binding == null) binding = text.gameObject.AddComponent<UiFontSizeBinding>();
            binding.Initialize(text, authoredSize, false, 0f, 0f);
        }

        public static void BindAutoSize(TextMeshProUGUI text, float authoredMinimum,
            float authoredMaximum)
        {
            if (text == null) return;
            UiFontSizeBinding binding = text.GetComponent<UiFontSizeBinding>();
            if (binding == null) binding = text.gameObject.AddComponent<UiFontSizeBinding>();
            binding.Initialize(text, authoredMaximum, true, authoredMinimum, authoredMaximum);
        }

        static int Normalize(int value)
            => value >= Small && value <= Large ? value : Default;

        static bool IsValid(string value)
            => DurableSettingsStore.IsSingleLine(value, 4, false)
                && int.TryParse(value.Trim(), out int parsed)
                && parsed >= Small && parsed <= Large;

        static void SetCurrent(int value)
        {
            value = Normalize(value);
            if (_current == value) return;
            _current = value;
            Action changed = Changed;
            if (changed == null) return;
            foreach (Delegate subscriber in changed.GetInvocationList())
            {
                try { ((Action)subscriber)(); }
                catch { }
            }
        }
    }

    /// <summary>记录控件的原始字号并响应全局档位变化；随 GameObject 启停自动订阅。</summary>
    internal sealed class UiFontSizeBinding : MonoBehaviour
    {
        TextMeshProUGUI _text;
        float _authoredSize;
        float _authoredMinimum;
        float _authoredMaximum;
        bool _autoSize;
        bool _initialized;
        bool _subscribed;

        internal void Initialize(TextMeshProUGUI text, float authoredSize, bool autoSize,
            float authoredMinimum, float authoredMaximum)
        {
            _text = text;
            _authoredSize = authoredSize;
            _autoSize = autoSize;
            _authoredMinimum = authoredMinimum;
            _authoredMaximum = authoredMaximum;
            _initialized = true;
            Refresh();
        }

        void OnEnable()
        {
            if (!_subscribed)
            {
                UiFontSizeStore.Changed += Refresh;
                _subscribed = true;
            }
            Refresh();
        }

        void OnDisable()
        {
            Unsubscribe();
        }

        void OnDestroy()
        {
            Unsubscribe();
        }

        void Unsubscribe()
        {
            if (!_subscribed) return;
            UiFontSizeStore.Changed -= Refresh;
            _subscribed = false;
        }

        void Refresh()
        {
            if (!_initialized || _text == null) return;
            if (_autoSize)
            {
                _text.fontSizeMin = UiFontSizeStore.Scale(_authoredMinimum);
                _text.fontSizeMax = UiFontSizeStore.Scale(_authoredMaximum);
                _text.fontSize = UiFontSizeStore.Scale(_authoredMaximum);
            }
            else
            {
                _text.fontSize = UiFontSizeStore.Scale(_authoredSize);
            }
        }
    }
}
