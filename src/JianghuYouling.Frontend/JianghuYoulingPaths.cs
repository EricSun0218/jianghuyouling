using System;
using System.Collections.Generic;
using System.IO;
using FrameWork;
using JianghuYouling.Core.Persistence;
using JianghuYouling.Core.Tools;

namespace JianghuYouling
{
    /// <summary>本地数据落盘路径:游戏根/TaiWu-JianghuYouling-Logs/*(与 mod 目录隔离)。</summary>
    public static class JianghuYoulingPaths
    {
        private static readonly object WorldGate = new object();
        private static readonly HashSet<uint> PreparedWorlds = new HashSet<uint>();
        // fail-closed 终态(认领标记损坏/旧数据已归属他世界)进程内缓存:不给出口是设计,
        // 但不能让每次路径访问都重扫重抛刷日志;玩家已通过 LegacyDataNotice 收到指引。
        private static readonly HashSet<uint> ImportLockedWorlds = new HashSet<uint>();
        private static readonly HashSet<int> ClaimedPendingGenerations = new HashSet<int>();
        // 瞬时失败(文件被杀毒/备份工具短暂锁定等)保留重试语义,但加冷却避免热循环重扫。
        private static DateTime _importRetryHoldUntilUtc = DateTime.MinValue;
        private static bool _pendingSweepDone;
        // generation 会在每次游戏进程重新从 1 开始;若目录名只含 generation,上一进程遗留的
        // Pending...Gen_1 可能被下一次启动打开的另一个存档误认领。加入进程随机会话号后,
        // 身份送达前的临时数据只可能被创建它的这一进程/这一 world generation 领取。
        private static readonly string ProcessSessionId = Guid.NewGuid().ToString("N");
        // ACK outbox embeds its authoritative WorldId and must never be claimed from legacy/pending scopes.
        // (它不在 ScopedKinds 内;pre-identity 阶段也没有任何代码可写它 —— OperationAckOutboxFor 直连 world 路径。)
        private static readonly string[] ScopedKinds = { "Portraits", "Memories", "ChatLogs", "Intents", "Personas", "Events", "Novels", "Exports" };
        // JHYL_LEGACY_CLAIM_BY_TAIWU_ID:0.29 及更早的旧根目录文件都以固定前缀 + 所属太吾 id 命名;
        // 旧数据按文件名内嵌的太吾 id 归属存档,而不是"谁先打开归谁"。
        private static readonly string[] LegacyTaiwuFilePrefixes =
        {
            "ChatArchive_", "GroupArc_", "GroupTxn_", "Group_", "Chat_", "assistant_history_",
            "Memory_", "Portrait_", "Persona_", "Worldbook_", "IntentQueue_", "events_", "saga_",
        };
        private const string LegacyImportCompleteMarkerName = "legacy-import-complete.txt";

        public static string Root => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TaiWu-JianghuYouling-Logs");
        public static string Portraits => WorldSub("Portraits");
        public static string Memories => WorldSub("Memories");
        public static string ChatLogs => WorldSub("ChatLogs");
        public static string Settings => Sub("Settings");
        public static string Intents => WorldSub("Intents");
        public static string Personas => WorldSub("Personas");   // 玩家为队友亲手设定的人设
        public static string Events => WorldSub("Events");        // 过月「江湖纪事」事件存档
        public static string Novels => WorldSub("Novels");        // 当前人物生成的最新小说草稿
        public static string Exports => WorldSub("Exports");      // 玩家主动导出的聊天记录(Markdown)
        public static string OperationAckOutboxFor(uint worldId)
        {
            string path = OperationAckOutboxStore.BuildWorldIsolatedPath(Root, worldId);
            try { Directory.CreateDirectory(Path.GetDirectoryName(path)); } catch { }
            return path;
        }

        /// <summary>最新版权威 BasicGameData.WorldId;0 表示世界身份尚未送达前端。</summary>
        public static uint CurrentWorldId
        {
            get { return WorldLifecycle.WorldId; }
        }

        private static string WorldSub(string name)
        {
            uint worldId = CurrentWorldId;
            string worlds = Path.Combine(Root, "Worlds");
            // 身份通知前若确有早到读写,按本次 world generation 隔离;WorldId 到达后只迁入
            // 同一 generation 的正式目录,既不丢失,也不会让 A→B 的 pending 数据串进下个存档。
            string scope = Path.Combine(worlds, worldId > 0
                ? ("World_" + worldId)
                : PendingWorldScopeName(WorldLifecycle.Generation));
            if (worldId > 0) PrepareWorldScope(worldId, scope, worlds);
            var p = Path.Combine(scope, name);
            try { Directory.CreateDirectory(p); } catch { }
            return p;
        }

