using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using JianghuYouling.Effects;

namespace JianghuYouling
{
    /// <summary>
    /// Per-save active actor manager shared by monthly actions and NPC proactive messages.
    /// Current eligible companions are implicit defaults; ordinary characters are explicit
    /// additions. The two features retain independent enable switches.
    /// </summary>
    public static class MonthlyCandidateWindow
    {
        static GameObject _root;
        static RectTransform _list;
        static TextMeshProUGUI _status;
        static TMP_FontAsset _font;
        static int _taiwuId;
        static uint _worldId;
        static int _generation = -1;
        static long _requestEpoch;

        public static void Open(int taiwuId, TMP_FontAsset font)
        {
            CancelForWorldExit();
            if (taiwuId <= 0 || !WorldLifecycle.HasWorldIdentity) return;
            _taiwuId = taiwuId;
            _worldId = WorldLifecycle.WorldId;
            _generation = WorldLifecycle.Generation;
            if (font != null) _font = font;
            if (_font == null) _font = ResolveFont();
            long epoch = ++_requestEpoch;
            BuildShell(epoch);
            var host = ConfigHost.Instance;
            if (host == null) { SetStatus("无法读取当前存档。", true); return; }
            host.StartCoroutine(LoadRows(epoch));
        }

        public static void CancelForWorldExit()
        {
            unchecked { _requestEpoch++; }
            _taiwuId = 0;
            _worldId = 0;
            _generation = -1;
            DestroyRoot();
        }

        static bool IsCurrent(long epoch)
            => epoch == _requestEpoch && _taiwuId > 0 && _worldId > 0
                && WorldLifecycle.IsSameWorld(_generation) && WorldLifecycle.WorldId == _worldId;

        static IEnumerator LoadRows(long epoch)
        {
            if (!IsCurrent(epoch)) yield break;
            SetStatus("正在读取可主动行事的人物……", false);

            bool teamDone = false, teamOk = false;
            List<int> team = null;
            EffectHandler.QueryNonBabyCompanionGroup(_taiwuId, (ok, ids) =>
            {
                if (!IsCurrent(epoch)) return;
                teamOk = ok; team = ids; teamDone = true;
            });
            float deadline = Time.unscaledTime + EffectHandler.ReadOnlyQueryWaitSeconds;
            while (!teamDone && Time.unscaledTime < deadline && IsCurrent(epoch)) yield return null;
            if (!IsCurrent(epoch)) yield break;
            if (!teamDone || !teamOk)
            {
                SetStatus("同道名单读取失败，请稍后重开。", true);
                yield break;
            }

            var stored = CompanionMonthlyCandidateStore.Load(_taiwuId);
            bool manualDone = false, manualOk = false;
            List<int> manual = null;
            EffectHandler.QueryMonthlyAgentEligibility(stored.Included, (ok, ids, _) =>
            {
                if (!IsCurrent(epoch)) return;
                manualOk = ok; manual = ids; manualDone = true;
            });
            deadline = Time.unscaledTime + EffectHandler.ReadOnlyQueryWaitSeconds;
            while (!manualDone && Time.unscaledTime < deadline && IsCurrent(epoch)) yield return null;
            if (!IsCurrent(epoch)) yield break;
            if (!manualDone || !manualOk)
            {
                SetStatus("手动加入名单读取失败，请稍后重开。", true);
                yield break;
            }

            team = team ?? new List<int>();
            manual = manual ?? new List<int>();
            List<int> effective = CompanionMonthlyCandidateStore.BuildEffective(_taiwuId, team, manual);
            var teamSet = new HashSet<int>(team);
            bool namesDone = effective.Count == 0;
            List<string> names = new List<string>();
            if (effective.Count > 0)
                EffectHandler.QueryCharNames(effective, value =>
                {
                    if (!IsCurrent(epoch)) return;
                    names = value ?? new List<string>(); namesDone = true;
                });
            deadline = Time.unscaledTime + EffectHandler.ReadOnlyQueryWaitSeconds;
            while (!namesDone && Time.unscaledTime < deadline && IsCurrent(epoch)) yield return null;
            if (!IsCurrent(epoch)) yield break;

            ClearRows();
            if (effective.Count == 0)
            {
                AddEmptyRow("当前名单为空。可在人物对话窗口点击「加入主动」。");
                SetStatus("共 0 人。", false);
                yield break;
            }
            for (int i = 0; i < effective.Count; i++)
            {
                int id = effective[i];
                string name = i < names.Count && !string.IsNullOrWhiteSpace(names[i])
                    ? names[i] : ("人物#" + id);
                AddActorRow(epoch, id, name, teamSet.Contains(id));
            }
            SetStatus("共 " + effective.Count + " 人；同道只是默认候选，也可移除或重新加入。", false);
            Canvas.ForceUpdateCanvases();
        }

