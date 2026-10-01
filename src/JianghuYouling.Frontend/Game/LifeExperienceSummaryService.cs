using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEngine;
using JianghuYouling.Core.Llm;
using JianghuYouling.Core.Persistence;
using JianghuYouling.Core.Prompt;

namespace JianghuYouling
{
    /// <summary>
    /// 每人物独立的生平滚动摘要。游戏本体生平永不删除；摘要保存精确来源前缀指纹，
    /// 读档/回档导致记录减少或变化时会自动作废重建，避免未来经历串回旧档。
    /// </summary>
    internal static class LifeExperienceSummaryService
    {
        internal const int RecentRawCount = 28;
        internal const int CompactStep = 12;
        const int MaxBatchRecords = 120;
        const int MaxBatchChars = 24000;
        const int MaxRawFallback = 40;

        static readonly object ActiveGate = new object();
        static readonly HashSet<string> Active = new HashSet<string>(StringComparer.Ordinal);

        internal static IEnumerator BuildForQuery(int taiwuId, NpcSnapshot snapshot,
            CancellationToken cancellation, Func<bool> stillCurrent, Action<string> done)
        {
            if (snapshot == null || snapshot.NpcId <= 0 || snapshot.LifeRecords == null
                || snapshot.LifeRecords.Count == 0)
            {
                done?.Invoke("(你这一生还没什么可记的大事。)");
                yield break;
            }

            var records = new List<(int date, string type, string text)>(snapshot.LifeRecords);
            records.Sort(CompareRecords);
            uint worldId = JianghuYoulingPaths.CurrentWorldId;
            string key = worldId + ":" + taiwuId + ":" + snapshot.NpcId;

            while (!TryBegin(key))
            {
                if (!IsCurrent(worldId, cancellation, stillCurrent))
                {
                    done?.Invoke("(本轮已取消，未继续翻检生平。)");
                    yield break;
                }
                yield return null;
            }

            LifeExperienceSummaryStore.Document document = null;
            try
            {
                document = ValidatedDocument(worldId, taiwuId, snapshot.NpcId, records);
                int compactableCount = Math.Max(0, records.Count - RecentRawCount);
                int pendingCount = compactableCount - document.CompactedCount;

                // 首次及后续都按固定步长整理；不足一步时仍作为近期原文保留，避免每新增一条
                // 生平就触发一次模型请求。
                if (pendingCount >= CompactStep)
                {
                    OpenAiCompatibleClient client = LlmService.GetBackgroundClient();
                    int cursor = document.CompactedCount;
                    string summary = document.Summary;
                    LlmTraceContext trace = LlmTraceContext.NewRun(
                        "life-experience:" + snapshot.NpcId);

                    while (client != null && cursor < compactableCount)
                    {
                        if (!IsCurrent(worldId, cancellation, stillCurrent)) break;

                        List<LifeExperienceFact> batch = TakeBatch(records, cursor,
                            compactableCount, out int next);
                        if (batch.Count == 0 || next <= cursor) break;
                        var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                            cancellation);
                        var task = client.SendAsyncWithTrace(
                            LifeExperienceCompactor.BuildMessages(snapshot.Name, summary, batch),
                            2048, 0.2, requestCancellation.Token, 120, false, "生平整理",
                            LlmReasoningPolicy.Off,
                            trace.WithRound(cursor / Math.Max(1, MaxBatchRecords),
                                "life-experience-compaction"));
                        while (!task.IsCompleted)
                        {
                            if (!IsCurrent(worldId, cancellation, stillCurrent))
                            {
                                try { requestCancellation.Cancel(); } catch { }
                                break;
                            }
                            yield return null;
                        }
                        bool requestStillCurrent = IsCurrent(worldId, cancellation, stillCurrent);
                        requestCancellation.Dispose();
                        if (!requestStillCurrent || !task.IsCompleted
                            || task.IsCanceled || task.IsFaulted) break;

                        LlmResult result = null;
                        try { result = task.Result; } catch { }
                        string nextSummary = result != null && result.Ok
                            ? LifeExperienceCompactor.Clean(result.Content) : string.Empty;
                        if (string.IsNullOrWhiteSpace(nextSummary)) break;

                        var candidate = new LifeExperienceSummaryStore.Document
                        {
                            SchemaVersion = LifeExperienceSummaryStore.CurrentSchemaVersion,
                            WorldId = worldId,
                            TaiwuId = taiwuId,
                            NpcId = snapshot.NpcId,
                            CompactedCount = next,
                            PrefixSha256 = PrefixSha256(records, next),
                            Summary = nextSummary,
                            UpdatedUtcTicks = DateTime.UtcNow.Ticks,
                        };
                        // 每批单独提交；中途网络失败时下次从最后可靠批次续做，而不是重算全部。
                        if (!LifeExperienceSummaryStore.Save(candidate)) break;
                        document = candidate;
                        summary = nextSummary;
                        cursor = next;
                    }
                }
            }
            finally
            {
                End(key);
            }

