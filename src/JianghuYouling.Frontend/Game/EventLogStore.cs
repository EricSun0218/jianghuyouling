using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using JianghuYouling.Core.Persistence;
using UnityEngine;

namespace JianghuYouling
{
    /// <summary>一桩已发生的过月江湖事件:故事正文 + 真实落地的动作明细 + 风闻人数 + 世界日期/地点。</summary>
    public sealed class EventLogEntry
    {
        public string EventId;                            // 稳定投影 id；过月重启恢复用它幂等 upsert
        public int Date;                                  // 世界日期(开局起的月序)
        public string Area;                               // 发生地(区域名)
        public string Text;                               // 故事正文(街坊口耳相传那段)
        public string Brief;                              // 新闻简报式摘要(新版本优先显示)
        public string Detail;                             // 点击详情后按真实落地动作生成的完整故事
        public string Roster;                             // 当事人名册快照(供延迟生成详情)
        public string StopReason;                         // 本月未完成部分被取消时的明确原因
        public List<string> Actions = new List<string>(); // 真实落地的动作明细(如「甲将一门武艺传授给乙」)
        public int Heard;                                 // 江湖上约几人风闻
        // 同月总览归档；旧记录没有此字段时按空集合读取。
        public List<CompanionMonthlyLogEntry> CompanionActions = new List<CompanionMonthlyLogEntry>();
    }

    public sealed class CompanionMonthlyLogEntry
    {
        public int NpcId;
        public string Name;
        public string LocationText;
        public string Summary;
        public string Detail;
        public List<string> Outcomes = new List<string>();
    }

    /// <summary>过月「江湖纪事」持久库:每月一条真实发生的事件,按太吾存档分文件存。供历史查看窗只读回放。
    /// 最多留近 300 条,超出丢最旧。新者在前。</summary>
    public static class EventLogStore
    {
        const int Cap = 300;
        const int MaxFileBytes = 8 * 1024 * 1024;
        static readonly object Sync = new object();
        static readonly HashSet<string> Unreliable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static string PathFor(int taiwuId) => Path.Combine(JianghuYoulingPaths.Events, "events_" + taiwuId + ".json");

        public static List<EventLogEntry> Load(int taiwuId)
        {
            if (taiwuId <= 0) return new List<EventLogEntry>();
            lock (Sync) try
            {
                var p = PathFor(taiwuId);
                if (DurableFileStore.TryReadRecoverableText(p, MaxFileBytes, IsValidDocument,
                    out string json, out bool any, out _))
                {
                    Unreliable.Remove(p);
                    var l = JArray.Parse(json).ToObject<List<EventLogEntry>>();
                    if (l != null) return l;
                }
                if (any) Unreliable.Add(p); else Unreliable.Remove(p);
            }
            catch (Exception e) { Debug.LogWarning("[江湖有灵] 纪事读取失败: " + e.GetType().Name); }
            return new List<EventLogEntry>();
        }

        public static bool Append(int taiwuId, EventLogEntry e)
        {
            if (taiwuId <= 0 || e == null) return false;
            lock (Sync) try
            {
                var list = Load(taiwuId);
                list.Insert(0, e);   // 新者在前
                return SaveAll(taiwuId, list);
            }
            catch (Exception e2) { Debug.LogWarning("[江湖有灵] 纪事存盘失败: " + e2.GetType().Name); return false; }
        }

