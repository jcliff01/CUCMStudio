using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CubeScope
{
    /// <summary>
    /// The prefix the syslog receiver and IOS put in front of a message:
    /// <c>&lt;rxseq&gt;: &lt;iosseq&gt;: [*]Mon d [yyyy] hh:mm:ss[.mmm] [TZ]: message</c>. Both
    /// sequence numbers, the year and the zone are optional. The header carries no year unless the
    /// CUBE has <c>service timestamps log datetime ... year</c>.
    /// </summary>
    public struct Header
    {
        public long Rx, Ios;
        public int Nums;            // how many leading "n: " numbers were found
        public bool HasTime;
        public int Year, Month, Day, Hour, Min, Sec, Milli;
        public string Tz;           // null when the header has no zone token
        /// <summary>'*' = IOS has never set the clock from NTP, '.' = set but not synchronised now, else 0.</summary>
        public char ClockMark;
        public int Msg;             // index where the message starts

        /// <summary>Parses the prefix of s. Never throws; Msg is 0 for a bare line.</summary>
        public static Header Parse(string s)
        {
            Header h = new Header();
            int n = s.Length, i = 0;
            for (int k = 0; k < 2; k++)
            {
                int j = i;
                long v = 0;
                while (j < n && s[j] >= '0' && s[j] <= '9' && j - i < 18) { v = v * 10 + (s[j] - '0'); j++; }
                if (j > i && j + 1 < n && s[j] == ':' && s[j + 1] == ' ')
                {
                    if (h.Nums == 0) h.Rx = v; else h.Ios = v;
                    h.Nums++;
                    i = j + 2;
                }
                else break;
            }
            h.Msg = i;
            int p = i;
            char mark = '\0';
            if (p < n && (s[p] == '*' || s[p] == '.')) { mark = s[p]; p++; }      // clock not in sync with NTP
            int mon = TimeUtil.MonthOf(s, p);
            if (mon == 0 || p + 3 >= n || s[p + 3] != ' ') return h;
            p += 4;
            while (p < n && s[p] == ' ') p++;
            int day = 0, dd = 0;
            while (p < n && s[p] >= '0' && s[p] <= '9' && dd < 2) { day = day * 10 + (s[p] - '0'); p++; dd++; }
            if (dd == 0 || p >= n || s[p] != ' ') return h;
            p++;
            int year = 0;
            // optional year: four digits then a space
            if (p + 5 <= n && Digits(s, p, 4) && s[p + 4] == ' ') { year = Num(s, p, 4); p += 5; }
            if (p + 8 > n || !Digits(s, p, 2) || s[p + 2] != ':' || !Digits(s, p + 3, 2) || s[p + 5] != ':' || !Digits(s, p + 6, 2))
                return h;
            int hh = Num(s, p, 2), mi = Num(s, p + 3, 2), ss = Num(s, p + 6, 2), ms = 0;
            p += 8;
            if (p < n && s[p] == '.')
            {
                p++;
                int q = 0, m = 0;
                while (p < n && s[p] >= '0' && s[p] <= '9') { if (q < 3) { m = m * 10 + (s[p] - '0'); q++; } p++; }
                while (q < 3) { m *= 10; q++; }
                ms = m;
            }
            string tz = null;
            if (p < n && s[p] == ' ')
            {
                int a = p + 1, b = a;
                while (b < n && b - a < 6 && s[b] >= 'A' && s[b] <= 'Z') b++;
                if (b == a || b >= n || s[b] != ':') return h;
                tz = s.Substring(a, b - a);
                p = b;
            }
            if (p >= n || s[p] != ':') return h;
            p++;
            if (p < n && s[p] == ' ') p++;
            h.HasTime = true;
            h.Year = year; h.Month = mon; h.Day = day; h.Hour = hh; h.Min = mi; h.Sec = ss; h.Milli = ms;
            h.Tz = tz;
            h.ClockMark = mark;
            h.Msg = p;
            return h;
        }

        static bool Digits(string s, int at, int count)
        {
            if (at + count > s.Length) return false;
            for (int i = 0; i < count; i++) if (s[at + i] < '0' || s[at + i] > '9') return false;
            return true;
        }

        static int Num(string s, int at, int count)
        {
            int v = 0;
            for (int i = 0; i < count; i++) v = v * 10 + (s[at + i] - '0');
            return v;
        }
    }

    /// <summary>What one file (or one stretch of a growing file) produced.</summary>
    public sealed class ParsedChunk
    {
        public readonly List<object> Records = new List<object>();
        /// <summary>The call key of every Leg in Records, in the same order (a leg does not carry it).</summary>
        public readonly List<string> LegKeys = new List<string>();

        public void AddLeg(Leg g, string key) { Records.Add(g); LegKeys.Add(key); }

        public long Lines, Bytes, Accounting, Features, Events, Skipped, BadRecords;
        /// <summary>
        /// Gaps in the IOS sequence number. Debug lines count too when the feed shows they share the
        /// counter (LineParser.NoteSideLine). Each missing number is a message the CUBE sent that is not
        /// in the file: lost in transit (UDP) or below the syslog trap level.
        /// </summary>
        public long IosGaps, IosMissing, IosRestarts;
        public long FirstIos = -1, LastIos = -1;
        public readonly List<string> Warnings = new List<string>();
        public string LastTz;
        public long FirstMs = -1, LastMs = -1;
        /// <summary>From a call record file: a leg with several features repeats, one row per feature.</summary>
        public bool OneRowPerFeature;
        /// <summary>The extra columns of a call record file, for the legs read from one.</summary>
        public Dictionary<Leg, LegDetail> Details;
        /// <summary>From gw-accounting syslog stats: legs with real numbers. The leg may have been in the chunk before this one.</summary>
        public Dictionary<Leg, LegDetail> MeasuredLegs;
        public long EndPos = -1;          // bytes consumed, for a tailer to carry on from
        public int EndLine;

        // The CUBE's clock as its message headers show it, for comparing with the receiver's hour in
        // the file name (Clock.cs). Syslog messages only: debug trace lines are stamped differently.
        public int FileIndex = -1;
        public long ClockFirst = -1, ClockLast = -1;
        public long ClockLines, ClockNotSet, ClockNotSynced;
        public long NotSetFirst = -1, NotSetLast = -1;
        /// <summary>Zone label ("" = none) -> messages; created on first use.</summary>
        public Dictionary<string, long> ZoneCounts;
        /// <summary>Where the label changed, in log order (the first message counts as a change).</summary>
        public List<ZoneRun> ZoneRuns;

        public void Warn(string w) { if (Warnings.Count < 20) Warnings.Add(w); }
    }

    /// <summary>
    /// Turns syslog lines into Leg, Feature, IecError, TraceDump and SysEvent objects, in log order.
    /// It keeps the state a line needs from the lines before it: the year to give header times,
    /// the zone to convert dump times with, and the Cover Buffer dump being collected. One parser
    /// per file for a closed file; one per source, kept across polls, for the file being tailed.
    /// </summary>
    public sealed class LineParser
    {
        public int FileIndex;
        public int RefYear, RefMonth;        // year/month the next header-only time belongs to
        public string Tz;                    // the stream's zone (from accounting headers)
        ParsedChunk _out = new ParsedChunk();
        long _lastIos = -1;

        // Debug output (trace lines and plain debug lines) takes its numbers from the same IOS counter as the messages
        // on a CUBE with debug on, but the few trace lines of a feed without debug do not. Each feed shows which:
        // a debug line numbered right after a message, or a message right after a debug line, a few times over.
        const int SideShareHits = 3;
        int _sideHits;
        bool _sideShares;
        long _lastSide = -1, _firstSide = -1;
        long _logMs = -1;                    // the last %-message's header time: the CUBE's own clock

        // Cover Buffer collection
        TraceDump _dump;
        StringBuilder _dumpText;
        int _dumpPhase;                      // 1 = key = value header, 2 = trace body
        string _lastTraceText; int _lastTraceLine; string _lastTraceCall, _lastTraceGuid;
        const int DumpMaxLines = 600, DumpMaxChars = 64 * 1024;

        public const string CallHistory = "VOIPAAA-5-VOIP_CALL_HISTORY";
        public const string FeatHistory = "VOIPAAA-5-VOIP_FEAT_HISTORY";
        public const string CallStats = "VOIP_CALL_STATISTICS";
        Leg _lastLeg;                        // the leg a statistics block belongs to: it follows that leg's records
        LegQuality _stats;                   // the statistics block being read

        public LineParser(int fileIndex, int refYear, int refMonth)
        {
            FileIndex = fileIndex; RefYear = refYear; RefMonth = refMonth;
        }

        /// <summary>The chunk being filled.</summary>
        public ParsedChunk Current { get { return _out; } }

        /// <summary>Hands over what has been parsed so far and starts a new chunk.</summary>
        public ParsedChunk Take()
        {
            ParsedChunk c = _out;
            c.LastTz = Tz;
            c.FileIndex = FileIndex;
            _out = new ParsedChunk();
            // Gaps inside a chunk are the parser's to count; the gap between this chunk and the next
            // is the store's (it compares FirstIos with the previous LastIos). A parser kept across
            // polls must not count that boundary too.
            _lastIos = -1;
            _lastSide = -1; _firstSide = -1;      // what the feed has shown about its debug lines stays
            return c;
        }

        public void Feed(string s, int lineNo)
        {
            _out.Lines++;
            _out.Bytes += s.Length + 2;
            if (s.Length == 0) { if (_dump != null) AppendDump(""); return; }
            Header h = Header.Parse(s);
            int m = h.Msg;
            // gw-accounting syslog stats: a block after each leg's records. It is not a %-message, but it does use up
            // an IOS sequence number, so left alone every block would read as a lost message.
            if (h.HasTime && string.CompareOrdinal(s, m, CallStats, 0, CallStats.Length) == 0)
            {
                CloseStats();
                NoteMessage(h);
                _stats = new LegQuality();
                ReadStats(s, m);
                return;
            }
            if (_stats != null)
            {
                if (string.CompareOrdinal(s, m, "DSP/", 0, 4) == 0) { ReadStats(s, m); return; }
                CloseStats();
            }
            if (h.HasTime && m < s.Length && s[m] == '%')
            {
                if (h.Tz != null && !h.Tz.Equals("UTC") && h.Tz.Length > 0) Tz = h.Tz;
                NoteClock(h);
                CloseDump();
                NoteMessage(h);
                Message(s, h, lineNo);
                return;
            }
            if (h.HasTime && h.Nums == 2) NoteSideLine(h);
            if (!h.HasTime)
            {
                // A receiver with a prefix this parser does not know still yields the accounting:
                // the records carry their own full times.
                int k = s.IndexOf("%" + CallHistory + ": ", StringComparison.Ordinal);
                if (k < 0) k = s.IndexOf("%" + FeatHistory + ": ", StringComparison.Ordinal);
                if (k >= 0)
                {
                    CloseDump();
                    h.Msg = k;
                    Message(s, h, lineNo);
                    return;
                }
            }
            if (h.HasTime && m + 1 < s.Length && s[m] == '/' && s[m + 1] == '/')
            {
                Trace(s, h, lineNo);
                return;
            }
            if (s.StartsWith("------------------ Cover Buffer", StringComparison.Ordinal))
            {
                StartDump(lineNo);
                return;
            }
            if (_dump != null) { AppendDump(h.Nums > 0 && !h.HasTime ? s.Substring(h.Msg) : s); return; }
            _out.Skipped++;
        }

        /// <summary>End of file: a dump or a statistics block still open is complete.</summary>
        public void Flush() { CloseDump(); CloseStats(); }

        /// <summary>A %-message or a statistics block: a number the counter certainly used.</summary>
        void NoteMessage(Header h)
        {
            if (h.Nums == 2 && !_sideShares && _lastSide >= 0 && h.Ios == _lastSide + 1)
            {
                // the debug lines since the last message account for the numbers in between
                _lastIos = _lastSide;
                if (++_sideHits >= SideShareHits) ShareSide();
            }
            NoteSequence(h);
        }

        /// <summary>A timed line that is not a message: part of the count only once the feed has shown its debug lines share the counter.</summary>
        void NoteSideLine(Header h)
        {
            if (!_sideShares)
            {
                if (_lastIos >= 0 && h.Ios == _lastIos + 1 && ++_sideHits >= SideShareHits) ShareSide();
                if (!_sideShares)
                {
                    _lastSide = h.Ios;
                    if (_firstSide < 0) _firstSide = h.Ios;
                    return;
                }
            }
            NoteSequence(h);
        }

        void ShareSide()
        {
            _sideShares = true;
            // the chunk really began with the debug lines before its first message
            if (_firstSide >= 0 && (_out.FirstIos < 0 || _firstSide < _out.FirstIos)) _out.FirstIos = _firstSide;
        }

        void NoteSequence(Header h)
        {
            if (h.Nums != 2) return;
            if (_lastIos >= 0 && h.Ios != _lastIos + 1)
            {
                long miss = h.Ios - _lastIos - 1;
                if (miss > 0 && miss < 1000000) { _out.IosGaps++; _out.IosMissing += miss; }
                else _out.IosRestarts++;           // reload, or the counter wrapped
            }
            if (_out.FirstIos < 0) _out.FirstIos = h.Ios;
            _out.LastIos = h.Ios;
            _lastIos = h.Ios;
        }

        static double StatValue(string s, int from, string key)
        {
            int k = s.IndexOf(key, from, StringComparison.Ordinal);
            if (k < 0) return 0;
            k += key.Length;
            int e = k;
            while (e < s.Length && (char.IsDigit(s[e]) || s[e] == '.')) e++;
            double v;
            return e > k && double.TryParse(s.Substring(k, e - k), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        /// <summary>The numbers in one line of the block: most of them are DSP counters, zero on a call with no DSP in its path.</summary>
        void ReadStats(string s, int at)
        {
            int tag = s.IndexOf("DSP/", at, StringComparison.Ordinal);
            if (tag < 0) return;
            if (string.CompareOrdinal(s, tag, "DSP/RF:", 0, 7) == 0)
            {
                double mos = StatValue(s, tag, "MC=");           // conversational MOS, else listening
                _stats.Mos = mos > 0 ? mos : StatValue(s, tag, "ML=");
            }
            else if (string.CompareOrdinal(s, tag, "DSP/PD:", 0, 7) == 0) _stats.Jitter = StatValue(s, tag, "IJ=");
            else if (string.CompareOrdinal(s, tag, "DSP/RX:", 0, 7) == 0)
            {
                _stats.LatePackets = (int)StatValue(s, tag, "LP=");
                _stats.EarlyPackets = (int)StatValue(s, tag, "EP=");
            }
        }

        /// <summary>Gives the finished block to the leg before it. An all-zero block says the leg was not measured: nothing is kept.</summary>
        void CloseStats()
        {
            LegQuality q = _stats;
            _stats = null;
            if (q == null || _lastLeg == null) return;
            if (_out.MeasuredLegs == null) _out.MeasuredLegs = new Dictionary<Leg, LegDetail>();
            // a block of zeros still says the CUBE is measuring: the store marks the leg, and keeps no detail for it
            if (q.Mos == 0 && q.Jitter == 0 && q.LatePackets == 0 && q.EarlyPackets == 0) { _out.MeasuredLegs[_lastLeg] = null; return; }
            LegDetail d = new LegDetail();
            d.Quality = q;
            _out.MeasuredLegs[_lastLeg] = d;
        }

        string _lastLabel;

        /// <summary>A syslog message's header time, label and sync mark, for the clock check.</summary>
        void NoteClock(Header h)
        {
            long t = HeaderMs(h);
            if (t < 0) return;
            _logMs = t;
            ParsedChunk o = _out;
            o.ClockLines++;
            if (o.ClockFirst < 0 || t < o.ClockFirst) o.ClockFirst = t;
            if (t > o.ClockLast) o.ClockLast = t;
            if (h.ClockMark == '*')
            {
                o.ClockNotSet++;
                if (o.NotSetFirst < 0) o.NotSetFirst = t;
                o.NotSetLast = t;
            }
            else if (h.ClockMark == '.') o.ClockNotSynced++;
            string label = h.Tz ?? "";
            if (o.ZoneCounts == null) o.ZoneCounts = new Dictionary<string, long>(StringComparer.Ordinal);
            long n;
            o.ZoneCounts.TryGetValue(label, out n);
            o.ZoneCounts[label] = n + 1;
            if (o.ZoneRuns == null || _lastLabel == null || !string.Equals(label, _lastLabel, StringComparison.Ordinal))
            {
                if (o.ZoneRuns == null) o.ZoneRuns = new List<ZoneRun>(2);
                ZoneRun r; r.From = t; r.Tz = label;
                o.ZoneRuns.Add(r);
                _lastLabel = label;
            }
        }

        // ------------------------------------------------------------ messages

        void Message(string s, Header h, int lineNo)
        {
            int m = h.Msg + 1;
            int colon = s.IndexOf(": ", m, StringComparison.Ordinal);
            if (colon < 0) { _out.Skipped++; return; }
            string mnem = s.Substring(m, colon - m);
            int body = colon + 2;
            if (mnem == CallHistory)
            {
                string key;
                Leg leg = ParseLeg(s, body, out key);
                if (leg == null)
                {
                    _out.BadRecords++;
                    _out.Warn("line " + lineNo + ": call history record could not be read");
                    return;
                }
                leg.File = FileIndex; leg.Line = lineNo;
                RefYear = TimeUtil.ToDate(leg.Disconnect).Year;
                RefMonth = TimeUtil.ToDate(leg.Disconnect).Month;
                _out.AddLeg(leg, key);
                _lastLeg = leg;
                _out.Accounting++;
                Span(leg.Disconnect);
                return;
            }
            if (mnem == FeatHistory)
            {
                Feature f = ParseFeature(s, body);
                if (f == null)
                {
                    _out.BadRecords++;
                    _out.Warn("line " + lineNo + ": feature record could not be read");
                    return;
                }
                f.File = FileIndex; f.Line = lineNo;
                _out.Records.Add(f);
                _out.Features++;
                return;
            }
            long ts = HeaderMs(h);
            if (ts < 0) { _out.Skipped++; return; }
            Span(ts);
            if (mnem.StartsWith("VOICE_IEC-", StringComparison.Ordinal))
            {
                IecError e = ParseIec(s, body);
                e.Ts = ts; e.File = FileIndex; e.Line = lineNo;
                _out.Records.Add(e);
            }
            SysEvent ev = new SysEvent();
            ev.Ts = ts; ev.Mnemonic = mnem; ev.Text = Redact.Secrets(s.Substring(body).Trim());
            ev.Kind = KindOf(mnem);
            ev.File = FileIndex; ev.Line = lineNo;
            if (ev.Kind == "licence") ev.Value = LicenceCount(ev.Text);
            _out.Records.Add(ev);
            _out.Events++;
        }

        void Span(long ms)
        {
            if (_out.FirstMs < 0 || ms < _out.FirstMs) _out.FirstMs = ms;
            if (ms > _out.LastMs) _out.LastMs = ms;
        }

        /// <summary>Header time with the year inferred: the year of the newest accounting record,
        /// stepped across a New Year when the month wraps.</summary>
        public long HeaderMs(Header h)
        {
            if (!h.HasTime) return -1;
            int y = h.Year > 0 ? h.Year : RefYear;
            if (h.Year == 0 && RefMonth > 0)
            {
                if (h.Month == 1 && RefMonth == 12) y++;
                else if (h.Month == 12 && RefMonth == 1) y--;
            }
            if (y <= 0) y = DateTime.Now.Year;
            return TimeUtil.Ms(y, h.Month, h.Day, h.Hour, h.Min, h.Sec, h.Milli);
        }

        public static string KindOf(string mnem)
        {
            if (mnem.StartsWith("VOICE_IEC-", StringComparison.Ordinal)) return "iec";
            if (mnem == "SIP-2-TLS_HANDSHAKE_FAILED") return "tls-fail";
            if (mnem.StartsWith("SIP-", StringComparison.Ordinal) && mnem.IndexOf("TLS", StringComparison.Ordinal) >= 0) return "tls";
            if (mnem == "SIP-3-BADPAIR" || mnem == "SIP-6-IGNORE") return "sip-state";
            if (mnem.StartsWith("CUBE-", StringComparison.Ordinal) && mnem.EndsWith("LICENSE_INFO", StringComparison.Ordinal)) return "licence";
            if (mnem.StartsWith("SEC_LOGIN-", StringComparison.Ordinal) || mnem.StartsWith("SSH-", StringComparison.Ordinal) ||
                mnem == "SYS-6-LOGOUT" || mnem == "SYS-6-TTY_EXPIRE_TIMER" || mnem.StartsWith("SYS-6-LOGGINGHOST", StringComparison.Ordinal))
                return "login";
            if (mnem.StartsWith("PARSER-", StringComparison.Ordinal) || mnem == "SYS-5-CONFIG_I") return "config";
            if (mnem.StartsWith("SIP-", StringComparison.Ordinal) || mnem.StartsWith("CCSIP", StringComparison.Ordinal)) return "sip";
            return "other";
        }

        static double LicenceCount(string text)
        {
            int k = text.IndexOf("Requesting for ", StringComparison.Ordinal);
            if (k < 0) return double.NaN;
            k += 15;
            int v = 0, d = 0;
            while (k < text.Length && text[k] >= '0' && text[k] <= '9') { v = v * 10 + (text[k] - '0'); k++; d++; }
            return d > 0 ? v : double.NaN;
        }

        // ------------------------------------------------------------ VOIP_CALL_HISTORY

        static readonly string[] LegFields = {
            "CallLegType", "ConnectionId", "SetupTime", "PeerAddress", "PeerSubAddress", "DisconnectCause",
            "DisconnectText", "ConnectTime", "DisconnectTime", "CallOrigin", "ChargedUnits", "InfoType",
            "TransmitPackets", "TransmitBytes", "ReceivePackets", "ReceiveBytes" };

        /// <summary>
        /// Values are found by the known field names in their fixed order, not by splitting on
        /// commas, because a value may be empty, padded or (DisconnectText) contain spaces.
        /// </summary>
        public static Leg ParseLeg(string s, int body, out string key)
        {
            key = null;
            string[] v = new string[LegFields.Length];
            int pos = body;
            int start = -1, prevField = -1;
            for (int k = 0; k < LegFields.Length; k++)
            {
                int at;
                if (k == 0)
                {
                    at = s.IndexOf(LegFields[0] + " ", pos, StringComparison.Ordinal);
                    if (at < 0) return null;
                    at += LegFields[0].Length + 1;
                }
                else
                {
                    int f = s.IndexOf(", " + LegFields[k] + " ", pos, StringComparison.Ordinal);
                    if (f < 0)
                    {
                        // tolerate "Name ," with nothing after (an empty last field)
                        if (s.EndsWith(", " + LegFields[k], StringComparison.Ordinal)) f = s.Length - LegFields[k].Length - 2;
                        else continue;
                    }
                    if (prevField >= 0) v[prevField] = s.Substring(start, f - start).Trim();
                    at = Math.Min(s.Length, f + 2 + LegFields[k].Length + 1);
                }
                start = at; prevField = k; pos = at;
            }
            if (prevField >= 0 && start <= s.Length) v[prevField] = s.Substring(start).Trim();

            Leg g = new Leg();
            if (string.IsNullOrEmpty(v[1])) return null;
            key = GuidForm.Key(v[1]);
            g.Setup = IosTime(v[2]);
            g.Connect = IosTime(v[7]);
            g.Disconnect = IosTime(v[8]);
            if (g.Disconnect < 0) return null;
            if (g.Setup < 0) g.Setup = g.Disconnect;
            if (g.Connect < 0) g.Connect = g.Disconnect;
            g.Peer = v[3] ?? "";
            g.LegType = (byte)Int(v[0]);
            g.Origin = (byte)Int(v[9]);
            g.CauseText = v[6] ?? "";
            int open = g.CauseText.LastIndexOf('(');
            if (open >= 0 && g.CauseText.EndsWith(")", StringComparison.Ordinal))
            {
                int dec;
                if (int.TryParse(g.CauseText.Substring(open + 1, g.CauseText.Length - open - 2), NumberStyles.Integer,
                                 CultureInfo.InvariantCulture, out dec)) g.TextCause = (short)dec;
                g.CauseText = g.CauseText.Substring(0, open).Trim();
            }
            int hex;
            if (!string.IsNullOrEmpty(v[5]) && int.TryParse(v[5], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hex))
                g.Cause = (short)hex;
            else g.Cause = g.TextCause >= 0 ? g.TextCause : (short)0;
            g.TxPackets = Int(v[12]);
            g.TxBytes = Leg.Bytes(Long(v[13]));
            g.RxPackets = Int(v[14]);
            g.RxBytes = Leg.Bytes(Long(v[15]));
            return g;
        }

        /// <summary>"14:59:43.015 EDT Mon Sep 28 2026" (zone and weekday optional) to ms; -1 if unreadable.</summary>
        public static long IosTime(string t)
        {
            if (string.IsNullOrEmpty(t)) return -1;
            string[] p = t.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 4) return -1;
            string clock = p[0];
            if (clock.Length > 0 && (clock[0] == '*' || clock[0] == '.')) clock = clock.Substring(1);
            if (clock.Length < 8 || clock[2] != ':' || clock[5] != ':') return -1;
            int hh, mi, ss, ms = 0;
            if (!TwoDigits(clock, 0, out hh) || !TwoDigits(clock, 3, out mi) || !TwoDigits(clock, 6, out ss)) return -1;
            if (clock.Length > 9 && clock[8] == '.')
            {
                int q = 0, m = 0;
                for (int i = 9; i < clock.Length && q < 3; i++)
                {
                    char c = clock[i];
                    if (c < '0' || c > '9') break;
                    m = m * 10 + (c - '0'); q++;
                }
                while (q < 3) { m *= 10; q++; }
                ms = m;
            }
            int mon = TimeUtil.MonthOf(p[p.Length - 3], 0);
            int day, year;
            if (mon == 0 || !int.TryParse(p[p.Length - 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out day) ||
                !int.TryParse(p[p.Length - 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out year)) return -1;
            return TimeUtil.Ms(year, mon, day, hh, mi, ss, ms);
        }

        static bool TwoDigits(string s, int at, out int v)
        {
            v = 0;
            char a = s[at], b = s[at + 1];
            if (a < '0' || a > '9' || b < '0' || b > '9') return false;
            v = (a - '0') * 10 + (b - '0');
            return true;
        }

        static int Int(string s)
        {
            int v;
            if (string.IsNullOrEmpty(s) || !int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return 0;
            return v;
        }

        static long Long(string s)
        {
            long v;
            if (string.IsNullOrEmpty(s) || !long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return 0;
            return v;
        }

        // ------------------------------------------------------------ VOIP_FEAT_HISTORY

        /// <summary>
        /// FEAT_VSA=fn:TWC,ft:09/28/2026 14:57:02.634,cgn:...,legID:6C72D3,bguid:... - every pair is
        /// split on its FIRST colon, because values such as Xor:&lt;sip:13301@10.0.0.1&gt; hold more.
        /// </summary>
        public static Feature ParseFeature(string s, int body)
        {
            int k = s.IndexOf("FEAT_VSA=", body, StringComparison.Ordinal);
            if (k < 0) return null;
            string[] parts = s.Substring(k + 9).TrimEnd().Split(',');
            Feature f = new Feature();
            List<string> kv = new List<string>(parts.Length * 2);
            string fcid = null;
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                int c = part.IndexOf(':');
                if (c <= 0) continue;
                string key = part.Substring(0, c), val = part.Substring(c + 1);
                switch (key)
                {
                    case "fn": f.Fn = val; break;
                    case "ft": f.Ft = FeatTime(val); break;
                    case "cgn": f.Cgn = val; break;
                    case "cdn": f.Cdn = val; break;
                    case "fid": long fid; if (long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out fid)) f.Fid = fid; break;
                    case "fcid": fcid = val; break;
                    case "legID": int lid; if (int.TryParse(val, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out lid)) f.LegId = lid; break;
                    case "bguid": f.Padded = val.Trim().ToUpperInvariant(); break;
                    default: kv.Add(key); kv.Add(val); break;
                }
            }
            if (string.IsNullOrEmpty(f.Fn)) return null;
            if (!string.IsNullOrEmpty(f.Padded)) f.Key = GuidForm.Key(f.Padded);
            else if (!string.IsNullOrEmpty(fcid)) f.Key = GuidForm.Key(fcid);
            else return null;
            if (f.Fn != "TWC") f.Kv = kv.ToArray();
            return f;
        }

        /// <summary>"09/28/2026 14:57:02.634" to ms.</summary>
        public static long FeatTime(string t)
        {
            if (t == null || t.Length < 19 || t[2] != '/' || t[5] != '/' || t[10] != ' ') return -1;
            int mo, d, hh, mi, ss, ms = 0, y;
            if (!TwoDigits(t, 0, out mo) || !TwoDigits(t, 3, out d) || !TwoDigits(t, 11, out hh) ||
                !TwoDigits(t, 14, out mi) || !TwoDigits(t, 17, out ss)) return -1;
            if (!int.TryParse(t.Substring(6, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out y)) return -1;
            if (t.Length > 20 && t[19] == '.')
            {
                int q = 0, m = 0;
                for (int i = 20; i < t.Length && q < 3; i++) { char c = t[i]; if (c < '0' || c > '9') break; m = m * 10 + (c - '0'); q++; }
                while (q < 3) { m *= 10; q++; }
                ms = m;
            }
            return TimeUtil.Ms(y, mo, d, hh, mi, ss, ms);
        }

        // ------------------------------------------------------------ %VOICE_IEC-3-GW

        /// <summary>"C SCRIPTS: Internal Error (No dialpeer match): IEC=1.1.128.11.5.0 on callID 7027088 GUID=..."</summary>
        public static IecError ParseIec(string s, int body)
        {
            IecError e = new IecError();
            string b = s.Substring(body);
            int c = b.IndexOf(": ", StringComparison.Ordinal);
            e.Sub = c > 0 ? b.Substring(0, c) : "";
            int ie = b.IndexOf("Internal Error (", StringComparison.Ordinal);
            int code = b.IndexOf("IEC=", StringComparison.Ordinal);
            if (ie >= 0 && code > ie)
            {
                int close = b.LastIndexOf(")", code, StringComparison.Ordinal);
                e.Text = close > ie ? b.Substring(ie + 16, close - ie - 16) : b.Substring(ie + 16);
            }
            else e.Text = c > 0 ? b.Substring(c + 2) : b;
            if (code >= 0) e.Code = Word(b, code + 4);
            int cid = b.IndexOf("callID ", StringComparison.Ordinal);
            if (cid >= 0) e.CallId = Word(b, cid + 7);
            int g = b.IndexOf("GUID=", StringComparison.Ordinal);
            if (g >= 0)
            {
                e.Padded = Word(b, g + 5).ToUpperInvariant();
                if (GuidForm.IsHex(e.Padded)) e.Key = GuidForm.Key(e.Padded); else e.Padded = null;
            }
            return e;
        }

        static string Word(string s, int at)
        {
            int e = at;
            while (e < s.Length && s[e] != ' ' && s[e] != ',') e++;
            return s.Substring(at, e - at);
        }

        // ------------------------------------------------------------ Cover Buffer dumps

        /// <summary>A //callID/guid/... debug line. Inside a dump of the same call it is dump text;
        /// otherwise it is remembered, since a Cover Buffer block is introduced by one.</summary>
        void Trace(string s, Header h, int lineNo)
        {
            int a = h.Msg + 2;
            int b = s.IndexOf('/', a);
            string callId = b > a ? s.Substring(a, b - a) : "";
            string guid = "";
            if (b > 0)
            {
                int c = s.IndexOf('/', b + 1);
                guid = c > b ? s.Substring(b + 1, c - b - 1) : s.Substring(b + 1);
            }
            if (_dump != null && _dumpPhase == 2 && callId == _dump.CallId && guid == _dump.ShortGuid)
            {
                AppendDump(s.Substring(h.Msg));
                return;
            }
            if (_dump != null) CloseDump();
            _lastTraceText = s.Substring(h.Msg); _lastTraceLine = lineNo;
            _lastTraceCall = callId; _lastTraceGuid = guid;
            _out.Skipped++;
        }

        void StartDump(int lineNo)
        {
            CloseDump();
            _dump = new TraceDump();
            _dump.File = FileIndex; _dump.Line = lineNo;
            _dump.CallId = _lastTraceCall; _dump.ShortGuid = _lastTraceGuid;
            _dumpText = new StringBuilder(4096);
            if (_lastTraceText != null && _lastTraceLine == lineNo - 1)
            {
                _out.Skipped--;                       // counted as unused when it arrived; it is the dump's first line
                _dumpText.Append(_lastTraceText).Append('\n');
                _dump.Line = _lastTraceLine;
                _dump.LineCount++;
            }
            _dumpText.Append("------------------ Cover Buffer ---------------\n");
            _dump.LineCount++;
            _dumpPhase = 1;
        }

        void AppendDump(string text)
        {
            _dump.LineCount++;
            if (_dumpPhase == 1)
            {
                string t = text.Trim();
                if (t.Length > 10 && t.Trim('-').Length == 0) { _dumpPhase = 2; }
                else
                {
                    int eq = t.IndexOf('=');
                    if (eq > 0)
                    {
                        string k = t.Substring(0, eq).Trim(), v = t.Substring(eq + 1).Trim();
                        switch (k)
                        {
                            case "Timestamp": _dump.Ts = DumpTime(v); break;
                            case "CallID": _dump.CallId = v; break;
                            case "Called-Number": _dump.Called = v; break;
                            case "Calling-Number": _dump.Calling = v; break;
                            case "SIP CallID": _dump.SipCallId = v; break;
                            case "GUID": if (v.Length > 0) _dump.ShortGuid = v.ToUpperInvariant(); break;
                            case "Cause-code":
                                _dump.CauseText = v;
                                int o = v.LastIndexOf('('), cl = v.LastIndexOf(')');
                                int cc;
                                if (o >= 0 && cl > o && int.TryParse(v.Substring(o + 1, cl - o - 1), NumberStyles.Integer,
                                                                      CultureInfo.InvariantCulture, out cc)) _dump.CauseCode = cc;
                                break;
                        }
                    }
                }
            }
            if (_dump.LineCount > DumpMaxLines || _dumpText.Length > DumpMaxChars) { _dump.Truncated = true; return; }
            _dumpText.Append(text).Append('\n');
        }

        /// <summary>The dump's Timestamp is UTC ("Sep 28 14:19:25.788"); the stream's zone converts it.</summary>
        /// <summary>A Cover Buffer time is stamped in UTC. The zone a CUBE prints converts it; a CUBE that
        /// prints none is settled in CloseDump, by the syslog message nearest it.</summary>
        long DumpTime(string v)
        {
            Header h = Header.Parse(v + ": x");
            long utc = HeaderMs(h);
            if (utc < 0) return -1;
            double off = TimeUtil.TzOffsetHours(Tz);
            if (double.IsNaN(off)) { _dump.TsIsUtc = true; return utc; }
            return utc + (long)(off * 3600000);
        }

        /// <summary>A CUBE's offset from UTC, from how far a syslog message (its own clock) is from a UTC
        /// time stamped moments apart: a whole hour if one is within ten minutes, else the nearest quarter.</summary>
        static long ZoneOf(long diff)
        {
            const long Hour = 3600000, Quarter = 900000;
            long whole = (long)Math.Round(diff / (double)Hour) * Hour;
            if (Math.Abs(diff - whole) <= 600000) return whole;
            return (long)Math.Round(diff / (double)Quarter) * Quarter;
        }

        void CloseDump()
        {
            if (_dump == null) return;
            // left in UTC it would sit hours away from its own call, and could be the CUBE's newest record
            if (_dump.TsIsUtc && _logMs >= 0)
            {
                _dump.Ts += ZoneOf(_logMs - _dump.Ts);
                _dump.TsIsUtc = false;
            }
            _dump.Text = _dumpText.ToString();
            if (_dump.ShortGuid != null) _dump.ShortGuid = _dump.ShortGuid.ToUpperInvariant();
            _out.Records.Add(_dump);
            _dump = null; _dumpText = null; _dumpPhase = 0;
            _lastTraceText = null;
        }

        // ------------------------------------------------------------ whole files

        /// <summary>Reads a closed file start to end.</summary>
        public static ParsedChunk ParseFile(string path, int fileIndex, int refYear, int refMonth)
        {
            return ParseFile(path, fileIndex, refYear, refMonth, false);
        }

        /// <summary>
        /// Reads a file. completeLinesOnly is for a file the receiver may still be writing: a last line
        /// with no line ending yet is left for the tailer, rather than read cut short (a TWC record cut
        /// mid-number would bind with the wrong number), and EndPos points at its first byte.
        /// </summary>
        public static ParsedChunk ParseFile(string path, int fileIndex, int refYear, int refMonth, bool completeLinesOnly)
        {
            LineParser p = new LineParser(fileIndex, refYear, refMonth);
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
            {
                long limit = completeLinesOnly ? CompleteLength(fs) : long.MaxValue;
                fs.Position = 0;
                using (StreamReader r = new StreamReader(new LimitedStream(fs, limit), Latin1, false, 1 << 16))
                {
                    string line;
                    int n = 0;
                    while ((line = r.ReadLine()) != null) p.Feed(line, ++n);
                    p.Flush();
                    ParsedChunk c = p.Take();
                    c.EndPos = fs.Position;
                    c.EndLine = n;
                    return c;
                }
            }
        }

        /// <summary>Length up to and including the last line ending.</summary>
        static long CompleteLength(FileStream fs)
        {
            long len = fs.Length;
            byte[] buf = new byte[65536];
            long end = len;
            while (end > 0)
            {
                int take = (int)Math.Min(buf.Length, end);
                fs.Position = end - take;
                int got = 0;
                while (got < take) { int k = fs.Read(buf, got, take - got); if (k <= 0) break; got += k; }
                for (int i = got - 1; i >= 0; i--)
                    if (buf[i] == (byte)'\n') return end - take + i + 1;
                end -= take;
            }
            return 0;
        }

        /// <summary>A read-only view of the first `limit` bytes of a stream.</summary>
        sealed class LimitedStream : Stream
        {
            readonly Stream _s;
            readonly long _limit;
            public LimitedStream(Stream s, long limit) { _s = s; _limit = limit; }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return Math.Min(_limit, _s.Length); } }
            public override long Position { get { return _s.Position; } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                long left = _limit - _s.Position;
                if (left <= 0) return 0;
                if (count > left) count = (int)left;
                return _s.Read(buffer, offset, count);
            }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }

        /// <summary>Latin-1 decodes any byte, so a stray non-ASCII byte cannot stop a read.</summary>
        public static readonly Encoding Latin1 = Encoding.GetEncoding(28591);
    }
}
