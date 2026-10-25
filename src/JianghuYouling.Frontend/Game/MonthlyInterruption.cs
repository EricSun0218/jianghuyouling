using System;
using System.Collections.Generic;
using System.Threading;
using JianghuYouling.Core.Prompt;
using Newtonsoft.Json;

namespace JianghuYouling
{
    public sealed class CompanionMonthlyResult
    {
        public int NpcId;
        public string Name;
        public string LocationText;
        public string Summary;
        public string Detail;
        public string StopReason;
        public readonly List<string> Outcomes = new List<string>();
        public readonly List<string> ActionTools = new List<string>();
        [JsonIgnore]
        public readonly List<StoryProjectionReceipt> StoryReceipts = new List<StoryProjectionReceipt>();
        // Authority comes from the game-owned scene, never from a receipt's own participants.
        [JsonIgnore]
        internal readonly HashSet<int> AuthorizedParticipantIds = new HashSet<int>();
    }

    // Kept by the current run's closures only. This is a reason, not a resumable task or journal.
    internal sealed class CompanionMonthlyStopState
    {
        private string _code;
        internal string Code => Volatile.Read(ref _code);
        internal void Stop(string code)
        {
            if (string.IsNullOrWhiteSpace(code) || code == "completed"
                || code == "completed_with_interruptions") return;
            Interlocked.CompareExchange(ref _code, code, null);
        }
    }

    internal static class MonthlyInterruption
    {
        internal static string Describe(string code)
        {
            switch (code)
            {
                case "location_changed": return "因太吾移动到另一地块，原地点的主动行事已中止。";
                case "location_unavailable": return "因无法继续确认当前所在地，主动行事已中止。";
                case "conversation_cleared": return "因你清空了该人物的聊天记录，基于旧记录的主动行事已中止。";
                case "native_month_advance": return "因你开始了下一次过月，上一轮尚未完成的主动行事已中止。";
                case "travel": return "因太吾进入旅行状态，尚未完成的主动行事已中止。";
                case "timeout": return "因主动行事长时间没有取得新进展，本批已超时中止。";
                case "operation_receipt_unknown":
                case "mutation_unknown": return "因行动执行回执未能确认，后续主动行事已停止；未知动作不会自动重试。";
                case "child_exception": return "因主动行事处理发生内部异常，本批未完成部分已中止。";
                case "world_exit": return "因退出或重新加载存档，本轮主动行事已中止。";
                default: return "因本轮主动行事任务已失效，未完成部分已中止。";
            }
        }

        internal static CompanionMonthlyResult Stop(CompanionMonthlyResult result, string reason)
        {
            if (result == null) return null;
            if (string.IsNullOrWhiteSpace(result.StopReason)) result.StopReason = reason;
            result.Summary = result.ActionTools.Count > 0
                ? "已完成部分行动，后续已中止。" : "尚无已确认完成的行动，本次已中止。";
            return result;
        }

        internal static string ModelFailure(string error, int elapsedMs, int timeoutSeconds)
        {
            // Do not copy provider bodies, request URLs or private configuration into archives/UI.
            string category = "未取得有效模型回复";
            string message = error ?? "";
            if (message.IndexOf("WebException", StringComparison.OrdinalIgnoreCase) >= 0)
                category = "HTTP 传输异常（WebException）";
            else if (message.Contains("401") || message.Contains("403")) category = "接口认证或访问权限失败";
            else if (message.Contains("429")) category = "接口限流或额度不足";
            else if (message.Contains("超时") || message.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0)
                category = "请求超时";
            else if (message.Contains("HTTP")) category = "HTTP 请求失败";
            string wait = timeoutSeconds > 0 && elapsedMs >= timeoutSeconds * 1000 - 1000
                ? "等待约 " + Math.Max(1, elapsedMs / 1000) + " 秒后" : "";
            return "模型请求" + wait + "失败：" + category + "。本轮未完成部分已停止，不会自动重试动作。";
        }
    }
}
