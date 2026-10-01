using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using FrameWork;
using FrameWork.UISystem.UIElements;
using Game.Views.EventWindow;
using GameData.Domains.Character;
using GameData.Domains.Mod;
using GameData.Domains.TaiwuEvent;
using GameData.Domains.TaiwuEvent.DisplayEvent;
using GameData.Serializer;
using GameData.Utilities;
using HarmonyLib;
using JianghuYouling.Core.Prompt;
using JianghuYouling.Shared;
using TMPro;
using UnityEngine;

namespace JianghuYouling
{
    /// <summary>
    /// 玩家开启原版互动记录后，捕获事件窗里真正提交的玩家选项及随后展示的文本。
    /// 只排除 EventCommonOptionSelect 分类切换，不按互动 GUID、文本或状态变化过滤，
    /// 因此普通、敌对、囚禁、奇遇及剧情专属互动均覆盖。
    /// </summary>
    internal static class NativeInteractionCapture
    {
        private sealed class Pending
        {
            public uint WorldId;
            public int Generation;
            public int TaiwuId;
            public int NpcId;
            public string NpcName;
            public TalkTurn Turn;
        }

        private sealed class SelectionAttempt
        {
            public string Token;
            public string EventGuid;
            public string OptionKey;
            public string Choice;
            public int Date;
            public string Location;
            public bool Pending;
            public bool Resolved;
            public bool Landed;
            public bool ChoicePersisted;
            public bool QueryInFlight;
            public bool RetryScheduled;
            public int AuditPolls;
            public int PersistedResultCount;
            public readonly List<string> Results = new List<string>();
            public ActiveInteraction Owner;
        }

        private sealed class ActiveInteraction
        {
            public uint WorldId;
            public int Generation;
            public int TaiwuId;
            public int NpcId;
            public string NpcName;
            public string ExchangeId;
            public ViewEventWindow Window;
            public bool Closing;
            public readonly List<SelectionAttempt> Attempts = new List<SelectionAttempt>();
        }

        private static readonly object Gate = new object();
        private static readonly List<Pending> PendingTurns = new List<Pending>();
        private static readonly Regex TmpTag = new Regex("<[^>]+>", RegexOptions.Compiled);
        private static readonly FieldInfo EventContentField = typeof(ViewEventWindow).GetField(
            "eventContent", BindingFlags.Instance | BindingFlags.NonPublic);
        private static ViewEventWindow _window;
        private static int _sessionNpcId = -1;
        private static string _lastFingerprint;
        private static ActiveInteraction _active;
        private static readonly Dictionary<string, SelectionAttempt> AttemptsByToken
            = new Dictionary<string, SelectionAttempt>(StringComparer.Ordinal);
        private static bool _pumpRunning;

        internal static void OnWindowDisabled(ViewEventWindow window)
        {
            if (_window == null || ReferenceEquals(_window, window))
            {
                // 战斗、选人、选物等原版流程会临时关闭事件窗，随后仍回到同一互动链。
                // 此处不结束事务，战斗结算等二次返回仍会接到原选择上。
                RequestPendingAudits();
                _window = null;
                _sessionNpcId = -1;
            }
        }

        internal static void OnCommonOptionChanging()
        {
            // 交谈/比试/修习/亲近/敌对/互动顶部分类只是浏览，不属于实际行为。
            // 离开当前行为链时先提交已经发生的真实互动，再让接下来的分类预览刷新保持静默。
            CloseActiveInteraction();
        }

        internal static void CaptureDisplayedText(ViewEventWindow window)
        {
            if (!TryContext(window, true, out TaiwuEventDisplayData data, out int taiwuId,
                out int npcId, out string npcName, out string exchangeId)) return;
            ActiveInteraction active = _active;
            SelectionAttempt attempt = LatestAttempt(active);
            if (active == null || attempt == null
                || active.WorldId != WorldLifecycle.WorldId
                || active.Generation != WorldLifecycle.Generation
                || active.TaiwuId != taiwuId || active.NpcId != npcId) return;
            string text = null;
            try { text = (EventContentField?.GetValue(window) as TextMeshProUGUI)?.text; } catch { }
            text = Clean(text);
            if (string.IsNullOrWhiteSpace(text)) return;
            string location = null;
            try { location = NpcSnapshotReader.ResolveLocationText(data.TargetCharacter.Location); } catch { }
            if (string.IsNullOrWhiteSpace(attempt.Location)) attempt.Location = location;
            AddDistinct(attempt.Results, text);
            PersistLandedAttempt(attempt);
            RequestPendingAudits();
        }

