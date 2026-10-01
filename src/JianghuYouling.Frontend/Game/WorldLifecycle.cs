using HarmonyLib;
using GameData.GameDataBridge;
using GameData.Common;
using JianghuYouling.Rpc;
using JianghuYouling.Effects;
using UnityEngine;

namespace JianghuYouling
{
    internal static class WorldLifecycle
    {
        private static volatile bool _active;
        private static volatile int _generation;
        private static volatile bool _identityReady;
        private static volatile bool _dateReady;
        private static volatile uint _worldId;
        private static volatile int _lastRecoveryTaiwuId;

        public static bool IsActive => _active;
        public static int Generation => _generation;
        public static bool HasWorldIdentity => _active && _identityReady && _worldId > 0;
        public static bool HasWorldDate => _active && _dateReady;
        public static uint WorldId => HasWorldIdentity ? _worldId : 0;

        public static bool IsSameWorld(int generation)
        {
            return HasWorldIdentity && _generation == generation;
        }

        private static void ResetWorldScopedUi()
        {
            // Every window below is DontDestroyOnLoad and carries either a Taiwu/NPC identity
            // or late async callbacks. Invalidate them at the same generation boundary as the
            // orchestrators so an old save cannot repaint or write through a new save's UI.
            try { ConfigWindow.ResetForWorldExit(); } catch { }
            try { HistoryWindow.ResetForWorldExit(); } catch { }
            try { ChatHistoryWindow.ResetForWorldExit(); } catch { }
            try { NpcNovelWindow.ResetForWorldExit(); } catch { }
            try { PersonaWindow.ResetForWorldExit(); } catch { }
            try { WorldBookWindow.ResetForWorldExit(); } catch { }
            try { StructuredWorldBookWindow.ResetForWorldExit(); } catch { }
            try { MonthlyDigestPopup.ResetForWorldExit(); } catch { }
            try { MonthlyChronicleProgressStore.ResetForWorldExit(); } catch { }
            try { MonthlyChronicleNoticeStore.ResetForWorldExit(); } catch { }
            try { NpcChatUnreadStore.ResetForWorldExit(); } catch { }
            try { ArchivedCharacterStatusStore.ResetForWorldExit(); } catch { }
            try { CharacterProxyIdentityService.ResetForWorldExit(); } catch { }
            try { MonthlyTimelineRollbackPruner.ResetForWorldExit(); } catch { }
            try { ChatHistoryEntryHost.CancelForWorldExit(); } catch { }
            try { TalkEntryInjector.ResetForWorldExit(); } catch { }
            try { VoicePlayer.ResetForWorldExit(); } catch { }
            try { GroupChatOrchestrator.ResetForWorldExit(); } catch { }
            try { JianghuYouling.Rpc.JianghuBrothelContextReader.ResetForWorldExit(); } catch { }
            try { MonthlyCandidateWindow.CancelForWorldExit(); } catch { }
            try { ModIntroductionWindow.ResetForWorldExit(); } catch { }
            try { CommissionWindow.ResetForWorldExit(); } catch { }
            try { EffectHandler.DetachOperationOutcomeObserversForWorldExit(); } catch { }
        }

        public static void MarkWorldReady()
        {
            try { NativeInteractionCapture.FlushPendingNow(); } catch { }
            // OnWorldDataReady 只注册异步数据 monitor；BasicGameData.WorldId 仍可能是上个存档的残值。
            _active = false;
            _identityReady = false;
            _dateReady = false;
            _worldId = 0;
            _lastRecoveryTaiwuId = 0;
            _generation++;
            OperationRpcClient.ResetAcknowledgementPumpForWorldChange();
            MonthlySettlement.SuspendForWorldChange();
            AssistantWidget.CancelForWorldExit();
            NpcProactiveChatScheduler.CancelForWorldExit();
            GroupMemberPicker.CancelForWorldExit();
            ResetWorldScopedUi();
            // 防御性清掉上一个世界遗留的 DontDestroy 页签/静态缓存，再允许新世界动作。
            ChatWindow.ResetForWorldExit();
            TalkOrchestrator.ResetConversationCacheForWorldExit();
            NativeInteractionCapture.ResetForWorldExit();
            AssistantOrchestrator.ResetForWorldExit();
            PortraitService.ResetForWorldExit();
            CompanionMonthlyActions.CancelForWorldExit();
            MonthlyEventGenerator.CancelForWorldExit();
            _active = true;
            Debug.Log("[江湖有灵] 世界场景已就绪，等待权威 WorldId(gen=" + _generation + ")");
        }

