using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JianghuYouling.Core.Llm
{
    /// <summary>
    /// 工具是游戏副作用边界。模型输出必须在执行前按本轮 ToolDef 做严格本地校验，
    /// 不能依赖 provider 的 strict 模式，也不能把未知字段静默交给执行器。
    /// </summary>
    public static class ToolArgumentsValidator
    {
        private static readonly Regex ToolName = new Regex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);

        /// <summary>
        /// Normalize a very small allowlist of common, read-only provider aliases before
        /// schema validation.  Never alias state-changing tools or accept arbitrary names.
        /// </summary>
        public static string NormalizeKnownReadOnlyAlias(string name,
            IList<ToolDef> allowedTools = null, string argumentsJson = null)
        {
            // A few providers occasionally translate a semantically obvious read-only
            // function name even though the exact canonical schema name was supplied.
            // Keep this list explicit and read-only: aliases for mutations would turn a
            // provider naming mistake into a real game-state change.
            bool Has(string canonical)
            {
                if (allowedTools == null) return true;
                foreach (ToolDef tool in allowedTools)
                    if (tool != null && string.Equals(tool.Name, canonical, StringComparison.Ordinal))
                        return true;
                return false;
            }
            switch (name)
            {
                case "query_current_block_location":
                    return Has("query_current_block") ? "query_current_block" : name;
                case "query_character_info":
                    // With a concrete name this means a third-party lookup; without one it
                    // means the current speaking NPC. Resolve only to a canonical tool that
                    // actually exists in this scene.
                    bool namesThirdParty = false;
                    if (!string.IsNullOrWhiteSpace(argumentsJson))
                    {
                        try
                        {
                            JObject args = JObject.Parse(argumentsJson);
                            namesThirdParty = !string.IsNullOrWhiteSpace(args["name"]?.ToString());
                        }
                        catch { }
                    }
                    if (namesThirdParty && Has("query_person")) return "query_person";
                    return Has("query_npc_status") ? "query_npc_status" : name;
                case "query_character_relationships":
                case "query_relationships":
                    return Has("query_npc_relationships") ? "query_npc_relationships" : name;
                default: return name;
            }
        }

        public static bool ValidateDefinitions(IList<ToolDef> tools, out string error)
        {
            error = null;
            if (tools == null) return true;
            if (tools.Count > LlmProtocolLimits.MaxToolDefinitions)
            {
                error = "工具定义过多(上限 " + LlmProtocolLimits.MaxToolDefinitions + ")";
                return false;
            }
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tool in tools)
            {
                if (tool == null || string.IsNullOrWhiteSpace(tool.Name) || !ToolName.IsMatch(tool.Name))
                {
                    error = "工具定义包含非法名称";
                    return false;
                }
                if (!names.Add(tool.Name))
                {
                    error = "工具定义名称重复:" + tool.Name;
                    return false;
                }
                JObject schema = tool.Parameters ?? EmptyObjectSchema();
                if (!ValidateSchemaDefinition(schema, "$", 0, out error))
                {
                    error = "工具 " + tool.Name + " schema 无效:" + error;
                    return false;
                }
            }
            return true;
        }

        public static bool ValidateCall(IList<ToolDef> tools, string name, string argumentsJson, out string error)
        {
            error = null;
            name = NormalizeKnownReadOnlyAlias(name, tools, argumentsJson);
            if (string.IsNullOrWhiteSpace(name) || name.Length > LlmProtocolLimits.MaxToolNameChars)
            {
                error = "工具函数名无效";
                return false;
            }
            ToolDef match = null;
            if (tools != null)
                foreach (var tool in tools)
                    if (tool != null && string.Equals(tool.Name, name, StringComparison.Ordinal))
                    {
                        if (match != null) { error = "本轮工具定义重名:" + name; return false; }
                        match = tool;
                    }
            if (match == null)
            {
                error = "模型调用了当前场景未提供的工具:" + name;
                return false;
            }
            if (argumentsJson == null)
            {
                error = "工具参数缺失";
                return false;
            }
            if (Encoding.UTF8.GetByteCount(argumentsJson) > LlmProtocolLimits.MaxToolArgumentsBytes)
            {
                error = "工具参数超过 " + LlmProtocolLimits.MaxToolArgumentsBytes + " 字节";
                return false;
            }

            JToken value;
            try
            {
                using (var sr = new System.IO.StringReader(argumentsJson))
                using (var reader = new StrictJsonTextReader(sr)
                {
                    MaxDepth = LlmProtocolLimits.MaxJsonDepth,
                    DateParseHandling = DateParseHandling.None,
                    FloatParseHandling = FloatParseHandling.Decimal,
                })
                {
                    value = JToken.ReadFrom(reader, new JsonLoadSettings
                    {
                        CommentHandling = CommentHandling.Ignore,
                        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                    });
                    if (reader.Read()) throw new JsonReaderException("参数 JSON 后有额外内容");
                }
            }
            catch (Exception ex)
            {
                error = "工具参数不是完整 JSON:" + ex.GetType().Name;
                return false;
            }
            if (value.Type != JTokenType.Object)
            {
                error = "工具参数必须是 JSON object";
                return false;
            }
            JObject callSchema = match.Parameters ?? EmptyObjectSchema();
            if (!ValidateSchemaDefinition(callSchema, "$", 0, out error))
            {
                error = "本轮工具 schema 无效:" + error;
                return false;
            }
            return ValidateValue(value, callSchema, "$", 0, out error);
        }

        private static bool ValidateValue(JToken value, JObject schema, string path, int depth, out string error)
        {
            error = null;
            if (depth > LlmProtocolLimits.MaxJsonDepth) { error = path + " 嵌套过深"; return false; }
            string type = schema?["type"]?.ToString();
            JToken validatedValue = NormalizeIntegerString(value, type);
            if (!MatchesType(validatedValue, type))
            {
                error = path + " 类型应为 " + (type ?? "已声明类型") + "，实为 " + value.Type;
                return false;
            }

            var enumValues = schema?["enum"] as JArray;
            if (enumValues != null)
            {
                bool matched = false;
                foreach (var allowed in enumValues)
                    if (JToken.DeepEquals(allowed, validatedValue)) { matched = true; break; }
                if (!matched) { error = path + " 不在允许枚举中"; return false; }
            }

            if (validatedValue.Type == JTokenType.Integer || validatedValue.Type == JTokenType.Float)
            {
                decimal number;
                try { number = validatedValue.Value<decimal>(); }
                catch { error = path + " 不是有限数字"; return false; }
                if (type == "integer" && (number < int.MinValue || number > int.MaxValue))
                { error = path + " 超出 32 位整数范围"; return false; }
                if (!CheckMinimum(number, schema, false, "minimum", out error)
                    || !CheckMinimum(number, schema, true, "exclusiveMinimum", out error)
                    || !CheckMaximum(number, schema, false, "maximum", out error)
                    || !CheckMaximum(number, schema, true, "exclusiveMaximum", out error))
                { error = path + " " + error; return false; }
            }

            if (value.Type == JTokenType.String)
            {
                string text = value.Value<string>() ?? "";
                int min = ReadNonNegativeInt(schema?["minLength"], -1);
                int max = ReadNonNegativeInt(schema?["maxLength"], -1);
                if (min >= 0 && text.Length < min) { error = path + " 长度小于 " + min; return false; }
                if (max >= 0 && text.Length > max) { error = path + " 长度大于 " + max; return false; }
            }

            if (value.Type == JTokenType.Object)
            {
                var obj = (JObject)value;
                var properties = schema?["properties"] as JObject ?? new JObject();
                var required = schema?["required"] as JArray;
                if (required != null)
                {
                    foreach (var requiredName in required)
                    {
                        if (requiredName.Type != JTokenType.String) { error = path + " schema required 非字符串"; return false; }
                        string key = requiredName.Value<string>();
                        if (obj.Property(key, StringComparison.Ordinal) == null)
                        { error = path + " 缺少必填字段 " + key; return false; }
                    }
                }
                JToken additional = schema?["additionalProperties"];
                foreach (var prop in obj.Properties())
                {
                    var childSchema = properties[prop.Name] as JObject;
                    if (childSchema == null)
                    {
                        if (additional == null || (additional.Type == JTokenType.Boolean && !additional.Value<bool>()))
                        { error = path + " 含未声明字段 " + prop.Name; return false; }
                        childSchema = additional as JObject;
                        if (childSchema == null) continue;
                    }
                    if (!ValidateValue(prop.Value, childSchema, path + "." + prop.Name, depth + 1, out error)) return false;
                }
            }
            else if (value.Type == JTokenType.Array)
            {
                var arr = (JArray)value;
                int min = ReadNonNegativeInt(schema?["minItems"], -1);
                int max = ReadNonNegativeInt(schema?["maxItems"], -1);
                if (min >= 0 && arr.Count < min) { error = path + " 项数小于 " + min; return false; }
                if (max >= 0 && arr.Count > max) { error = path + " 项数大于 " + max; return false; }
                var itemSchema = schema?["items"] as JObject;
                if (itemSchema != null)
                    for (int i = 0; i < arr.Count; i++)
                        if (!ValidateValue(arr[i], itemSchema, path + "[" + i + "]", depth + 1, out error)) return false;
            }
            return true;
        }

        private static JToken NormalizeIntegerString(JToken value, string type)
        {
            if (type != "integer" || value?.Type != JTokenType.String) return value;
            string text = value.Value<string>();
            if (string.IsNullOrWhiteSpace(text) || !string.Equals(text, text.Trim(), StringComparison.Ordinal)
                || !int.TryParse(text, NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out int parsed)) return value;
            return new JValue(parsed);
        }

        private static bool ValidateSchemaDefinition(JObject schema, string path, int depth, out string error)
        {
            error = null;
            if (schema == null) { error = path + " 不是 object"; return false; }
            if (depth > LlmProtocolLimits.MaxJsonDepth) { error = path + " 嵌套过深"; return false; }
            foreach (var property in schema.Properties())
                if (!SupportedSchemaKeyword(property.Name))
                { error = path + " 含本地校验器不支持的 schema 关键字 " + property.Name; return false; }
            string type = schema["type"]?.ToString();
            if (type != "object" && type != "array" && type != "string" && type != "integer"
                && type != "number" && type != "boolean" && type != "null")
            { error = path + " 缺少或含不支持的 type"; return false; }
            if (type == "object")
            {
                var props = schema["properties"] as JObject;
                if (props == null) { error = path + " object 缺 properties"; return false; }
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in props.Properties())
                {
                    if (!names.Add(p.Name) || !(p.Value is JObject child)) { error = path + "." + p.Name + " schema 无效"; return false; }
                    if (!ValidateSchemaDefinition(child, path + "." + p.Name, depth + 1, out error)) return false;
                }
                var required = schema["required"] as JArray;
                if (schema["required"] != null && required == null) { error = path + " required 不是数组"; return false; }
                if (required != null)
                {
                    var req = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var token in required)
                    {
                        string name = token.Type == JTokenType.String ? token.Value<string>() : null;
                        if (name == null || !names.Contains(name) || !req.Add(name))
                        { error = path + " required 引用了未知/重复字段"; return false; }
                    }
                }
                JToken additional = schema["additionalProperties"];
                if (additional != null && additional.Type != JTokenType.Boolean && !(additional is JObject))
                { error = path + " additionalProperties 无效"; return false; }
                if (additional is JObject addSchema && !ValidateSchemaDefinition(addSchema, path + ".*", depth + 1, out error)) return false;
            }
            if (type == "array")
            {
                var items = schema["items"] as JObject;
                if (items == null) { error = path + " array 缺 items"; return false; }
                if (!ValidateSchemaDefinition(items, path + "[]", depth + 1, out error)) return false;
            }
            if (!ValidateCommonConstraints(schema, type, path, out error)) return false;
            return true;
        }

        private static bool SupportedSchemaKeyword(string name)
        {
            switch (name)
            {
                case "type": case "description": case "enum":
                case "properties": case "required": case "additionalProperties":
                case "items": case "minItems": case "maxItems":
                case "minLength": case "maxLength":
                case "minimum": case "exclusiveMinimum": case "maximum": case "exclusiveMaximum":
                    return true;
                default: return false;
            }
        }

        private static bool ValidateCommonConstraints(JObject schema, string type, string path, out string error)
        {
            error = null;
            var enumValues = schema["enum"];
            if (enumValues != null)
            {
                if (!(enumValues is JArray values) || values.Count == 0)
                { error = path + " enum 必须是非空数组"; return false; }
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var value in values)
                {
                    if (!MatchesType(value, type)) { error = path + " enum 值与 type 不匹配"; return false; }
                    string canonical = value.ToString(Newtonsoft.Json.Formatting.None);
                    if (!seen.Add(canonical)) { error = path + " enum 含重复值"; return false; }
                }
            }

            if (schema["description"] != null && schema["description"].Type != JTokenType.String)
            { error = path + " description 不是字符串"; return false; }

            if (type == "string")
            {
                if (!ReadSchemaNonNegativeInt(schema, "minLength", out int min, out error)
                    || !ReadSchemaNonNegativeInt(schema, "maxLength", out int max, out error))
                { error = path + " " + error; return false; }
                if (min >= 0 && max >= 0 && min > max)
                { error = path + " minLength 大于 maxLength"; return false; }
            }
            else if (schema["minLength"] != null || schema["maxLength"] != null)
            { error = path + " 非 string 使用了长度约束"; return false; }

            if (type == "array")
            {
                if (!ReadSchemaNonNegativeInt(schema, "minItems", out int min, out error)
                    || !ReadSchemaNonNegativeInt(schema, "maxItems", out int max, out error))
                { error = path + " " + error; return false; }
                if (min >= 0 && max >= 0 && min > max)
                { error = path + " minItems 大于 maxItems"; return false; }
            }
            else if (schema["minItems"] != null || schema["maxItems"] != null)
            { error = path + " 非 array 使用了项数约束"; return false; }

            string[] numericKeys = { "minimum", "exclusiveMinimum", "maximum", "exclusiveMaximum" };
            if (type == "integer" || type == "number")
            {
                foreach (string key in numericKeys)
                    if (!ReadSchemaDecimal(schema, key, out _, out error))
                    { error = path + " " + error; return false; }
                if (!EffectiveLowerBound(schema, out decimal lower, out bool hasLower, out bool lowerExclusive, out error)
                    || !EffectiveUpperBound(schema, out decimal upper, out bool hasUpper, out bool upperExclusive, out error))
                { error = path + " " + error; return false; }
                if (hasLower && hasUpper && (lower > upper
                    || (lower == upper && (lowerExclusive || upperExclusive))))
                { error = path + " 数值上下界无可行区间"; return false; }
            }
            else
                foreach (string key in numericKeys)
                    if (schema[key] != null) { error = path + " 非数值 type 使用了 " + key; return false; }
            return true;
        }

        private static bool ReadSchemaNonNegativeInt(JObject schema, string key, out int value, out string error)
        {
            value = -1; error = null;
            var token = schema[key];
            if (token == null) return true;
            if (token.Type != JTokenType.Integer)
            { error = key + " 必须是非负整数"; return false; }
            try
            {
                long parsed = token.Value<long>();
                if (parsed < 0 || parsed > int.MaxValue) throw new OverflowException();
                value = (int)parsed; return true;
            }
            catch { error = key + " 必须是非负 32 位整数"; return false; }
        }

        private static bool ReadSchemaDecimal(JObject schema, string key, out decimal value, out string error)
        {
            value = 0; error = null;
            var token = schema[key];
            if (token == null) return true;
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
            { error = key + " 必须是数值"; return false; }
            try { value = token.Value<decimal>(); return true; }
            catch { error = key + " 不是有限 decimal"; return false; }
        }

        private static bool EffectiveLowerBound(JObject schema, out decimal value, out bool present,
            out bool exclusive, out string error)
        {
            value = decimal.MinValue; present = false; exclusive = false; error = null;
            foreach (string key in new[] { "minimum", "exclusiveMinimum" })
                if (schema[key] != null)
                {
                    if (!ReadSchemaDecimal(schema, key, out decimal candidate, out error)) return false;
                    bool candidateExclusive = key == "exclusiveMinimum";
                    if (!present || candidate > value)
                    { value = candidate; exclusive = candidateExclusive; }
                    else if (candidate == value && candidateExclusive) exclusive = true;
                    present = true;
                }
            return true;
        }

        private static bool EffectiveUpperBound(JObject schema, out decimal value, out bool present,
            out bool exclusive, out string error)
        {
            value = decimal.MaxValue; present = false; exclusive = false; error = null;
            foreach (string key in new[] { "maximum", "exclusiveMaximum" })
                if (schema[key] != null)
                {
                    if (!ReadSchemaDecimal(schema, key, out decimal candidate, out error)) return false;
                    bool candidateExclusive = key == "exclusiveMaximum";
                    if (!present || candidate < value)
                    { value = candidate; exclusive = candidateExclusive; }
                    else if (candidate == value && candidateExclusive) exclusive = true;
                    present = true;
                }
            return true;
        }

        private static bool MatchesType(JToken value, string type)
        {
            switch (type)
            {
                case "object": return value.Type == JTokenType.Object;
                case "array": return value.Type == JTokenType.Array;
                case "string": return value.Type == JTokenType.String;
                case "integer": return value.Type == JTokenType.Integer;
                case "number": return value.Type == JTokenType.Integer || value.Type == JTokenType.Float;
                case "boolean": return value.Type == JTokenType.Boolean;
                case "null": return value.Type == JTokenType.Null;
                default: return false;
            }
        }

        private static bool CheckMinimum(decimal number, JObject schema, bool exclusive, string key, out string error)
        {
            error = null;
            if (schema?[key] == null) return true;
            decimal limit;
            try { limit = schema[key].Value<decimal>(); } catch { error = key + " 无效"; return false; }
            if (exclusive ? number <= limit : number < limit) { error = (exclusive ? "必须大于 " : "不得小于 ") + limit; return false; }
            return true;
        }

        private static bool CheckMaximum(decimal number, JObject schema, bool exclusive, string key, out string error)
        {
            error = null;
            if (schema?[key] == null) return true;
            decimal limit;
            try { limit = schema[key].Value<decimal>(); } catch { error = key + " 无效"; return false; }
            if (exclusive ? number >= limit : number > limit) { error = (exclusive ? "必须小于 " : "不得大于 ") + limit; return false; }
            return true;
        }

        private static int ReadNonNegativeInt(JToken token, int fallback)
        {
            if (token == null) return fallback;
            try { int v = token.Value<int>(); return v < 0 ? fallback : v; }
            catch { return fallback; }
        }

        private static JObject EmptyObjectSchema() => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject(),
            ["additionalProperties"] = false,
        };

    }
}
