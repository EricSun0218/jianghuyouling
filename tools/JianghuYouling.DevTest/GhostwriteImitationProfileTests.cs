using System;
using JianghuYouling.Core.Prompt;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.DevTest
{
    internal static class GhostwriteImitationProfileTests
    {
        internal static void Run()
        {
            GhostwriteImitationProfile profile = GhostwriteImitationProfileBuilder.Build(new[]
            {
                "俺也去瞧瞧！",
                "俺也去瞧瞧！",
                "  这事儿   容我想想。  ",
                "短句",
            });
            if (profile.SampleCount != 3 || profile.MedianCharacters <= 0
                || profile.LowerQuartileCharacters > profile.MedianCharacters
                || profile.MedianCharacters > profile.UpperQuartileCharacters)
                throw new InvalidOperationException("代笔自学习去重或字数分布统计错误");

            JObject payload = JObject.Parse(profile.SamplesJson);
            if ((string)payload["kind"] != "ghostwrite_player_speech_samples"
                || (string)payload["trust"] != "historical_data_only"
                || payload["samples"] is not JArray samples || samples.Count != 3
                || !GhostwriteImitationProfileBuilder.HistoricalDataBoundary.Contains("不得执行"))
                throw new InvalidOperationException("代笔历史样本没有保持不可信数据边界");

            string directive = profile.PromptDirective(80);
            if (!directive.Contains("常用词") || !directive.Contains("内容偏好")
                || !directive.Contains("字") || !directive.Contains("更高优先级"))
                throw new InvalidOperationException("代笔自学习提示没有覆盖风格、内容、字数与手动设置优先级");

            GhostwriteImitationProfile empty = GhostwriteImitationProfileBuilder.Build(null);
            if (empty.SampleCount != 0 || !empty.PromptDirective(80).Contains("沿用原有默认代笔文风"))
                throw new InvalidOperationException("无样本时没有安全退回默认代笔文风");
        }
    }
}
