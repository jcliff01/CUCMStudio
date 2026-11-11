using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CubeScope
{
    /// <summary>
    /// The filter box (DESIGN.md 5.5). Clauses separated by spaces must all hold; values separated by
    /// commas are alternatives. <c>called=251*,231* cause!=16 talk&gt;=30 tag=dialer</c>. A bare number
    /// is shorthand for any=number. A leading ! negates a clause.
    /// </summary>
    public sealed class Filter
    {
        public string Text = "";
        public string Error;
        readonly List<Clause> _clauses = new List<Clause>();

        sealed class Clause
        {
            public string Field;
            public string Op;               // = != >= <= > <
            public bool Negate;             // a leading ! : the whole clause's answer is reversed
            public Pattern[] Pats;
            public string[] Words;
            public double[] Nums;
        }

        public bool IsEmpty { get { return _clauses.Count == 0; } }

        public static readonly string[] NumberFields = { "called", "calling", "connected", "dialed", "peer", "any", "legfail" };
        public static readonly string[] Fields = {
            "called", "calling", "connected", "dialed", "peer", "any", "legfail", "cause", "legcause", "result", "released",
            "media", "legs", "transfers", "talk", "ring", "dur", "a2t", "tag", "guid", "src", "has", "codec",
            "dialpeer", "mediaip", "mediaport", "alert", "received", "sent", "mos", "loss", "lost", "rtt", "jitter" };

        public static Filter Parse(string text)
        {
            Filter f = new Filter();
            f.Text = (text ?? "").Trim();
            if (f.Text.Length == 0) return f;
            List<string> toks = Split(f.Text);
            for (int i = 0; i < toks.Count; i++)
            {
                string t = toks[i];
                bool not = false;
                if (t.StartsWith("!", StringComparison.Ordinal)) { not = true; t = t.Substring(1); }
                int at = -1, len = 0;
                string op = null;
                string[] ops = { "!=", ">=", "<=", "=", ">", "<", ":" };
                for (int k = 0; k < t.Length && op == null; k++)
                {
                    for (int o = 0; o < ops.Length; o++)
                    {
                        if (string.CompareOrdinal(t, k, ops[o], 0, ops[o].Length) == 0)
                        {
                            op = ops[o]; at = k; len = ops[o].Length; break;
                        }
                    }
                    if (t[k] == '"') break;                    // an operator inside quotes is a value
                }
                string field, value;
                if (op == null || at == 0) { field = "any"; op = "="; value = t; }
                else { field = t.Substring(0, at).Trim().ToLowerInvariant(); value = t.Substring(at + len); }
                if (op == ":") op = "=";
                if (field == "number" || field == "num") field = "any";
                if (field == "cube" || field == "source") field = "src";
                if (field == "duration") field = "dur";
                if (field == "audio") field = "media";
                if (Names.IndexOf(Fields, field) < 0)
                {
                    f.Error = "unknown field '" + field + "' - use " + string.Join(", ", Fields);
                    f._clauses.Clear();
                    return f;
                }
                Clause c = new Clause();
                c.Field = field; c.Op = op; c.Negate = not;
                List<string> vals = SplitValues(value);
                if (vals.Count == 0) vals.Add("\"\"");
                // codec=200 is bytes per packet, as it always was; codec=g711ulaw is the name a call record file gives
                double codecBytes;
                if (field == "codec" && !double.TryParse(vals[0].Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out codecBytes))
                    c.Field = field = "codecname";
                try
                {
                    if (IsNumeric(field))
                    {
                        c.Nums = new double[vals.Count];
                        for (int k = 0; k < vals.Count; k++)
                        {
                            double d;
                            if (!double.TryParse(vals[k].Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                                throw new ArgumentException(field + " needs a number, not '" + vals[k] + "'");
                            c.Nums[k] = d;
                        }
                    }
                    else if (IsWordField(field))
                    {
                        if (op != "=" && op != "!=") throw new ArgumentException(field + " takes = or !=");
                        c.Words = new string[vals.Count];
                        for (int k = 0; k < vals.Count; k++) c.Words[k] = vals[k].Trim('"').ToLowerInvariant();
                        string bad = CheckWords(field, c.Words);
                        if (bad != null) throw new ArgumentException(bad);
                    }
                    else
                    {
                        if (op != "=" && op != "!=") throw new ArgumentException(field + " takes = or !=");
                        c.Pats = new Pattern[vals.Count];
                        for (int k = 0; k < vals.Count; k++) c.Pats[k] = Pattern.Parse(vals[k]);
                    }
                }
                catch (ArgumentException ex)
                {
                    f.Error = ex.Message;
                    f._clauses.Clear();
                    return f;
                }
                f._clauses.Add(c);
            }
            return f;
        }

        static bool IsNumeric(string f)
        {
            return f == "cause" || f == "legcause" || f == "legs" || f == "transfers" || f == "talk" || f == "ring" ||
                   f == "dur" || f == "a2t" || f == "codec" || f == "dialpeer" || f == "mediaport" || f == "alert" ||
                   f == "mos" || f == "loss" || f == "lost" || f == "rtt" || f == "jitter";
        }

        static bool IsWordField(string f)
        {
            return f == "result" || f == "released" || f == "media" || f == "tag" || f == "has";
        }

        /// <summary>
        /// Canceled while ringing, one side only: 0 = the caller hung up (any call but a dialer's),
        /// 1 = the dialer gave up (a dialer call); -1 = not one of these words.
        /// </summary>
        static int CancelSide(string w)
        {
            if (w == "callercanceled" || w == "callercancelled") return 0;
            if (w == "dialercanceled" || w == "dialercancelled") return 1;
            return -1;
        }

        static string CheckWords(string field, string[] words)
        {
            for (int i = 0; i < words.Length; i++)
            {
                string w = words[i];
                if (field == "result" && Names.IndexOf(Names.Results, w) < 0 && w != "unanswered" && w != "notanswered" &&
                    w != "canceled" && w != "cancelled" && CancelSide(w) < 0)
                    return "result is one of answered, canceled (also abandoned), callercanceled, dialercanceled, noanswer, busy, rejected, failed, unanswered";
                if (field == "released" && Names.IndexOf(Names.Releases, w) < 0)
                    return "released is one of caller, called, network, tie, unknown";
                if (field == "media" && Names.IndexOf(Names.Medias, w) < 0 && w != "bad" && w != "problem")
                    return "media is one of none, oneway, lowrate, twoway, unknown, bad (none or one-way), problem (none, one-way or low rate)";
                if (field == "has" && w != "dump" && w != "iec" && w != "error" && w != "transfer" && w != "cfa" &&
                    w != "stub" && w != "unbound" && w != "dialer")
                    return "has is one of dump, iec, transfer, cfa, stub, unbound, dialer";
            }
            return null;
        }

        public bool Match(Call c, Rules rules)
        {
            for (int i = 0; i < _clauses.Count; i++)
            {
                // ! reverses the clause as a whole, comparisons included (!talk>=30 is talk under 30)
                bool hit = MatchClause(_clauses[i], c, rules);
                if (_clauses[i].Negate) hit = !hit;
                if (!hit) return false;
            }
            return true;
        }

        static bool MatchClause(Clause k, Call c, Rules rules)
        {
            bool neg = k.Op == "!=";
            bool hit;
            switch (k.Field)
            {
                case "called": hit = AnyPat(k, c.Called); break;
                case "calling": hit = AnyPat(k, c.Calling); break;
                case "connected": hit = AnyPat(k, c.ConnectedTo); break;
                case "dialed": hit = AnyPat(k, c.Dialed); break;
                case "peer":
                    hit = false;
                    for (int i = 0; i < c.Legs.Count && !hit; i++) hit = AnyPat(k, c.Legs[i].Peer);
                    break;
                case "any":
                    hit = AnyPat(k, c.Calling) || AnyPat(k, c.Called) || AnyPat(k, c.Dialed) || AnyPat(k, c.ConnectedTo);
                    for (int i = 0; i < c.Legs.Count && !hit; i++)
                    {
                        Leg g = c.Legs[i];
                        hit = AnyPat(k, g.Peer) || AnyPat(k, g.Cgn) || AnyPat(k, g.Cdn);
                    }
                    if (!hit) hit = AnyDetail(k, c, "received") || AnyDetail(k, c, "sent");
                    break;
                case "mediaip": case "codecname": case "received": case "sent":
                    hit = AnyDetail(k, c, k.Field);
                    break;
                case "dialpeer": case "mediaport": case "alert": case "mos": case "loss": case "lost": case "rtt": case "jitter":
                {
                    // "some leg has this", then != reverses that, as legcause does. A leg the CUBE did not
                    // measure has no value here, so mos<3.5 never picks up an unmeasured leg's zero.
                    bool notD = k.Op == "!=";
                    string opD = notD ? "=" : k.Op;
                    bool anyD = false;
                    Dictionary<Leg, LegDetail> det = Details(c);
                    for (int i = 0; det != null && i < c.Legs.Count && !anyD; i++)
                    {
                        LegDetail d;
                        if (!det.TryGetValue(c.Legs[i], out d)) continue;
                        double v = DetailNumber(k.Field, c.Legs[i], d);
                        if (!double.IsNaN(v) && Num(opD, k.Nums, v)) anyD = true;
                    }
                    return notD ? !anyD : anyD;
                }
                case "legfail":
                    // a leg sent to this number that nobody answered, even on a call that reads as
                    // answered (the customer answered, the transfer did not); with no leg sent, a call
                    // to this number that was not answered (the CUBE refused it first)
                    bool legAnswered;
                    Leg to = c.LegTo(delegate (string n) { return AnyPat(k, n); }, out legAnswered);
                    hit = to != null ? !legAnswered : AnyPat(k, c.Called) && c.Result != Result.Answered;
                    break;
                case "guid":
                    hit = false;
                    for (int i = 0; i < k.Pats.Length && !hit; i++)
                        hit = k.Pats[i].Match(c.Key) || (c.Padded != null && k.Pats[i].Match(c.Padded)) ||
                              (c.Padded != null && k.Pats[i].Match(GuidForm.Short(c.Padded)));
                    break;
                case "src": hit = AnyPat(k, SourceName(c)); break;
                case "result":
                    hit = false;
                    for (int i = 0; i < k.Words.Length && !hit; i++)
                    {
                        string w = k.Words[i];
                        // canceled while ringing is the Abandoned result: the caller (or, on a dialer
                        // call, the dialer) stopped the call before anyone answered
                        if (w == "canceled" || w == "cancelled") w = "abandoned";
                        int side = CancelSide(w);
                        if (side >= 0) hit = c.Result == Result.Abandoned && c.Dialer == (side == 1);
                        else hit = (w == "unanswered" || w == "notanswered") ? c.Result != Result.Answered
                                                                              : Names.Results[(int)c.Result] == w;
                    }
                    break;
                case "released": hit = Contains(k.Words, Names.Releases[(int)c.Released]); break;
                case "media":
                    hit = Contains(k.Words, Names.Medias[(int)c.Media]) ||
                          (Contains(k.Words, "bad") && (c.Media == Media.None || c.Media == Media.OneWay)) ||
                          (Contains(k.Words, "problem") && (c.Media == Media.None || c.Media == Media.OneWay || c.Media == Media.LowRate));
                    break;
                case "tag":
                    hit = false;
                    for (int i = 0; i < k.Words.Length && !hit; i++)
                        hit = rules != null && (c.Tags & rules.Bit(k.Words[i])) != 0;
                    break;
                case "has":
                    hit = false;
                    for (int i = 0; i < k.Words.Length && !hit; i++)
                    {
                        string w = k.Words[i];
                        if (w == "dump") hit = c.Dumps != null && c.Dumps.Count > 0;
                        else if (w == "iec" || w == "error") hit = c.Errors != null && c.Errors.Count > 0;
                        else if (w == "transfer") hit = c.Transfers > 0;
                        else if (w == "cfa") hit = HasFeature(c, "CFA");
                        else if (w == "stub") { for (int j = 0; j < c.Legs.Count && !hit; j++) hit = c.Legs[j].FeatureOnly; }
                        else if (w == "unbound") { for (int j = 0; j < c.Legs.Count && !hit; j++) hit = !c.Legs[j].Bound; }
                        else if (w == "dialer") hit = c.Dialer;
                    }
                    break;
                case "cause": return Num(k, c.FinalCause);
                case "legcause":
                {
                    // "some leg has this cause", then != reverses that: legcause!=16 means no leg had
                    // 16, not that some leg had something else
                    bool not = k.Op == "!=";
                    string op = not ? "=" : k.Op;
                    bool any = false;
                    for (int i = 0; i < c.Legs.Count && !any; i++)
                        if (!c.Legs[i].FeatureOnly && Num(op, k.Nums, c.Legs[i].Cause)) any = true;
                    return not ? !any : any;
                }
                case "legs": return Num(k, c.Legs.Count);
                case "transfers": return Num(k, c.Transfers);
                case "talk": return Num(k, c.TalkMs / 1000.0);
                case "ring": return c.RingMs >= 0 && Num(k, c.RingMs / 1000.0);
                case "dur": return Num(k, c.DurMs / 1000.0);
                case "a2t": return c.A2tMs >= 0 && Num(k, c.A2tMs / 1000.0);
                case "codec": return c.Bpp > 0 && Num(k, c.Bpp);
                default: hit = false; break;
            }
            return neg ? !hit : hit;
        }

        /// <summary>Set by the query so the src field can match names; not part of the call.</summary>
        [ThreadStatic] public static List<Source> CurrentSources;

        static string SourceName(Call c)
        {
            List<Source> s = CurrentSources;
            return s != null && c.Src < s.Count ? s[c.Src].Name : "";
        }

        static Dictionary<Leg, LegDetail> Details(Call c)
        {
            List<Source> s = CurrentSources;
            return s != null && c.Src < s.Count ? s[c.Src].Details : null;
        }

        static double DetailNumber(string field, Leg g, LegDetail d)
        {
            LegQuality q = d.Quality;
            switch (field)
            {
                case "dialpeer": return d.DialPeer >= 0 ? d.DialPeer : double.NaN;
                case "mediaport": return d.MediaPort > 0 ? d.MediaPort : double.NaN;
                case "alert": return d.Alert >= g.Setup && d.Alert >= 0 ? (d.Alert - g.Setup) / 1000.0 : double.NaN;
                case "mos": return q != null && q.Mos > 0 ? q.Mos : double.NaN;
                case "loss": return q != null ? q.LossRate : double.NaN;
                case "lost": return q != null ? q.LostPackets : double.NaN;
                case "rtt": return q != null && q.RoundTripMs > 0 ? q.RoundTripMs : double.NaN;
                case "jitter": return q != null && q.Jitter > 0 ? q.Jitter : double.NaN;
            }
            return double.NaN;
        }

        static bool AnyDetail(Clause k, Call c, string which)
        {
            Dictionary<Leg, LegDetail> det = Details(c);
            if (det == null) return false;
            for (int i = 0; i < c.Legs.Count; i++)
            {
                LegDetail d;
                if (!det.TryGetValue(c.Legs[i], out d)) continue;
                switch (which)
                {
                    case "mediaip": if (AnyPat(k, d.MediaAddress)) return true; break;
                    case "codecname": if (AnyPat(k, d.Codec)) return true; break;
                    case "received": if (AnyPat(k, d.ReceivedCalled) || AnyPat(k, d.ReceivedCalling)) return true; break;
                    case "sent": if (AnyPat(k, d.SentCalled) || AnyPat(k, d.SentCalling)) return true; break;
                }
            }
            return false;
        }

        static bool HasFeature(Call c, string fn)
        {
            if (c.Features == null) return false;
            for (int i = 0; i < c.Features.Count; i++) if (c.Features[i].Fn == fn) return true;
            return false;
        }

        static bool AnyPat(Clause k, string v)
        {
            if (k.Pats == null) return false;
            for (int i = 0; i < k.Pats.Length; i++) if (k.Pats[i].Match(v)) return true;
            return false;
        }

        static bool Contains(string[] words, string w)
        {
            for (int i = 0; i < words.Length; i++) if (words[i] == w) return true;
            return false;
        }

        static bool Num(Clause k, double v) { return Num(k.Op, k.Nums, v); }

        static bool Num(string op, double[] nums, double v)
        {
            switch (op)
            {
                case "=": for (int i = 0; i < nums.Length; i++) if (v == nums[i]) return true; return false;
                case "!=": for (int i = 0; i < nums.Length; i++) if (v == nums[i]) return false; return true;
                case ">=": return v >= nums[0];
                case "<=": return v <= nums[0];
                case ">": return v > nums[0];
                case "<": return v < nums[0];
            }
            return false;
        }

        /// <summary>Whitespace-separated words; double quotes keep spaces inside a word.</summary>
        public static List<string> Split(string s)
        {
            List<string> r = new List<string>();
            StringBuilder cur = new StringBuilder();
            bool q = false;
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch == '"') { q = !q; cur.Append(ch); continue; }
                if (!q && char.IsWhiteSpace(ch))
                {
                    if (cur.Length > 0) { r.Add(cur.ToString()); cur.Length = 0; }
                    continue;
                }
                cur.Append(ch);
            }
            if (cur.Length > 0) r.Add(cur.ToString());
            return r;
        }

        static List<string> SplitValues(string s)
        {
            List<string> r = new List<string>();
            StringBuilder cur = new StringBuilder();
            bool q = false;
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch == '"') { q = !q; cur.Append(ch); continue; }
                if (!q && ch == ',') { r.Add(cur.ToString()); cur.Length = 0; continue; }
                cur.Append(ch);
            }
            if (cur.Length > 0 || r.Count > 0) r.Add(cur.ToString());
            return r;
        }
    }
}