        static void AddActorRow(long epoch, int npcId, string name, bool companion)
        {
            if (_list == null) return;
            var row = new GameObject("Actor_" + npcId, typeof(RectTransform), typeof(Image),
                typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            row.transform.SetParent(_list, false);
            row.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.055f);
            row.GetComponent<LayoutElement>().preferredHeight = 46f;
            var layout = row.GetComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(12, 8, 4, 4); layout.spacing = 10f;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlHeight = true; layout.childControlWidth = true;
            layout.childForceExpandHeight = true; layout.childForceExpandWidth = false;

            var label = NewText("Name", row.transform, 17, TextAlignmentOptions.Left);
            label.text = name + (companion
                ? "  <size=76%><color=#7FB89A>当前同道 · 默认</color></size>"
                : "  <size=76%><color=#C5A86B>手动加入</color></size>");
            label.enableWordWrapping = false; label.overflowMode = TextOverflowModes.Ellipsis;
            label.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

            var buttonGo = NewButton("Remove", row.transform, "移除", 16, out var button);
            var buttonLe = buttonGo.AddComponent<LayoutElement>();
            buttonLe.preferredWidth = 78f; buttonLe.preferredHeight = 34f;
            button.onClick.AddListener(() =>
            {
                if (!IsCurrent(epoch)) { CancelForWorldExit(); return; }
                if (!CompanionMonthlyCandidateStore.Remove(_taiwuId, npcId, companion))
                { SetStatus("移除失败，名单没有写入磁盘。", true); return; }
                try { UnityEngine.Object.Destroy(row); } catch { }
                ChatWindow.NotifyMonthlyCandidateChanged(_taiwuId, npcId);
                SetStatus("已移除「" + name + "」。需要时可在其对话窗口重新加入。", false);
            });
        }

        static void AddEmptyRow(string value)
        {
            var text = NewText("Empty", _list, 16, TextAlignmentOptions.Center);
            text.text = value; text.color = new Color(0.66f, 0.68f, 0.63f, 1f);
            text.gameObject.AddComponent<LayoutElement>().preferredHeight = 64f;
        }

        static void ClearRows()
        {
            if (_list == null) return;
            for (int i = _list.childCount - 1; i >= 0; i--)
                try { UnityEngine.Object.Destroy(_list.GetChild(i).gameObject); } catch { }
        }

        static void BuildShell(long epoch)
        {
            DestroyRoot();
            _root = new GameObject("JHYL_MonthlyCandidates", typeof(Canvas), typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            UnityEngine.Object.DontDestroyOnLoad(_root);
            var canvas = _root.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 30030;
            var scaler = _root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);

            var dim = new GameObject("Dim", typeof(RectTransform), typeof(Image), typeof(Button));
            dim.transform.SetParent(_root.transform, false);
            var dimRt = dim.GetComponent<RectTransform>();
            dimRt.anchorMin = Vector2.zero; dimRt.anchorMax = Vector2.one;
            dimRt.offsetMin = dimRt.offsetMax = Vector2.zero;
            dim.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.56f);
            dim.GetComponent<Button>().onClick.AddListener(CancelForWorldExit);

            var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(_root.transform, false);
            var panelRt = panel.GetComponent<RectTransform>();
            panelRt.anchorMin = panelRt.anchorMax = panelRt.pivot = new Vector2(0.5f, 0.5f);
            panelRt.sizeDelta = new Vector2(680f, 620f);
            panel.GetComponent<Image>().color = new Color(0.10f, 0.115f, 0.105f, 0.995f);

            var title = NewText("Title", panel.transform, 23, TextAlignmentOptions.Left);
            title.text = "主动人物名单";
            Anchor(title.rectTransform, new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(20, -54), new Vector2(-80, -12));
            var closeGo = NewButton("Close", panel.transform, "X", 20, out var close);
            var closeRt = closeGo.GetComponent<RectTransform>();
            closeRt.anchorMin = closeRt.anchorMax = closeRt.pivot = new Vector2(1, 1);
            closeRt.anchoredPosition = new Vector2(-12, -12); closeRt.sizeDelta = new Vector2(42, 38);
            close.onClick.AddListener(CancelForWorldExit);

