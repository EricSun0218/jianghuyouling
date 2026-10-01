using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace JianghuYouling
{
    /// <summary>
    /// 从主动人物名单与已经建立单聊历史的交集中挑选一人主动来信。生成沿用普通单聊
    /// 完整上下文，可按人设选择查询或 NPC 自主行动，但不要求调用工具，也不允许替太吾作决定。
    /// </summary>
    public sealed class NpcProactiveChatScheduler : MonoBehaviour
    {
        public static NpcProactiveChatScheduler Instance { get; private set; }
        bool _settingsLoaded;
        bool _enabled = true;
        int _frequency = NpcProactiveChatStore.DefaultFrequency;
        float _nextFire = -1f;
        Coroutine _running;
        TalkOrchestrator _orchestrator;
        int _runningTaiwuId;
        int _runningNpcId;
        int _lastNpcId;
        readonly List<int> _recentNpcIds = new List<int>();

        public static void Initialize()
        {
            if (Instance != null) return;
            var host = new GameObject("JHYL_NpcProactiveChatHost");
            DontDestroyOnLoad(host);
            Instance = host.AddComponent<NpcProactiveChatScheduler>();
        }

        public static void NotifySettingsChanged()
        {
            if (Instance == null) return;
            Instance.ReloadSettings(true);
        }

        public static void CancelForWorldExit()
        {
            Instance?.CancelCurrent();
            if (Instance != null)
            {
                Instance._nextFire = -1f;
                Instance._lastNpcId = 0;
                Instance._recentNpcIds.Clear();
            }
        }

        public static bool IsGeneratingFor(int taiwuId, int npcId)
            => Instance != null && Instance._running != null
                && Instance._runningTaiwuId == taiwuId && Instance._runningNpcId == npcId;

        void ReloadSettings(bool resetSchedule)
        {
            bool enabled = NpcProactiveChatStore.LoadEnabled();
            int frequency = NpcProactiveChatStore.LoadFrequency();
            bool changed = _settingsLoaded && (_enabled != enabled || _frequency != frequency);
            _enabled = enabled;
            _frequency = frequency;
            _settingsLoaded = true;
            if (resetSchedule || changed)
            {
                _nextFire = -1f;
                if (!_enabled) CancelCurrent();
            }
        }

        void Update()
        {
            if (!_settingsLoaded) ReloadSettings(false);
            if (!_enabled || !WorldLifecycle.HasWorldIdentity)
            {
                _nextFire = -1f;
                return;
            }
            if (_running != null) return;
            if (!LlmService.IsConfigured)
            {
                _nextFire = Time.unscaledTime + 60f;
                return;
            }
            if (_nextFire < 0f)
            {
                NpcProactiveChatStore.IntervalRange(_frequency, out float min, out float max);
                _nextFire = Time.unscaledTime + UnityEngine.Random.Range(min, max);
                return;
            }
            if (Time.unscaledTime < _nextFire) return;
            _nextFire = -1f;
            _running = StartCoroutine(GenerateOne());
        }

        IEnumerator GenerateOne()
        {
            int generation = WorldLifecycle.Generation;
            uint worldId = WorldLifecycle.WorldId;
            int taiwuId = 0;
            int currentDate = 0;
            try
            {
                var game = SingletonObject.getInstance<BasicGameData>();
                taiwuId = game.TaiwuCharId;
                currentDate = game.CurrDate;
            }
            catch { }
            if (taiwuId <= 0 || worldId == 0) { Finish(); yield break; }

            yield return ChatWindow.EnsureConversationIndexesReady(taiwuId, generation, worldId);
            if (!StillCurrent(generation, worldId, taiwuId)) { Finish(); yield break; }

            List<int> activeNpcIds = null;
            string activePoolError = null;
            yield return CompanionMonthlyActions.BuildCandidatePool(taiwuId, currentDate,
                (ids, error) => { activeNpcIds = ids; activePoolError = error; },
                () => StillCurrent(generation, worldId, taiwuId));
            if (!StillCurrent(generation, worldId, taiwuId)) { Finish(); yield break; }
            var activeNpcSet = new HashSet<int>(activeNpcIds ?? new List<int>());
            if (activeNpcSet.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(activePoolError))
                    Debug.Log("[JHYL_NPC_PROACTIVE] active_pool_empty " + activePoolError);
                Finish();
                yield break;
            }

            List<TalkOrchestrator.ConversedPartner> partners = TalkOrchestrator.ConversedPartners(taiwuId);
            var candidates = new List<TalkOrchestrator.ConversedPartner>();
            foreach (TalkOrchestrator.ConversedPartner partner in partners)
                if (partner != null && partner.NpcId > 0 && partner.NpcId != taiwuId
                    && activeNpcSet.Contains(partner.NpcId)
                    && !ArchivedCharacterStatusStore.IsDead(taiwuId, partner.NpcId)
                    && !ChatWindow.IsSingleConversationBusy(taiwuId, partner.NpcId))
                    candidates.Add(partner);
            if (candidates.Count == 0) { Finish(); yield break; }

            if (candidates.Count > 1 && _lastNpcId > 0)
                candidates.RemoveAll(x => x.NpcId == _lastNpcId);

            // 在仍有未轮到的人时，先从最近三次没有发过消息的人里抽；权重只在这个
            // 公平候选池内做轻微偏置，避免“聊得最多”的对象长期霸占主动消息。
            if (candidates.Count > 1 && _recentNpcIds.Count > 0)
            {
                var fresh = candidates.FindAll(x => !_recentNpcIds.Contains(x.NpcId));
                if (fresh.Count > 0) candidates = fresh;
            }
            var weights = new List<double>(candidates.Count);
            foreach (TalkOrchestrator.ConversedPartner candidate in candidates)
            {
                int monthsSinceLast = currentDate > 0 && candidate.LastPlayerDate > 0
                    ? Math.Max(0, currentDate - candidate.LastPlayerDate) : 0;
                int recentRank = _recentNpcIds.IndexOf(candidate.NpcId);
                weights.Add(NpcProactiveChatStore.CandidateWeight(
                    candidate.PlayerConversations, candidate.RecentPlayerConversations,
                    candidate.RecentActiveMonths, monthsSinceLast, recentRank));
            }
            int selectedIndex = NpcProactiveChatStore.PickWeightedIndex(
                weights, UnityEngine.Random.value);
            if (selectedIndex < 0 || selectedIndex >= candidates.Count)
            { Finish(); yield break; }
            TalkOrchestrator.ConversedPartner selected = candidates[selectedIndex];
            // 双重门禁：候选建立后人物可能恰好死亡，已排期任务也不得再发出灵魂主动消息。
            if (ArchivedCharacterStatusStore.IsDead(taiwuId, selected.NpcId))
            { Finish(); yield break; }
            _runningTaiwuId = taiwuId;
            _runningNpcId = selected.NpcId;
            _orchestrator = new TalkOrchestrator
            {
                GrantSink = (_, __, ___) => { },
                SysSink = _ => { },
            };

            string reply = null;
            string error = null;
            yield return _orchestrator.ProcessTurn(selected.NpcId, null,
                value => reply = value,
                value => error = value,
                npcInitiated: true);

            if (!StillCurrent(generation, worldId, taiwuId)) { Finish(); yield break; }
            if (!string.IsNullOrWhiteSpace(reply))
            {
                string name = TalkOrchestrator.IsUnresolvedNpcName(_orchestrator.LastNpcName)
                    ? selected.Name : _orchestrator.LastNpcName;
                if (TalkOrchestrator.IsUnresolvedNpcName(name)) name = "有人";
                _lastNpcId = selected.NpcId;
                RememberRecentRecipient(selected.NpcId);
                ChatWindow.ReceiveNpcProactiveMessage(taiwuId, selected.NpcId, reply,
                    _orchestrator.LastTurnActions, _orchestrator.LastTurnToolResults);
                // 仅复用灵儿原有主动气泡做本地通知；不调用模型，也不污染灵儿聊天记录。
                AssistantWidget.ShowLocalProactiveNotice(name.Trim() + "给你发消息了哦。");
                Debug.Log("[JHYL_NPC_PROACTIVE] delivered npc=" + selected.NpcId
                    + " weight=" + weights[selectedIndex].ToString("F3")
                    + " conversations=" + selected.PlayerConversations
                    + " recent=" + selected.RecentPlayerConversations
                    + "/" + selected.RecentActiveMonths + "m");
            }
            else if (!string.IsNullOrWhiteSpace(error))
                Debug.LogWarning("[JHYL_NPC_PROACTIVE] skipped npc=" + selected.NpcId
                    + " reason=" + error);
            Finish();
        }

        void RememberRecentRecipient(int npcId)
        {
            _recentNpcIds.Remove(npcId);
            _recentNpcIds.Insert(0, npcId);
            if (_recentNpcIds.Count > 3)
                _recentNpcIds.RemoveRange(3, _recentNpcIds.Count - 3);
        }

        bool StillCurrent(int generation, uint worldId, int taiwuId)
        {
            if (!WorldLifecycle.IsSameWorld(generation) || WorldLifecycle.WorldId != worldId) return false;
            try { return SingletonObject.getInstance<BasicGameData>().TaiwuCharId == taiwuId; }
            catch { return false; }
        }

        void Finish()
        {
            _orchestrator = null;
            _running = null;
            _runningTaiwuId = 0;
            _runningNpcId = 0;
        }

        void CancelCurrent()
        {
            try { _orchestrator?.Cancel(); } catch { }
            if (_running != null) try { StopCoroutine(_running); } catch { }
            Finish();
        }
    }
}
