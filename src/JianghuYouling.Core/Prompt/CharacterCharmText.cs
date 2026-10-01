using System.Globalization;

namespace JianghuYouling.Core.Prompt
{
    /// <summary>本体实时魅力值；负值表示资料未知，零是合法数值。</summary>
    public static class CharacterCharmText
    {
        public static string Format(int charm)
            => charm < 0 ? "未知（本体未提供）" : charm.ToString(CultureInfo.InvariantCulture);
    }
}
