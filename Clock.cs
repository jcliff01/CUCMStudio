using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CubeScope
{
    /// <summary>What one file showed of the CUBE's clock: its hour by the receiver's clock (from the file
    /// name) and the earliest and latest syslog header times in it, by the CUBE's clock.</summary>
    public sealed class FileClock
    {
        public int File = -1;
        public long NameMs = -1;
        public long First = -1, Last = -1;
        public long Lines;
    }

    /// <summary>From this message on (CUBE clock), the headers carried this zone label ("" = none).</summary>
    public struct ZoneRun
    {
        public long From;
        public string Tz;
    }

    /// <summary>A stretch of files over which the CUBE's clock kept one offset from the receiver's.</summary>
    public sealed class ClockSegment
    {
        public long ServerFrom, ServerTo;     // receiver's clock: first file's hour to the end of the last one's
        public long Lo, Hi;                   // the offset (CUBE minus receiver, ms) lies between these
        public int Files;
        public long Lines;
        public string Tz = "";                // the label its messages carried ("" = none)

        /// <summary>The zone part: a whole hour inside [Lo, Hi] if one fits (else a half, else a
        /// quarter hour), nearest the middle; with none inside, the quarter hour nearest the middle.</summary>
        public long ZoneDiff
        {
            get
            {
                long mid = Lo + (Hi - Lo) / 2, lo = Math.Min(Lo, Hi) - Clock.SlackMs, hi = Math.Max(Lo, Hi) + Clock.SlackMs;
                long[] steps = { Clock.Hour, Clock.Hour / 2, Clock.Hour / 4 };
                for (int k = 0; k < steps.Length; k++)
                {
                    long st = steps[k];
                    long a = (long)Math.Ceiling(lo / (double)st) * st, best = long.MinValue;
                    for (long v = a; v <= hi; v += st)
                        if (best == long.MinValue || Math.Abs(v - mid) < Math.Abs(best - mid)) best = v;
                    if (best != long.MinValue) return best;
                }
                return Clock.ZonePart(mid);
            }
        }
        /// <summary>The clock error the measurement allows: Lo and Hi less the zone part.</summary>
        public long DriftLo { get { return Math.Min(Lo, Hi) - ZoneDiff; } }
        public long DriftHi { get { return Math.Max(Lo, Hi) - ZoneDiff; } }
        /// <summary>Best estimate: the zone part plus the smallest clock error the bounds allow (a clock
        /// is assumed right unless the files prove it wrong).</summary>
        public long Offset
        {
            get
            {
                long z = ZoneDiff, a = DriftLo, b = DriftHi;
                return z + (a > 0 ? a : b < 0 ? b : 0);
            }
        }
        /// <summary>How far the true offset could be from Offset.</summary>
        public long Spread { get { return Math.Max(Math.Abs(Offset - Math.Min(Lo, Hi)), Math.Abs(Math.Max(Lo, Hi) - Offset)); } }
        public long NodeFrom { get { return ServerFrom + Offset; } }
        public long NodeTo { get { return ServerTo + Offset; } }
    }

    /// <summary>The receiver's zone: given (--server-zone), worked out from the CUBEs' labels, or this PC's.</summary>
    public sealed class ServerZone
    {
        public string How = "pc";              // "option", "labels" or "pc"
        public string Text = "";               // for people
        public TimeZoneInfo Tzi;               // option naming a Windows zone, or this PC
        public double FixedHours = double.NaN; // option "UTC-6"

        public sealed class Piece
        {
            public long From, To;              // receiver's clock
            public double Hours;
            public long Weight;
        }
        public readonly List<Piece> Pieces = new List<Piece>();

        /// <summary>Hours to add to UTC to get the receiver's clock, at a receiver time.</summary>
        public double HoursAt(long serverMs)
        {
            if (!double.IsNaN(FixedHours)) return FixedHours;
            if (Pieces.Count > 0)
            {
                Piece best = null, near = null;
                long nearGap = long.MaxValue;
                for (int i = 0; i < Pieces.Count; i++)
                {
                    Piece p = Pieces[i];
                    if (serverMs >= p.From && serverMs < p.To) { if (best == null || p.Weight > best.Weight) best = p; continue; }
                    long gap = serverMs < p.From ? p.From - serverMs : serverMs - p.To;
                    if (gap < nearGap) { nearGap = gap; near = p; }
                }
                if (best != null) return best.Hours;
                if (near != null) return near.Hours;
            }
            return Clock.LocalHours(Tzi ?? TimeZoneInfo.Local, serverMs);
        }
    }

    /// <summary>
    /// Moves record times (each CUBE's own clock) onto one clock for display: the syslog server's,
    /// UTC, or a named zone. Through the server's clock where the CUBE's offset was measured (that
    /// also corrects a wrong CUBE clock); through the CUBE's zone label where it was not. A time is
    /// moved by the shift of its hour, worked out once per CUBE per hour.
    /// </summary>
    public sealed class ZoneMap
    {
        public string Id = "cube";
        public bool Identity = true;
        public string Name = "each CUBE's own clock";
        /// <summary>No shift moves a time further than this: a query widens its search by it.</summary>
        public long MaxAbsShift;
        /// <summary>CUBEs whose times could not be moved (no measured offset and no label).</summary>
        public readonly List<string> NotConverted = new List<string>();

        int _kind;                         // 1 server, 2 UTC, 3 zone, 4 fixed offset
        double _fixed;
        TimeZoneInfo _target;
        ServerZone _server;
        List<ClockSegment>[] _segs;
        Source[] _src;
        Dictionary<long, long>[] _memo;

        /// <summary>id and label for the picker; ids are also accepted on the command line.</summary>
        public static readonly string[][] Choices = {
            new string[] { "cube", "Each CUBE's own clock" }, new string[] { "server", "Syslog server's clock" },
            new string[] { "UTC", "UTC" }, new string[] { "Eastern Standard Time", "US Eastern" },
            new string[] { "Central Standard Time", "US Central" }, new string[] { "Mountain Standard Time", "US Mountain" },
            new string[] { "US Mountain Standard Time", "Arizona" }, new string[] { "Pacific Standard Time", "US Pacific" } };

        public static bool Valid(string id)
        {
            if (string.IsNullOrEmpty(id) || id == "cube" || id == "server") return true;
            TimeZoneInfo t; double f;
            return Clock.ParseZone(id, out t, out f);
        }

        public static ZoneMap Build(Store st, string id)
        {
            ZoneMap z = new ZoneMap();
            if (string.IsNullOrEmpty(id) || id == "cube") return z;
            z.Id = id;
            if (id == "server") { z._kind = 1; z.Name = "the syslog server's clock"; }
            else
            {
                TimeZoneInfo t; double f;
                if (!Clock.ParseZone(id, out t, out f)) { z.Id = "cube"; return z; }
                if (t != null) { z._kind = 3; z._target = t; z.Name = NameOf(id, t); }
                else if (f == 0) { z._kind = 2; z.Name = "UTC"; }
                else { z._kind = 4; z._fixed = f; z.Name = Clock.Utc(f); }
            }
            z.Identity = false;
            z._server = Clock.Server(st);
            int n = st.Sources.Count;
            z._segs = new List<ClockSegment>[n];
            z._src = new Source[n];
            z._memo = new Dictionary<long, long>[n];
            long most = 0;
            for (int i = 0; i < n; i++)
            {
                Source s = st.Sources[i];
                z._src[i] = s;
                z._segs[i] = Clock.Segments(s);
                z._memo[i] = new Dictionary<long, long>();
                bool labelled = false;
                foreach (KeyValuePair<string, long> kv in s.ZoneCounts)
                    if (!double.IsNaN(TimeUtil.TzOffsetHours(kv.Key))) labelled = true;
                if (z._segs[i].Count == 0 && !labelled && s.FirstMs >= 0) z.NotConverted.Add(s.Name);
                if (s.FirstMs >= 0) { most = Math.Max(most, Math.Abs(z.Shift(i, s.FirstMs))); most = Math.Max(most, Math.Abs(z.Shift(i, s.LastMs))); }
                for (int k = 0; k < z._segs[i].Count; k++) most = Math.Max(most, Math.Abs(z.Shift(i, z._segs[i][k].NodeFrom)));
            }
            // daylight-saving changes in between can add an hour
            z.MaxAbsShift = most + Clock.Hour;
            return z;
        }

        static string NameOf(string id, TimeZoneInfo t)
        {
            for (int i = 0; i < Choices.Length; i++) if (Choices[i][0] == id) return Choices[i][1];
            return t.Id == TimeZoneInfo.Local.Id ? "this computer's zone" : t.StandardName;
        }

        /// <summary>Milliseconds to add to a time on CUBE src's clock to put it on this map's clock.</summary>
        public long Shift(int src, long nodeMs)
        {
            if (Identity || src < 0 || src >= _memo.Length) return 0;
            long hk = nodeMs >= 0 ? nodeMs / Clock.Hour : (nodeMs - Clock.Hour + 1) / Clock.Hour;
            Dictionary<long, long> m = _memo[src];
            long v;
            if (m.TryGetValue(hk, out v)) return v;
            v = Compute(src, hk * Clock.Hour + Clock.Hour / 2);
            m[hk] = v;
            return v;
        }

        public long ToDisplay(int src, long nodeMs) { return nodeMs < 0 ? nodeMs : nodeMs + Shift(src, nodeMs); }

        /// <summary>The time now on this map's clock, or -1 on "each CUBE's own clock" (there is no one clock).</summary>
        public long NowDisplay(DateTime utcNow)
        {
            return Identity ? -1 : FromUtc(TimeUtil.FromDate(utcNow));
        }

        /// <summary>A UTC time on this map's clock; unchanged on "each CUBE's own clock".</summary>
        public long FromUtc(long u)
        {
            switch (_kind)
            {
                case 0: return u;
                case 1: return u + Hrs(_server.HoursAt(u + Hrs(_server.HoursAt(u))));
                case 2: return u;
                case 4: return u + Hrs(_fixed);
                default: return u + Hrs(Clock.HoursAtUtc(_target, u));
            }
        }

        /// <summary>
        /// The time now on CUBE src's own clock, by the earlier of two readings: UTC plus the zone its
        /// messages print, and the receiver's clock now plus the CUBE's latest measured offset. The
        /// earlier one wins so a clock that runs ahead of its own zone still counts as ahead.
        /// -1 when neither is known.
        /// </summary>
        public static long NowOnCube(Store st, int src, DateTime utcNow, ServerZone sz)
        {
            Source s = st.Sources[src];
            long u = TimeUtil.FromDate(utcNow);
            long now = -1;
            double label = s.LastMs >= 0 ? TimeUtil.TzOffsetHours(Clock.LabelAt(s, s.LastMs)) : double.NaN;
            if (!double.IsNaN(label)) now = u + Hrs(label);
            ClockSegment last = null;
            foreach (ClockSegment g in Clock.Segments(s)) if (last == null || g.ServerTo > last.ServerTo) last = g;
            if (last != null && sz != null)
            {
                long m = u + Hrs(sz.HoursAt(u + Hrs(sz.HoursAt(u)))) + last.Offset;
                if (now < 0 || m < now) now = m;
            }
            return now;
        }

        static long Hrs(double h) { return (long)Math.Round(h * Clock.Hour); }

        long Compute(int src, long t)
        {
            List<ClockSegment> segs = _segs[src];
            ClockSegment g = Find(segs, t);
            double label = g == null ? TimeUtil.TzOffsetHours(Clock.LabelAt(_src[src], t)) : double.NaN;
            long utc;
            if (g != null)
            {
                long server = t - g.Offset;
                if (_kind == 1) return server - t;
                utc = server - Hrs(_server.HoursAt(server));
            }
            else if (!double.IsNaN(label)) utc = t - Hrs(label);
            else return 0;
            switch (_kind)
            {
                case 1:
                    long sv = utc + Hrs(_server.HoursAt(utc));
                    return utc + Hrs(_server.HoursAt(sv)) - t;
                case 2: return utc - t;
                case 4: return utc + Hrs(_fixed) - t;
                default: return utc + Hrs(Clock.HoursAtUtc(_target, utc)) - t;
            }
        }

        static ClockSegment Find(List<ClockSegment> segs, long nodeMs)
        {
            ClockSegment near = null;
            long gap = long.MaxValue;
            for (int i = 0; i < segs.Count; i++)
            {
                ClockSegment g = segs[i];
                long a = g.NodeFrom, b = g.NodeTo;
                if (nodeMs >= a && nodeMs < b) return g;
                long d = nodeMs < a ? a - nodeMs : nodeMs - b;
                if (d < gap) { gap = d; near = g; }
            }
            return near;
        }

        /// <summary>A short label for times on this clock around a display time: "EDT", "UTC", "server (UTC-6)".</summary>
        public string Label(long displayMs)
        {
            switch (_kind)
            {
                case 0: return "";
                case 1: return "server clock (" + Clock.Utc(_server.HoursAt(displayMs)) + ")";
                case 2: return "UTC";
                case 4: return Clock.Utc(_fixed);
                default:
                    bool dst = false;
                    try { dst = _target.IsDaylightSavingTime(new DateTime(TimeUtil.EpochTicks + displayMs * 10000, DateTimeKind.Unspecified)); }
                    catch (ArgumentException) { }
                    return Abbrev(_target, dst);
            }
        }

        static string Abbrev(TimeZoneInfo t, bool dst)
        {
            switch (t.Id)
            {
                case "Eastern Standard Time": return dst ? "EDT" : "EST";
                case "Central Standard Time": return dst ? "CDT" : "CST";
                case "Mountain Standard Time": return dst ? "MDT" : "MST";
                case "US Mountain Standard Time": return "MST";
                case "Pacific Standard Time": return dst ? "PDT" : "PST";
                case "Alaskan Standard Time": return dst ? "AKDT" : "AKST";
                case "Hawaiian Standard Time": return "HST";
                case "UTC": return "UTC";
            }
            string name = dst ? t.DaylightName : t.StandardName;
            StringBuilder sb = new StringBuilder();
            foreach (string w in (name ?? "").Split(' ')) if (w.Length > 0 && char.IsUpper(w[0])) sb.Append(w[0]);
            return sb.Length > 0 ? sb.ToString() : Clock.Utc(t.BaseUtcOffset.TotalHours);
        }
    }

    /// <summary>Everything the clock check found for one CUBE.</summary>
    public sealed class ClockReport
    {
        public Source Src;
        public List<ClockSegment> Segments = new List<ClockSegment>();
        public int FilesTimed, FilesUnplaced;
        public string MainTz = "";            // the label most messages carry ("" = none)
        public string Labels = "";            // "EDT 1,234, none 5"
        public bool Measured;
        public long Offset, Spread, ZoneDiff, Drift;   // the latest stretch, ms
        public long DriftLo, DriftHi;                  // the clock error it allows, ms
        public double ClockHours = double.NaN;        // UTC offset the CUBE's clock reads
        public double LabelHours = double.NaN;        // UTC offset its label claims
        public readonly List<string> Notes = new List<string>();
    }

    /// <summary>
    /// Compares each CUBE's clock with the syslog receiver's. The receiver names its files by its own
    /// hour (name-yyyy-mm-dd-hh.log), so every message in a file was received within that hour: for a
    /// file named hour H whose messages are stamped a..b by the CUBE, the offset (CUBE minus receiver)
    /// is above b - (H + 1 h) and at most a - H. A busy CUBE logs near both ends of every hour, which
    /// pins the offset to a second or so. Whole hours of offset are a zone difference; the rest is the
    /// CUBE's clock running fast or slow.
    /// </summary>
    public static class Clock
    {
        public const long Hour = 3600000L;
        /// <summary>How far two files may disagree and still be one stretch (network delay, write buffering).</summary>
        public const long SlackMs = 5000;

        public static List<ClockSegment> Segments(Source s, out int timed, out int unplaced)
        {
            List<FileClock> files = new List<FileClock>();
            timed = 0; unplaced = 0;
            for (int i = 0; i < s.Clocks.Count; i++)
            {
                FileClock f = s.Clocks[i];
                if (f.Lines <= 0 || f.First < 0) continue;
                timed++;
                // no hour in the name; or messages spread over more than an hour (the receiver's DST
                // fall-back hour writes two hours into one file): such a file cannot place the clock
                if (f.NameMs < 0 || f.Last - f.First > Hour + SlackMs) { unplaced++; continue; }
                files.Add(f);
            }
            files.Sort(delegate (FileClock a, FileClock b) { int c = a.NameMs.CompareTo(b.NameMs); return c != 0 ? c : a.File.CompareTo(b.File); });
            List<ClockSegment> segs = new List<ClockSegment>();
            ClockSegment cur = null;
            for (int i = 0; i < files.Count; i++)
            {
                FileClock f = files[i];
                long lo = f.Last - (f.NameMs + Hour), hi = f.First - f.NameMs;
                if (cur != null && (lo > cur.Hi + SlackMs || hi < cur.Lo - SlackMs)) cur = null;
                if (cur == null)
                {
                    cur = new ClockSegment();
                    cur.ServerFrom = f.NameMs; cur.Lo = lo; cur.Hi = hi;
                    segs.Add(cur);
                }
                else
                {
                    cur.Lo = Math.Max(cur.Lo, lo);
                    cur.Hi = Math.Min(cur.Hi, hi);
                }
                cur.ServerTo = Math.Max(cur.ServerTo, f.NameMs + Hour);
                cur.Files++;
                cur.Lines += f.Lines;
            }
            for (int i = 0; i < segs.Count; i++)
            {
                ClockSegment g = segs[i];
                g.Tz = LabelAt(s, g.NodeFrom + (g.NodeTo - g.NodeFrom) / 2);
            }
            return segs;
        }

        public static List<ClockSegment> Segments(Source s)
        {
            int a, b;
            return Segments(s, out a, out b);
        }

        /// <summary>The zone label the CUBE's messages carried at a time on its clock ("" = none).</summary>
        public static string LabelAt(Source s, long nodeMs)
        {
            string tz = "";
            for (int i = 0; i < s.ZoneRuns.Count; i++)
            {
                if (s.ZoneRuns[i].From > nodeMs && i > 0) break;
                tz = s.ZoneRuns[i].Tz ?? "";
            }
            return tz;
        }

        /// <summary>Offset rounded to the nearest quarter hour: the part a zone difference explains.</summary>
        public static long ZonePart(long offsetMs)
        {
            const long Q = 15 * 60000L;
            return (long)Math.Round(offsetMs / (double)Q) * Q;
        }

        /// <summary>Hours a zone adds to UTC at a wall-clock time in that zone.</summary>
        public static double LocalHours(TimeZoneInfo z, long localMs)
        {
            try { return z.GetUtcOffset(new DateTime(TimeUtil.EpochTicks + localMs * 10000, DateTimeKind.Unspecified)).TotalHours; }
            catch (ArgumentException) { return z.BaseUtcOffset.TotalHours; }
        }

        /// <summary>Hours a zone adds to UTC at a UTC time.</summary>
        public static double HoursAtUtc(TimeZoneInfo z, long utcMs)
        {
            try { return z.GetUtcOffset(new DateTime(TimeUtil.EpochTicks + utcMs * 10000, DateTimeKind.Utc)).TotalHours; }
            catch (ArgumentException) { return z.BaseUtcOffset.TotalHours; }
        }

        /// <summary>"UTC", "UTC-6", "UTC+5:30", "local"/"pc", or a Windows zone name. Null when unknown.</summary>
        public static bool ParseZone(string text, out TimeZoneInfo tzi, out double fixedHours)
        {
            tzi = null; fixedHours = double.NaN;
            if (string.IsNullOrEmpty(text)) return false;
            string t = text.Trim();
            if (t.Equals("local", StringComparison.OrdinalIgnoreCase) || t.Equals("pc", StringComparison.OrdinalIgnoreCase)) { tzi = TimeZoneInfo.Local; return true; }
            if (t.Equals("utc", StringComparison.OrdinalIgnoreCase) || t.Equals("gmt", StringComparison.OrdinalIgnoreCase)) { fixedHours = 0; return true; }
            if (t.StartsWith("UTC", StringComparison.OrdinalIgnoreCase) && t.Length > 4 && (t[3] == '+' || t[3] == '-'))
            {
                string[] p = t.Substring(4).Split(':');
                int h, m = 0;
                if (!int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out h) || h > 14) return false;
                if (p.Length > 1 && !int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out m)) return false;
                fixedHours = (h + m / 60.0) * (t[3] == '-' ? -1 : 1);
                return true;
            }
            try { tzi = TimeZoneInfo.FindSystemTimeZoneById(t); return true; }
            catch (Exception) { return false; }
        }

        /// <summary>The receiver's zone, by the option, else by the CUBEs' labels, else this PC's.</summary>
        public static ServerZone Server(Store st)
        {
            ServerZone z = new ServerZone();
            if (!string.IsNullOrEmpty(st.ServerZoneId))
            {
                if (ParseZone(st.ServerZoneId, out z.Tzi, out z.FixedHours))
                {
                    z.How = "option";
                    z.Text = "set with --server-zone " + st.ServerZoneId;
                    return z;
                }
            }
            // each stretch with a known label says: receiver = label - zone part of the offset
            Dictionary<double, long> votes = new Dictionary<double, long>();
            Dictionary<double, int> voters = new Dictionary<double, int>();
            for (int i = 0; i < st.Sources.Count; i++)
            {
                Source s = st.Sources[i];
                List<ClockSegment> segs = Segments(s);
                bool counted = false;
                for (int k = 0; k < segs.Count; k++)
                {
                    ClockSegment g = segs[k];
                    double L = TimeUtil.TzOffsetHours(g.Tz);
                    // a range an hour wide or more cannot tell one zone from the next
                    if (double.IsNaN(L) || Math.Abs(g.Hi - g.Lo) >= Hour - 60000) continue;
                    ServerZone.Piece p = new ServerZone.Piece();
                    p.From = g.ServerFrom; p.To = g.ServerTo; p.Weight = g.Lines;
                    p.Hours = L - g.ZoneDiff / (double)Hour;
                    z.Pieces.Add(p);
                    long w;
                    votes.TryGetValue(p.Hours, out w);
                    votes[p.Hours] = w + g.Lines;
                    if (!counted) { int n; voters.TryGetValue(p.Hours, out n); voters[p.Hours] = n + 1; counted = true; }
                }
            }
            if (z.Pieces.Count > 0)
            {
                z.How = "labels";
                List<KeyValuePair<double, long>> l = new List<KeyValuePair<double, long>>(votes);
                l.Sort(delegate (KeyValuePair<double, long> a, KeyValuePair<double, long> b) { return b.Value.CompareTo(a.Value); });
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < l.Count; i++)
                {
                    if (i > 0) sb.Append(", at other times ");
                    int nv; voters.TryGetValue(l[i].Key, out nv);
                    sb.Append(Utc(l[i].Key)).Append(" (").Append(nv).Append(nv == 1 ? " CUBE agrees)" : " CUBEs agree)");
                }
                sb.Append(", worked out from the zone labels the CUBEs print");
                z.Text = sb.ToString();
                return z;
            }
            z.How = "pc";
            z.Tzi = TimeZoneInfo.Local;
            z.Text = "taken from this computer (" + TimeZoneInfo.Local.StandardName + "): no CUBE prints a zone label to work it out from";
            return z;
        }

        public static string Utc(double hours)
        {
            if (double.IsNaN(hours)) return "?";
            if (Math.Abs(hours) < 1e-9) return "UTC";
            int mins = (int)Math.Round(Math.Abs(hours) * 60);
            string s = "UTC" + (hours < 0 ? "-" : "+") + (mins / 60).ToString(CultureInfo.InvariantCulture);
            if (mins % 60 != 0) s += ":" + (mins % 60).ToString("00", CultureInfo.InvariantCulture);
            return s;
        }

        /// <summary>"+2:00:00.4": a signed offset as hours, minutes, seconds and a tenth.</summary>
        public static string FmtOffset(long ms)
        {
            string sign = ms < 0 ? "-" : "+";
            long a = Math.Abs(ms);
            long h = a / Hour, m = a / 60000 % 60;
            double sec = (a % 60000) / 1000.0;
            return sign + h.ToString(CultureInfo.InvariantCulture) + ":" + m.ToString("00", CultureInfo.InvariantCulture) + ":" +
                   sec.ToString("00.0", CultureInfo.InvariantCulture);
        }

        /// <summary>"0.4 s", "12 s", "3 min", "2 h": a length of time for a sentence.</summary>
        public static string FmtSpan(long ms)
        {
            long a = Math.Abs(ms);
            if (a < 10000) return (a / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s";
            if (a < 120000) return ((long)Math.Round(a / 1000.0, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture) + " s";
            if (a < 2 * Hour) return ((long)Math.Round(a / 60000.0, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture) + " min";
            return (a / (double)Hour).ToString("0.#", CultureInfo.InvariantCulture) + " h";
        }

        /// <summary>A clock error proven beyond this is reported.</summary>
        public const long DriftNoteMs = 2000;

        /// <summary>"12 s fast", "3 to 4 min slow", "no error seen (could be up to 44 s off)".</summary>
        public static string ErrorText(long lo, long hi)
        {
            if (lo > DriftNoteMs)
                return FmtSpan(lo) + (hi - lo > 2000 ? " to " + FmtSpan(hi) : "") + " fast";
            if (hi < -DriftNoteMs)
                return FmtSpan(-hi) + (hi - lo > 2000 ? " to " + FmtSpan(-lo) : "") + " slow";
            long most = Math.Max(Math.Abs(lo), Math.Abs(hi));
            return "no error seen" + (most > DriftNoteMs ? " (could be up to " + FmtSpan(most) + " off)" : " (within " + FmtSpan(Math.Max(most, 1000)) + ")");
        }

        /// <summary>"+2 h", "-1 h", "+5 h 30 min", "0 h".</summary>
        public static string FmtZoneDiff(long ms)
        {
            if (ms == 0) return "0 h";
            long a = Math.Abs(ms), h = a / Hour, m = a / 60000 % 60;
            return (ms < 0 ? "-" : "+") + h.ToString(CultureInfo.InvariantCulture) + " h" + (m != 0 ? " " + m.ToString(CultureInfo.InvariantCulture) + " min" : "");
        }

        /// <summary>"2 h ahead of the server", "1 h behind the server", "on the server's hour".</summary>
        public static string ZoneWords(long ms)
        {
            if (ms == 0) return "on the server's hour";
            string d = FmtZoneDiff(ms).Substring(1);
            return d + (ms > 0 ? " ahead of" : " behind") + " the server";
        }

        /// <summary>US daylight-saving time at a local date: second Sunday of March to first Sunday of November.</summary>
        public static bool UsDaylight(DateTime d)
        {
            int y = d.Year;
            DateTime start = NthSunday(y, 3, 2).AddHours(2), end = NthSunday(y, 11, 1).AddHours(2);
            return d >= start && d < end;
        }

        static DateTime NthSunday(int y, int month, int n)
        {
            DateTime d = new DateTime(y, month, 1);
            while (d.DayOfWeek != DayOfWeek.Sunday) d = d.AddDays(1);
            return d.AddDays(7 * (n - 1));
        }

        static readonly string[] UsStandard = { "EST", "CST", "MST", "PST", "AKST" };
        static readonly string[] UsDaylightLabels = { "EDT", "CDT", "MDT", "PDT", "AKDT" };

        public static ClockReport Report(Store st, Source s, ServerZone server)
        {
            ClockReport r = new ClockReport();
            r.Src = s;
            r.Segments = Segments(s, out r.FilesTimed, out r.FilesUnplaced);
            // labels, most messages first
            List<KeyValuePair<string, long>> l = new List<KeyValuePair<string, long>>(s.ZoneCounts);
            l.Sort(delegate (KeyValuePair<string, long> a, KeyValuePair<string, long> b) { return b.Value.CompareTo(a.Value); });
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < l.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(l[i].Key.Length == 0 ? "none" : l[i].Key).Append(' ').Append(l[i].Value.ToString("N0", CultureInfo.InvariantCulture));
            }
            r.Labels = sb.ToString();
            r.MainTz = l.Count > 0 ? l[0].Key : "";
            if (r.Segments.Count == 0)
            {
                if (r.FilesTimed == 0) r.Notes.Add("No timed syslog messages yet, so the clock cannot be checked.");
                else r.Notes.Add("The file names carry no hour (name-yyyy-mm-dd-hh.log), so there is nothing to compare the clock with.");
            }
            else
            {
                ClockSegment g = r.Segments[r.Segments.Count - 1];
                r.Measured = true;
                r.Offset = g.Offset; r.Spread = g.Spread;
                r.ZoneDiff = g.ZoneDiff;
                r.Drift = g.Offset - r.ZoneDiff;
                r.DriftLo = g.DriftLo; r.DriftHi = g.DriftHi;
                long mid = g.ServerFrom + (g.ServerTo - g.ServerFrom) / 2;
                r.ClockHours = server.HoursAt(mid) + r.ZoneDiff / (double)Hour;
                r.LabelHours = TimeUtil.TzOffsetHours(g.Tz);
                if (r.DriftLo > DriftNoteMs || r.DriftHi < -DriftNoteMs)
                    r.Notes.Add("Its clock is " + ErrorText(r.DriftLo, r.DriftHi) + " against the syslog server's. Check NTP on the CUBE.");
                else if (r.DriftHi - r.DriftLo > 10 * 60000L)
                    r.Notes.Add("It logs little near the start and end of each hour, so the clock check is loose.");
                if (!double.IsNaN(r.LabelHours) && Math.Abs(r.LabelHours - r.ClockHours) > 0.01)
                    r.Notes.Add("It labels its times " + g.Tz + " (" + Utc(r.LabelHours) + ") but its clock reads " + Utc(r.ClockHours) +
                                ", going by the syslog server's zone.");
                if (r.Segments.Count > 1)
                    r.Notes.Add("The offset changed " + (r.Segments.Count - 1) + (r.Segments.Count == 2 ? " time" : " times") +
                                ": a daylight-saving change on one side only, a clock reset, or NTP stepping it.");
                // US standard-time label in summer (or the reverse): summer time is probably not configured
                DateTime at = TimeUtil.ToDate(g.NodeFrom + (g.NodeTo - g.NodeFrom) / 2);
                bool summer = UsDaylight(at);
                if (summer && Array.IndexOf(UsStandard, g.Tz) >= 0)
                    r.Notes.Add("It prints " + g.Tz + " during US daylight-saving time: 'clock summer-time' is probably missing" +
                                (g.Tz == "MST" ? " (unless it is in Arizona)" : "") + ".");
                if (!summer && Array.IndexOf(UsDaylightLabels, g.Tz) >= 0)
                    r.Notes.Add("It prints " + g.Tz + " outside US daylight-saving time: check 'clock summer-time'.");
            }
            if (r.MainTz.Length == 0 && r.FilesTimed > 0)
                r.Notes.Add("Its messages carry no zone label: add show-timezone to 'service timestamps log datetime msec localtime'.");
            if (s.ClockNotSet > 0)
                r.Notes.Add(s.ClockNotSet.ToString("N0", CultureInfo.InvariantCulture) + " messages are marked * (IOS never set the clock from NTP), " +
                            TimeUtil.FmtShort(s.NotSetFirst) + " to " + TimeUtil.FmtShort(s.NotSetLast) + " by its clock.");
            if (s.ClockNotSynced > 0)
                r.Notes.Add(s.ClockNotSynced.ToString("N0", CultureInfo.InvariantCulture) + " messages are marked . (clock set, but NTP not synchronised at the time).");
            return r;
        }

        public static void WriteJson(Json j, Store st)
        {
            ServerZone z = Server(st);
            j.Key("clock").Obj();
            j.P("serverHow", z.How).P("serverText", z.Text);
            j.Key("cubes").Arr();
            for (int i = 0; i < st.Sources.Count; i++)
            {
                ClockReport r = Report(st, st.Sources[i], z);
                j.Obj().P("src", i).P("name", r.Src.Name).P("labels", r.Labels).P("tz", r.MainTz).P("measured", r.Measured)
                 .P("filesTimed", r.FilesTimed).P("filesUnplaced", r.FilesUnplaced);
                if (r.Measured)
                {
                    j.P("offset", r.Offset).P("spread", r.Spread).P("zoneDiff", r.ZoneDiff).P("drift", r.Drift)
                     .P("driftLo", r.DriftLo).P("driftHi", r.DriftHi).P("zoneText", FmtZoneDiff(r.ZoneDiff)).P("zoneWords", ZoneWords(r.ZoneDiff))
                     .P("errorText", ErrorText(r.DriftLo, r.DriftHi)).P("errorBad", r.DriftLo > DriftNoteMs || r.DriftHi < -DriftNoteMs)
                     .P("clockUtc", Utc(r.ClockHours)).P("labelUtc", double.IsNaN(r.LabelHours) ? "" : Utc(r.LabelHours));
                }
                j.Key("notes").Arr();
                for (int k = 0; k < r.Notes.Count; k++) j.Val(r.Notes[k]);
                j.EndArr();
                j.Key("segments").Arr();
                int from = Math.Max(0, r.Segments.Count - 12);
                for (int k = from; k < r.Segments.Count; k++)
                {
                    ClockSegment g = r.Segments[k];
                    j.Obj().P("from", g.ServerFrom / 1000).P("to", g.ServerTo / 1000).P("offset", g.Offset).P("spread", g.Spread)
                     .P("files", g.Files).P("tz", g.Tz).P("zoneWords", ZoneWords(g.ZoneDiff)).P("errorText", ErrorText(g.DriftLo, g.DriftHi)).EndObj();
                }
                j.EndArr();
                j.P("segmentsOmitted", from);
                j.EndObj();
            }
            j.EndArr();
            j.EndObj();
        }

        /// <summary>The clock section of the console summary.</summary>
        public static void WriteText(Store st, System.IO.TextWriter w)
        {
            ServerZone z = Server(st);
            w.WriteLine("Clocks against the syslog server (its zone: " + z.Text + "):");
            for (int i = 0; i < st.Sources.Count; i++)
            {
                ClockReport r = Report(st, st.Sources[i], z);
                string line = "  " + r.Src.Name + ": labels " + (r.Labels.Length == 0 ? "nothing yet" : r.Labels);
                if (r.Measured)
                    line += "; " + ZoneWords(r.ZoneDiff) + ", so its clock reads " + Utc(r.ClockHours) +
                            "; clock error: " + ErrorText(r.DriftLo, r.DriftHi);
                w.WriteLine(line);
                for (int k = 0; k < r.Notes.Count; k++) w.WriteLine("      " + r.Notes[k]);
                if (r.Segments.Count > 1)
                    for (int k = Math.Max(0, r.Segments.Count - 6); k < r.Segments.Count; k++)
                    {
                        ClockSegment g = r.Segments[k];
                        w.WriteLine("      from " + TimeUtil.FmtShort(g.ServerFrom) + " (server clock): " + FmtZoneDiff(g.ZoneDiff) + ", error " +
                                    ErrorText(g.DriftLo, g.DriftHi) + (g.Tz.Length > 0 ? ", labels " + g.Tz : "") + ", " + g.Files + " files");
                    }
            }
        }
    }
}
