namespace JianghuYouling.Core.Tools
{
    public static class TaiwuActionAuthorization
    {
        public static bool AllowsModelTool(string toolName)
            => toolName != "taiwu_teach" && toolName != "taiwu_give_item"
                && toolName != "taiwu_write_book";
    }
}
