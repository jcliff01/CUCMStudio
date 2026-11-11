using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CubeScope
{
    /// <summary>One CUBE reported by both syslog and call record files. Every number and GUID here is invented.</summary>
    public static partial class SelfTest
    {
        const string PairHost = "example-cube", PairAddress = "10.9.9.9", PairLog = "10.9.9.9-2026-11-09-10.log";
        const string PairCdrAfter = "cdr.example-cube.11_09_2026_10_05_00.100", PairCdrBefore = "cdr.example-cube.11_09_2026_09_30_00.100";
        static int _pairLine = 1000, _pairIos = 5000;

        static string PairGuid(int i) { return "ABCD000" + i.ToString(CultureInfo.InvariantCulture) + " 11110000 2222 33334444"; }

        static string PairPadded(string guid)
        {
            StringBuilder b = new StringBuilder(32);
            foreach (string w in guid.Split(' ')) b.Append(w.PadLeft(8, '0'));
            return b.ToString();
        }

        /// <summary>The lines a CUBE sends for one leg with gw-accounting syslog stats, in the form seen live: one line each.</summary>
        static string[] PairLegLines(string guid, int origin, string peer, string setup, string connect, string disc, int packets, int fid, string legHex, string jitter)
        {
            string id = guid.Replace(" ", ""), bytes = (packets * 200).ToString(CultureInfo.InvariantCulture), head = "*Nov  9 " + disc + " UTC: ";
            Func<string> numbers = delegate () { return (++_pairLine) + ": " + (++_pairIos) + ": "; };
            List<string> l = new List<string>();
            l.Add(numbers() + head + "%VOIPAAA-5-VOIP_CALL_HISTORY: CallLegType 2, ConnectionId " + id + ", SetupTime " + CTime(setup) + ", PeerAddress " + peer +
                  ", PeerSubAddress , DisconnectCause 10  , DisconnectText normal call clearing (16), ConnectTime " + CTime(connect) + ", DisconnectTime " + CTime(disc) +
                  ", CallOrigin " + origin + ", ChargedUnits 0, InfoType 2, TransmitPackets " + packets + ", TransmitBytes " + bytes + ", ReceivePackets " + packets + ", ReceiveBytes " + bytes);
            l.Add(numbers() + head + "%VOIPAAA-5-VOIP_FEAT_HISTORY: FEAT_VSA=fn:TWC,ft:11/09/2026 10:00:00.000,cgn:5550001,cdn:5550002,frs:0,fid:" + fid +
                  ",fcid:" + id + ",legID:" + legHex + ",bguid:" + PairPadded(guid));
            if (jitter != null)
            {
                l.Add(numbers() + head + "VOIP_CALL_STATISTICS:DSP/TX: PK=0, SG=0, NS=0, DU=0, VO=0");
                foreach (string d in new[] { "DSP/RX: PK=0, SG=0, CF=0, RX=0, VO=0, BS=0, BP=0, LP=0, EP=0", "DSP/PD: CU=0, MI=0, MA=0, CO=0, IJ=" + jitter,
                                             "DSP/PE: PC=0, IC=0, SC=0, RM=0, BO=0, EE=0", "DSP/LE: TP=0, TX=0, RP=0, RM=0, BN=0, ER=0, AC=0", "DSP/ER: RD=0, TD=0, RC=0, TC=0", "DSP/IC: IC=0" })
                    l.Add((++_pairLine) + ": " + d);
            }
            return l.ToArray();
        }

        static string PairDisc(int i) { return "10:01:0" + i.ToString(CultureInfo.InvariantCulture) + ".000"; }

        /// <summary>Call i's two legs as syslog: shift moves the answer time, as another CUBE on the same call would see it.</summary>
        static string PairSyslog(int i, int shiftMs)
        {
            string g = PairGuid(i), connect = "10:00:05." + shiftMs.ToString("000", CultureInfo.InvariantCulture);
            List<string> l = new List<string>();
            l.AddRange(PairLegLines(g, 2, "5550001", "10:00:00.000", connect, PairDisc(i), 300 + i, 2000 + i * 2, "A" + i, "0.0468"));
            l.AddRange(PairLegLines(g, 1, "5550002", "10:00:00.000", connect, PairDisc(i), 300 + i, 2001 + i * 2, "B" + i, null));
            return string.Join("\n", l.ToArray()) + "\n";
        }

        static string[] PairRows(int i)
        {
            string g = PairGuid(i);
            Dictionary<int, string> extra = new Dictionary<int, string> { { 26, "20" }, { 71, "\"192.0.2.10\"" }, { 37, "16500" } };
            return new[]
            {
                CRow(g, 100 + i * 2, "answer", "5550001", "10:00:05.000", PairDisc(i), 300 + i, Twc(g, 2000 + i * 2, "A" + i), extra),
                CRow(g, 101 + i * 2, "originate", "5550002", "10:00:05.000", PairDisc(i), 300 + i, Twc(g, 2001 + i * 2, "B" + i), extra)
            };
        }

        static void WritePair(string dir, string cdrName, int[] inFiles, int[] inSyslog, int shiftMs)
        {
            List<string> rows = new List<string>();
            foreach (int i in inFiles) rows.AddRange(PairRows(i));
            if (rows.Count > 0) WriteCdr(dir, cdrName, rows.ToArray());
            StringBuilder log = new StringBuilder();
            foreach (int i in inSyslog) log.Append(PairSyslog(i, shiftMs));
            string p = Path.Combine(dir, PairLog);
            File.WriteAllText(p, log.ToString(), LineParser.Latin1);
            Old(p);
        }

        static Call PairCall(Store st, int i)
        {
            string k = GuidForm.Key(PairGuid(i));
            foreach (Call c in st.Calls) if (c.Key == k) return c;
            return null;
        }

        static Leg AnsweredLeg(Call c)
        {
            foreach (Leg g in c.Legs) if (g.In) return g;
            return null;
        }

        /// <summary>The checks that hold whichever feed's files are read first.</summary>
        static void CheckPair(Store st, string how)
        {
            Eq(st.Sources.Count, 1, how + ": one CUBE");
            if (st.Sources.Count != 1) return;
            Source s = st.Sources[0];
            Eq(s.Name, PairHost, how + ": under its host name");
            Eq(string.Join(",", s.AlsoNames.ToArray()), PairAddress, how + ": and the page can say what else it is called");
            Eq(st.Calls.Count, 5, how + ": each call once");
            Eq(s.LegsAdded, 10L, how + ": each leg once");
            Eq(s.LegsInBoth, 6L, how + ": legs in both feeds");
            Eq(s.LegsFromFiles - s.LegsInBoth, 2L, how + ": legs only in the files");
            Eq(s.LegsFromSyslog - s.LegsInBoth, 2L, how + ": legs only in syslog");
            Eq(s.DuplicateLegs, 0L, how + ": the other feed's copy is not a duplicate to report");
            Eq(s.DuplicateFeatures, 0L, how + ": nor are its features");
            Eq(s.Unbound, 0L, how + ": every leg has its TWC record");
            Eq(s.Stubs, 0L, how + ": and no TWC record made a leg of its own");
            LegDetail both = s.Details[AnsweredLeg(PairCall(st, 0))];
            Eq(both.DialPeer, 20, how + ": a leg in both has the file's dial-peer");
            Eq(both.MediaAddress, "192.0.2.10", how + ": its far-end media address");
            Check(both.Quality != null && both.Quality.Jitter == 0.0468, how + ": and syslog's jitter");
            Check(PairCall(st, 0).Padded != null, how + ": the full GUID syslog alone carries is kept, for matching failure traces");
            string window = Query.CallJson(st, PairCall(st, 0), true, null);
            Check(window.IndexOf("\"cols\"", StringComparison.Ordinal) > 0 && window.IndexOf("peer-id", StringComparison.Ordinal) > 0, how + ": the call window can list the file's columns");
            LegDetail fileOnly = s.Details[AnsweredLeg(PairCall(st, 3))];
            Check(fileOnly.DialPeer == 20 && fileOnly.Quality == null, how + ": a leg only in the files has what the files say");
            LegDetail syslogOnly = s.Details[AnsweredLeg(PairCall(st, 4))];
            Check(syslogOnly.DialPeer == -1 && syslogOnly.Quality != null, how + ": a leg only in syslog has what syslog says");
        }

        static void TestRedact()
        {
            Group("passwords in log messages");
            const string Pw = "Pa55.word";
            string line = "17825: 56074843: *Nov  9 22:00:10.388: %VOICE_FILE_ACCT-3-DUMPFAIL: Could not dump to remote file - write to sftp://cube-cdr:" + Pw +
                          "@192.0.2.1:2121/cdr. Error=-1(Unknown error -1)";
            Eq(Redact.Secrets(line).IndexOf(Pw, StringComparison.Ordinal), -1, "the password is hidden");
            Check(Redact.Secrets(line).IndexOf("sftp://cube-cdr:****@192.0.2.1:2121/cdr. Error=-1", StringComparison.Ordinal) > 0, "the user, server, port and path stay");
            Eq(Redact.Secrets("ftp://u:p@ss@192.0.2.1/cdr"), "ftp://u:****@192.0.2.1/cdr", "a password holding an @");
            Eq(Redact.Secrets("INVITE sip:5550001@192.0.2.1:5060 SIP/2.0"), "INVITE sip:5550001@192.0.2.1:5060 SIP/2.0", "a SIP address is not a login");
            Eq(Redact.Secrets("see http://192.0.2.1:8080/x and sftp://user@192.0.2.1/y"), "see http://192.0.2.1:8080/x and sftp://user@192.0.2.1/y", "a URL with no password is left alone");
            LineParser p = new LineParser(0, 2026, 11);
            p.Feed(line, 1);
            p.Flush();
            string shown = null;
            foreach (object o in p.Take().Records) { SysEvent e = o as SysEvent; if (e != null) shown = e.Text; }
            Check(shown != null && shown.IndexOf(Pw, StringComparison.Ordinal) < 0 && shown.IndexOf("DUMPFAIL", StringComparison.Ordinal) < 0 && shown.IndexOf("****", StringComparison.Ordinal) > 0,
                  "the dashboard's copy of the message never holds it");
        }

        static void TestTwoFeeds()
        {
            Group("one CUBE, two feeds");
            Eq(GuidForm.Key("ABCD0001 11110000 2222 33334444"), "ABCD0001111100002222" + "33334444", "the file's GUID is syslog's without the spaces");
            Eq(GuidForm.Key("ABCD0001111100000000222233334444"), "ABCD0001111100002222" + "33334444", "and the full form still comes to the same");

            List<string> dirs = new List<string>();
            Func<string> fresh = delegate () { string d = NewDir(); dirs.Add(d); return d; };
            try
            {
                int[] filesHave = { 0, 1, 2, 3 }, syslogHas = { 0, 1, 2, 4 };
                string a = fresh();
                WritePair(a, PairCdrAfter, filesHave, syslogHas, 0);
                CheckPair(ReadCdr(a), "syslog read first");
                string b = fresh();
                WritePair(b, PairCdrBefore, filesHave, syslogHas, 0);
                CheckPair(ReadCdr(b), "files read first");

                // syslog's records for a leg can straddle a read, with a file arriving in between
                {
                    Store st = new Store();
                    Source s = st.AddSource("cube", "");
                    string[] lines = PairLegLines(PairGuid(0), 2, "5550001", "10:00:00.000", "10:00:05.000", PairDisc(0), 300, 2000, "A0", "0.0468");
                    LineParser p = new LineParser(0, 2026, 11);
                    p.Feed(lines[0], 1);
                    st.Ingest(s, p.Take());
                    string f = fresh();
                    WriteCdr(f, PairCdrAfter, PairRows(0)[0]);
                    st.Ingest(s, CdrParser.ParseFile(Path.Combine(f, PairCdrAfter), 1, 0, 0, false));
                    for (int i = 1; i < lines.Length; i++) p.Feed(lines[i], i + 1);
                    p.Flush();
                    st.Ingest(s, p.Take());
                    st.Settle(s, true);
                    Eq(st.Calls.Count, 1, "interleaved: one call");
                    Eq(st.Calls[0].Legs.Count, 1, "interleaved: one leg");
                    Eq(s.Stubs, 0L, "interleaved: the TWC record found its leg");
                    Eq(s.Unbound, 0L, "interleaved: nothing left waiting");
                    LegDetail d = s.Details[st.Calls[0].Legs[0]];
                    Check(d.DialPeer == 20 && d.Quality != null && d.Quality.Jitter == 0.0468, "interleaved: a stats block arriving later still lands on the kept leg");
                }

                // the two feeds can give the same moment a few milliseconds apart, on any leg
                {
                    string near = fresh();
                    List<string> rows = new List<string>();
                    for (int i = 0; i < 5; i++) rows.AddRange(PairRows(i));
                    rows.Add(CRow(PairGuid(3), 106, "answer", "5550001", "10:00:05.000", PairDisc(3), 303, Cfa(PairGuid(3), 9000, "A3"), null));
                    rows.Add("1794255059");
                    WriteCdr(near, PairCdrAfter, rows.ToArray());
                    File.WriteAllText(Path.Combine(near, PairLog), PairSyslog(0, 0) + PairSyslog(1, 0) + PairSyslog(2, 0) + PairSyslog(3, 1) + PairSyslog(4, 10), LineParser.Latin1);
                    Store st = ReadCdr(near);
                    Eq(st.Sources.Count, 1, "a few ms apart: one CUBE");
                    Source s = st.Sources[0];
                    Eq(s.LegsAdded, 10L, "a few ms apart: each leg once");
                    Eq(s.LegsInBoth, 10L, "a few ms apart: all in both feeds");
                    Eq(s.Unbound, 0L, "a few ms apart: every leg has its TWC record");
                    Eq(s.Features, 1L, "a few ms apart: a repeated row still brings its feature, once");
                    Eq(s.BadRecords, 0L, "the CUBE's five-minute time mark is not a bad record");
                    Eq(s.Warnings.Count, 0, "and raises no warning");
                    LegDetail d = s.Details[AnsweredLeg(PairCall(st, 4))];
                    Check(d.DialPeer == 20 && d.Quality != null && d.Quality.Jitter == 0.0468, "a few ms apart: the leg has both feeds' details");

                    string far = fresh();
                    rows = new List<string>();
                    for (int i = 0; i < 3; i++) rows.AddRange(PairRows(i));
                    rows.AddRange(PairRows(5));
                    WriteCdr(far, PairCdrAfter, rows.ToArray());
                    File.WriteAllText(Path.Combine(far, PairLog), PairSyslog(0, 0) + PairSyslog(1, 0) + PairSyslog(2, 0) + PairSyslog(5, 100), LineParser.Latin1);
                    Store fs = ReadCdr(far);
                    Eq(fs.Sources.Count, 1, "100 ms apart: the feeds still pair on the exact legs");
                    Eq(fs.Sources[0].LegsInBoth, 6L, "100 ms apart: that leg is a different leg");
                }

                string none = fresh();
                WritePair(none, PairCdrAfter, new[] { 0, 1, 2 }, new[] { 3, 4 }, 0);
                Eq(ReadCdr(none).Sources.Count, 2, "no call in common: two CUBEs, as far as anyone can tell");

                string chain = fresh();
                WritePair(chain, PairCdrAfter, new[] { 0, 1, 2 }, new[] { 0, 1, 2 }, 5);
                Eq(ReadCdr(chain).Sources.Count, 2, "same GUIDs 5 ms apart is another CUBE on the same call, not this one");

                string two = fresh();
                List<string> rowsA = new List<string>(), rowsB = new List<string>();
                for (int i = 0; i < 3; i++) { rowsA.AddRange(PairRows(i)); rowsB.AddRange(PairRows(i + 3)); }
                WriteCdr(two, "cdr.cube-a.11_09_2026_10_05_00.100", rowsA.ToArray());
                WriteCdr(two, "cdr.cube-b.11_09_2026_10_05_00.100", rowsB.ToArray());
                StringBuilder relay = new StringBuilder();
                for (int i = 0; i < 6; i++) relay.Append(PairSyslog(i, 0));
                File.WriteAllText(Path.Combine(two, PairLog), relay.ToString(), LineParser.Latin1);
                Eq(ReadCdr(two).Sources.Count, 3, "syslog holding two CUBEs' calls (a relay) is joined to neither");

                string keep = fresh(), keptIn = Path.Combine(fresh(), "cube-links.json");
                WritePair(keep, PairCdrAfter, new[] { 0, 1, 2 }, new[] { 0, 1, 2 }, 0);
                CubeLinks links = new CubeLinks(keptIn);
                Eq(Loader.LoadAll(new LoadOptions { Root = keep, Days = 0, Quiet = true, Links = links }, null).Sources.Count, 1, "found");
                Check(File.Exists(keptIn), "and kept");
                CubeLinks again = new CubeLinks(keptIn);
                Eq(again.Resolve(PairAddress), PairHost, "read back after a restart");
                Eq(again.Resolve("10.9.9.8"), "10.9.9.8", "another name is left alone");
                Eq(again.OtherNames(PairHost).Count, 1, "both ways");
                string apart = fresh();
                WritePair(apart, PairCdrAfter, new[] { 0 }, new[] { 4 }, 0);
                Eq(Loader.LoadAll(new LoadOptions { Root = apart, Days = 0, Quiet = true, Links = again }, null).Sources.Count, 1, "known links hold when the two do not overlap");

                LoadOptions want = new LoadOptions { Links = again };
                want.Cubes.Add(PairHost);
                Check(Loader.Wanted(want, PairAddress), "--cube by host name takes in its syslog");
                LoadOptions want2 = new LoadOptions { Links = again };
                want2.Cubes.Add(PairAddress);
                Check(Loader.Wanted(want2, PairHost), "--cube by address takes in its files");
                Check(!Loader.Wanted(want2, "another-cube"), "and nothing else");

                // a GUID used twice, 6 hours apart, with the file for the first use read after syslog for both
                string reuse = fresh(), reuseLinks = Path.Combine(fresh(), "cube-links.json");
                File.WriteAllText(reuseLinks, "{\"" + PairAddress + "\":\"" + PairHost + "\"}");
                string g = PairGuid(0);
                File.WriteAllText(Path.Combine(reuse, PairLog), string.Join("\n", PairLegLines(g, 2, "5550001", "10:00:00.000", "10:00:05.000", PairDisc(0), 300, 2000, "A0", null)) + "\n", LineParser.Latin1);
                File.WriteAllText(Path.Combine(reuse, "10.9.9.9-2026-11-09-16.log"), string.Join("\n", PairLegLines(g, 2, "5550009", "16:00:00.000", "16:00:05.000", "16:01:00.000", 900, 7000, "C0", null)) + "\n", LineParser.Latin1);
                WriteCdr(reuse, "cdr.example-cube.11_09_2026_20_00_00.100", PairRows(0)[0]);
                Store rs = Loader.LoadAll(new LoadOptions { Root = reuse, Days = 0, Quiet = true, Links = new CubeLinks(reuseLinks) }, null);
                Eq(rs.Calls.Count, 2, "a reused GUID stays two calls");
                Check(rs.Calls[0].Legs.Count == 1 && rs.Calls[1].Legs.Count == 1, "the late file's leg did not land on the newer call");
                Check(rs.Calls[0].Legs[0].InBoth && !rs.Calls[1].Legs[0].InBoth, "it matched the earlier use");

                // following: two feeds arriving at their own pace
                string live = fresh();
                string logPath = Path.Combine(live, PairLog);
                WritePair(live, PairCdrAfter, new[] { 0, 1, 2 }, new[] { 0, 1, 2 }, 0);
                string more = PairSyslog(3, 0);
                File.AppendAllText(logPath, more.Substring(0, 40), LineParser.Latin1);
                Old(logPath);
                LoadOptions o = new LoadOptions { Root = live, Days = 0, Quiet = true, Live = true, Links = new CubeLinks(null) };
                Store ls = Loader.LoadAll(o, null);
                Tailer t = new Tailer(ls, o);
                t.IntervalMs = 3600000;
                t.Start();
                Eq(ls.Sources.Count, 1, "following: one CUBE");
                Eq(ls.Sources[0].LegsAdded, 6L, "following: what the load read");
                File.AppendAllText(logPath, more.Substring(40), LineParser.Latin1);
                t.Poll();
                Eq(ls.Sources.Count, 1, "following: still one after syslog moves on");
                Eq(ls.Sources[0].LegsAdded, 8L, "following: the line half written at the load is read whole");
                Eq(ls.Sources[0].BadRecords, 0L, "following: and not read cut short");
                WriteCdr(live, "cdr.example-cube.11_09_2026_10_06_00.100", PairRows(3));
                t.PollCdr(true);
                Eq(ls.Sources.Count, 1, "following: the file adds no CUBE");
                Eq(ls.Sources[0].LegsAdded, 8L, "following: nor any leg");
                Eq(ls.Sources[0].LegsInBoth, 8L, "following: its legs join syslog's");
                Check(ls.Sources[0].Details[AnsweredLeg(PairCall(ls, 3))].DialPeer == 20, "following: and bring their detail");
                t.Stop();

                // the no-records alarm: syslog still arriving must not hide a failed file delivery
                string al = fresh(), data = fresh();
                WritePair(al, PairCdrAfter, new[] { 0, 1, 2 }, new[] { 0, 1, 2 }, 0);
                File.WriteAllText(Path.Combine(al, "10.9.9.9-2026-11-09-11.log"), string.Join("\n", PairLegLines(PairGuid(5), 2, "5550001", "11:00:00.000", "11:00:05.000", "11:01:00.000", 300, 3000, "D5", null)) + "\n", LineParser.Latin1);
                Store ast = ReadCdr(al);
                Eq(ast.Sources.Count, 1, "alarm: one CUBE");
                List<string> got = new List<string>();
                AlarmEngine e = new AlarmEngine(data, delegate () { return ast; });
                e.Zone = "cube"; e.OnLog = delegate (string x) { };
                e.Deliver = delegate (int id, string level, string msg, AlarmInfo info) { got.Add(id + " " + msg); return true; };
                AlarmConfig cfg = new AlarmConfig(); cfg.Enabled = true; cfg.EventLog = false; cfg.StaleMinutes = 30;
                e.Use(cfg);
                e.Evaluate(new DateTime(2026, 11, 9, 10, 20, 0));
                Eq(got.Count, 0, "alarm: both feeds recent");
                e.Evaluate(new DateTime(2026, 11, 9, 11, 5, 0));
                Eq(got.Count, 1, "alarm: the files stopped although syslog goes on");
                Check(got.Count > 0 && got[0].StartsWith("1005 No call records from example-cube", StringComparison.Ordinal), "alarm: says which CUBE");
                Check(got.Count > 0 && got[0].IndexOf("The call record files have stopped; syslog is still arriving.", StringComparison.Ordinal) > 0, "alarm: and which feed");
                WriteCdr(al, "cdr.example-cube.11_09_2026_11_20_00.100", CRow(PairGuid(6), 900, "answer", "5550001", "11:19:05.000", "11:20:00.000", 300, Twc(PairGuid(6), 4000, "E6"), null));
                ast = ReadCdr(al);
                e.Evaluate(new DateTime(2026, 11, 9, 11, 25, 0));
                Eq(got.Count, 2, "alarm: cleared when the files return");
                Check(got.Count > 1 && got[1].StartsWith("1006", StringComparison.Ordinal), "alarm: a clear");
                e.Evaluate(new DateTime(2026, 11, 9, 11, 40, 0));
                Eq(got.Count, 3, "alarm: and syslog stopping is noticed too");
                Check(got.Count > 2 && got[2].IndexOf("Syslog has stopped; the call record files are still arriving.", StringComparison.Ordinal) > 0, "alarm: says so");

                AlarmConfig byAddress = OneRule("5550002");
                byAddress.Rules[0].Cubes.Add(PairAddress);
                AlarmEngine e2 = new AlarmEngine(fresh(), delegate () { return ast; });
                e2.Zone = "cube"; e2.OnLog = delegate (string x) { }; e2.Deliver = delegate (int id, string level, string msg, AlarmInfo info) { return true; };
                e2.Use(byAddress);
                e2.Evaluate(new DateTime(2026, 11, 9, 11, 25, 0));
                Json j = new Json(4096); e2.WriteStatusJson(j);
                Check(j.ToString().IndexOf("reads no CUBE named", StringComparison.Ordinal) < 0, "a rule that names the address still finds the merged CUBE");
            }
            finally { foreach (string d in dirs) Directory.Delete(d, true); }
        }
    }
}
