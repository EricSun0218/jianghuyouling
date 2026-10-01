using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Steamworks;
using UnityEngine;

namespace JianghuYouling
{
    /// <summary>
    /// 进入存档后只向 Steam 查询一次当前创意工坊条目的长描述，并读取随每次发布同步的
    /// 机器可读版本行。太吾 Mod 不会在运行中的订阅目录里自动换 DLL，因此发现不一致时
    /// 明确提示重新订阅；本地 Source=0 开发包不联网、不误报。
    /// </summary>
    internal static class WorkshopUpdateChecker
    {
        internal const string WorkshopVersionMarker = "【当前版本】";
        private const int MaxConfigBytes = 2 * 1024 * 1024;
        private static readonly Regex VersionRegex = new Regex(
            "(?m)^\\s*Version\\s*=\\s*[\"'](?<v>[^\"']+)[\"']\\s*,?\\s*$",
            RegexOptions.CultureInvariant);
        private static readonly Regex SourceRegex = new Regex(
            @"(?m)^\s*Source\s*=\s*(?<v>\d+)\s*,?\s*$",
            RegexOptions.CultureInvariant);
        private static readonly Regex FileIdRegex = new Regex(
            @"(?m)^\s*FileId\s*=\s*(?<v>\d+)\s*,?\s*$",
            RegexOptions.CultureInvariant);

        private static bool _started;
        private static UGCQueryHandle_t _queryHandle = UGCQueryHandle_t.Invalid;
        private static CallResult<SteamUGCQueryCompleted_t> _callResult;

        internal static void CheckOnceAfterWorldReady()
        {
            if (_started) return;
            _started = true;

            try
            {
                if (!TryReadInstalledConfig(out string localVersion, out ulong fileId,
                        out int source, out string error))
                {
                    Debug.LogWarning("[江湖有灵] 创意工坊版本检查跳过:" + error);
                    return;
                }
                if (source != 1 || fileId == 0)
                {
                    Debug.Log("[江湖有灵] 本地开发包不执行创意工坊版本检查");
                    return;
                }
                if (!SteamAPI.IsSteamRunning())
                {
                    Debug.LogWarning("[江湖有灵] Steam 尚未初始化，跳过本次版本检查");
                    return;
                }

                var ids = new[] { new PublishedFileId_t(fileId) };
                _queryHandle = SteamUGC.CreateQueryUGCDetailsRequest(ids, 1u);
                if (_queryHandle == UGCQueryHandle_t.Invalid)
                {
                    Debug.LogWarning("[江湖有灵] 无法建立创意工坊版本查询");
                    return;
                }
                SteamUGC.SetReturnLongDescription(_queryHandle, true);
                SteamUGC.SetAllowCachedResponse(_queryHandle, 0u);
                SteamAPICall_t call = SteamUGC.SendQueryUGCRequest(_queryHandle);
                _callResult = CallResult<SteamUGCQueryCompleted_t>.Create();
                _callResult.Set(call, (result, ioFailure) =>
                    CompleteQuery(result, ioFailure, localVersion, fileId));
            }
            catch (Exception e)
            {
                ReleaseQuery();
                Debug.LogWarning("[江湖有灵] 创意工坊版本检查异常:" + e.GetType().Name);
            }
        }

