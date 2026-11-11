using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CubeScope
{
    // ---------------------------------------------------------------- call-level verdicts

    /// <summary>How a call ended, one per call. Decided from the OUTBOUND leg's cause when there is
    /// one: on a refused call the inbound leg only carries the CUBE's generic 47 (DESIGN.md 1.11).</summary>
    public enum Result : byte { Unknown = 0, Answered = 1, Abandoned = 2, NoAnswer = 3, Busy = 4, Rejected = 5, Failed = 6 }

    /// <summary>Which far end started the teardown. Caller = the far end of an inbound leg,
    /// Called = the far end of an outbound leg, Network = the CUBE or a carrier failed it.</summary>
    public enum Released : byte { Unknown = 0, Caller = 1, Called = 2, Network = 3, Tie = 4 }

    /// <summary>Worst media seen on the call's answered, talking (> 5 s), non-signalling legs.</summary>
    public enum Media : byte { Unknown = 0, TwoWay = 1, LowRate = 2, OneWay = 3, None = 4 }

    public static class Names
    {
        public static readonly string[] Results = { "unknown", "answered", "abandoned", "noanswer", "busy", "rejected", "failed" };
        public static readonly string[] ResultLabels = { "Unknown", "Answered", "Canceled while ringing", "No answer",
                                                         "Busy", "Rejected", "Failed" };
        public static readonly string[] Releases = { "unknown", "caller", "called", "network", "tie" };
        public static readonly string[] Medias = { "unknown", "twoway", "lowrate", "oneway", "none" };
        public static readonly string[] MediaLabels = { "-", "Two-way", "Low packet rate", "One-way", "No media" };

        public static int IndexOf(string[] list, string v)
        {
            for (int i = 0; i < list.Length; i++)
                if (string.Equals(list[i], v, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
    }

    // ---------------------------------------------------------------- time

    /// <summary>
    /// Every record time is the CUBE's own wall clock. It is kept as milliseconds since 1970 "as if
    /// UTC" and never converted, so a dashboard in another zone still shows the CUBE's clock. The
    /// one exception is the Cover Buffer dump, which IOS stamps in UTC: the parser puts it on the
    /// CUBE's clock by the printed zone, or by the nearest syslog message (LineParser.CloseDump).
    /// </summary>
    public static class TimeUtil
    {
        public static readonly long EpochTicks = new DateTime(1970, 1, 1).Ticks;

        static readonly string[] Months = { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };

        /// <summary>1-12 for a three-letter English month, 0 otherwise.</summary>
        public static int MonthOf(string s, int at)
        {
            if (at + 3 > s.Length) return 0;
            char a = char.ToLowerInvariant(s[at]), b = char.ToLowerInvariant(s[at + 1]), c = char.ToLowerInvariant(s[at + 2]);
            for (int i = 0; i < 12; i++)
                if (Months[i][0] == a && Months[i][1] == b && Months[i][2] == c) return i + 1;
            return 0;
        }

        /// <summary>Milliseconds for a wall-clock time; -1 when any part is out of range.</summary>
        public static long Ms(int y, int mo, int d, int h, int mi, int s, int ms)
        {
            if (y < 1971 || y > 9998 || mo < 1 || mo > 12 || d < 1 || h < 0 || h > 23 || mi < 0 || mi > 59 ||
                s < 0 || s > 60 || ms < 0 || ms > 999) return -1;
            if (d > DateTime.DaysInMonth(y, mo)) return -1;
            if (s == 60) s = 59;                                    // leap second: fold
            return (new DateTime(y, mo, d, h, mi, s, ms).Ticks - EpochTicks) / 10000;
        }

        public static DateTime ToDate(long ms) { return new DateTime(EpochTicks + ms * 10000); }

        public static long FromDate(DateTime d) { return (d.Ticks - EpochTicks) / 10000; }

        public static string Fmt(long ms)
        {
            if (ms < 0) return "";
            return ToDate(ms).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }

        public static string FmtShort(long ms)
        {
            if (ms < 0) return "";
            return ToDate(ms).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        public static string FmtDur(long ms)
        {
            if (ms < 0) return "";
            if (ms < 1000) return ms + " ms";
            double s = ms / 1000.0;
            if (s < 60) return s.ToString("0.0", CultureInfo.InvariantCulture) + " s";
            long t = (long)Math.Round(s);
            if (t < 3600) return (t / 60) + ":" + (t % 60).ToString("00");
            return (t / 3600) + ":" + ((t / 60) % 60).ToString("00") + ":" + (t % 60).ToString("00");
        }

        /// <summary>
        /// Hours to add to UTC for the zone abbreviations IOS prints with show-timezone. NaN when the
        /// abbreviation is unknown, in which case nothing is converted and the dump keeps its UTC stamp.
        /// </summary>
        public static double TzOffsetHours(string tz)
        {
            if (string.IsNullOrEmpty(tz)) return double.NaN;
            switch (tz.ToUpperInvariant())
            {
                case "UTC": case "GMT": case "Z": case "WET": return 0;
                case "EDT": return -4; case "EST": return -5;
                case "CDT": return -5; case "CST": return -6;
                case "MDT": return -6; case "MST": return -7;
                case "PDT": return -7; case "PST": return -8;
                case "AKDT": return -8; case "AKST": return -9;
                case "HST": return -10;
                case "ADT": return -3; case "AST": return -4;
                case "NDT": return -2.5; case "NST": return -3.5;
                case "BST": case "CET": case "WEST": return 1;
                case "CEST": case "EET": return 2;
                case "EEST": return 3;
                case "IST": return 5.5;
                case "SGT": case "AWST": return 8;
                case "JST": case "KST": return 9;
                case "AEST": return 10; case "AEDT": return 11;
                case "NZST": return 12; case "NZDT": return 13;
                default: return double.NaN;
            }
        }
    }

    // ---------------------------------------------------------------- GUID spellings

    /// <summary>
    /// The same 128-bit call GUID is printed four ways (DESIGN.md 1.6). ConnectionId and fcid drop
    /// leading zeros per 32-bit word; bguid and the IEC line are padded; trace dumps use 12 hex
    /// chars. The call key is the unpadded form, which cannot be split back into words.
    /// </summary>
    /// <summary>
    /// Hides passwords that log messages carry in a URL. A CUBE's file accounting prints its SFTP or FTP login whole in
    /// %VOICE_FILE_ACCT-3-DUMPFAIL ("write to sftp://user:password@server"), and CubeScope shows those messages.
    /// </summary>
    public static class Redact
    {
        // user, then everything up to the last @ before the path: a password may itself hold an @
        static readonly Regex UrlLogin = new Regex(@"\b([A-Za-z][A-Za-z0-9+.-]*://[^:/@\s]+:)[^\s/]+@", RegexOptions.CultureInvariant);

        public static string Secrets(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf("://", StringComparison.Ordinal) < 0) return text;
            return UrlLogin.Replace(text, "$1****@");
        }
    }

    public static class GuidForm
    {
        /// <summary>Upper-case; a full 32-char padded form loses each word's leading zeros.</summary>
        public static string Key(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Trim().ToUpperInvariant();
            if (s.IndexOf(' ') >= 0) s = s.Replace(" ", "");       // a call record file writes the four words apart
            if (s.Length != 32) return s;
            StringBuilder sb = null;
            for (int w = 0; w < 4; w++)
            {
                int a = w * 8, e = a + 8;
                int i = a;
                while (i < e - 1 && s[i] == '0') i++;
                if (i != a && sb == null) { sb = new StringBuilder(32); sb.Append(s, 0, a); }
                if (sb != null) sb.Append(s, i, e - i);
            }
            return sb == null ? s : sb.ToString();
        }

        /// <summary>The 12-char form trace dumps use: padded[0..8] + padded[16..20].</summary>
        public static string Short(string padded)
        {
            if (padded == null || padded.Length != 32) return null;
            return padded.Substring(0, 8) + padded.Substring(16, 4);
        }

        /// <summary>The short form as a number (12 hex digits fit in a long); -1 when not a padded GUID.</summary>
        public static long ShortKey(string padded)
        {
            if (padded == null || padded.Length != 32) return -1;
            long a = HexNum(padded, 0, 8), b = HexNum(padded, 16, 4);
            return a < 0 || b < 0 ? -1 : (a << 16) | b;
        }

        /// <summary>A 12-char short form (as a trace dump prints it) as a number; -1 when it is not one.</summary>
        public static long ShortKeyOf(string shortForm)
        {
            if (shortForm == null || shortForm.Length != 12) return -1;
            return HexNum(shortForm, 0, 12);
        }

        static long HexNum(string s, int at, int count)
        {
            long v = 0;
            for (int i = at; i < at + count; i++)
            {
                char c = s[i];
                int d = c >= '0' && c <= '9' ? c - '0' : c >= 'A' && c <= 'F' ? c - 'A' + 10 : c >= 'a' && c <= 'f' ? c - 'a' + 10 : -1;
                if (d < 0) return -1;
                v = (v << 4) + d;
            }
            return v;
        }

        public static bool IsHex(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }
    }

    // ---------------------------------------------------------------- records

    /// <summary>
    /// One VOIP_CALL_HISTORY record: one leg of a call, written when the leg disconnected. There are
    /// millions of these, so the fields are kept small: the call key travels beside the leg from the
    /// parser to the store (ParsedChunk.LegKeys), the store knows each leg's call, and the TWC
    /// record's file is kept as an offset from the leg's own.
    /// </summary>
    public sealed class Leg
    {
        public long Setup, Connect, Disconnect;      // ms, CUBE wall clock (see TimeUtil)
        /// <summary>
        /// Start of the stretch whose packets the CUBE counted; -1 = from the answer. On a dialer call
        /// the customer leg counts nothing while it is bridged to the signalling-only dialer leg
        /// (answer detection), only after the transfer - so its media is judged from the transfer.
        /// </summary>
        public long MediaFrom = -1;
        public string Peer, Cgn, Cdn, CauseText;
        public uint TxBytes, RxBytes;                // capped at 4 GB, far beyond any one leg
        public int TxPackets, RxPackets;
        public int TwcFid = -1;                      // the TWC record's fid (low 32 bits), to recognise a repeat of it
        public int TwcLine;                          // where the TWC record was (with TwcFile)
        public int LegId = -1;                       // TWC legID (hex in the log)
        public int File, Line;                       // provenance: source file index, 1-based line
        public int Tags;                             // rule tag bits (Rules)
        public short Cause;                          // decimal Q.850
        public short TextCause = -1;                 // the decimal in DisconnectText's "(n)", -1 when absent
        short _twcFile = NoTwc;                      // TWC record's file, as an offset from File
        public byte Origin;                          // 2 = the CUBE answered this leg, 1 = the CUBE placed it
        public byte LegType;                         // CallLegType: 1 telephony, 2 VoIP
        public bool Bound;                           // its TWC record was found
        public bool FeatureOnly;                     // made from a TWC record that had no history line
        public bool SignalOnly;                      // a rule says this leg never carries media (the dialer)
        public bool FromFile;                        // first read from a call record file, not syslog
        public bool InBoth;                          // the CUBE's other feed reported this same leg too
        public bool Measured;                        // the CUBE sent media statistics for it (gw-accounting syslog stats), even all zero

        const short NoTwc = short.MinValue;

        /// <summary>The file the TWC record was in (it can be the next one); -1 when there was none. Set File first.</summary>
        public int TwcFile
        {
            get { return _twcFile == NoTwc ? -1 : File + _twcFile; }
            set
            {
                int d = value - File;
                _twcFile = value < 0 || d <= short.MinValue || d > short.MaxValue ? NoTwc : (short)d;
            }
        }

        /// <summary>A byte count from the log, held in 32 bits.</summary>
        public static uint Bytes(long v) { return v <= 0 ? 0u : v >= uint.MaxValue ? uint.MaxValue : (uint)v; }

        public bool In { get { return Origin == 2; } }
        public bool Out { get { return Origin == 1; } }
        /// <summary>Unanswered legs carry ConnectTime == DisconnectTime (DESIGN.md 1.4).</summary>
        public bool Answered { get { return !FeatureOnly && Connect >= 0 && Connect < Disconnect; } }
        public long TalkMs { get { return Answered ? Disconnect - Connect : 0; } }
        public long RingMs { get { return Answered ? Connect - Setup : -1; } }
        /// <summary>How long the CUBE was counting this leg's packets.</summary>
        public long MediaMs { get { return Answered ? Disconnect - Math.Max(Connect, MediaFrom) : 0; } }

        public Media MediaClass
        {
            get
            {
                if (!Answered || SignalOnly || FeatureOnly) return Media.Unknown;
                long win = MediaMs;
                if (win <= 5000) return Media.Unknown;
                if (TxPackets == 0 && RxPackets == 0) return Media.None;
                if ((TxPackets == 0 && RxPackets > 100) || (RxPackets == 0 && TxPackets > 100)) return Media.OneWay;
                if (TxPackets == 0 || RxPackets == 0) return Media.None;
                double sec = win / 1000.0;
                // 20 ms packetisation is 50 a second; under half of that on either side is patchy,
                // late or held media (hold without music sends nothing)
                if (TxPackets / sec < 25 || RxPackets / sec < 25) return Media.LowRate;
                return Media.TwoWay;
            }
        }

        /// <summary>Bytes per packet on the busier side, the codec's fingerprint; 0 when no media.</summary>
        public int BytesPerPacket
        {
            get
            {
                if (TxPackets >= RxPackets && TxPackets > 0) return (int)Math.Round((double)TxBytes / TxPackets);
                if (RxPackets > 0) return (int)Math.Round((double)RxBytes / RxPackets);
                return 0;
            }
        }

        public string Number { get { return !string.IsNullOrEmpty(Peer) ? Peer : (In ? Cgn : Cdn); } }

        public bool SameAs(Leg o)
        {
            return o.Setup == Setup && o.Disconnect == Disconnect && o.Connect == Connect && o.Origin == Origin &&
                   o.TxPackets == TxPackets && o.RxPackets == RxPackets && string.Equals(o.Peer, Peer);
        }

        /// <summary>Syslog and call record files can give the same moment a few milliseconds apart (10 at most on a real CUBE).</summary>
        public const long OtherFeedSlackMs = 50;

        /// <summary>The same leg as the CUBE's other feed reported it: times close, everything else exact.</summary>
        public bool SameAsOtherFeed(Leg o)
        {
            return o.Origin == Origin && o.TxPackets == TxPackets && o.RxPackets == RxPackets && string.Equals(o.Peer, Peer) &&
                   Math.Abs(o.Setup - Setup) <= OtherFeedSlackMs && Math.Abs(o.Connect - Connect) <= OtherFeedSlackMs && Math.Abs(o.Disconnect - Disconnect) <= OtherFeedSlackMs;
        }
    }

    /// <summary>
    /// One VOIP_FEAT_HISTORY record as the parser reads it, every key and value included. A TWC record
    /// is folded into its leg; any other becomes a CallFeature.
    /// </summary>
    public sealed class Feature
    {
        public string Key;          // call key while in transit
        public string Padded;       // bguid
        public string Fn;           // TWC, BXFER, CFA, ...
        public long Ft = -1;
        public long Fid = -1;
        public int LegId = -1;
        public int File, Line;
        public string Cgn, Cdn;     // TWC only
        public string[] Kv;         // every key and value, alternating, in log order (non-TWC only)

        public string Get(string k)
        {
            if (Kv == null) return null;
            for (int i = 0; i + 1 < Kv.Length; i += 2)
                if (string.Equals(Kv[i], k, StringComparison.OrdinalIgnoreCase)) return Kv[i + 1];
            return null;
        }
    }

    /// <summary>
    /// A feature record other than TWC (BXFER, CFA, ...) as its call keeps it: only what the derivation
    /// needs. Its other keys and values are read back from the log line when the call is opened, so
    /// the store does not hold a dozen strings for every transfer (Query.CallLines).
    /// </summary>
    public sealed class CallFeature
    {
        public string Fn;           // BXFER, CFA, ...
        public string Xto;          // the transfer target, which groups the records of one transfer
        public long Ft = -1;
        public long Fid = -1;
        public int LegId = -1;
        public int File, Line;
    }

    /// <summary>%VOICE_IEC-3-GW: an internal error code for one call, with its full GUID.</summary>
    public sealed class IecError
    {
        public string Key, Padded;
        public long Ts;
        public string Sub;          // "C SCRIPTS", "SIP"
        public string Text;         // "No dialpeer match"
        public string Code;         // 1.1.128.11.5.0
        public string CallId;
        public int File, Line;
    }

    /// <summary>
    /// The CUBE's call-failure trace ("Cover Buffer") for one call: a key = value header followed by
    /// the debug lines it had buffered, typically the SIP INVITE. The only record of a rejected call.
    /// </summary>
    public sealed class TraceDump
    {
        public long Ts = -1;        // the Timestamp field converted to the CUBE's local clock
        public bool TsIsUtc;        // nothing to convert with: no zone printed, no syslog message read
        public string CallId, ShortGuid, Called, Calling, SipCallId, CauseText;
        public int CauseCode = -1;
        public string Text;
        public int LineCount;
        public bool Truncated;
        public int File, Line;
    }

    /// <summary>Any other syslog message worth a mark on the events lane.</summary>
    public sealed class SysEvent
    {
        public long Ts;
        public string Kind;         // tls-fail, tls, sip-state, licence, login, config, iec, dump, other
        public string Mnemonic;     // SIP-2-TLS_HANDSHAKE_FAILED
        public string Text;
        public double Value = double.NaN;   // licence count, when the message carries one
        public int File, Line;
    }

    /// <summary>
    /// A call's legs in an array sized to fit. A List kept a 40-byte list object and spare room on
    /// every call; most calls have two legs and never change after they are read. Being a struct, it
    /// lives inside its Call: a copy reads the same legs, but a leg added to a copy is lost.
    /// </summary>
    public struct LegList
    {
        Leg[] _a;

        public int Count { get { return _a == null ? 0 : _a.Length; } }

        public Leg this[int i]
        {
            get
            {
                if (_a == null || (uint)i >= (uint)_a.Length) throw new ArgumentOutOfRangeException("i");
                return _a[i];
            }
        }

        public void Add(Leg g)
        {
            int n = Count;
            Leg[] b = new Leg[n + 1];
            if (n > 0) Array.Copy(_a, b, n);
            b[n] = g;
            _a = b;
        }

        public int IndexOf(Leg g)
        {
            for (int i = 0; i < Count; i++) if (ReferenceEquals(_a[i], g)) return i;
            return -1;
        }

        public void Clear() { _a = null; }

        public List<Leg> ToList() { return _a == null ? new List<Leg>() : new List<Leg>(_a); }

        public Enumerator GetEnumerator() { return new Enumerator(_a); }

        public struct Enumerator
        {
            readonly Leg[] _a;
            int _i;
            public Enumerator(Leg[] a) { _a = a; _i = -1; }
            public Leg Current { get { return _a[_i]; } }
            public bool MoveNext() { return _a != null && ++_i < _a.Length; }
        }
    }

    /// <summary>
    /// Every leg, feature, error and dump the CUBE logged under one GUID. Derived fields are filled
    /// by Store.Derive and recomputed whenever a record is added.
    /// </summary>
    public sealed class Call
    {
        public int Id;              // position in Store.Calls: unique, unlike the GUID
        public int Src;
        public string Key;
        public string Padded;
        /// <summary>A struct: add through the field (c.Legs.Add), never through a copy.</summary>
        public LegList Legs;
        public List<CallFeature> Features;
        public List<IecError> Errors;
        public List<TraceDump> Dumps;
        public bool Dirty;

        // derived
        public long Start = -1, End = -1, Answer = -1, A2tMs = -1, LastSeen = -1;
        public string Calling, Called, Dialed, ConnectedTo;
        public Result Result;
        public Released Released;
        public int ReleaseLeg = -1;
        public short FinalCause = -1;
        public Media Media;
        public int Bpp;
        public int Transfers;
        public int Tags;
        public bool Dialer;          // the first inbound leg is signalling-only (the dialer)

        public long DurMs { get { return End >= Start && Start >= 0 ? End - Start : 0; } }

        /// <summary>The outbound legs sent to a number, as one outcome: the last one tried (null when
        /// none went there) and whether any of them was answered.</summary>
        public Leg LegTo(Func<string, bool> isNumber, out bool answered)
        {
            Leg last = null;
            answered = false;
            for (int i = 0; i < Legs.Count; i++)
            {
                Leg g = Legs[i];
                if (g.FeatureOnly || !g.Out || !(isNumber(g.Peer) || isNumber(g.Cdn))) continue;
                if (g.Answered) answered = true;
                if (last == null || g.Setup >= last.Setup) last = g;
            }
            return last;
        }
        public long TalkMs { get { return Answer >= 0 && End > Answer ? End - Answer : 0; } }
        public long RingMs { get { return Answer >= 0 ? Answer - Start : -1; } }
    }

    // ---------------------------------------------------------------- Q.850

    public static class Q850
    {
        public static string Name(int c)
        {
            switch (c)
            {
                case 0: return "No cause given";
                case 1: return "Unallocated number";
                case 2: return "No route to network";
                case 3: return "No route to destination";
                case 6: return "Channel unacceptable";
                case 16: return "Normal call clearing";
                case 17: return "User busy";
                case 18: return "No user responding";
                case 19: return "No answer (alerted)";
                case 20: return "Subscriber absent";
                case 21: return "Call rejected";
                case 22: return "Number changed";
                case 27: return "Destination out of order";
                case 28: return "Invalid number format";
                case 29: return "Facility rejected";
                case 31: return "Normal, unspecified";
                case 34: return "No circuit available";
                case 38: return "Network out of order";
                case 41: return "Temporary failure";
                case 42: return "Switching congestion";
                case 44: return "Channel not available";
                case 47: return "Resource unavailable";
                case 50: return "Facility not subscribed";
                case 57: return "Bearer not authorized";
                case 58: return "Bearer not available";
                case 63: return "Service unavailable";
                case 65: return "Bearer not implemented";
                case 79: return "Service not implemented";
                case 86: return "Call cleared";
                case 88: return "Incompatible destination";
                case 95: return "Invalid message";
                case 100: return "Invalid IE contents";
                case 102: return "Recovery on timer expiry";
                case 111: return "Protocol error";
                case 127: return "Interworking, unspecified";
                default: return "Cause " + c;
            }
        }

        /// <summary>Causes that mean the CUBE or the network failed the call rather than a person.</summary>
        public static bool IsNetwork(int c)
        {
            switch (c)
            {
                case 0: case 2: case 3: case 27: case 34: case 38: case 41: case 42: case 44: case 47:
                case 63: case 86: case 95: case 100: case 102: case 111: case 127:
                    return true;
                default: return false;
            }
        }
    }

    // ---------------------------------------------------------------- json

    /// <summary>Minimal allocation-friendly JSON writer (no external dependencies).</summary>
    public sealed class Json
    {
        readonly StringBuilder _sb;
        bool _needComma;

        public Json() { _sb = new StringBuilder(1 << 16); }
        public Json(int capacity) { _sb = new StringBuilder(capacity); }

        public Json Obj() { Sep(); _sb.Append('{'); _needComma = false; return this; }
        public Json EndObj() { _sb.Append('}'); _needComma = true; return this; }
        public Json Arr() { Sep(); _sb.Append('['); _needComma = false; return this; }
        public Json EndArr() { _sb.Append(']'); _needComma = true; return this; }

        public Json Key(string k)
        {
            Sep();
            Str(k);
            _sb.Append(':');
            _needComma = false;
            return this;
        }

        void Sep() { if (_needComma) _sb.Append(','); }

        public Json Val(string s) { Sep(); Str(s); _needComma = true; return this; }
        public Json Val(bool b) { Sep(); _sb.Append(b ? "true" : "false"); _needComma = true; return this; }
        public Json Val(int i) { Sep(); _sb.Append(i.ToString(CultureInfo.InvariantCulture)); _needComma = true; return this; }
        public Json Val(long i) { Sep(); _sb.Append(i.ToString(CultureInfo.InvariantCulture)); _needComma = true; return this; }
        public Json Null() { Sep(); _sb.Append("null"); _needComma = true; return this; }

        public Json Val(double d)
        {
            Sep();
            if (double.IsNaN(d) || double.IsInfinity(d)) _sb.Append("null");
            else _sb.Append(Math.Round(d, 4).ToString("0.####", CultureInfo.InvariantCulture));
            _needComma = true;
            return this;
        }

        public Json P(string k, string v) { Key(k); Val(v); return this; }
        public Json P(string k, int v) { Key(k); Val(v); return this; }
        public Json P(string k, long v) { Key(k); Val(v); return this; }
        public Json P(string k, double v) { Key(k); Val(v); return this; }
        public Json P(string k, bool v) { Key(k); Val(v); return this; }

        public Json PArr(string k, double[] v)
        {
            Key(k); Arr();
            for (int i = 0; i < v.Length; i++) Val(v[i]);
            EndArr();
            return this;
        }

        public Json PArr(string k, long[] v)
        {
            Key(k); Arr();
            for (int i = 0; i < v.Length; i++) Val(v[i]);
            EndArr();
            return this;
        }

        public Json PArr(string k, int[] v)
        {
            Key(k); Arr();
            for (int i = 0; i < v.Length; i++) Val(v[i]);
            EndArr();
            return this;
        }

        /// <summary>Appends already-serialised JSON as a value.</summary>
        public Json Raw(string json) { Sep(); _sb.Append(json); _needComma = true; return this; }

        void Str(string s)
        {
            if (s == null) { _sb.Append("null"); return; }
            _sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': _sb.Append("\\\""); break;
                    case '\\': _sb.Append("\\\\"); break;
                    case '\n': _sb.Append("\\n"); break;
                    case '\r': _sb.Append("\\r"); break;
                    case '\t': _sb.Append("\\t"); break;
                    case '<': _sb.Append("\\u003c"); break;     // safe inside a <script> block
                    default:
                        if (c < 0x20 || c > 0x7e) _sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else _sb.Append(c);
                        break;
                }
            }
            _sb.Append('"');
        }

        public override string ToString() { return _sb.ToString(); }
    }
}
