using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using JianghuYouling.Core.Tools;
using JianghuYouling.Effects;
using JianghuYouling.Rpc;
using UnityEngine;

namespace JianghuYouling
{
    /// <summary>
    /// Cleanup-only receipt tracking for one-shot monthly work. No action arguments,
    /// story projections or retry instructions are persisted here.
    /// </summary>
    internal static class MonthlyOperationReceipts
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Func<bool>> Prepared =
            new Dictionary<string, Func<bool>>(StringComparer.Ordinal);
        private static int _cleaningGeneration = -1;

        internal static void Register(string operationId, Func<bool> current)
        {
            lock (Gate) Prepared[operationId] = current;
        }

        internal static Func<bool> Take(string operationId)
        {
            lock (Gate)
            {
                Prepared.TryGetValue(operationId, out Func<bool> current);
                Prepared.Remove(operationId);
                return current;
            }
        }

        internal static void Forget(string operationId)
        {
            lock (Gate) Prepared.Remove(operationId);
        }

        private static string PathFor(uint worldId)
            => Path.Combine(Path.GetDirectoryName(JianghuYoulingPaths.OperationAckOutboxFor(worldId)),
                "monthly_receipt_cleanup.json");

        internal static bool Remember(uint worldId, int taiwuId, string operationId)
            => OperationAckOutboxStore.TryEnqueue(PathFor(worldId), worldId, taiwuId,
                operationId, out _);

        internal static void Acknowledge(uint worldId, int taiwuId, string operationId)
        {
            // Transfer responsibility only after the existing ACK-only outbox is durable.
            if (OperationRpcClient.AcknowledgeExistingReceipt(worldId, taiwuId, operationId, null)
                && !OperationAckOutboxStore.TryRemoveAcknowledged(PathFor(worldId),
                    worldId, taiwuId, operationId, out _))
                Debug.LogWarning("[JHYL_MONTHLY_RECEIPT_CLEANUP_SAVE_FAILED] op=" + operationId);
        }

        internal static void CleanUp()
        {
            if (!WorldLifecycle.HasWorldIdentity) return;
            int generation = WorldLifecycle.Generation;
            if (_cleaningGeneration == generation) return;
            MonoBehaviour host = TalkEntryHost.Instance;
            if (host == null) host = ConfigHost.Instance;
            if (host == null) return;
            _cleaningGeneration = generation;
            host.StartCoroutine(CleanUp(generation, WorldLifecycle.WorldId));
        }

        private static IEnumerator CleanUp(int generation, uint worldId)
        {
            try
            {
                var snapshot = OperationAckOutboxStore.Load(PathFor(worldId), worldId);
                if (!snapshot.Reliable)
                {
                    Debug.LogWarning("[JHYL_MONTHLY_RECEIPT_CLEANUP_UNAVAILABLE] world=" + worldId);
                    yield break;
                }
                foreach (var entry in snapshot.Entries)
                {
                    if (!WorldLifecycle.IsSameWorld(generation)) yield break;
                    if (entry == null || entry.WorldId != worldId || entry.TaiwuId <= 0) continue;
                    ToolOutcome outcome = null;
                    bool done = false;
                    EffectHandler.QueryOperation(worldId, entry.TaiwuId, entry.OperationId,
                        value => { outcome = value; done = true; });
                    float deadline = Time.unscaledTime + 8f;
                    while (!done && Time.unscaledTime < deadline
                        && WorldLifecycle.IsSameWorld(generation)) yield return null;
                    if (!WorldLifecycle.IsSameWorld(generation)) yield break;
                    if (outcome != null && outcome.IsTerminal
                        && outcome.OperationId == entry.OperationId
                        && !string.IsNullOrWhiteSpace(outcome.Receipt))
                        Acknowledge(worldId, entry.TaiwuId, entry.OperationId);
                    // Unknown remains unknown. Never dispatch, project text or block a month.
                }
            }
            finally
            {
                if (_cleaningGeneration == generation) _cleaningGeneration = -1;
            }
        }
    }
}