        internal static string CaptureConfirmedOption(string eventGuid, string optionKey)
        {
            ViewEventWindow window = null;
            try { window = UIElement.EventWindow.Exist ? UIElement.EventWindow.UiBase as ViewEventWindow : null; }
            catch { }
            if (!TryContext(window, false, out TaiwuEventDisplayData data, out int taiwuId,
                out int npcId, out string npcName, out string exchangeId)) return null;
            if (!NativeInteractionRecordingStore.IsEnabled(npcId)) return null;
            EventOptionInfo? selected = null;
            if (data.EventOptionInfos != null)
                for (int i = 0; i < data.EventOptionInfos.Count; i++)
                    if (string.Equals(data.EventOptionInfos[i].OptionKey, optionKey, StringComparison.Ordinal))
                    {
                        selected = data.EventOptionInfos[i];
                        break;
                    }
            if (!selected.HasValue) return null;
            string text = FormatOption(selected.Value);
            if (string.IsNullOrWhiteSpace(text)) return null;
            string location = null;
            try
            {
                if (data.MainCharacter != null)
                    location = NpcSnapshotReader.ResolveLocationText(data.MainCharacter.Location);
            }
            catch { }
            ActiveInteraction active = _active;
            if (active == null)
            {
                active = new ActiveInteraction
                {
                    WorldId = WorldLifecycle.WorldId,
                    Generation = WorldLifecycle.Generation,
                    TaiwuId = taiwuId,
                    NpcId = npcId,
                    NpcName = npcName,
                    ExchangeId = "native-" + Guid.NewGuid().ToString("N"),
                    Window = window,
                };
                _active = active;
            }
            string token = Guid.NewGuid().ToString("N");
            var attempt = new SelectionAttempt
            {
                Token = token,
                EventGuid = eventGuid ?? "",
                OptionKey = optionKey ?? "",
                Choice = text,
                Date = ReadCurrentDate(),
                Location = location,
                Owner = active,
            };
            active.Attempts.Add(attempt);
            AttemptsByToken[token] = attempt;
            return token;
        }

        internal static void ConfirmSelectionAfterDispatch(string token)
        {
            if (string.IsNullOrEmpty(token)) return;
            SelectionAttempt attempt;
            if (!AttemptsByToken.TryGetValue(token, out attempt)) return;
            // 开关由玩家显式开启后，EventSelect 的实际派发本身就是记录依据；不再以
            // 状态字段变化或时间消耗作严格过滤。后续原版文本及战斗结算继续增量追加。
            attempt.Pending = false;
            attempt.Landed = true;
            attempt.Resolved = true;
            PersistLandedAttempt(attempt);
        }

        internal static void OnEventSelectContinue()
            => RequestPendingAudits();