        public static void MarkWorldIdentityReady(uint worldId)
        {
            if (!_active || worldId == 0) return;
            if (_identityReady && _worldId == worldId) return;
            if (_identityReady && _worldId != worldId)
            {
                try { NativeInteractionCapture.FlushPendingNow(); } catch { }
                _identityReady = false;
                _worldId = 0;
                _lastRecoveryTaiwuId = 0;
                // 极端情况下同一 scene 收到另一世界身份，先让所有旧协程失效。
                _generation++;
                OperationRpcClient.ResetAcknowledgementPumpForWorldChange();
                AssistantWidget.CancelForWorldExit();
                NpcProactiveChatScheduler.CancelForWorldExit();
                GroupMemberPicker.CancelForWorldExit();
                ResetWorldScopedUi();
                ChatWindow.ResetForWorldExit();
                TalkOrchestrator.ResetConversationCacheForWorldExit();
                NativeInteractionCapture.ResetForWorldExit();
                AssistantOrchestrator.ResetForWorldExit();
                PortraitService.ResetForWorldExit();
                CompanionMonthlyActions.CancelForWorldExit();
                MonthlyEventGenerator.CancelForWorldExit();
            }
            _worldId = worldId;
            _identityReady = true;
            if (_dateReady)
            {
                MonthlyTimelineRollbackPruner.PruneIfReady();
                MonthlySettlement.ResetForWorldReady();
            }
            Debug.Log("[江湖有灵] 世界身份已就绪(worldId=" + worldId + ",gen=" + _generation + ")");
            WorkshopUpdateChecker.CheckOnceAfterWorldReady();
            OperationRpcClient.ReplayAcknowledgementOutbox();
            TalkOrchestrator.RecoverWorldPendingConversations();
            GroupChatOrchestrator.RecoverWorldTransactions();
        }

        public static void MarkTaiwuIdentityChanged(uint worldId, int taiwuId)
        {
            if (!HasWorldIdentity || worldId == 0 || worldId != _worldId || taiwuId <= 0
                || _lastRecoveryTaiwuId == taiwuId) return;
            _lastRecoveryTaiwuId = taiwuId;
            try { NativeInteractionCapture.FlushPendingNow(); } catch { }
            // Succession keeps the world identity but invalidates every in-flight UI,
            // prompt and mutation snapshot tied to the former Taiwu. Query/ACK recovery
            // deliberately remains allowed for an earlier Taiwu in the same WorldId.
            _generation++;
            OperationRpcClient.ResetAcknowledgementPumpForWorldChange();
            MonthlySettlement.SuspendForWorldChange();
            AssistantWidget.CancelForWorldExit();
            NpcProactiveChatScheduler.CancelForWorldExit();
            GroupMemberPicker.CancelForWorldExit();
            ResetWorldScopedUi();
            ChatWindow.ResetForWorldExit();
            TalkOrchestrator.ResetConversationCacheForWorldExit();
            NativeInteractionCapture.ResetForWorldExit();
            AssistantOrchestrator.ResetForWorldExit();
            PortraitService.ResetForWorldExit();
            CompanionMonthlyActions.CancelForWorldExit();
            MonthlyEventGenerator.CancelForWorldExit();
            if (_dateReady)
            {
                MonthlyTimelineRollbackPruner.PruneIfReady();
                MonthlySettlement.ResetForWorldReady();
            }
            Debug.Log("[江湖有灵] 太吾身份已更新，重放同世界旧身份事务 worldId=" + worldId
                + ",taiwuId=" + taiwuId + ",gen=" + _generation);
            OperationRpcClient.ReplayAcknowledgementOutbox();
            TalkOrchestrator.RecoverWorldPendingConversations();
            GroupChatOrchestrator.RecoverWorldTransactions();
            ChatWindow.PrewarmConversationIndexes(taiwuId);
        }

