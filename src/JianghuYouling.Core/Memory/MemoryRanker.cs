using System;
using System.Collections.Generic;
using System.Linq;

namespace JianghuYouling.Core.Memory
{
    /// <summary>
    /// 统一相关性排序:把记忆条目按 与当下话题的内容/关键词重叠 + 重要性 + 新近度 + 频次/巩固 排序,
    /// 并做时间衰减(core 免疫)与 MMR 去重召回(避免一次想起多条近义记忆)。纯逻辑,可单测,无 embedding。
    /// </summary>
    public static class MemoryRanker
    {
        const double HalfLifeMonths = 36.0;   // 非核心记忆的时间衰减半衰期(月);core 免疫
        const double Ln2 = 0.6931471805599453;
        const double MmrLambda = 0.7;         // MMR 相关性 vs 多样性权衡(越大越偏相关)

        /// <summary>按 晋升分(重要度+频次+巩固+新近)取前 k——用于索引超大时收窄"交给模型选取"的候选集。</summary>
        public static List<MemoryEntry> TopByImportanceRecency(IEnumerable<MemoryEntry> all, int k, long now)
        {
            if (all == null) return new List<MemoryEntry>();
            return all.Where(e => e != null && e.Valid)
                      .OrderByDescending(e => PromoteScore(e, now))
                      .Take(Math.Max(0, k))
                      .ToList();
        }

        /// <summary>按相关性取前 limit,并做 MMR 去重(同义记忆不重复召回);core 参与排序但不衰减。</summary>
        public static List<MemoryEntry> TopK(IEnumerable<MemoryEntry> all, string topic, long now, int limit)
        {
            if (all == null || limit <= 0) return new List<MemoryEntry>();
            var topicTokens = Tokenize(topic);
            var cand = all.Where(e => e != null && e.Valid)
                          .Select(e => new Cand { E = e, S = Score(e, topicTokens, now), T = TokensOf(e) })
                          .OrderByDescending(c => c.S)
                          .Take(Math.Max(limit * 4, limit))   // 限定 MMR 工作集,控成本
                          .ToList();
            if (cand.Count == 0) return new List<MemoryEntry>();
            double maxS = cand.Max(c => c.S); if (maxS <= 0) maxS = 1;

            var picked = new List<Cand>();
            while (picked.Count < limit && cand.Count > 0)
            {
                Cand best = null; double bestMmr = double.NegativeInfinity;
                foreach (var c in cand)
                {
                    double sim = 0;
                    foreach (var p in picked) { double j = Jaccard(c.T, p.T); if (j > sim) sim = j; }
                    double mmr = MmrLambda * (c.S / maxS) - (1 - MmrLambda) * sim;
                    if (mmr > bestMmr) { bestMmr = mmr; best = c; }
                }
                if (best == null) break;
                picked.Add(best); cand.Remove(best);
            }
            return picked.Select(c => c.E).ToList();
        }

        sealed class Cand { public MemoryEntry E; public double S; public HashSet<string> T; }

        public static double Score(MemoryEntry e, HashSet<string> topic, long now)
        {
            double overlap = 0;
            if (topic != null && topic.Count > 0)
            {
                // 关键词命中权重高,正文命中权重低
                foreach (var k in Tokenize(e.Keywords)) if (topic.Contains(k)) overlap += 1.0;
                foreach (var k in Tokenize(e.Content)) if (topic.Contains(k)) overlap += 0.4;
            }
            double age = Math.Max(0, now - e.WorldDate);
            double recency = 1.0 / (1.0 + age);
            double freq = Math.Min(1.0, Math.Log(1 + Math.Max(0, e.RecallCount)) / Math.Log(7.0));   // 频次(被想起越多越牢)
            double consol = Math.Min(1.0, Math.Max(0, e.RecallMonths) / 3.0);                            // 巩固(跨多月被想起)
            double baseScore = e.Importance * 0.8 + recency * 2.0 + freq * 1.0 + consol * 0.8;
            double decay = e.IsCore ? 1.0 : Math.Exp(-Ln2 * age / HalfLifeMonths);                     // core 免衰减
            return overlap * 3.0 + baseScore * decay;   // 话题相关分不衰减;基底分随时间衰减
        }

        /// <summary>晋升/淘汰打分(无话题):0.45·重要度 + 0.25·频次 + 0.2·巩固 + 0.1·新近(巩固/淘汰门控)。</summary>
        public static double PromoteScore(MemoryEntry e, long now)
        {
            if (e == null) return 0;
            double rel = e.Importance / 10.0;
            double freq = Math.Min(1.0, Math.Log(1 + Math.Max(0, e.RecallCount)) / Math.Log(7.0));
            double consol = Math.Min(1.0, Math.Max(0, e.RecallMonths) / 3.0);
            double recency = 1.0 / (1.0 + Math.Max(0, now - e.WorldDate));
            return 0.45 * rel + 0.25 * freq + 0.2 * consol + 0.1 * recency;
        }

        /// <summary>
        /// Tie-breaker for bounded core-memory retention. Explicitly pinned memories
        /// outrank threshold-only Importance=9 entries; promises and grudges then retain
        /// precedence because forgetting either can make the character contradict itself.
        /// </summary>
        public static int CoreRetentionPriority(MemoryEntry e)
        {
            if (e == null) return 0;
            int value = e.Core ? 100 : 0;
            switch (e.Type)
            {
                case MemoryType.Promise: return value + 40;
                case MemoryType.Grudge: return value + 30;
                case MemoryType.Secret: return value + 20;
                case MemoryType.Favor: return value + 10;
                default: return value;
            }
        }

        /// <summary>两个 token 集的 Jaccard 相似度(去重/MMR 用)。</summary>
        public static double Jaccard(HashSet<string> a, HashSet<string> b)
        {
            if (a == null || b == null || a.Count == 0 || b.Count == 0) return 0;
            int inter = 0;
            foreach (var x in a) if (b.Contains(x)) inter++;
            int union = a.Count + b.Count - inter;
            return union == 0 ? 0 : (double)inter / union;
        }

        /// <summary>一条记忆的 token 集(正文 + 关键词),供 Jaccard 去重/MMR。</summary>
        public static HashSet<string> TokensOf(MemoryEntry e)
        {
            if (e == null) return new HashSet<string>();
            var set = Tokenize(e.Content);
            foreach (var k in Tokenize(e.Keywords)) set.Add(k);
            return set;
        }

        public static HashSet<string> Tokenize(string s)
        {
            var set = new HashSet<string>();
            if (string.IsNullOrEmpty(s)) return set;
            // 英文/数字按分隔符切;中文按 2-gram 切(无分词器时的轻量近似,够话题召回/去重用)
            foreach (var seg in s.Split(new[] { ' ', ',', '，', '、', ';', '；', '。', '\n', '\r', '\t', ':', ':', '(', ')', '(', ')', '"', '”', '“' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = seg.Trim();
                if (t.Length == 0) continue;
                bool han = false;
                foreach (var ch in t) if (ch >= 0x4E00 && ch <= 0x9FFF) { han = true; break; }
                if (han)
                {
                    if (t.Length == 1) set.Add(t);
                    for (int i = 0; i + 1 < t.Length; i++) set.Add(t.Substring(i, 2));  // 2-gram
                }
                else set.Add(t.ToLowerInvariant());
            }
            return set;
        }
    }
}
