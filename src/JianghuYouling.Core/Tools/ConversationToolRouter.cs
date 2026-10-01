using System.Collections.Generic;
using JianghuYouling.Core.Llm;

namespace JianghuYouling.Core.Tools
{
    /// <summary>
    /// 一轮对话使用的稳定工具表。工具不再按玩家措辞猜测意图、分能力包或中途补载；
    /// 唯一筛选来源是 ToolRegistry 中可由游戏现场确定的硬场景边界。
    /// </summary>
    public sealed class ConversationToolRoute
    {
        internal ConversationToolRoute(ToolContext context)
        {
            Context = context ?? new ToolContext();
            Tools = ToolRegistry.BuildConversationTools(Context);
        }

        public ToolContext Context { get; }
        public List<ToolDef> Tools { get; }
    }

    /// <summary>
    /// 保留统一入口供单聊与群聊构造工具现场。入口不接收玩家输入，
    /// 从类型上保证不同话题之间的工具 schema 与顺序稳定并利于前缀缓存。
    /// </summary>
    public static class ConversationToolRouter
    {
        public static ConversationToolRoute Create(ToolContext context)
            => new ConversationToolRoute(context);

        public static ConversationToolRoute CreateGroup(ToolContext context)
        {
            context = context ?? new ToolContext();
            return new ConversationToolRoute(new ToolContext
            {
                IsMerchant = context.IsMerchant,
                InSect = context.InSect,
                Remote = context.Remote,
                NpcInitiated = context.NpcInitiated,
                ConversationOnly = context.ConversationOnly,
                IsGroup = true,
                // 群聊没有一个唯一的当前交谈对手，不能在此直接排定原生单挑。
                CanStartCombat = false,
                // 梳头修面需要一个唯一目标和玩家随后操作本体界面，群聊不开放。
                CanOpenGrooming = false,
            });
        }
    }
}
