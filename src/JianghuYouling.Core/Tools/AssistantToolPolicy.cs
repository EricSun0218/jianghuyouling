using System;
using System.Collections.Generic;

namespace JianghuYouling.Core.Tools
{
    /// <summary>
    /// 灵儿工具的确定性授权边界。设置修改必须由玩家本轮原文直接授权；日志/联网结果属于
    /// 不可信工具输出，不能再替玩家扩大写权限。此类保持纯逻辑，便于离线回归恶意提示注入。
    /// </summary>
    public static class AssistantToolPolicy
    {
        public sealed class DirectAuthorization
        {
            private readonly Dictionary<string, string> _values =
                new Dictionary<string, string>(StringComparer.Ordinal);

            public bool RequestsUntrustedRead { get; internal set; }
            public bool IsAuthorized(string tool) => !string.IsNullOrWhiteSpace(tool) && _values.ContainsKey(tool);
            public bool TryGetBoolean(string tool, out bool value)
            {
                value = false;
                string raw;
                return _values.TryGetValue(tool ?? string.Empty, out raw)
                    && raw.StartsWith("bool:", StringComparison.Ordinal)
                    && bool.TryParse(raw.Substring(5), out value);
            }
            public bool TryGetScalar(string tool, out string value)
            {
                value = null;
                string raw;
                if (!_values.TryGetValue(tool ?? string.Empty, out raw)
                    || !raw.StartsWith("scalar:", StringComparison.Ordinal)) return false;
                value = raw.Substring(7);
                return true;
            }
            public bool TryGetFreeText(string tool, out string value)
            {
                value = null;
                string raw;
                if (!_values.TryGetValue(tool ?? string.Empty, out raw)
                    || !raw.StartsWith("text:", StringComparison.Ordinal)) return false;
                value = raw.Substring(5);
                return true;
            }
            internal void AddBoolean(string tool, bool value) => _values[tool] = "bool:" + value;
            internal void AddScalar(string tool, string value) => _values[tool] = "scalar:" + (value ?? string.Empty);
            internal void AddText(string tool, string value) => _values[tool] = "text:" + (value ?? string.Empty);
            internal void AddAction(string tool) => _values[tool] = "action";
        }

        private static readonly string[] BooleanTools =
        {
            "toggle_ai_event", "toggle_companion_monthly", "toggle_thinking", "toggle_stream",
            "toggle_assistant_proactive",
        };
        private static readonly string[] ScalarTools =
        {
            "set_difficulty", "set_reply_length", "set_ghostwrite_length", "set_proactive_frequency",
        };
        private static readonly string[] FreeTextTools =
        {
            "set_taiwu_voice", "set_assistant_name",
        };

        public static DirectAuthorization ParseDirectAuthorization(string input)
        {
            var authorization = new DirectAuthorization
            {
                RequestsUntrustedRead = InputRequestsUntrustedRead(input),
            };
            if (string.IsNullOrWhiteSpace(input) || IsNonDirectCommandContext(input)) return authorization;
            foreach (string tool in BooleanTools)
                if (TryGetExpectedBooleanSetting(tool, input, out bool boolean))
                    authorization.AddBoolean(tool, boolean);
            foreach (string tool in ScalarTools)
                if (TryGetExpectedScalarSetting(tool, input, out string scalar) && HasDirectChangeCue(input))
                    authorization.AddScalar(tool, scalar);
            foreach (string tool in FreeTextTools)
                if (TryGetExpectedFreeTextSetting(tool, input, out string text)
                    && (HasDirectChangeCue(input) || tool == "set_assistant_name"
                        && ContainsAny(input, "以后叫", "叫你", "你叫")))
                    authorization.AddText(tool, text);
            if (InputDirectlyRequestsDiagnosticExport(input))
                authorization.AddAction("export_diagnostic_logs");
            return authorization;
        }

        public static bool IsUntrustedReadTool(string name)
            => string.Equals(name, "analyze_logs", StringComparison.Ordinal);

        public static bool IsLocalExportTool(string name)
            => string.Equals(name, "export_diagnostic_logs", StringComparison.Ordinal);