            if (!IsCurrent(worldId, cancellation, stillCurrent))
            {
                done?.Invoke("(本轮已取消，未继续翻检生平。)");
                yield break;
            }

            // 保存后重新校验一次；若当前调用中持久化失败，仍只采用之前已可靠落盘的摘要。
            document = ValidatedDocument(worldId, taiwuId, snapshot.NpcId, records);
            done?.Invoke(Render(records, document));
        }

        static int CompareRecords((int date, string type, string text) left,
            (int date, string type, string text) right)
        {
            int result = left.date.CompareTo(right.date);
            if (result != 0) return result;
            result = string.Compare(left.type, right.type, StringComparison.Ordinal);
            return result != 0 ? result
                : string.Compare(left.text, right.text, StringComparison.Ordinal);
        }

        static bool IsCurrent(uint worldId, CancellationToken cancellation,
            Func<bool> stillCurrent)
            => worldId != 0 && !cancellation.IsCancellationRequested
                && JianghuYoulingPaths.CurrentWorldId == worldId
                && (stillCurrent == null || stillCurrent());

        static LifeExperienceSummaryStore.Document ValidatedDocument(uint worldId, int taiwuId,
            int npcId, IList<(int date, string type, string text)> records)
        {
            LifeExperienceSummaryStore.Document document =
                LifeExperienceSummaryStore.Load(worldId, taiwuId, npcId);
            if (document == null || document.CompactedCount <= 0
                || document.CompactedCount > records.Count
                || document.CompactedCount > Math.Max(0, records.Count - RecentRawCount)
                || !string.Equals(document.PrefixSha256,
                    PrefixSha256(records, document.CompactedCount), StringComparison.OrdinalIgnoreCase))
                return LifeExperienceSummaryStore.Empty(worldId, taiwuId, npcId);
            return document;
        }

        static List<LifeExperienceFact> TakeBatch(
            IList<(int date, string type, string text)> records, int start, int end,
            out int next)
        {
            var batch = new List<LifeExperienceFact>();
            int chars = 0;
            next = start;
            while (next < end && batch.Count < MaxBatchRecords)
            {
                var record = records[next];
                int recordChars = (record.type?.Length ?? 0) + (record.text?.Length ?? 0) + 32;
                if (batch.Count > 0 && chars + recordChars > MaxBatchChars) break;
                batch.Add(new LifeExperienceFact
                {
                    Date = record.date,
                    Type = record.type,
                    Text = record.text,
                });
                chars += recordChars;
                next++;
            }
            return batch;
        }

        static string Render(IList<(int date, string type, string text)> records,
            LifeExperienceSummaryStore.Document document)
        {
            var output = new StringBuilder("你的生平大事：");
            int rawStart = 0;
            if (document != null && document.CompactedCount > 0
                && !string.IsNullOrWhiteSpace(document.Summary))
            {
                output.Append("\n\n【较早经历摘要】\n").Append(document.Summary.Trim());
                rawStart = document.CompactedCount;
            }

            int boundedStart = Math.Max(rawStart, records.Count - MaxRawFallback);
            if (boundedStart > rawStart)
                output.Append("\n（另有 ").Append(boundedStart - rawStart)
                    .Append(" 条较早原始记录尚待下一次整理；不要据此编造。）");
            output.Append("\n\n【最近经历原文（由近及远）】");
            for (int index = records.Count - 1; index >= boundedStart; index--)
            {
                var record = records[index];
                if (string.IsNullOrWhiteSpace(record.text)) continue;
                int date = record.date < 0 ? 0 : record.date;
                output.Append("\n· 第").Append(date / 12 + 1).Append("年")
                    .Append(date % 12 + 1).Append("月");
                if (!string.IsNullOrWhiteSpace(record.type))
                    output.Append("〔").Append(record.type).Append("〕");
                output.Append(' ').Append(record.text);
            }
            return output.ToString();
        }

