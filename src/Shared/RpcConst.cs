namespace JianghuYouling.Shared
{
    /// <summary>前后端 RPC 共享常量(以链接源码形式同时编入前端 net48 与后端 net8.0 两工程)。</summary>
    public static class RpcConst
    {
        public const string PingMethod = "Ping";   // 方法名,前后端必须一致
        public const string ExecuteRelationMethod = "JYL_ExecuteRelation";  // 建立挚友/结义/师徒(需后端权限)
        public const string AddGoalMethod = "JYL_AddGoal";  // 过月给 NPC 注入行动目标 goal(需后端权限)
        public const string FilterDeadMethod = "JYL_FilterDead";  // 权威区分活人、真死者与剧情退场/失效人物；真死者仅标档案状态，不删除内容
        public const string RecognizeMethod = "JYL_Recognize";    // 令 NPC 认可太吾(归心;仅门派/据点成员有效)
        public const string ChangeMoralityMethod = "JYL_ChangeMorality";  // 过月立场漂移(改道德值)
        public const string ResolveCharMethod = "JYL_ResolveChar";        // 在说话者关系网里把具名第三方解析成 charId
        public const string ChangeAlertnessMethod = "JYL_ChangeAlertness"; // NPC 对太吾戒备升降(联动好感上限)
        public const string ReleaseMethod = "JYL_Release";                // NPC 被劝后放走所掳之人/败者
        public const string TeachSkillMethod = "JYL_TeachSkill";          // 传功:按 NPC 练法(正/逆练+进度)传授,try/catch 兜住"已学"等异常防后端崩
        public const string GmMethod = "JYL_Gm";                          // 通用 GM 分发器:好感/赠银/赠物/换装/卸下走后端 try/catch 通道(防失效角色/错槽冲垮后端)
        public const string LeaveMethod = "JYL_Leave";                    // 离去:退地图跟随名单 + (若在队伍)退队伍 LeaveGroup;后者对不在队伍者会抛异常,故走后端先判后退
        public const string JoinTeamMethod = "JYL_JoinTeam";              // 入队:JoinGroup 让 NPC 真正加入太吾队伍(设 LeaderId+入太吾村);GetElement_Objects 对失效角色会抛,故后端 try/catch
        public const string EnsureCharacterProxyMethod = "JYL_EnsureCharacterProxy"; // 固定模板人物首次实际发言或开启互动记录时建立持久副本；后续所有 mod 行为只使用副本 id
        public const string MatchmakeMethod = "JYL_Matchmake";            // 男媒女约:撮合两个角色成婚(ApplyBecomeHusbandOrWife);校验异性/成年/未感染/可婚 + 落地校验,后端 try/catch
        public const string IsTeammateMethod = "JYL_IsTeammate";          // 队友判定:DomainManager.Taiwu.IsInGroup(npc);供"自定义人设"入口前置校验(非队友则拒并提示)
        public const string QueryTaiwuSkillsMethod = "JYL_QueryTaiwuSkills"; // 只读:直接从后端 Character 读取太吾完整已学武学/技艺
        public const string KillMethod = "JYL_Kill";                      // 杀人:MakeCharacterDead 取目标性命;硬校验施法者精纯>=目标(GetConsummateLevel，相等可成);后端 try/catch
        public const string CaptureMethod = "JYL_Capture";                // 绑人/擒拿:AddKidnappedCharacter 掳为俘虏;硬校验精纯>=目标(相等可成) + 背包有真绳子(type12 tpl82-90,不自动造);后端 try/catch
        public const string StartCombatMethod = "JYL_StartCombat";        // 带 operation ledger 的对话战斗入口；后端最终复核目标存活、未被绑架且仍与太吾同场
        public const string QueryOperationMethod = "JYL_QueryOperation";  // 查询持久化的副作用回执，用于 RPC 回包丢失后恢复
        public const string AckOperationMethod = "JYL_AckOperation";      // 前端 durable journal 已提交终态后确认回执；后端方可压缩完整回执
        public const string QueryNativeInteractionAuditMethod = "JYL_QueryNativeInteractionAudit"; // 原版人物互动落地审计：仅回传后端确认的真实状态写入

        // 副作用 RPC v2 统一协议。operation_id 为 32 位小写 hex；未 ACK 回执绝不淘汰，
        // 已 ACK 回执可压缩为只防重、不再携带业务字段的存档墓碑。
        public const int OperationProtocolVersion = 2;
        public const string OperationIdField = "operation_id";
        public const string OperationStatusField = "status";
        public const string OperationCodeField = "code";
        public const string OperationRetryableField = "retryable";
        public const string OperationReceiptField = "receipt";
        // A receipt is ACK-eligible only after the backend wrote it and read it back.
        // The integrity digest binds every persisted primitive field except itself.
        public const string OperationReceiptPersistedField = "operation_receipt_persisted";
        public const string OperationReceiptIntegrityField = "operation_integrity_sha256";
        // mutation/query 必须绑定实际载入的世界与当前太吾；cleanup-only ACK 绑定原始
        // (WorldId,TaiwuId,operationId)，允许同世界传承后清理旧太吾回执但绝不改绑到新太吾。
        // WorldId 用十进制 string 传输，避免 SerializableModData 只有 int 而权威 WorldId 为 uint 的截断。
        public const string OperationWorldIdField = "operation_world_id";
        public const string OperationTaiwuIdField = "operation_taiwu_id";
        public const string OperationGroupPhysicalGuardField = "operation_group_physical_guard";
        public const string OperationGroupActorIdField = "operation_group_actor_id";
        public const string OperationGroupPhysicalEndpointsField = "operation_group_physical_endpoints";
        public const string OperationGroupPhysicalActorSceneField = "operation_group_physical_actor_scene";

        // 拿不到游戏注入的 ModIdStr 时的回退占位。正常情况下前后端都用游戏注入的真实 ModIdStr。
        public const string FallbackModId = "JianghuYouling";
    }
}