        private static bool TryContext(ViewEventWindow window, bool allowActiveActorContinuation,
            out TaiwuEventDisplayData data,
            out int taiwuId, out int npcId, out string npcName, out string exchangeId)
        {
            data = null; taiwuId = -1; npcId = -1; npcName = null; exchangeId = null;
            if (window == null || !WorldLifecycle.HasWorldIdentity) return false;
            try { data = SingletonObject.getInstance<EventModel>()?.DisplayingEventData; } catch { }
            if (data?.TargetCharacter == null) return false;
            try { taiwuId = SingletonObject.getInstance<BasicGameData>().TaiwuCharId; } catch { }
            if (taiwuId <= 0) return false;
            int displayedNpcId = EwReflect.ResolveNpcId(EwReflect.Right(window));
            bool exactDisplayedTarget = displayedNpcId >= 0
                && displayedNpcId == data.TargetCharacter.CharacterId;
            ActiveInteraction active = _active;
            if (!exactDisplayedTarget)
            {
                int eventNpcId = CharacterProxyIdentityService.ResolveKnown(taiwuId,
                    data.TargetCharacter.CharacterId);
                if (!allowActiveActorContinuation || active == null
                    || !ReferenceEquals(active.Window, window)
                    || active.WorldId != WorldLifecycle.WorldId
                    || active.Generation != WorldLifecycle.Generation
                    || active.TaiwuId != taiwuId || active.NpcId != eventNpcId)
                    return false;
                npcId = active.NpcId;
                npcName = active.NpcName;
            }
            else
            {
                npcId = CharacterProxyIdentityService.ResolveKnown(taiwuId, displayedNpcId);
                try { npcName = Clean(NameCenter.GetMonasticTitleOrDisplayName(data.TargetCharacter, false)); }
                catch { }
            }
            if (npcId < 0 || npcId == taiwuId) return false;
            if (string.IsNullOrWhiteSpace(npcName)) npcName = "NPC#" + npcId;

            if (!ReferenceEquals(_window, window) || _sessionNpcId != npcId)
            {
                if (_active != null && (_active.NpcId != npcId
                    || _active.WorldId != WorldLifecycle.WorldId
                    || _active.Generation != WorldLifecycle.Generation))
                    CloseActiveInteraction();
                _window = window;
                _sessionNpcId = npcId;
                _lastFingerprint = null;
            }
            if (_active != null && _active.NpcId == npcId) _active.Closing = false;
            exchangeId = _active?.ExchangeId;
            return true;
        }

        private static SelectionAttempt LatestAttempt(ActiveInteraction active)
        {
            if (active == null || active.Attempts.Count == 0) return null;
            return active.Attempts[active.Attempts.Count - 1];
        }

        private static void RequestPendingAudits()
        {
            ActiveInteraction active = _active;
            if (active == null) return;
            foreach (SelectionAttempt attempt in active.Attempts.ToArray())
                if (!attempt.Resolved && !attempt.QueryInFlight) RequestAudit(attempt);
        }

        private static void RequestAudit(SelectionAttempt attempt)
        {
            ActiveInteraction owner = attempt?.Owner;
            if (attempt == null || owner == null || attempt.Resolved || attempt.QueryInFlight) return;
            if (owner.WorldId != WorldLifecycle.WorldId
                || owner.Generation != WorldLifecycle.Generation)
            {
                attempt.Resolved = true;
                TryFinalizeActiveInteraction(owner);
                return;
            }

            attempt.QueryInFlight = true;
            attempt.AuditPolls++;
            try
            {
                var parameter = new SerializableModData();
                parameter.Set("world_id", owner.WorldId.ToString(CultureInfo.InvariantCulture));
                parameter.Set("event_guid", attempt.EventGuid ?? "");
                parameter.Set("option_key", attempt.OptionKey ?? "");
                string modId = Plugin.Instance?.ModIdStr;
                if (string.IsNullOrWhiteSpace(modId)) modId = RpcConst.FallbackModId;
                ModDomainMethod.AsyncCall.CallModMethodWithParamAndRet(
                    null, modId, RpcConst.QueryNativeInteractionAuditMethod, parameter,
                    delegate (int offset, RawDataPool pool)
                    {
                        SerializableModData response = null;
                        try { Serializer.Deserialize(pool, offset, ref response); } catch { }
                        HandleAuditResponse(attempt.Token, response);
                    });
            }
            catch
            {
                attempt.QueryInFlight = false;
            }
        }

        private static void HandleAuditResponse(string token, SerializableModData response)
        {
            SelectionAttempt attempt;
            if (string.IsNullOrEmpty(token) || !AttemptsByToken.TryGetValue(token, out attempt)) return;
            attempt.QueryInFlight = false;
            ActiveInteraction owner = attempt.Owner;
            if (owner == null || owner.WorldId != WorldLifecycle.WorldId
                || owner.Generation != WorldLifecycle.Generation)
            {
                attempt.Resolved = true;
                TryFinalizeActiveInteraction(owner);
                return;
            }

            bool success = false, available = false, changed = false, pending = false;
            response?.Get("success", out success);
            response?.Get("available", out available);
            response?.Get("changed", out changed);
            response?.Get("pending", out pending);
            if (!success || !available)
            {
                ScheduleAuditRetry(attempt);
                return;
            }

            attempt.Pending = pending;
            attempt.Landed = changed;
            attempt.Resolved = changed || !pending;
            PersistLandedAttempt(attempt);
            if (pending && !changed) ScheduleAuditRetry(attempt);
            if (owner.Closing) TryFinalizeActiveInteraction(owner);
        }

