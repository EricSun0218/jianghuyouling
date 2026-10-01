using Newtonsoft.Json.Linq;

namespace JianghuYouling.Core.Llm
{
    /// <summary>一个工具(function)定义,序列化为 OpenAI tools 数组项。Parameters 为 JSON Schema。</summary>
    public sealed class ToolDef
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public JObject Parameters { get; set; }   // { "type":"object", "properties":{...}, "required":[...] }

        public JObject ToJson() => new JObject
        {
            ["type"] = "function",
            ["function"] = new JObject
            {
                ["name"] = Name,
                ["description"] = Description ?? "",
                ["parameters"] = Parameters ?? new JObject
                {
                    ["type"] = "object", ["properties"] = new JObject(), ["additionalProperties"] = false,
                },
            },
        };

        public static ToolDef Of(string name, string desc, JObject parameters = null) =>
            new ToolDef { Name = name, Description = desc, Parameters = parameters };

        // —— Schema 便捷构造 ——
        public static JObject Obj(params (string key, JObject prop, bool required)[] fields)
        {
            var props = new JObject();
            var req = new JArray();
            foreach (var (key, prop, required) in fields)
            {
                props[key] = prop;
                if (required) req.Add(key);
            }
            // Mod 工具会产生真实游戏副作用；默认禁止模型夹带 schema 外字段。
            var schema = new JObject
            {
                ["type"] = "object",
                ["properties"] = props,
                ["additionalProperties"] = false,
            };
            if (req.Count > 0) schema["required"] = req;
            return schema;
        }
        public static JObject Str(string desc = null) { var o = new JObject { ["type"] = "string" }; if (desc != null) o["description"] = desc; return o; }
        public static JObject Int(string desc = null) { var o = new JObject { ["type"] = "integer" }; if (desc != null) o["description"] = desc; return o; }
        public static JObject Int(string desc, int minimum, int maximum)
        {
            var o = Int(desc); o["minimum"] = minimum; o["maximum"] = maximum; return o;
        }
        public static JObject Bool(string desc = null) { var o = new JObject { ["type"] = "boolean" }; if (desc != null) o["description"] = desc; return o; }
        public static JObject Sel(string desc, params string[] values)
        {
            var arr = new JArray(); foreach (var v in values) arr.Add(v);
            var o = new JObject { ["type"] = "string", ["enum"] = arr };
            if (desc != null) o["description"] = desc;
            return o;
        }
    }
}