        private static void PrepareWorldScope(uint worldId, string scope, string worlds)
        {
            lock (WorldGate)
            {
                int generation = WorldLifecycle.Generation;
                bool pendingDone = ClaimedPendingGenerations.Contains(generation);
                bool rootDone = PreparedWorlds.Contains(worldId) || ImportLockedWorlds.Contains(worldId);
                if (pendingDone && rootDone) return;
                if (DateTime.UtcNow < _importRetryHoldUntilUtc) return;

                if (!pendingDone) ClaimPendingGenerationScope(generation, worlds, scope);
                if (!rootDone) ImportLegacyRootData(worldId, scope, worlds);
                SweepOrphanPendingScopesOnce(worlds);
            }
        }

        /// <summary>同进程同 generation 的身份前临时目录:逐文件容错复制;全部通过读回校验后
        /// 删除源目录(转移语义),避免 Worlds\ 下孤儿 Pending 目录累积。</summary>
        private static void ClaimPendingGenerationScope(int generation, string worlds, string scope)
        {
            try
            {
                string pending = Path.Combine(worlds, PendingWorldScopeName(generation));
                if (!Directory.Exists(pending)) { ClaimedPendingGenerations.Add(generation); return; }
                var failures = new ImportFailureLog();
                foreach (string kind in ScopedKinds)
                    CopyLegacyDirectory(Path.Combine(pending, kind), Path.Combine(scope, kind), 0, failures);
                if (failures.Count > 0)
                {
                    HoldImportRetry();
                    ReportImportFailures("pending-claim", failures);
                    return;   // 不标记本 generation;已复制文件因 target 已存在而天然幂等,稍后重试
                }
                TryDeleteDirectoryQuiet(pending);
                ClaimedPendingGenerations.Add(generation);
            }
            catch (Exception e)
            {
                HoldImportRetry();
                try { UnityEngine.Debug.LogWarning("[江湖有灵] 身份前临时数据认领失败:" + e.GetType().Name); } catch { }
            }
        }

