param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

function Read-Source([string]$relativePath) {
    $path = Join-Path $Root $relativePath
    if (-not (Test-Path -LiteralPath $path)) { throw "missing source: $relativePath" }
    return [IO.File]::ReadAllText($path, [Text.UTF8Encoding]::new($false, $true))
}

function Require-NotMatch([string]$text, [string]$pattern, [string]$label) {
    if ([Text.RegularExpressions.Regex]::IsMatch(
        $text, $pattern, [Text.RegularExpressions.RegexOptions]::Singleline)) {
        throw "contract violated (should be absent): $label"
    }
}
function Require-Match([string]$text, [string]$pattern, [string]$label) {
    if (-not [Text.RegularExpressions.Regex]::IsMatch(
        $text, $pattern, [Text.RegularExpressions.RegexOptions]::Singleline)) {
        throw "contract missing: $label"
    }
}

function Reject-Match([string]$text, [string]$pattern, [string]$label) {
    if ([Text.RegularExpressions.Regex]::IsMatch(
        $text, $pattern, [Text.RegularExpressions.RegexOptions]::Singleline)) {
        throw "forbidden contract present: $label"
    }
}

$talk = Read-Source 'src/JianghuYouling.Frontend/Talk/TalkOrchestrator.cs'
$group = Read-Source 'src/JianghuYouling.Frontend/Talk/GroupChatOrchestrator.cs'
$chat = Read-Source 'src/JianghuYouling.Frontend/UI/ChatWindow.cs'
$config = Read-Source 'src/JianghuYouling.Frontend/UI/ConfigWindow.cs'
$voice = Read-Source 'src/JianghuYouling.Frontend/UI/VoicePlayer.cs'
$dictation = Read-Source 'src/JianghuYouling.Frontend/UI/WindowsDictation.cs'
$worldLifecycle = Read-Source 'src/JianghuYouling.Frontend/Game/WorldLifecycle.cs'
$toolRegistry = Read-Source 'src/JianghuYouling.Core/Tools/ToolRegistry.cs'
$nativeGrooming = Read-Source 'src/JianghuYouling.Frontend/UI/NativeGroomingInteraction.cs'

# 调试暗号是玩家键入触发、LLM 不可达的单机作弊面(不是 LLM 信任边界),须在 Release 也
# 可用供作者实机测试;作者要求 /帮助 与 /列表 都列出全表。钉死它不被编译门排除。
Require-Match $talk 'if\s*\(!npcInitiated\s*&&\s*IsDebugCommandInput\(intentInput\)\)[\s\S]{0,360}if\s*\(GroupCtx\s*==\s*null\s*&&\s*!snap\.IsDead\s*&&\s*!Remote\)[\s\S]{0,160}HandleTestCommand\(snap, intentInput, onReply\);[\s\S]{0,220}yield break;' 'debug command dispatch remains player-only, local-single-only, never reaches the model and reads exact current-turn input'
Require-NotMatch $talk '#if\s+DEBUG\s+if\s*\(HandleTestCommand\(' 'debug command dispatch must not be DEBUG-gated'
Require-NotMatch $talk 'Release builds contain no executable cheat surface' 'debug command body must not be compiled out of Release'
Require-Match $chat 'JHYL_DEBUG_COMMAND_LOCAL_SINGLE_ONLY[\s\S]{0,900}if \(_assistantMode \|\| _groupMode \|\| _soulMode \|\| _remote\)' 'chat UI rejects slash commands outside a live face-to-face single chat before model dispatch'
Require-Match $chat 'string slashCommand = NormalizeSlashCommand\(text\);[\s\S]{0,900}Resend\(text\);' 'full-width slash commands normalize before local single-chat dispatch'

# 0.29 及更早写盘的单聊文档没有 Version 字段(仅 Summary/Turns),是真实玩家存档主体。
# 缺 Version 必须按 v1 语义接纳、首次保存再升级,绝不能 fail-closed 成"动作日志已损坏"。
Require-Match $talk 'JToken versionToken = raw\["Version"\] \?\? raw\["version"\];[\s\S]{0,160}if \(versionToken == null\)[\s\S]{0,420}version = 1;' 'legacy single-chat document without Version field is accepted as v1'

