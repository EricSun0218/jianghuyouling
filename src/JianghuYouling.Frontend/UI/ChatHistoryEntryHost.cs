using System;
using System.Collections;
using System.Reflection;
using Game.Views.CharacterMenu;        // ViewCharacterMenuInfo, ViewCharacterMenu
using TMPro;
using UnityEngine;
using UnityEngine.UI;                  // Image, Button
using JianghuYouling.Core.Llm;         // LlmService

namespace JianghuYouling
{
    /// <summary>反射访问 ViewCharacterMenuInfo 的 private 字段。nonTaiwuRoot=非太吾内容根;
    /// talkButton=本体「交谈」键；这里只复用 RectTransform，不依赖它是否允许交谈。</summary>
    internal static class CmReflect
    {
        const BindingFlags NP = BindingFlags.Instance | BindingFlags.NonPublic;
        static readonly FieldInfo NonTaiwuRootF = typeof(ViewCharacterMenuInfo).GetField("nonTaiwuRoot", NP);
        static readonly FieldInfo FeatureScrollF = typeof(ViewCharacterMenuInfo).GetField("featureScroll", NP);
        static readonly FieldInfo TalkBtnF = typeof(ViewCharacterMenuInfo).GetField("talkButton", NP);

        public static GameObject NonTaiwuRoot(ViewCharacterMenuInfo v) => v == null ? null : NonTaiwuRootF?.GetValue(v) as GameObject;

        // 本体刷新会临时隐藏/禁用交谈键；仍可读取其几何，不继承交谈权限。
        public static RectTransform TalkButtonLayoutRt(ViewCharacterMenuInfo v)
        {
            var o = v == null ? null : TalkBtnF?.GetValue(v);
            var comp = o as Component;
            if (comp == null && o is GameObject g) comp = g.transform;
            if (comp == null) return null;
            return comp.transform as RectTransform;
        }

        // 「人物特性」面板的 RectTransform(陌生人页定位锚)
        public static RectTransform FeatureScrollRt(ViewCharacterMenuInfo v)
        {
            var fs = v == null ? null : FeatureScrollF?.GetValue(v) as Component;
            return fs != null ? fs.transform as RectTransform : null;
        }
    }

    /// <summary>0.3s 轮询宿主:在人物详情页(同道与普通人物页都显示)自建一条等宽两半的操作条:
    /// 左「往事成书」(按当前人物聊天与记忆写作)+ 右「千里传音」(远程对话)。
    /// 使用本体交谈键下方的几何位置，不依赖它可见或可交互。太吾本人页不显示。</summary>
    public sealed class ChatHistoryEntryHost : MonoBehaviour
    {
        public static ChatHistoryEntryHost Instance { get; private set; }
        const string BarName = "JHYL_EntryBar";
        float _next;
        long _requestVersion;
        static int _lastLogChar = -999;   // 仅在打开的角色变化时打一次诊断日志,避免 0.3s 刷屏

        public static void Initialize()
        {
            if (Instance != null) return;
            var go = new GameObject("JHYL_ChatHistoryEntryHost");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<ChatHistoryEntryHost>();
        }

        public static void CancelForWorldExit()
        {
            if (Instance != null) unchecked { Instance._requestVersion++; }
            _lastLogChar = -999;
        }

        public static void Shutdown()
        {
            CancelForWorldExit();
            var instance = Instance;
            Instance = null;
            if (instance != null) try { Destroy(instance.gameObject); } catch { }
        }

        void Update()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.3f;
            if (!WorldLifecycle.HasWorldIdentity) return;
            try
            {
                var info = UIElement.CharacterMenuInfo.UiBaseAs<ViewCharacterMenuInfo>();
                if (info != null && info.gameObject.activeInHierarchy) TryInject(info);
            }
            catch (Exception ex) { Debug.LogWarning("[江湖有灵] 入口条轮询异常: " + ex.GetType().Name); }
        }

        void TryInject(ViewCharacterMenuInfo info)
        {
            var shell = info.CharacterMenu;
            if (shell == null) return;
            int charId = shell.CurCharacterId;
            bool isTaiwu = shell.CurrentCharacterIsTaiwu;

            // 统一挂到「非太吾内容根」(整块面板、非滚动区,稳定可见)。
            var fsRt = CmReflect.FeatureScrollRt(info);
            var root = CmReflect.NonTaiwuRoot(info);
            RectTransform rootRt = root != null ? root.transform as RectTransform : null;
            Transform parent = rootRt;

            bool logThis = charId != _lastLogChar;
            if (logThis) { _lastLogChar = charId; Debug.Log("[江湖有灵] 入口条:charId=" + charId + " isTaiwu=" + isTaiwu + " root=" + (rootRt != null) + " fs=" + (fsRt != null)); }
            if (parent == null) return;

            var existing = parent.Find(BarName);
            // 太吾本人 / 无效角色 → 隐藏
            if (isTaiwu || charId <= 0)
            {
                if (existing != null) existing.gameObject.SetActive(false);
                return;
            }
            bool built = existing == null;
            if (existing == null) existing = BuildBar(parent).transform;
            existing.gameObject.SetActive(true);
            // 1.1.21 已删除 interactionButton；交谈键为当前非太吾人物页的共用锚点。
            var layoutRt = CmReflect.TalkButtonLayoutRt(info);
            PositionBar(existing as RectTransform, rootRt, layoutRt);
            existing.SetAsLastSibling();   // 置同级最上层,避免被遮
            if (logThis || built)
                Debug.Log("[江湖有灵] 入口条:已" + (built ? "新建" : "复用")
                    + "并定位(layout=talk"
                    + " geometry=" + (layoutRt != null) + " fs=" + (fsRt != null)
                    + " 世界坐标=" + ((RectTransform)existing).position + ")");
        }