        private static void ScheduleAuditRetry(SelectionAttempt attempt)
        {
            if (attempt == null || attempt.Resolved || attempt.RetryScheduled) return;
            ActiveInteraction owner = attempt.Owner;
            if (owner == null) return;
            if (attempt.AuditPolls >= 10)
            {
                if (owner.Closing)
                {
                    attempt.Pending = false;
                    attempt.Resolved = true;
                    TryFinalizeActiveInteraction(owner);
                }
                return;
            }
            TalkEntryHost host = TalkEntryHost.Instance;
            if (host == null) return;
            attempt.RetryScheduled = true;
            host.StartCoroutine(RetryAudit(attempt));
        }

        private static IEnumerator RetryAudit(SelectionAttempt attempt)
        {
            yield return new WaitForSecondsRealtime(0.2f);
            if (attempt == null) yield break;
            attempt.RetryScheduled = false;
            if (!attempt.Resolved) RequestAudit(attempt);
        }

        private static void AddDistinct(List<string> target, string text)
        {
            if (target == null || string.IsNullOrWhiteSpace(text)) return;
            if (target.Count > 0 && string.Equals(target[target.Count - 1], text,
                StringComparison.Ordinal)) return;
            target.Add(text);
        }

        private static int ReadCurrentDate()
        {
            try { return SingletonObject.getInstance<BasicGameData>().CurrDate; }
            catch { return -1; }
        }

        private static void CloseActiveInteraction()
        {
            ActiveInteraction active = _active;
            if (active == null) return;
            active.Closing = true;
            _active = null;
            foreach (SelectionAttempt attempt in active.Attempts.ToArray())
                if (!attempt.Resolved && !attempt.QueryInFlight) RequestAudit(attempt);
            TryFinalizeActiveInteraction(active);
        }

        private static void TryFinalizeActiveInteraction(ActiveInteraction active)
        {
            if (active == null) return;
            if (active.WorldId != WorldLifecycle.WorldId
                || active.Generation != WorldLifecycle.Generation)
            {
                DiscardActiveInteraction(active);
                return;
            }
            foreach (SelectionAttempt attempt in active.Attempts)
                PersistLandedAttempt(attempt);
            if (active.Closing && AllAttemptsResolved(active)) DiscardActiveInteraction(active);
        }

        internal static bool CaptureCombatResult(int mainEnemyId, sbyte combatResult, sbyte combatType)
        {
            ActiveInteraction active = _active;
            if (active != null)
                mainEnemyId = CharacterProxyIdentityService.ResolveKnown(active.TaiwuId, mainEnemyId);
            if (active == null || active.NpcId != mainEnemyId
                || active.WorldId != WorldLifecycle.WorldId
                || active.Generation != WorldLifecycle.Generation) return false;

            SelectionAttempt attempt = LatestAttempt(active);
            if (attempt == null) return false;
            // 原版战斗结算窗本身就是比 EventSelect 写入计数更强的最终凭据。
            // 有结算即说明这次切磋/相搏已真实发生，即使启动战斗的事件选择只改了
            // TaiwuEvent 运行态，也不能把战果当作未落地丢弃。
            attempt.Pending = false;
            attempt.Landed = true;
            attempt.Resolved = true;
            string result = CombatResultProjection.ToolResult(active.NpcName, combatResult, combatType);
            AddDistinct(attempt.Results, result);
            PersistLandedAttempt(attempt);
            return true;
        }

        private static bool AllAttemptsResolved(ActiveInteraction active)
        {
            if (active == null) return true;
            foreach (SelectionAttempt attempt in active.Attempts)
                if (!attempt.Resolved) return false;
            return true;
        }

