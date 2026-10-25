using System;

namespace JianghuYouling.Rpc
{
    public static class OperationRpcClient
    {
        public static Func<uint, int, string, bool> AcknowledgeForTest;
        public static bool AcknowledgeExistingReceipt(uint worldId, int taiwuId,
            string operationId, Action<bool> completed)
        {
            bool saved = AcknowledgeForTest(worldId, taiwuId, operationId);
            completed?.Invoke(saved);
            return saved;
        }
    }
}
