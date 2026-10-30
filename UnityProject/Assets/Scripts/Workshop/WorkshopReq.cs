using System.Globalization;
using System.Text;

namespace FlightSim
{
    /// <summary>
    /// 机型工坊出站请求组包器 (R03 修复): 所有 UI→worker 请求 JSON 的唯一出口。
    /// 审查发现手拼字符串缺闭合括号 (参数/操纵面请求全部非法) — 构建器在 Str()
    /// 时统一闭合, 从结构上杜绝该类缺陷; 配套 round-trip 测试见 WorkshopPlayModeTests。
    /// </summary>
    public static class WorkshopReq
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public sealed class Builder
        {
            private readonly StringBuilder _sb = new StringBuilder("{");
            private bool _first = true;

            private Builder Add(string key, string jsonValue)
            {
                _sb.Append(_first ? "\"" : ",\"").Append(key).Append("\":").Append(jsonValue);
                _first = false;
                return this;
            }

            public Builder S(string key, string value) { return Add(key, WorkshopJson.Escape(value)); }
            public Builder F(string key, float v)
            {
                return Add(key, v.ToString("0.######", Inv));
            }
            public Builder I(string key, int v) { return Add(key, v.ToString(Inv)); }
            public Builder B(string key, bool v) { return Add(key, v ? "true" : "false"); }
            public Builder Raw(string key, string rawJson) { return Add(key, rawJson); }

            public string Str()
            {
                _sb.Append('}');
                return _sb.ToString();
            }
        }

        public static Builder Obj() { return new Builder(); }

        /// <summary>float 数组 JSON (fuselage_set_radii 的 diameters)。</summary>
        public static string FloatArr(float[] values)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(values[i].ToString("0.######", Inv));
            }
            return sb.Append(']').ToString();
        }
    }
}