        public static void MarkWorldDateReady()
        {
            if (!_active) return;
            // DataId=26 每次过月都会再通知；这里只消费新世界的第一次日期就绪。
            // 否则每月都把 MonthlySettlement 基线重置为“本月已处理”，月度系统将永不运行。
            if (_dateReady) return;
            _dateReady = true;
            if (_identityReady)
            {
                MonthlyTimelineRollbackPruner.PruneIfReady();
                MonthlySettlement.ResetForWorldReady();
            }
        }

        public static void MarkWorldLeaving()
        {
            try { NativeInteractionCapture.FlushPendingNow(); } catch { }
            _active = false;
            _identityReady = false;
            _dateReady = false;
            _worldId = 0;
            _lastRecoveryTaiwuId = 0;
            _generation++;
            OperationRpcClient.ResetAcknowledgementPumpForWorldChange();
            MonthlySettlement.SuspendForWorldChange();
            AssistantWidget.CancelForWorldExit();
            NpcProactiveChatScheduler.CancelForWorldExit();
            GroupMemberPicker.CancelForWorldExit();
            ResetWorldScopedUi();
            ChatWindow.ResetForWorldExit();
            TalkOrchestrator.ResetConversationCacheForWorldExit();
            NativeInteractionCapture.ResetForWorldExit();
            AssistantOrchestrator.ResetForWorldExit();
            PortraitService.ResetForWorldExit();
            CompanionMonthlyActions.CancelForWorldExit();
            MonthlyEventGenerator.CancelForWorldExit();
            Debug.Log("[江湖有灵] 已离开存档,停止后台世界动作(gen=" + _generation + ")");
        }
    }

    [HarmonyPatch(typeof(BasicGameData), "UpdateTaiwuDomainData")]
    internal static class WorldLifecycleTaiwuDataPatch
    {
        private static void Postfix(DataUid uid, BasicGameData __instance)
        {
            if (uid.DataId != 0 || __instance == null) return;
            WorldLifecycle.MarkTaiwuIdentityChanged(__instance.WorldId, __instance.TaiwuCharId);
        }
    }

    // BasicGameData 的世界域通知才是 WorldId/CurrDate 真正反序列化完成的权威时点。
    [HarmonyPatch(typeof(BasicGameData), "UpdateWorldDomainData")]
    internal static class WorldLifecycleBasicDataPatch
    {
        private static void Postfix(DataUid uid, BasicGameData __instance)
        {
            if (uid.DomainId != 1) return;
            if (uid.DataId == 0) WorldLifecycle.MarkWorldIdentityReady(__instance != null ? __instance.WorldId : 0);
            else if (uid.DataId == 26) WorldLifecycle.MarkWorldDateReady();
        }
    }

    [HarmonyPatch(typeof(GlobalOperations), nameof(GlobalOperations.OnWorldDataReady))]
    internal static class WorldLifecycleOnWorldDataReadyPatch
    {
        // 必须先失效上一世界，再让原方法注册 BasicGameData 的 WorldId/日期 monitor；
        // 即使某个桥接实现同步回送首包，也不会在 _active=false 时漏掉权威身份。
        private static void Prefix()
        {
            WorldLifecycle.MarkWorldReady();
        }
    }

    [HarmonyPatch(typeof(GlobalOperations), nameof(GlobalOperations.OnLeaveWorld))]
    internal static class WorldLifecycleOnLeaveWorldPatch
    {
        private static void Prefix()
        {
            WorldLifecycle.MarkWorldLeaving();
        }
    }
}
