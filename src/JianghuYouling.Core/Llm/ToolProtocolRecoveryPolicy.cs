using System;

namespace JianghuYouling.Core.Llm
{
    /// <summary>
    /// Classifies model-authored tool protocol failures that are safe to correct once.
    /// The caller owns the retry count and must never execute a rejected call.
    /// </summary>
    public static class ToolProtocolRecoveryPolicy
    {
        private enum FailureKind
        {
            None,
            UnknownTool,
            InvalidArguments,
            IncompleteArguments
        }

        public static bool IsRecoverable(string error)
        {
            return Classify(error) != FailureKind.None;
        }

        public static string BuildCorrectionPrompt(string error)
        {
            FailureKind kind = Classify(error);
            if (kind == FailureKind.IncompleteArguments)
                return "上一条工具调用没有形成完整 JSON 对象，因此那项动作不得执行。请重新决定本轮回应；"
                    + "若仍需调用工具，必须从头生成一个完整 JSON 对象，补齐全部 required 字段，"
                    + "不得续写、猜测或复用上一条残缺参数。若没有把握或无需继续行动，直接用角色口吻作答。";
            if (kind == FailureKind.InvalidArguments)
                return "上一条工具调用的参数没有通过本轮 schema，因此那项动作不得执行。只可使用工具定义列出的字段，"
                    + "补齐 required 字段，删除未定义字段，并严格使用要求的字符串、整数、布尔或枚举值；"
                    + "不得把解释文字塞进参数。若没有把握或无需继续行动，直接用角色口吻作答。";
            return "上一条工具调用使用了当前场景中不存在的名称，因此那项动作不得执行。"
                + "请严格从本轮已提供的工具中选择确切名称；不要申请、翻译或自造工具名。"
                + "若无需继续行动，直接用角色口吻作答。";
        }

        private static FailureKind Classify(string error)
        {
            if (string.IsNullOrWhiteSpace(error)) return FailureKind.None;
            if (error.IndexOf("工具参数不完整", StringComparison.Ordinal) >= 0)
                return FailureKind.IncompleteArguments;
            if (error.IndexOf("工具参数未通过 schema", StringComparison.OrdinalIgnoreCase) >= 0
                || error.IndexOf("工具参数不是合法", StringComparison.Ordinal) >= 0)
                return FailureKind.InvalidArguments;
            if (error.IndexOf("当前场景未提供的工具", StringComparison.Ordinal) >= 0)
                return FailureKind.UnknownTool;
            return FailureKind.None;
        }
    }
}
