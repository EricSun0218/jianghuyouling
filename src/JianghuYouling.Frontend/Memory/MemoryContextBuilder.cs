using System.Collections.Generic;
using JianghuYouling.Core.Memory;

namespace JianghuYouling
{
    /// <summary>
    /// 统一记忆组装:AI 要扮演这个 NPC,就该握有它的全部上下文。
    /// 把 NPC 底座现取的信息(人生经历、所知秘闻、与太吾的关系)与对话沉淀的持久记忆,
    /// 合成同一个候选池,按当下话题相关性 + 重要性 + 新近度统一排序,挑出最该被想起的若干条组装进上下文。
    /// 底座条目是"现取的活信息",不持久化(随游戏态变化);只有对话沉淀才落盘。
    /// </summary>
    public static class MemoryContextBuilder
    {
        /// <summary>由快照底座生成活记忆候选(经历/秘闻/与太吾关系/赋性印象)。</summary>
        public static List<MemoryEntry> LiveFromSnapshot(NpcSnapshot s)
        {
            var list = new List<MemoryEntry>();
            if (s == null) return list;

            if (s.LifeRecords != null)
                foreach (var (date, type, text) in s.LifeRecords)
                    if (!string.IsNullOrWhiteSpace(text))
                        list.Add(new MemoryEntry { Content = text, Type = MemoryType.Event, Keywords = text, Importance = 4, WorldDate = date, Valid = true });

            if (s.Secrets != null)
                foreach (var (date, type, text) in s.Secrets)
                    if (!string.IsNullOrWhiteSpace(text))
                        list.Add(new MemoryEntry { Content = text, Type = MemoryType.Secret, Keywords = text, Importance = 6, WorldDate = date, Valid = true });

            // 与太吾的关系也是记忆的一部分
            string rel = string.IsNullOrWhiteSpace(s.Relation) ? "" : ("与太吾的关系:" + s.Relation);
            string fav = string.IsNullOrWhiteSpace(s.FavorLevel) ? "" : ("对太吾好感:" + s.FavorLevel);
            string relLine = string.Join(";", new[] { rel, fav }).Trim(';');
            if (!string.IsNullOrWhiteSpace(relLine))
                list.Add(new MemoryEntry { Content = relLine, Type = MemoryType.Impression, Keywords = "太吾,关系,好感", Importance = 7, WorldDate = s.CurrentDate, Valid = true });

            return list;
        }

        /// <summary>合并 持久记忆 + 底座活记忆,按话题统一排序,取前 limit 条,格式化成 prompt 行。</summary>
        public static List<string> Assemble(IReadOnlyList<MemoryEntry> persisted, NpcSnapshot s, string topic, long now, int limit)
        {
            var pool = new List<MemoryEntry>();
            if (persisted != null) pool.AddRange(persisted);
            pool.AddRange(LiveFromSnapshot(s));

            var top = MemoryRanker.TopK(pool, topic, now, limit);
            var lines = new List<string>(top.Count);
            foreach (var e in top)
                if (!string.IsNullOrWhiteSpace(e.Content))
                    lines.Add("[" + TypeLabel(e.Type) + "] " + e.Content.Trim());
            return lines;
        }

        private static string TypeLabel(MemoryType t)
        {
            switch (t)
            {
                case MemoryType.Favor: return "恩情";
                case MemoryType.Grudge: return "仇怨";
                case MemoryType.Promise: return "承诺";
                case MemoryType.Secret: return "秘闻";
                case MemoryType.Event: return "经历";
                default: return "印象";
            }
        }
    }
}
