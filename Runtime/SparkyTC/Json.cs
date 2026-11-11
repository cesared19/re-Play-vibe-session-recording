// Only in the Editor (the window and tests): the headset never talks to SparkyTC.
#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace XRSessionCapture.SparkyTC
{
    /// <summary>
    /// Writes the JSON bodies sent to SparkyTC. JsonUtility can't leave a value out (it writes 0 for a missing number),
    /// so optional values are skipped here when they're null.
    /// </summary>
    public sealed class Json
    {
        readonly List<(string key, string json)> m_Fields = new List<(string, string)>();

        public Json Add(string key, string value)
        {
            if (value != null)
                m_Fields.Add((key, Quote(value)));
            return this;
        }

        public Json Add(string key, bool? value)
        {
            if (value.HasValue)
                m_Fields.Add((key, value.Value ? "true" : "false"));
            return this;
        }

        public Json Add(string key, long? value)
        {
            if (value.HasValue)
                m_Fields.Add((key, value.Value.ToString(CultureInfo.InvariantCulture)));
            return this;
        }

        public Json Add(string key, double? value)
        {
            if (value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value))
                m_Fields.Add((key, value.Value.ToString("R", CultureInfo.InvariantCulture)));
            return this;
        }

        public Json Add(string key, Json value)
        {
            if (value != null)
                m_Fields.Add((key, value.ToString()));
            return this;
        }

        public Json Add(string key, IEnumerable<string> values)
        {
            if (values == null)
                return this;
            var items = new List<string>();
            foreach (var v in values)
                items.Add(Quote(v ?? string.Empty));
            m_Fields.Add((key, "[" + string.Join(",", items) + "]"));
            return this;
        }

        public Json Add(string key, IEnumerable<long> values)
        {
            var items = new List<string>();
            foreach (var v in values)
                items.Add(v.ToString(CultureInfo.InvariantCulture));
            m_Fields.Add((key, "[" + string.Join(",", items) + "]"));
            return this;
        }

        public Json Add(string key, IEnumerable<Json> values)
        {
            var items = new List<string>();
            foreach (var v in values)
                items.Add(v.ToString());
            m_Fields.Add((key, "[" + string.Join(",", items) + "]"));
            return this;
        }

        public override string ToString()
        {
            var sb = new StringBuilder("{");
            for (var i = 0; i < m_Fields.Count; i++)
            {
                if (i > 0)
                    sb.Append(',');
                sb.Append(Quote(m_Fields[i].key)).Append(':').Append(m_Fields[i].json);
            }
            return sb.Append('}').ToString();
        }

        public static string Quote(string value)
        {
            var sb = new StringBuilder(value.Length + 2).Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
#endif