        /// <summary>日志导出会写桌面，只接受玩家本轮明确的导出命令，不从故障抱怨或询问中推断。</summary>
        public static bool InputDirectlyRequestsDiagnosticExport(string input)
        {
            if (string.IsNullOrWhiteSpace(input) || IsConsultative(input)
                || IsNonDirectCommandContext(input)) return false;
            if (!ContainsAny(input, "日志", "Player.log", "Player-prev.log", "诊断文件", "诊断包")) return false;
            if (!ContainsAny(input, "导出", "打包", "复制到桌面", "放到桌面", "存到桌面")) return false;
            if (ContainsAny(input, "不要导出", "别导出", "不用导出", "不必导出", "不要打包", "别打包",
                "不要复制", "别复制", "不要放到桌面", "别放到桌面")) return false;
            return true;
        }

        public static bool InputRequestsUntrustedRead(string input)
            => ContainsAny(input, "日志", "Player.log", "Player-prev.log", "报错", "流式异常", "接口异常",
                "语音失败", "TTS失败", "交换失败", "工具失败", "工具没执行", "工具没调用", "没落地");

        public static bool IsSettingMutationTool(string name)
        {
            switch (name)
            {
                case "set_difficulty": case "set_reply_length": case "set_ghostwrite_length":
                case "set_taiwu_voice": case "toggle_ai_event": case "toggle_companion_monthly":
                case "toggle_thinking": case "toggle_stream": case "set_assistant_name":
                case "toggle_assistant_proactive": case "set_proactive_frequency":
                    return true;
                default:
                    return false;
            }
        }

        public static bool IsFreeTextSettingTool(string name)
            => string.Equals(name, "set_taiwu_voice", StringComparison.Ordinal)
                || string.Equals(name, "set_assistant_name", StringComparison.Ordinal);

        public static bool PlayerDirectlyAuthorized(string tool, string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return false;
            if (IsConsultative(input) || IsNonDirectCommandContext(input)) return false;
            bool change = HasDirectChangeCue(input);
            switch (tool)
            {
                case "set_difficulty":
                    return change && TryGetExpectedScalarSetting(tool, input, out _);
                case "set_reply_length":
                    return change && TryGetExpectedScalarSetting(tool, input, out _);
                case "set_ghostwrite_length":
                    return change && TryGetExpectedScalarSetting(tool, input, out _);
                case "set_taiwu_voice":
                    return change && TryGetExpectedFreeTextSetting(tool, input, out _);
                case "toggle_ai_event":
                    return TryGetExpectedBooleanSetting(tool, input, out _);
                case "toggle_companion_monthly":
                    return TryGetExpectedBooleanSetting(tool, input, out _);
                case "toggle_thinking":
                    return TryGetExpectedBooleanSetting(tool, input, out _);
                case "toggle_stream":
                    return TryGetExpectedBooleanSetting(tool, input, out _);
                case "set_assistant_name":
                    return (change || ContainsAny(input, "以后叫", "叫你", "你叫"))
                        && TryGetExpectedFreeTextSetting(tool, input, out _);
                case "toggle_assistant_proactive":
                    return TryGetExpectedBooleanSetting(tool, input, out _);
                case "set_proactive_frequency":
                    return change && TryGetExpectedScalarSetting(tool, input, out _);
                case "export_diagnostic_logs":
                    return InputDirectlyRequestsDiagnosticExport(input);
                default:
                    return false;
            }
        }

        public static bool TryGetExpectedBooleanSetting(string tool, string input, out bool expected)
        {
            expected = false;
            if (string.IsNullOrWhiteSpace(input) || IsConsultative(input)
                || IsNonDirectCommandContext(input)) return false;
            string[] topics;
            switch (tool)
            {
                case "toggle_ai_event": topics = new[] { "AI 江湖", "AI江湖", "江湖事件", "江湖大事" }; break;
                case "toggle_companion_monthly": topics = new[] { "同道主动", "同道行事", "同道过月", "同道行为" }; break;
                case "toggle_thinking": topics = new[] { "思量", "思考过程", "推理过程", "推理内容" }; break;
                case "toggle_stream": topics = new[] { "流式输出", "流式", "逐字输出", "逐字显示" }; break;
                case "toggle_assistant_proactive": topics = new[] { "主动消息", "主动找我", "主动搭话", "打扰", "主动" }; break;
                default: return false;
            }
            if (!ContainsAny(input, topics)) return false;
            // A prohibition such as “不要关掉” is not permission to silently force
            // the opposite state. Ask/answer it in prose and leave settings untouched.
            if (ContainsAny(input, "不要关", "别关", "不要关闭", "别关闭", "不要隐藏", "别隐藏",
                "不要停", "别停", "不必关", "不用关")) return false;
            string[] turnOnCues = { "打开", "开启", "启用", "显示", "恢复显示", "开始" };
            string[] turnOffCues = { "关掉", "关闭", "停用", "隐藏", "停止", "取消",
                "别打扰", "别主动", "不要主动", "不再主动" };
            bool turnOn = MatchesDirectBooleanCommand(input, topics, turnOnCues);
            bool turnOff = MatchesDirectBooleanCommand(input, topics, turnOffCues);
            if (turnOn == turnOff) return false;
            expected = turnOn;
            return true;
        }

