using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CubeScope
{
    /// <summary>What the CUBE measured on one leg. Kept apart from Leg, because only call record files have it.</summary>
    public sealed class LegQuality
    {
        public double Mos, LossRate, OutOfOrderRate;
        /// <summary>The IJ value of gw-accounting syslog stats. Cisco does not give its unit, so it is shown as printed.</summary>
        public double Jitter;
        public int LostPackets, EarlyPackets, LatePackets, RoundTripMs, ReceiveDelayMs, Icpif;

        /// <summary>Fills what this feed left at zero from what the CUBE's other feed says about the same leg.</summary>
        public void Absorb(LegQuality o)
        {
            if (Mos == 0) Mos = o.Mos;
            if (LossRate == 0) LossRate = o.LossRate;
            if (OutOfOrderRate == 0) OutOfOrderRate = o.OutOfOrderRate;
            if (Jitter == 0) Jitter = o.Jitter;
            if (LostPackets == 0) LostPackets = o.LostPackets;
            if (EarlyPackets == 0) EarlyPackets = o.EarlyPackets;
            if (LatePackets == 0) LatePackets = o.LatePackets;
            if (RoundTripMs == 0) RoundTripMs = o.RoundTripMs;
            if (ReceiveDelayMs == 0) ReceiveDelayMs = o.ReceiveDelayMs;
            if (Icpif == 0) Icpif = o.Icpif;
        }
    }

    /// <summary>What a call record file says about a leg beyond what syslog does. Kept apart from Leg: only these files have it.</summary>
    public sealed class LegDetail
    {
        public int DialPeer = -1, MediaPort;
        public string MediaAddress, Codec, ReceivedCalled, ReceivedCalling, SentCalled, SentCalling;
        /// <summary>When the far end started ringing, on the same clock as Leg.Setup; -1 when the file does not say.</summary>
        public long Alert = -1;
        /// <summary>Null when every measured column is zero.</summary>
        public LegQuality Quality;
        /// <summary>Where this leg's call record row is, when the leg itself was first read from syslog; -1 otherwise.</summary>
        public int RowFile = -1, RowLine;

        /// <summary>Fills what is blank here from what the CUBE's other feed says about the same leg.</summary>
        public void Absorb(LegDetail o)
        {
            if (DialPeer < 0) DialPeer = o.DialPeer;
            if (MediaPort == 0) MediaPort = o.MediaPort;
            if (MediaAddress == null) MediaAddress = o.MediaAddress;
            if (Codec == null) Codec = o.Codec;
            if (ReceivedCalled == null) ReceivedCalled = o.ReceivedCalled;
            if (ReceivedCalling == null) ReceivedCalling = o.ReceivedCalling;
            if (SentCalled == null) SentCalled = o.SentCalled;
            if (SentCalling == null) SentCalling = o.SentCalling;
            if (Alert < 0) Alert = o.Alert;
            if (RowFile < 0) { RowFile = o.RowFile; RowLine = o.RowLine; }
            if (Quality == null) Quality = o.Quality;
            else if (o.Quality != null) Quality.Absorb(o.Quality);
        }
    }

    /// <summary>
    /// Reads the files a CUBE writes with gw-accounting file: one CSV row per call leg, in Cisco's fixed columns
    /// (Table 3-1 of "Configuring File Accounting", 133 columns in the detailed format, 23 in the compact one).
    /// Each row is turned into the two syslog messages the same leg would have sent, and given to LineParser,
    /// so calls read from these files go through exactly the same parsing and joining as the syslog feed.
    /// A leg with a feature besides TWC (a transfer or forward) gets one row per feature: the store knows
    /// (ParsedChunk.OneRowPerFeature) to read the repeat as the same leg's next feature.
    /// </summary>
    public static class CdrParser
    {
        const int LegType = 3, ConfId = 4, Peer = 5, PeerSub = 6, Setup = 7, Connect = 9, Disconnect = 10, Cause = 11, CauseText = 12,
                  Origin = 13, Charged = 14, InfoType = 15, PaksOut = 16, BytesOut = 17, PaksIn = 18, BytesIn = 19,
                  ReceiveDelay = 39, RoundTrip = 40, Early = 47, Late = 48, Lost = 49, Icpif = 70,
                  FeatureName = 112, FeatureTime = 113, FeatureFirst = 114, Mos = 130, LossRate = 131, OutOfOrder = 132;
        const int MinColumns = 20;
        const int Alert = 8, PeerId = 26, CodecName = 34, MediaPort = 37, MediaAddress = 71, RxCalled = 93, RxCalling = 94, SentCalled = 98, SentCalling = 99;

        /// <summary>Cisco's name for each column, Table 3-1.</summary>
        public static readonly string[] ColumnNames = {
            "unix_time", "call-id", "cdr-type", "leg-type", "h323-conf-id", "peer-address", "peer-sub-address", "h323-setup-time", "alert-time", "h323-connect-time",
            "h323-disconnect-time", "h323-disconnect-cause", "disconnect-text", "h323-call-origin", "charged-units", "info-type", "paks-out", "bytes-out", "paks-in", "bytes-in",
            "username", "clid", "dnis", "gtd-orig-cic", "gtd-term-cic", "tx-duration", "peer-id", "peer-if-index", "logical-if-index", "acom-level",
            "noise-level", "voice-tx-duration", "account-code", "codec-bytes", "codec-type-rate", "ontime-rv-playout", "remote-udp-port", "remote-media-udp-port", "vad-enable", "receive-delay",
            "round-trip-delay", "hiwater-playout-delay", "lowater-playout-delay", "gapfill-with-interpolation", "gapfill-with-redundancy", "gapfill-with-silence", "gapfill-with-prediction", "early-packets", "late-packets", "lost-packets",
            "max-bitrate", "faxrelay-start-time", "faxrelay-stop-time", "faxrelay-max-jit-buf-depth", "faxrelay-jit-buf-ovflow", "faxrelay-init-hs-mod", "faxrelay-mr-hs-mod", "faxrelay-num-pages", "faxrelay-tx-packets", "faxrelay-rx-packets",
            "faxrelay-direction", "faxrelay-pkt-conceal", "faxrelay-ecm-status", "faxrelay-encap-protocol", "faxrelay-nsf-country-code", "faxrelay-nsf-manuf-code", "faxrelay-fax-success", "override-session-time", "h323-ivr-out", "internal-error-code",
            "h323-voice-quality", "remote-media-address", "remote-media-id", "carrier-id", "calling-party-category", "originating-line-info", "charge-number", "transmission-medium-req", "service-descriptor", "outgoing-area",
            "incoming-area", "out-trunkgroup-label", "out-carrier-id", "dsp-id", "in-trunkgroup-label", "in-carrier-id", "cust-biz-grp-id", "supp-svc-xfer-by", "voice-feature", "feature-operation",
            "feature-op-status", "feature-op-time", "feature-id", "gw-rxd-cdn", "gw-rxd-cgn", "gtd-gw-rxd-ocn", "gtd-gw-rxd-cnn", "gw-rxd-rdn", "gw-final-xlated-cdn", "gw-final-xlated-cgn",
            "gw-final-xlated-rdn", "gk-xlated-cdn", "gk-xlated-cgn", "gw-collected-cdn", "ip-hop", "redirected-station", "subscriber", "in-intrfc-desc", "out-intrfc-desc", "session-protocol",
            "local-hostname", "backward-call-id", "feature-id_field1", "feature-id_field2", "feature-id_field3", "feature-id_field4", "feature-id_field5", "feature-id_field6", "feature-id_field7", "feature-id_field8",
            "feature-id_field9", "feature-id_field10", "feature-id_field11", "feature-id_field12", "ip-phone-info", "ip-pbx-mode", "in-lpcor-group", "out-lpcor-group", "fac-digit", "fac-status",
            "MOSQe", "Packet-Loss-Rate", "Out-of-Order-Rate" };

        /// <summary>A row's filled columns as number-and-name, value, alternating. Null when it is not a call record.</summary>
        public static string[] NamedColumns(string line)
        {
            string[] f = SplitRow(line);
            if (f.Length < MinColumns) return null;
            List<string> kv = new List<string>(100);
            for (int i = 0; i < f.Length && i < ColumnNames.Length; i++)
            {
                string v = f[i].Trim();
                if (v.Length == 0) continue;
                kv.Add(i.ToString(CultureInfo.InvariantCulture) + " " + ColumnNames[i]);
                kv.Add(Explained(i, v));
            }
            return kv.ToArray();
        }

        /// <summary>A column's value with its meaning in words where the CUBE writes a code.</summary>
        public static string Explained(int column, string v)
        {
            if (column == Cause)
            {
                // the CUBE writes the cause in hexadecimal: 10 is cause 16
                int c;
                if (int.TryParse(v, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out c) && c >= 0 && c < 256)
                    return v + "  (cause " + c.ToString(CultureInfo.InvariantCulture) + ": " + Q850.Name(c) + ")";
            }
            if (column == Origin)
            {
                if (v == "answer") return v + "  (the CUBE answered this leg: it came in)";
                if (v == "originate") return v + "  (the CUBE placed this leg: it went out)";
            }
            return v;
        }

        static string Filled(string s) { s = (s ?? "").Trim(); return s.Length == 0 ? null : s; }

        /// <summary>"ton:0,npi:0,#:5550001" is the number after #:.</summary>
        static string NumberOf(string[] f, int i)
        {
            string v = Col(f, i);
            int k = v.IndexOf("#:", StringComparison.Ordinal);
            return Filled(k >= 0 ? v.Substring(k + 2) : v);
        }

        static LegDetail DetailOf(string[] f)
        {
            LegDetail d = new LegDetail();
            int n;
            if (int.TryParse(Col(f, PeerId).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out n) && n > 0) d.DialPeer = n;
            if (int.TryParse(Col(f, MediaPort).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out n) && n > 0) d.MediaPort = n;
            d.MediaAddress = Filled(Col(f, MediaAddress));
            if (d.MediaAddress == "0.0.0.0") d.MediaAddress = null;
            d.Codec = Filled(Col(f, CodecName));
            d.Alert = LineParser.IosTime(Col(f, Alert));
            d.ReceivedCalled = NumberOf(f, RxCalled); d.ReceivedCalling = NumberOf(f, RxCalling);
            d.SentCalled = NumberOf(f, SentCalled); d.SentCalling = NumberOf(f, SentCalling);
            d.Quality = QualityOf(f);
            return d;
        }

        // what columns 114 onward hold depends on the feature: Cisco's table, and the same keys its syslog records use
        static readonly string[] TwcKeys = { "cgn", "cdn", "frs", "fid", "fcid", "legID" };
        static readonly string[] ForwardKeys = { "frs", "fid", "fcid", "legID", "frson", "fdcnt", "fwder", "fwdee", "fwdto", "frm" };
        static readonly string[] HoldKeys = { "frs", "fid", "fcid", "legID", "hrson", "holding", "held", "sl", "usr", "tag" };
        static readonly string[] XferKeysDoc = { "frs", "fid", "fcid", "xconsID", "legID", "xrsn", "xsts", "Xor", "Xee", "Xto" };
        static readonly string[] XferKeysLog = { "frs", "fid", "xconsID", "fcid", "legID", "xrsn", "xsts", "Xor", "Xee", "Xto" };

        /// <summary>A row's columns. Text may be quoted, and an unquoted value may hold spaces.</summary>
        public static string[] SplitRow(string line)
        {
            List<string> cols = new List<string>(140);
            StringBuilder cur = new StringBuilder(32);
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (quoted && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                    else quoted = !quoted;
                }
                else if (c == ',' && !quoted) { cols.Add(cur.ToString()); cur.Length = 0; }
                else cur.Append(c);
            }
            cols.Add(cur.ToString());
            return cols.ToArray();
        }

        static string Col(string[] f, int i) { return i < f.Length ? f[i] : ""; }

        static double Num(string[] f, int i)
        {
            string s = Col(f, i).Trim();
            int e = 0;
            while (e < s.Length && (char.IsDigit(s[e]) || s[e] == '.' || s[e] == '-')) e++;
            double v;
            return e > 0 && double.TryParse(s.Substring(0, e), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        /// <summary>The syslog header a leg's disconnect time gives: "*Nov 9 2026 20:10:59.336 UTC: ". Null if unreadable.</summary>
        static string HeaderOf(string time)
        {
            string[] p = (time ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 4) return null;
            string clock = p[0], mark = "";
            if (clock[0] == '*' || clock[0] == '.') { mark = clock.Substring(0, 1); clock = clock.Substring(1); }
            string zone = p.Length >= 6 ? " " + p[1] : "";
            return mark + p[p.Length - 3] + " " + p[p.Length - 2] + " " + p[p.Length - 1] + " " + clock + zone + ": ";
        }

        static string LegText(string[] f)
        {
            string o = Col(f, Origin).Trim();
            int origin = o.Equals("answer", StringComparison.OrdinalIgnoreCase) ? 2 : o.Equals("originate", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            StringBuilder b = new StringBuilder(400);
            b.Append("CallLegType ").Append(Col(f, LegType)).Append(", ConnectionId ").Append(Col(f, ConfId))
             .Append(", SetupTime ").Append(Col(f, Setup)).Append(", PeerAddress ").Append(Col(f, Peer))
             .Append(", PeerSubAddress ").Append(Col(f, PeerSub)).Append(", DisconnectCause ").Append(Col(f, Cause))
             .Append(", DisconnectText ").Append(Col(f, CauseText)).Append(", ConnectTime ").Append(Col(f, Connect))
             .Append(", DisconnectTime ").Append(Col(f, Disconnect)).Append(", CallOrigin ").Append(origin)
             .Append(", ChargedUnits ").Append(Col(f, Charged)).Append(", InfoType ").Append(Col(f, InfoType))
             .Append(", TransmitPackets ").Append(Col(f, PaksOut)).Append(", TransmitBytes ").Append(Col(f, BytesOut))
             .Append(", ReceivePackets ").Append(Col(f, PaksIn)).Append(", ReceiveBytes ").Append(Col(f, BytesIn));
            return b.ToString();
        }

        static bool LooksLikeGuid(string s)
        {
            string t = (s ?? "").Trim();
            return t.Length >= 20 && t.IndexOf(' ') > 0 && GuidForm.IsHex(t.Replace(" ", ""));
        }

        static string FeatureBody(string[] f)
        {
            string fn = Col(f, FeatureName).Trim();
            if (fn.Length == 0) return null;
            string[] keys;
            if (fn == "TWC") keys = TwcKeys;
            else if (fn == "BXFER" || fn == "CXFER") keys = LooksLikeGuid(Col(f, FeatureFirst + 2)) ? XferKeysDoc : XferKeysLog;
            else if (fn == "HOLD" || fn == "RESUME") keys = HoldKeys;
            else keys = ForwardKeys;
            StringBuilder b = new StringBuilder(200);
            b.Append("FEAT_VSA=fn:").Append(fn).Append(",ft:").Append(Col(f, FeatureTime));
            for (int k = 0; k < keys.Length; k++)
            {
                string v = Col(f, FeatureFirst + k);
                if (v.Length > 0) b.Append(',').Append(keys[k]).Append(':').Append(v);
            }
            return b.ToString();
        }

        /// <summary>A row's feature as the syslog text it stands for, or null when it has none (to read its keys back).</summary>
        public static string FeatureText(string line)
        {
            string[] f = SplitRow(line);
            return f.Length >= MinColumns ? FeatureBody(f) : null;
        }

        static LegQuality QualityOf(string[] f)
        {
            LegQuality q = new LegQuality();
            q.Mos = Num(f, Mos); q.LossRate = Num(f, LossRate); q.OutOfOrderRate = Num(f, OutOfOrder);
            q.LostPackets = (int)Num(f, Lost); q.EarlyPackets = (int)Num(f, Early); q.LatePackets = (int)Num(f, Late);
            q.RoundTripMs = (int)Num(f, RoundTrip); q.ReceiveDelayMs = (int)Num(f, ReceiveDelay); q.Icpif = (int)Num(f, Icpif);
            bool any = q.Mos != 0 || q.LossRate != 0 || q.OutOfOrderRate != 0 || q.LostPackets != 0 || q.EarlyPackets != 0 ||
                       q.LatePackets != 0 || q.RoundTripMs != 0 || q.ReceiveDelayMs != 0 || q.Icpif != 0;
            return any ? q : null;
        }

        /// <summary>A line holding only a Unix time: the CUBE writes one every five minutes. Nothing is wrong with it, and it is not a call.</summary>
        static bool IsTimeMark(string line)
        {
            string t = line.Trim();
            if (t.Length != 10) return false;
            for (int i = 0; i < t.Length; i++) if (t[i] < '0' || t[i] > '9') return false;
            return true;
        }

        /// <summary>One row into the parser. False when it is not a call record.</summary>
        static bool FeedRow(LineParser p, string line, int lineNo)
        {
            string[] f = SplitRow(line);
            if (f.Length < MinColumns) return false;
            string header = HeaderOf(Col(f, Disconnect));
            if (header == null || Col(f, ConfId).Trim().Length == 0) return false;
            ParsedChunk c = p.Current;
            long lines = c.Lines, bytes = c.Bytes;
            int records = c.Records.Count;
            p.Feed(header + "%" + LineParser.CallHistory + ": " + LegText(f), lineNo);
            if (c.Records.Count == records) { c.Lines = lines; c.Bytes = bytes; return false; }
            Leg leg = c.Records[c.Records.Count - 1] as Leg;
            string feature = FeatureBody(f);
            if (feature != null) p.Feed("%" + LineParser.FeatHistory + ": " + feature, lineNo);     // no header: one row is one clock reading
            c.Lines = lines + 1;
            c.Bytes = bytes + line.Length + 2;
            if (leg != null)
            {
                if (c.Details == null) c.Details = new Dictionary<Leg, LegDetail>();
                c.Details[leg] = DetailOf(f);
            }
            return true;
        }

        /// <summary>
        /// Reads a call record file from byte position from. completeLinesOnly is for a file that may still be
        /// arriving: a last row with no line ending is left for later, and EndPos points at its first byte.
        /// </summary>
        public static ParsedChunk ParseFile(string path, int fileIndex, long from, int lineNo, bool completeLinesOnly)
        {
            byte[] data;
            long began;
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
            {
                fs.Position = Math.Min(from, fs.Length);
                began = fs.Position;
                data = new byte[fs.Length - fs.Position];
                int got = 0;
                while (got < data.Length)
                {
                    int r = fs.Read(data, got, data.Length - got);
                    if (r <= 0) break;
                    got += r;
                }
                if (got < data.Length) Array.Resize(ref data, got);
            }
            int len = data.Length;
            if (completeLinesOnly) while (len > 0 && data[len - 1] != (byte)'\n') len--;
            string text = LineParser.Latin1.GetString(data, 0, len);
            DateTime now = DateTime.Now;
            LineParser p = new LineParser(fileIndex, now.Year, now.Month);
            int n = lineNo, start = 0;
            while (start < text.Length)
            {
                int nl = text.IndexOf('\n', start);
                int end = nl < 0 ? text.Length : nl;
                string line = text.Substring(start, end > start && text[end - 1] == '\r' ? end - start - 1 : end - start);
                n++;
                if (line.Trim().Length > 0 && !IsTimeMark(line) && !FeedRow(p, line, n))
                {
                    p.Current.Lines++;
                    p.Current.BadRecords++;
                    p.Current.Warn(Path.GetFileName(path) + " line " + n + ": not a call record");
                }
                start = nl < 0 ? text.Length : nl + 1;
            }
            p.Flush();
            ParsedChunk c = p.Take();
            c.OneRowPerFeature = true;
            c.EndPos = began + len;
            c.EndLine = n;
            return c;
        }
    }

    /// <summary>Deletes old call record files. Nothing else is ever touched: a file must have the CUBE's own name form.</summary>
    public static class CdrRetention
    {
        public const int MaxPerPass = 200000;

        /// <summary>Deletes the call record files under root last written more than keepDays ago; how many went.</summary>
        public static int Run(string root, int keepDays, DateTime nowUtc, out long bytes, out int failed)
        {
            bytes = 0; failed = 0;
            if (keepDays < 1 || string.IsNullOrEmpty(root) || !Directory.Exists(root)) return 0;
            DateTime cut = nowUtc.AddDays(-keepDays);
            int deleted = 0;
            Walk(new DirectoryInfo(root), cut, 0, ref deleted, ref bytes, ref failed);
            return deleted;
        }

        static void Walk(DirectoryInfo dir, DateTime cut, int depth, ref int deleted, ref long bytes, ref int failed)
        {
            try
            {
                foreach (FileInfo f in dir.EnumerateFiles(Loader.CdrPattern))
                {
                    if (deleted >= MaxPerPass) return;
                    if (!Loader.IsCdrName(f.Name) || f.LastWriteTimeUtc >= cut) continue;
                    long size = f.Length;
                    try { f.Delete(); deleted++; bytes += size; }
                    catch (Exception) { failed++; }
                }
                if (depth >= 2) return;
                foreach (DirectoryInfo d in dir.EnumerateDirectories()) Walk(d, cut, depth + 1, ref deleted, ref bytes, ref failed);
            }
            catch (Exception) { failed++; }
        }
    }
}