        private static void CompleteQuery(SteamUGCQueryCompleted_t result, bool ioFailure,
            string localVersion, ulong expectedFileId)
        {
            try
            {
                if (ioFailure || result.m_eResult != EResult.k_EResultOK
                    || result.m_unNumResultsReturned == 0)
                {
                    Debug.LogWarning("[江湖有灵] 创意工坊版本查询未成功，不影响继续游戏");
                    return;
                }
                if (!SteamUGC.GetQueryUGCResult(_queryHandle, 0u, out SteamUGCDetails_t details)
                    || details.m_nPublishedFileId.m_PublishedFileId != expectedFileId)
                {
                    Debug.LogWarning("[江湖有灵] 创意工坊版本查询返回了无效条目");
                    return;
                }

                string remoteVersion = ExtractWorkshopVersion(details.m_rgchDescription);
                if (string.IsNullOrWhiteSpace(remoteVersion))
                {
                    Debug.LogWarning("[江湖有灵] 创意工坊介绍未提供版本标记，不影响继续游戏");
                    return;
                }
                if (string.Equals(localVersion.Trim(), remoteVersion.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    Debug.Log("[江湖有灵] 创意工坊版本一致:" + localVersion);
                    return;
                }

                CommonUtils.ShowDialog("江湖有灵需要更新",
                    "检测到本地版本 " + localVersion + " 与创意工坊版本 "
                    + remoteVersion + " 不一致。\n\n需要重新订阅，《太吾绘卷》的 Mod 不支持自动更新。");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[江湖有灵] 创意工坊版本结果处理异常:" + e.GetType().Name);
            }
            finally
            {
                ReleaseQuery();
            }
        }

        internal static string ExtractWorkshopVersion(string description)
        {
            if (string.IsNullOrWhiteSpace(description)) return null;
            int start = description.IndexOf(WorkshopVersionMarker, StringComparison.Ordinal);
            if (start < 0) return null;
            start += WorkshopVersionMarker.Length;
            int end = start;
            while (end < description.Length)
            {
                char c = description[end];
                if (!(char.IsDigit(c) || c == '.')) break;
                end++;
            }
            string value = description.Substring(start, end - start).Trim();
            return System.Version.TryParse(value, out _) ? value : null;
        }

        private static bool TryReadInstalledConfig(out string version, out ulong fileId,
            out int source, out string error)
        {
            version = null;
            fileId = 0;
            source = 0;
            error = null;
            try
            {
                // ModManager has already parsed and validated the enabled Config.lua before a
                // plugin can run.  Prefer that authoritative in-memory identity.  Assembly.Location
                // may be empty for byte-loaded plugins and some 1.0.77 installations expose a
                // normalized/relative DirectoryName, neither of which should disable the check.
                if (TryReadLoadedModIdentity(out version, out fileId, out source)) return true;

                string root = TryResolveInstalledModRoot();
                string path = string.IsNullOrWhiteSpace(root) ? null
                    : Path.Combine(root, "Config.lua");
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                { error = "找不到当前 Mod 的 Config.lua"; return false; }
                var info = new FileInfo(path);
                if (info.Length <= 0 || info.Length > MaxConfigBytes)
                { error = "Config.lua 大小异常"; return false; }
                string text = File.ReadAllText(path);
                Match versionMatch = VersionRegex.Match(text);
                Match sourceMatch = SourceRegex.Match(text);
                Match fileIdMatch = FileIdRegex.Match(text);
                if (!versionMatch.Success || !System.Version.TryParse(versionMatch.Groups["v"].Value,
                        out _))
                { error = "Config.lua 版本无效"; return false; }
                if (!sourceMatch.Success || !int.TryParse(sourceMatch.Groups["v"].Value,
                        out source))
                { error = "Config.lua 来源无效"; return false; }
                if (!fileIdMatch.Success || !ulong.TryParse(fileIdMatch.Groups["v"].Value,
                        out fileId))
                { error = "Config.lua 创意工坊编号无效"; return false; }
                version = versionMatch.Groups["v"].Value.Trim();
                return true;
            }
            catch (Exception e)
            {
                error = e.GetType().Name;
                return false;
            }
        }

        private static bool TryReadLoadedModIdentity(out string version, out ulong fileId,
            out int source)
        {
            version = null;
            fileId = 0;
            source = 0;
            try
            {
                if (ModManager.EnabledMods == null) return false;
                foreach (var modId in ModManager.EnabledMods)
                {
                    var info = ModManager.GetModInfo(modId);
                    if (!IsCurrentMod(modId, info)) continue;
                    string parsedVersion = info?.GetVersionString();
                    if (info == null || !System.Version.TryParse(parsedVersion, out _)) return false;
                    version = parsedVersion;
                    fileId = info.ModId.FileId;
                    source = info.ModId.Source;
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static string TryResolveInstalledModRoot()
        {
            // Unity 从字节流载入 Mod DLL 时 Assembly.Location 可能为空；直接交给
            // Path.GetDirectoryName 会抛 ArgumentException。先走本体 ModManager 已解析好的
            // DirectoryName，再保留普通磁盘加载的兼容回退。
            try
            {
                if (ModManager.EnabledMods != null)
                {
                    foreach (var modId in ModManager.EnabledMods)
                    {
                        var info = ModManager.GetModInfo(modId);
                        if (info == null
                            || !IsCurrentMod(modId, info)
                            || string.IsNullOrWhiteSpace(info.DirectoryName)) continue;
                        string candidate = Path.GetFullPath(info.DirectoryName);
                        if (File.Exists(Path.Combine(candidate, "Config.lua"))) return candidate;
                    }
                }
            }
            catch { }

            try
            {
                string assemblyPath = Assembly.GetExecutingAssembly().Location;
                if (string.IsNullOrWhiteSpace(assemblyPath)) return null;
                string plugins = Path.GetDirectoryName(assemblyPath);
                if (string.IsNullOrWhiteSpace(plugins)) return null;
                return Directory.GetParent(plugins)?.FullName;
            }
            catch { return null; }
        }

        private static bool IsCurrentMod(GameData.Domains.Mod.ModId modId,
            FrameWork.ModSystem.ModInfoWithDisplayData info)
        {
            if (modId.FileId == 3747674580UL || modId.FileId == 3764815892UL) return true;
            if (info == null) return false;

            if (ContainsFrontendPlugin(info.FrontendPlugins)
                || ContainsFrontendPlugin(info.FrontendPluginsLegacy)) return true;

            // Local installs receive a temporary FileId, and some Mod managers normalize the
            // plugin list after loading. Config.lua has already been parsed by the game here, so
            // title + author is the remaining stable identity for that installation form.
            bool knownTitle = string.Equals(info.Title, "江湖有灵（太吾AI NPC互动）",
                    StringComparison.Ordinal)
                || string.Equals(info.Title, "江湖有灵测试版", StringComparison.Ordinal);
            return knownTitle
                && string.Equals(info.Author, "EricSun0218", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsFrontendPlugin(System.Collections.Generic.List<string> plugins)
        {
            if (plugins == null) return false;
            return plugins.Exists(plugin =>
            {
                if (string.IsNullOrWhiteSpace(plugin)) return false;
                string normalized = plugin.Trim().Replace('\\', '/');
                int separator = normalized.LastIndexOf('/');
                string fileName = separator >= 0 ? normalized.Substring(separator + 1) : normalized;
                return string.Equals(fileName, "JianghuYouling.Frontend.dll",
                    StringComparison.OrdinalIgnoreCase);
            });
        }

        private static void ReleaseQuery()
        {
            try
            {
                if (_queryHandle != UGCQueryHandle_t.Invalid)
                    SteamUGC.ReleaseQueryUGCRequest(_queryHandle);
            }
            catch { }
            _queryHandle = UGCQueryHandle_t.Invalid;
            _callResult = null;
        }
    }
}
