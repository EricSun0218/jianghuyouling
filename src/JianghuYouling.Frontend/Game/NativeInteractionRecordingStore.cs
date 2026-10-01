using System.IO;
using System.Collections.Generic;
using System.Globalization;

namespace JianghuYouling
{
    /// <summary>
    /// 原版 NPC 互动记录开关。按存档世界和 NPC 分别保存，首次遇见与没有配置文件时默认关闭。
    /// 每个人物的值在运行期只读取磁盘一次，互动界面的刷新轮询不会反复访问文件系统。
    /// </summary>
    internal static class NativeInteractionRecordingStore
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, bool> Cache
            = new Dictionary<string, bool>();

        internal static bool IsEnabled(int npcId)
        {
            if (!TryIdentity(npcId, out uint worldId, out string key)) return false;
            lock (Gate)
            {
                if (Cache.TryGetValue(key, out bool cached)) return cached;
                bool enabled = Load(PathFor(worldId, npcId));
                Cache[key] = enabled;
                return enabled;
            }
        }

        internal static bool Save(int npcId, bool enabled)
        {
            if (!TryIdentity(npcId, out uint worldId, out string key)) return false;
            lock (Gate)
            {
                if (!DurableSettingsStore.Save(PathFor(worldId, npcId), enabled ? "1" : "0",
                    DurableSettingsStore.Small, DurableSettingsStore.IsLegacyBoolean)) return false;
                Cache[key] = enabled;
                return true;
            }
        }

        internal static bool ReplaceIdentity(int oldNpcId, int newNpcId)
        {
            if (oldNpcId <= 0 || newNpcId <= 0 || oldNpcId == newNpcId) return true;
            bool enabled = IsEnabled(oldNpcId);
            // 默认关闭无需制造新文件；开启状态必须可靠转交给副本。
            if (!enabled) return true;
            return Save(newNpcId, true);
        }

        internal static bool RemoveIdentity(int npcId)
        {
            if (!TryIdentity(npcId, out uint worldId, out string key)) return false;
            lock (Gate)
            {
                bool ok = JianghuYouling.Core.Persistence.DurableFileStore
                    .TryDeleteAllArtifacts(PathFor(worldId, npcId));
                Cache.Remove(key);
                return ok;
            }
        }

        private static bool Load(string path)
        {
            try
            {
                if (DurableSettingsStore.TryLoad(path, DurableSettingsStore.Small,
                    DurableSettingsStore.IsLegacyBoolean, out string raw))
                {
                    string value = raw.Trim().ToLowerInvariant();
                    return value == "1" || value == "on" || value == "true"
                        || value == "开" || value == "是" || value == "yes";
                }
            }
            catch { }
            return false;
        }

        private static bool TryIdentity(int npcId, out uint worldId, out string key)
        {
            worldId = WorldLifecycle.WorldId;
            key = null;
            if (!WorldLifecycle.HasWorldIdentity || worldId == 0 || npcId <= 0) return false;
            key = worldId.ToString(CultureInfo.InvariantCulture) + ":"
                + npcId.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        private static string PathFor(uint worldId, int npcId)
            => Path.Combine(JianghuYoulingPaths.Settings, "native_interaction_recording",
                worldId.ToString(CultureInfo.InvariantCulture) + "_"
                + npcId.ToString(CultureInfo.InvariantCulture) + ".txt");
    }
}