            var hint = NewText("Hint", panel.transform, 15, TextAlignmentOptions.TopLeft);
            hint.text = "此名单同时用于主动来信与过月主动行事，两项功能可在设置中分别关闭。同道只是默认候选，也可移除；其他人物可从对话窗口加入。婴儿、动物、死亡或失效人物不会进入名单。";
            hint.enableWordWrapping = true; hint.color = new Color(0.72f, 0.74f, 0.68f, 1f);
            Anchor(hint.rectTransform, new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(20, -112), new Vector2(-20, -64));

            var scrollGo = new GameObject("Scroll", typeof(RectTransform), typeof(Image),
                typeof(ScrollRect), typeof(RectMask2D));
            scrollGo.transform.SetParent(panel.transform, false);
            Anchor(scrollGo.GetComponent<RectTransform>(), Vector2.zero, Vector2.one,
                new Vector2(20, 66), new Vector2(-20, -124));
            scrollGo.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.22f);
            var scroll = scrollGo.GetComponent<ScrollRect>();
            scroll.horizontal = false; scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            var content = new GameObject("Content", typeof(RectTransform),
                typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            _list = content.GetComponent<RectTransform>();
            _list.SetParent(scrollGo.transform, false);
            _list.anchorMin = new Vector2(0, 1); _list.anchorMax = new Vector2(1, 1);
            _list.pivot = new Vector2(0.5f, 1); _list.offsetMin = Vector2.zero; _list.offsetMax = Vector2.zero;
            var vertical = content.GetComponent<VerticalLayoutGroup>();
            vertical.padding = new RectOffset(8, 8, 8, 8); vertical.spacing = 7f;
            vertical.childControlWidth = true; vertical.childForceExpandWidth = true;
            vertical.childControlHeight = true; vertical.childForceExpandHeight = false;
            content.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.content = _list;
            scroll.verticalScrollbar = UiScroll.AddVerticalForScrollRect(scrollGo);
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;

            _status = NewText("Status", panel.transform, 15, TextAlignmentOptions.Left);
            Anchor(_status.rectTransform, new Vector2(0, 0), new Vector2(1, 0),
                new Vector2(20, 18), new Vector2(-20, 54));
            try { PopupRegistry.Register(_root, CancelForWorldExit); } catch { }
        }

        static void SetStatus(string value, bool error)
        {
            if (_status == null) return;
            _status.text = value ?? string.Empty;
            _status.color = error ? new Color(0.92f, 0.48f, 0.42f, 1f)
                : new Color(0.68f, 0.78f, 0.70f, 1f);
        }

        static void DestroyRoot()
        {
            GameObject root = _root; _root = null; _list = null; _status = null;
            if (root == null) return;
            try { PopupRegistry.Unregister(root); } catch { }
            try { UnityEngine.Object.Destroy(root); } catch { }
        }

        static TextMeshProUGUI NewText(string name, Transform parent, float size, TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var text = go.GetComponent<TextMeshProUGUI>();
            if (_font != null) text.font = _font;
            UiFontSizeStore.Bind(text, size); text.alignment = align; text.richText = true;
            text.color = new Color(0.92f, 0.90f, 0.82f, 1f);
            return text;
        }

        static GameObject NewButton(string name, Transform parent, string label, float size, out Button button)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = new Color(0.30f, 0.38f, 0.36f, 0.95f);
            button = go.GetComponent<Button>();
            var text = NewText("Label", go.transform, size, TextAlignmentOptions.Center);
            text.text = label; text.raycastTarget = false;
            Anchor(text.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            return go;
        }

        static void Anchor(RectTransform rt, Vector2 min, Vector2 max, Vector2 offMin, Vector2 offMax)
        {
            rt.anchorMin = min; rt.anchorMax = max; rt.offsetMin = offMin; rt.offsetMax = offMax;
        }

        static TMP_FontAsset ResolveFont()
        {
            try
            {
                foreach (var text in Resources.FindObjectsOfTypeAll<TextMeshProUGUI>())
                    if (text != null && text.font != null && text.gameObject.scene.IsValid()) return text.font;
            }
            catch { }
            return null;
        }
    }
}