        private static bool MatchesDirectBooleanCommand(string input, string[] topics, string[] cues)
        {
            string command = NormalizeDirectBooleanCommand(input);
            if (command.Length == 0 || topics == null || cues == null) return false;
            foreach (string topic in topics)
                foreach (string cue in cues)
                {
                    if (string.IsNullOrEmpty(topic) || string.IsNullOrEmpty(cue)) continue;
                    if (string.Equals(command, cue + topic, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(command, topic + cue, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(command, JoinWithOverlap(cue, topic), StringComparison.OrdinalIgnoreCase)
                        || string.Equals(command, JoinWithOverlap(topic, cue), StringComparison.OrdinalIgnoreCase)
                        || cue.IndexOf(topic, StringComparison.OrdinalIgnoreCase) >= 0
                            && string.Equals(command, cue, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            return false;
        }

        private static string NormalizeDirectBooleanCommand(string input)
        {
            string value = (input ?? string.Empty).Trim();
            // A mixed "read, then explicitly change" turn remains represented immutably for
            // audit even though the orchestrator taints the whole exchange and hides mutations.
            // Only split at an explicit clause delimiter; reported/quoted contexts were already
            // rejected by IsNonDirectCommandContext.
            string[] sequenceMarkers = { "，再", ",再", "；再", ";再", "。再", ".再", "然后", "随后" };
            int sequenceAt = -1, sequenceLength = 0;
            foreach (string marker in sequenceMarkers)
            {
                int at = value.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (at >= 0 && at + marker.Length > sequenceAt + sequenceLength)
                { sequenceAt = at; sequenceLength = marker.Length; }
            }
            if (sequenceAt >= 0) value = value.Substring(sequenceAt + sequenceLength);
            value = value.Replace(" ", string.Empty).Replace("\t", string.Empty)
                .Replace("\r", string.Empty).Replace("\n", string.Empty);
            string[] prefixes = { "麻烦帮我", "麻烦给我", "请帮我", "请给我", "请替我", "麻烦",
                "帮我", "给我", "替我", "我要", "我想", "现在", "立即", "马上", "请", "把", "将" };
            bool changed;
            do
            {
                changed = false;
                value = value.TrimStart('，', ',', '。', '.', '！', '!', '：', ':');
                foreach (string prefix in prefixes)
                    if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        value = value.Substring(prefix.Length);
                        changed = true;
                        break;
                    }
            } while (changed && value.Length > 0);

            string[] suffixes = { "谢谢你", "谢谢", "一下吧", "一下", "吧", "呀", "啊", "了" };
            do
            {
                changed = false;
                value = value.TrimEnd('，', ',', '。', '.', '！', '!', '；', ';');
                foreach (string suffix in suffixes)
                    if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        value = value.Substring(0, value.Length - suffix.Length);
                        changed = true;
                        break;
                    }
            } while (changed && value.Length > 0);
            return value;
        }

        private static string JoinWithOverlap(string left, string right)
        {
            int max = Math.Min(left?.Length ?? 0, right?.Length ?? 0);
            for (int length = max; length > 0; length--)
                if (string.Equals(left.Substring(left.Length - length), right.Substring(0, length),
                    StringComparison.OrdinalIgnoreCase))
                    return left + right.Substring(length);
            return (left ?? string.Empty) + (right ?? string.Empty);
        }

        public static bool TryGetExpectedScalarSetting(string tool, string input, out string expected)
        {
            expected = null;
            if (string.IsNullOrWhiteSpace(input) || IsConsultative(input)
                || IsNonDirectCommandContext(input)) return false;
            string[] values;
            switch (tool)
            {
                case "set_difficulty":
                    if (!ContainsAny(input, "难度", "简单", "均衡", "困难")) return false;
                    values = new[] { "简单", "均衡", "困难" };
                    break;
                case "set_reply_length":
                    if (!ContainsAny(input, "回复篇幅", "回复长度", "回话篇幅", "NPC回复")) return false;
                    values = new[] { "简短", "适中", "详细", "不限" };
                    break;
                case "set_ghostwrite_length":
                    if (!ContainsAny(input, "代笔篇幅", "代笔长度")) return false;
                    values = new[] { "简短", "适中", "详细" };
                    break;
                case "set_proactive_frequency":
                    if (!ContainsAny(input, "主动消息频率", "主动频率", "搭话频率", "找我频率")) return false;
                    string[] frequencyPrefixes = { "主动消息频率设为", "主动消息频率设成", "主动消息频率改成", "主动消息频率调成", "主动消息频率调到",
                        "主动频率设为", "主动频率设成", "主动频率改成", "主动频率调成", "主动频率调到",
                        "搭话频率设为", "搭话频率改成", "搭话频率调成", "找我频率设为", "找我频率调成" };
                    foreach (string prefix in frequencyPrefixes)
                    {
                        int at = input.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
                        if (at < 0) continue;
                        string level = CleanCommandValue(input.Substring(at + prefix.Length));
                        if (level == "关" || level == "低" || level == "中" || level == "高")
                        { expected = level; return true; }
                        return false;
                    }
                    return false;
                default: return false;
            }
            foreach (string value in values)
                if (input.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (expected != null) return false;
                    expected = value;
                }
            return expected != null;
        }

        public static bool TryGetExpectedFreeTextSetting(string tool, string input, out string expected)
        {
            expected = null;
            if (string.IsNullOrWhiteSpace(input) || IsConsultative(input)
                || IsNonDirectCommandContext(input)) return false;
            string[] topics;
            string[] prefixes;
            switch (tool)
            {
                case "set_assistant_name":
                    topics = new[] { "名字", "改名", "以后叫", "叫你", "你叫" };
                    prefixes = new[] { "名字改成", "名字改为", "名字设为", "改名为", "改名叫", "以后叫你", "叫你", "你叫" };
                    break;
                case "set_taiwu_voice":
                    topics = new[] { "太吾口吻", "我的口吻", "太吾说话", "代笔口吻" };
                    prefixes = new[] { "太吾口吻改成", "太吾口吻改为", "太吾口吻设为", "我的口吻改成", "我的口吻设为",
                        "太吾说话改成", "太吾说话设为", "代笔口吻改成", "代笔口吻设为" };
                    break;
                default: return false;
            }
            if (!ContainsAny(input, topics)) return false;
            if (ContainsAny(input, "不要改", "别改", "不用改", "不必改", "不要设置", "别设置",
                "不要设", "别设", "不要把", "别把", "不用把", "不必把", "不要将", "别将",
                "不要叫", "别叫", "不要再叫", "别再叫", "先别", "暂不"))
                return false;

            string[] clearCommands = null;
            if (tool == "set_taiwu_voice")
                clearCommands = new[] { "清空太吾口吻", "太吾口吻清空", "清除太吾口吻", "恢复默认太吾口吻",
                    "太吾口吻恢复默认", "取消太吾口吻设定", "取消太吾口吻设置", "清空我的口吻", "我的口吻清空",
                    "清空代笔口吻", "代笔口吻清空" };

            if (clearCommands != null && ContainsAny(input, clearCommands))
            {
                // 清空只接受与目标字段紧邻的明确命令；“别清空”以及同句又给出新值都拒绝猜测。
                if (ContainsAny(input, "不要清空", "别清空", "不用清空", "不必清空", "不要恢复默认",
                    "别恢复默认", "不要取消", "别取消") || ContainsAny(input, prefixes))
                    return false;
                expected = string.Empty;
                return true;
            }
            return TryGetUniqueCommandValue(input, prefixes, out expected);
        }

        private static bool TryGetUniqueCommandValue(string input, string[] prefixes, out string value)
        {
            value = null;
            int chosenAt = -1, chosenEnd = -1;
            foreach (string prefix in prefixes)
            {
                int at = input.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
                if (at < 0) continue;
                int end = at + prefix.Length;
                if (chosenAt < 0 || at < chosenAt || (at == chosenAt && end > chosenEnd))
                {
                    chosenAt = at;
                    chosenEnd = end;
                }
            }
            if (chosenAt < 0) return false;

            // “以后叫你”内含“叫你”是同一个命令；除这种完全内含的别名外，第二个命令一律视为歧义。
            foreach (string prefix in prefixes)
            {
                int searchAt = 0;
                while (searchAt < input.Length)
                {
                    int at = input.IndexOf(prefix, searchAt, StringComparison.OrdinalIgnoreCase);
                    if (at < 0) break;
                    int end = at + prefix.Length;
                    bool containedInChosen = at >= chosenAt && end <= chosenEnd;
                    if (!containedInChosen) return false;
                    searchAt = at + 1;
                }
            }

            string extracted = CleanCommandValue(input.Substring(chosenEnd));
            if (extracted.Length == 0 || extracted.Length > 200) return false;
            value = extracted;
            return true;
        }

        private static string CleanCommandValue(string value)
        {
            value = (value ?? string.Empty).Trim();
            value = value.Trim('"', '\'', '“', '”', '‘', '’', '「', '」', '『', '』', '。', '.', '！', '!', '，', ',');
            return value.Trim();
        }

        private static bool HasDirectChangeCue(string input)
        {
            if (ContainsAny(input, "改成", "改为", "修改", "调整", "调成", "调为", "调到", "设置为", "设为",
                "设成", "切换为", "换成", "清空", "恢复默认", "帮我把", "请把", "麻烦把", "以后叫", "改名"))
                return true;
            if (ContainsAny(input, "怎么", "如何", "为什么", "是什么", "能不能", "可以吗", "可不可以", "?", "？"))
                return false;
            return ContainsAny(input, "改", "调", "设", "换");
        }

        private static bool IsConsultative(string input)
            => ContainsAny(input, "怎么", "如何", "为什么", "是什么", "能不能", "可以吗", "可不可以",
                "该不该", "要不要", "请问", "教程", "在哪设置", "哪里设置", "?", "？",
                // A state word inside an explanation request is data, not an imperative. Keep
                // this deliberately broad and fail closed for mixed “change + explain” turns.
                "告诉我", "解释", "说明", "介绍", "讲讲", "聊聊", "科普", "想知道", "想了解",
                "了解一下", "考虑", "影响", "后果", "会发生", "会怎样", "会怎么样", "有什么用",
                "有何作用", "区别", "优缺点", "安全吗", "值得吗", "必要吗", "建议", "什么");

        private static bool IsNonDirectCommandContext(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return true;
            // Settings are a control surface.  Quoted/reported/conditional/negated text is
            // deliberately rejected instead of attempting natural-language scope guessing.
            return ContainsAny(input,
                "没有让你", "没让你", "并没有让", "未让你", "没有说", "没说", "未要求", "没要求",
                "不是让你", "不是要你", "并非要", "不想", "不需要", "无需", "不许", "禁止",
                "不关闭", "不关掉", "不开启", "不打开", "不启用", "不显示", "不隐藏", "不停用",
                "不停止", "不取消", "并非关闭", "并非开启", "并非打开", "并非启用",
                "不要开启", "别开启", "不要打开", "别打开", "不要启用", "别启用",
                "不要显示", "别显示", "不要恢复", "别恢复", "先别", "暂时别", "暂不", "以后再",
                "没有开启", "没开启", "没有打开", "没打开", "没有关闭", "没关闭", "没有停用", "没停用",
                "如果", "假如", "要是", "除非", "等到", "否则", "但不要", "而不是",
                "日志里", "日志中", "日志写", "日志显示", "上面写", "报告里", "报错里",
                "他说", "她说", "有人说", "原话", "引用", "转述", "例如", "比如", "假设",
                "写着‘", "写着“", "写着\"", "看到‘", "看到“", "看到\"");
        }

        private static bool HasToggleCue(string input)
        {
            if (ContainsAny(input, "打开", "开启", "开一下", "关掉", "关闭", "关一下", "显示", "隐藏", "不要",
                "别再", "别让", "停止", "取消", "太吵", "别打扰", "别主动"))
                return true;
            if (ContainsAny(input, "怎么", "如何", "为什么", "是什么", "能不能", "可以吗", "可不可以", "?", "？"))
                return false;
            return ContainsAny(input, "开", "关");
        }

        private static bool ContainsAny(string text, params string[] values)
        {
            if (string.IsNullOrEmpty(text) || values == null) return false;
            foreach (string value in values)
                if (!string.IsNullOrEmpty(value) && text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }
    }
}
