using JianghuYouling.Core.Persona;

namespace JianghuYouling.Core.Prompt
{
    /// <summary>
    /// 内置默认世界书。正文来自用户提供的世界书，经可复现净化后作为 EmbeddedResource
    /// 编进 Core DLL；运行时不读取桌面文件。玩家自定义 replace/append 仍由 WorldBookFilter 处理。
    /// </summary>
    public static class DefaultWorldBook
    {
        public static string Text => SpecialPersonaCatalog.DefaultWorldBook;
    }
}
