using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CubeScope
{
    /// <summary>
    /// A number pattern, the same everywhere (filters and rules): plain digits match as a prefix,
    /// * and ? are wildcards over the whole number, "x" in quotes or =x is exact, "" is empty, and
    /// re:... is a regular expression over the whole number.
    /// </summary>
    public sealed class Pattern
    {
        string _exact, _prefix;
        Regex _re;
        public string Source;

        public static Pattern Parse(string p)
        {
            Pattern r = new Pattern();
            r.Source = p ?? "";
            p = (p ?? "").Trim();
            if (p.StartsWith("re:", StringComparison.OrdinalIgnoreCase))
            {
                r._re = new Regex("^(?:" + p.Substring(3) + ")$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
                return r;
            }
            if (p.Length >= 2 && p[0] == '"' && p[p.Length - 1] == '"') { r._exact = p.Substring(1, p.Length - 2); return r; }
            if (p.StartsWith("=", StringComparison.Ordinal)) { r._exact = p.Substring(1); return r; }
            if (p.IndexOf('*') >= 0 || p.IndexOf('?') >= 0)
            {
                string rx = Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".");
                r._re = new Regex("^" + rx + "$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
                return r;
            }
            r._prefix = p;
            return r;
        }

        public bool Match(string s)
        {
            if (s == null) s = "";
            if (_exact != null) return string.Equals(s, _exact, StringComparison.OrdinalIgnoreCase);
            if (_re != null) return _re.IsMatch(s);
            if (_prefix.Length == 0) return true;
            return s.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Labels for legs, from a small text file (cubescope.rules). A rule is a list of conditions that
    /// must all hold on the same leg. "signalling-only" marks legs that never carry media, so their
    /// silence is not reported as a media fault: on this customer that is the SIP dialer's leg.
    /// </summary>
    public sealed class Rules
    {
        public readonly List<string> TagNames = new List<string>();
        readonly List<Rule> _rules = new List<Rule>();
        public readonly List<string> Errors = new List<string>();
        public string Text = "";
        public string Path = "";

        sealed class Cond { public string Field; public Pattern P; }
        sealed class Rule { public bool Signal; public int Bit; public Cond[] Conds; public string Line; }

        public const string DefaultText =
"# CubeScope number rules - one rule per line, # starts a comment.\n" +
"#   tag <name> <condition>...         label legs; filter with tag=<name>\n" +
"#   signalling-only <condition>...    legs that never carry media (not a media fault)\n" +
"# Conditions, all of which must hold on the same leg:\n" +
"#   in / out        the leg the CUBE answered / the leg it placed\n" +
"#   cgn:<pattern>   calling number     cdn:<pattern>  called number     peer:<pattern>  PeerAddress\n" +
"# Patterns: digits = prefix, * and ? = wildcards over the whole number, \"x\" = exactly x,\n" +
"# \"\" = empty, re:<regex> = regular expression over the whole number.\n" +
"signalling-only in cgn:\"\"\n" +
"tag dialer in cgn:\"\"\n" +
"tag cvp-label out cdn:555*\n" +
"tag extension out peer:re:\\d{4,6}\n" +
"tag external peer:re:1?[2-9]\\d{9}\n";

        public static Rules Default()
        {
            Rules r = Parse(DefaultText);
            r.Path = "(built in)";
            return r;
        }

        public static Rules Load(string path)
        {
            Rules r = Parse(File.ReadAllText(path, Encoding.UTF8));
            r.Path = path;
            return r;
        }

        public static Rules Parse(string text)
        {
            Rules r = new Rules();
            r.Text = text ?? "";
            string[] lines = r.Text.Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int hash = line.IndexOf('#');
                if (hash >= 0) line = line.Substring(0, hash);
                line = line.Trim();
                if (line.Length == 0) continue;
                List<string> words = Filter.Split(line);
                Rule rule = new Rule();
                rule.Line = line;
                int at;
                if (words[0].Equals("signalling-only", StringComparison.OrdinalIgnoreCase) ||
                    words[0].Equals("signaling-only", StringComparison.OrdinalIgnoreCase))
                {
                    rule.Signal = true; at = 1;
                }
                else if (words[0].Equals("tag", StringComparison.OrdinalIgnoreCase) && words.Count >= 3)
                {
                    string name = words[1].ToLowerInvariant();
                    int bit = r.TagNames.IndexOf(name);
                    if (bit < 0)
                    {
                        if (r.TagNames.Count >= 30) { r.Errors.Add("line " + (i + 1) + ": more than 30 tags"); continue; }
                        r.TagNames.Add(name); bit = r.TagNames.Count - 1;
                    }
                    rule.Bit = bit; at = 2;
                }
                else { r.Errors.Add("line " + (i + 1) + ": start with tag <name> or signalling-only"); continue; }
                List<Cond> conds = new List<Cond>();
                bool bad = false;
                for (int k = at; k < words.Count; k++)
                {
                    string w = words[k];
                    Cond c = new Cond();
                    string lw = w.ToLowerInvariant();
                    if (lw == "in" || lw == "out") { c.Field = lw; conds.Add(c); continue; }
                    int colon = w.IndexOf(':');
                    if (colon <= 0) { r.Errors.Add("line " + (i + 1) + ": '" + w + "' is not in, out or field:pattern"); bad = true; break; }
                    c.Field = w.Substring(0, colon).ToLowerInvariant();
                    if (c.Field != "cgn" && c.Field != "cdn" && c.Field != "peer")
                    {
                        r.Errors.Add("line " + (i + 1) + ": unknown field '" + c.Field + "' (cgn, cdn or peer)"); bad = true; break;
                    }
                    try { c.P = Pattern.Parse(w.Substring(colon + 1)); }
                    catch (ArgumentException ex) { r.Errors.Add("line " + (i + 1) + ": " + ex.Message); bad = true; break; }
                    conds.Add(c);
                }
                if (bad) continue;
                if (conds.Count == 0) { r.Errors.Add("line " + (i + 1) + ": a rule needs at least one condition"); continue; }
                rule.Conds = conds.ToArray();
                r._rules.Add(rule);
            }
            return r;
        }

        public void Apply(Leg g)
        {
            int tags = 0;
            bool sig = false;
            for (int i = 0; i < _rules.Count; i++)
            {
                Rule rule = _rules[i];
                bool ok = true;
                for (int k = 0; k < rule.Conds.Length && ok; k++)
                {
                    Cond c = rule.Conds[k];
                    switch (c.Field)
                    {
                        case "in": ok = g.In; break;
                        case "out": ok = g.Out; break;
                        // without its TWC record a leg's own numbers are unknown; PeerAddress is the
                        // calling number on an inbound leg and the called one on an outbound leg
                        case "cgn": ok = g.Bound ? c.P.Match(g.Cgn) : (g.In && c.P.Match(g.Peer)); break;
                        case "cdn": ok = g.Bound ? c.P.Match(g.Cdn) : (g.Out && c.P.Match(g.Peer)); break;
                        case "peer": ok = c.P.Match(g.Peer); break;
                    }
                }
                if (!ok) continue;
                if (rule.Signal) sig = true; else tags |= 1 << rule.Bit;
            }
            g.Tags = tags;
            g.SignalOnly = sig;
        }

        public int Bit(string name)
        {
            int i = TagNames.IndexOf((name ?? "").ToLowerInvariant());
            return i < 0 ? 0 : 1 << i;
        }

        public string TagList(int bits)
        {
            if (bits == 0) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < TagNames.Count; i++)
                if ((bits & (1 << i)) != 0) { if (sb.Length > 0) sb.Append(' '); sb.Append(TagNames[i]); }
            return sb.ToString();
        }
    }
}
