using System.Text;

namespace FlightSim
{
    /// <summary>
    /// 机型工坊 W1: worker JSON-Lines 协议的轻量字段抽取。
    /// JsonUtility 不支持任意嵌套对象/字典, 协议层用本工具取标量字段与
    /// 原始子对象 (data), 调用方再按具体结构 FromJson。
    /// 仅覆盖协议所需: 顶层标量 + 顶层对象的原文抽取, 不是通用 JSON 解析器。
    /// </summary>
    public static class WorkshopJson
    {
    /// <summary>顶层字符串字段 (无该字段/非字符串返回 null)。处理 \" 与 \\ 转义。</summary>
    public static string Str(string json, string field)
    {
        if (string.IsNullOrEmpty(json)) return null;   // 上层 Raw 可能返回 null
        int i = FindTopLevel(json, "\"" + field + "\"");
        if (i < 0) return null;
            i = SkipWs(json, i + field.Length + 2);
            if (i >= json.Length || json[i] != ':') return null;
            i = SkipWs(json, i + 1);
            if (i >= json.Length || json[i] != '"') return null;
            i++;
            var sb = new StringBuilder();
            while (i < json.Length)
            {
                char c = json[i];
                if (c == '\\' && i + 1 < json.Length)
                {
                    char n = json[i + 1];
                    sb.Append(n == 'n' ? '\n' : n == 't' ? '\t' : n == 'r' ? '\r' : n);
                    i += 2;
                    continue;
                }
                if (c == '"') break;
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        /// <summary>顶层数值字段 (无/解析失败返回 def)。</summary>
        public static float Num(string json, string field, float def = 0f)
        {
            if (string.IsNullOrEmpty(json)) return def;
            int i = FindTopLevel(json, "\"" + field + "\"");
            if (i < 0) return def;
            i = SkipWs(json, i + field.Length + 2);
            if (i >= json.Length || json[i] != ':') return def;
            i = SkipWs(json, i + 1);
            int start = i;
            while (i < json.Length && (char.IsDigit(json[i]) || json[i] == '-' ||
                   json[i] == '+' || json[i] == '.' || json[i] == 'e' || json[i] == 'E'))
                i++;
            return float.TryParse(json.Substring(start, i - start),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : def;
        }

        public static int Int(string json, string field, int def = 0)
        {
            return (int)Num(json, field, def);
        }

        public static bool Bool(string json, string field, bool def = false)
        {
            if (string.IsNullOrEmpty(json)) return def;
            int i = FindTopLevel(json, "\"" + field + "\"");
            if (i < 0) return def;
            i = SkipWs(json, i + field.Length + 2);
            if (i >= json.Length || json[i] != ':') return def;
            i = SkipWs(json, i + 1);
            if (json.Substring(i).StartsWith("true")) return true;
            if (json.Substring(i).StartsWith("false")) return false;
            return def;
        }

        /// <summary>顶层对象/数组字段原文 (括号配平, 处理字符串内转义); 无返回 null。</summary>
        public static string Raw(string json, string field)
        {
            if (string.IsNullOrEmpty(json)) return null;
            int i = FindTopLevel(json, "\"" + field + "\"");
            if (i < 0) return null;
            i = SkipWs(json, i + field.Length + 2);
            if (i >= json.Length || json[i] != ':') return null;
            i = SkipWs(json, i + 1);
            if (i >= json.Length) return null;
            char open = json[i];
            if (open != '{' && open != '[') return null;
            char close = open == '{' ? '}' : ']';
            int depth = 0, instr = 0;
            for (int j = i; j < json.Length; j++)
            {
                char c = json[j];
                if (instr > 0)
                {
                    if (c == '\\') { j++; continue; }
                    if (c == '"') instr = 0;
                    continue;
                }
                if (c == '"') instr = 1;
                else if (c == open) depth++;
                else if (c == close)
                {
                    depth--;
                    if (depth == 0) return json.Substring(i, j - i + 1);
                }
            }
            return null;
        }

        /// <summary>
        /// 顶层 JSON 对象 (字典) 拆成 "键" → 值原文 对列表 (括号配平, 处理转义)。
        /// worker 的 mass_props.per_geom 是 {"部件名": {...}} 字典 (Python 侧
        /// dict 直转), 不是数组 — SplitArray 对它恒返回空, 换机 mass_map 因此
        /// 曾恒为空 (U06)。
        /// </summary>
        public static System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>>
            SplitObject(string jsonObject)
        {
            var outList = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>>();
            if (string.IsNullOrEmpty(jsonObject) || jsonObject[0] != '{') return outList;
            int len = jsonObject.Length;
            int i = 1;   // 跳过根 '{'
            while (i < len)
            {
                while (i < len && (jsonObject[i] == ' ' || jsonObject[i] == ',' ||
                       jsonObject[i] == '\t' || jsonObject[i] == '\n' || jsonObject[i] == '\r')) i++;
                if (i >= len || jsonObject[i] == '}') break;
                if (jsonObject[i] != '"') break;   // 非字符串键: 格式异常, 停止
                i++;
                var sb = new StringBuilder();
                while (i < len && jsonObject[i] != '"')
                {
                    if (jsonObject[i] == '\\' && i + 1 < len) { sb.Append(jsonObject[i + 1]); i += 2; continue; }
                    sb.Append(jsonObject[i]);
                    i++;
                }
                string key = sb.ToString();
                i++;   // 收尾引号
                while (i < len && (jsonObject[i] == ' ' || jsonObject[i] == ':')) i++;
                if (i >= len) break;
                int start = i;
                if (jsonObject[i] == '{' || jsonObject[i] == '[')
                {
                    int end = SkipBalanced(jsonObject, i, jsonObject[i], jsonObject[i] == '{' ? '}' : ']');
                    if (end < 0) break;
                    i = end + 1;
                }
                else
                    while (i < len && jsonObject[i] != ',' && jsonObject[i] != '}') i++;
                outList.Add(new System.Collections.Generic.KeyValuePair<string, string>(
                    key, jsonObject.Substring(start, i - start)));
            }
            return outList;
        }

        private static int SkipBalanced(string json, int start, char open, char close)
        {
            int d = 0, ins = 0;
            for (int j = start; j < json.Length; j++)
            {
                char c = json[j];
                if (ins > 0)
                {
                    if (c == '\\') { j++; continue; }
                    if (c == '"') ins = 0;
                    continue;
                }
                if (c == '"') ins = 1;
                else if (c == open) d++;
                else if (c == close && --d == 0) return j;
            }
            return -1;
        }

        /// <summary>C# 字符串 → JSON 字符串字面量 (含引号; 转义 \ 与 ")。</summary>
        public static string Escape(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                if (c == '\\' || c == '"') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        // ---- 内部: 顶层字段定位 (跳过嵌套对象/数组与字符串字面量) ----
        // "顶层" = 根容器的直接成员: 对象根的字段在 depth 1; 数组根的元素对象字段
        // 在 depth 2 (Raw 抽出的子 JSON 常是数组, 其元素对象字段要能命中)
        private static int FindTopLevel(string json, string token)
        {
            int depth = 0, instr = 0, target = -1;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (instr > 0)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"') instr = 0;
                    continue;
                }
                if (c == '"')
                {
                    // 引号既可能是目标字段名的开头, 也可能是普通字符串的开头
                    if (depth == target && json.Length - i >= token.Length &&
                        json.Substring(i, token.Length) == token)
                        return i;
                    instr = 1;
                    continue;
                }
                if (c == '{' || c == '[')
                {
                    depth++;
                    if (target < 0 && depth == 1)
                        target = c == '{' ? 1 : 2;
                }
                else if (c == '}' || c == ']') depth--;
            }
            return -1;
        }

        private static int SkipWs(string json, int i)
        {
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t')) i++;
            return i;
        }
    }
}