        // v0.29:旧版把所有存档写在 Root/{kind}。0.30.0 曾以"第一个拿到有效 WorldId 的存档
        // 一次性认领全部旧数据"(Worlds/legacy-claimed-world.txt);该策略对"先开新档试新版、
        // 再回旧档"的常见动线会把旧档数据错误归属新档。现在:
        // - 已写下的全局认领标记继续有效:claimed==worldId 的世界保持旧的全量导入语义;
        // - 不再写入新的全局认领标记;改为按文件名内嵌太吾 id 归属(legacy-claim-taiwu-<id>.txt),
        //   当前存档只认领属于自己太吾的旧文件,同一批文件仍只认领一次;
        // - JHYL_LEGACY_IMPORT_COMPLETE_MARKER:一轮零失败的完整导入后,在世界目录内写入
        //   一次性 durable 完成标记;此后启动不再重扫旧目录 —— 0.30 中被"清空记录"、过月
        //   死者清理、连载 Clear 等正常删除的文件,不会再从旧根目录原件复活;
        // - JHYL_LEGACY_IMPORT_PER_FILE_TOLERANT:逐文件容错,单个文件被锁/损坏只跳过该文件,
        //   不再放弃其后全部文件与后续 kind;任何失败不写完成标记,带冷却自动重试,并通过
        //   LegacyDataNotice 给玩家可见提示。原文件永远保留供人工恢复。
        private static void ImportLegacyRootData(uint worldId, string scope, string worlds)
        {
            try
            {
                Directory.CreateDirectory(worlds);

                string completeMarker = Path.Combine(scope, LegacyImportCompleteMarkerName);
                if (HasDurableArtifacts(completeMarker))
                {
                    // 完成标记只在一轮零失败导入之后写;它的任何残迹(含写标记途中崩溃留下的
                    // tmp)都意味着导入曾完整成功,绝不能再重扫复制。
                    PreparedWorlds.Add(worldId);
                    return;
                }

                bool MarkerValid(string raw) => uint.TryParse((raw ?? "").Trim(), out uint value) && value > 0;
                string marker = Path.Combine(worlds, "legacy-claimed-world.txt");
                uint claimed = 0;
                if (DurableFileStore.TryReadRecoverableText(marker, 64, MarkerValid,
                    out string markerText, out bool markerArtifacts, out _))
                    uint.TryParse(markerText.Trim(), out claimed);
                else if (markerArtifacts)
                {
                    // fail-closed(拒绝猜测存档身份)不变,但要让玩家知道发生了什么、如何自救,
                    // 且本进程不再对每次路径访问重复重扫重抛。
                    ImportLockedWorlds.Add(worldId);
                    LegacyDataNotice.Post("legacy-claim-marker-corrupt",
                        "旧版数据的存档认领标记已损坏,旧数据暂不会自动导入本存档。\n"
                        + "请退出游戏后检查(或直接删除)此文件再重启:\n" + marker
                        + "\n删除标记后旧数据会按存档归属重新自动导入;旧数据文件本身不受影响。");
                    return;
                }

                var failures = new ImportFailureLog();
                if (claimed != 0 && claimed == worldId)
                {
                    // 0.30.0 已全局认领过的世界:保持原全量导入语义(含无太吾 id 的旧文件)。
                    CopyLegacyRootForWorld(scope, 0, failures);
                }
                else
                {
                    int taiwuId = CurrentTaiwuCharId();
                    if (taiwuId <= 0) return;   // 太吾身份未就绪:不冷却,下次路径访问再试

                    if (!LegacyRootHasTaiwuFiles(taiwuId))
                    {
                        // 旧目录中没有属于本存档太吾的文件 → 无可导入内容,本世界直接完成。
                        if (TryWriteCompletionMarker(completeMarker, worldId)) PreparedWorlds.Add(worldId);
                        else HoldImportRetry();
                        return;
                    }

                    string taiwuMarker = Path.Combine(worlds, "legacy-claim-taiwu-" + taiwuId + ".txt");
                    uint taiwuClaimed = 0;
                    if (DurableFileStore.TryReadRecoverableText(taiwuMarker, 64, MarkerValid,
                        out string taiwuMarkerText, out bool taiwuMarkerArtifacts, out _))
                        uint.TryParse(taiwuMarkerText.Trim(), out taiwuClaimed);
                    else if (taiwuMarkerArtifacts)
                    {
                        ImportLockedWorlds.Add(worldId);
                        LegacyDataNotice.Post("legacy-claim-taiwu-marker-corrupt-" + taiwuId,
                            "旧版数据的太吾认领标记已损坏,旧数据暂不会自动导入本存档。\n"
                            + "请退出游戏后检查(或直接删除)此文件再重启:\n" + taiwuMarker
                            + "\n删除标记后旧数据会重新自动导入;旧数据文件本身不受影响。");
                        return;
                    }
                    else
                    {
                        if (!DurableFileStore.TryWriteTextAtomic(taiwuMarker, worldId.ToString(), 64, MarkerValid))
                        {
                            HoldImportRetry();
                            return;
                        }
                        taiwuClaimed = worldId;
                    }
                    if (taiwuClaimed != worldId)
                    {
                        // 同一太吾 id 的旧文件已归属另一世界:拒绝重复导入(fail-closed),明确告知玩家。
                        ImportLockedWorlds.Add(worldId);
                        LegacyDataNotice.Post("legacy-claim-taiwu-owned-" + taiwuId,
                            "检测到旧版(0.29 及更早)数据已归属另一个存档,本存档不会自动导入,以免两档数据互相混淆。\n"
                            + "旧文件仍原样保留在 TaiWu-JianghuYouling-Logs 根目录下,如需恢复可手工复制。");
                        return;
                    }
                    CopyLegacyRootForWorld(scope, taiwuId, failures);
                }

                if (failures.Count > 0)
                {
                    HoldImportRetry();
                    ReportImportFailures("root-import", failures);
                    return;   // JHYL_LEGACY_IMPORT_RETRYABLE:不写完成标记,保留重试
                }
                // 零失败的完整一轮才允许写完成标记。
                if (TryWriteCompletionMarker(completeMarker, worldId)) PreparedWorlds.Add(worldId);
                else HoldImportRetry();
            }
            catch (Exception e)
            {
                HoldImportRetry();
                try { UnityEngine.Debug.LogWarning("[江湖有灵] 旧数据世界隔离迁移失败:" + e.GetType().Name); } catch { }
            }
        }