        private static void PersistLandedAttempt(SelectionAttempt attempt)
        {
            ActiveInteraction active = attempt?.Owner;
            if (attempt == null || active == null || !attempt.Landed) return;
            if (active.WorldId != WorldLifecycle.WorldId
                || active.Generation != WorldLifecycle.Generation) return;

            if (!attempt.ChoicePersisted && !string.IsNullOrWhiteSpace(attempt.Choice))
            {
                attempt.ChoicePersisted = true;
                Enqueue(attempt.EventGuid, attempt.OptionKey, active.TaiwuId, active.NpcId,
                    active.NpcName, active.ExchangeId, TalkTurnKinds.NativePlayerChoice, true,
                    attempt.Choice, attempt.Location, attempt.Date);
            }
            while (attempt.PersistedResultCount < attempt.Results.Count)
            {
                string result = attempt.Results[attempt.PersistedResultCount++];
                if (string.IsNullOrWhiteSpace(result)) continue;
                Enqueue(attempt.EventGuid, attempt.OptionKey, active.TaiwuId, active.NpcId,
                    active.NpcName, active.ExchangeId, TalkTurnKinds.NativeGameText, false,
                    result, attempt.Location, attempt.Date);
            }
        }

        private static void DiscardActiveInteraction(ActiveInteraction active)
        {
            if (active == null) return;
            foreach (SelectionAttempt attempt in active.Attempts)
                if (!string.IsNullOrEmpty(attempt.Token)) AttemptsByToken.Remove(attempt.Token);
            if (ReferenceEquals(_active, active)) _active = null;
        }

        private static void Enqueue(string eventGuid, string optionKey, int taiwuId, int npcId,
            string npcName, string exchangeId, string kind, bool fromPlayer, string text, string location,
            int date = -1)
        {
            string fingerprint = kind + "|" + (eventGuid ?? "") + "|" + (optionKey ?? "") + "|" + text;
            if (string.Equals(_lastFingerprint, fingerprint, StringComparison.Ordinal)) return;
            _lastFingerprint = fingerprint;
            if (date < 0) date = ReadCurrentDate();
            var pending = new Pending
            {
                WorldId = WorldLifecycle.WorldId,
                Generation = WorldLifecycle.Generation,
                TaiwuId = taiwuId,
                NpcId = npcId,
                NpcName = npcName,
                Turn = new TalkTurn
                {
                    Id = Guid.NewGuid().ToString("N"),
                    ExchangeId = exchangeId,
                    FromPlayer = fromPlayer,
                    Text = text,
                    Date = date,
                    LocationText = location,
                    ContactMode = "游戏原生互动",
                    Kind = kind,
                },
            };
            lock (Gate)
            {
                PendingTurns.Add(pending);
                if (_pumpRunning) return;
                _pumpRunning = true;
            }
            TalkEntryHost host = TalkEntryHost.Instance;
            if (host != null) host.StartCoroutine(PersistPump());
            else lock (Gate) _pumpRunning = false;
        }

        private static IEnumerator PersistPump()
        {
            yield return null;
            int failures = 0;
            while (true)
            {
                Pending seed;
                lock (Gate)
                {
                    if (PendingTurns.Count == 0) { _pumpRunning = false; yield break; }
                    seed = PendingTurns[0];
                }
                if (!WorldLifecycle.IsSameWorld(seed.Generation) || WorldLifecycle.WorldId != seed.WorldId)
                {
                    lock (Gate) PendingTurns.RemoveAll(x => x.Generation == seed.Generation && x.WorldId == seed.WorldId);
                    continue;
                }
                List<TalkTurn> batch = BatchFor(seed);
                bool saved = TalkOrchestrator.AppendNativeInteractions(seed.TaiwuId, seed.NpcId,
                    seed.NpcName, batch);
                if (saved)
                {
                    failures = 0;
                    RemoveBatch(seed, batch);
                    ChatWindow.NotifyNativeInteractionCommitted(seed.TaiwuId, seed.NpcId);
                    yield return null;
                    continue;
                }
                failures++;
                if (failures == 1 || failures % 10 == 0)
                    Debug.LogWarning("[JHYL_NATIVE_INTERACTION] 游戏互动记录暂未写入，保留重试 npc="
                        + seed.NpcId + " pending=" + batch.Count);
                yield return new WaitForSecondsRealtime(failures < 4 ? 0.5f : 30f);
            }
        }

