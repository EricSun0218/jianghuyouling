using System;
using System.Linq;
using JianghuYouling.Core.Tools;

namespace JianghuYouling.DevTest
{
    internal static class TaiwuActionAuthorizationTests
    {
        internal static void Run()
        {
            RejectModelTool("taiwu_teach");
            RejectModelTool("taiwu_give_item");
            RejectModelTool("taiwu_write_book");
            Require(TaiwuActionAuthorization.AllowsModelTool("query_taiwu_skills"),
                "只读查询不应受太吾按钮行动边界影响");
            Require(TaiwuActionAuthorization.AllowsModelTool("gift"),
                "NPC 自主赠出自己的物品不应受太吾按钮行动边界影响");

            string talk = ReadRepoFile("src", "JianghuYouling.Frontend", "Talk", "TalkOrchestrator.cs");
            int dispatchGuard = talk.IndexOf("yield return BuildGroupDispatchGuard(",
                StringComparison.Ordinal);
            int resourceClaim = talk.IndexOf("TryClaimResolved(", StringComparison.Ordinal);
            Require(dispatchGuard >= 0 && resourceClaim > dispatchGuard
                && talk.Contains("TaiwuActionAuthorization.AllowsModelTool(toolName)"),
                "太吾按钮行动必须在资源占用和实际派发之前被拒绝");
            Require(talk.Contains("此类太吾付出只能由太吾使用对话窗按钮确认执行"),
                "被拦截的模型必须收到不得假称收下的明确回执");
            string registry = ReadRepoFile("src", "JianghuYouling.Core", "Tools", "ToolRegistry.cs");
            Require(registry.Contains("只能由太吾使用对话窗按钮确认执行"),
                "工具说明必须引导玩家使用按钮，而不是让 NPC 替太吾付出");
        }

        private static void RejectModelTool(string tool)
        {
            Require(!TaiwuActionAuthorization.AllowsModelTool(tool),
                tool + " 不得由普通聊天模型直接执行");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static string ReadRepoFile(params string[] parts)
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            for (int depth = 0; dir != null && depth < 12; depth++, dir = dir.Parent)
            {
                string path = parts.Aggregate(dir.FullName, System.IO.Path.Combine);
                if (System.IO.File.Exists(path)) return System.IO.File.ReadAllText(path);
            }
            throw new System.IO.FileNotFoundException("找不到仓库源码", System.IO.Path.Combine(parts));
        }
    }
}
