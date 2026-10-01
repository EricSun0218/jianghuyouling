namespace JianghuYouling.Core.Behavior
{
    /// <summary>
    /// 一条"过月生效"的意图,排进 IntentQueue、月度结算消费。
    /// 注:普通前往/寻人由 goto_place 立即安排有期限行程，显式赴约及营救/保护/追杀等按各自权威目标落地，不再走此队列;
    /// 现在本队列仅保留【立场漂移(MoralityDelta)】一种过月消费。
    /// </summary>
    public sealed class BehaviorIntent
    {
        public string Id { get; set; }
        public int NpcId { get; set; }
        public int TaiwuId { get; set; }
        public string Kind { get; set; }    // 如 保护某人/放下仇恨/投奔门派/赴约/前往某地;纯立场漂移时可空
        public string Target { get; set; }  // 目标人或地点(自然语言,过月审议时解析)
        public string Note { get; set; }    // NPC 为何要这么做(画像依据)
        public int MoralityDelta { get; set; } // 立场漂移道德值 delta(过月生效;非 0 即一条纯漂移意图,Kind 可空)
        public int WorldDate { get; set; }  // 种下时的世界日期
    }
}
