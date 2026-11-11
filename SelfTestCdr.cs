using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace CubeScope
{
    /// <summary>Call record files, their cleanup and the no-records alarm. Every number and GUID here is invented.</summary>
    public static partial class SelfTest
    {
        const string CGuid = "ABCD0001 11110000 2222 33334444";
        const string CName = "cdr.example-cube.11_09_2026_10_05_00.100";

        static void TestCdr()
        {
            bool echo = Log.Echo;
            Log.Echo = false;
            try
            {
                TestCdrNames();
                TestCdrRows();
                TestCdrFeatureRows();
                TestCdrQuality();
                TestCdrColumns();
                TestSyslogStats();
                TestSequenceWithDebug();
                TestCdrFollow();
                TestCdrRetention();
                TestSyslogRetention();
                TestNoRecordsWhatCounts();
                TestTwoFeeds();
                TestRedact();
                TestCallFacts();
                TestSettingsWindow();
                TestAlarmScopes();
                TestCdrNoRecordsAlarm();
                TestCdrServiceStep();
            }
            finally { Log.Echo = echo; }
        }

        static string CTime(string hms) { return "*" + hms + " UTC Mon Nov 9 2026"; }

        /// <summary>A row in Cisco's 133 columns. feat is column 112 onward; extra sets any other column.</summary>
        static string CRow(string guid, int callId, string origin, string peer, string connect, string disc, int packets, string[] feat, Dictionary<int, string> extra)
        {
            string[] f = new string[133];
            for (int i = 0; i < f.Length; i++) f[i] = "";
            f[0] = "1794255059"; f[1] = callId.ToString(CultureInfo.InvariantCulture); f[2] = "1"; f[3] = "2";
            f[4] = "\"" + guid + "\""; f[5] = "\"" + peer + "\""; f[7] = "\"" + CTime("10:00:00.000") + "\""; f[9] = "\"" + CTime(connect) + "\"";
            f[10] = "\"" + CTime(disc) + "\""; f[11] = "\"10  \""; f[12] = "\"normal call clearing (16)\""; f[13] = "\"" + origin + "\""; f[14] = "0"; f[15] = "\"speech\"";
            string p = packets.ToString(CultureInfo.InvariantCulture), b = (packets * 200).ToString(CultureInfo.InvariantCulture);
            f[16] = p; f[17] = b; f[18] = p; f[19] = b;
            f[34] = "\"g711ulaw\"";
            for (int c = 39; c <= 46; c++) f[c] = "\"0 ms\"";
            f[47] = f[48] = f[49] = f[70] = "0";
            f[130] = "\"0.0000\""; f[131] = "\"0.00 %\""; f[132] = "\"0.00 %\"";
            for (int k = 0; feat != null && k < feat.Length; k++) f[112 + k] = feat[k];
            if (extra != null) foreach (KeyValuePair<int, string> kv in extra) f[kv.Key] = kv.Value;
            return string.Join(",", f);
        }

        static string[] Twc(string guid, int fid, string legHex)
        {
            return new[] { "TWC", "11/09/2026 10:00:00.000", "5550001", "5550002", "0", fid.ToString(CultureInfo.InvariantCulture), guid, legHex };
        }

        static string[] Cfa(string guid, int fid, string legHex)
        {
            return new[] { "CFA", "11/09/2026 10:00:00.000", "0", fid.ToString(CultureInfo.InvariantCulture), guid, legHex, "1", "1", "5550003", "5550004", "5550005", "5550006" };
        }

        static string NewDir() { string d = Path.Combine(Path.GetTempPath(), "cs_cdr_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }

        static void Old(string path) { File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5)); }

        static void WriteCdr(string dir, string name, params string[] rows)
        {
            string p = Path.Combine(dir, name);
            File.WriteAllText(p, string.Join("\n", rows) + "\n", LineParser.Latin1);
            Old(p);
        }

        static Store ReadCdr(string dir) { return Loader.LoadAll(new LoadOptions { Root = dir, Days = 0, Quiet = true }, null); }

        static void TestCdrNames()
        {
            Group("call record files: names and rows");
            Check(Loader.IsCdrName(CName), "the name a CUBE gives");
            Check(Loader.IsCdrName("anything.host.a.b.11_09_2026_10_05_00.100"), "any prefix, and a host name with dots");
            Check(!Loader.IsCdrName("cube-2026-11-09-10.log"), "a syslog file");
            Check(!Loader.IsCdrName("test.csv"), "something else");
            Check(!Loader.IsCdrName("cdr.example-cube.11_09_2026_10_05_00.100.bak"), "a copy with another ending");
            LogFile f = Loader.Describe(Path.Combine("x", CName));
            Check(f.IsCdr, "known as a call record file");
            Eq(f.SourceName, "example-cube", "the CUBE comes from the name");
            Eq(f.NameMs, TimeUtil.Ms(2026, 11, 9, 10, 5, 0, 100), "its time comes from the name");
            Eq(Loader.NameMsOf(Path.Combine("x", CName)), -1L, "the clock check does not take it for the receiver's hour");

            string[] c = CdrParser.SplitRow("1,\"a,b\",c d,,\"say \"\"hi\"\"\"");
            Eq(c.Length, 5, "columns");
            Eq(c[1], "a,b", "a comma inside quotes");
            Eq(c[2], "c d", "a space without quotes");
            Eq(c[3], "", "an empty column");
            Eq(c[4], "say \"hi\"", "a doubled quote");
        }

        static void TestCdrRows()
        {
            Group("call record files: a plain call");
            string dir = NewDir();
            try
            {
                WriteCdr(dir, CName,
                    CRow(CGuid, 100, "answer", "5550001", "10:00:05.000", "10:01:05.000", 3000, Twc(CGuid, 900, "A1"), null),
                    CRow(CGuid, 101, "originate", "5550002", "10:00:05.000", "10:01:05.500", 3000, Twc(CGuid, 901, "A2"), null),
                    "not a call record");
                Store st = ReadCdr(dir);
                Eq(st.Sources.Count, 1, "one CUBE");
                Eq(st.Sources[0].Name, "example-cube", "named by its file");
                Eq(st.Calls.Count, 1, "one call");
                Eq(st.Sources[0].BadRecords, 1L, "the junk line is counted");
                Call c = st.Calls[0];
                Eq(c.Legs.Count, 2, "two legs");
                Leg a = c.Legs[0], b = c.Legs[1];
                Check(a.In && b.Out, "answer is the leg the CUBE answered, originate the one it placed");
                Eq(a.Peer, "5550001", "peer");
                Eq(a.Cause, (short)16, "cause, from hex");
                Eq(a.CauseText, "normal call clearing", "cause text");
                Eq(a.Setup, TimeUtil.Ms(2026, 11, 9, 10, 0, 0, 0), "setup");
                Eq(a.Connect, TimeUtil.Ms(2026, 11, 9, 10, 0, 5, 0), "answer");
                Eq(a.Disconnect, TimeUtil.Ms(2026, 11, 9, 10, 1, 5, 0), "disconnect");
                Eq(a.TxPackets, 3000, "packets sent");
                Eq(a.RxBytes, 600000u, "bytes received");
                Check(a.Bound && b.Bound, "the TWC columns joined their legs");
                Eq(a.Cgn, "5550001", "calling number");
                Eq(a.Cdn, "5550002", "called number");
                Eq(a.LegId, 0xA1, "leg id");
                st.DeriveDirty();
                Eq(c.Result, Result.Answered, "answered");
                Eq(st.Sources[0].Lines, 3L, "rows counted once each");
                Eq(st.Sources[0].Unbound, 0L, "nothing left unjoined");
                string json = Query.CallJson(st, c, true);
                Check(json.IndexOf("cdr.example-cube", StringComparison.Ordinal) > 0, "the raw view names the file");
                Check(json.IndexOf("\"q\":", StringComparison.Ordinal) < 0, "all-zero quality columns are not shown as a measurement");
                Check(st.Sources[0].Details != null && st.Sources[0].Details[a].Quality == null, "and are not kept");
            }
            finally { Directory.Delete(dir, true); }
        }

        static int LegsBound(Call c) { int n = 0; for (int i = 0; i < c.Legs.Count; i++) if (c.Legs[i].Bound) n++; return n; }

        static void TestCdrFeatureRows()
        {
            Group("call record files: one row per feature");
            string dir = NewDir();
            try
            {
                string g2 = "ABCD0002 11110000 2222 33334444", g3 = "ABCD0003 11110000 2222 33334444";
                WriteCdr(dir, "cdr.example-cube.11_09_2026_10_05_00.100",
                    CRow(CGuid, 100, "answer", "5550001", "10:00:05.000", "10:01:05.000", 300, Cfa(CGuid, 910, "A1"), null),
                    CRow(CGuid, 100, "answer", "5550001", "10:00:05.000", "10:01:05.000", 300, Twc(CGuid, 900, "A1"), null),
                    CRow(CGuid, 101, "originate", "5550002", "10:00:05.000", "10:01:05.000", 300, Cfa(CGuid, 911, "A2"), null));
                // the leg's second row landed in the next file, as it can when the CUBE's buffer fills
                WriteCdr(dir, "cdr.example-cube.11_09_2026_10_05_15.100",
                    CRow(CGuid, 101, "originate", "5550002", "10:00:05.000", "10:01:05.000", 300, Twc(CGuid, 901, "A2"), null),
                    CRow(g2, 200, "answer", "5550001", "10:00:05.000", "10:01:05.000", 300, Twc(g2, 920, "B1"), null),
                    CRow(g2, 200, "answer", "5550001", "10:00:05.000", "10:01:05.000", 300, Cfa(g2, 921, "B1"), null),
                    CRow(g3, 300, "answer", "5550001", "10:00:05.000", "10:01:05.000", 300,
                         new[] { "BXFER", "11/09/2026 10:00:30.000", "0", "930", g3, "XC", "C1", "1", "2", "<sip:a@x>", "<sip:b@x>", "<sip:5550007@x>" }, null),
                    CRow(g3, 300, "answer", "5550001", "10:00:05.000", "10:01:05.000", 300,
                         new[] { "BXFER", "11/09/2026 10:00:40.000", "0", "931", "XC", g3, "C1", "1", "2", "<sip:a@x>", "<sip:b@x>", "<sip:5550008@x>" }, null));
                Store st = ReadCdr(dir);
                Source s = st.Sources[0];
                Eq(st.Calls.Count, 3, "three calls");
                Eq(s.DuplicateLegs, 0L, "a repeat for a feature is not a duplicate");
                Eq(s.DuplicateFeatures, 0L, "no feature lost as a duplicate");
                Call c = st.Calls[0], c2 = st.Calls[1], c3 = st.Calls[2];
                Eq(c.Legs.Count, 2, "four rows are two legs");
                Eq(LegsBound(c), 2, "TWC joined, even in the next file");
                Check(c.Features != null && c.Features.Count == 2, "both forwards kept");
                Eq(s.Unbound, 1L, "only the transfer-only call has a leg with no TWC");
                Eq(c2.Legs.Count, 1, "TWC first, then the forward: one leg");
                Eq(LegsBound(c2), 1, "bound");
                Check(c2.Features != null && c2.Features.Count == 1, "the forward after the TWC is kept");
                Check(c3.Features != null && c3.Features.Count == 2, "transfers, with the call id in either column");
                if (c3.Features != null && c3.Features.Count == 2)
                {
                    Eq(c3.Features[0].Xto, "<sip:5550007@x>", "transfer target");
                    Eq(c3.Features[1].Xto, "<sip:5550008@x>", "transfer target, other order");
                    Eq(c3.Features[0].LegId, 0xC1, "the leg it belongs to");
                }
                string json = Query.CallJson(st, c, true);
                Check(json.IndexOf("fwdto", StringComparison.Ordinal) > 0, "a feature's fields read back from its row");
            }
            finally { Directory.Delete(dir, true); }
        }

        static void TestCdrQuality()
        {
            Group("call record files: quality");
            string dir = NewDir();
            try
            {
                Dictionary<int, string> q = new Dictionary<int, string>();
                q[39] = "\"70 ms\""; q[40] = "\"45 ms\""; q[47] = "3"; q[48] = "5"; q[49] = "12"; q[70] = "20"; q[130] = "\"3.8100\""; q[131] = "\"1.25 %\""; q[132] = "\"0.10 %\"";
                WriteCdr(dir, CName,
                    CRow(CGuid, 100, "answer", "5550001", "10:00:05.000", "10:01:05.000", 3000, Twc(CGuid, 900, "A1"), q),
                    CRow(CGuid, 101, "originate", "5550002", "10:00:05.000", "10:01:05.000", 3000, Twc(CGuid, 901, "A2"), null));
                Store st = ReadCdr(dir);
                Source s = st.Sources[0];
                Check(s.Details != null && s.Details.Count == 2 && s.Details[st.Calls[0].Legs[1]].Quality == null, "only the leg that was measured");
                LegQuality lq = s.Details != null && s.Details[st.Calls[0].Legs[0]].Quality != null ? s.Details[st.Calls[0].Legs[0]].Quality : new LegQuality();
                Eq(lq.Mos, 3.81, "MOS");
                Eq(lq.LossRate, 1.25, "loss rate");
                Eq(lq.OutOfOrderRate, 0.1, "out of order");
                Eq(lq.LostPackets, 12, "lost");
                Eq(lq.EarlyPackets, 3, "early");
                Eq(lq.LatePackets, 5, "late");
                Eq(lq.RoundTripMs, 45, "round trip");
                Eq(lq.ReceiveDelayMs, 70, "receive delay");
                Eq(lq.Icpif, 20, "ICPIF");
                string json = Query.CallJson(st, st.Calls[0], false);
                Check(json.IndexOf("\"q\":{\"mos\":3.81", StringComparison.Ordinal) > 0, "the call detail gets it");
            }
            finally { Directory.Delete(dir, true); }
        }

        static Dictionary<int, string> Extra(int dialPeer, string codec, string ip, int port, string alert, string rxCalled, string sentCalled)
        {
            Dictionary<int, string> x = new Dictionary<int, string>();
            x[26] = dialPeer.ToString(CultureInfo.InvariantCulture); x[34] = "\"" + codec + "\""; x[71] = "\"" + ip + "\""; x[37] = port.ToString(CultureInfo.InvariantCulture);
            if (alert != null) x[8] = "\"" + CTime(alert) + "\"";
            x[93] = "\"ton:0,npi:0,#:" + rxCalled + "\""; x[94] = "\"ton:0,npi:0,pi:0,si:0,#:5559001\"";
            if (sentCalled != null) { x[98] = "\"ton:0,npi:0,#:" + sentCalled + "\""; x[99] = "\"ton:0,npi:0,pi:0,si:0,#:5559002\""; }
            return x;
        }

        static int Matches(Store st, string text)
        {
            Filter f = Filter.Parse(text);
            Check(f.Error == null, "the filter reads: " + text + " " + f.Error);
            int n = 0;
            Filter.CurrentSources = st.Sources;
            for (int i = 0; i < st.Calls.Count; i++) if (f.Match(st.Calls[i], st.Rules)) n++;
            Filter.CurrentSources = null;
            return n;
        }

        static void TestCdrColumns()
        {
            Group("call record files: the extra columns");
            Eq(CdrParser.ColumnNames.Length, 133, "every column is named");
            Eq(CdrParser.ColumnNames[26], "peer-id", "column 26");
            Eq(CdrParser.ColumnNames[71], "remote-media-address", "column 71");
            Eq(CdrParser.ColumnNames[130], "MOSQe", "column 130");
            string dir = NewDir();
            try
            {
                string g2 = "ABCD0002 11110000 2222 33334444", g3 = "ABCD0003 11110000 2222 33334444";
                Dictionary<int, string> bad = Extra(20, "g729r8", "192.0.2.77", 20000, "10:00:03.000", "5550022", "9875550022");
                bad[130] = "\"3.1000\""; bad[131] = "\"2.50 %\""; bad[49] = "30"; bad[40] = "\"200 ms\"";
                WriteCdr(dir, CName,
                    CRow(CGuid, 100, "answer", "5550001", "10:00:05.000", "10:01:05.000", 3000, Twc(CGuid, 900, "A1"), Extra(5, "g711ulaw", "192.0.2.10", 16384, null, "5550002", null)),
                    CRow(CGuid, 101, "originate", "5550002", "10:00:05.000", "10:01:05.000", 3000, Twc(CGuid, 901, "A2"), Extra(10, "g711ulaw", "198.51.100.20", 16386, "10:00:01.500", "5550002", "5550002")),
                    CRow(g2, 200, "answer", "5550001", "10:00:05.000", "10:01:05.000", 3000, Twc(g2, 920, "B1"), Extra(5, "g729r8", "192.0.2.11", 16388, null, "5550022", null)),
                    CRow(g2, 201, "originate", "5550002", "10:00:05.000", "10:01:05.000", 3000, Twc(g2, 921, "B2"), bad),
                    CRow(g3, 300, "answer", "5550001", "10:00:00.000", "10:00:00.000", 0, Twc(g3, 930, "C1"), Extra(6, "g711ulaw", "192.0.2.12", 16390, null, "5550033", null)));
                Store st = ReadCdr(dir);
                Source s = st.Sources[0];
                LegDetail d = s.Details[st.Calls[0].Legs[1]];
                Eq(d.DialPeer, 10, "dial-peer");
                Eq(d.Codec, "g711ulaw", "codec");
                Eq(d.MediaAddress, "198.51.100.20", "far-end media address");
                Eq(d.MediaPort, 16386, "far-end media port");
                Eq(d.Alert, TimeUtil.Ms(2026, 11, 9, 10, 0, 1, 500), "ringing started");
                Eq(d.ReceivedCalled, "5550002", "the number after #:");
                Eq(d.SentCalling, "5559002", "calling number as sent");
                Eq(s.Details[st.Calls[0].Legs[0]].Alert, -1L, "no ringing time on this leg");

                Group("call record files: filtering on them");
                st.DeriveDirty();
                Eq(Matches(st, "dialpeer=5"), 2, "any leg's dial-peer");
                Eq(Matches(st, "dialpeer=5,6"), 3, "alternatives");
                Eq(Matches(st, "dialpeer!=5"), 1, "no leg used it");
                Eq(Matches(st, "!dialpeer=20"), 2, "the whole clause reversed");
                Eq(Matches(st, "mediaip=192.0.2.*"), 3, "address with a wildcard");
                Eq(Matches(st, "mediaip=\"198.51.100.20\""), 1, "exact address");
                Eq(Matches(st, "mediaport>=20000"), 1, "port");
                Eq(Matches(st, "codec=g729"), 1, "codec by name");
                Eq(Matches(st, "codec=\"g711ulaw\""), 2, "exact codec");
                Eq(Matches(st, "alert>2"), 1, "seconds until ringing");
                Eq(Matches(st, "alert>0"), 2, "a leg with no ringing time does not count");
                Eq(Matches(st, "received=5550022"), 1, "number as received");
                Eq(Matches(st, "sent=9875550022"), 1, "number as sent");
                Eq(Matches(st, "any=9875550022"), 1, "any also searches the translated number");
                Eq(Matches(st, "mos<3.5"), 1, "poor MOS");
                Eq(Matches(st, "mos<5"), 1, "a leg that was not measured is not a MOS of zero");
                Eq(Matches(st, "loss>1"), 1, "loss");
                Eq(Matches(st, "lost>0"), 1, "lost packets");
                Eq(Matches(st, "rtt>=200"), 1, "round trip");
                Check(Filter.Parse("codec=200").Error == null, "codec as bytes per packet still reads");
                Check(Filter.Parse("dialpeer=abc").Error != null, "a dial-peer must be a number");

                Group("call record files: tables and totals");
                QueryParams q = new QueryParams();
                QueryResult r;
                lock (st.Sync) r = Query.Run(st, q);
                Eq(r.TopDialPeers.Count, 4, "four dial-peers");
                Eq(r.TopDialPeers[0].Number, "5", "busiest first");
                Eq(r.TopDialPeers[0].Calls, 2, "a call counts once per dial-peer");
                Eq(r.TopCodecs[0].Number, "g711ulaw", "commonest codec");
                Eq(r.TopCodecs[0].Calls, 2, "codec counts");
                Eq(r.TopCodecs[1].Calls, 1, "the call with g729 on both legs counts once");
                Eq(r.TopMedia.Count, 5, "every far-end address");
                Eq(r.AlertN, 2, "calls with a ringing time");
                Eq(r.QualityCalls, 1, "measured calls");
                Eq(r.QualityPoor, 1, "poor calls");
                Eq(r.WorstMos, 3.1, "worst MOS");
                Eq(r.WorstLoss, 2.5, "worst loss");
                Eq(r.ChoiceDialPeers["5"], 2, "the Find picker's count");
                Eq(r.ChoiceCodecs["g729r8"], 1, "the codec picker's count");

                string json;
                lock (st.Sync) json = Query.CallJson(st, st.Calls[0], true);
                Check(json.IndexOf("\"dialPeer\":5", StringComparison.Ordinal) > 0, "the call detail has the dial-peer");
                Check(json.IndexOf("\"26 peer-id\"", StringComparison.Ordinal) > 0, "and every filled column by name");
                string state;
                lock (st.Sync) state = Query.StateJson(st, q, r, "");
                Check(state.IndexOf("\"dialpeer\":[{\"n\":\"5\"", StringComparison.Ordinal) > 0, "the page gets the table");
                Check(state.IndexOf("\"qualityPoor\":1", StringComparison.Ordinal) > 0, "and the quality total");

                StringWriter csv = new StringWriter();
                lock (st.Sync) Report.Csv(st, Query.Sorted(r.Matched, "start", false, r.Zone), csv, r.Zone);
                string[] lines = csv.ToString().Replace("\r\n", "\n").Split('\n');
                Check(lines[0].EndsWith("worst_loss_pct,worst_jitter", StringComparison.Ordinal), "the export has the columns");
                Check(lines[0].IndexOf("dial_peers", StringComparison.Ordinal) > 0, "dial-peers among them");
                bool found = false;
                for (int i = 1; i < lines.Length; i++) if (lines[i].IndexOf(";", StringComparison.Ordinal) > 0 && lines[i].IndexOf("3.10,2.50", StringComparison.Ordinal) > 0) found = true;
                Check(found, "several values in one cell, and the worst quality");

                Group("alarm on a dial-peer");
                AlarmConfig c = new AlarmConfig(); c.Enabled = true; c.EventLog = false;
                AlarmRule rule = new AlarmRule(); rule.Dnis = "6"; rule.Field = "dialpeer"; rule.MinCalls = 1; rule.MinFailures = 1; rule.WindowMinutes = 60;
                rule.FailResults = new List<string> { "abandoned", "rejected", "failed", "noanswer" };
                c.Rules.Add(rule);
                List<string> got = new List<string>();
                string data = NewDir();
                AlarmEngine e = new AlarmEngine(data, delegate () { return st; });
                e.Zone = "cube"; e.OnLog = delegate (string x) { };
                e.Deliver = delegate (int id, string level, string msg, AlarmInfo info) { got.Add(id + " " + msg.Split('\n')[0]); return true; };
                e.Use(c);
                e.Evaluate(new DateTime(2026, 11, 9, 10, 30, 0));
                Eq(got.Count, 1, "one alarm for that dial-peer's failed call");
                Check(got.Count > 0 && got[0].IndexOf("call failures to dial-peer 6 on example-cube", StringComparison.Ordinal) > 0, "named as a dial-peer");
                Directory.Delete(data, true);
                string err;
                Check(AlarmConfig.FromJson((Dictionary<string, object>)JsonRead.Parse("{\"intervalMinutes\":5,\"rules\":[{\"dnis\":\"20\",\"field\":\"dialpeer\"}]}"), out err) != null, "a dial-peer rule saves: " + err);
                Check(AlarmConfig.FromJson((Dictionary<string, object>)JsonRead.Parse("{\"intervalMinutes\":5,\"rules\":[{\"dnis\":\"20x\",\"field\":\"dialpeer\"}]}"), out err) == null, "a dial-peer must be a number");
            }
            finally { Directory.Delete(dir, true); }
        }

        static string SLeg(int rx, int ios, string key)
        {
            return rx + ": " + ios + ": Nov  9 16:16:12.668 CST: %VOIPAAA-5-VOIP_CALL_HISTORY: CallLegType 2, ConnectionId " + key +
                   ", SetupTime 16:08:30.208 CST Mon Nov 9 2026, PeerAddress 5550001, PeerSubAddress , DisconnectCause 10  , DisconnectText normal call clearing (16), ConnectTime 16:08:30.278 CST Mon Nov 9 2026, " +
                   "DisconnectTime 16:16:12.668 CST Mon Nov 9 2026, CallOrigin 1, ChargedUnits 0, InfoType 2, TransmitPackets 23049, TransmitBytes 4606992, ReceivePackets 23012, ReceiveBytes 4602360";
        }

        static string STwc(int rx, int ios, string key, int fid)
        {
            return rx + ": " + ios + ": Nov  9 16:16:12.668 CST: %VOIPAAA-5-VOIP_FEAT_HISTORY: FEAT_VSA=fn:TWC,ft:11/09/2026 16:08:30.213,cgn:+15550001,cdn:+15550002,frs:0,fid:" + fid + ",fcid:" + key + ",legID:A1,bguid:" + key;
        }

        static string[] SBlock(int rx, int ios, string ij, string late, string extraRf)
        {
            List<string> l = new List<string>();
            l.Add(rx + ": " + ios + ": Nov  9 16:16:12.668 CST: VOIP_CALL_STATISTICS:DSP/TX: PK=0, SG=0, NS=0, DU=0, VO=0");
            l.Add((rx + 1) + ": DSP/RX: PK=0, SG=0, CF=0, RX=0, VO=0, BS=0, BP=0, LP=" + late + ", EP=0");
            l.Add((rx + 2) + ": DSP/PD: CU=0, MI=0, MA=0, CO=0, IJ=" + ij);
            l.Add((rx + 3) + ": DSP/PE: PC=0, IC=0, SC=0, RM=0, BO=0, EE=0");
            l.Add((rx + 4) + ": DSP/LE: TP=0, TX=0, RP=0, RM=0, BN=0, ER=0, AC=0");
            l.Add((rx + 5) + ": DSP/ER: RD=0, TD=0, RC=0, TC=0");
            l.Add((rx + 6) + ": DSP/IC: IC=0");
            if (extraRf != null) l.Add((rx + 7) + ": DSP/RF: ML=" + extraRf + ", MC=" + extraRf + ", R1=86, R2=86, IF=0, ID=0, IE=0, BL=0, R0=93, VR=2.0");
            return l.ToArray();
        }

        static void TestSyslogStats()
        {
            Group("gw-accounting syslog stats");
            const string K1 = "ABCD000111110000222233334444", K2 = "ABCD000211110000222233334444", K3 = "ABCD000311110000222233334444";
            LineParser p = new LineParser(0, 2026, 11);
            int n = 0;
            Action<string> feed = delegate (string l) { p.Feed(l, ++n); };
            feed(SLeg(100, 1000, K1)); feed(STwc(101, 1001, K1, 900));
            foreach (string l in SBlock(102, 1002, "0.0000", "0", null)) feed(l);
            feed(SLeg(110, 1003, K2)); feed(STwc(111, 1004, K2, 901));
            foreach (string l in SBlock(112, 1005, "0.0156", "4", "3.2000")) feed(l);
            feed(SLeg(121, 1006, K3)); feed(STwc(122, 1007, K3, 902));
            ParsedChunk first = p.Take();                  // a poll ends in the gap between a leg and its block
            string[] last = SBlock(123, 1008, "0.0078", "0", "4.1000");
            foreach (string l in last) feed(l);
            feed("131: 1010: Nov  9 16:16:14.000 CST: %SYS-5-CONFIG_I: Configured from console by test");   // 1009 really is lost
            p.Flush();
            ParsedChunk second = p.Take();
            Eq(first.IosMissing, 0L, "a statistics block is not a lost message");
            Eq(first.Skipped, 0L, "and its lines are read, not skipped");
            Eq(second.IosMissing, 1L, "a message really lost is still counted");
            Store st = new Store();
            Source s = st.AddSource("cube", "");
            st.Ingest(s, first); st.Ingest(s, second);
            st.Settle(s, true); st.DeriveDirty();
            Eq(st.Calls.Count, 3, "three calls");
            Check(s.Details != null && !s.Details.ContainsKey(st.Calls[0].Legs[0]), "an all-zero block is not a measurement");
            Check(s.Details != null && s.Details.ContainsKey(st.Calls[1].Legs[0]), "a block with numbers goes with the leg before it");
            LegQuality q = s.Details != null && s.Details.ContainsKey(st.Calls[1].Legs[0]) ? s.Details[st.Calls[1].Legs[0]].Quality : new LegQuality();
            Eq(q.Mos, 3.2, "MOS");
            Eq(q.Jitter, 0.0156, "jitter");
            Eq(q.LatePackets, 4, "late packets");
            Check(s.Details != null && s.Details.ContainsKey(st.Calls[2].Legs[0]), "the leg can be in the chunk before its block");
            Eq(Matches(st, "mos<3.5"), 1, "filter on MOS");
            Eq(Matches(st, "mos>0"), 2, "only measured legs");
            Eq(Matches(st, "jitter>0.01"), 1, "filter on jitter");
            QueryResult r;
            lock (st.Sync) r = Query.Run(st, new QueryParams());
            Eq(r.QualityCalls, 2, "measured calls");
            Eq(r.QualityPoor, 1, "poor calls");
            Eq(r.WorstMos, 3.2, "worst MOS");
            Eq(r.WorstJitter, 0.0156, "worst jitter");
            string json;
            lock (st.Sync) json = Query.CallJson(st, st.Calls[1], false);
            Check(json.IndexOf("\"q\":{\"mos\":3.2", StringComparison.Ordinal) > 0, "the call detail gets it");
            Check(json.IndexOf("\"d\":{", StringComparison.Ordinal) < 0, "and no empty call record table");
        }

        const string SeqTime = "Nov  9 16:00:17.376 CST: ";
        static string SMsg(int ios) { return "9: " + ios + ": " + SeqTime + "%SYS-5-CONFIG_I: Configured from console by test"; }
        static string STrace(int ios) { return "9: " + ios + ": " + SeqTime + "//15918433/DB62F3800002/SIP/Msg/ccsipDisplayMsg:"; }
        static string SDebug(int ios) { return "9: " + ios + ": " + SeqTime + "cc_api_get_xcode_stream : stream 1"; }

        static ParsedChunk SParse(LineParser p, params string[] lines)
        {
            int n = 0;
            foreach (string l in lines) p.Feed(l, ++n);
            return p.Take();
        }

        static void TestSequenceWithDebug()
        {
            Group("sequence numbers with debug on");
            ParsedChunk c = SParse(new LineParser(0, 2026, 11), SMsg(100), STrace(101), STrace(102), SDebug(103), SMsg(104), STrace(105), SMsg(106), SDebug(107), SMsg(108));
            Eq(c.IosMissing, 0L, "debug lines that share the counter are not lost messages");
            Eq(c.IosGaps, 0L, "and make no gaps");
            Eq(c.LastIos, 108L, "last number");

            c = SParse(new LineParser(0, 2026, 11), SMsg(100), STrace(101), SMsg(102), STrace(103), SMsg(104), STrace(105), STrace(107), STrace(108), SMsg(109), SMsg(112));
            Eq(c.IosMissing, 3L, "lost lines are still counted, inside debug (106) and between messages (110, 111)");
            Eq(c.IosGaps, 2L, "two gaps");

            c = SParse(new LineParser(0, 2026, 11), SMsg(100), STrace(101), SMsg(102), STrace(103), SMsg(104), STrace(105), SMsg(106), STrace(107), STrace(109), SMsg(110));
            Eq(c.IosMissing, 1L, "a message lost among debug lines is found (108)");

            c = SParse(new LineParser(0, 2026, 11), STrace(50), STrace(51), SMsg(52), STrace(53), SMsg(54), STrace(55), SMsg(56));
            Eq(c.FirstIos, 50L, "a chunk that opens with debug lines begins at the first of them");
            Eq(c.IosMissing, 0L, "and has no gap");

            Group("sequence numbers with a separate debug counter");
            c = SParse(new LineParser(0, 2026, 11), SMsg(1000), STrace(5000), STrace(5001), SMsg(1001), STrace(5002), SMsg(1002), STrace(5003), SMsg(1003));
            Eq(c.IosMissing, 0L, "their numbers are ignored");
            Eq(c.IosRestarts, 0L, "and are not restarts");
            Eq(c.FirstIos, 1000L, "the chunk begins at its first message");
            c = SParse(new LineParser(0, 2026, 11), SMsg(1000), STrace(5000), SMsg(1001), STrace(5001), SMsg(1005));
            Eq(c.IosMissing, 3L, "a real gap between messages still shows");
            c = SParse(new LineParser(0, 2026, 11), SMsg(100), STrace(101), STrace(102), SMsg(101), STrace(103), SMsg(102));
            Eq(c.IosMissing, 0L, "one number that happens to follow does not change how the feed is read");

            Group("sequence numbers across chunks, debug on");
            LineParser p = new LineParser(0, 2026, 11);
            Store st = new Store();
            Source s = st.AddSource("cube", "");
            st.Ingest(s, SParse(p, SMsg(100), STrace(101), SMsg(102), STrace(103), SMsg(104), STrace(105), STrace(106)));
            st.Ingest(s, SParse(p, STrace(107), SDebug(108), SMsg(109), STrace(110)));
            Eq(s.IosMissing, 0L, "polls that end among debug lines lose nothing");
            st.Ingest(s, SParse(p, SMsg(113)));
            Eq(s.IosMissing, 2L, "a gap between chunks still counts");
        }

        static void TestCdrFollow()
        {
            Group("call record files: following a folder");
            string dir = NewDir();
            try
            {
                WriteCdr(dir, CName, CRow(CGuid, 100, "answer", "5550001", "10:00:05.000", "10:01:05.000", 300, Twc(CGuid, 900, "A1"), null));
                LoadOptions o = new LoadOptions { Root = dir, Days = 0, Quiet = true, Live = true };
                Store st = Loader.LoadAll(o, null);
                Tailer t = new Tailer(st, o);
                t.IntervalMs = 3600000;
                t.Start();
                Eq(st.Sources[0].LegsAdded, 1L, "what the load read");
                t.PollCdr(true);
                Eq(st.Sources[0].LegsAdded, 1L, "nothing read twice");

                string g2 = "ABCD0002 11110000 2222 33334444";
                WriteCdr(dir, "cdr.example-cube.11_09_2026_10_05_15.100", CRow(g2, 200, "answer", "5550001", "10:00:05.000", "10:01:05.000", 300, Twc(g2, 920, "B1"), null));
                t.PollCdr(true);
                Eq(st.Sources[0].LegsAdded, 2L, "a new file");
                Eq(st.Sources[0].Files.Count, 2, "kept for the raw view");

                string fresh = Path.Combine(dir, "cdr.example-cube.11_09_2026_10_05_30.100");
                string g3 = "ABCD0003 11110000 2222 33334444";
                string row3 = CRow(g3, 300, "answer", "5550001", "10:00:05.000", "10:01:05.000", 300, Twc(g3, 930, "C1"), null);
                File.WriteAllText(fresh, row3 + "\n", LineParser.Latin1);
                t.PollCdr(true);
                Eq(st.Sources[0].LegsAdded, 2L, "a file this fresh may still be arriving");
                Old(fresh);
                t.PollCdr(true);
                Eq(st.Sources[0].LegsAdded, 3L, "read once it has settled");

                // a CUBE on flash, or a server that appends, grows the same file
                string g4 = "ABCD0004 11110000 2222 33334444";
                string row4 = CRow(g4, 400, "answer", "5550001", "10:00:05.000", "10:01:05.000", 300, Twc(g4, 940, "D1"), null);
                File.AppendAllText(fresh, row4.Substring(0, 40), LineParser.Latin1);
                t.PollCdr(true);
                Eq(st.Sources[0].LegsAdded, 3L, "half a row waits for its line ending");
                File.AppendAllText(fresh, row4.Substring(40) + "\n", LineParser.Latin1);
                t.PollCdr(true);
                Eq(st.Sources[0].LegsAdded, 4L, "the rest of a file that grew");
                Eq(st.Sources[0].BadRecords, 0L, "and no row was read cut short");

                Directory.CreateDirectory(Path.Combine(dir, "other"));
                string g5 = "ABCD0005 11110000 2222 33334444";
                WriteCdr(Path.Combine(dir, "other"), "cdr.second-cube.11_09_2026_10_05_45.100", CRow(g5, 500, "answer", "5550001", "10:00:05.000", "10:01:05.000", 300, Twc(g5, 950, "E1"), null));
                t.PollCdr(true);
                Eq(st.Sources.Count, 2, "a CUBE that appears later");

                File.WriteAllText(Path.Combine(dir, "cdr.example-cube.01_14_2026_10_05_00.100"), "", LineParser.Latin1);
                Eq(Loader.FindCdr(dir, new DateTime(2026, 1, 15)).Count, 1, "the quick listing finds only a day either side");
                Check(Loader.FindCdr(dir, null).Count >= 5, "the full listing finds them all");
                t.Stop();
            }
            finally { Directory.Delete(dir, true); }
        }

        static void TestCdrRetention()
        {
            Group("call record files: cleanup");
            string dir = NewDir();
            try
            {
                DateTime now = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
                Func<string, int, string> file = delegate (string name, int days)
                {
                    string p = Path.Combine(dir, name);
                    File.WriteAllText(p, "12345");
                    File.SetLastWriteTimeUtc(p, now.AddDays(-days));
                    return p;
                };
                string old = file("cdr.a.10_01_2026_10_05_00.100", 40);
                string edge = file("cdr.a.10_11_2026_10_05_00.100", 29);
                string other = file("important.txt", 400);
                string syslog = file("cube-2026-01-01-10.log", 400);
                string bak = file("cdr.a.10_01_2026_10_05_00.100.bak", 400);
                Directory.CreateDirectory(Path.Combine(dir, "sub"));
                string under = file(Path.Combine("sub", "cdr.b.10_01_2026_10_05_00.100"), 40);
                long bytes; int failed;
                Eq(CdrRetention.Run(dir, 0, now, out bytes, out failed), 0, "0 days means keep them");
                Check(File.Exists(old), "nothing went");
                Eq(CdrRetention.Run(dir, 30, now, out bytes, out failed), 2, "older than 30 days");
                Eq(bytes, 10L, "bytes freed");
                Eq(failed, 0, "none failed");
                Check(!File.Exists(old) && !File.Exists(under), "the old ones, including one folder down");
                Check(File.Exists(edge), "a newer one stays");
                Check(File.Exists(other) && File.Exists(syslog) && File.Exists(bak), "anything not named like a CUBE's file stays, however old");

                AlarmEngine e = new AlarmEngine(Path.Combine(dir, "data"), delegate () { return (Store)null; });
                e.CdrRoot = dir; e.OnLog = delegate (string s) { };
                AlarmConfig c = new AlarmConfig(); c.KeepDays = 1;
                e.Use(c);
                e.Cleanup(now);
                Check(!File.Exists(edge), "the engine runs it with the saved days");
                string later = file("cdr.a.11_05_2026_10_05_00.100", 4);
                e.Cleanup(now.AddMinutes(10));
                Check(File.Exists(later), "not again within the hour");
                e.Cleanup(now.AddHours(1));
                Check(!File.Exists(later), "again after an hour");

                string err;
                Check(AlarmConfig.FromJson((Dictionary<string, object>)JsonRead.Parse("{\"intervalMinutes\":5,\"keepDays\":30}"), out err) != null, "saved setting");
                Eq(AlarmConfig.FromJson((Dictionary<string, object>)JsonRead.Parse("{\"intervalMinutes\":5,\"keepDays\":30}"), out err).KeepDays, 30, "read back");
                Check(AlarmConfig.FromJson((Dictionary<string, object>)JsonRead.Parse("{\"intervalMinutes\":5,\"keepDays\":-1}"), out err) == null, "a negative number is refused");
                Eq(AlarmConfig.FromJson((Dictionary<string, object>)JsonRead.Parse("{\"intervalMinutes\":5}"), out err).KeepDays, 0, "older settings files keep everything");
            }
            finally { Directory.Delete(dir, true); }
        }

        static void TestSyslogRetention()
        {
            Group("syslog files: cleanup");
            string dir = NewDir();
            try
            {
                DateTime now = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
                Func<string, int, string> file = delegate (string name, int days)
                {
                    string p = Path.Combine(dir, name);
                    Directory.CreateDirectory(Path.GetDirectoryName(p));
                    File.WriteAllText(p, "12345");
                    File.SetLastWriteTimeUtc(p, now.AddDays(-days));
                    return p;
                };
                string old = file("cube-2026-01-01-10.log", 300);
                string check = file("cube-2026-01-01-10.sha256", 300);
                string merged = file("cube-2026-01-01-11.merged.log", 300);
                string down1 = file(Path.Combine("a", "cube-2026-01-02-10.log"), 300);
                string down2 = file(Path.Combine("a", "b", "cube-2026-01-03-10.log"), 300);
                string down3 = file(Path.Combine("a", "b", "c", "cube-2026-01-04-10.log"), 300);
                string other = file("router-2026-01-01-10.log", 300);
                string writing = file("cube-2026-11-09-11.log", 0);
                string copiedIn = file("cube-2026-02-01-10.log", 1);
                string oddClock = file("cube-2026-11-08-10.log", 300);
                string[] stay =
                {
                    file("cube-2026-01-01-10 - Copy.log", 300), file("cube-2026-01-01-10.log1", 300), file("cube-2026-01-01-10.txt", 300),
                    file("cube-2026-05-01-10.sha256", 300), file("messages.log", 300), file("cube-2026-13-45-10.log", 300),
                    file("cdr.a.01_01_2026_10_05_00.100", 300)
                };
                LoadOptions feed = new LoadOptions(); feed.Root = dir;
                long bytes; int failed;
                Eq(SyslogRetention.Run(feed, 0, now, out bytes, out failed), 0, "0 days means keep them");
                Check(File.Exists(old), "nothing went");

                LoadOptions narrow = new LoadOptions(); narrow.Root = dir; narrow.Pattern = "nothing*.log";
                Eq(SyslogRetention.Run(narrow, 30, now, out bytes, out failed), 0, "files --pattern leaves out are not this tool's");
                narrow = new LoadOptions(); narrow.Root = dir; narrow.Cubes.Add("cube");
                Eq(SyslogRetention.Run(narrow, 30, now, out bytes, out failed), 4, "older than 30 days, for the CUBEs being read");
                Eq(bytes, 25L, "bytes freed, the check file included");
                Eq(failed, 0, "none failed");
                Check(!File.Exists(old) && !File.Exists(merged) && !File.Exists(down1) && !File.Exists(down2), "old ones go, two folders down at most");
                Check(!File.Exists(check), "the check file goes with its log");
                Check(File.Exists(down3), "the third folder down is not one CubeScope reads");
                Check(File.Exists(other), "another device's log stays when --cube leaves it out");
                Check(File.Exists(writing), "the file being written stays");
                Check(File.Exists(copiedIn), "an old log copied in yesterday stays");
                Check(File.Exists(oddClock), "a recent hour in the name stays, whatever its file date");
                foreach (string s in stay) Check(File.Exists(s), Path.GetFileName(s) + " is not an hourly log or has no real date: it stays");

                Eq(SyslogRetention.Run(feed, 30, now, out bytes, out failed), 1, "with no --cube, every source");
                Check(!File.Exists(other), "the other device's log goes then");

                string locked = file("cube-2026-03-01-10.log", 300);
                string lockedCheck = file("cube-2026-03-01-10.sha256", 300);
                using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    Eq(SyslogRetention.Run(feed, 30, now, out bytes, out failed), 0, "a file in use cannot go");
                    Eq(failed, 1, "and is counted");
                    Check(File.Exists(lockedCheck), "its check file stays too");
                }

                File.Delete(locked);
                File.Delete(lockedCheck);
                string oldLog = file("cube-2026-04-01-10.log", 300);
                string oldCdr = file("cdr.a.01_02_2026_10_05_00.100", 300);
                List<string> logged = new List<string>();
                AlarmEngine e = new AlarmEngine(Path.Combine(dir, "data"), delegate () { return (Store)null; });
                e.CdrRoot = dir; e.SyslogFeed = feed; e.OnLog = delegate (string s) { logged.Add(s); };
                AlarmConfig c = new AlarmConfig(); c.KeepSyslogDays = 30;
                e.Use(c);
                e.Cleanup(now);
                Check(!File.Exists(oldLog), "the engine runs it with the saved days");
                Check(File.Exists(oldCdr), "and call record files keep their own setting");
                Eq(logged.Count, 1, "written to the log");
                Check(logged[0].IndexOf("deleted 1 syslog file older than 30 days", StringComparison.Ordinal) >= 0, "says what went: " + logged[0]);

                string later = file("cube-2026-11-01-10.log", 8);
                e.Cleanup(now.AddMinutes(10));
                Check(File.Exists(later), "not again within the hour");
                AlarmConfig c2 = new AlarmConfig(); c2.KeepSyslogDays = 7; c2.KeepDays = 30;
                e.Use(c2);
                e.Cleanup(now.AddMinutes(11));
                Check(!File.Exists(later) && !File.Exists(oldCdr), "a changed setting runs at once, both kinds");
                AlarmConfig off = new AlarmConfig();
                e.Use(off);
                string kept = file("cube-2026-04-02-10.log", 300);
                e.Cleanup(now.AddHours(5));
                Check(File.Exists(kept), "back to 0, nothing goes");

                string err;
                Func<string, AlarmConfig> read = delegate (string json) { return AlarmConfig.FromJson((Dictionary<string, object>)JsonRead.Parse(json), out err); };
                Eq(read("{\"intervalMinutes\":5,\"keepSyslogDays\":90}").KeepSyslogDays, 90, "read back");
                Eq(read(c2.ToJsonText()).KeepSyslogDays, 7, "written and read back");
                Eq(read(c2.ToJsonText()).KeepDays, 30, "beside the call record setting");
                Check(read("{\"intervalMinutes\":5,\"keepSyslogDays\":-1}") == null, "a negative number is refused");
                Check(read("{\"intervalMinutes\":5,\"keepSyslogDays\":3651}") == null, "so is one too large");
                Eq(read("{\"intervalMinutes\":5}").KeepSyslogDays, 0, "older settings files keep everything");
            }
            finally { Directory.Delete(dir, true); }
        }

        static void TestCdrNoRecordsAlarm()
        {
            Group("alarm when call records stop");
            string dir = NewDir();
            string data = NewDir();
            try
            {
                WriteCdr(dir, CName, CRow(CGuid, 100, "answer", "5550001", "10:00:05.000", "10:01:00.000", 300, Twc(CGuid, 900, "A1"), null));
                Store st = ReadCdr(dir);
                List<string> got = new List<string>();
                List<AlarmInfo> infos = new List<AlarmInfo>();
                AlarmEngine e = new AlarmEngine(data, delegate () { return st; });
                e.Zone = "cube"; e.OnLog = delegate (string s) { };
                e.Deliver = delegate (int id, string level, string msg, AlarmInfo info) { got.Add(id + " " + level + " " + msg.Split('\n')[0]); infos.Add(info); return true; };
                AlarmConfig c = new AlarmConfig(); c.Enabled = true; c.EventLog = false; c.StaleMinutes = 30;
                e.Use(c);
                DateTime t = new DateTime(2026, 11, 9, 10, 1, 0);

                e.Evaluate(t.AddMinutes(10));
                Eq(got.Count, 0, "quiet while records are recent, with no DNIS rules at all");
                e.Evaluate(t.AddMinutes(31));
                Eq(got.Count, 1, "one alarm");
                Eq(got.Count > 0 ? got[0] : "", "1005 Error No call records from example-cube for 30 minutes or more", "says which CUBE");
                Check(infos.Count > 0 && infos[0].Key == "example-cube|records" && infos[0].Dnis == "records", "its key for the NMS");
                e.Evaluate(t.AddMinutes(40));
                Eq(got.Count, 1, "only once");

                AlarmEngine e2 = new AlarmEngine(data, delegate () { return st; });
                e2.Zone = "cube"; e2.OnLog = e.OnLog; e2.Deliver = e.Deliver;
                e2.Load(); e2.Use(c);
                e2.Evaluate(t.AddMinutes(45));
                Eq(got.Count, 1, "a restart does not repeat it");

                string g2 = "ABCD0002 11110000 2222 33334444";
                WriteCdr(dir, "cdr.example-cube.11_09_2026_11_00_00.100", CRow(g2, 200, "answer", "5550001", "10:00:05.000", "11:00:00.000", 300, Twc(g2, 920, "B1"), null));
                st = ReadCdr(dir);
                e2.Evaluate(new DateTime(2026, 11, 9, 11, 5, 0));
                Eq(got.Count, 2, "a clear when they return");
                Check(got.Count > 1 && got[1].StartsWith("1006 Information Cleared: call records from example-cube", StringComparison.Ordinal), "says so");

                e2.Evaluate(new DateTime(2026, 11, 9, 12, 0, 0));
                Eq(got.Count, 3, "stops again");
                AlarmConfig off = new AlarmConfig(); off.Enabled = true; off.EventLog = false; off.StaleMinutes = 0;
                e2.Use(off); e2.Evaluate(new DateTime(2026, 11, 9, 12, 5, 0));
                Eq(got.Count, 4, "turning the warning off closes it");
                Check(got.Count > 3 && got[3].StartsWith("1006", StringComparison.Ordinal), "with a clear, so the NMS is not left holding it");
                AlarmConfig zero = new AlarmConfig(); zero.Enabled = true; zero.EventLog = false;
                e2.Use(zero); e2.Evaluate(new DateTime(2026, 11, 10, 12, 5, 0));
                Eq(got.Count, 4, "off by default");
            }
            finally { Directory.Delete(dir, true); Directory.Delete(data, true); }
        }

        static void TestNoRecordsWhatCounts()
        {
            Group("alarm when call records stop: what counts");
            string dir = NewDir(), dir2 = NewDir(), data = NewDir(), data2 = NewDir();
            try
            {
                WriteCdr(dir, CName, CRow(CGuid, 100, "answer", "5550001", "10:00:05.000", "10:01:00.000", 300, Twc(CGuid, 900, "A1"), null));
                File.WriteAllText(Path.Combine(dir, "notes.log"), "some notes someone left here\r\n");
                File.WriteAllText(Path.Combine(dir, "router-2026-11-09-10.log"), "1: 2: Nov  9 10:00:00.000: %SYS-5-CONFIG_I: Configured from console\r\n");
                File.WriteAllText(Path.Combine(dir, "example-cube-2026-11-09-10.log"), "7: 8: Nov  9 10:25:00.000: %SYS-5-CONFIG_I: Configured from console\r\n");
                Store st = ReadCdr(dir);
                Eq(st.Sources.Count, 3, "the CUBE, the router and the stray file");
                List<string> got = new List<string>();
                Func<int, string, string, AlarmInfo, bool> deliver = delegate (int id, string level, string msg, AlarmInfo info) { got.Add(id + " " + msg.Split('\n')[0]); return true; };
                Func<string, Func<Store>, AlarmEngine> engine = delegate (string where, Func<Store> store)
                {
                    AlarmEngine a = new AlarmEngine(where, store);
                    a.Zone = "cube"; a.OnLog = delegate (string s) { }; a.Deliver = deliver;
                    return a;
                };
                AlarmConfig c = new AlarmConfig(); c.Enabled = true; c.EventLog = false; c.StaleMinutes = 30;
                DateTime t = new DateTime(2026, 11, 9, 10, 1, 0);

                AlarmEngine e = engine(data, delegate () { return st; });
                e.Use(c);
                e.Evaluate(t.AddMinutes(31));
                Eq(got.Count, 1, "one alarm");
                Eq(got.Count > 0 ? got[0] : "", "1005 No call records from example-cube for 30 minutes or more", "for the CUBE, though it logged something 7 minutes ago");
                e.Evaluate(t.AddMinutes(200));
                Eq(got.Count, 1, "the router and the stray file never alarm, however long they are quiet");
                Json j = new Json(4096); e.WriteStatusJson(j);
                Check(j.ToString().IndexOf("no call records ever read from", StringComparison.Ordinal) >= 0 && j.ToString().IndexOf("router", StringComparison.Ordinal) >= 0, "the page names what is not watched");
                Check(File.Exists(e.RecordsSeenFile), "what has sent records is remembered");

                File.WriteAllText(Path.Combine(dir2, "example-cube-2026-11-09-14.log"), "9: 9: Nov  9 14:00:00.000: %SYS-5-CONFIG_I: Configured from console\r\n");
                Store noRecords = ReadCdr(dir2);
                DateTime restart = new DateTime(2026, 11, 9, 14, 5, 0);
                AlarmEngine e2 = engine(data, delegate () { return noRecords; });
                e2.Load(); e2.Use(c);
                e2.Evaluate(restart);
                Eq(got.Count, 1, "restarted with no records loaded: still open, no false clear");
                File.Delete(e2.StateFile);
                AlarmEngine e3 = engine(data, delegate () { return noRecords; });
                e3.Load(); e3.Use(c);
                e3.Evaluate(restart);
                Eq(got.Count, 2, "a CUBE known to send records alarms even with none loaded");
                Check(got.Count > 1 && got[1].StartsWith("1005", StringComparison.Ordinal), "raised, not cleared");

                File.WriteAllText(Path.Combine(data2, "alarm-state.json"), "{\"router|records\":\"2026-11-09T10:00:00.0000000Z\"}");
                AlarmEngine e4 = engine(data2, delegate () { return st; });
                e4.Load(); e4.Use(c);
                e4.Evaluate(t.AddMinutes(10));
                Eq(got.Count, 3, "a false alarm left open by an earlier version");
                Check(got.Count > 2 && got[2].StartsWith("1006 Cleared: no longer watching the call records of router", StringComparison.Ordinal), "gets its clear");
                e4.Evaluate(t.AddMinutes(11));
                Eq(got.Count, 3, "once");
            }
            finally { Directory.Delete(dir, true); Directory.Delete(dir2, true); Directory.Delete(data, true); Directory.Delete(data2, true); }
        }

        static void TestCdrServiceStep()
        {
            Group("admin: running as a service");
            bool busy;
            ReceiverSettings s = GoodSettings("Pw-123456"); s.RunService = true;
            SimulatedReceiverOps o = new SimulatedReceiverOps();
            ReceiverAdmin a = new ReceiverAdmin(o, true, "t");
            a.Apply(s, out busy);
            Eq(string.Join(",", o.Calls), "install,start,create,folder,write,test,restart,firewall,service", "after everything else");
            Eq(o.ServiceFolder, s.Folder, "reading the receiver's folder");
            o.Calls.Clear();
            a.Apply(GoodSettings(""), out busy);
            Eq(o.Calls.Count, 0, "not installed twice");

            SimulatedReceiverOps other = new SimulatedReceiverOps();
            other.ServiceFolder = @"D:\Syslog";
            ReceiverSettings s2 = GoodSettings("Pw-123456"); s2.RunService = true;
            ReceiverReview r = new ReceiverAdmin(other, true, "t").Apply(s2, out busy);
            Check(!other.Calls.Contains("service"), "a service reading another folder is left alone");
            Check(r.Notes.Count == 1 && r.Notes[0].IndexOf(@"D:\Syslog", StringComparison.Ordinal) > 0, "and the page says so");
            SimulatedReceiverOps none = new SimulatedReceiverOps();
            new ReceiverAdmin(none, true, "t").Apply(GoodSettings("Pw-123456"), out busy);
            Check(!none.Calls.Contains("service"), "not unless ticked");
        }
    }
}
