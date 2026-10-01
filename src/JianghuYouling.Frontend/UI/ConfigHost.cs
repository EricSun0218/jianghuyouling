using UnityEngine;
using TMPro;

namespace JianghuYouling
{
    /// <summary>
    /// 常驻宿主:作为 ConfigWindow 的协程载体 + 字体提供者。DontDestroyOnLoad。
    /// (配置面板由聊天窗内的"设置"按钮打开;本 mod 不占用任何游戏快捷键。)
    /// 打开时尽量从场景里现成的 TMP 文本借一个游戏中文字体,避免缺字形。
    /// </summary>
    public sealed class ConfigHost : MonoBehaviour
    {
        public static ConfigHost Instance { get; private set; }

        // 缓存借来的游戏中文字体,避免每次开窗都全场景扫描
        TMP_FontAsset _font;

        void Update()
        {
            // Monthly model work is asynchronous and may still be running after the player
            // begins a trip. Observe the authoritative travel transition every frame so both
            // lanes are cancelled before any later tool dispatch.
            MonthlySettlement.ObserveTravelState();
        }

        public static void Initialize()
        {
            if (Instance != null) return;
            var go = new GameObject("JHYL_ConfigHost");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<ConfigHost>();
        }

        public static void Shutdown()
        {
            var instance = Instance;
            Instance = null;
            if (instance != null) try { Destroy(instance.gameObject); } catch { }
        }

        // 从场景中任一 TextMeshProUGUI 借游戏中文字体;借不到则返回 null(TMP 退化为默认字体)
        TMP_FontAsset ResolveFont()
        {
            if (_font != null) return _font;
            try
            {
                var texts = Resources.FindObjectsOfTypeAll<TextMeshProUGUI>();
                foreach (var t in texts)
                {
                    // 跳过我们自己面板里的文本,优先借游戏自带文本的字体
                    if (t != null && t.font != null && t.gameObject.scene.IsValid())
                    {
                        _font = t.font;
                        break;
                    }
                }
                // 退一步:连场景内的也借不到,就取任意一个非空字体
                if (_font == null)
                {
                    foreach (var t in texts)
                        if (t != null && t.font != null) { _font = t.font; break; }
                }
            }
            catch { _font = null; }
            return _font;
        }
    }
}
