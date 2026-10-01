# 实机 E2E 数据保护与证据闭环

本流程用于 143 项全 Mod 实机验收。它解决两个必须同时满足的终态：原 `SaveGames` / `TaiWu-JianghuYouling-Logs` 被逐文件恢复；测试备份副本已经删除。最终验证只依赖不含文件内容的不可变清单，不依赖仍然存在的备份树。

## 1. 建立备份

先完全退出太吾绘卷。脚本会同时检查前端 `The Scroll Of Taiwu` 和后端 `GameData`，任一仍在运行都会封闭失败。会话必须位于明确的父目录中，不能和游戏目录重叠，也不能经过 junction、符号链接或其他 reparse point。

```powershell
$repo = (Get-Location).Path # 在克隆的仓库根目录运行
$parent = Join-Path $repo '.e2e-results'
$runId = 'real-game-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
$session = Join-Path $parent $runId
New-Item -ItemType Directory -Force -Path $parent | Out-Null

powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  (Join-Path $repo 'tools\e2e\protect-real-game-data.ps1') `
  -Mode Backup -SessionRoot $session -AllowedBackupParent $parent

$backupManifest = Join-Path $session 'backup-manifest.json'
$backupManifestSha = (Get-FileHash -LiteralPath $backupManifest -Algorithm SHA256).Hash
```

必须把 `$backupManifestSha` 保留在当前自动化变量或受控发布记录中；恢复时强制传回，不能从已可能被替换的同一文件重新“计算预期值”。备份阶段会对源树做前后两次快照，对目标做一次完整读回；任何文件变化、锁定、路径穿越、reparse point 或哈希不一致都会失败并保留副本供人工排障。

随后把逐项 `PENDING` 模板放入同一会话目录，并把该目录下 `command-output` 作为本轮全部命令输出的唯一归档根：

```powershell
New-Item -ItemType Directory -Force -Path (Join-Path $session 'command-output') | Out-Null
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  (Join-Path $repo 'tools\new-real-game-e2e-manifest.ps1') `
  -OutputPath (Join-Path $session 'real-game-e2e.json')
```

## 2. 执行 143 项实机验收

所有实机日志、截图索引、性能汇总、部署哈希、测试报告和故障注入记录都写入 `$session\command-output` 或其子目录。证据文件本身不得含 key。逐项 evidence 仍需在主 manifest 中记录独立的文件 SHA-256、精确 locator、观察结果和 UTC 时间。

测试期聊天导出仍保留在游戏的 `TaiWu-JianghuYouling-Logs\...\Exports` 中；不要在恢复前手工删除。恢复脚本会在交换 ModLogs 之前，把当轮所有 `Exports` 目录原子复制并读回到 `$session\chat-exports\root-xx`。因此测试导出即使含有凭据，也不会因 E2E-137 恢复原数据而从秘密扫描中消失。

本轮不得把 LLM/TTS/Minimax 凭据轮换为不同的值。恢复脚本会分别在内存中解开测试树和备份树的 main/bak/tmp DPAPI 配置并比较 exact credential 集合；集合不同就会在交换任何目录前失败。这样恢复后的最终扫描所用 exact key 集合必然覆盖测试期使用的 key，不会出现“测试 key 随恢复消失、泄漏却无法再检出”的缺口。

## 3. 恢复并删除备份副本

再次完全退出太吾前后端，然后运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  (Join-Path $repo 'tools\e2e\protect-real-game-data.ps1') `
  -Mode Restore -SessionRoot $session -AllowedBackupParent $parent `
  -BackupManifestPath $backupManifest `
  -ExpectedBackupManifestSha256 $backupManifestSha `
  -RestoreReportPath (Join-Path $session 'restore-verification.json')
```

恢复顺序固定为：验证清单和全部备份文件 → 在游戏目录同卷建立完整 staging → 读回 staging → 原目录移入隔离名 → staging 交换为权威目录 → 逐文件重算 → 删除隔离目录 → 删除 `$session\backup-data` → 再次重算 → 原子写恢复报告。失败时绝不能手工把报告改成 `PASS`；只要 `backup-data` 尚在，就应保留现场并修复原因后重新执行。

成功终态必须同时满足：

- `SaveGames` 与清单的文件、空目录、长度和 SHA-256 全部一致；
- `TaiWu-JianghuYouling-Logs` 与清单全部一致；
- `$session\backup-data` 不存在；
- staging、testdata quarantine、copy/write 临时文件均不存在；
- `$session\chat-exports` 保留的是测试期导出证据，不是用户数据备份副本；
- `backup-manifest.json` 与 `restore-verification.json` 仍存在且哈希写入主 manifest。

## 4. 生成真实六范围秘密报告

恢复成功后运行。`ChatExportRoots` 必须传入恢复脚本保全的每个 `root-xx`，不能改传一个人为挑选的空目录。最终验证器会重新枚举该目录下的全部直接子目录。

```powershell
$exports = @(Get-ChildItem -LiteralPath (Join-Path $session 'chat-exports') -Directory |
  Sort-Object FullName | ForEach-Object FullName)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  (Join-Path $repo 'tools\e2e\new-secret-scan-report.ps1') `
  -OutputPath (Join-Path $session 'secret-scan.json') `
  -RepositoryRoot $repo `
  -CommandOutputRoot (Join-Path $session 'command-output') `
  -PlayerLogPath "$env:USERPROFILE\AppData\LocalLow\Conchship\The Scroll of Taiwu\Player.log" `
  -LlmMetricsPath "$env:LOCALAPPDATA\JianghuYouling\llm_metrics.jsonl" `
  -ChatExportRoots $exports
```

`-PlayerLogPath` 与 `-LlmMetricsPath` 指向当前文件；生成器和最终独立验证器还会自动纳入同目录的 `Player-prev.log` 与 `llm_metrics.jsonl.1`（存在时），因此游戏重启或指标轮转不会把上一份敏感输出移出扫描范围。

生成器会读取并严格验证 `Settings\llm.json`、`tts.json`、`minimax.json` 的 main/bak/tmp，使用 Windows DPAPI CurrentUser 只在内存中取得所有有效 exact credential；任何损坏、副本歧义、明文降级、无法解密或过短值都会失败。六范围为：当前 repository 文件、全部可达 Git Blob、当轮 command-output、当轮 Player.log、LLM metrics、保全的测试聊天导出。报告只保存输入集合、长度、SHA-256、计数和零发现结论。

## 5. 最终只读门

补齐主 manifest 的所有 143 项和三个报告哈希后执行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  (Join-Path $repo 'tools\verify-real-game-e2e-evidence.ps1') `
  -ManifestPath (Join-Path $session 'real-game-e2e.json')
```

最终门会独立重读 DPAPI exact key、重枚举六范围、重扫 Git Blob 和全部文件，并根据备份清单逐行核对已恢复的两个源树。只声明 `scopes` 名称、伪造总计、遗漏一个导出目录、保留备份副本或在恢复后修改任一文件，都不能通过。
