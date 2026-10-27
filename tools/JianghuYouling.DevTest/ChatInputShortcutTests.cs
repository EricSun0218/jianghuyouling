using System;
using System.Linq;

namespace JianghuYouling.DevTest
{
    internal static class ChatInputShortcutTests
    {
        internal static void Run()
        {
            string input = ReadRepoFile("src", "JianghuYouling.Frontend", "UI", "SafeChatInputField.cs");
            Require(input.Contains("EventModifiers.Control | EventModifiers.Shift"),
                "Shift+Enter 必须与 Ctrl+Enter 一样进入 TMP 原生换行路径");
            Require(!input.Contains("Input.GetKey("),
                "换行快捷键必须读取缓冲事件修饰键，不能依赖帧级按键状态");

            string chat = ReadRepoFile("src", "JianghuYouling.Frontend", "UI", "ChatWindow.cs");
            Require(chat.Contains("Ctrl/Shift+Enter 换行"),
                "聊天框必须向玩家展示 Shift+Enter 换行快捷键");
            Require(chat.Contains("Ctrl/Shift+Enter 只换行不发送"),
                "帮助文本必须说明 Shift+Enter 只换行不发送");
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
