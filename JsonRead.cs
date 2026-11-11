using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CubeScope
{
    /// <summary>
    /// A small JSON reader for the alarm settings the page posts and the files they are kept in:
    /// objects become Dictionary&lt;string, object&gt;, arrays List&lt;object&gt;, numbers double, and
    /// true/false/null bool/null. Throws FormatException with the position on bad input.
    /// </summary>
    public static class JsonRead
    {
        public static object Parse(string text)
        {
            int i = 0;
            object v = Value(text ?? "", ref i, 0);
            Skip(text, ref i);
            if (i != text.Length) throw new FormatException("unexpected text after the value at " + i);
            return v;
        }

        static void Skip(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }

        static object Value(string s, ref int i, int depth)
        {
            if (depth > 64) throw new FormatException("nested too deeply");
            Skip(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end");
            char c = s[i];
            if (c == '{')
            {
                Dictionary<string, object> o = new Dictionary<string, object>(StringComparer.Ordinal);
                i++; Skip(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return o; }
                while (true)
                {
                    Skip(s, ref i);
                    if (i >= s.Length || s[i] != '"') throw new FormatException("expected a name at " + i);
                    string k = Str(s, ref i);
                    Skip(s, ref i);
                    if (i >= s.Length || s[i] != ':') throw new FormatException("expected : at " + i);
                    i++;
                    o[k] = Value(s, ref i, depth + 1);
                    Skip(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == '}') { i++; return o; }
                    throw new FormatException("expected , or } at " + i);
                }
            }
            if (c == '[')
            {
                List<object> a = new List<object>();
                i++; Skip(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return a; }
                while (true)
                {
                    a.Add(Value(s, ref i, depth + 1));
                    Skip(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; return a; }
                    throw new FormatException("expected , or ] at " + i);
                }
            }
            if (c == '"') return Str(s, ref i);
            if (Word(s, ref i, "true")) return true;
            if (Word(s, ref i, "false")) return false;
            if (Word(s, ref i, "null")) return null;
            int st = i;
            if (i < s.Length && s[i] == '-') i++;
            while (i < s.Length && ("0123456789.eE+-".IndexOf(s[i]) >= 0)) i++;
            double d;
            if (i == st || !double.TryParse(s.Substring(st, i - st), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                throw new FormatException("bad value at " + st);
            return d;
        }

        static bool Word(string s, ref int i, string w)
        {
            if (string.CompareOrdinal(s, i, w, 0, w.Length) == 0) { i += w.Length; return true; }
            return false;
        }

        static string Str(string s, ref int i)
        {
            i++;                                               // opening quote
            StringBuilder sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("bad \\u escape");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new FormatException("bad escape \\" + e);
                }
            }
            throw new FormatException("unterminated string");
        }

        // ---- helpers for reading a parsed object with defaults

        public static string GetString(Dictionary<string, object> o, string k, string def)
        {
            object v; return o != null && o.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : def;
        }

        public static double GetNumber(Dictionary<string, object> o, string k, double def)
        {
            object v;
            if (o == null || !o.TryGetValue(k, out v) || v == null) return def;
            if (v is double) return (double)v;
            double d;
            return double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : def;
        }

        public static bool GetBool(Dictionary<string, object> o, string k, bool def)
        {
            object v; return o != null && o.TryGetValue(k, out v) && v is bool ? (bool)v : def;
        }

        public static List<string> GetStrings(Dictionary<string, object> o, string k)
        {
            List<string> r = new List<string>();
            object v;
            if (o == null || !o.TryGetValue(k, out v) || v == null) return r;
            List<object> a = v as List<object>;
            if (a != null) { foreach (object x in a) if (x != null) r.Add(Convert.ToString(x, CultureInfo.InvariantCulture)); return r; }
            foreach (string part in Convert.ToString(v, CultureInfo.InvariantCulture).Split(','))
                if (part.Trim().Length > 0) r.Add(part.Trim());
            return r;
        }
    }
}
