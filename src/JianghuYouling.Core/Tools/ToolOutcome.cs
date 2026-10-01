namespace JianghuYouling.Core.Tools
{
    /// <summary>
    /// Agent 与执行器之间的统一结果契约。Succeeded/Failed 是终态；Pending/Unknown 不得
    /// 叙述成成功或失败，也不得用新的 operationId 自动重发副作用。
    /// </summary>
    public sealed class ToolOutcome
    {
        public string Status { get; set; }
        public string Code { get; set; }
        public bool Retryable { get; set; }
        public string OperationId { get; set; }
        public string Receipt { get; set; }
        public string Message { get; set; }

        public bool IsSucceeded => Status == "succeeded";
        public bool IsTerminal => Status == "succeeded" || Status == "failed" || Status == "rejected" || Status == "canceled"
            || (Status == "unknown" && !Retryable);
        public bool IsUnconfirmed => Status == "pending" || (Status == "unknown" && Retryable);

        public static ToolOutcome Succeeded(string operationId, string receipt, string message = null)
            => new ToolOutcome { Status = "succeeded", Code = "OK", Retryable = false, OperationId = operationId, Receipt = receipt, Message = message };

        public static ToolOutcome Failed(string code, string operationId, string receipt, string message, bool retryable = false)
            => new ToolOutcome { Status = "failed", Code = code ?? "FAILED", Retryable = retryable, OperationId = operationId, Receipt = receipt, Message = message };

        public static ToolOutcome Unknown(string operationId, string message)
            => new ToolOutcome { Status = "unknown", Code = "CALLBACK_TIMEOUT", Retryable = true, OperationId = operationId, Receipt = null, Message = message };
    }
}
