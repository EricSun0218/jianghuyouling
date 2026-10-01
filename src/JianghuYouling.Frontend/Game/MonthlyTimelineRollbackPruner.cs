using System;
using System.Collections.Generic;
using GameData.Common;
using JianghuYouling.Core.Memory;
using UnityEngine;

namespace JianghuYouling
{
    /// <summary>
    /// 本体存档是月度事实的唯一时间权威。每次进入/换代完成身份和日期反序列化后，
    /// 本地保留存档当前月及更早的已结算投影，只裁掉严格晚于存档月份的旧时间线。
    /// 当前月不会再次触发月变事件；删掉它会造成永久缺月而不是重新生成。
    /// </summary>
    internal static class MonthlyTimelineRollbackPruner
    {
        private static string _lastBoundary;

        internal static void ResetForWorldExit() => _lastBoundary = null;

        internal static void PruneIfReady()
        {
            if (!WorldLifecycle.HasWorldIdentity) return;
            try
            {
                BasicGameData data = SingletonObject.getInstance<BasicGameData>();
                if (data == null || data.WorldId != WorldLifecycle.WorldId
                    || data.TaiwuCharId <= 0 || data.CurrDate < 0) return;
                string boundary = data.WorldId + ":" + data.TaiwuCharId + ":" + data.CurrDate;
                if (string.Equals(_lastBoundary, boundary, StringComparison.Ordinal)) return;
                int firstInvalidDate = data.CurrDate == int.MaxValue
                    ? int.MaxValue : data.CurrDate + 1;

                // 先关闭任何残留恢复入口，再裁剪回档点之后的投影、事务证据和 NPC 月度记忆。
                // 各存储方法采用“含边界删除”，所以传入当前月的下一月。
                bool stateOk = MonthlySettlement.PruneTimelineAtOrAfter(firstInvalidDate);
                bool companionOk = CompanionMonthlyActions.PruneTimelineAtOrAfter(
                    data.WorldId, data.TaiwuCharId, firstInvalidDate);
                bool eventOk = MonthlyEventGenerator.PruneTimelineAtOrAfter(
                    data.TaiwuCharId, firstInvalidDate);
                bool archiveOk = EventLogStore.PruneAtOrAfter(data.TaiwuCharId, firstInvalidDate);
                bool memoryOk = PruneMonthlyMemories(data.TaiwuCharId, firstInvalidDate);
                bool commissionOk = CommissionStore.PruneForRollback(data.TaiwuCharId,
                    data.CurrDate);
                if (!(stateOk && companionOk && eventOk && archiveOk && memoryOk
                    && commissionOk))
                {
                    Debug.LogWarning("[JHYL_MONTHLY_TIMELINE_PRUNE_INCOMPLETE] world="
                        + data.WorldId + " taiwu=" + data.TaiwuCharId + " boundary="
                        + data.CurrDate + " state=" + stateOk + " companion=" + companionOk
                        + " event=" + eventOk + " archive=" + archiveOk + " memory=" + memoryOk
                        + " commission=" + commissionOk);
                    return;
                }
                _lastBoundary = boundary;
                MonthlyChronicleNoticeStore.ResetForWorldExit();
                MonthlyChronicleProgressStore.ResetForWorldExit();
                AssistantWidget.NotifyCommissionChanged();
                CommissionWindow.RefreshIfOpen();
                Debug.Log("[JHYL_MONTHLY_TIMELINE_PRUNED] world=" + data.WorldId
                    + " taiwu=" + data.TaiwuCharId + " kept_through=" + data.CurrDate);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[JHYL_MONTHLY_TIMELINE_PRUNE_FAILED] " + e.GetType().Name);
            }
        }

        private static bool PruneMonthlyMemories(int taiwuId, int currentDate)
        {
            List<int> npcIds = NpcMemoryStore.EnumerateNpcIds(
                JianghuYoulingPaths.Memories, taiwuId.ToString());
            foreach (int npcId in npcIds)
            {
                NpcMemoryStore memory = NpcMemoryStore.Load(JianghuYoulingPaths.Memories,
                    taiwuId.ToString(), npcId.ToString());
                if (!memory.LoadReliable) return false;
                var removeIds = new List<string>();
                foreach (MemoryEntry entry in memory.All)
                {
                    if (entry == null || entry.WorldDate < currentDate
                        || string.IsNullOrWhiteSpace(entry.Id) || !IsMonthlyMemory(entry)) continue;
                    removeIds.Add(entry.Id);
                }
                if (removeIds.Count > 0 && (memory.RemoveByIds(removeIds) != removeIds.Count
                    || !memory.Save())) return false;
            }
            return true;
        }

        private static bool IsMonthlyMemory(MemoryEntry entry)
        {
            string kind = (entry.SourceKind ?? string.Empty).Trim();
            if (kind == "monthly_event_fanout" || kind == "companion_monthly_outcomes"
                || kind == "companion_monthly_target" || kind == "companion_monthly_note"
                || kind == "companion_message") return true;
            // 兼容此前 remember 没有 SourceKind 的记录，只按明确的过月关键词/稳定来源识别。
            string keywords = entry.Keywords ?? string.Empty;
            string source = entry.SourceId ?? string.Empty;
            return kind.Length == 0 && (keywords.IndexOf("同道主动行事", StringComparison.Ordinal) >= 0
                || source.StartsWith("companion-local-story|", StringComparison.Ordinal));
        }
    }
}