        public static bool Upsert(int taiwuId, EventLogEntry value)
        {
            if (taiwuId <= 0 || value == null || string.IsNullOrWhiteSpace(value.EventId)) return false;
            lock (Sync) try
            {
                var list = Load(taiwuId);
                int found = -1;
                for (int i = 0; i < list.Count; i++)
                    if (list[i] != null && string.Equals(list[i].EventId, value.EventId, StringComparison.Ordinal))
                    { found = i; break; }
                if (found >= 0)
                {
                    EventLogEntry existing = list[found];
                    if ((value.CompanionActions == null || value.CompanionActions.Count == 0)
                        && existing?.CompanionActions != null)
                        value.CompanionActions = new List<CompanionMonthlyLogEntry>(existing.CompanionActions);
                    if (string.IsNullOrWhiteSpace(value.StopReason)
                        && !string.IsNullOrWhiteSpace(existing?.StopReason))
                        value.StopReason = existing.StopReason;
                    list.RemoveAt(found);
                }
                // A guaranteed non-mutating story is used only when the normal event lane ends
                // empty.  If a later retry commits the real event for the same month, replace the
                // fallback and carry over any companion digest already merged into it.
                if (!IsGuaranteedFallback(value.EventId))
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        EventLogEntry fallback = list[i];
                        if (fallback == null || fallback.Date != value.Date
                            || !IsGuaranteedFallback(fallback.EventId)) continue;
                        if ((value.CompanionActions == null || value.CompanionActions.Count == 0)
                            && fallback.CompanionActions != null)
                            value.CompanionActions = new List<CompanionMonthlyLogEntry>(fallback.CompanionActions);
                        if (string.IsNullOrWhiteSpace(value.StopReason)
                            && !string.IsNullOrWhiteSpace(fallback.StopReason))
                            value.StopReason = fallback.StopReason;
                        list.RemoveAt(i);
                    }
                // 若同道结果先于江湖故事完成，月度协调器会先写一条仅含同道的占位归档。
                // 真正事件随后提交时合并它，避免同一年月在时间导航中留下重复记录。
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    EventLogEntry pendingDigest = list[i];
                    if (pendingDigest == null || pendingDigest.Date != value.Date
                        || !IsMonthlyDigestOnly(pendingDigest.EventId)) continue;
                    if ((value.CompanionActions == null || value.CompanionActions.Count == 0)
                        && pendingDigest.CompanionActions != null)
                        value.CompanionActions = new List<CompanionMonthlyLogEntry>(pendingDigest.CompanionActions);
                    if (string.IsNullOrWhiteSpace(value.StopReason)
                        && !string.IsNullOrWhiteSpace(pendingDigest.StopReason))
                        value.StopReason = pendingDigest.StopReason;
                    list.RemoveAt(i);
                }
                list.Insert(0, value);
                return SaveAll(taiwuId, list);
            }
            catch (Exception e2) { Debug.LogWarning("[江湖有灵] 纪事幂等提交失败: " + e2.GetType().Name); return false; }
        }

        // JHYL_MONTHLY_DIGEST_ARCHIVE: 把已完成的同道结果增量并入同月纪事。它不改写江湖
        // 事件正文或动作回执；事件关闭/尚未完成时使用稳定的仅总览记录，后续可无损合并。
        public static bool UpsertMonthlyDigest(int taiwuId, int date, IList<CompanionMonthlyResult> companions)
        {
            if (taiwuId <= 0 || date < 0 || companions == null) return false;
            lock (Sync) try
            {
                var list = Load(taiwuId);
                EventLogEntry target = null;
                foreach (EventLogEntry entry in list)
                    if (entry != null && entry.Date == date && !IsMonthlyDigestOnly(entry.EventId))
                    { target = entry; break; }
                if (target == null)
                    foreach (EventLogEntry entry in list)
                        if (entry != null && entry.Date == date && IsMonthlyDigestOnly(entry.EventId))
                        { target = entry; break; }
                if (target == null)
                {
                    target = new EventLogEntry
                    {
                        EventId = MonthlyDigestEventId(taiwuId, date),
                        Date = date,
                        Area = "",
                        Text = "",
                        Brief = "本月同道行止",
                        Detail = "",
                        Roster = "",
                        Actions = new List<string>(),
                        Heard = 0,
                    };
                    list.Insert(0, target);
                }
                target.CompanionActions = CloneCompanions(companions);
                return SaveAll(taiwuId, list);
            }
            catch (Exception e2)
            {
                Debug.LogWarning("[江湖有灵] 月度总览归档失败: " + e2.GetType().Name);
                return false;
            }
        }

        private static List<CompanionMonthlyLogEntry> CloneCompanions(IList<CompanionMonthlyResult> values)
        {
            var result = new List<CompanionMonthlyLogEntry>();
            if (values == null) return result;
            foreach (CompanionMonthlyResult value in values)
            {
                if (value == null) continue;
                result.Add(new CompanionMonthlyLogEntry
                {
                    NpcId = value.NpcId,
                    Name = value.Name ?? "",
                    LocationText = value.LocationText ?? "",
                    Summary = !string.IsNullOrWhiteSpace(value.Summary)
                        ? value.Summary : (value.Detail ?? ""),
                    // 新版主动行事正文只在玩家点“查看详情”后由后台模型生成。
                    // 过月阶段的确定性投影只作为摘要/事实使用，不能冒充已生成正文。
                    Detail = "",
                    Outcomes = value.Outcomes == null ? new List<string>() : new List<string>(value.Outcomes),
                });
            }
            return result;
        }

        private static List<CompanionMonthlyLogEntry> CloneCompanionLogEntries(
            IList<CompanionMonthlyLogEntry> values)
        {
            var result = new List<CompanionMonthlyLogEntry>();
            if (values == null) return result;
            foreach (CompanionMonthlyLogEntry value in values)
            {
                if (value == null) continue;
                result.Add(new CompanionMonthlyLogEntry
                {
                    NpcId = value.NpcId,
                    Name = value.Name ?? "",
                    LocationText = value.LocationText ?? "",
                    Summary = value.Summary ?? "",
                    Detail = value.Detail ?? "",
                    Outcomes = value.Outcomes == null ? new List<string>() : new List<string>(value.Outcomes),
                });
            }
            return result;
        }

        private static string MonthlyDigestEventId(int taiwuId, int date)
            => "monthly-digest:" + taiwuId + ":" + date;

        private static bool IsMonthlyDigestOnly(string eventId)
            => !string.IsNullOrWhiteSpace(eventId)
                && eventId.StartsWith("monthly-digest:", StringComparison.Ordinal);

        private static bool IsGuaranteedFallback(string eventId)
            => !string.IsNullOrWhiteSpace(eventId)
                && eventId.StartsWith("monthly-guaranteed:", StringComparison.Ordinal);

        public static bool TryUpdateHeard(int taiwuId, string eventId, int heard)
        {
            if (taiwuId <= 0 || string.IsNullOrWhiteSpace(eventId) || heard < 0) return false;
            lock (Sync) try
            {
                var list = Load(taiwuId);
                EventLogEntry found = null;
                foreach (var entry in list)
                    if (entry != null && string.Equals(entry.EventId, eventId, StringComparison.Ordinal))
                    { found = entry; break; }
                if (found == null) return false;
                found.Heard = heard;
                return SaveAll(taiwuId, list);
            }
            catch (Exception e2) { Debug.LogWarning("[江湖有灵] 纪事风闻数更新失败: " + e2.GetType().Name); return false; }
        }

        /// <summary>把整月生成被安全取消的原因持久化；允许与已经落地的部分结果共存。</summary>
        public static bool UpsertMonthlyStopReason(int taiwuId, int date, string reason)
        {
            if (taiwuId <= 0 || date < 0 || string.IsNullOrWhiteSpace(reason)) return false;
            string bounded = reason.Trim();
            if (bounded.Length > 1024) bounded = bounded.Substring(0, 1024);
            lock (Sync) try
            {
                var list = Load(taiwuId);
                EventLogEntry target = null;
                foreach (EventLogEntry entry in list)
                    if (entry != null && entry.Date == date && !IsMonthlyDigestOnly(entry.EventId))
                    { target = entry; break; }
                if (target == null)
                    foreach (EventLogEntry entry in list)
                        if (entry != null && entry.Date == date && IsMonthlyDigestOnly(entry.EventId))
                        { target = entry; break; }
                if (target == null)
                {
                    target = new EventLogEntry
                    {
                        EventId = MonthlyDigestEventId(taiwuId, date),
                        Date = date,
                        Area = "",
                        Text = "",
                        Brief = "本月过月记录",
                        Detail = "",
                        Roster = "",
                        Actions = new List<string>(),
                        Heard = 0,
                    };
                    list.Insert(0, target);
                }
                target.StopReason = bounded;
                return SaveAll(taiwuId, list);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[江湖有灵] 过月中止原因归档失败: " + e.GetType().Name);
                return false;
            }
        }

        /// <summary>把玩家点击“详情”后按需生成的同道正文补进该月归档；真实结果与摘要保持不变。</summary>
        public static bool TryUpdateCompanionDetail(int taiwuId, int date, int npcId, string detail)
        {
            if (taiwuId <= 0 || date < 0 || npcId <= 0 || string.IsNullOrWhiteSpace(detail)) return false;
            lock (Sync) try
            {
                var list = Load(taiwuId);
                bool changed = false;
                foreach (EventLogEntry entry in list)
                {
                    if (entry == null || entry.Date != date || entry.CompanionActions == null) continue;
                    foreach (CompanionMonthlyLogEntry companion in entry.CompanionActions)
                    {
                        if (companion == null || companion.NpcId != npcId) continue;
                        companion.Detail = detail.Trim();
                        changed = true;
                    }
                }
                return changed && SaveAll(taiwuId, list);
            }
            catch (Exception e2)
            {
                Debug.LogWarning("[江湖有灵] 同道纪事正文补写失败: " + e2.GetType().Name);
                return false;
            }
        }

        /// <summary>把玩家点击“查看详情”后由后台模型生成的江湖事件正文补入稳定事件。</summary>
        public static bool TryUpdateEventDetail(int taiwuId, string eventId, string detail)
        {
            if (taiwuId <= 0 || string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(detail)) return false;
            lock (Sync) try
            {
                var list = Load(taiwuId);
                EventLogEntry found = null;
                foreach (EventLogEntry entry in list)
                    if (entry != null && string.Equals(entry.EventId, eventId, StringComparison.Ordinal))
                    { found = entry; break; }
                if (found == null) return false;
                found.Detail = detail.Trim();
                return SaveAll(taiwuId, list);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[江湖有灵] 江湖事件正文补写失败: " + e.GetType().Name);
                return false;
            }
        }

        /// <summary>
        /// Removes story projections for a date after the current game save proves that their
        /// backend operations do not exist. Companion results are retained as a digest-only row;
        /// that lane performs its own receipt reconciliation and may replace them independently.
        /// </summary>
        public static bool RemoveStoryProjectionAtDate(int taiwuId, int date)
        {
            if (taiwuId <= 0 || date < 0) return false;
            lock (Sync) try
            {
                var list = Load(taiwuId);
                var companions = new List<CompanionMonthlyLogEntry>();
                EventLogEntry digest = null;
                string stopReason = null;
                bool changed = false;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    EventLogEntry entry = list[i];
                    if (entry == null || entry.Date != date) continue;
                    if (entry.CompanionActions != null && entry.CompanionActions.Count > 0
                        && companions.Count == 0)
                        companions = CloneCompanionLogEntries(entry.CompanionActions);
                    if (string.IsNullOrWhiteSpace(stopReason)
                        && !string.IsNullOrWhiteSpace(entry.StopReason))
                        stopReason = entry.StopReason;
                    if (IsMonthlyDigestOnly(entry.EventId))
                    {
                        digest = entry;
                        continue;
                    }
                    list.RemoveAt(i);
                    changed = true;
                }
                if (companions.Count > 0 || !string.IsNullOrWhiteSpace(stopReason))
                {
                    if (digest == null)
                    {
                        digest = new EventLogEntry
                        {
                            EventId = MonthlyDigestEventId(taiwuId, date),
                            Date = date,
                            Area = "",
                            Text = "",
                            Brief = "本月同道行止",
                            Detail = "",
                            Roster = "",
                            Actions = new List<string>(),
                            Heard = 0,
                        };
                        list.Insert(0, digest);
                        changed = true;
                    }
                    digest.CompanionActions = companions;
                    digest.StopReason = stopReason;
                }
                return !changed || SaveAll(taiwuId, list);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[JHYL_EVENT_STALE_PROJECTION_CLEANUP_FAILED] taiwu=" + taiwuId
                    + " date=" + date + " exception=" + e.GetType().Name);
                return false;
            }
        }

        /// <summary>
        /// 游戏存档回到某个月时，该月尚未重新结算，因而本地只允许保留更早月份。
        /// 删除当前月及未来月的完整纪事与同道总览，防止旧时间线在回档后复现。
        /// </summary>
        public static bool PruneAtOrAfter(int taiwuId, int currentDate)
        {
            if (taiwuId <= 0 || currentDate < 0) return false;
            lock (Sync) try
            {
                var list = Load(taiwuId);
                int removed = list.RemoveAll(entry => entry != null && entry.Date >= currentDate);
                return removed == 0 || SaveAll(taiwuId, list);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[JHYL_MONTHLY_TIMELINE_PRUNE_FAILED] archive taiwu="
                    + taiwuId + " date=" + currentDate + " exception=" + e.GetType().Name);
                return false;
            }
        }

        public static bool SaveAll(int taiwuId, List<EventLogEntry> list)
        {
            if (taiwuId <= 0 || list == null) return false;
            lock (Sync) try
            {
                var p = PathFor(taiwuId);
                if (Unreliable.Contains(p)) throw new InvalidDataException("纪事候选全部损坏，拒绝覆盖恢复现场");
                if (!DurableFileStore.TryReadRecoverableText(p, MaxFileBytes, IsValidDocument,
                    out _, out bool any, out _) && any)
                { Unreliable.Add(p); throw new InvalidDataException("纪事候选全部损坏，拒绝覆盖恢复现场"); }
                var keep = new List<EventLogEntry>(list);
                if (keep.Count > Cap) keep.RemoveRange(Cap, keep.Count - Cap);
                // 纪事会在长档中累积到数百 KB；紧凑 JSON 可直接降低每次耐久读回与
                // 原子三副本提交的主线程 I/O，不改变任何存档字段或向后兼容性。
                string json = JsonConvert.SerializeObject(keep, Formatting.None);
                if (!DurableFileStore.TryWriteTextAtomic(p, json, MaxFileBytes, IsValidDocument))
                    throw new IOException("纪事耐久提交或语义读回失败");
                Unreliable.Remove(p);
                return true;
            }
            catch (Exception e2) { Debug.LogWarning("[江湖有灵] 纪事存盘失败: " + e2.GetType().Name); return false; }
        }

        private static bool IsValidDocument(string json)
        {
            if (!DurableFileStore.TryParseJsonStrict(json, 16, out JToken root)
                || !(root is JArray array) || array.Count > Cap) return false;
            foreach (JToken token in array)
            {
                if (!(token is JObject item)
                    || !DurableFileStore.HasOnlyProperties(item, "EventId", "Date", "Area", "Text", "Brief", "Detail",
                        "Roster", "StopReason", "Actions", "Heard", "CompanionActions") || item["Date"]?.Type != JTokenType.Integer
                    || (item["Heard"] != null && item["Heard"].Type != JTokenType.Integer)
                    || !DurableFileStore.IsBoundedString(item["EventId"], 256)
                    || !DurableFileStore.IsBoundedString(item["Area"], 1024)
                    || !DurableFileStore.IsBoundedString(item["Text"], 131072)
                    || !DurableFileStore.IsBoundedString(item["Brief"], 16384)
                    || !DurableFileStore.IsBoundedString(item["Detail"], 262144)
                    || !DurableFileStore.IsBoundedString(item["Roster"], 32768)
                    || !DurableFileStore.IsBoundedString(item["StopReason"], 1024)) return false;
                JToken actions = item["Actions"];
                if (actions != null && actions.Type != JTokenType.Null)
                {
                    if (!(actions is JArray actionArray) || actionArray.Count > 256) return false;
                    foreach (JToken action in actionArray)
                        if (!DurableFileStore.IsBoundedString(action, 8192, false)) return false;
                }
                JToken companions = item["CompanionActions"];
                if (companions != null && companions.Type != JTokenType.Null)
                {
                    if (!(companions is JArray companionArray) || companionArray.Count > 128) return false;
                    foreach (JToken companion in companionArray)
                    {
                        if (!(companion is JObject value)
                            || !DurableFileStore.HasOnlyProperties(value, "NpcId", "Name", "LocationText", "Summary", "Detail", "Outcomes")
                            || value["NpcId"]?.Type != JTokenType.Integer
                            || !DurableFileStore.IsBoundedString(value["Name"], 1024)
                            || !DurableFileStore.IsBoundedString(value["LocationText"], 2048)
                            || !DurableFileStore.IsBoundedString(value["Summary"], 16384)
                            || !DurableFileStore.IsBoundedString(value["Detail"], 131072)) return false;
                        JToken outcomes = value["Outcomes"];
                        if (outcomes != null && outcomes.Type != JTokenType.Null)
                        {
                            if (!(outcomes is JArray outcomeArray) || outcomeArray.Count > 64) return false;
                            foreach (JToken outcome in outcomeArray)
                                if (!DurableFileStore.IsBoundedString(outcome, 8192, false)) return false;
                        }
                    }
                }
            }
            return true;
        }
    }
}