        /// <summary>taiwuFilter&gt;0 时只复制文件名内嵌该太吾 id 的旧文件;0 表示全量(全局认领世界/pending)。</summary>
        private static void CopyLegacyRootForWorld(string scope, int taiwuFilter, ImportFailureLog failures)
        {
            foreach (string kind in ScopedKinds)
                CopyLegacyDirectory(Path.Combine(Root, kind), Path.Combine(scope, kind), taiwuFilter, failures);
            // 旧版助手历史误放在全局 Settings,实际同样属于具体存档。
            string legacySettings = Path.Combine(Root, "Settings");
            string scopedChats = Path.Combine(scope, "ChatLogs");
            if (!Directory.Exists(legacySettings)) return;
            foreach (string file in Directory.GetFiles(legacySettings, "assistant_history*.json"))
            {
                if (taiwuFilter > 0 && ExtractLegacyTaiwuId(Path.GetFileName(file)) != taiwuFilter) continue;
                try
                {
                    Directory.CreateDirectory(scopedChats);
                    CopyFileDurableIfAbsent(file, Path.Combine(scopedChats, Path.GetFileName(file)));
                }
                catch (Exception e) { failures.Record(file, e); }
            }
        }

        private static bool LegacyRootHasTaiwuFiles(int taiwuId)
        {
            foreach (string kind in ScopedKinds)
            {
                string dir = Path.Combine(Root, kind);
                if (!Directory.Exists(dir)) continue;
                foreach (string file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    if (ExtractLegacyTaiwuId(Path.GetFileName(file)) == taiwuId) return true;
            }
            string legacySettings = Path.Combine(Root, "Settings");
            if (Directory.Exists(legacySettings))
                foreach (string file in Directory.GetFiles(legacySettings, "assistant_history*.json"))
                    if (ExtractLegacyTaiwuId(Path.GetFileName(file)) == taiwuId) return true;
            return false;
        }

        /// <summary>从旧文件名解析所属太吾 id:已知前缀后必须紧跟数字,数字后是 '_'、'.' 或结尾。解析不出返回 0。</summary>
        internal static int ExtractLegacyTaiwuId(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return 0;
            foreach (string prefix in LegacyTaiwuFilePrefixes)
            {
                if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                int start = prefix.Length, end = start;
                while (end < fileName.Length && fileName[end] >= '0' && fileName[end] <= '9') end++;
                if (end == start || end - start > 9) return 0;
                if (end < fileName.Length && fileName[end] != '_' && fileName[end] != '.') return 0;
                return int.TryParse(fileName.Substring(start, end - start), out int id) && id > 0 ? id : 0;
            }
            return 0;
        }

        private static int CurrentTaiwuCharId()
        {
            try { return SingletonObject.getInstance<BasicGameData>().TaiwuCharId; } catch { return 0; }
        }

        private static bool HasDurableArtifacts(string path)
        {
            try { return File.Exists(path) || File.Exists(path + ".tmp") || File.Exists(path + ".bak"); }
            catch { return true; }   // 无法检查时宁可当作已完成:重扫的代价是复活已删除文件
        }

        private static bool TryWriteCompletionMarker(string path, uint worldId)
            => DurableFileStore.TryWriteTextAtomic(path, worldId.ToString(), 64,
                raw => uint.TryParse((raw ?? "").Trim(), out uint value) && value > 0);

        private static void HoldImportRetry()
        {
            _importRetryHoldUntilUtc = DateTime.UtcNow.AddSeconds(60);
        }

        private sealed class ImportFailureLog
        {
            public int Count;
            public readonly List<string> Samples = new List<string>();
            public void Record(string file, Exception e)
            {
                Count++;
                if (Samples.Count < 6) Samples.Add(Path.GetFileName(file) + "(" + e.GetType().Name + ")");
            }
        }

        private static void ReportImportFailures(string stage, ImportFailureLog failures)
        {
            string samples = string.Join("、", failures.Samples.ToArray());
            try
            {
                UnityEngine.Debug.LogWarning("[江湖有灵] 旧数据迁移失败 " + failures.Count
                    + " 个文件(" + stage + "),稍后自动重试: " + samples);
            }
            catch { }
            LegacyDataNotice.Post("legacy-import-failures-" + stage,
                "旧版数据迁移有 " + failures.Count + " 个文件暂时未能导入,将自动重试。\n示例: " + samples
                + "\n若持续出现,请检查这些文件是否被杀毒或备份软件占用,详情见 Player.log。");
        }

        /// <summary>每进程一次:清理其它会话遗留的空 Pending 目录;非空目录留一条人工恢复指引日志。</summary>
        private static void SweepOrphanPendingScopesOnce(string worlds)
        {
            if (_pendingSweepDone) return;
            _pendingSweepDone = true;
            try
            {
                if (!Directory.Exists(worlds)) return;
                string own = "PendingWorldIdentity_Session_" + ProcessSessionId + "_";
                foreach (string dir in Directory.GetDirectories(worlds, "PendingWorldIdentity_*"))
                {
                    string name = Path.GetFileName(dir);
                    if (name.StartsWith(own, StringComparison.OrdinalIgnoreCase)) continue;   // 本进程自己的目录
                    bool empty;
                    try { empty = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length == 0; }
                    catch { continue; }
                    if (empty) TryDeleteDirectoryQuiet(dir);
                    else
                        try
                        {
                            UnityEngine.Debug.LogWarning("[江湖有灵] 发现上次运行遗留的身份前临时数据目录(不会被自动认领): "
                                + dir + " ;如需恢复其中文件请手工并入对应 Worlds\\World_<id> 子目录,确认无用可整目录删除。");
                        }
                        catch { }
                }
            }
            catch { }
        }

        private static void TryDeleteDirectoryQuiet(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }

        private static string PendingWorldScopeName(int generation)
            => "PendingWorldIdentity_Session_" + ProcessSessionId + "_Gen_" + generation;

        private static void CopyLegacyDirectory(string source, string destination, int taiwuFilter,
            ImportFailureLog failures)
        {
            if (!Directory.Exists(source)) return;
            Directory.CreateDirectory(destination);
            string prefix = source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                if (taiwuFilter > 0 && ExtractLegacyTaiwuId(Path.GetFileName(file)) != taiwuFilter) continue;
                string relative = file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? file.Substring(prefix.Length) : Path.GetFileName(file);
                string target = Path.Combine(destination, relative);
                try
                {
                    string parent = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                    // 逐文件容错:单个文件失败只跳过该文件并计入失败清单,其余文件与后续 kind 照常。
                    CopyFileDurableIfAbsent(file, target);
                }
                catch (Exception e) { failures.Record(file, e); }
            }
        }

