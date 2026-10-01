using System.Text;
using System.Text.RegularExpressions;
using JianghuYouling.Core.Memory;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.Core.Influence
{
    public sealed class ParsedTalk
    {
        public string Reply { get; set; }
        public InfluenceIntent Intent { get; set; }
        public bool IntentParsed { get; set; }      // 成功解析出 JSON 意图块(原样或修复后)
        public bool JsonRepairFailed { get; set; }  // 明显是意图块、却连修复后都解析不出 → 调用方据此决定重试
    }

    /// <summary>
    /// 解析 LLM 扮演输出:约定 = 角色回话正文 + 末尾一个 ```json {…} ``` 意图块(效果全靠这段 JSON 驱动)。
    /// 健壮性:LLM 指令遵循不完美会写歪 JSON(尾逗号/中文标点/截断/漏围栏),若一坏就丢弃意图,会造成"答应却没生效"。
    /// 故:① 容错抽取(围栏优先,否则字符串感知地取末尾平衡 {…},截断也兜);② 修复后再解析(去尾逗号/注释、全角→半角、补括号);
    /// ③ 仍失败且明显是意图块 → 置 JsonRepairFailed 让上层重试。纯逻辑,可单测。
    /// </summary>
    public static class TalkResponseParser
    {
        private static readonly Regex JsonFence = new Regex("```(?:json)?\\s*([\\s\\S]*?)```", RegexOptions.IgnoreCase);

        public static ParsedTalk Parse(string content)
        {
            var res = new ParsedTalk { Reply = "", Intent = new InfluenceIntent() };
            if (string.IsNullOrWhiteSpace(content)) { res.Reply = "……"; return res; }

            string replyField = null;

            // 主路径:整条回复就是一个 JSON 对象(response_format=json_object 时如此),reply 字段即正文。
            var o = TryParseJObject(content.Trim());
            if (o != null)
            {
                res.IntentParsed = true;
                replyField = o["reply"]?.ToString();
                Populate(res.Intent, o);
            }
            else
            {
                // 回退:provider 不支持 json_object,模型可能给 正文 + ```json 块```。抠块解析;正文取块之前。
                string json = ExtractJsonCandidate(content, out int jsonStart);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    if (jsonStart >= 0) res.Reply = content.Substring(0, jsonStart);
                    var o2 = TryParseJObject(json);
                    if (o2 != null) { res.IntentParsed = true; replyField = o2["reply"]?.ToString(); Populate(res.Intent, o2); }
                    else res.JsonRepairFailed = LooksLikeIntent(json);   // 像意图块却修不回 → 供上层整体重试
                }
                else
                {
                    res.Reply = content;   // 连 JSON 都没有:整条当纯正文(闲聊)
                }
            }

            // 正文优先取 JSON 的 reply 字段;否则用回退算出的正文。
            if (!string.IsNullOrWhiteSpace(replyField)) res.Reply = replyField;
            // 防漏:显示给玩家的正文绝不残留 ```围栏``` 或裸 JSON(抠块不准时兜底)。
            res.Reply = SanitizeReply(res.Reply);
            if (string.IsNullOrWhiteSpace(res.Reply)) res.Reply = "……";
            return res;
        }

        // 兜底清洗:剥掉 ``` 代码块、以及末尾可能残留的裸 JSON 对象,避免把意图块显示给玩家。
        private static string SanitizeReply(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return s;
            string t = Regex.Replace(s, "```[\\s\\S]*?```", "");   // 成对代码块
            t = t.Replace("```json", "").Replace("```", "");        // 孤立围栏标记
            int brace = t.IndexOf('{');
            if (brace >= 0)
            {
                string after = t.Substring(brace);
                if (Regex.IsMatch(after, "\"[A-Za-z_]+\"\\s*:"))    // 仅当像 JSON(有 "键":)才剥,避免误伤正文花括号
                    t = t.Substring(0, brace);
            }
            return t.Trim();
        }

        // 安全读整数:非数字串(如 satisfaction:""很高"")不抛异常,尽力提取或取默认,避免整块解析崩。
        private static int SafeInt(JToken t, int def = 0)
        {
            if (t == null || t.Type == JTokenType.Null) return def;
            try { return t.Value<int>(); } catch { }
            string s = t.ToString();
            if (int.TryParse(s, out int v)) return v;
            var m = Regex.Match(s ?? "", "-?\\d+");
            return (m.Success && int.TryParse(m.Value, out int v2)) ? v2 : def;
        }

        // 安全读布尔:容 true/1/yes/是 等写法,不抛异常。
        private static bool SafeBool(JToken t, bool def = false)
        {
            if (t == null || t.Type == JTokenType.Null) return def;
            try { return t.Value<bool>(); } catch { }
            string s = (t.ToString() ?? "").Trim().ToLowerInvariant();
            if (s == "true" || s == "1" || s == "yes" || s == "是") return true;
            if (s == "false" || s == "0" || s == "no" || s == "否") return false;
            return def;
        }

        // 把已解析的 JObject 各字段读进 InfluenceIntent(与提示词字段一一对应)
        private static void Populate(InfluenceIntent intent, JObject o)
        {
            intent.Satisfaction = SafeInt(o["satisfaction"]);
            intent.Mood = SafeInt(o["mood"]);
            intent.RelationProposal = o["relation"]?.ToString();
            intent.RecognizeTaiwu = SafeBool(o["recognize"]);
            intent.ShareSecret = SafeBool(o["share_secret"]);
            intent.MoralityShift = SafeInt(o["morality_shift"]);
            intent.GiveSilverToTaiwu = SafeInt(o["give_silver"]);
            intent.GiveItemName = o["give_item"]?.ToString();
            intent.GiveItemCount = SafeInt(o["give_item_count"], 1);
            intent.SectSupport = SafeBool(o["sect_support"]);
            intent.MatchmakeTarget = o["matchmake"]?.ToString();
            intent.FeudTarget = o["feud_target"]?.ToString();
            intent.ReconcileTarget = o["reconcile_target"]?.ToString();
            var follow = (o["follow"]?.ToString() ?? "").Trim().ToLowerInvariant();
            intent.FollowDecision = (follow == "follow" || follow == "1" || follow == "true") ? 1
                                  : (follow == "leave" || follow == "-1") ? -1 : 0;
            intent.DiscloseSecretTo = o["disclose_secret_to"]?.ToString();
            intent.SecretIndex = SafeInt(o["secret_index"]);
            intent.AlertnessShift = SafeInt(o["alertness_shift"]);
            intent.TeachSkillName = o["teach_skill"]?.ToString();
            intent.ReleaseTarget = o["release_target"]?.ToString();
            intent.FavorTarget = o["favor_target"]?.ToString();
            intent.FavorTargetDelta = SafeInt(o["favor_target_delta"]);
            var eq = o["equip"];
            if (eq != null && eq.Type == JTokenType.Object)
            {
                var act = (eq["action"]?.ToString() ?? "").Trim().ToLowerInvariant();
                if (act == "on" || act == "equip") intent.EquipPutOnItem = eq["item"]?.ToString();
                else if (act == "off" || act == "unequip") intent.EquipTakeOff = eq["part"]?.ToString();
            }
            intent.Reasoning = o["reasoning"]?.ToString();
            var mem = o["memory"];
            if (mem != null && mem.Type == JTokenType.Object)
            {
                var draft = new MemoryDraft
                {
                    Content = mem["content"]?.ToString(),
                    Type = mem["type"]?.ToString(),
                    Keywords = mem["keywords"]?.ToString(),
                    Importance = MemoryTrustPolicy.ClampModelImportance(SafeInt(mem["importance"], 3)),
                };
                if (!string.IsNullOrWhiteSpace(draft.Content)) intent.Memory = draft;
            }
            var beh = o["behavior"];
            if (beh != null && beh.Type == JTokenType.Object)
            {
                var bd = new BehaviorDraft
                {
                    Kind = beh["kind"]?.ToString(),
                    Target = beh["target"]?.ToString(),
                    Note = beh["note"]?.ToString(),
                };
                if (!string.IsNullOrWhiteSpace(bd.Kind)) intent.Behavior = bd;
            }
        }

        // 抽取意图 JSON:① 优先 ```json … ``` 围栏(取最后一个);② 否则字符串感知扫描,取末尾一个"平衡的 {…}";
        // ③ 若有未闭合的顶层 {(被 maxTokens 截断),取到末尾留待修复补括号。返回候选串,jsonStart=其在原文中的起点(-1=没抽到)。
        private static string ExtractJsonCandidate(string content, out int jsonStart)
        {
            jsonStart = -1;
            if (string.IsNullOrEmpty(content)) return null;

            // 围栏优先(意图块在末尾,取最后一处)
            Match last = null;
            foreach (Match mm in JsonFence.Matches(content)) last = mm;
            if (last != null && last.Success)
            {
                jsonStart = last.Index;
                return last.Groups[1].Value;
            }

            // 字符串感知扫描:记录每个闭合的顶层对象;末尾那个最可能是意图块
            int depth = 0, start = -1, lastOpenDepth0 = -1, bestStart = -1, bestEnd = -1;
            bool inStr = false; char q = '"';
            for (int i = 0; i < content.Length; i++)
            {
                char c = content[i];
                if (inStr)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == q) inStr = false;
                    continue;
                }
                if (c == '"' || c == '\'') { inStr = true; q = c; continue; }
                if (c == '{') { if (depth == 0) { start = i; lastOpenDepth0 = i; } depth++; }
                else if (c == '}')
                {
                    if (depth > 0) { depth--; if (depth == 0 && start >= 0) { bestStart = start; bestEnd = i; start = -1; } }
                }
            }
            if (bestStart >= 0) { jsonStart = bestStart; return content.Substring(bestStart, bestEnd - bestStart + 1); }
            if (lastOpenDepth0 >= 0) { jsonStart = lastOpenDepth0; return content.Substring(lastOpenDepth0); }  // 截断:留待修复
            return null;
        }

        // 解析:原样 → 修复后。两者皆失败返回 null。
        private static JObject TryParseJObject(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JObject.Parse(json); } catch { }
            try { return JObject.Parse(RepairJson(json)); } catch { }
            return null;
        }

        // 修复 LLM 常见不合规(仅在原样解析失败时走,避免误伤正常输出):
        // 智能/全角引号→直引号、全角逗号冒号→半角、去 // 与 /* */ 注释、去尾逗号、补未闭合的字符串/括号。
        private static string RepairJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            string t = s.Trim();
            // 去可能残留的围栏标记
            if (t.StartsWith("```")) t = t.TrimStart('`');
            int fence = t.IndexOf("```");
            if (fence >= 0) t = t.Substring(0, fence);
            // 引号与标点归一(全角→半角)
            t = t.Replace('“', '"').Replace('”', '"')   // “ ”
                 .Replace('‘', '\'').Replace('’', '\'') // ‘ ’
                 .Replace('＂', '"').Replace('＇', '\'')  // 全角引号
                 .Replace('：', ':').Replace('，', ',');  // 全角冒号、逗号
            // 去注释
            t = Regex.Replace(t, "/\\*[\\s\\S]*?\\*/", "");
            t = Regex.Replace(t, "(?m)//[^\\n]*$", "");
            // 去尾逗号(逗号后紧跟 } 或 ])
            t = Regex.Replace(t, ",\\s*([}\\]])", "$1");
            // 补未闭合:扫描括号深度与字符串状态
            int braces = 0, brk = 0; bool inStr = false; char q = '"';
            for (int i = 0; i < t.Length; i++)
            {
                char c = t[i];
                if (inStr) { if (c == '\\') { i++; continue; } if (c == q) inStr = false; continue; }
                if (c == '"' || c == '\'') { inStr = true; q = c; continue; }
                if (c == '{') braces++; else if (c == '}') braces--;
                else if (c == '[') brk++; else if (c == ']') brk--;
            }
            var sb = new StringBuilder(t);
            if (inStr) sb.Append(q);              // 未闭合字符串
            while (brk-- > 0) sb.Append(']');
            while (braces-- > 0) sb.Append('}');
            return sb.ToString();
        }

        // 候选串是否"明显是意图块":含键值冒号 + 至少一个已知意图键名。
        // (即便引号/标点写歪,键名文本仍在;以此区分真·格式botch vs 正文里偶发的花括号,避免无谓重试。)
        private static bool LooksLikeIntent(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length < 12) return false;
            if (json.IndexOf(':') < 0 && json.IndexOf('：') < 0) return false;
            return json.Contains("satisfaction") || json.Contains("reasoning") || json.Contains("mood")
                || json.Contains("relation") || json.Contains("give_") || json.Contains("teach_skill")
                || json.Contains("matchmake") || json.Contains("follow")
                || json.Contains("morality") || json.Contains("memory");
        }
    }
}