# A reaction is a commit state, not a boolean attempt flag. It remains durable so an
# unresolved callback cannot be duplicated, but it is optional and never gates prose.
Require-Match $talk 'enum ReactionCommitState[\s\S]*?NotAttempted[\s\S]*?Attempting[\s\S]*?Failed[\s\S]*?Unknown[\s\S]*?Succeeded' 'reaction state machine'
Reject-Match $talk '_reactionDone' 'legacy attempted-equals-succeeded flag'
Require-Match $talk 'JHYL_OPTIONAL_REACTION_NO_REPLY_GATE' 'optional reaction never gates prose'
Require-NotMatch $talk '模型未能可靠记录本轮即时反应，正文未提交' 'reaction failure cannot swallow prose'
Require-Match $talk 'bool optionalReaction = string\.Equals\(call\.Name, "record_reaction"[\s\S]*?if \(!optionalReaction && durableMutation\)' 'reaction excluded from gameplay action failure recovery'
Require-Match $talk 'ReactionStateFromPendingTurn[\s\S]*?status\s*==\s*"unknown"[\s\S]*?ReactionCommitState\.Unknown' 'unknown recovery barrier'
Require-Match $talk 'record_reaction[\s\S]*?existingStatus\s*==\s*"failed"[\s\S]*?continue;' 'known-failure retry'

# Parent exchange identity separates a crash retry from a later identical utterance.
Require-Match $talk 'public string CommittedExchangeId' 'pending parent exchange field'
Require-Match $talk 'JsonProperty\(NullValueHandling\s*=\s*NullValueHandling\.Ignore\)[\s\S]*?CommittedExchangeId' 'legacy integrity compatibility for pending parent marker'
Require-Match $talk '!string\.IsNullOrWhiteSpace\(turn\.CommittedExchangeId\)\) continue;' 'committed pending exclusion'
Require-Match $talk '_activePendingActionTurn\.CommittedExchangeId\s*=\s*exchangeId;[\s\S]*?SaveConv' 'atomic parent marker'

# Current authoritative presence determines the tool surface and all nominally local
# mutations participate in the per-dispatch presence guard.
Require-Match $talk 'yield return RecalculateRemoteFromAuthoritativePresence\(snap, ct\);[\s\S]*?new ToolContext' 'per-turn remote recalculation'
Require-Match $talk 'case "change_equipment":\s*physical\(npc\)' 'equipment presence guard'
Require-NotMatch $talk 'case "change_appearance":\s*physical\(npc\)' 'native grooming must not enter durable group presence guard'
Require-Match $talk 'CanOpenGrooming = !npcInitiated && !Remote && GroupCtx == null' 'grooming is player-initiated local single-chat only'
Require-Match $toolRegistry 'IsAutonomousToolEnabled[\s\S]*?change_appearance' 'autonomous appearance denylist'
Require-Match $nativeGrooming 'UIElement\.CharacterShave\.SetOnInitArgs[\s\S]*?UIManager\.Instance\.MaskUI\(UIElement\.CharacterShave\)' 'direct native CharacterShave view entry'
Require-NotMatch $nativeGrooming 'JumpToInteractionEventOption' 'grooming bypasses original interaction prerequisites'
Require-Match $talk 'case "change_caravan_favor":\s*physical\(npc\);\s*physical\(taiwu\)' 'caravan presence guard'

# Interjections are accepted only inside their exchange window and UI visibility follows
# durable acceptance rather than preceding it.
Require-Match $group 'Queue<PendingInterjection>' 'exchange-aware interjection queue'
Require-Match $group 'public bool Interject\(string text\)' 'interjection acceptance result'
Require-Match $group 'pending\.ExchangeId, exchangeId, StringComparison\.Ordinal' 'exchange-bound drain'
Require-Match $group 'BeginInterjectionWindow\(exchangeId\)' 'interjection receive phase'
Require-Match $group 'EndInterjectionExchange\(exchangeId\)' 'interjection cleanup'
Require-Match $chat 'if\s*\(!_group\.Interject\(text(?:,\s*out\s+committedLine)?\)\)[\s\S]*?return;[\s\S]*?ClearComposer\(false\);[\s\S]*?AppendPlayer\(text\)' 'UI displays only accepted interjection'
Require-Match $chat 'hasExchangeId[\s\S]*?replayExchangeId[\s\S]*?StringComparison\.Ordinal[\s\S]*?if\s*\(!hasExchangeId\)' 'group replay aggregates modern exchange ids and isolates legacy lines'
Require-Match $chat 'if\s*\(string\.IsNullOrEmpty\(cur\.playerInput\)\)\s*cur\.playerInput\s*=\s*l\.Text' 'group interjection is not a second retry round'
Require-Match $chat 'BindGroupVoiceFromLastLine\(Round r\)[\s\S]*?line\.IsTaiwu\s*\|\|[\s\S]*?r\.replyNpcId\s*=\s*line\.SpeakerId' 'group voice follows last non-Taiwu speaker'
Require-Match $chat 'FinalizeRound\(\)[\s\S]*?r\.groupLines\.Add\(tr\[i\]\);[\s\S]*?BindGroupVoiceFromLastLine\(r\)' 'new group round binds persisted speaker identity'