        private static void CopyFileDurableIfAbsent(string source, string target)
        {
            if (File.Exists(target))
            {
                // 早先版本迁移未保留源文件时间戳,群聊「全部场次」等按时间排序的读取端会被
                // 拷贝瞬间的 mtime 打乱。对内容仍与源一致的已迁移文件做一次性时间戳补修
                // (0.30 已改写过的文件内容不再一致,绝不触碰)。
                TryRepairMigratedTimestamp(source, target);
                return;
            }
            var info = new FileInfo(source);
            if (!info.Exists || info.Length < 0 || info.Length > 64L * 1024 * 1024)
                throw new InvalidDataException("迁移源文件大小非法: " + source);
            string tmp = target + ".migration.tmp";
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.SequentialScan))
            using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920,
                FileOptions.WriteThrough))
            {
                input.CopyTo(output);
                output.Flush(true);
            }
            if (!FilesEqual(source, tmp)) throw new IOException("迁移暂存读回不一致: " + source);
            if (File.Exists(target)) File.Delete(tmp); else File.Move(tmp, target);
            if (!FilesEqual(source, target)) throw new IOException("迁移提交读回不一致: " + source);
            // 迁移是复制语义:目标保留源文件最后写入时间,按时间排序的读取端才不会乱序。
            try { File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source)); } catch { }
        }

        private static void TryRepairMigratedTimestamp(string source, string target)
        {
            try
            {
                DateTime src = File.GetLastWriteTimeUtc(source);
                DateTime dst = File.GetLastWriteTimeUtc(target);
                if (Math.Abs((dst - src).TotalSeconds) < 2) return;
                if (!FilesEqual(source, target)) return;
                File.SetLastWriteTimeUtc(target, src);
            }
            catch { }
        }

        private static bool FilesEqual(string left, string right)
        {
            try
            {
                var a = new FileInfo(left); var b = new FileInfo(right);
                if (!a.Exists || !b.Exists || a.Length != b.Length) return false;
                using (var x = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var y = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var xb = new byte[81920]; var yb = new byte[81920];
                    while (true)
                    {
                        int xn = x.Read(xb, 0, xb.Length); int yn = y.Read(yb, 0, yb.Length);
                        if (xn != yn) return false;
                        if (xn == 0) return true;
                        for (int i = 0; i < xn; i++) if (xb[i] != yb[i]) return false;
                    }
                }
            }
            catch { return false; }
        }

        private static string Sub(string name)
        {
            var p = Path.Combine(Root, name);
            try { Directory.CreateDirectory(p); } catch { }
            return p;
        }
    }
}
