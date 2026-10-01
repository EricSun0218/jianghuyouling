using System;
using System.Reflection;
using UnityEngine;

namespace JianghuYouling
{
    /// <summary>关系变更后,促使游戏【原生】互动 UI 重新评估选项,修「结为恋人/化解仇怨后,原生对话框还停留在『倾诉爱意/化解』上」。
    /// 只触发【显示刷新】(不改游戏状态):反射调地块角色的私有 RefreshInteraction(重取 GetCharacterDisplayDataForMapBlock→重评互动选项)。
    /// 注:不再调 UpdateShowingEventTaiwuCharacterDisplayData——它只刷新太吾在事件窗里的头像(不刷互动选项,对本需求无用),
    /// 且无事件窗显示时(_displayingEventData=null)原生方法不做空判会在域线程抛 NPE;那 NPE 在域线程,前端 try/catch 拦不住。</summary>
    public static class NativeUiRefresh
    {
        // 反射句柄缓存
        static FieldInfo _charIdF2, _charIdF1;
        static MethodInfo _refreshM2, _refreshM1;
        static bool _reflectReady;

        static void EnsureReflect()
        {
            if (_reflectReady) return;
            _reflectReady = true;
            const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
            try { _charIdF2 = typeof(MapBlockCharBase2).GetField("CharId", BF); _refreshM2 = typeof(MapBlockCharBase2).GetMethod("RefreshInteraction", BF); } catch { }
            try { _charIdF1 = typeof(MapBlockCharBase).GetField("CharId", BF); _refreshM1 = typeof(MapBlockCharBase).GetMethod("RefreshInteraction", BF); } catch { }
        }

        /// <summary>某 NPC 与太吾的关系刚变(恋人/仇怨/绝交等),刷新原生互动显示。</summary>
        public static void AfterRelationChange(int npcId)
        {
            // 地块角色互动菜单重评(倾诉爱意/化解 等按钮按当前关系重新显隐)
            if (npcId <= 0) return;
            try
            {
                EnsureReflect();
                int hit = 0;
                hit += RefreshType<MapBlockCharBase2>(npcId, _charIdF2, _refreshM2);
                hit += RefreshType<MapBlockCharBase>(npcId, _charIdF1, _refreshM1);
                Debug.Log("[江湖有灵] 原生刷新:互动菜单重评 npc=" + npcId + " 命中 " + hit + " 个地块角色");
            }
            catch (Exception e) { Debug.Log("[江湖有灵] 原生刷新:互动菜单重评失败(忽略) " + e.GetType().Name); }
        }

        static int RefreshType<T>(int npcId, FieldInfo charIdF, MethodInfo refreshM) where T : MonoBehaviour
        {
            if (charIdF == null || refreshM == null) return 0;
            int n = 0;
            var all = Resources.FindObjectsOfTypeAll<T>();
            foreach (var c in all)
            {
                if (c == null || !c.gameObject.scene.IsValid() || !c.isActiveAndEnabled) continue;
                int cid;
                try { cid = (int)charIdF.GetValue(c); } catch { continue; }
                if (cid != npcId) continue;
                try { refreshM.Invoke(c, null); n++; } catch { }
            }
            return n;
        }
    }
}