        private static List<TalkTurn> BatchFor(Pending seed)
        {
            var result = new List<TalkTurn>();
            lock (Gate)
                foreach (Pending item in PendingTurns)
                    if (item.WorldId == seed.WorldId && item.Generation == seed.Generation
                        && item.TaiwuId == seed.TaiwuId && item.NpcId == seed.NpcId)
                        result.Add(item.Turn);
            return result;
        }

        private static void RemoveBatch(Pending seed, IList<TalkTurn> batch)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (TalkTurn turn in batch) if (!string.IsNullOrEmpty(turn?.Id)) ids.Add(turn.Id);
            lock (Gate) PendingTurns.RemoveAll(x => x.WorldId == seed.WorldId
                && x.Generation == seed.Generation && x.TaiwuId == seed.TaiwuId
                && x.NpcId == seed.NpcId && ids.Contains(x.Turn?.Id));
        }

        internal static void FlushPendingNow()
        {
            CloseActiveInteraction();
            while (true)
            {
                Pending seed;
                lock (Gate) { if (PendingTurns.Count == 0) return; seed = PendingTurns[0]; }
                if (!WorldLifecycle.IsSameWorld(seed.Generation) || WorldLifecycle.WorldId != seed.WorldId) return;
                List<TalkTurn> batch = BatchFor(seed);
                if (!TalkOrchestrator.AppendNativeInteractions(seed.TaiwuId, seed.NpcId,
                    seed.NpcName, batch)) return;
                RemoveBatch(seed, batch);
                ChatWindow.NotifyNativeInteractionCommitted(seed.TaiwuId, seed.NpcId);
            }
        }

        internal static void ResetForWorldExit()
        {
            ResetSession();
            lock (Gate) { PendingTurns.Clear(); _pumpRunning = false; }
        }

        private static string FormatOption(EventOptionInfo option)
        {
            string text = option.OptionContent;
            try
            {
                if (option.ExtraFormatLanguageKeys != null && option.ExtraFormatLanguageKeys.Count > 0)
                {
                    object[] args = option.ExtraFormatLanguageKeys.ConvertAll(LocalStringManager.Get).ToArray();
                    text = text.GetFormat(args);
                }
            }
            catch { }
            return Clean(text);
        }

        private static string Clean(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string text = TmpTag.Replace(value, "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            return text.Length <= 262144 ? text : text.Substring(0, 262144);
        }

        private static void ResetSession()
        {
            _window = null;
            _sessionNpcId = -1;
            _active = null;
            AttemptsByToken.Clear();
            _lastFingerprint = null;
        }
    }

    [HarmonyPatch(typeof(ViewEventWindow), "UpdateContent")]
    internal static class NativeInteractionContentPatch
    {
        private static void Postfix(ViewEventWindow __instance)
            => NativeInteractionCapture.CaptureDisplayedText(__instance);
    }

    [HarmonyPatch(typeof(ViewEventWindow), "OnDisable")]
    internal static class NativeInteractionWindowDisablePatch
    {
        private static void Postfix(ViewEventWindow __instance)
            => NativeInteractionCapture.OnWindowDisabled(__instance);
    }

    [HarmonyPatch(typeof(TaiwuEventDomainMethod.Call), nameof(TaiwuEventDomainMethod.Call.EventSelect),
        new Type[] { typeof(string), typeof(string) })]
    internal static class NativeInteractionOptionPatch
    {
        private static void Prefix(string eventGuid, string optionKey, out string __state)
            => __state = NativeInteractionCapture.CaptureConfirmedOption(eventGuid, optionKey);

        private static void Postfix(string __state)
            => NativeInteractionCapture.ConfirmSelectionAfterDispatch(__state);
    }

    [HarmonyPatch(typeof(TaiwuEventDomainMethod.Call),
        nameof(TaiwuEventDomainMethod.Call.EventSelectContinue), new Type[0])]
    internal static class NativeInteractionContinuePatch
    {
        private static void Postfix()
            => NativeInteractionCapture.OnEventSelectContinue();
    }

    [HarmonyPatch(typeof(TaiwuEventDomainMethod.Call),
        nameof(TaiwuEventDomainMethod.Call.EventCommonOptionSelect),
        new Type[] { typeof(short) })]
    internal static class NativeInteractionCommonOptionPatch
    {
        private static void Prefix()
            => NativeInteractionCapture.OnCommonOptionChanging();
    }
}
