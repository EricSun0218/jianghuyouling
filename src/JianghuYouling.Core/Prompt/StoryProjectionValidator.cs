using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using JianghuYouling.Core.Llm;
using JianghuYouling.Core.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.Core.Prompt
{
    /// <summary>A code-owned successful state transition that may be projected to prose.</summary>
    public sealed class StoryProjectionReceipt
    {
        public string Kind { get; set; }
        public string OperationId { get; set; }
        public int ActorId { get; set; }
        public int TargetId { get; set; }
        public string ActorName { get; set; }
        public string TargetName { get; set; }
        public string Asset { get; set; }
        public string Summary { get; set; }
    }

    /// <summary>
    /// Receipt boundary for player-visible monthly projections. Game state is always authoritative:
    /// the model may supply narrative prose only inside a strict envelope. Code-owned receipts remain
    /// authoritative and are validated directly; the model is never required to reproduce internal
    /// operation identifiers merely to make otherwise valid prose persistable. The prose must also
    /// pass an outcome-aware semantic gate; otherwise callers fall back to a deterministic projection.
    /// </summary>
    public static class StoryProjectionValidator
    {
        private const int MaxEnvelopeBytes = 256 * 1024;
        // One action is consumed per agent round; the runtime loop breaker is 64 rounds.
        // Projection capacity must never be smaller than execution capacity or a successful
        // month could disappear only because its durable receipt list no longer fits.
        private const int MaxClaims = 64;
        private const int MaxNameChars = 256;
        private const int MaxAssetChars = 2048;
        private const int MaxSummaryChars = 8192;

        private static readonly HashSet<string> AllowedKinds = new HashSet<string>(StringComparer.Ordinal)
        {
            "kill", "poison", "capture", "steal", "relationship", "enmity", "gift_item",
            "gift_silver", "barter", "teach", "practice", "feature", "movement", "remember",
            "heal", "book", "secret", "equipment", "appearance", "support", "favor", "relationship_end",
            "message", "intimacy", "mood", "fame", "item_use", "book_read"
        };

        // Deliberately strong, state-changing phrases rather than broad words such as “去”“给” or
        // “关系”. This keeps ordinary wuxia scene-setting out of the gate while still catching the
        // concrete actions exposed by monthly tools.
        private static readonly Dictionary<string, string[]> NarrativeTermsByKind =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["kill"] = new[] { "杀死", "杀了", "毙命", "命丧", "取其性命", "取了性命", "死在" },
                ["poison"] = new[] { "下毒", "投毒", "毒发", "毒性发作", "中了毒" },
                ["capture"] = new[] { "擒下", "擒住", "捉住", "被擒", "束手就擒" },
                ["steal"] = new[] { "偷走", "偷得", "偷取", "窃取", "盗走", "顺走" },
                ["relationship"] = new[] { "结为", "结成", "义结金兰", "订立婚约", "定下婚约", "拜为义亲" },
                ["relationship_end"] = new[] { "解除关系", "断绝关系", "割袍断义", "退了婚", "退婚", "和离" },
                ["enmity"] = new[] { "结下仇怨", "结仇", "反目成仇", "化解仇怨", "化解旧怨", "恩怨化解" },
                ["gift_item"] = new[] { "赠物", "赠出", "相赠", "赠给", "送给" },
                ["gift_silver"] = new[] { "赠银", "送银", "银钱交给", "银两交给", "赠给" },
                ["barter"] = new[] { "以物易物", "完成交换", "换得", "换到了" },
                ["teach"] = new[] { "传授", "亲授", "倾囊相授", "教会了" },
                ["practice"] = new[] { "正练改为逆练", "逆练改为正练", "正练改作逆练", "逆练改作正练", "正逆练法已经改变", "改成正练", "改成逆练" },
                ["feature"] = new[] { "获得特性", "多了特性", "添了特性", "长进了" },
                ["movement"] = new[] { "动身前往", "赶赴", "迁往", "迁居", "抵达了", "抵达" },
                ["remember"] = new[] { "写入长期记忆", "记入长期记忆", "牢牢记住本月", "记住了此事" },
                ["heal"] = new[] { "疗伤完成", "治好了伤", "完成疗伤", "医好了" },
                ["book"] = new[] { "写成秘籍", "写成了秘籍", "著成秘籍", "写成一部", "写成一本" },
                ["secret"] = new[] { "告知秘闻", "说出秘闻", "把秘闻告诉", "透露秘闻" },
                ["equipment"] = new[] { "换上装备", "换下装备", "调整装备", "重新装备" },
                ["item_use"] = new[] { "吃下", "服下", "饮下", "使用了", "服用了" },
                ["book_read"] = new[] { "读完", "读罢", "研读完", "看完了" },
                ["appearance"] = new[] { "改变容貌", "改换形貌", "完成易容", "改换装束形貌" },
                ["support"] = new[] { "公开支持", "站出来支持", "在门派中支持" },
                ["favor"] = new[] { "好感上升", "好感下降", "好感增加", "好感降低", "好感变化" },
                ["message"] = new[] { "千里传音", "传信给", "捎信给", "传话给" },
                ["intimacy"] = new[] { "共度一夜", "共赴春宵", "一夜春宵", "同榻而眠" },
                ["mood"] = new[] { "心情好转", "心情低落", "心情变化", "心境转好", "心境转坏" },
                ["fame"] = new[] { "名望上升", "名望下降", "名望变化", "声名大振", "声名受损" },
            };

        private static readonly string[] NegativeOutcomeCues =
        {
            "未能", "没能", "不曾", "并未", "未曾", "未成", "失败", "失手", "落空", "无果",
            "作罢", "罢手", "逃脱", "躲过", "避开", "没做成", "未得手", "没有得手", "未遂"
        };

        private static readonly string[] UnknownOutcomeCues =
        {
            "没有确讯", "尚无确讯", "尚未确认", "未能确认", "不知结果", "无从知晓", "尚无定论",
            "生死未卜", "成败难料", "真假难辨", "不敢断言", "仍是疑云", "下落不明"
        };

        private static readonly string[] HistoricalCues =
        {
            "此前", "先前", "前回", "上月", "早先", "昔日", "旧日", "当年", "曾经", "早已", "往昔"
        };

        private static readonly string[] CurrentOutcomeCues =
        {
            "本月", "当月", "这个月", "此月", "如今", "而今", "眼下", "此刻", "这回", "此番", "随后", "后来"
        };

        private static readonly string[] ClauseConnectors =
        {
            "但是", "然而", "不过", "可是", "随后", "继而", "而后", "然后", "接着", "同时", "并且",
            "但", "却", "并", "又", "再", "而"
        };

        private sealed class NarrativeClause
        {
            public string Text;
            public bool Historical;
        }

        private sealed class ControlledOccurrence
        {
            public int Start;
            public int End;
            public string Text;
            public readonly HashSet<string> Kinds = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> Terms = new HashSet<string>(StringComparer.Ordinal);
        }

        /// <summary>
        /// Rejects agent-process chatter that must never be persisted as player-facing story prose.
        /// This deliberately targets structural labels and unmistakable self-commentary rather than
        /// ordinary narrative vocabulary, so legitimate first-person companion stories still pass.
        /// </summary>
        public static bool ContainsNarrativeProcessMeta(string value)
        {
            string normalized = (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            if (normalized.Length == 0) return false;
            string[] lines = normalized.Split('\n');
            string first = null;
            foreach (string raw in lines)
            {
                string line = (raw ?? string.Empty).Trim();
                if (line.Length == 0) continue;
                if (first == null) first = line;
                if (line.StartsWith("正文如下", StringComparison.Ordinal)
                    || line.StartsWith("故事正文", StringComparison.Ordinal)
                    || line.StartsWith("最终正文", StringComparison.Ordinal)
                    || line.StartsWith("本月行动正文", StringComparison.Ordinal)) return true;
            }

            if (!string.IsNullOrEmpty(first)
                && (first.StartsWith("动因已足", StringComparison.Ordinal)
                    || first.StartsWith("本月该做的事已经", StringComparison.Ordinal)
                    || first.StartsWith("话已问出口，剩下", StringComparison.Ordinal)
                    || first.StartsWith("传音已发，接下来", StringComparison.Ordinal)
                    || IsLeadingMonthlyCompletionPreamble(first))) return true;

            string[] unmistakable =
            {
                "让我直接输出", "现在开始输出正文", "接下来输出正文", "可以收束本回",
                "足够的因果链条", "三角关系已经成型", "不必再多做工具动作",
                "本月行动正文：", "本月行动正文:"
            };
            foreach (string marker in unmistakable)
                if (normalized.IndexOf(marker, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private static bool IsLeadingMonthlyCompletionPreamble(string value)
        {
            string text = (value ?? string.Empty).TrimStart();
            if (text.Length == 0) return false;
            int boundary = text.IndexOfAny(new[] { '。', '！', '？', '\n' });
            string head = (boundary >= 0 ? text.Substring(0, boundary) : text).Trim();
            if (head.Length == 0 || head.Length > 160) return false;

            string[] completionMarkers =
            {
                "已达成", "已经达成", "已完成", "已经完成", "已满足", "已经满足",
                "已覆盖", "已经覆盖", "可以收束", "可收束"
            };
            if (!completionMarkers.Any(marker => head.IndexOf(marker, StringComparison.Ordinal) >= 0))
                return false;

            // Only status language about the Agent's monthly quota is metadata.  A literary line
            // such as “两人终于达成约定” has no quota/process noun and must remain untouched.
            string[] processScopes =
            {
                "本月", "本回", "行动", "行为", "三项", "两项", "两类", "三类",
                "目标", "条件", "门槛", "要求", "因果链", "动因", "前置"
            };
            return processScopes.Any(scope => head.IndexOf(scope, StringComparison.Ordinal) >= 0);
        }

        /// <summary>
        /// Rejects prose that is technically factual but still reads like a game-state receipt.
        /// This is shared by event and companion monthly agents so neither path can persist raw
        /// outcome rows, world-state labels or numeric relationship deltas as player-facing story.
        /// </summary>
        public static bool ContainsMechanicalOutcomeNarration(string value)
        {
            string normalized = (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            if (normalized.Length == 0) return false;
            string[] markers =
            {
                "【当下世道】", "这一着终究落了定", "茶肆里的人还没听全第一句话",
                "执行结果：", "执行结果:", "成功清单", "失败清单", "本月结果中，",
                "项终态成功", "项明确未遂", "typed receipts", "typed receipt",
                "好感变化+", "好感变化-", "名望变化+", "名望变化-",
            };
            foreach (string marker in markers)
                if (normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            foreach (string raw in normalized.Split('\n'))
            {
                string line = (raw ?? string.Empty).TrimStart(' ', '\t', '·', '•', '-', '—');
                if (line.StartsWith("成功:", StringComparison.Ordinal)
                    || line.StartsWith("成功：", StringComparison.Ordinal)
                    || line.StartsWith("失败:", StringComparison.Ordinal)
                    || line.StartsWith("失败：", StringComparison.Ordinal)
                    || line.StartsWith("未知:", StringComparison.Ordinal)
                    || line.StartsWith("未知：", StringComparison.Ordinal)
                    || line.StartsWith("未行动:", StringComparison.Ordinal)
                    || line.StartsWith("未行动：", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>Maps runtime tool names to the closed durable projection vocabulary.</summary>
        public static string KindForTool(string tool)
        {
            string value = (tool ?? string.Empty).Trim().ToLowerInvariant();
            switch (value)
            {
                case "kill": case "event_kill": return "kill";
                case "poison": case "event_poison": return "poison";
                case "capture": case "event_capture": return "capture";
                case "steal": case "event_steal": return "steal";
                case "relate": case "event_relate": case "set_relation": case "matchmake": case "event_matchmake": return "relationship";
                case "dissolve_relation": case "event_dissolve": return "relationship_end";
                case "enmity": case "event_enmity": case "set_enmity": return "enmity";
                case "gift_item": case "event_gift": case "gift": return "gift_item";
                case "gift_silver": case "event_gift_silver": return "gift_silver";
                case "barter": case "event_barter": return "barter";
                case "teach": case "event_teach": return "teach";
                case "heal": case "event_heal": return "heal";
                case "write_book": case "event_write_book": return "book";
                case "tell_secret": case "event_secret": return "secret";
                case "change_equipment": case "event_equipment": return "equipment";
                case "use_item": case "event_use_item": return "item_use";
                case "sect_support": return "support";
                case "adjust_favor": case "event_favor": return "favor";
                case "flip_practice": case "event_flip_practice": case "train_skill": return "practice";
                case "read_book": return "book_read";
                case "add_feature": case "event_feature": return "feature";
                case "goto_place": case "event_goto": return "movement";
                case "remember": return "remember";
                case "send_message": return "message";
                case "spend_night": case "event_spend_night": return "intimacy";
                case "adjust_mood": case "event_mood": return "mood";
                case "adjust_fame": case "event_fame": case "event_taiwu_fame": return "fame";
                default: return null;
            }
        }

        /// <summary>Stable machine-readable manifest used when an optional model echoes claims.</summary>
        public static string RenderReceiptManifest(IList<StoryProjectionReceipt> receipts)
        {
            var array = new JArray();
            if (receipts != null)
                foreach (StoryProjectionReceipt receipt in receipts)
                {
                    if (receipt == null) continue;
                    array.Add(new JObject
                    {
                        ["kind"] = NormalizeKind(receipt.Kind),
                        ["operation_id"] = receipt.OperationId ?? string.Empty,
                        ["actor_id"] = receipt.ActorId,
                        ["target_id"] = receipt.TargetId,
                        ["asset"] = NormalizeText(receipt.Asset),
                    });
                }
            return array.ToString(Formatting.None);
        }

        /// <summary>
        /// Validates code-owned receipt identity and complete coverage of successful outcomes,
        /// then renders the only text allowed into histories, event logs and NPC memories.
        /// </summary>
        public static bool TryBuildDurableProjection(IList<string> outcomes,
            IList<StoryProjectionReceipt> receipts, ISet<int> allowedParticipantIds,
            out string story, out string reason)
        {
            story = null;
            if (!ValidateReceipts(receipts, allowedParticipantIds,
                out Dictionary<string, StoryProjectionReceipt> byOperation, out reason)) return false;
            if (!ValidateOutcomeCoverage(outcomes, receipts, out int failed, out int unknown,
                out int noAction, out reason)) return false;
            story = RenderCodeOwnedStory(receipts, failed, unknown, noAction);
            if (string.IsNullOrWhiteSpace(story))
            {
                reason = "没有可持久化的已确认结果";
                return false;
            }
            reason = null;
            return byOperation.Count == (receipts?.Count ?? 0);
        }

        /// <summary>
        /// Binds a persisted story receipt to the executor-owned outcome fields. Dispatch/model
        /// JSON is deliberately absent from this API: callers must pass identities and names
        /// captured from the authoritative roster plus a persisted backend-receipt decision.
        /// </summary>
        public static bool ReceiptMatchesAuthoritativeOutcome(string tool, string operationId,
            int actorId, int targetId, string actorName, string targetName, string expectedAsset, string summary,
            bool succeeded, bool backendReceiptStored, StoryProjectionReceipt receipt)
        {
            if (!succeeded || !backendReceiptStored || receipt == null) return false;
            string kind = KindForTool(tool);
            if (string.IsNullOrWhiteSpace(kind)) return false;
            bool selfTarget = kind == "feature"
                || string.Equals(tool, "event_taiwu_fame", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tool, "event_equipment", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tool, "event_use_item", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tool, "event_flip_practice", StringComparison.OrdinalIgnoreCase);
            int expectedTarget = kind == "movement" ? 0 : selfTarget ? actorId : targetId;
            string expectedTargetName = expectedTarget <= 0 ? string.Empty
                : expectedTarget == actorId ? actorName : targetName;
            return OperationId.IsValid(operationId)
                && string.Equals(NormalizeKind(receipt.Kind), kind, StringComparison.Ordinal)
                && string.Equals(receipt.OperationId, operationId, StringComparison.Ordinal)
                && receipt.ActorId == actorId && receipt.TargetId == expectedTarget
                && string.Equals(NormalizeText(receipt.ActorName), NormalizeText(actorName), StringComparison.Ordinal)
                && string.Equals(NormalizeText(receipt.TargetName), NormalizeText(expectedTargetName), StringComparison.Ordinal)
                && string.Equals(NormalizeText(receipt.Asset), NormalizeText(expectedAsset), StringComparison.Ordinal)
                && string.Equals(NormalizeText(receipt.Summary), NormalizeText(summary), StringComparison.Ordinal)
                && NormalizeText(receipt.ActorName).Length > 0
                && NormalizeText(receipt.ActorName).Length <= MaxNameChars
                && (expectedTarget <= 0 || NormalizeText(receipt.TargetName).Length > 0)
                && NormalizeText(receipt.TargetName).Length <= MaxNameChars
                && (!KindRequiresAsset(kind) || NormalizeText(receipt.Asset).Length > 0)
                && NormalizeText(receipt.Asset).Length <= MaxAssetChars
                && NormalizeText(receipt.Summary).Length > 0
                && NormalizeText(receipt.Summary).Length <= MaxSummaryChars;
        }

        /// <summary>
        /// Code-owned fail-closed text for legacy/corrupt evidence that cannot satisfy typed
        /// identity coverage. It intentionally exposes no actor, target, asset or claimed state.
        /// </summary>
        public static string BuildNonFactualFallback(IList<string> outcomes)
        {
            int succeeded = 0, failed = 0, unknown = 0, noAction = 0, invalid = 0;
            if (outcomes != null)
                foreach (string raw in outcomes)
                {
                    string value = NormalizeText(raw);
                    if (value.StartsWith("成功:", StringComparison.Ordinal)) succeeded++;
                    else if (value.StartsWith("失败:", StringComparison.Ordinal)) failed++;
                    else if (value.StartsWith("未知:", StringComparison.Ordinal)) unknown++;
                    else if (value.StartsWith("未行动:", StringComparison.Ordinal)) noAction++;
                    else invalid++;
                }
            bool anyTrace = succeeded + failed + unknown + noAction + invalid > 0;
            return anyTrace
                ? "这个月有几桩传闻在街巷间来回辗转，可留下的线索彼此对不上，谁也说不清究竟牵涉了哪些人。\n\n说书人把惊堂木轻轻一搁，没有替含混的风声添上姓名和结局。待日后有了确讯，这段疑云再续不迟。"
                : "这个月的江湖风平浪静，没有留下足以写进纪事的确切波澜。\n\n檐下风铃响过几声，来往客人仍各走各的路，未曾被传闻改了方向。";
        }

        /// <summary>
        /// Validates a strict {story,claims} response. Every successful receipt must have one exact
        /// claim. Exact claims are necessary but not sufficient: the story must cover each successful
        /// receipt and may not turn a failed/unknown/unexecuted controlled action into a success or add
        /// a new controlled state transition. A rejected draft is never persisted; callers use the
        /// deterministic receipt projection as the rich fail-closed fallback.
        /// </summary>
        public static bool TryParseAndValidate(string response, IList<string> outcomes,
            IList<StoryProjectionReceipt> receipts, int minChars, int maxChars,
            ISet<int> allowedParticipantIds, out string story, out string reason)
        {
            story = null;
            if (!ValidateReceipts(receipts, allowedParticipantIds,
                out Dictionary<string, StoryProjectionReceipt> byOperation, out reason)) return false;
            if (!ValidateOutcomeCoverage(outcomes, receipts, out int failed, out int unknown,
                out int noAction, out reason)) return false;
            if (!LlmJsonProtocol.TryParseObject((response ?? string.Empty).Trim(), MaxEnvelopeBytes,
                out JObject root, out reason)) return false;
            if (!HasOnlyFields(root, "story", "claims")
                || !(root["story"] is JValue storyValue) || storyValue.Type != JTokenType.String)
            {
                reason = "故事 envelope 必须只含字符串 story 与 claims";
                return false;
            }
            if (!(root["claims"] is JArray claims) || claims.Count > MaxClaims
                || claims.Count != byOperation.Count)
            {
                reason = "故事 claims 数量未逐条覆盖 typed receipts";
                return false;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JToken token in claims)
            {
                if (!(token is JObject claim) || !HasOnlyFields(claim,
                    "kind", "operation_id", "actor_id", "target_id", "asset")
                    || !TryRequiredString(claim, "kind", out string kind)
                    || !TryRequiredString(claim, "operation_id", out string operationId)
                    || !TryRequiredInteger(claim, "actor_id", out int actorId)
                    || !TryRequiredInteger(claim, "target_id", out int targetId)
                    || !TryRequiredString(claim, "asset", out string asset)
                    || !seen.Add(operationId)
                    || !byOperation.TryGetValue(operationId, out StoryProjectionReceipt receipt)
                    || !ClaimExactlyMatches(kind, operationId, actorId, targetId, asset, receipt))
                {
                    reason = "故事 claim 与权威 kind+actorId+targetId+asset+operationId 不精确一致";
                    return false;
                }
            }

            string narrative = ((string)storyValue ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(narrative)
                || minChars > 0 && narrative.Length < minChars
                || maxChars > 0 && narrative.Length > maxChars
                || Encoding.UTF8.GetByteCount(narrative) > MaxEnvelopeBytes)
            {
                reason = "故事正文为空或长度越界";
                return false;
            }
            if (!ValidateNarrativeConsistency(narrative, outcomes, receipts, out reason)) return false;
            // JHYL_DURABLE_STORY_OUTCOME_GATE: the complete model story remains the final prose only
            // after exact claims plus the outcome-aware narrative gate both pass. Rejected prose is
            // never partially salvaged; the caller falls back to RenderCodeOwnedStory.
            story = narrative;
            reason = null;
            return true;
        }

        /// <summary>Legacy prose-only projection cannot establish entity-bound authority.</summary>
        public static bool Validate(string story, IList<string> outcomes, int minChars, int maxChars,
            ISet<int> allowedParticipantIds, out string reason)
        {
            reason = "任意自然语言不能证明事实边界；必须使用代码持有的 typed receipts";
            return false;
        }

        private static bool ValidateReceipts(IList<StoryProjectionReceipt> receipts,
            ISet<int> allowedParticipantIds, out Dictionary<string, StoryProjectionReceipt> byOperation,
            out string reason)
        {
            reason = null;
            byOperation = new Dictionary<string, StoryProjectionReceipt>(StringComparer.Ordinal);
            if (receipts == null || receipts.Count > MaxClaims)
            {
                reason = "typed receipts 缺失或超过上限";
                return false;
            }
            foreach (StoryProjectionReceipt receipt in receipts)
            {
                string kind = NormalizeKind(receipt?.Kind);
                string actorName = NormalizeText(receipt?.ActorName);
                string targetName = NormalizeText(receipt?.TargetName);
                string asset = NormalizeText(receipt?.Asset);
                string summary = NormalizeText(receipt?.Summary);
                if (receipt == null || !AllowedKinds.Contains(kind)
                    || !OperationId.IsValid(receipt.OperationId) || receipt.ActorId <= 0
                    || receipt.TargetId < 0 || byOperation.ContainsKey(receipt.OperationId)
                    || actorName.Length == 0 || actorName.Length > MaxNameChars
                    || targetName.Length > MaxNameChars || asset.Length > MaxAssetChars
                    || KindRequiresAsset(kind) && asset.Length == 0
                    || summary.Length == 0 || summary.Length > MaxSummaryChars
                    || receipt.TargetId > 0 && targetName.Length == 0)
                {
                    reason = "typed receipt 身份、种类、名称、摘要或 operation_id 无效";
                    return false;
                }
                if (allowedParticipantIds == null || !allowedParticipantIds.Contains(receipt.ActorId)
                    || receipt.TargetId > 0 && !allowedParticipantIds.Contains(receipt.TargetId))
                {
                    reason = "typed receipt 引用名单外实体";
                    return false;
                }
                byOperation.Add(receipt.OperationId, receipt);
            }
            return true;
        }

        private static bool ValidateOutcomeCoverage(IList<string> outcomes,
            IList<StoryProjectionReceipt> receipts, out int failed, out int unknown,
            out int noAction, out string reason)
        {
            failed = unknown = noAction = 0;
            reason = null;
            if (outcomes == null || outcomes.Count == 0)
            {
                reason = "没有权威结果可供投影";
                return false;
            }
            var successes = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string raw in outcomes)
            {
                string outcome = NormalizeText(raw);
                if (outcome.StartsWith("成功:", StringComparison.Ordinal))
                {
                    if (!successes.ContainsKey(outcome)) successes[outcome] = 0;
                    successes[outcome]++;
                }
                else if (outcome.StartsWith("失败:", StringComparison.Ordinal)) failed++;
                else if (outcome.StartsWith("未知:", StringComparison.Ordinal)) unknown++;
                else if (outcome.StartsWith("未行动:", StringComparison.Ordinal)) noAction++;
                else
                {
                    reason = "存在无法分类的权威结果";
                    return false;
                }
            }
            foreach (StoryProjectionReceipt receipt in receipts)
            {
                string summary = NormalizeText(receipt.Summary);
                string outcome = summary.StartsWith("成功:", StringComparison.Ordinal)
                    ? summary : "成功:" + summary;
                if (!successes.TryGetValue(outcome, out int count) || count <= 0)
                {
                    reason = "typed receipt 未绑定到一个精确成功结果";
                    return false;
                }
                if (count == 1) successes.Remove(outcome); else successes[outcome] = count - 1;
            }
            if (successes.Count > 0)
            {
                reason = "存在没有 typed receipt 的成功结果，拒绝持久化";
                return false;
            }
            return receipts.Count > 0 || failed + unknown + noAction > 0;
        }

        private static bool ValidateNarrativeConsistency(string narrative, IList<string> outcomes,
            IList<StoryProjectionReceipt> receipts, out string reason,
            bool requireEverySuccessfulReceipt = true)
        {
            reason = null;
            if (ContainsNarrativeProcessMeta(narrative))
            {
                reason = "故事仍含 Agent 过程话术或成稿说明";
                return false;
            }
            if (ContainsMechanicalOutcomeNarration(narrative))
            {
                reason = "故事仍在照抄世界状态、执行结果或数值变化";
                return false;
            }

            int soleActorId = 0;
            bool oneActor = true;
            if (receipts != null)
                foreach (StoryProjectionReceipt receipt in receipts)
                {
                    if (receipt == null) continue;
                    if (soleActorId == 0) soleActorId = receipt.ActorId;
                    else if (soleActorId != receipt.ActorId) oneActor = false;
                }
            bool allowFirstPersonActor = oneActor && soleActorId > 0;

            var failedKinds = new HashSet<string>(StringComparer.Ordinal);
            var unknownKinds = new HashSet<string>(StringComparer.Ordinal);
            var noActionKinds = new HashSet<string>(StringComparer.Ordinal);
            if (outcomes != null)
                foreach (string raw in outcomes)
                {
                    string outcome = NormalizeText(raw);
                    HashSet<string> destination = outcome.StartsWith("失败:", StringComparison.Ordinal) ? failedKinds
                        : outcome.StartsWith("未知:", StringComparison.Ordinal) ? unknownKinds
                        : outcome.StartsWith("未行动:", StringComparison.Ordinal) ? noActionKinds : null;
                    if (destination != null) AddKindsMentioned(outcome, destination);
                }

            var coveredOperations = new HashSet<string>(StringComparer.Ordinal);
            List<NarrativeClause> clauses = SplitNarrativeClauses(narrative);
            string inheritedActor = null;
            string inheritedOperationId = null;
            bool inheritedHistorical = false;
            for (int clauseIndex = 0; clauseIndex < clauses.Count; clauseIndex++)
            {
                NarrativeClause clause = clauses[clauseIndex];
                if (clause.Historical != inheritedHistorical)
                {
                    inheritedActor = null;
                    inheritedOperationId = null;
                    inheritedHistorical = clause.Historical;
                }
                List<ControlledOccurrence> occurrences = FindControlledOccurrences(clause.Text);
                for (int occurrenceIndex = 0; occurrenceIndex < occurrences.Count; occurrenceIndex++)
                {
                    ControlledOccurrence occurrence = occurrences[occurrenceIndex];
                    if (clause.Historical)
                    {
                        reason = "正文写入了没有受信历史回执的受控行动：" + JoinKinds(occurrence.Kinds);
                        return false;
                    }
                    string outcomeContext = BuildOutcomeContext(clauses, clauseIndex, occurrence.Text);
                    if (ContainsAny(outcomeContext, UnknownOutcomeCues))
                    {
                        if (AnyKindRecorded(unknownKinds, occurrence.Kinds)) continue;
                        reason = "正文写入了没有权威未知回执的受控行动：" + JoinKinds(occurrence.Kinds);
                        return false;
                    }
                    if (ContainsAny(outcomeContext, NegativeOutcomeCues))
                    {
                        if (AnyKindRecorded(failedKinds, occurrence.Kinds)
                            || AnyKindRecorded(noActionKinds, occurrence.Kinds)) continue;
                        reason = "正文写入了没有权威失败/未行动回执的受控行动：" + JoinKinds(occurrence.Kinds);
                        return false;
                    }

                    string context = BuildActionContext(clauses, clauseIndex);
                    StoryProjectionReceipt matched = FindMatchingReceipt(occurrence, context,
                        receipts, coveredOperations, allowFirstPersonActor, inheritedActor,
                        inheritedOperationId);
                    if (matched == null)
                    {
                        reason = "正文中的受控行动子句没有绑定 actor/target/kind/asset 完全匹配的成功 receipt："
                            + JoinKinds(occurrence.Kinds);
                        return false;
                    }
                    coveredOperations.Add(matched.OperationId);
                    inheritedActor = NormalizeText(matched.ActorName);
                    inheritedOperationId = matched.OperationId;
                }
            }

            if (requireEverySuccessfulReceipt && receipts != null)
                foreach (StoryProjectionReceipt receipt in receipts)
                    if (receipt != null && !coveredOperations.Contains(receipt.OperationId))
                    {
                        reason = "故事没有在同一受控行动子句中覆盖成功 receipt：" + receipt.OperationId;
                        return false;
                    }
            return true;
        }

        /// <summary>
        /// Parses player-facing monthly prose while keeping executor receipts authoritative.
        /// Unlike the strict durable projection, prose may naturally omit some completed actions;
        /// every controlled action it does claim must still bind to an exact successful receipt.
        /// This prevents invented kills, transfers or relationship changes without turning the
        /// visible story into a mechanical transaction report.
        /// </summary>
        public static bool TryParsePlayerFacingNarrativeAgainstAuthoritativeReceipts(
            string response, IList<string> outcomes, IList<StoryProjectionReceipt> receipts,
            int maxChars, ISet<int> allowedParticipantIds, out string story, out string reason)
        {
            story = null;
            if (!ValidateReceipts(receipts, allowedParticipantIds, out _, out reason)) return false;
            if (!ValidateOutcomeCoverage(outcomes, receipts, out _, out _, out _, out reason))
                return false;
            if (!TryParsePlayerFacingNarrative(response, maxChars, out string narrative,
                out reason)) return false;
            if (!ValidateNarrativeConsistency(narrative, outcomes, receipts, out reason,
                requireEverySuccessfulReceipt: false)) return false;
            story = narrative;
            reason = null;
            return true;
        }

        /// <summary>
        /// Validates a strict {story} response against receipts already owned by the executor.
        /// This is the preferred monthly-agent boundary: exact receipt identity and successful-outcome
        /// coverage are checked in code, while the model is responsible only for faithful prose.
        /// Requiring an LLM to echo operation ids added no authority and caused valid long-form stories
        /// to be discarded for harmless JSON copying mistakes.
        /// </summary>
        public static bool TryParseStoryAgainstAuthoritativeReceipts(string response,
            IList<string> outcomes, IList<StoryProjectionReceipt> receipts, int minChars, int maxChars,
            ISet<int> allowedParticipantIds, out string story, out string reason)
        {
            story = null;
            if (!ValidateReceipts(receipts, allowedParticipantIds,
                out Dictionary<string, StoryProjectionReceipt> byOperation, out reason)) return false;
            if (!ValidateOutcomeCoverage(outcomes, receipts, out _, out _, out _, out reason)) return false;
            if (byOperation.Count != (receipts?.Count ?? 0))
            {
                reason = "权威回执存在重复 operation id";
                return false;
            }
            string trimmedResponse = (response ?? string.Empty).Trim();
            if (!LlmJsonProtocol.TryParseObject(trimmedResponse, MaxEnvelopeBytes,
                out JObject root, out reason))
            {
                // 部分强模型在“最终只写正文”阶段会忠实输出完整小说正文，却省掉 {story} 外壳。
                // 外壳本身不提供事实权威；真正的信任边界仍是下方 executor-owned receipts、
                // 参与者集合、长度和逐句动作一致性校验。仅兼容明确的纯文本，任何像坏 JSON、
                // tool/receipt 协议或内部字段的内容仍 fail-closed，避免把结构错误伪装成小说。
                if (!TryWrapPlainStory(trimmedResponse, out root)) return false;
                reason = null;
            }
            if (!HasOnlyFields(root, "story")
                || !(root["story"] is JValue storyValue) || storyValue.Type != JTokenType.String)
            {
                reason = "故事 envelope 必须只含字符串 story";
                return false;
            }

            string narrative = ((string)storyValue ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(narrative)
                || minChars > 0 && narrative.Length < minChars
                || maxChars > 0 && narrative.Length > maxChars
                || Encoding.UTF8.GetByteCount(narrative) > MaxEnvelopeBytes)
            {
                reason = "故事正文为空或长度越界";
                return false;
            }
            if (!ValidateNarrativeConsistency(narrative, outcomes, receipts, out reason)) return false;
            story = narrative;
            reason = null;
            return true;
        }

        /// <summary>
        /// Extracts player-facing prose with the same lightweight trust boundary used by normal
        /// conversations.  Monthly agents keep executor-owned outcomes and receipts in their own
        /// journals; the prose is presentation, not a second transaction log and therefore does
        /// not have to prove, enumerate or phrase-match every tool result.
        /// </summary>
        public static bool TryParsePlayerFacingNarrative(string response, int maxChars,
            out string story, out string reason)
        {
            story = null;
            reason = null;
            string trimmed = (response ?? string.Empty).Trim();
            string narrative = null;
            if (LlmJsonProtocol.TryParseObject(trimmed, MaxEnvelopeBytes, out JObject root, out _))
            {
                if (!HasOnlyFields(root, "story")
                    || !(root["story"] is JValue storyValue) || storyValue.Type != JTokenType.String)
                {
                    reason = "正文对象只能包含字符串 story";
                    return false;
                }
                narrative = ((string)storyValue ?? string.Empty).Trim();
            }
            else if (!TryParseLenientSingleStoryEnvelope(trimmed, out narrative))
            {
                if (!TryWrapPlainStory(trimmed, out root))
                {
                    reason = "正文既不是可读纯文本，也不是只含 story 的对象";
                    return false;
                }
                narrative = ((string)root["story"] ?? string.Empty).Trim();
            }

            if (string.IsNullOrWhiteSpace(narrative) || !ContainsMeaningfulText(narrative))
            {
                reason = "正文为空或只有标点";
                return false;
            }
            if (ContainsNarrativeProcessMeta(narrative)
                && !TryRemoveLeadingNarrativeProcessMeta(narrative, out narrative))
            {
                reason = "正文含明显的 Agent 过程说明";
                return false;
            }
            // This is an extreme-size fuse, not a stylistic word-count requirement.  Check after
            // removing a recognised leading status sentence so harmless process chatter cannot
            // make an otherwise valid completed story exceed the presentation fuse.
            if (maxChars > 0 && narrative.Length > maxChars
                || Encoding.UTF8.GetByteCount(narrative) > MaxEnvelopeBytes)
            {
                reason = "正文超过极端安全上限";
                return false;
            }
            if (ContainsMechanicalOutcomeNarration(narrative) || ContainsInternalProtocol(narrative))
            {
                reason = "正文含工具、回执或系统技术内容";
                return false;
            }
            story = narrative;
            return true;
        }

        /// <summary>
        /// Some strong reasoning models prepend one unmistakable planning sentence and then
        /// immediately continue with otherwise complete prose.  The planning sentence is not a
        /// reason to buy another full completion: remove only a leading, explicitly recognised
        /// preamble and keep the model's story byte-for-byte after that boundary.  Process chatter
        /// in the middle of prose, protocol text, and a response containing no real story still
        /// fail the normal trust boundary.
        /// </summary>
        private static bool TryRemoveLeadingNarrativeProcessMeta(string value, out string cleaned)
        {
            cleaned = null;
            string text = (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            if (text.Length == 0) return false;

            // Harmless literary scene separators are presentation, not Agent metadata.  A leading
            // separator can be discarded here; internal separators are retained by the parser.
            while (text.StartsWith("---", StringComparison.Ordinal)
                || text.StartsWith("———", StringComparison.Ordinal)
                || text.StartsWith("——", StringComparison.Ordinal))
            {
                int separatorLength = text.StartsWith("---", StringComparison.Ordinal) ? 3
                    : text.StartsWith("———", StringComparison.Ordinal) ? 3 : 2;
                text = text.Substring(separatorLength).TrimStart();
            }

            string[] colonPreambles =
            {
                "正文如下", "故事正文", "最终正文", "本月行动正文"
            };
            foreach (string marker in colonPreambles)
            {
                if (!text.StartsWith(marker, StringComparison.Ordinal)) continue;
                int colon = text.IndexOfAny(new[] { '：', ':' }, marker.Length);
                if (colon < 0) return false;
                text = text.Substring(colon + 1).TrimStart();
                if (!ContainsMeaningfulText(text) || ContainsNarrativeProcessMeta(text)) return false;
                cleaned = text;
                return true;
            }

            string[] sentencePreambles =
            {
                "动因已足", "本月该做的事已经", "话已问出口，剩下", "传音已发，接下来",
                "让我直接输出", "现在开始输出正文", "接下来输出正文", "可以收束本回",
                "足够的因果链条", "三角关系已经成型", "不必再多做工具动作"
            };
            bool recognised = false;
            foreach (string marker in sentencePreambles)
                if (text.StartsWith(marker, StringComparison.Ordinal)) { recognised = true; break; }
            if (!recognised) recognised = IsLeadingMonthlyCompletionPreamble(text);
            if (!recognised) return false;

            int boundary = text.IndexOfAny(new[] { '。', '！', '？', '\n' });
            if (boundary < 0 || boundary + 1 >= text.Length) return false;
            string candidate = text.Substring(boundary + 1).TrimStart();
            if (!ContainsMeaningfulText(candidate) || ContainsNarrativeProcessMeta(candidate)) return false;
            cleaned = candidate;
            return true;
        }

        /// <summary>
        /// Some compatible models emit a visually correct one-field story object but put literal
        /// line breaks inside its JSON string. That is invalid JSON, yet losing a completed novel
        /// and falling back to a template is worse. This scanner is intentionally available only
        /// to player-facing prose: it accepts exactly one string field named story (optionally in
        /// one final code fence) and still rejects extra fields or appended protocol text.
        /// Tool arguments, receipts and persisted state continue to use strict JSON parsers.
        /// </summary>
        private static bool TryParseLenientSingleStoryEnvelope(string response, out string story)
        {
            story = null;
            string value = (response ?? string.Empty).Trim();
            if (value.StartsWith("```", StringComparison.Ordinal))
            {
                int firstLine = value.IndexOf('\n');
                int closing = value.LastIndexOf("```", StringComparison.Ordinal);
                if (firstLine <= 0 || closing <= firstLine || closing != value.Length - 3) return false;
                value = value.Substring(firstLine + 1, closing - firstLine - 1).Trim();
            }
            if (value.Length == 0 || Encoding.UTF8.GetByteCount(value) > MaxEnvelopeBytes) return false;

            int i = 0;
            SkipWhitespace(value, ref i);
            if (!Consume(value, ref i, '{')) return false;
            SkipWhitespace(value, ref i);
            if (!ConsumeLiteral(value, ref i, "\"story\"")) return false;
            SkipWhitespace(value, ref i);
            if (!Consume(value, ref i, ':')) return false;
            SkipWhitespace(value, ref i);
            if (!Consume(value, ref i, '\"')) return false;

            var decoded = new StringBuilder(value.Length);
            bool closed = false;
            while (i < value.Length)
            {
                char c = value[i++];
                if (c == '\"') { closed = true; break; }
                if (c != '\\')
                {
                    // Raw CR/LF/TAB are the provider defect this compatibility path repairs.
                    // Other unescaped control characters remain invalid.
                    if (c < 0x20 && c != '\r' && c != '\n' && c != '\t') return false;
                    decoded.Append(c);
                    continue;
                }
                if (i >= value.Length) return false;
                char escaped = value[i++];
                switch (escaped)
                {
                    case '\"': decoded.Append('\"'); break;
                    case '\\': decoded.Append('\\'); break;
                    case '/': decoded.Append('/'); break;
                    case 'b': decoded.Append('\b'); break;
                    case 'f': decoded.Append('\f'); break;
                    case 'n': decoded.Append('\n'); break;
                    case 'r': decoded.Append('\r'); break;
                    case 't': decoded.Append('\t'); break;
                    case 'u':
                        if (i + 4 > value.Length) return false;
                        if (!ushort.TryParse(value.Substring(i, 4), NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture, out ushort code)) return false;
                        decoded.Append((char)code);
                        i += 4;
                        break;
                    default: return false;
                }
            }
            if (!closed) return false;
            SkipWhitespace(value, ref i);
            // A sole trailing comma is a common harmless model formatting slip. It cannot
            // authorize another field because the next required token is the final brace.
            if (i < value.Length && value[i] == ',') { i++; SkipWhitespace(value, ref i); }
            if (!Consume(value, ref i, '}')) return false;
            SkipWhitespace(value, ref i);
            if (i != value.Length) return false;
            story = decoded.ToString().Trim();
            return true;
        }

        private static void SkipWhitespace(string value, ref int index)
        {
            while (index < value.Length && char.IsWhiteSpace(value[index])) index++;
        }

        private static bool Consume(string value, ref int index, char expected)
        {
            if (index >= value.Length || value[index] != expected) return false;
            index++;
            return true;
        }

        private static bool ConsumeLiteral(string value, ref int index, string expected)
        {
            if (index < 0 || expected == null || index + expected.Length > value.Length
                || string.CompareOrdinal(value, index, expected, 0, expected.Length) != 0) return false;
            index += expected.Length;
            return true;
        }

        private static bool ContainsMeaningfulText(string value)
        {
            int meaningful = 0;
            foreach (char c in value ?? string.Empty)
            {
                if (char.IsLetterOrDigit(c) || c >= 0x3400 && c <= 0x9fff) meaningful++;
                if (meaningful >= 6) return true;
            }
            return false;
        }

        private static bool ContainsInternalProtocol(string value)
        {
            string lower = (value ?? string.Empty).ToLowerInvariant();
            string[] cues =
            {
                "jhyl_", "tool_calls", "tool_call", "tool_result", "operation_id",
                "typed receipt", "record_reaction", "reasoning_content", "thought_signature",
                "调用工具", "工具调用", "工具执行", "工具回执", "系统回执", "权威回执",
                "授权的工具", "不在授权", "系统提示", "思考过程", "作为ai", "作为 ai",
                "语言模型", "api", "llm"
            };
            foreach (string cue in cues)
                if (lower.IndexOf(cue, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static bool TryWrapPlainStory(string response, out JObject root)
        {
            root = null;
            string value = (response ?? string.Empty).Trim();
            if (value.StartsWith("```", StringComparison.Ordinal))
            {
                int firstLine = value.IndexOf('\n');
                int closing = value.LastIndexOf("```", StringComparison.Ordinal);
                // A fenced story is accepted only when the closing fence is the final token.
                // Never discard protocol/debug text appended after the fence.
                if (firstLine <= 0 || closing <= firstLine || closing != value.Length - 3) return false;
                value = value.Substring(firstLine + 1, closing - firstLine - 1).Trim();
            }
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("{", StringComparison.Ordinal)
                || value.StartsWith("[", StringComparison.Ordinal)
                || value.IndexOf("```", StringComparison.Ordinal) >= 0
                || Encoding.UTF8.GetByteCount(value) > MaxEnvelopeBytes) return false;
            string lowered = value.ToLowerInvariant();
            if (lowered.IndexOf("tool_call", StringComparison.Ordinal) >= 0
                || lowered.IndexOf("operation_id", StringComparison.Ordinal) >= 0
                || lowered.IndexOf("receipt", StringComparison.Ordinal) >= 0
                || lowered.IndexOf("\"claims\"", StringComparison.Ordinal) >= 0
                || lowered.IndexOf("\"story\"", StringComparison.Ordinal) >= 0
                || value.IndexOf("工具调用", StringComparison.Ordinal) >= 0
                || value.IndexOf("调用工具", StringComparison.Ordinal) >= 0
                || value.IndexOf("操作编号", StringComparison.Ordinal) >= 0
                || value.IndexOf("操作ID", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("权威回执", StringComparison.Ordinal) >= 0
                || value.IndexOf("回执编号", StringComparison.Ordinal) >= 0) return false;
            root = new JObject { ["story"] = value };
            return true;
        }

        private static StoryProjectionReceipt FindMatchingReceipt(ControlledOccurrence occurrence,
            string context, IList<StoryProjectionReceipt> receipts, ISet<string> coveredOperations,
            bool allowFirstPersonActor, string inheritedActor, string inheritedOperationId)
        {
            StoryProjectionReceipt alreadyCovered = null;
            if (occurrence == null || receipts == null) return null;
            foreach (StoryProjectionReceipt receipt in receipts)
            {
                if (!OccurrenceMatchesReceipt(occurrence, context, receipt,
                    allowFirstPersonActor, inheritedActor, inheritedOperationId)) continue;
                if (coveredOperations == null || !coveredOperations.Contains(receipt.OperationId)) return receipt;
                if (alreadyCovered == null) alreadyCovered = receipt;
            }
            return alreadyCovered;
        }

        private static bool OccurrenceMatchesReceipt(ControlledOccurrence occurrence, string context,
            StoryProjectionReceipt receipt, bool allowFirstPersonActor, string inheritedActor,
            string inheritedOperationId)
        {
            if (occurrence == null || receipt == null) return false;
            string kind = NormalizeKind(receipt.Kind);
            if (!OccurrenceCanMeanKind(occurrence, kind)) return false;

            string actor = NormalizeText(receipt.ActorName);
            string target = NormalizeText(receipt.TargetName);
            string local = occurrence.Text ?? string.Empty;
            string window = context ?? local;
            bool actorLocal = actor.Length > 0 && local.IndexOf(actor, StringComparison.Ordinal) >= 0;
            bool actorFirstPerson = allowFirstPersonActor && local.IndexOf("我", StringComparison.Ordinal) >= 0;
            bool targetLocal = receipt.TargetId <= 0 || receipt.TargetId == receipt.ActorId
                ? actorLocal || actorFirstPerson
                : target.Length > 0 && local.IndexOf(target, StringComparison.Ordinal) >= 0;
            bool actorInWindow = actor.Length > 0 && window.IndexOf(actor, StringComparison.Ordinal) >= 0;
            bool targetInWindow = receipt.TargetId <= 0 || receipt.TargetId == receipt.ActorId
                ? actorInWindow || actorFirstPerson
                : target.Length > 0 && window.IndexOf(target, StringComparison.Ordinal) >= 0;
            bool resultContinuation = IsResultContinuation(occurrence)
                && actor.Length > 0
                && string.Equals(actor, NormalizeText(inheritedActor), StringComparison.Ordinal)
                && string.Equals(receipt.OperationId, inheritedOperationId, StringComparison.Ordinal);
            if (!(actorLocal || actorFirstPerson || targetLocal || resultContinuation)
                || !actorInWindow && !actorFirstPerson
                || !(targetLocal || resultContinuation && targetInWindow)) return false;

            if (!actorLocal && !actorFirstPerson)
            {
                bool passiveResult = IsActorElidingResult(occurrence);
                bool inherited = actor.Length > 0
                    && string.Equals(actor, NormalizeText(inheritedActor), StringComparison.Ordinal)
                    && PrefixAllowsInheritedSubject(occurrence, receipt);
                if (!passiveResult && !inherited) return false;
            }

            if (AssetNeedsNarrativeCoverage(kind)
                && !NarrativeCoversAsset(resultContinuation ? window : local, receipt.Asset, kind)) return false;
            return true;
        }

        private static void AddKindsMentioned(string text, ISet<string> destination)
        {
            if (destination == null) return;
            foreach (KeyValuePair<string, string[]> rule in NarrativeTermsByKind)
                if (ContainsAny(text, rule.Value)) destination.Add(rule.Key);
            // Executor summaries often use compact nouns rather than literary result phrases.
            if (text.IndexOf("杀人", StringComparison.Ordinal) >= 0) destination.Add("kill");
            if (text.IndexOf("投毒", StringComparison.Ordinal) >= 0 || text.IndexOf("毒", StringComparison.Ordinal) >= 0) destination.Add("poison");
            if (text.IndexOf("擒拿", StringComparison.Ordinal) >= 0) destination.Add("capture");
            if (text.IndexOf("偷窃", StringComparison.Ordinal) >= 0 || text.IndexOf("偷取", StringComparison.Ordinal) >= 0) destination.Add("steal");
            if (text.IndexOf("结缘", StringComparison.Ordinal) >= 0 || text.IndexOf("关系", StringComparison.Ordinal) >= 0) destination.Add("relationship");
            if (text.IndexOf("解除", StringComparison.Ordinal) >= 0 && text.IndexOf("关系", StringComparison.Ordinal) >= 0) destination.Add("relationship_end");
            if (text.IndexOf("仇", StringComparison.Ordinal) >= 0) destination.Add("enmity");
            if (text.IndexOf("赠物", StringComparison.Ordinal) >= 0) destination.Add("gift_item");
            if (text.IndexOf("银", StringComparison.Ordinal) >= 0 && text.IndexOf("赠", StringComparison.Ordinal) >= 0) destination.Add("gift_silver");
            if (text.IndexOf("交换", StringComparison.Ordinal) >= 0) destination.Add("barter");
            if (text.IndexOf("传功", StringComparison.Ordinal) >= 0 || text.IndexOf("传授", StringComparison.Ordinal) >= 0) destination.Add("teach");
            if (text.IndexOf("正逆练", StringComparison.Ordinal) >= 0) destination.Add("practice");
            if (text.IndexOf("特性", StringComparison.Ordinal) >= 0) destination.Add("feature");
            if (text.IndexOf("移动", StringComparison.Ordinal) >= 0 || text.IndexOf("前往", StringComparison.Ordinal) >= 0) destination.Add("movement");
            if (text.IndexOf("疗伤", StringComparison.Ordinal) >= 0) destination.Add("heal");
            if (text.IndexOf("写书", StringComparison.Ordinal) >= 0 || text.IndexOf("秘籍", StringComparison.Ordinal) >= 0) destination.Add("book");
            if (text.IndexOf("读书", StringComparison.Ordinal) >= 0 || text.IndexOf("读完", StringComparison.Ordinal) >= 0) destination.Add("book_read");
            if (text.IndexOf("秘闻", StringComparison.Ordinal) >= 0) destination.Add("secret");
            if (text.IndexOf("装备", StringComparison.Ordinal) >= 0) destination.Add("equipment");
            if (text.IndexOf("外貌", StringComparison.Ordinal) >= 0 || text.IndexOf("容貌", StringComparison.Ordinal) >= 0) destination.Add("appearance");
            if (text.IndexOf("门派支持", StringComparison.Ordinal) >= 0) destination.Add("support");
            if (text.IndexOf("好感", StringComparison.Ordinal) >= 0) destination.Add("favor");
            if (text.IndexOf("传音", StringComparison.Ordinal) >= 0 || text.IndexOf("消息", StringComparison.Ordinal) >= 0) destination.Add("message");
            if (text.IndexOf("春宵", StringComparison.Ordinal) >= 0 || text.IndexOf("共度一夜", StringComparison.Ordinal) >= 0) destination.Add("intimacy");
            if (text.IndexOf("心情", StringComparison.Ordinal) >= 0 || text.IndexOf("心境", StringComparison.Ordinal) >= 0) destination.Add("mood");
            if (text.IndexOf("名望", StringComparison.Ordinal) >= 0 || text.IndexOf("声名", StringComparison.Ordinal) >= 0) destination.Add("fame");
        }

        private static List<NarrativeClause> SplitNarrativeClauses(string narrative)
        {
            var result = new List<NarrativeClause>();
            string text = (narrative ?? string.Empty).Replace('\r', '\n');
            string[] hardSentences = text.Split(new[] { '。', '！', '？', '\n' },
                StringSplitOptions.RemoveEmptyEntries);
            foreach (string hardSentence in hardSentences)
            {
                bool historical = false;
                string[] softParts = hardSentence.Split(new[] { '，', ',', '；', ';', '：', ':' },
                    StringSplitOptions.RemoveEmptyEntries);
                foreach (string softPart in softParts)
                {
                    foreach (string raw in SplitControlledConjunctions(softPart))
                    {
                        string part = NormalizeText(raw);
                        if (part.Length == 0) continue;
                        int historicalIndex = LastCueIndex(part, HistoricalCues);
                        int currentIndex = LastCueIndex(part, CurrentOutcomeCues);
                        if (historicalIndex >= 0 || currentIndex >= 0)
                            historical = historicalIndex > currentIndex;
                        result.Add(new NarrativeClause { Text = part, Historical = historical });
                    }
                }
            }
            return result;
        }

        private static IEnumerable<string> SplitControlledConjunctions(string value)
        {
            string text = value ?? string.Empty;
            int start = 0;
            while (start < text.Length)
            {
                int boundary = FindNextControlledConnector(text, start);
                if (boundary < 0) break;
                yield return text.Substring(start, boundary - start);
                start = boundary;
            }
            if (start < text.Length) yield return text.Substring(start);
        }

        private static int FindNextControlledConnector(string text, int start)
        {
            int best = -1;
            foreach (string connector in ClauseConnectors)
            {
                int search = start + 1;
                while (search < text.Length)
                {
                    int found = text.IndexOf(connector, search, StringComparison.Ordinal);
                    if (found < 0) break;
                    string prefix = text.Substring(start, found - start);
                    if (ContainsControlledTerm(prefix))
                    {
                        if (best < 0 || found < best) best = found;
                        break;
                    }
                    search = found + connector.Length;
                }
            }
            return best;
        }

        private static bool ContainsControlledTerm(string text)
        {
            foreach (KeyValuePair<string, string[]> rule in NarrativeTermsByKind)
                if (ContainsAny(text, rule.Value)) return true;
            return false;
        }

        private static int LastCueIndex(string text, string[] cues)
        {
            int result = -1;
            if (string.IsNullOrEmpty(text) || cues == null) return result;
            foreach (string cue in cues)
            {
                int index = text.LastIndexOf(cue, StringComparison.Ordinal);
                if (index > result) result = index;
            }
            return result;
        }

        private static List<ControlledOccurrence> FindControlledOccurrences(string clause)
        {
            string text = clause ?? string.Empty;
            var raw = new List<ControlledOccurrence>();
            foreach (KeyValuePair<string, string[]> rule in NarrativeTermsByKind)
                foreach (string term in rule.Value)
                {
                    int search = 0;
                    while (search < text.Length)
                    {
                        int found = text.IndexOf(term, search, StringComparison.Ordinal);
                        if (found < 0) break;
                        var occurrence = new ControlledOccurrence { Start = found, End = found + term.Length };
                        occurrence.Kinds.Add(rule.Key);
                        occurrence.Terms.Add(term);
                        raw.Add(occurrence);
                        search = found + Math.Max(1, term.Length);
                    }
                }
            raw.Sort((left, right) => left.Start != right.Start
                ? left.Start.CompareTo(right.Start) : right.End.CompareTo(left.End));

            var merged = new List<ControlledOccurrence>();
            foreach (ControlledOccurrence item in raw)
            {
                ControlledOccurrence last = merged.Count == 0 ? null : merged[merged.Count - 1];
                if (last != null && item.Start < last.End)
                {
                    if (item.End > last.End) last.End = item.End;
                    last.Kinds.UnionWith(item.Kinds);
                    last.Terms.UnionWith(item.Terms);
                    continue;
                }
                merged.Add(item);
            }

            var result = new List<ControlledOccurrence>();
            for (int i = 0; i < merged.Count; i++)
            {
                ControlledOccurrence source = merged[i];
                int localStart = i == 0 ? 0 : merged[i - 1].End;
                int localEnd = i + 1 < merged.Count ? merged[i + 1].Start : text.Length;
                if (localEnd < source.End) localEnd = source.End;
                var occurrence = new ControlledOccurrence
                {
                    Start = source.Start - localStart,
                    End = source.End - localStart,
                    Text = text.Substring(localStart, localEnd - localStart),
                };
                occurrence.Kinds.UnionWith(source.Kinds);
                occurrence.Terms.UnionWith(source.Terms);
                result.Add(occurrence);
            }
            return result;
        }

        private static string BuildActionContext(IList<NarrativeClause> clauses, int index)
        {
            if (clauses == null || index < 0 || index >= clauses.Count) return string.Empty;
            bool historical = clauses[index].Historical;
            int first = index;
            for (int i = index - 1, remaining = 2; i >= 0 && remaining > 0; i--, remaining--)
            {
                if (clauses[i].Historical != historical) break;
                first = i;
            }
            int last = index;
            if (index + 1 < clauses.Count && clauses[index + 1].Historical == historical) last = index + 1;
            var parts = new List<string>();
            for (int i = first; i <= last; i++) parts.Add(clauses[i].Text);
            return string.Join("，", parts.ToArray());
        }

        private static string BuildOutcomeContext(IList<NarrativeClause> clauses, int index, string local)
        {
            string result = local ?? string.Empty;
            if (clauses == null || index < 0 || index + 1 >= clauses.Count) return result;
            NarrativeClause current = clauses[index];
            NarrativeClause next = clauses[index + 1];
            if (next.Historical != current.Historical || ContainsControlledTerm(next.Text)) return result;
            if (ContainsAny(next.Text, NegativeOutcomeCues) || ContainsAny(next.Text, UnknownOutcomeCues))
                return result + "，" + next.Text;
            return result;
        }

        private static bool OccurrenceCanMeanKind(ControlledOccurrence occurrence, string kind)
        {
            if (occurrence == null) return false;
            if (occurrence.Kinds.Contains(kind)) return true;
            return (kind == "gift_item" || kind == "gift_silver")
                && (occurrence.Kinds.Contains("gift_item") || occurrence.Kinds.Contains("gift_silver"));
        }

        private static bool IsActorElidingResult(ControlledOccurrence occurrence)
        {
            if (occurrence == null) return false;
            string[] resultTerms = { "毙命", "命丧", "死在", "毒发", "毒性发作", "中了毒", "被擒", "束手就擒" };
            foreach (string term in resultTerms)
                if (occurrence.Terms.Contains(term)) return true;
            return false;
        }

        private static bool IsResultContinuation(ControlledOccurrence occurrence)
        {
            if (occurrence == null) return false;
            string text = occurrence.Text ?? string.Empty;
            string[] cues =
            {
                "已经完成", "已完成", "已经落定", "已落定", "已经交割", "已交割", "已经收下", "已收下",
                "已经开始", "已开始", "易物已成", "事情已经落定", "此事已经落定"
            };
            return ContainsAny(text, cues);
        }

        private static bool PrefixAllowsInheritedSubject(ControlledOccurrence occurrence,
            StoryProjectionReceipt receipt)
        {
            if (occurrence == null || receipt == null || occurrence.Start < 0
                || occurrence.Start > (occurrence.Text?.Length ?? 0)) return false;
            string prefix = NormalizeText(occurrence.Text.Substring(0, occurrence.Start));
            string[] allowed =
            {
                "但是", "然而", "不过", "可是", "随后", "继而", "而后", "然后", "接着", "同时", "并且",
                "本月", "当月", "这个月", "此月", "如今", "而今", "眼下", "此刻", "这回", "此番",
                "但", "却", "并", "又", "再", "便", "遂", "就", "终究", "终于"
            };
            bool changed;
            do
            {
                changed = false;
                foreach (string marker in allowed)
                    if (prefix.StartsWith(marker, StringComparison.Ordinal))
                    {
                        prefix = prefix.Substring(marker.Length).Trim();
                        changed = true;
                        break;
                    }
            } while (changed && prefix.Length > 0);
            return prefix.Length == 0;
        }

        private static bool AnyKindRecorded(ISet<string> recordedKinds, ISet<string> occurrenceKinds)
        {
            if (occurrenceKinds == null) return false;
            foreach (string kind in occurrenceKinds)
                if (KindRecorded(recordedKinds, kind)) return true;
            return false;
        }

        private static string JoinKinds(IEnumerable<string> kinds)
            => kinds == null ? string.Empty : string.Join("/", new List<string>(kinds).ToArray());

        private static bool KindAuthorizedBySuccess(ISet<string> successKinds, string kind)
        {
            if (successKinds != null && successKinds.Contains(kind)) return true;
            // Both gift tools share ordinary Chinese verbs; the exact receipt asset check still
            // prevents the authorized gift from silently changing its subject.
            return successKinds != null && (kind == "gift_item" || kind == "gift_silver")
                && (successKinds.Contains("gift_item") || successKinds.Contains("gift_silver"));
        }

        private static bool KindRecorded(ISet<string> recordedKinds, string kind)
        {
            if (recordedKinds != null && recordedKinds.Contains(kind)) return true;
            return recordedKinds != null && (kind == "gift_item" || kind == "gift_silver")
                && (recordedKinds.Contains("gift_item") || recordedKinds.Contains("gift_silver"));
        }

        private static bool AssetNeedsNarrativeCoverage(string kind)
        {
            switch (kind)
            {
                case "steal": case "gift_item": case "gift_silver": case "barter": case "teach":
                case "practice": case "feature": case "movement": case "book": case "book_read": case "secret":
                case "equipment": case "appearance": case "message": case "mood": case "fame":
                    return true;
                default:
                    return false;
            }
        }

        private static bool NarrativeCoversAsset(string narrative, string asset, string kind)
        {
            string value = NormalizeText(asset);
            if (value.Length == 0) return false;
            // Numeric state deltas are code evidence, not player-facing prose.  The story only
            // needs to express the corresponding fact in natural language.
            if (string.Equals(kind, "mood", StringComparison.Ordinal)
                && (narrative.IndexOf("心情", StringComparison.Ordinal) >= 0
                    || narrative.IndexOf("心境", StringComparison.Ordinal) >= 0)) return true;
            if (string.Equals(kind, "fame", StringComparison.Ordinal)
                && (narrative.IndexOf("名望", StringComparison.Ordinal) >= 0
                    || narrative.IndexOf("声名", StringComparison.Ordinal) >= 0)) return true;
            if (string.Equals(kind, "gift_silver", StringComparison.Ordinal)
                && value.StartsWith("银钱", StringComparison.Ordinal)
                && (narrative.IndexOf("银", StringComparison.Ordinal) >= 0
                    || narrative.IndexOf("钱袋", StringComparison.Ordinal) >= 0)) return true;
            if (narrative.IndexOf(value, StringComparison.Ordinal) >= 0) return true;
            string unsigned = value.TrimStart('+');
            if (unsigned.Length > 0 && unsigned.Length <= 8
                && narrative.IndexOf(unsigned, StringComparison.Ordinal) >= 0) return true;
            string[] parts = value.Split(new[] { '|', ',', '，', ';', '；', ':', '：', '=', '>', '↔', '/', '\\', '[', ']', '{', '}', '(', ')', '（', '）' },
                StringSplitOptions.RemoveEmptyEntries);
            foreach (string raw in parts)
            {
                string part = NormalizeText(raw).Trim('"', '\'', '「', '」', '“', '”');
                if (part.Length >= 2 && part.Length <= 48 && ContainsCjkOrDigit(part)
                    && narrative.IndexOf(part, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        private static bool ContainsCjkOrDigit(string value)
        {
            foreach (char c in value)
                if (char.IsDigit(c) || c >= 0x3400 && c <= 0x9fff) return true;
            return false;
        }

        private static string[] SplitNarrativeSentences(string narrative)
            => (narrative ?? string.Empty).Split(new[] { '。', '！', '？', '\n', '\r' },
                StringSplitOptions.RemoveEmptyEntries);

        private static bool ContainsAny(string value, string[] candidates)
        {
            if (string.IsNullOrEmpty(value) || candidates == null) return false;
            foreach (string candidate in candidates)
                if (!string.IsNullOrEmpty(candidate)
                    && value.IndexOf(candidate, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private static string RenderCodeOwnedStory(IList<StoryProjectionReceipt> receipts,
            int failed, int unknown, int noAction)
        {
            int successCount = receipts?.Count ?? 0;
            var paragraphs = new List<string>();
            if (receipts != null)
                foreach (StoryProjectionReceipt receipt in receipts)
                    paragraphs.Add(RenderReceiptSentence(receipt));
            if (successCount == 0)
                paragraphs.Add("这一个月里虽有不少盘算，却没有一件真正落到江湖上。未曾发生、尚未证实的事，不会被写成既定结局。" );
            else if (failed > 0 || unknown > 0 || noAction > 0)
                paragraphs.Add("其余谋划或因条件不足而止步，或始终没有确切回音；它们没有改变眼前已经确认的局面。" );
            return string.Join("\n\n", paragraphs.ToArray());
        }

        private static string RenderReceiptSentence(StoryProjectionReceipt receipt)
        {
            string actor = Entity(receipt.ActorName, receipt.ActorId);
            string target = receipt.TargetId > 0 ? Entity(receipt.TargetName, receipt.TargetId) : "";
            string asset = NormalizeText(receipt.Asset);
            switch (NormalizeKind(receipt.Kind))
            {
                case "kill": return actor + "与" + target + "狭路相逢。刀光收处，" + target + "已命丧其手，此事再无转圜。";
                case "poison": return actor + "暗中向" + target + "下了毒，药性已经发作，这一手终究落到了实处。";
                case "capture": return actor + "截住了" + target + "的去路，几番周旋之后，终于将其擒下。";
                case "steal": return actor + "趁隙从" + target + "处取走了「" + Asset(asset, "那件物品") + "」，东西如今已经易手。";
                case "relationship":
                {
                    string relation = NormalizeText(asset).ToLowerInvariant();
                    if (relation == "adored")
                        return actor + "向" + target + "表明了爱慕；这份心意只属于" + actor + "，并不代表" + target + "也已倾心。";
                    if (relation == "lover")
                        return actor + "向" + target + "表明了爱慕；因" + target + "早已有意，两人的心意至此相通。";
                    return actor + "与" + target + "当面定下了" + RelationshipDisplayName(asset, false) + "，往后再见，彼此已有了新的名分。";
                }
                case "relationship_end": return actor + "与" + target + "解除了彼此的" + RelationshipDisplayName(asset, true) + "，旧日名分就此作罢。";
                case "enmity": return IsPositiveAsset(asset)
                    ? actor + "与" + target + "终于把旧怨说开，横在两人之间的那根刺也松了几分。"
                    : actor + "与" + target + "当面撕破了最后一点情面，这场过节从此再难轻轻揭过。";
                case "gift_item": return actor + "把「" + NaturalAsset(asset, "一件财物") + "」放到" + target + "手边。" + target + "收下时没有多说，分量却已落在心里。";
                case "gift_silver": return actor + "把一只沉甸甸的钱袋推到" + target + "面前。" + target + "没有推辞，这份人情便算接了下来。";
                case "barter": return RenderBarter(actor, target, asset);
                case "teach": return actor + "向" + target + "倾囊相授，将「" + Asset(asset, "一门武艺") + "」真正传了下去。";
                case "practice": return actor + "重新参悟「" + Asset(asset, "一门功法") + "」，行功路数自此有了不同。";
                case "feature": return actor + "经此一遭，心性与根骨都显出一番新的变化。";
                case "movement": return actor + "动身上路，最终抵达了「" + Asset(asset, "目的地") + "」。";
                case "remember": return actor + "把本月真正做成的事记在心里，此后不会轻易忘却。";
                case "heal": return actor + "替" + target + "诊治伤势，疗伤之举已经完成。";
                case "book": return actor + "伏案写成「" + Asset(asset, "一本秘籍") + "」，随后将书交给了" + target + "。";
                case "book_read": return actor + "静心读完了「" + Asset(asset, "一本书") + "」，书中要义已尽数记下。";
                case "secret": return actor + "把秘闻「" + Asset(asset, "一则秘闻") + "」亲口告诉了" + target + "。";
                case "equipment": return actor + "在灯下重新整顿随身器物，换下旧物，又把趁手的东西安置妥当。";
                case "item_use": return actor + "取出「" + Asset(asset, "一件随身物")
                    + "」自行用了，物品的效用已经落到实处。";
                case "appearance": return actor + "对镜改换装束形貌，再出门时，已是另一番精神。";
                case "support": return actor + "在门派众人面前表明态度，公开站在了" + target + "一边。";
                case "favor": return IsPositiveAsset(asset)
                    ? actor + "再看" + target + "时，神色已缓和许多，先前那点隔阂也随之淡了。"
                    : actor + "望向" + target + "的目光冷了下来，原本尚可转圜的情分也薄了几分。";
                case "message": return actor + "把话传给了" + target + "：“" + Asset(asset, "此事容后再叙") + "”。";
                case "intimacy": return actor + "与" + target + "情意相投，共度了一夜。";
                case "mood": return IsPositiveAsset(asset)
                    ? target + "因" + actor + "这一番作为心情渐渐好转，眉间也舒展开来。"
                    : target + "因" + actor + "这一番作为心情沉了下去，久久难以释怀。";
                case "fame": return IsPositiveAsset(asset)
                    ? target + "的事迹渐渐传开，江湖名望也随之抬高，往来之人不免另眼相看。"
                    : target + "的所作所为传入江湖后惹来不少非议，声名也因此受了损。";
                default: return actor + "做成了一件确有着落的事。";
            }
        }

        private static string Entity(string name, int id)
        {
            string normalized = NormalizeText(name);
            return normalized.Length == 0 ? "一名江湖客" : normalized;
        }

        private static string Asset(string value, string fallback)
            => string.IsNullOrWhiteSpace(value) ? fallback : value;

        private static bool IsPositiveAsset(string value)
        {
            string normalized = NormalizeText(value);
            if (normalized == "化解") return true;
            if (normalized.StartsWith("-", StringComparison.Ordinal)) return false;
            return normalized.StartsWith("+", StringComparison.Ordinal)
                || normalized.IndexOf("增加", StringComparison.Ordinal) >= 0
                || normalized.IndexOf("上升", StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// Converts every relation enum accepted by chat, companion-monthly and event tools
        /// into player-facing Chinese. Runtime enum tokens remain in durable receipts, but
        /// must never leak into green results, histories or deterministic projections.
        /// </summary>
        public static string RelationshipDisplayName(string asset, bool ending)
        {
            switch (NormalizeText(asset).ToLowerInvariant())
            {
                case "befriend":
                case "best_friend":
                case "friend":
                    return ending ? "挚友情分" : "挚友之谊";
                case "mentor":
                case "apprentice":
                case "take_disciple":
                    return ending ? "师徒名分" : "师徒之礼";
                case "adoptive_parent":
                    return ending ? "义父母名分" : "义父母之亲";
                case "adoptive_child":
                    return ending ? "义子女名分" : "义子女之亲";
                case "lover":
                    return ending ? "爱慕之情" : "两情相悦";
                case "adored":
                    return ending ? "爱慕之情" : "单向爱慕";
                case "spouse":
                case "husband_or_wife":
                    return ending ? "夫妻情分" : "夫妻之约";
                case "sworn":
                case "swear_sibling":
                case "sworn_sibling":
                    return ending ? "结义名分" : "结义之盟";
                default: return ending ? "旧日名分" : "一桩郑重约定";
            }
        }

        private static string NaturalAsset(string value, string fallback)
        {
            string normalized = NormalizeText(value);
            int quantity = normalized.LastIndexOf('x');
            if (quantity > 0 && quantity < normalized.Length - 1)
            {
                bool digits = true;
                for (int i = quantity + 1; i < normalized.Length; i++)
                    if (!char.IsDigit(normalized[i])) { digits = false; break; }
                if (digits) normalized = normalized.Substring(0, quantity);
            }
            return normalized.Length == 0 ? fallback : normalized;
        }

        private static string RenderBarter(string actor, string target, string asset)
        {
            string normalized = NormalizeText(asset);
            int split = normalized.IndexOf('↔');
            if (split > 0 && split < normalized.Length - 1)
            {
                string offered = normalized.Substring(0, split).Trim();
                string received = normalized.Substring(split + 1).Trim();
                return actor + "取出「" + offered + "」，与" + target + "换来了「" + received
                    + "」。两人当面验过，这桩买卖便算谈妥。";
            }
            return actor + "与" + target + "当面谈妥一桩以物易物，各自收下了看中的东西。";
        }

        private static bool ClaimExactlyMatches(string kind, string operationId, int actorId,
            int targetId, string asset, StoryProjectionReceipt receipt)
            => receipt != null && string.Equals(NormalizeKind(kind), NormalizeKind(receipt.Kind), StringComparison.Ordinal)
                && string.Equals(operationId, receipt.OperationId, StringComparison.Ordinal)
                && actorId == receipt.ActorId && targetId == receipt.TargetId
                && string.Equals(NormalizeText(asset), NormalizeText(receipt.Asset), StringComparison.Ordinal);

        private static bool HasOnlyFields(JObject value, params string[] required)
        {
            if (value == null || value.Count != required.Length) return false;
            var names = new HashSet<string>(required, StringComparer.Ordinal);
            foreach (JProperty property in value.Properties())
                if (!names.Remove(property.Name)) return false;
            return names.Count == 0;
        }

        private static bool TryRequiredString(JObject value, string name, out string result)
        {
            result = null;
            JToken token = value?[name];
            if (!(token is JValue scalar) || scalar.Type != JTokenType.String) return false;
            result = (string)scalar;
            return result != null;
        }

        private static bool TryRequiredInteger(JObject value, string name, out int result)
        {
            result = 0;
            JToken token = value?[name];
            if (!(token is JValue scalar) || scalar.Type != JTokenType.Integer) return false;
            try { result = scalar.Value<int>(); return true; }
            catch { return false; }
        }

        private static string NormalizeKind(string value)
            => (value ?? string.Empty).Trim().ToLowerInvariant();

        private static bool KindRequiresAsset(string kind)
        {
            switch (kind)
            {
                case "relationship": case "relationship_end": case "enmity": case "gift_item": case "gift_silver":
                case "barter": case "teach": case "practice": case "feature":
                case "movement": case "steal":
                case "book": case "book_read": case "secret": case "equipment": case "item_use": case "appearance": case "favor": case "message": case "intimacy": case "mood": case "fame":
                    return true;
                default:
                    return false;
            }
        }

        private static string NormalizeText(string value)
        {
            string text = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
            var builder = new StringBuilder(text.Length);
            bool previousSpace = false;
            foreach (char c in text)
            {
                bool space = char.IsWhiteSpace(c);
                if (!space || !previousSpace) builder.Append(space ? ' ' : c);
                previousSpace = space;
            }
            return builder.ToString();
        }
    }
}
