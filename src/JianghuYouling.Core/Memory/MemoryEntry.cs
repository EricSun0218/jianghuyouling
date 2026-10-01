namespace JianghuYouling.Core.Memory
{
    public enum MemoryType { Impression, Favor, Grudge, Promise, Secret, Event }

    /// <summary>NPC 主观记住的一件事(仿文件式记忆的一条)。</summary>
    public sealed class MemoryEntry
    {
        public string Id { get; set; }
        public string Content { get; set; }     // NPC 第一人称
        public MemoryType Type { get; set; }
        public string Keywords { get; set; }    // 召回索引(关键词,分隔)
        public long WorldDate { get; set; }     // 写入时的世界日期(月)
        public int Importance { get; set; }     // 1..10
        public bool Valid { get; set; } = true;
        public long LastRecalled { get; set; }
        // —— 巩固/晋升信号:被想起越多 / 跨越越多个月 → 越该长留、越往前排 ——
        public int RecallCount { get; set; }    // 累计被召回次数(频次)
        public int RecallMonths { get; set; }   // 跨多少个不同"过月"仍被想起(巩固跨度;太吾按月推进,计月不计日)
        public bool Core { get; set; }          // 常驻核心记忆(誓约/血仇/归心等):永不衰减、召回必带

        // 可选溯源：群聊/事件等可重放数据用稳定 SourceId 做幂等 upsert，并在删除/重试时精确撤回。
        // 旧记忆没有这些字段，JSON 向后兼容。
        public string SourceKind { get; set; }
        public string SourceId { get; set; }
        public System.Collections.Generic.List<string> SourceLineIds { get; set; }

        /// <summary>核心档 = 显式标记 或 重要度≥9(高分大事)。核心记忆免衰减、淘汰必留、召回必带。</summary>
        [Newtonsoft.Json.JsonIgnore]
        public bool IsCore => Core || Importance >= 9;
    }
}
