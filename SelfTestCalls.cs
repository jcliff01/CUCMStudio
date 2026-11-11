using System;
using System.Collections.Generic;
using System.IO;

namespace CubeScope
{
    /// <summary>What the call list and a call's detail say beyond the call itself: where its details came from,
    /// the packet rate behind a low-rate verdict, how other calls to its number fared, causes in words.</summary>
    public static partial class SelfTest
    {
        static Call ByKey(Store st, string key)
        {
            for (int i = 0; i < st.Calls.Count; i++) if (st.Calls[i].Key == key) return st.Calls[i];
            return null;
        }

        static void TestCallFacts()
        {
            Group("call list: where each call's details came from");
            const string K1 = "ABCD000111110000222233334444", K2 = "ABCD000211110000222233334444";
            LineParser p = new LineParser(0, 2026, 11);
            int n = 0;
            Action<string> feed = delegate (string l) { p.Feed(l, ++n); };
            feed(SLeg(100, 1000, K1)); feed(STwc(101, 1001, K1, 900));
            foreach (string l in SBlock(102, 1002, "0.0000", "0", null)) feed(l);        // measuring is on, nothing measured
            feed(SLeg(110, 1003, K2)); feed(STwc(111, 1004, K2, 901));
            feed("120: 1010: Nov  9 16:16:14.000 CST: %SYS-5-CONFIG_I: Configured from console by test");
            p.Flush();
            Store st = new Store();
            Source s = st.AddSource("cube", "");
            st.Ingest(s, p.Take());
            st.Settle(s, true); st.DeriveDirty();
            Call a = ByKey(st, K1), b = ByKey(st, K2);
            Check(a != null && b != null, "both calls read");
            if (a == null || b == null) return;
            Eq(Query.Feeds(st, a), Query.FeedSyslog | Query.FeedMedia, "a block of zeros still says media statistics are on");
            Eq(Query.Feeds(st, b), Query.FeedSyslog, "no block: syslog only");
            Check(s.Details == null || !s.Details.ContainsKey(a.Legs[0]), "and the zeros are still not kept as a measurement");
            Json j = new Json(1024);
            Query.WriteCallRow(j, st, a);
            Check(j.ToString().IndexOf("\"feeds\":5", StringComparison.Ordinal) > 0, "the list row carries it");

            string dir = NewDir();
            try
            {
                const string G1 = "ABCD0101 11110000 2222 33334444", G2 = "ABCD0102 11110000 2222 33334444", G3 = "ABCD0103 11110000 2222 33334444";
                Dictionary<int, string> rejected = new Dictionary<int, string> { { 11, "\"15  \"" }, { 12, "\"call rejected (21)\"" } };
                Dictionary<int, string> lost = new Dictionary<int, string> { { 49, "5" } };
                WriteCdr(dir, CName,
                    // an answered call, measured: 5 packets lost
                    CRow(G1, 100, "answer", "5550001", "10:00:05.000", "10:01:05.000", 3000, Twc(G1, 900, "A1"), lost),
                    CRow(G1, 101, "originate", "5550002", "10:00:05.000", "10:01:05.000", 3000, Twc(G1, 901, "A2"), null),
                    // the same number, rejected
                    CRow(G2, 102, "answer", "5550001", "10:00:01.000", "10:00:01.000", 0, Twc(G2, 902, "B1"), rejected),
                    CRow(G2, 103, "originate", "5550002", "10:00:01.000", "10:00:01.000", 0, Twc(G2, 903, "B2"), rejected),
                    // the same number, answered, 600 packets in 60 s each way: 10 a second
                    CRow(G3, 104, "answer", "5550001", "10:00:05.000", "10:01:05.000", 600, Twc(G3, 904, "C1"), null),
                    CRow(G3, 105, "originate", "5550002", "10:00:05.000", "10:01:05.000", 600, Twc(G3, 905, "C2"), null));
                Store fs = ReadCdr(dir);
                fs.DeriveDirty();
                Call c1 = ByKey(fs, G1.Replace(" ", "")), c2 = ByKey(fs, G2.Replace(" ", "")), c3 = ByKey(fs, G3.Replace(" ", ""));
                Check(c1 != null && c2 != null && c3 != null, "three file calls read");
                if (c1 == null || c2 == null || c3 == null) return;
                Eq(Query.Feeds(fs, c1), Query.FeedFiles | Query.FeedMedia, "a file with a measured value: files and media");
                Eq(Query.Feeds(fs, c3), Query.FeedFiles, "a file with every quality column zero: files only");

                Group("call list: the packet rate behind low rate");
                Eq(c3.Media, Media.LowRate, "10 packets a second reads as low");
                Json r3 = new Json(1024);
                Query.WriteCallRow(r3, fs, c3);
                string row = r3.ToString();
                Check(row.IndexOf("\"ptx\":10,", StringComparison.Ordinal) > 0 && row.IndexOf("\"prx\":10,", StringComparison.Ordinal) > 0, "packets a second each way: " + row);
                Check(row.IndexOf("\"psec\":60", StringComparison.Ordinal) > 0, "over the 60 s it talked");
                Json r1 = new Json(1024);
                Query.WriteCallRow(r1, fs, c1);
                Check(r1.ToString().IndexOf("\"ptx\"", StringComparison.Ordinal) < 0, "nothing added to a two-way call");

                Group("call detail: other calls to the same number");
                string json = Query.CallJson(fs, c1, false);
                Check(json.IndexOf("\"same\":{\"number\":\"5550002\",\"calls\":3", StringComparison.Ordinal) > 0, "three calls to 5550002: " + json.Substring(0, Math.Min(400, json.Length)));
                Check(json.IndexOf("\"failed\":1,\"answered\":2", StringComparison.Ordinal) > 0, "one failed, two answered");
                Check(json.IndexOf("\"rejected\":1", StringComparison.Ordinal) > 0, "by result");

                Group("call detail: causes in words");
                Check(json.IndexOf("\"causeName\":\"Normal call clearing\"", StringComparison.Ordinal) > 0, "each leg's cause named");
                Check(Query.CallJson(fs, c2, false).IndexOf("\"causeName\":\"Call rejected\"", StringComparison.Ordinal) > 0, "a rejected leg's too");
                Eq(CdrParser.Explained(11, "10"), "10  (cause 16: Normal call clearing)", "the file's hexadecimal cause, explained");
                Eq(CdrParser.Explained(11, "66"), "66  (cause 102: Recovery on timer expiry)", "66 is cause 102");
                Check(CdrParser.Explained(13, "answer").IndexOf("came in", StringComparison.Ordinal) > 0, "answer: the leg came in");
                Eq(CdrParser.Explained(12, "normal call clearing (16)"), "normal call clearing (16)", "other columns as they are");
                Check(Query.CallJson(fs, c1, true).IndexOf("(cause 16: Normal call clearing)", StringComparison.Ordinal) > 0, "the every-column view uses it");
            }
            finally { Directory.Delete(dir, true); }
        }