        // 保留原有等宽双按钮和下方间距，只读取交谈键几何，不依赖刷新时的激活状态。
        static void PositionBar(RectTransform brt, RectTransform rootRt, RectTransform talkLayoutRt)
        {
            if (brt == null) return;
            float h = brt.rect.height; if (h < 10f) h = 42f;
            if (talkLayoutRt != null && talkLayoutRt.rect.width > 40f)
            {
                brt.anchorMin = brt.anchorMax = brt.pivot = new Vector2(0f, 1f);
                brt.sizeDelta = new Vector2(talkLayoutRt.rect.width, h);
                var ic = new Vector3[4]; talkLayoutRt.GetWorldCorners(ic);
                var bc = new Vector3[4]; brt.GetWorldCorners(bc);
                float gap = (ic[1].y - ic[0].y) * 0.2f;
                brt.position += new Vector3(ic[0].x, ic[0].y - gap, ic[0].z) - bc[1];
                return;
            }

            // 极端 prefab 读取失败时仍保持可见，不再因为几何锚点未就绪而主动隐藏。
            brt.anchorMin = brt.anchorMax = brt.pivot = new Vector2(0f, 0f);
            brt.sizeDelta = new Vector2(240f, h);
            brt.anchoredPosition = new Vector2(16f, 90f);
        }

        // 等宽两半的操作条容器:左「往事成书」(青蓝)| 右「千里传音」(暖棕)。容器无图,两键各拉伸占一半,随整条等宽缩放。
        static GameObject BuildBar(Transform parent)
        {
            var bar = new GameObject(BarName, typeof(RectTransform), typeof(LayoutElement));
            bar.transform.SetParent(parent, false);
            bar.GetComponent<LayoutElement>().ignoreLayout = true;   // 若父级带布局组,防其重排/压没本条(钉死手工定位)
            var brt = bar.GetComponent<RectTransform>();
            brt.sizeDelta = new Vector2(220f, 42f);

            // JHYL_ENTRY_BAR_FONT_READY_BEFORE_TMP:TextMeshProUGUI.Awake 会在 AddComponent 时同步执行；
            // 必须先把游戏已加载的中文字体设成 TMP 默认字体，不能等组件创建后再补 t.font。
            var font = ResolveFontBeforeTmp();
            MakeSplit(bar.transform, "JHYL_HistoryBtn", "往事成书", new Color(0.26f, 0.49f, 0.60f, 0.98f), 0f, 0.5f, 0f, -3f, font, OnClickHistory);
            MakeSplit(bar.transform, "JHYL_RemoteBtn", "千里传音", new Color(0.55f, 0.42f, 0.24f, 0.98f), 0.5f, 1f, 3f, 0f, font, OnClickRemote);
            return bar;
        }

