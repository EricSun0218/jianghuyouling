using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using GameData.Domains.Mod;
using GameData.Serializer;
using GameData.Utilities;
using JianghuYouling.Core.Prompt;
using UnityEngine;

namespace JianghuYouling.Rpc
{
    /// <summary>
    /// 可选“青楼体系”集成。只调用对方公开的只读 ModDomain 方法；对方未安装、无记录、
    /// 返回损坏或跨世界数据时都静默降级，不阻断《江湖有灵》对话。
    /// </summary>
    internal static class JianghuBrothelContextReader
    {
        internal const string TargetModId = "8";
        internal const string OverviewMethod = "get_business_log_overview";
        private const float TimeoutSeconds = 1.5f;
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, CacheEntry> Cache =
            new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        private static int _unavailableGeneration = -1;
        private static int _loggedUnavailableGeneration = -1;

        private sealed class CacheEntry
        {
            public DateTime ExpiresUtc;
            public string Context;
        }

        internal static void ResetForWorldExit()
        {
            lock (Gate)
            {
                Cache.Clear();
                _unavailableGeneration = -1;
                _loggedUnavailableGeneration = -1;
            }
        }

        internal static IEnumerator Fetch(int npcId, Action<string> completed)
        {
            int generation = WorldLifecycle.Generation;
            uint worldId = WorldLifecycle.WorldId;
            if (npcId < 0 || worldId == 0 || !WorldLifecycle.IsSameWorld(generation))
            {
                completed?.Invoke(null);
                yield break;
            }

            // 这是可选适配，不能用一次必然失败的 ModDomain 调用来探测安装状态：
            // 未安装/未启用时本体会把“方法不存在”记成红色 Backend Warning。
            // GetLoadedModInfoList 是 b24769549 本体提供的已加载清单；只有目标后端
            // 程序集确实在清单中时才允许调用它公开的只读方法。
            if (!IsTargetBackendLoaded())
            {
                completed?.Invoke(null);
                yield break;
            }

            string key = generation + ":" + worldId + ":" + npcId;
            bool unavailable;
            bool hasCached;
            string cachedContext = null;
            lock (Gate)
            {
                unavailable = _unavailableGeneration == generation;
                hasCached = Cache.TryGetValue(key, out CacheEntry cached)
                    && cached.ExpiresUtc > DateTime.UtcNow;
                if (hasCached) cachedContext = cached.Context;
            }
            if (unavailable) { completed?.Invoke(null); yield break; }
            if (hasCached) { completed?.Invoke(cachedContext); yield break; }

            bool callbackAccepted = true;
            bool done = false;
            bool success = false;
            int responseNpcId = -1;
            int recordCount = 0;
            string responseWorldKey = null;
            string overview = null;
            try
            {
                var param = new SerializableModData();
                param.Set("employee_character_id", npcId);
                param.Set("limit", JianghuBrothelContextPolicy.MaxRecords);
                ModDomainMethod.AsyncCall.CallModMethodWithParamAndRet(
                    null, TargetModId, OverviewMethod, param,
                    delegate (int offset, RawDataPool pool)
                    {
                        if (!callbackAccepted) return;
                        try
                        {
                            SerializableModData response = null;
                            Serializer.Deserialize(pool, offset, ref response);
                            response?.Get("success", out success);
                            response?.Get("employee_character_id", out responseNpcId);
                            response?.Get("record_count", out recordCount);
                            response?.Get("world_key", out responseWorldKey);
                            response?.Get("overview_text", out overview);
                        }
                        catch { success = false; }
                        finally { done = true; }
                    });
            }
            catch
            {
                MarkUnavailable(generation, "dispatch");
                completed?.Invoke(null);
                yield break;
            }

            float deadline = Time.realtimeSinceStartup + TimeoutSeconds;
            while (!done && Time.realtimeSinceStartup < deadline
                && WorldLifecycle.IsSameWorld(generation) && WorldLifecycle.WorldId == worldId)
                yield return null;
            callbackAccepted = false;

            if (!WorldLifecycle.IsSameWorld(generation) || WorldLifecycle.WorldId != worldId)
            {
                completed?.Invoke(null);
                yield break;
            }
            if (!done)
            {
                MarkUnavailable(generation, "timeout");
                completed?.Invoke(null);
                yield break;
            }

            JianghuBrothelContextPolicy.TryFormat(npcId, worldId, success,
                responseNpcId, recordCount, responseWorldKey, overview, out string context);
            lock (Gate)
                Cache[key] = new CacheEntry
                {
                    Context = context,
                    // 本地 RPC 很轻；短缓存只合并同一瞬间的重复构建，不掩盖当月新日志。
                    ExpiresUtc = DateTime.UtcNow.AddSeconds(3),
                };
            completed?.Invoke(context);
        }

        private static bool IsTargetBackendLoaded()
        {
            try
            {
                ModInfoList loaded = ModManager.GetLoadedModInfoList();
                if (loaded.Items == null) return false;
                foreach (ModInfo info in loaded.Items)
                {
                    if (ContainsTargetAssembly(info?.BackendPlugins)
                        || ContainsTargetAssembly(info?.BackendPluginsLegacy)) return true;
                }
            }
            catch { }
            return false;
        }

        private static bool ContainsTargetAssembly(IEnumerable<string> plugins)
        {
            if (plugins == null) return false;
            foreach (string plugin in plugins)
            {
                if (string.IsNullOrWhiteSpace(plugin)) continue;
                string name;
                try { name = Path.GetFileNameWithoutExtension(plugin.Trim()); }
                catch { continue; }
                if (string.Equals(name, "JianghuBrothel",
                    StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static void MarkUnavailable(int generation, string reason)
        {
            bool log;
            lock (Gate)
            {
                _unavailableGeneration = generation;
                log = _loggedUnavailableGeneration != generation;
                _loggedUnavailableGeneration = generation;
            }
            if (log)
                Debug.Log("[JHYL_BROTHEL_CONTEXT] optional integration unavailable reason=" + reason);
        }
    }
}