        static Dictionary<string, object> Obj(string json) { return (Dictionary<string, object>)JsonRead.Parse(json); }

        static AlarmRule ScopeRule(string field, string value, string match)
        {
            AlarmRule r = new AlarmRule();
            r.Field = field; r.Dnis = value; r.Match = match; r.MinCalls = 1; r.MinFailures = 1; r.WindowMinutes = 60; r.ThresholdPercent = 1;
            return r;
        }

        static void TestAlarmScopes()
        {
            Group("alarms: a far-end address or a whole CUBE, not only a DNIS");
            string dir = NewDir(), data = NewDir();
            try
            {
                const string G1 = "ABCD0201 11110000 2222 33334444", G2 = "ABCD0202 11110000 2222 33334444", G3 = "ABCD0203 11110000 2222 33334444";
                Func<string, Dictionary<int, string>> at = delegate (string ip) { return new Dictionary<int, string> { { 71, "\"" + ip + "\"" } }; };
                Dictionary<int, string> rej = new Dictionary<int, string> { { 11, "\"15  \"" }, { 12, "\"call rejected (21)\"" }, { 71, "\"192.0.2.33\"" } };
                Dictionary<int, string> rejOut = new Dictionary<int, string> { { 11, "\"15  \"" }, { 12, "\"call rejected (21)\"" }, { 71, "\"198.51.100.12\"" } };
                WriteCdr(dir, CName,
                    CRow(G1, 100, "answer", "5550001", "10:00:05.000", "10:01:05.000", 3000, Twc(G1, 900, "A1"), at("192.0.2.33")),
                    CRow(G1, 101, "originate", "5550002", "10:00:05.000", "10:01:05.000", 3000, Twc(G1, 901, "A2"), at("198.51.100.12")),
                    CRow(G2, 102, "answer", "5550001", "10:00:01.000", "10:00:01.000", 0, Twc(G2, 902, "B1"), rej),
                    CRow(G2, 103, "originate", "5550003", "10:00:01.000", "10:00:01.000", 0, Twc(G2, 903, "B2"), rejOut),
                    CRow(G3, 104, "answer", "5550001", "10:00:05.000", "10:01:05.000", 3000, Twc(G3, 904, "C1"), at("192.0.2.40")),
                    CRow(G3, 105, "originate", "5550004", "10:00:05.000", "10:01:05.000", 3000, Twc(G3, 905, "C2"), at("198.51.100.12")));
                Store st = ReadCdr(dir);
                st.DeriveDirty();
                Eq(st.Calls.Count, 3, "three calls");

                AlarmRule exact = ScopeRule("endpoint", "192.0.2.33", "exact"), range = ScopeRule("endpoint", "198.51.100.", "prefix"), all = ScopeRule("cube", "", "exact");
                Eq(exact.FilterText(), "mediaip=\"192.0.2.33\"", "an address filters the page exactly");
                Eq(range.FilterText(), "mediaip=198.51.100.", "the start of an address takes the range");
                Eq(all.FilterText(), "", "a whole CUBE needs no filter");
                Eq(all.FailedFilterText(), "result=rejected,failed", "its failed calls are just the failed results");
                Eq(exact.Key, "endpoint 192.0.2.33", "its key for the NMS");
                Eq(range.Target, "far end 198.51.100.*", "named in the alarm");
                Eq(all.Key, "all calls", "a whole CUBE's key");

                AlarmConfig c = new AlarmConfig(); c.Enabled = true; c.EventLog = false;
                c.Rules.Add(exact); c.Rules.Add(range); c.Rules.Add(all);
                List<string> got = new List<string>();
                List<AlarmInfo> infos = new List<AlarmInfo>();
                AlarmEngine e = new AlarmEngine(data, delegate () { return st; });
                e.Zone = "cube"; e.OnLog = delegate (string x) { };
                e.Deliver = delegate (int id, string level, string msg, AlarmInfo info) { got.Add(id + " " + msg); infos.Add(info); return true; };
                e.Use(c);
                e.Evaluate(new DateTime(2026, 11, 9, 10, 30, 0));
                Eq(got.Count, 3, "one alarm each");
                Func<string, string> find = delegate (string s) { foreach (string g in got) if (g.IndexOf(s, StringComparison.Ordinal) >= 0) return g; return null; };
                string a1 = find("far end 192.0.2.33 on"), a2 = find("far end 198.51.100.* on"), a3 = find("all calls on");
                Check(a1 != null && a1.IndexOf("1 of 2 calls failed", StringComparison.Ordinal) > 0, "the address: its two calls, one failed");
                Check(a2 != null && a2.IndexOf("1 of 3 calls failed", StringComparison.Ordinal) > 0, "the range: all three calls reached it");
                Check(a3 != null && a3.IndexOf("1 of 3 calls failed", StringComparison.Ordinal) > 0 && a3.IndexOf("Counts every call on this CUBE", StringComparison.Ordinal) > 0, "the whole CUBE");
                bool keyed = false;
                foreach (AlarmInfo i in infos) if (i.Key == "example-cube|endpoint 192.0.2.33" && i.Dnis == "endpoint 192.0.2.33") keyed = true;
                Check(keyed, "the trap's key and DNIS field name the address");
                Json sj = new Json(4096); e.WriteStatusJson(sj);
                Check(sj.ToString().IndexOf("\"target\":\"all calls\"", StringComparison.Ordinal) > 0, "the window's last check names it in words");

                got.Clear();
                AlarmConfig fewer = new AlarmConfig(); fewer.Enabled = true; fewer.EventLog = false; fewer.Rules.Add(exact); fewer.Rules.Add(range);
                e.Use(fewer);
                e.Evaluate(new DateTime(2026, 11, 9, 10, 35, 0));
                Check(got.Count == 1 && got[0].StartsWith("1002 Cleared: no longer watching call failures to all calls on example-cube", StringComparison.Ordinal),
                      "removing the rule clears its alarm in words: " + (got.Count > 0 ? got[0].Split('\n')[0] : "nothing sent"));

                Group("alarms: the new kinds of rule are checked when saved");
                string err;
                AlarmConfig ok = AlarmConfig.FromJson(Obj("{\"intervalMinutes\":5,\"rules\":[{\"field\":\"cube\"},{\"dnis\":\"192.0.2.\",\"field\":\"endpoint\",\"match\":\"prefix\"},{\"dnis\":\"5550200\"}]}"), out err);
                Check(ok != null && ok.Rules.Count == 3, "a whole CUBE, an address range and a DNIS together: " + err);
                Check(AlarmConfig.FromJson(Obj("{\"intervalMinutes\":5,\"rules\":[{\"field\":\"cube\",\"dnis\":\"123\"}]}"), out err) != null, "a whole CUBE ignores a number typed in");
                Check(AlarmConfig.FromJson(Obj("{\"intervalMinutes\":5,\"rules\":[{\"field\":\"cube\"},{\"field\":\"cube\"}]}"), out err) == null, "two whole-CUBE rules are one too many");
                Check(AlarmConfig.FromJson(Obj("{\"intervalMinutes\":5,\"rules\":[{\"dnis\":\"\",\"field\":\"endpoint\"}]}"), out err) == null, "an address cannot be empty");
                Check(AlarmConfig.FromJson(Obj("{\"intervalMinutes\":5,\"rules\":[{\"dnis\":\"carrier.example\",\"field\":\"endpoint\"}]}"), out err) == null, "a name is not an address");
                Check(AlarmConfig.FromJson(Obj("{\"intervalMinutes\":5,\"rules\":[{\"dnis\":\"1\",\"field\":\"trunk\"}]}"), out err) == null, "an unknown kind is refused");
            }
            finally { Directory.Delete(dir, true); Directory.Delete(data, true); }
        }