        static TMP_FontAsset ResolveFontBeforeTmp()
        {
            TMP_FontAsset font = null;
            try
            {
                var texts = Resources.FindObjectsOfTypeAll<TextMeshProUGUI>();
                foreach (var any in texts)
                    if (any != null && any.font != null && any.gameObject.scene.IsValid()) { font = any.font; break; }
                if (font == null)
                    foreach (var any in texts)
                        if (any != null && any.font != null) { font = any.font; break; }
                if (font == null)
                {
                    var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                    if (fonts != null && fonts.Length > 0) font = fonts[0];
                }
                if (font != null)
                {
                    var settings = TMP_Settings.instance;
                    var fld = settings == null ? null : typeof(TMP_Settings).GetField("m_defaultFontAsset", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (fld != null) fld.SetValue(settings, font);
                    GlyphSanitizer.SetFont(font);
                }
            }
            catch { }
            return font;
        }

        // 在条内造一个按钮:横向锚在 [aMinX, aMaxX] 这一半,左右各留 off 间隙;拉伸填满该半 → 随条等宽缩放,定位无关
        static void MakeSplit(Transform parent, string name, string label, Color color, float aMinX, float aMaxX, float offL, float offR, TMP_FontAsset font, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(aMinX, 0f); rt.anchorMax = new Vector2(aMaxX, 1f);
            rt.offsetMin = new Vector2(offL, 0f); rt.offsetMax = new Vector2(offR, 0f);
            var img = go.GetComponent<Image>(); img.color = color;
            var btn = go.GetComponent<Button>(); btn.targetGraphic = img; btn.onClick.AddListener(onClick);

            var lblGo = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
            lblGo.transform.SetParent(go.transform, false);
            var lrt = lblGo.GetComponent<RectTransform>();
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one; lrt.offsetMin = new Vector2(2f, 2f); lrt.offsetMax = new Vector2(-2f, -2f);
            var t = lblGo.GetComponent<TextMeshProUGUI>();
            if (font != null) t.font = font;
            t.text = label; t.alignment = TextAlignmentOptions.Center; t.raycastTarget = false;
            t.enableAutoSizing = true; UiFontSizeStore.BindAutoSize(t, 10f, 20f);
            t.color = new Color(0.95f, 0.98f, 1f, 1f);
        }

        static bool CurNpc(out int charId, out int taiwuId)
        {
            charId = -1; taiwuId = -1;
            try
            {
                var info = UIElement.CharacterMenuInfo.UiBaseAs<ViewCharacterMenuInfo>();
                var shell = info != null ? info.CharacterMenu : null;
                if (shell == null) return false;
                charId = shell.CurCharacterId;
                if (charId <= 0 || shell.CurrentCharacterIsTaiwu) return false;
                try { taiwuId = SingletonObject.getInstance<BasicGameData>().TaiwuCharId; } catch { }
                return true;
            }
            catch { return false; }
        }

        static void OnClickHistory()
        {
            if (Instance == null || !CurNpc(out int charId, out int taiwuId)) return;
            int generation = WorldLifecycle.Generation; uint worldId = WorldLifecycle.WorldId;
            long requestVersion = unchecked(++Instance._requestVersion);
            try { Instance.StartCoroutine(Instance.ResolveNameThen(charId, taiwuId, false,
                requestVersion, generation, worldId)); }
            catch (Exception e) { Debug.LogWarning("[江湖有灵] 打开人物小说失败: " + e.GetType().Name); }
        }

        static void OnClickRemote()
        {
            if (Instance == null || !CurNpc(out int charId, out int taiwuId)) return;
            // 没配 key 先弹配置页(开个聊不了的窗没意义)
            if (!LlmService.IsConfigured) { try { ConfigWindow.Open((TMP_FontAsset)null); } catch { } return; }
            int generation = WorldLifecycle.Generation; uint worldId = WorldLifecycle.WorldId;
            long requestVersion = unchecked(++Instance._requestVersion);
            try { Instance.StartCoroutine(Instance.ResolveNameThen(charId, taiwuId, true,
                requestVersion, generation, worldId)); }
            catch (Exception e) { Debug.LogWarning("[江湖有灵] 打开千里传音失败: " + e.GetType().Name); }
        }

        // 异步取一次该 NPC 显示名(只为标题/发言人前缀好看),取到或超时后开窗:remote=true→千里传音,false→人物小说。
        IEnumerator ResolveNameThen(int charId, int taiwuId, bool remote,
            long requestVersion, int generation, uint worldId)
        {
            if (!ResolveRequestCurrent(requestVersion, generation, worldId, taiwuId)) yield break;
            string nm = "NPC#" + charId;
            GameData.Domains.Character.Display.CharacterDisplayData dd = null; bool done = false;
            GameData.Domains.Character.CharacterDomainMethod.AsyncCall.GetCharacterDisplayData(null, charId, (offset, pool) =>
            {
                try { GameData.Serializer.Serializer.Deserialize(pool, offset, ref dd); } catch { } finally { done = true; }
            });
            float dl = Time.unscaledTime + 3f;
            while (!done && Time.unscaledTime < dl
                && ResolveRequestCurrent(requestVersion, generation, worldId, taiwuId)) yield return null;
            if (!ResolveRequestCurrent(requestVersion, generation, worldId, taiwuId)) yield break;
            if (dd != null) { try { nm = NameCenter.GetMonasticTitleOrDisplayName(dd, false) ?? nm; } catch { } }
            if (remote) ChatWindow.OpenRemote(charId, taiwuId, nm, null);
            else NpcNovelWindow.Open(charId, taiwuId, nm, null);
        }

        bool ResolveRequestCurrent(long requestVersion, int generation, uint worldId, int taiwuId)
        {
            if (requestVersion != _requestVersion || worldId == 0
                || !WorldLifecycle.IsSameWorld(generation) || WorldLifecycle.WorldId != worldId) return false;
            try { return SingletonObject.getInstance<BasicGameData>().TaiwuCharId == taiwuId; }
            catch { return false; }
        }
    }
}
