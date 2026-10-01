namespace JianghuYouling
{
    /// <summary>
    /// MiniMax 系统音色选择。旧版 minimax.json 凭据入口没有生产调用且可能误导为会复用主模型密钥，已移除；
    /// 所有语音 provider 统一只从 TtsConfig 的独立 tts.json 读取凭据。
    /// </summary>
    public static class MiniMaxConfig
    {
        /// <summary>只按性别(0女/1男)+年龄选择 MiniMax 系统音色。</summary>
        public static string PickVoice(int gender, int age, string features = null, string behavior = null)
        {
            if (gender == 0)
            {
                if (age >= 50) return "audiobook_female_1";
                if (age >= 40) return "female-chengshu";
                if (age >= 28) return "female-yujie";
                return "female-shaonv";
            }
            if (age >= 50) return "audiobook_male_1";
            if (age >= 30) return "presenter_male";
            return "male-qn-qingse";
        }

        public const string AssistantVoice = "female-tianmei";
    }
}