        static void TestSettingsWindow()
        {
            Group("settings: how long files are kept");
            AlarmConfig cur = OneRule("5550200");
            cur.Enabled = true; cur.KeepDays = 30; cur.KeepSyslogDays = 90; cur.StaleMinutes = 45;
            string err;
            AlarmConfig next = WebServer.WithRetention(cur, Obj("{\"keepDays\":7,\"keepSyslogDays\":14}"), out err);
            Check(next != null, "saved: " + err);
            if (next == null) return;
            Eq(next.KeepDays, 7, "call record files");
            Eq(next.KeepSyslogDays, 14, "syslog files");
            Check(next.Enabled && next.StaleMinutes == 45 && next.Rules.Count == 1 && next.Rules[0].Dnis == "5550200", "the alarm settings are left as they were");
            Eq(cur.KeepDays, 30, "the saved copy is not changed until the engine takes the new one");
            AlarmConfig half = WebServer.WithRetention(cur, Obj("{\"keepSyslogDays\":0}"), out err);
            Check(half != null && half.KeepSyslogDays == 0 && half.KeepDays == 30, "one value alone changes only that one");
            Check(WebServer.WithRetention(cur, Obj("{\"keepDays\":4000}"), out err) == null && err.IndexOf("3650", StringComparison.Ordinal) > 0, "over ten years is refused");
            Check(WebServer.WithRetention(cur, Obj("{\"keepDays\":-1}"), out err) == null, "below zero is refused");
            Check(WebServer.WithRetention(cur, Obj("{\"keepDays\":1.5}"), out err) == null, "half a day is refused");
            Check(WebServer.WithRetention(cur, null, out err) == null, "nothing readable is refused");

            Group("settings: saving the alarms keeps the clean-up");
            Dictionary<string, object> alarmSave = Obj("{\"enabled\":true,\"intervalMinutes\":5,\"rules\":[]}");
            AlarmConfig posted = AlarmConfig.FromJson(alarmSave, out err);
            Check(posted != null && posted.KeepDays == 0, "an alarm save without them would read as keep everything");
            WebServer.KeepRetention(alarmSave, posted, cur);
            Eq(posted.KeepDays, 30, "so the call record files' days stay");
            Eq(posted.KeepSyslogDays, 90, "and the syslog files' days");
            Dictionary<string, object> older = Obj("{\"enabled\":true,\"intervalMinutes\":5,\"keepDays\":3,\"rules\":[]}");
            AlarmConfig fromOld = AlarmConfig.FromJson(older, out err);
            WebServer.KeepRetention(older, fromOld, cur);
            Check(fromOld.KeepDays == 3 && fromOld.KeepSyslogDays == 90, "a page that still sends one keeps working for that one");
        }
    }
}