# Voice work belongs to a concrete round and all callbacks carry a tab/request epoch.
Require-Match $voice 'public static void Stop\(object owner\)' 'owner-scoped voice stop'
Require-Match $voice 'public static void Speak\(object owner' 'owner-scoped voice start'
Require-Match $chat 'voiceOwner[\s\S]*?voiceRequestVersion' 'round voice owner'
Require-Match $chat 'VoiceCallbackCurrent\(r, lifecycleVersion, requestVersion\)' 'voice callback epoch guard'
Require-Match $chat 'ResetForWorldExit\(\)[\s\S]*?VoicePlayer\.Stop\(\)' 'world voice stop'
Require-Match $worldLifecycle 'StructuredWorldBookWindow\.ResetForWorldExit\(\)' 'structured worldbook world reset'
Require-Match $chat 'void ClearLog\(\)\s*\{\s*[\s\S]*?InvalidateAllVoice\(\);\s*InvalidateAllImages\(\);\s*if \(_content == null\) return;' 'clear stops tab voice and image work before destroying rows'
Require-Match $voice 'TryPlanSpeech\(provider, text,[\s\S]*?for \(int index = 0; index < chunks\.Count; index\+\+\)' 'voice plan is bounded before chunk dispatch'
Require-Match $voice 'TryGetNextRequestTimeout\(out int requestTimeoutMs\)[\s\S]*?CreateLinkedTokenSource' 'voice chunks share an overall synthesis deadline'
Require-Match $voice 'BuildTtsCacheScope\(provider, baseUrl, apiKey, configRevision\)' 'voice cache uses endpoint account and config identity'

# Batch rollback restores disk-backed runtime projections as well as files. Face path
# previews are explicitly invalidated because the widget caches the path it loaded.
Require-Match $config 'ReloadConversationTextRuntimeFromDisk\(\)[\s\S]*?TaiwuVoice\s*=\s*TaiwuVoiceStore\.Load\(\)' 'conversation runtime reload helper'
Require-Match $config 'System\.Action<string> abort[\s\S]*?transaction\.Rollback\(\)[\s\S]*?ReloadConversationTextRuntimeFromDisk\(\);[\s\S]*?Prefill\(\)[\s\S]*?AssistantWidget\.NotifyFacePathChanged\(\)' 'batch abort restores runtime and face projection'

# The production async state machine stays on Unity's synchronization context so each
# key phase can re-check the exact TMP target. DevTest executes this same state machine.
Reject-Match $dictation 'Thread\.Sleep' 'uncancellable dictation wait'
Reject-Match $dictation 'Task\.Run\(' 'dictation focus checks never run against Unity objects on a worker thread'
Require-Match $dictation 'TryCaptureFocusedInput\(out TMP_InputField targetInput[\s\S]*?InputStillOwnsFocus\(targetInput\)' 'dictation captures one exact TMP target'
Require-Match $dictation 'await waitStep\(StepDelayMs, cancellationToken\);[\s\S]*?GuardError\(cancellationToken, targetOwnsFocus, foregroundError\);[\s\S]*?TryPress\(VkH, 0' 'dictation rechecks target before H down'
Require-Match $dictation 'RunShortcutSequenceForTest[\s\S]*?SendShortcutAsync' 'offline test executes production dictation state machine'
Require-Match $dictation 'cancellationToken\.Register\([\s\S]*?AbortAndRelease\(CanceledMessage\)' 'dictation cancellation releases keys outside Unity continuation'
Require-Match $dictation 'new Timer\([\s\S]*?AbortAndRelease\(WatchdogMessage\)' 'dictation watchdog releases keys outside Unity continuation'
Require-Match $dictation 'Interlocked\.Exchange\(ref state, 1\)' 'dictation tracks key-down state atomically'
Require-Match $dictation 'finally[\s\S]*?keyState\.CompleteAndRelease\(\)' 'dictation key release finally is idempotent with watchdog'

Write-Host '[frontend-lifecycle-regression] PASS'