        internal static string PrefixSha256(
            IList<(int date, string type, string text)> records, int count)
        {
            var canonical = new StringBuilder();
            int safeCount = Math.Max(0, Math.Min(count, records?.Count ?? 0));
            canonical.Append(safeCount).Append('|');
            for (int index = 0; index < safeCount; index++)
            {
                var record = records[index];
                string type = record.type ?? string.Empty;
                string text = record.text ?? string.Empty;
                canonical.Append(index).Append(':').Append(record.date).Append(':')
                    .Append(type.Length).Append(':').Append(type)
                    .Append(':').Append(text.Length).Append(':').Append(text).Append('|');
            }
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical.ToString()));
                var hex = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash) hex.Append(value.ToString("x2"));
                return hex.ToString();
            }
        }

        static bool TryBegin(string key)
        {
            lock (ActiveGate) return Active.Add(key);
        }

        static void End(string key)
        {
            lock (ActiveGate) Active.Remove(key);
        }
    }

    internal static class LifeExperienceSummaryStore
    {
        internal const int CurrentSchemaVersion = 1;
        const int MaxFileBytes = 64 * 1024;
        static readonly object Sync = new object();

        internal sealed class Document
        {
            public int SchemaVersion;
            public uint WorldId;
            public int TaiwuId;
            public int NpcId;
            public int CompactedCount;
            public string PrefixSha256;
            public string Summary;
            public long UpdatedUtcTicks;
        }

        internal static Document Empty(uint worldId, int taiwuId, int npcId)
            => new Document
            {
                SchemaVersion = CurrentSchemaVersion,
                WorldId = worldId,
                TaiwuId = taiwuId,
                NpcId = npcId,
                PrefixSha256 = string.Empty,
                Summary = string.Empty,
            };

        static string PathFor(int taiwuId, int npcId)
            => Path.Combine(JianghuYoulingPaths.Memories,
                "LifeExperience_" + taiwuId + "_" + npcId + ".json");

        internal static Document Load(uint worldId, int taiwuId, int npcId)
        {
            lock (Sync)
            {
                try
                {
                    if (!DurableFileStore.TryReadRecoverableText(PathFor(taiwuId, npcId),
                        MaxFileBytes, IsValidDocument, out string json, out _, out _)
                        || string.IsNullOrWhiteSpace(json)) return null;
                    JObject root = JObject.Parse(json);
                    var document = new Document
                    {
                        SchemaVersion = root.Value<int>("schemaVersion"),
                        WorldId = root.Value<uint>("worldId"),
                        TaiwuId = root.Value<int>("taiwuId"),
                        NpcId = root.Value<int>("npcId"),
                        CompactedCount = root.Value<int>("compactedCount"),
                        PrefixSha256 = root.Value<string>("prefixSha256"),
                        Summary = root.Value<string>("summary"),
                        UpdatedUtcTicks = root.Value<long>("updatedUtcTicks"),
                    };
                    return document.SchemaVersion == CurrentSchemaVersion
                        && document.WorldId == worldId && document.TaiwuId == taiwuId
                        && document.NpcId == npcId ? document : null;
                }
                catch { return null; }
            }
        }

        internal static bool Save(Document document)
        {
            if (document == null || document.SchemaVersion != CurrentSchemaVersion
                || document.WorldId == 0 || document.TaiwuId <= 0 || document.NpcId <= 0
                || document.CompactedCount <= 0
                || string.IsNullOrWhiteSpace(document.PrefixSha256)
                || string.IsNullOrWhiteSpace(document.Summary)) return false;
            lock (Sync)
            {
                try
                {
                    var root = new JObject
                    {
                        ["schemaVersion"] = document.SchemaVersion,
                        ["worldId"] = document.WorldId,
                        ["taiwuId"] = document.TaiwuId,
                        ["npcId"] = document.NpcId,
                        ["compactedCount"] = document.CompactedCount,
                        ["prefixSha256"] = document.PrefixSha256,
                        ["summary"] = document.Summary,
                        ["updatedUtcTicks"] = document.UpdatedUtcTicks,
                    };
                    return DurableFileStore.TryWriteTextAtomic(
                        PathFor(document.TaiwuId, document.NpcId), root.ToString(),
                        MaxFileBytes, IsValidDocument);
                }
                catch { return false; }
            }
        }

        static bool IsValidDocument(string json)
        {
            if (!DurableFileStore.TryParseJsonStrict(json, 8, out JToken token)
                || !(token is JObject root)
                || !DurableFileStore.HasOnlyProperties(root, "schemaVersion", "worldId",
                    "taiwuId", "npcId", "compactedCount", "prefixSha256", "summary",
                    "updatedUtcTicks")
                || root["schemaVersion"]?.Type != JTokenType.Integer
                || root.Value<int>("schemaVersion") != CurrentSchemaVersion
                || root["worldId"]?.Type != JTokenType.Integer || root.Value<long>("worldId") <= 0
                || root["taiwuId"]?.Type != JTokenType.Integer || root.Value<int>("taiwuId") <= 0
                || root["npcId"]?.Type != JTokenType.Integer || root.Value<int>("npcId") <= 0
                || root["compactedCount"]?.Type != JTokenType.Integer
                || root.Value<int>("compactedCount") <= 0
                || !DurableFileStore.IsBoundedString(root["prefixSha256"], 64, false)
                || (root.Value<string>("prefixSha256")?.Length ?? 0) != 64
                || !DurableFileStore.IsBoundedString(root["summary"],
                    LifeExperienceCompactor.MaxSummaryChars, false)
                || root["updatedUtcTicks"]?.Type != JTokenType.Integer
                || root.Value<long>("updatedUtcTicks") <= 0) return false;
            return true;
        }
    }
}
