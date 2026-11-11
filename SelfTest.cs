using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CubeScope
{
    /// <summary>
    /// Offline self-test. Every fixture is built here to the shapes in DESIGN.md 1, with made-up
    /// numbers and GUIDs: no customer log ever enters the repository. The timings of the 5-leg dialer
    /// call are those of the real call the design's worked example was measured on.
    /// </summary>
    public static partial class SelfTest
    {
        static int _pass, _fail;
        static string _group = "";

        static void Group(string g) { _group = g; }

        static void Check(bool ok, string what)
        {
            if (ok) _pass++;
            else { _fail++; Console.WriteLine("  FAIL [" + _group + "] " + what); }
        }

        static void Eq(object actual, object expected, string what)
        {
            bool ok = (actual == null && expected == null) || (actual != null && actual.Equals(expected));
            if (!ok) Console.WriteLine("  FAIL [" + _group + "] " + what + "  expected <" + expected + "> got <" + actual + ">");
            if (ok) _pass++; else _fail++;
        }

        public static int Run()
        {
            _pass = 0; _fail = 0;
            Console.WriteLine("CubeScope " + Program.Version + " self-test");
            Action[] tests = {
                TestHeader, TestGuid, TestTimes, TestLegParse, TestFeatureParse, TestIecParse, TestCoverBuffer,
                TestWorkedExample, TestBinding, TestDuplicates, TestReuse, TestResults, TestReleased, TestMedia,
                TestRejected, TestFilter, TestRules, TestQuery, TestCanceled, TestReport, TestYear, TestPrefixes, TestLoader,
                TestTailer, TestLauncher, TestDashboard, TestTips, TestSweep20261008, TestLowMemory, TestClock,
                TestSnmp, TestJsonRead, TestAlarmConfig, TestAlarmEngine, TestAlarmTest, TestServiceBits, TestAlarmNoteAndHistory, TestAlarmCancelMin, TestAlarmByLeg, TestNoZoneTrace, TestReceiver, TestCdr, TestRemote };
            for (int i = 0; i < tests.Length; i++)
            {
                try { tests[i](); }
                catch (Exception ex) { _fail++; Console.WriteLine("  FAIL [" + _group + "] threw " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace); }
            }
            Console.WriteLine();
            Console.WriteLine(_pass + " passed, " + _fail + " failed");
            return _fail == 0 ? 0 : 1;
        }

        // ---------------------------------------------------------------- fixture builders
        // All numbers are invented. 555-01xx style numbers, GUIDs made of repeating digits.

        static long _rx = 7000000, _ios = 6000000;

        /// <summary>"7000001: 6000001: Sep 28 15:00:05.555 EDT: "</summary>
        static string H(string clock)
        {
            _rx++; _ios++;
            return _rx + ": " + _ios + ": Sep 28 " + clock + " EDT: ";
        }

        static string T(string clock) { return clock + " EDT Mon Sep 28 2026"; }

        /// <summary>One VOIP_CALL_HISTORY line.</summary>
        static string CH(string hdr, string guid, string setup, string peer, string causeHex, string text,
                         string connect, string disc, int origin, int tp, long tb, int rp, long rb)
        {
            return H(hdr) + "%VOIPAAA-5-VOIP_CALL_HISTORY: CallLegType 2, ConnectionId " + guid + ", SetupTime " + T(setup) +
                   ", PeerAddress " + peer + ", PeerSubAddress , DisconnectCause " + causeHex + "  , DisconnectText " + text +
                   ", ConnectTime " + T(connect) + ", DisconnectTime " + T(disc) + ", CallOrigin " + origin +
                   ", ChargedUnits 0, InfoType 2, TransmitPackets " + tp + ", TransmitBytes " + tb +
                   ", ReceivePackets " + rp + ", ReceiveBytes " + rb;
        }

        static string Twc(string hdr, string ft, string cgn, string cdn, long fid, string fcid, string legId, string bguid)
        {
            return H(hdr) + "%VOIPAAA-5-VOIP_FEAT_HISTORY: FEAT_VSA=fn:TWC,ft:09/28/2026 " + ft + ",cgn:" + cgn + ",cdn:" + cdn +
                   ",frs:0,fid:" + fid + ",fcid:" + fcid + ",legID:" + legId + ",bguid:" + bguid;
        }

        static string Bx(string hdr, string ft, long fid, string fcid, string legId, string xor, string xee, string xto, string bguid)
        {
            return H(hdr) + "%VOIPAAA-5-VOIP_FEAT_HISTORY: FEAT_VSA=fn:BXFER,ft:09/28/2026 " + ft + ",frs:0,fid:" + fid +
                   ",xconsID:,fcid:" + fcid + ",legID:" + legId + ",xrsn:0,xsts:5,Xor:" + xor + ",Xee:" + xee + ",Xto:" + xto +
                   ",bguid:" + bguid;
        }

        const string Normal = "normal call clearing (16)";

        // The 5-leg dialer call: dialer in-leg (signalling only), customer out-leg, blind transfer
        // to a routing number that hairpins back in and out to the agent side.
        const string G5 = "9A9A9A9AB1B111F1C2C2D3D3D3D3D3D3";
        static string[] FiveLeg()
        {
            const string N = "5550100100";      // customer
            return new string[] {
                CH("15:00:05.555", G5, "14:59:43.015", "", "10", Normal, "15:00:04.535", "15:00:05.555", 2, 0, 0, 0, 0),
                Bx("15:00:05.555", "15:00:04.783", 9001, G5, "6C7549", "<sip:13301@10.9.9.9>", N, "666111100010233", G5),
                Twc("15:00:05.555", "14:59:43.013", "", "7779" + N, 9000, G5, "6C7549", G5),
                CH("15:00:13.234", G5, "14:59:43.013", N, "10", Normal, "15:00:04.533", "15:00:13.233", 1, 379, 75800, 383, 76600),
                Bx("15:00:13.234", "15:00:04.784", 9003, G5, "6C754A", "<sip:13301@10.9.9.9>", N, "666111100010233", G5),
                Twc("15:00:13.234", "14:59:43.014", "5550199000", N, 9002, G5, "6C754A", G5),
                CH("15:00:13.239", G5, "15:00:04.789", "555666111100010233", "10", Normal, "15:00:05.429", "15:00:13.239", 1, 383, 76600, 379, 75800),
                Twc("15:00:13.239", "15:00:04.786", N, "555666111100010233", 9004, G5, "6C759E", G5),
                CH("15:00:13.249", G5, "15:00:05.758", N, "10", Normal, "15:00:06.159", "15:00:13.249", 2, 346, 69200, 354, 70800),
                Twc("15:00:13.249", "15:00:05.758", N, "5550188888", 9005, G5, "6C75A6", G5),
                CH("15:00:13.361", G5, "15:00:05.761", "5550188888", "10", Normal, "15:00:06.151", "15:00:13.361", 1, 354, 74340, 348, 73000),
                Twc("15:00:13.361", "15:00:05.758", N, "5550188888", 9006, G5, "6C75A7", G5),
            };
        }

        static Store Feed(params string[][] files)
        {
            Store st = new Store();
            Source s = st.AddSource("10.9.9.1", "");
            for (int f = 0; f < files.Length; f++)
            {
                s.Files.Add("fixture-" + f + ".log");
                LineParser p = new LineParser(f, 2026, 9);
                for (int i = 0; i < files[f].Length; i++) p.Feed(files[f][i], i + 1);
                p.Flush();
                st.Ingest(s, p.Take());
            }
            st.Settle(s, true);
            st.DeriveDirty();
            return st;
        }

        static Call Only(Store st, string guid)
        {
            Call c;
            st.Sources[0].ByKey.TryGetValue(GuidForm.Key(guid), out c);
            return c;
        }

        // ---------------------------------------------------------------- parser

        static void TestHeader()
        {
            Group("header");
            Header h = Header.Parse("9350126: 8340002: Sep 28 15:00:05.555 EDT: %VOIPAAA-5-VOIP_CALL_HISTORY: x");
            Check(h.HasTime, "time found");
            Eq(h.Nums, 2, "two sequence numbers");
            Eq(h.Rx, 9350126L, "receiver sequence");
            Eq(h.Ios, 8340002L, "IOS sequence");
            Eq(h.Tz, "EDT", "zone");
            Eq(h.Month, 9, "month"); Eq(h.Day, 28, "day"); Eq(h.Hour, 15, "hour"); Eq(h.Milli, 555, "milliseconds");
            Check(Header.Parse("1: 2: Sep 28 15:00:05.555 EDT: %X").Msg > 0, "message index set");
            Eq("9350126: 8340002: Sep 28 15:00:05.555 EDT: %VOIPAAA".Substring(h.Msg, 1), "%", "message starts at the %");

            h = Header.Parse("19687440: 2086866: Mar 26 09:00:00.232: ISDN Se0/1/3:23 Q931: RX");
            Check(h.HasTime, "no zone token is fine");
            Eq(h.Tz, null, "zone absent");

            h = Header.Parse("12: Oct  4 02:00:45.597 EDT: %SIP-2-TLS_HANDSHAKE_FAILED: x");
            Check(h.HasTime, "one sequence number, space-padded day");
            Eq(h.Day, 4, "padded day read");

            h = Header.Parse("5: 6: *Oct  4 2026 02:00:45.597 UTC: %SYS-5-CONFIG_I: x");
            Check(h.HasTime, "unsynced star and a year");
            Eq(h.Year, 2026, "year read when present");
            Eq(h.ClockMark, '*', "star kept: clock never set from NTP");
            Eq(Header.Parse("5: 6: .Oct  4 02:00:45.597 EDT: %X: x").ClockMark, '.', "dot kept: NTP not synchronised");
            Eq(Header.Parse("5: 6: Oct  4 02:00:45.597 EDT: %X: x").ClockMark, '\0', "no mark");

            h = Header.Parse("9221323: xed;boundary=uniqueBoundary");
            Check(!h.HasTime, "continuation line has no time");
            Eq(h.Nums, 1, "continuation keeps its number");

            h = Header.Parse("Content-Type: application/sdp");
            Check(!h.HasTime && h.Nums == 0 && h.Msg == 0, "bare line");
            h = Header.Parse("");
            Check(!h.HasTime, "empty line");
            h = Header.Parse("12: 13: Sep 28 15:00");
            Check(!h.HasTime, "cut-off time is not a time");
        }

        static void TestGuid()
        {
            Group("guid");
            Eq(GuidForm.Key("8A8B8C8D9E9F11F186A1981E0ABCDEF1"), "8A8B8C8D9E9F11F186A1981EABCDEF1", "a word loses its leading zero");
            Eq(GuidForm.Key("0D1E2F30B4B411F1A2980102A30405F6"), "D1E2F30B4B411F1A2980102A30405F6", "first word too");
            Eq(GuidForm.Key("00000001000000020000000300000004"), "1234", "every word");
            Eq(GuidForm.Key("1234567800000000ABCDEF0012345678"), "123456780ABCDEF0012345678", "an all-zero word is 0");
            Eq(GuidForm.Key("9a9a9a9ab1b111f1c2c2d3d3d3d3d3d3"), "9A9A9A9AB1B111F1C2C2D3D3D3D3D3D3", "upper-cased");
            Eq(GuidForm.Key("D1E2F30B4B411F1A2980102A30405F6"), "D1E2F30B4B411F1A2980102A30405F6", "already unpadded stays");
            Eq(GuidForm.Key(""), "", "empty");
            Eq(GuidForm.Short("3E3E3E3EB3B311F18E8EA1A2A3A4A5A6"), "3E3E3E3E8E8E", "trace form is words 1 and half of 3");
            Eq(GuidForm.Short("7A7A7A7AB2B211F1ABAB0102A30405F6"), "7A7A7A7AABAB", "second trace form example");
            Eq(GuidForm.Short("ABC"), null, "short form needs the padded GUID");
            Check(GuidForm.IsHex("0aF9"), "hex"); Check(!GuidForm.IsHex("0x12"), "not hex");
        }

        static void TestTimes()
        {
            Group("times");
            long t = LineParser.IosTime("14:59:43.015 EDT Mon Sep 28 2026");
            Eq(TimeUtil.Fmt(t), "2026-09-28 14:59:43.015", "IOS time with zone and weekday");
            Eq(TimeUtil.Fmt(LineParser.IosTime("14:59:43.015 Mon Sep 28 2026")), "2026-09-28 14:59:43.015", "no zone");
            Eq(TimeUtil.Fmt(LineParser.IosTime("*09:05:01.2 UTC Sun Oct 4 2026")), "2026-10-04 09:05:01.200", "star, short millis");
            Eq(LineParser.IosTime("garbage"), -1L, "unreadable");
            Eq(LineParser.IosTime("25:00:00.000 EDT Mon Sep 28 2026"), -1L, "hour out of range");
            Eq(LineParser.IosTime("10:00:00.000 EDT Mon Feb 30 2026"), -1L, "day out of range");
            Eq(TimeUtil.Fmt(LineParser.FeatTime("09/28/2026 14:57:02.634")), "2026-09-28 14:57:02.634", "feature time");
            Eq(LineParser.FeatTime("9/28/2026 14:57"), -1L, "short feature time");
            Eq(TimeUtil.FmtDur(1618), "1.6 s", "seconds");
            Eq(TimeUtil.FmtDur(185000), "3:05", "minutes");
            Eq(TimeUtil.FmtDur(3725000), "1:02:05", "hours");
            Eq(TimeUtil.TzOffsetHours("EDT"), -4.0, "EDT offset");
            Check(double.IsNaN(TimeUtil.TzOffsetHours("XYZ")), "unknown zone");
        }

        static void TestLegParse()
        {
            Group("leg parse");
            string line = CH("15:00:05.555", "9A9A9A9AB1B111F1C2C2D3D3D3D3D3D3", "14:59:43.015", "", "10", Normal,
                             "15:00:04.535", "15:00:05.555", 2, 0, 0, 0, 0);
            Header h = Header.Parse(line);
            int body = line.IndexOf(": ", h.Msg, StringComparison.Ordinal) + 2;
            string lk;
            Leg g = LineParser.ParseLeg(line, body, out lk);
            Check(g != null, "parsed");
            Eq(lk, "9A9A9A9AB1B111F1C2C2D3D3D3D3D3D3", "key");
            Eq(g.Peer, "", "empty PeerAddress");
            Eq(g.Cause, (short)16, "cause from hex 10");
            Eq(g.TextCause, (short)16, "cause in the text");
            Eq(g.CauseText, "normal call clearing", "cause text without the number");
            Eq((int)g.Origin, 2, "origin");
            Eq((int)g.LegType, 2, "leg type");
            Check(g.Answered, "connect before disconnect = answered");
            Eq(g.TalkMs, 1020L, "talk");

            string un = CH("08:01:33.911", "2F2F2F2FB6B611F19F9FA1A2A3A4A5A6", "08:01:33.661", "5550100111", "29",
                           "temporary failure (41)", "08:01:33.911", "08:01:33.911", 1, 0, 0, 0, 0);
            g = LineParser.ParseLeg(un, un.IndexOf("CallLegType", StringComparison.Ordinal), out lk);
            Eq(g.Cause, (short)41, "hex 29 is cause 41");
            Check(!g.Answered, "connect == disconnect is unanswered");
            Eq(g.RingMs, -1L, "no ring time when unanswered");
            Eq(g.Peer, "5550100111", "peer");

            string zero = CH("10:00:00.000", "1111111111111111111111111111111", "09:59:59.000", "", "0", "", "10:00:00.000",
                             "10:00:00.000", 1, 0, 0, 0, 0);
            g = LineParser.ParseLeg(zero, zero.IndexOf("CallLegType", StringComparison.Ordinal), out lk);
            Check(g != null, "cause 0 with empty text parses");
            Eq(g.Cause, (short)0, "cause 0");
            Eq(g.CauseText, "", "empty text");
            Eq(lk.Length, 31, "31-char ConnectionId kept as the key");

            // a text containing a comma must not shift the fields
            string comma = CH("10:00:00.000", "2222222222222222222222222222222", "09:59:59.000", "5550100", "1F",
                              "normal, unspecified (31)", "09:59:59.500", "10:00:00.000", 1, 25, 5000, 25, 5000);
            g = LineParser.ParseLeg(comma, comma.IndexOf("CallLegType", StringComparison.Ordinal), out lk);
            Eq(g.CauseText, "normal, unspecified", "comma inside DisconnectText");
            Eq((long)g.RxBytes, 5000L, "fields after it intact");
            Eq(g.BytesPerPacket, 200, "bytes per packet");

            Check(LineParser.ParseLeg("%VOIPAAA-5-VOIP_CALL_HISTORY: CallLegType 2, nothing", 0, out lk) == null, "truncated line rejected");
        }

        static void TestFeatureParse()
        {
            Group("feature parse");
            string twc = Twc("14:00:00.000", "13:59:01.000", "5550100", "5550200", 77, "8A8B8C8D9E9F11F186A1981EABCDEF1", "6A8D40",
                             "8A8B8C8D9E9F11F186A1981E0ABCDEF1");
            Feature f = LineParser.ParseFeature(twc, twc.IndexOf("FEAT_VSA", StringComparison.Ordinal));
            Eq(f.Fn, "TWC", "fn");
            Eq(f.Key, "8A8B8C8D9E9F11F186A1981EABCDEF1", "key from the padded bguid");
            Eq(f.Padded, "8A8B8C8D9E9F11F186A1981E0ABCDEF1", "padded kept");
            Eq(f.Cgn, "5550100", "cgn"); Eq(f.Cdn, "5550200", "cdn");
            Eq(f.LegId, 0x6A8D40, "legID hex");
            Eq(f.Fid, 77L, "fid");
            Eq(TimeUtil.Fmt(f.Ft), "2026-09-28 13:59:01.000", "ft");
            Check(f.Kv == null, "TWC keeps no key list (folded into its leg)");

            string bx = Bx("14:00:00.000", "13:59:30.123", 78, "ABCDEF0111111111222222223333333", "6A8D41",
                           "<sip:13301@10.9.9.9>", "5550100", "666111100010233", "ABCDEF0111111111222222223333333");
            f = LineParser.ParseFeature(bx, bx.IndexOf("FEAT_VSA", StringComparison.Ordinal));
            Eq(f.Fn, "BXFER", "transfer");
            Eq(f.Get("Xor"), "<sip:13301@10.9.9.9>", "value with colons split on the first one only");
            Eq(f.Get("Xto"), "666111100010233", "transfer target");
            Eq(f.Get("xconsID"), "", "empty value kept");
            Eq(f.Get("fid"), null, "fields held elsewhere are not repeated");

            string cfa = H("09:14:37.392") + "%VOIPAAA-5-VOIP_FEAT_HISTORY: FEAT_VSA=fn:CFA,ft:09/28/2026 09:13:02.215,frs:0,fid:5806404," +
                         "fcid:2C2C2C2CB5B511F19D9DA1A2A3A4A5A6,legID:6AF766,frson:1,fdcnt:2,fwder:5550101,fwdee:,fwdto:,frm:," +
                         "bguid:2C2C2C2CB5B511F19D9DA1A2A3A4A5A6";
            f = LineParser.ParseFeature(cfa, cfa.IndexOf("FEAT_VSA", StringComparison.Ordinal));
            Eq(f.Fn, "CFA", "forward");
            Eq(f.Get("fwder"), "5550101", "forwarding number");
            Check(LineParser.ParseFeature("FEAT_VSA=ft:1", 0) == null, "no fn: rejected");
        }

        static void TestIecParse()
        {
            Group("iec parse");
            string s = "%VOICE_IEC-3-GW: C SCRIPTS: Internal Error (No dialpeer match): IEC=1.1.128.11.5.0 on callID 7027088 GUID=7A7A7A7AB2B211F1ABAB0102A30405F6";
            IecError e = LineParser.ParseIec(s, s.IndexOf(": ", StringComparison.Ordinal) + 2);
            Eq(e.Sub, "C SCRIPTS", "subsystem");
            Eq(e.Text, "No dialpeer match", "text");
            Eq(e.Code, "1.1.128.11.5.0", "code");
            Eq(e.CallId, "7027088", "call id");
            Eq(e.Padded, "7A7A7A7AB2B211F1ABAB0102A30405F6", "GUID");
            Eq(e.Key, "7A7A7A7AB2B211F1ABAB0102A30405F6", "key");
            s = "%VOICE_IEC-3-GW: SIP: Internal Error (INVITE, invalid IE content): IEC=1.1.100.7.35.2 on callID 1 GUID=00000001000000020000000300000004";
            e = LineParser.ParseIec(s, s.IndexOf(": ", StringComparison.Ordinal) + 2);
            Eq(e.Text, "INVITE, invalid IE content", "text with a comma");
            Eq(e.Key, "1234", "padded GUID unpadded");
        }

        static void TestCoverBuffer()
        {
            Group("cover buffer");
            // the IEC arrives first in the EDT stream; the dump's own times are UTC (+4 h)
            string guid = "7A7A7A7AB2B211F1ABAB0102A30405F6";
            string[] lines = {
                H("10:19:25.792") + "%VOICE_IEC-3-GW: C SCRIPTS: Internal Error (No dialpeer match): IEC=1.1.128.11.5.0 on callID 7027088 GUID=" + guid,
                "9221321: 401959390: Sep 28 14:19:26.138: //7027088/7A7A7A7AABAB/",
                "------------------ Cover Buffer ---------------",
                "Search-key       = :5550100999:7027088",
                "  Timestamp      = Sep 28 14:19:25.788",
                "  CallID         = 7027088",
                "  Called-Number  = 5550100999",
                "  Calling-Number = ",
                "  SIP CallID     = 0a1b2c3d-4e5f6071",
                "  GUID           = 7A7A7A7AABAB",
                "  Cause-code     = unassigned number (1)",
                "-----------------------------------------------",
                "9221322: 401959313: Sep 28 14:19:25.788: //7027088/7A7A7A7AABAB/CUBE_VT/SIP/Msg/ccsipDisplayMsg:",
                "Received: SIP UDP message from 10.9.9.243:5060 to 10.9.9.12:5060 ",
                "INVITE sip:5550100999@10.9.9.12:5060 SIP/2.0",
                "Content-Type: Multipart/mi",
                "9221323: xed;boundary=uniqueBoundary",
                "",
                "9221324: 401959314: Sep 28 14:19:25.849: //7027088/7A7A7A7AABAB/CUBE_VT/SIP/API: voip_rtp_release_port (9864)",
                H("10:19:27.123") + "%SIP-3-BADPAIR: Unexpected event 8 (SIPSPI_EV_CC_CALL_DISCONNECT) in state 10 (STATE_DEAD)",
            };
            Store st = Feed(lines);
            Call c = Only(st, guid);
            Check(c != null, "IEC makes a call");
            Eq(c.Legs.Count, 0, "no accounting record for a rejected INVITE");
            Eq(c.Errors.Count, 1, "the IEC is attached");
            Check(c.Dumps != null && c.Dumps.Count == 1, "dump attached by its short GUID");
            TraceDump d = c.Dumps[0];
            Eq(d.CallId, "7027088", "call id");
            Eq(d.Called, "5550100999", "called number");
            Eq(d.Calling, "", "empty calling");
            Eq(d.CauseCode, 1, "cause code");
            Eq(TimeUtil.Fmt(d.Ts), "2026-09-28 10:19:25.788", "UTC timestamp converted with the stream's EDT");
            Check(!d.TsIsUtc, "converted");
            Check(d.Text.IndexOf("INVITE sip:5550100999", StringComparison.Ordinal) >= 0, "SIP body kept");
            Check(d.Text.IndexOf("xed;boundary", StringComparison.Ordinal) >= 0, "continuation line kept");
            Check(d.Text.IndexOf("voip_rtp_release_port", StringComparison.Ordinal) >= 0, "later trace lines of the same call kept");
            Check(d.Text.IndexOf("BADPAIR", StringComparison.Ordinal) < 0, "dump ends at the next syslog message");
            Eq(c.Result, Result.Rejected, "no dial-peer is rejected");
            Eq(c.Called, "5550100999", "called number from the dump");
            Eq(st.Sources[0].Events.Count, 3, "IEC, dump and BADPAIR on the events lane");
            Eq(st.Sources[0].Skipped, 0L, "no line went unused");

            // a dump with no IEC and no accounting becomes a call of its own at the end
            string[] lone = {
                H("11:00:00.000") + "%SIP-5-TLS_CONNECTION: Connection successful for addr=10.9.9.3, port=5061, connId=1",
                "1: 2: Sep 28 15:01:00.000: //9999/ABCDEF012345/",
                "------------------ Cover Buffer ---------------",
                "  Timestamp      = Sep 28 15:01:00.000",
                "  CallID         = 9999",
                "  Called-Number  = 5550100888",
                "  GUID           = ABCDEF012345",
                "  Cause-code     = recovery on timer expiry (102)",
                "-----------------------------------------------",
            };
            st = Feed(lone);
            Eq(st.Calls.Count, 1, "an unmatched dump becomes a call");
            Eq(st.Calls[0].Result, Result.Failed, "cause 102 is a failure");
            Eq(st.Calls[0].FinalCause, (short)102, "its cause");
        }

        // ---------------------------------------------------------------- correlation

        static void TestWorkedExample()
        {
            Group("worked example");
            Store st = Feed(FiveLeg());
            Call c = Only(st, G5);
            Check(c != null, "one call for the GUID");
            Eq(st.Calls.Count, 1, "only one call");
            Eq(c.Legs.Count, 5, "five legs");
            int bound = 0;
            for (int i = 0; i < c.Legs.Count; i++) if (c.Legs[i].Bound) bound++;
            Eq(bound, 5, "every leg bound to its TWC record");
            Eq(st.Sources[0].Unbound, 0L, "nothing unbound");
            Eq(c.Features.Count, 2, "two BXFER records");
            Eq(c.Transfers, 1, "logged once per bridged leg, counted once");
            Check(c.Dialer, "first inbound leg is the dialer (signalling-only)");
            Eq(c.Result, Result.Answered, "answered");
            Eq(c.RingMs, 21520L, "ring 21.5 s: setup to the customer's answer");
            Eq(c.A2tMs, 1618L, "answer to transfer connect 1.6 s");
            Eq(c.TalkMs, 8828L, "talk from answer to the last disconnect");
            Eq(c.Released, Released.Called, "released by the called side");
            Eq(c.Legs[c.ReleaseLeg].LegId, 0x6C754A, "the customer leg ended first");
            Eq(c.Calling, "5550199000", "calling: the dialer leg has none, so the customer leg's");
            Eq(c.Called, "5550100100", "called: what the CUBE dialled");
            Eq(c.Dialed, "77795550100100", "dialled: what the dialer sent, with its prefix");
            Eq(c.ConnectedTo, "5550188888", "connected to: the last outbound leg to answer");
            Eq(c.Media, Media.TwoWay, "two-way media");
            Eq(c.Bpp, 200, "200 bytes a packet: G.711 20 ms");
            // the customer leg answered before the transfer: judged from the transfer
            Leg cust = null;
            for (int i = 0; i < c.Legs.Count; i++) if (c.Legs[i].LegId == 0x6C754A) cust = c.Legs[i];
            Eq(TimeUtil.Fmt(cust.MediaFrom), "2026-09-28 15:00:04.783", "customer leg counted from the transfer");
            HashSet<int> away = Store.TransferredAway(c);
            Check(away != null && away.Count == 1 && c.Legs[new List<int>(away)[0]].LegId == 0x6C7549, "the dialer leg is the transferor");

            Store st2 = Feed(FiveLeg());
            Store.Derive(Only(st2, G5), st2.Rules);
            Eq(Only(st2, G5).A2tMs, 1618L, "derivation is repeatable");
        }

        static void TestBinding()
        {
            Group("binding");
            const string g = "B1B1B1B1C2C211F1D3D3E4E4E4E4E4E4";
            // a TWC whose history line was lost (a gap in the sequence numbers) becomes a stub leg
            string[] lines = {
                Twc("10:00:01.000", "09:59:50.000", "5550100", "5550200", 1, g, "100", g),
                CH("10:00:01.100", g, "09:59:50.002", "5550100", "10", Normal, "09:59:51.000", "10:00:01.100", 2, 500, 100000, 500, 100000),
                Twc("10:00:01.100", "09:59:50.001", "5550100", "5550200", 2, g, "101", g),
            };
            Store st = Feed(lines);
            Call c = Only(st, g);
            Eq(c.Legs.Count, 2, "stub plus the real leg");
            Eq(st.Sources[0].Stubs, 1L, "one stub");
            Check(c.Legs[0].FeatureOnly && c.Legs[0].Bound, "stub carries the TWC numbers");
            Eq(c.Legs[0].Cdn, "5550200", "stub cdn");
            Check(!c.Legs[0].Answered, "stub is not an answered leg");

            // a history line whose TWC was lost: the next history line closes it unbound
            const string g2 = "C1C1C1C1C2C211F1D3D3E4E4E4E4E4E4";
            string[] lines2 = {
                CH("10:00:02.000", g, "09:59:50.000", "5550200", "10", Normal, "09:59:51.000", "10:00:02.000", 1, 500, 100000, 500, 100000),
                CH("10:00:03.000", g2, "09:59:52.000", "5550300", "10", Normal, "09:59:53.000", "10:00:03.000", 1, 500, 100000, 500, 100000),
                Twc("10:00:03.000", "09:59:52.000", "5550100", "5550300", 3, g2, "200", g2),
            };
            st = Feed(lines2);
            Eq(st.Sources[0].Unbound, 1L, "one leg without its TWC");
            Check(!Only(st, g).Legs[0].Bound, "that leg is unbound");
            Check(Only(st, g2).Legs[0].Bound, "the next one binds");

            // a leg's TWC in the next hour's file still binds (state carries across files)
            const string g3 = "D1D1D1D1C2C211F1D3D3E4E4E4E4E4E4";
            st = Feed(new string[] { CH("10:59:59.999", g3, "10:59:00.000", "5550400", "10", Normal, "10:59:01.000", "10:59:59.999", 1, 10, 2000, 10, 2000) },
                      new string[] { Twc("11:00:00.000", "10:59:00.000", "5550100", "5550400", 4, g3, "300", g3) });
            Check(Only(st, g3).Legs[0].Bound, "TWC in the next file binds");
            Eq(st.Sources[0].Stubs, 0L, "and makes no stub");

            // an unpadded ConnectionId meets its padded bguid
            string padded = "0ABCDEF0111122220333344405556667";
            string key = GuidForm.Key(padded);
            Eq(key, "ABCDEF0111122223333444" + "5556667", "unpadded key");
            st = Feed(new string[] {
                CH("12:00:00.000", key, "11:59:00.000", "5550500", "10", Normal, "11:59:01.000", "12:00:00.000", 1, 10, 2000, 10, 2000),
                Twc("12:00:00.000", "11:59:00.000", "5550100", "5550500", 5, key, "400", padded) });
            Call cp = Only(st, padded);
            Check(cp != null && cp.Legs[0].Bound, "padded bguid binds to the unpadded ConnectionId");
            Eq(cp.Padded, padded, "call learns the padded form");
            Check(st.Sources[0].ByShort.ContainsKey(GuidForm.ShortKey(padded)), "short-GUID index for trace dumps");
            Eq(GuidForm.ShortKey(padded), GuidForm.ShortKeyOf(GuidForm.Short(padded)), "short key: padded and printed short form agree");
            Eq(GuidForm.ShortKeyOf("0ABCDEF0055G"), -1L, "short key: not hex");
        }

        static void TestDuplicates()
        {
            Group("duplicates");
            string[] a = FiveLeg();
            Store st = Feed(a, a);         // the same file twice (overlapping or merged files)
            Call c = Only(st, G5);
            Eq(c.Legs.Count, 5, "identical legs dropped");
            Eq(c.Features.Count, 2, "identical features dropped");
            Eq(st.Sources[0].DuplicateLegs, 5L, "five duplicates counted");
            Eq(st.Sources[0].Stubs, 0L, "a duplicate's TWC is not a stub");
            Eq(c.Transfers, 1, "still one transfer");
        }

        static void TestReuse()
        {
            Group("guid reuse");
            const string g = "E1E1E1E1C2C211F1D3D3E4E4E4E4E4E4";
            string[] lines = {
                CH("10:00:10.000", g, "10:00:00.000", "5550100", "10", Normal, "10:00:01.000", "10:00:10.000", 2, 450, 90000, 450, 90000),
                Twc("10:00:10.000", "10:00:00.000", "5550100", "5550200", 1, g, "1", g),
                CH("16:00:10.000", g, "16:00:00.000", "5550100", "10", Normal, "16:00:01.000", "16:00:10.000", 2, 450, 90000, 450, 90000),
                Twc("16:00:10.000", "16:00:00.000", "5550100", "5550200", 2, g, "2", g),
            };
            Store st = Feed(lines);
            Eq(st.Calls.Count, 2, "the same GUID six hours later is a new call");
            Eq(st.Calls[0].Legs.Count, 1, "first call keeps its leg");
            Eq(Only(st, g), st.Calls[1], "the key now points at the newer call");
        }

        static string[] Pair(string g, string inCause, string outCause, bool answered, string inDisc, string outDisc,
                             int tp, int rp, string cgn)
        {
            string conn = answered ? "10:00:05.000" : null;
            return new string[] {
                CH(inDisc, g, "10:00:00.000", cgn, inCause, "x (" + Convert.ToInt32(inCause, 16) + ")", conn ?? inDisc, inDisc, 2, tp, tp * 200L, rp, rp * 200L),
                Twc(inDisc, "10:00:00.000", cgn, "5550200", 1, g, "10", g),
                CH(outDisc, g, "10:00:00.010", "5550200", outCause, "x (" + Convert.ToInt32(outCause, 16) + ")", conn ?? outDisc, outDisc, 1, rp, rp * 200L, tp, tp * 200L),
                Twc(outDisc, "10:00:00.010", cgn, "5550200", 2, g, "11", g),
            };
        }

        static void Sort(List<string> l)
        {
            // feed in disconnect order, as the CUBE writes them
            l.Sort(delegate (string a, string b)
            {
                return string.CompareOrdinal(a.Substring(a.IndexOf("Sep 28", StringComparison.Ordinal)),
                                             b.Substring(b.IndexOf("Sep 28", StringComparison.Ordinal)));
            });
        }

        static Call One(string[] lines, string g)
        {
            Store st = Feed(lines);
            return Only(st, g);
        }

        static void TestResults()
        {
            Group("results");
            const string g = "F1F1F1F1C2C211F1D3D3E4E4E4E4E4E4";
            // the caller hung up while it rang: the CUBE cancels the outbound leg first, both say 16
            Call c = One(Pair(g, "10", "10", false, "10:00:20.003", "10:00:20.001", 0, 0, "5550100"), g);
            Eq(c.Result, Result.Abandoned, "16 on an unanswered outbound leg");
            Eq(c.Released, Released.Caller, "the caller released");
            Eq(c.FinalCause, (short)16, "cause");
            // refused: the outbound leg carries the reason, the inbound one the CUBE's 47
            c = One(Pair(g, "2F", "14", false, "10:00:30.050", "10:00:30.000", 0, 0, "5550100"), g);
            Eq(c.Result, Result.NoAnswer, "subscriber absent (20)");
            Eq(c.FinalCause, (short)20, "the outbound cause, not the inbound 47");
            Eq(c.Released, Released.Called, "released by the called side");
            Eq(One(Pair(g, "2F", "11", false, "10:00:03.050", "10:00:03.000", 0, 0, "5550100"), g).Result, Result.Busy, "busy (17)");
            Eq(One(Pair(g, "2F", "15", false, "10:00:03.050", "10:00:03.000", 0, 0, "5550100"), g).Result, Result.Rejected, "rejected (21)");
            Call f = One(Pair(g, "2F", "29", false, "10:00:03.050", "10:00:03.000", 0, 0, "5550100"), g);
            Eq(f.Result, Result.Failed, "temporary failure (41)");
            Eq(f.Released, Released.Network, "a failure is the network's");
            Eq(Store.ResultOf(18), Result.NoAnswer, "18"); Eq(Store.ResultOf(19), Result.NoAnswer, "19");
            Eq(Store.ResultOf(1), Result.Rejected, "1"); Eq(Store.ResultOf(28), Result.Rejected, "28");
            Eq(Store.ResultOf(47), Result.Failed, "47"); Eq(Store.ResultOf(102), Result.Failed, "102");
        }

        static void TestReleased()
        {
            Group("released");
            const string g = "A2A2A2A2C2C211F1D3D3E4E4E4E4E4E4";
            List<string> l = new List<string>(Pair(g, "10", "10", true, "10:01:00.000", "10:01:00.004", 2750, 2750, "5550100"));
            Sort(l);
            Call c = One(l.ToArray(), g);
            Eq(c.Released, Released.Caller, "inbound leg ended first: caller hung up");
            l = new List<string>(Pair(g, "10", "10", true, "10:01:00.060", "10:01:00.000", 2750, 2750, "5550100"));
            Sort(l);
            c = One(l.ToArray(), g);
            Eq(c.Released, Released.Called, "outbound leg ended first: called side hung up");
            // logged out of order: line order says inbound first, the timestamps say outbound
            l = new List<string>(Pair(g, "10", "10", true, "10:01:00.006", "10:01:00.000", 2750, 2750, "5550100"));
            c = One(l.ToArray(), g);
            Eq(c.Released, Released.Called, "decided on DisconnectTime, never on line order");
            l = new List<string>(Pair(g, "10", "10", true, "10:01:00.000", "10:01:00.000", 2750, 2750, "5550100"));
            c = One(l.ToArray(), g);
            Eq(c.Released, Released.Tie, "within a millisecond: a tie");
            l = new List<string>(Pair(g, "10", "66", true, "10:01:00.050", "10:01:00.000", 2750, 2750, "5550100"));
            Sort(l);
            c = One(l.ToArray(), g);
            Eq(c.Released, Released.Network, "cause 102 on the leg that ended first: the CUBE tore it down");
        }

        static void TestMedia()
        {
            Group("media");
            const string g = "A3A3A3A3C2C211F1D3D3E4E4E4E4E4E4";
            // 55 s of talk at 50 packets a second each way
            Eq(One(Pair(g, "10", "10", true, "10:01:00.000", "10:01:00.004", 2750, 2750, "5550100"), g).Media, Media.TwoWay, "two-way");
            Eq(One(Pair(g, "10", "10", true, "10:01:00.000", "10:01:00.004", 2750, 0, "5550100"), g).Media, Media.OneWay, "one-way");
            Eq(One(Pair(g, "10", "10", true, "10:01:00.000", "10:01:00.004", 0, 0, "5550100"), g).Media, Media.None, "none");
            Eq(One(Pair(g, "10", "10", true, "10:01:00.000", "10:01:00.004", 700, 2750, "5550100"), g).Media, Media.LowRate, "low packet rate");
            Eq(One(Pair(g, "10", "10", true, "10:00:08.000", "10:00:08.004", 0, 0, "5550100"), g).Media, Media.Unknown, "3 s of talk is too short to judge");
            // a dialer call that was never transferred: the customer leg never carried counted media
            Call d = One(Pair(g, "10", "10", true, "10:01:00.000", "10:01:00.004", 0, 0, ""), g);
            Check(d.Dialer, "empty calling number on the inbound leg is the dialer");
            Eq(d.Media, Media.Unknown, "no counted media is expected on a dialer call with no transfer");
            Eq(d.Calling, "", "no calling number");
        }

        static void TestRejected()
        {
            Group("rejected");
            Store st = Feed(new string[] {
                H("10:19:25.792") + "%VOICE_IEC-3-GW: C SCRIPTS: Internal Error (No dialpeer match): IEC=1.1.128.11.5.0 on callID 7 GUID=12121212121212121212121212121212" });
            Call c = st.Calls[0];
            Eq(c.Result, Result.Rejected, "no dial-peer match");
            Eq(c.Released, Released.Called, "rejected counts as the called side's");
            Eq(TimeUtil.Fmt(c.Start), "2026-09-28 10:19:25.792", "the call starts at the error");
        }

        // ---------------------------------------------------------------- filter and rules

        static void TestFilter()
        {
            Group("filter");
            Store st = Feed(FiveLeg());
            Call c = Only(st, G5);
            string[] yes = { "", "called=555", "called=5550100100", "called=\"5550100100\"", "called=555*100", "called=re:5{3}0100100",
                             "calling=5550199000", "connected=5550188888", "dialed=7779*", "peer=555666*", "any=5550188888",
                             "5550188888", "result=answered", "released=called", "media=twoway", "legs>=5", "legs=5", "transfers>=1",
                             "talk>8", "ring<22", "a2t<2", "codec=200", "tag=dialer", "has=transfer", "!has=dump", "cause=16",
                             "legcause=16", "result!=failed", "called=111,555*", "guid=9A9A9A9A*", "src=10.9.9.1", "dur>30" };
            for (int i = 0; i < yes.Length; i++)
            {
                Filter f = Filter.Parse(yes[i]);
                Check(f.Error == null, "parses: " + yes[i] + (f.Error != null ? " (" + f.Error + ")" : ""));
                Filter.CurrentSources = st.Sources;
                Check(f.Match(c, st.Rules), "matches: " + yes[i]);
                Filter.CurrentSources = null;
            }
            string[] no = { "called=\"555\"", "called=999", "result=failed", "result=unanswered", "media=bad", "legs<5", "talk>60",
                            "tag=extension", "has=dump", "cause=17", "!called=555*", "called!=555*" };
            for (int i = 0; i < no.Length; i++)
            {
                Filter f = Filter.Parse(no[i]);
                Check(f.Error == null, "parses: " + no[i]);
                Check(!f.Match(c, st.Rules), "does not match: " + no[i]);
            }
            string[] bad = { "colour=red", "talk>=abc", "result=maybe", "media=loud", "called>5", "has=nothing" };
            for (int i = 0; i < bad.Length; i++)
                Check(Filter.Parse(bad[i]).Error != null, "rejected: " + bad[i]);
            Check(Filter.Parse("").IsEmpty, "empty filter");
            List<string> words = Filter.Split("called=\"a b\" result=answered");
            Eq(words.Count, 2, "quotes keep spaces inside a word");
        }

        static void TestRules()
        {
            Group("rules");
            Rules r = Rules.Default();
            Eq(r.Errors.Count, 0, "built-in rules parse");
            Check(r.TagNames.Contains("dialer") && r.TagNames.Contains("extension"), "built-in tags");
            Rules bad = Rules.Parse("tag x colour:red\nfrobnicate in\nsignalling-only\ntag y re:(\n");
            Eq(bad.Errors.Count, 4, "four bad lines reported, nothing thrown");
            Rules mine = Rules.Parse("# comment\ntag agent out peer:?????\nsignalling-only in cgn:5550199*\n");
            Eq(mine.Errors.Count, 0, "custom rules");
            Leg g = new Leg(); g.Origin = 1; g.Peer = "36201"; g.Bound = true; g.Cgn = ""; g.Cdn = "";
            mine.Apply(g);
            Eq(g.Tags, mine.Bit("agent"), "five digits on an outbound leg tags agent");
            Leg i2 = new Leg(); i2.Origin = 2; i2.Peer = "5550199000"; i2.Bound = false;
            mine.Apply(i2);
            Check(i2.SignalOnly, "an unbound inbound leg falls back to PeerAddress for cgn");
            Eq(mine.TagList(mine.Bit("agent")), "agent", "tag names");
            Check(Pattern.Parse("=").Match(""), "= alone matches empty");
            Check(Pattern.Parse("\"\"").Match(""), "\"\" matches empty");
            Check(!Pattern.Parse("\"\"").Match("1"), "\"\" only matches empty");
            Check(Pattern.Parse("").Match("anything"), "empty pattern matches all");
        }

        // ---------------------------------------------------------------- query and reports

        static void TestQuery()
        {
            Group("query");
            List<string> all = new List<string>(FiveLeg());
            const string a = "A4A4A4A4C2C211F1D3D3E4E4E4E4E4E4", b = "A5A5A5A5C2C211F1D3D3E4E4E4E4E4E4";
            all.AddRange(Pair(a, "2F", "14", false, "10:00:30.050", "10:00:30.000", 0, 0, "5550100"));
            all.AddRange(Pair(b, "10", "10", true, "10:01:00.000", "10:01:00.004", 2750, 2750, "5550100"));
            Store st = Feed(all.ToArray());
            QueryParams q = new QueryParams();
            q.From = TimeUtil.Ms(2026, 9, 28, 9, 0, 0, 0);
            q.To = TimeUtil.Ms(2026, 9, 28, 16, 0, 0, 0);
            q.Bucket = 3600;
            QueryResult r = Query.Run(st, q);
            Eq(r.N, 7, "seven hourly buckets");
            Eq(r.Calls, 3, "three calls");
            Eq(r.ByResult[(int)Result.Answered], 2, "two answered");
            Eq(r.ByResult[(int)Result.NoAnswer], 1, "one no answer");
            Eq(r.SCalls[1], 2.0, "two calls in the 10:00 bucket");
            Eq(r.SCalls[5], 1.0, "one in the 14:00 bucket (the 5-leg call starts 14:59)");
            Eq(r.PeakCalls, 2, "the two 10:00 calls overlap");
            Eq(r.PeakLegs, 4, "four legs up at once");
            Eq(r.Transfers, 1, "one transfer");
            Eq(r.A2tN, 1, "one answer-to-transfer time");
            Eq(r.TopCalled[0].Number, "5550200", "top called number");
            Eq(r.TopCalled[0].Calls, 2, "its calls");
            Check(r.SCauses.Count == 1 && r.SCauses[0].Cause == 20, "unanswered causes series");
            Eq(r.Matched.Count, 3, "call list");

            q.Filter = Filter.Parse("result=answered");
            r = Query.Run(st, q);
            Eq(r.Calls, 2, "filter applies to the counts");
            q.Filter = new Filter();
            q.Bucket = 0;
            r = Query.Run(st, q);
            Eq(r.Bucket, 60, "seven hours fit 420 one-minute points");
            Eq(Query.AutoBucket(14L * 86400000, 720), 1800, "two weeks at 30 minutes");
            Eq(Query.AutoBucket(86400000L, 720), 300, "a day at five minutes");

            string json = Query.StateJson(st, q, r, "test");
            Check(json.StartsWith("{", StringComparison.Ordinal) && json.EndsWith("}", StringComparison.Ordinal), "state is an object");
            Check(Balanced(json), "brackets balance");
            foreach (string k in new string[] { "\"tiles\"", "\"series\"", "\"tables\"", "\"lanes\"", "\"health\"", "\"range\"", "\"rules\"" })
                Check(json.IndexOf(k, StringComparison.Ordinal) >= 0, "state has " + k);
            string calls = Query.CallsJson(st, Query.Sorted(r.Matched, "dur", true), "dur", true, 0, 2);
            Check(calls.IndexOf("\"total\":3", StringComparison.Ordinal) >= 0 && calls.IndexOf("\"pages\":2", StringComparison.Ordinal) >= 0, "paging");
            List<Call> byDur = Query.Sorted(r.Matched, "dur", true);
            Check(byDur[0].DurMs >= byDur[1].DurMs && byDur[1].DurMs >= byDur[2].DurMs, "sorted by duration, longest first");
            string detail = Query.CallJson(st, Only(st, G5), false);
            Check(Balanced(detail) && detail.IndexOf("\"legId\":\"6C754A\"", StringComparison.Ordinal) >= 0, "call detail");
            Check(detail.IndexOf("\"away\":true", StringComparison.Ordinal) >= 0, "detail marks the transferor leg");
        }

        // calls stopped while they rang, split by who stopped them and how long they ran first
        static void TestCanceled()
        {
            Group("canceled while ringing");
            const string d1 = "C1C1C1C1C2C211F1D3D3E4E4E4E4E4E4", d2 = "C2C2C2C2C2C211F1D3D3E4E4E4E4E4E4",
                         k1 = "C3C3C3C3C2C211F1D3D3E4E4E4E4E4E4", k2 = "C4C4C4C4C2C211F1D3D3E4E4E4E4E4E4",
                         an = "C5C5C5C5C2C211F1D3D3E4E4E4E4E4E4";
            List<string> all = new List<string>();
            all.AddRange(Pair(d1, "10", "10", false, "10:00:32.003", "10:00:32.001", 0, 0, ""));        // dialer ring timeout, 32 s
            all.AddRange(Pair(d2, "10", "10", false, "10:00:01.503", "10:00:01.501", 0, 0, ""));        // dialer gave up at once
            all.AddRange(Pair(k1, "10", "10", false, "10:00:04.003", "10:00:04.001", 0, 0, "5550100")); // caller, 4 s
            all.AddRange(Pair(k2, "10", "10", false, "10:01:05.003", "10:01:05.001", 0, 0, "5550100")); // caller, 65 s
            all.AddRange(Pair(an, "10", "10", true, "10:01:00.000", "10:01:00.004", 2750, 2750, "5550100"));
            Store st = Feed(all.ToArray());
            Call cd = Only(st, d1), ck = Only(st, k1);
            Eq(cd.Result, Result.Abandoned, "a dialer call stopped while ringing is canceled");
            Check(cd.Dialer && !ck.Dialer, "dialer and caller calls told apart");

            Eq(Query.CancelBin(-5), 0, "a negative length counts as under 1 s");
            Eq(Query.CancelBin(999), 0, "under 1 s");
            Eq(Query.CancelBin(1000), 1, "1 s");
            Eq(Query.CancelBin(32999), 32, "whole seconds, rounded down");
            Eq(Query.CancelBin(119999), 119, "the last whole second");
            Eq(Query.CancelBin(120000), QueryResult.CancelMaxSec, "120 s is the open last bin");
            Eq(Query.CancelBin(3600000), QueryResult.CancelMaxSec, "an hour is in the last bin");

            // piles: runs of seconds each holding 1% or more; a scattered tail is left over
            int[] bins = new int[QueryResult.CancelMaxSec + 1];
            bins[9] = 400; bins[10] = 500; bins[33] = 300; bins[34] = 200; bins[49] = 50; bins[50] = 30; bins[51] = 9;
            for (int i = 60; i < 70; i++) bins[i] = 1;
            bins[QueryResult.CancelMaxSec] = 20;
            List<CancelClump> piles = Query.Clumps(bins);   // 1,519 cancels: 1% is 16
            Eq(piles.Count, 4, "four piles");
            Check(piles[0].From == 9 && piles[0].To == 11 && piles[0].Calls == 900, "9 to 11 s: the second after the last busy one ends it");
            Check(piles[1].From == 33 && piles[1].To == 35 && piles[1].Calls == 500, "33 to 35 s");
            Check(piles[2].From == 49 && piles[2].To == 51 && piles[2].Calls == 80, "49 to 51 s (51 s has under 1%)");
            Check(piles[3].From == QueryResult.CancelMaxSec && piles[3].To == 0 && piles[3].Calls == 20, "120 s or more stands alone");
            int[] many = new int[QueryResult.CancelMaxSec + 1];
            for (int i = 0; i < 20; i++) many[i * 5] = 10 + i;
            List<CancelClump> top = Query.Clumps(many);
            Eq(top.Count, 8, "at most eight piles");
            Check(top[0].From == 60 && top[7].From == 95, "the eight biggest, in order of length");
            Eq(Query.Clumps(new int[QueryResult.CancelMaxSec + 1]).Count, 0, "no cancels, no piles");

            QueryParams q = new QueryParams();
            q.From = TimeUtil.Ms(2026, 9, 28, 9, 0, 0, 0);
            q.To = TimeUtil.Ms(2026, 9, 28, 11, 0, 0, 0);
            QueryResult r = Query.Run(st, q);
            Eq(r.Calls, 5, "five calls");
            Eq(r.ByResult[(int)Result.Abandoned], 4, "four canceled");
            Check(r.CancelDialer[1] == 1 && r.CancelDialer[32] == 1 && Sum(r.CancelDialer) == 2, "dialer: 1 s and 32 s");
            Check(r.CancelCaller[4] == 1 && r.CancelCaller[65] == 1 && Sum(r.CancelCaller) == 2, "caller: 4 s and 65 s");
            string json = Query.StateJson(st, q, r, "test");
            Check(Balanced(json), "state brackets balance");
            Check(json.IndexOf("\"canceled\":{\"maxSec\":120,\"dialer\":[0,1,0,", StringComparison.Ordinal) >= 0, "state carries per-second cancels");
            Check(json.IndexOf("\"dialerPiles\":[],\"callerPiles\":[]}", StringComparison.Ordinal) >= 0, "two cancels a side make no pile");
            int[] few = new int[QueryResult.CancelMaxSec + 1];
            few[8] = 1; few[13] = 1; few[49] = 2; few[50] = 3;
            List<CancelClump> fp = Query.Clumps(few);
            Check(fp.Count == 1 && fp[0].From == 50 && fp[0].To == 51 && fp[0].Calls == 3, "a pile needs 3 calls in each second");
            Json pj = new Json(); pj.Obj(); Query.WriteClumps(pj, "p", bins); pj.EndObj();
            Eq(pj.ToString(), "{\"p\":[[9,11,900],[33,35,500],[49,51,80],[120,0,20]]}", "piles as JSON: from, to, calls");

            // the filter a band click builds lists exactly that band's calls
            string[] f = { "result=canceled", "result=cancelled", "result=abandoned has=dialer", "result=abandoned !has=dialer dur>=2 dur<5",
                           "result=dialercanceled", "result=callercanceled", "result=CallerCancelled", "result=dialercancelled",
                           "result=callercanceled dur>=2 dur<5", "result=dialercanceled dur>=30 dur<35", "result=callercanceled dur>=60",
                           "result=callercanceled,dialercanceled", "result=answered,dialercanceled", "!result=callercanceled" };
            int[] want = { 4, 4, 2, 1, 2, 2, 2, 2, 1, 1, 1, 4, 3, 3 };
            for (int i = 0; i < f.Length; i++)
            {
                QueryParams qf = new QueryParams();
                qf.From = q.From; qf.To = q.To; qf.Filter = Filter.Parse(f[i]);
                Check(qf.Filter.Error == null, "parses: " + f[i]);
                Eq(Query.Run(st, qf).Calls, want[i], "calls for " + f[i]);
            }
            Check(Filter.Parse("has=dialer").Match(cd, st.Rules) && !Filter.Parse("has=dialer").Match(ck, st.Rules), "has=dialer");
            Check(Filter.Parse("result=hungup").Error != null, "an unknown result word is refused");
            Check(Filter.Parse("result=hungup").Error.IndexOf("callercanceled", StringComparison.Ordinal) >= 0, "the refusal lists the cancel words");
            Check(json.IndexOf("\"dialerCanceled\":2", StringComparison.Ordinal) >= 0, "the Find pickers get the dialer's share of the cancels");
            Check(json.IndexOf("\"fromMs\":" + q.From + ",\"toMs\":" + q.To, StringComparison.Ordinal) >= 0, "the range is also sent to the millisecond");

            // a chart click sends a bucket clipped to the range, to the millisecond
            Eq(WebServer.ParseTime("1759853566"), 1759853566000L, "whole seconds");
            Eq(WebServer.ParseTime("1759853566.501"), 1759853566501L, "seconds with a fraction");
            Eq(WebServer.ParseTime("1759853566.5"), 1759853566500L, "one decimal");
            Eq(WebServer.ParseTime("1.2.3"), -1L, "two points is not a time");
            Eq(WebServer.ParseTime("-5.5"), -1L, "no negative times");
            Eq(WebServer.ParseTime(".5"), -1L, "no bare fraction");
            StringWriter sw = new StringWriter();
            Report.Summary(st, new QueryParams(), sw, 0);
            Check(sw.ToString().IndexOf("Canceled while ringing: 4 (", StringComparison.Ordinal) >= 0, "summary line");
            Check(sw.ToString().IndexOf("dialer  no piles: 2 spread over many lengths", StringComparison.Ordinal) >= 0, "summary without piles");
            // media shares are of the answered calls (one here, two-way)
            Check(sw.ToString().IndexOf("Media on answered calls: two-way 1 (100.0%), low packet rate 0 (0.0%), one-way 0 (0.0%), none 0 (0.0%) of 1 answered",
                                        StringComparison.Ordinal) >= 0, "summary media shares of answered calls");

            // Live: the page turns it on by itself when the server says it is following new records
            Check(json.IndexOf("\"following\":false", StringComparison.Ordinal) >= 0, "a state built without a feed says not following");
            Check(Query.StateJson(st, q, r, "", true).IndexOf("\"following\":true", StringComparison.Ordinal) >= 0, "a following server says so");
        }

        static int Sum(int[] v)
        {
            int s = 0;
            for (int i = 0; i < v.Length; i++) s += v[i];
            return s;
        }

        // ---------------------------------------------------------------- service and alarms

        static void TestSnmp()
        {
            Group("snmp encoder");
            // the same hand-computed reference bytes as the PowerShell watch jobs
            Eq(Snmp.Hex(Snmp.Integer(0)), "02 01 00", "INTEGER 0");
            Eq(Snmp.Hex(Snmp.Integer(127)), "02 01 7F", "INTEGER 127");
            Eq(Snmp.Hex(Snmp.Integer(128)), "02 02 00 80", "INTEGER 128 pads the sign bit");
            Eq(Snmp.Hex(Snmp.Integer(65536)), "02 03 01 00 00", "INTEGER 65536");
            Eq(Snmp.Hex(Snmp.UInt(12345, 0x43)), "43 02 30 39", "TimeTicks tag");
            Eq(Snmp.Hex(Snmp.UInt(3385, 0x42)), "42 02 0D 39", "Gauge32 tag");
            Eq(Snmp.Hex(Snmp.OctetString("public")), "04 06 70 75 62 6C 69 63", "OCTET STRING");
            Eq(Snmp.Hex(Snmp.Oid("1.3.6.1.2.1.1.3.0")), "06 08 2B 06 01 02 01 01 03 00", "OID sysUpTime.0");
            Eq(Snmp.Hex(Snmp.Oid("1.3.6.1.6.3.1.1.4.1.0")), "06 0A 2B 06 01 06 03 01 01 04 01 00", "OID snmpTrapOID.0");
            Eq(Snmp.Hex(Snmp.Oid("1.3.6.1.4.1.311")), "06 07 2B 06 01 04 01 82 37", "OID multi-byte arc (311)");
            Eq(Snmp.Hex(Snmp.Oid("1.3.6.1.4.1.9999.1.3000.1001")), "06 0C 2B 06 01 04 01 CE 0F 01 97 38 87 69", "OID of the alarm trap");
            Check(Snmp.Hex(Snmp.Tlv(0x04, new byte[200])).StartsWith("04 81 C8", StringComparison.Ordinal), "long-form length");
            Check(Snmp.ValidOid("1.3.6.1.4.1.9999.1.3000") && !Snmp.ValidOid("1.3.x") && !Snmp.ValidOid("1") && !Snmp.ValidOid("1..3"), "OID check");

            AlarmConfig c = new AlarmConfig(); c.TrapCommunity = "splk";
            AlarmInfo ai = new AlarmInfo(); ai.Cube = "10.9.9.1"; ai.Dnis = "5550200"; ai.Calls = 3385; ai.Failed = 40; ai.Pct = "1.18"; ai.Threshold = "1"; ai.Key = "10.9.9.1|5550200";
            byte[] pkt = AlarmEngine.BuildTrap(c, 1001, "Greater than 1% call failures to DNIS 5550200 on 10.9.9.1\nDetail line", ai, 8780681, 941100624);
            string h = Snmp.Hex(pkt);
            Check(pkt[0] == 0x30 && h.Contains("02 01 01 04"), "a v2c message with its community");
            Check(h.Contains(" A7 "), "the SNMPv2-Trap PDU");
            Check(h.Contains(Snmp.Hex(Snmp.Oid("1.3.6.1.4.1.9999.1.3000.1001"))), "snmpTrapOID.0 is <TrapOid>.1001");
            Check(h.Contains(Snmp.Hex(Snmp.OctetString("Greater than 1% call failures to DNIS 5550200 on 10.9.9.1"))), ".1 is the first line alone");
            Check(h.Contains(Snmp.Hex(Snmp.OctetString("Greater than 1% call failures to DNIS 5550200 on 10.9.9.1 | Detail line"))), ".12 is the whole message on one line");
            Check(h.Contains(Snmp.Hex(Snmp.OctetString("10.9.9.1|5550200"))), "the alarm key");
            Check(h.Contains(Snmp.Hex(Snmp.UInt(3385, 0x42))), "calls as a Gauge32");
            Check(h.Contains(Snmp.Hex(Snmp.OctetString("splk"))), "the community");
        }

        static void TestJsonRead()
        {
            Group("json reader");
            Dictionary<string, object> o = JsonRead.Parse("{\"a\":1.5,\"b\":[1,\"x\",true,null],\"c\":{\"d\":\"q\\\"\\u0041\\n\"},\"e\":false,\"f\":-2e2}") as Dictionary<string, object>;
            Check(o != null, "an object");
            Eq(JsonRead.GetNumber(o, "a", 0), 1.5, "number");
            Eq(((List<object>)o["b"]).Count, 4, "array");
            Eq(JsonRead.GetString((Dictionary<string, object>)o["c"], "d", ""), "q\"A\n", "escapes");
            Eq(JsonRead.GetBool(o, "e", true), false, "false");
            Eq(JsonRead.GetNumber(o, "f", 0), -200.0, "exponent");
            Eq(JsonRead.GetStrings(o, "b").Count, 3, "strings of an array, nulls skipped");
            string[] bad = { "{", "{\"a\":}", "[1,]x", "\"open", "{\"a\" 1}", "tru" };
            foreach (string b in bad)
            {
                bool threw = false;
                try { JsonRead.Parse(b); } catch (FormatException) { threw = true; }
                Check(threw, "refused: " + b);
            }
        }

        static AlarmConfig OneRule(string dnis)
        {
            AlarmConfig c = new AlarmConfig();
            c.Enabled = true; c.EventLog = false;
            AlarmRule r = new AlarmRule(); r.Dnis = dnis;
            c.Rules.Add(r);
            return c;
        }

        static void TestAlarmConfig()
        {
            Group("alarm settings");
            AlarmConfig c = OneRule("5550200");
            c.TrapDestinations.Add("10.1.1.50"); c.TrapCommunity = "x"; c.Rules[0].Cubes.Add("10.9.9.1"); c.Rules[0].ThresholdPercent = 1.5;
            string text = c.ToJsonText();
            string err;
            AlarmConfig back = AlarmConfig.FromJson(JsonRead.Parse(text) as Dictionary<string, object>, out err);
            Check(back != null && err == null, "its own JSON reads back");
            Eq(back.ToJsonText(), text, "round trip is exact");
            Eq(back.Rules[0].FilterText(), "called=\"5550200\"", "the dashboard filter for an exact DNIS");
            string[][] bads = {
                new string[] { "\"intervalMinutes\":0", "check every" },
                new string[] { "\"trapOid\":\"1.3.x\"", "trap OID" },
                new string[] { "\"alertLevel\":\"Loud\"", "Error or Warning" },
                new string[] { "\"rules\":[{\"dnis\":\"\"}]", "DNIS is empty" },
                new string[] { "\"rules\":[{\"dnis\":\"55a\"}]", "digits" },
                new string[] { "\"rules\":[{\"dnis\":\"1\"},{\"dnis\":\"1\"}]", "already watched" },
                new string[] { "\"rules\":[{\"dnis\":\"1\",\"failResults\":[\"maybe\"]}]", "not a result" },
                new string[] { "\"rules\":[{\"dnis\":\"1\",\"windowMinutes\":2}]", "5 to 1440" },
                new string[] { "\"rules\":[{\"dnis\":\"1\",\"match\":\"fuzzy\"}]", "exact, suffix or prefix" },
                new string[] { "\"trapDestinations\":[\"a b\"]", "not an address" } };
            foreach (string[] b in bads)
            {
                AlarmConfig x = AlarmConfig.FromJson(JsonRead.Parse("{" + b[0] + "}") as Dictionary<string, object>, out err);
                Check(x == null && err != null && err.Contains(b[1]), "refused " + b[0] + " (" + err + ")");
            }
            AlarmConfig canc = AlarmConfig.FromJson(JsonRead.Parse("{\"rules\":[{\"dnis\":\"+12\",\"failResults\":\"canceled,failed\"}]}") as Dictionary<string, object>, out err);
            Check(canc != null && canc.Rules[0].Dnis == "12" && canc.Rules[0].FailResults.Contains("abandoned"), "a leading + is dropped; canceled means abandoned");
        }

        // 58 answered calls and 2 failed ones to 5550200, all starting 10:00 on the fixture CUBE 10.9.9.1
        static Store AlarmFixture()
        {
            List<string> all = new List<string>();
            for (int i = 0; i < 60; i++)
            {
                string g = (0xB0000000L + i).ToString("X8") + "C2C211F1D3D3E4E4E4E4E4E4";
                if (i < 2) all.AddRange(Pair(g, "2F", "29", false, "10:00:03.050", "10:00:03.000", 0, 0, "5550100"));
                else all.AddRange(Pair(g, "10", "10", true, "10:01:00.000", "10:01:00.004", 2750, 2750, "5550100"));
            }
            return Feed(all.ToArray());
        }

        static void TestAlarmEngine()
        {
            Group("alarm engine");
            Store st = AlarmFixture();
            string dir = Path.Combine(Path.GetTempPath(), "cs_alarm_" + Guid.NewGuid().ToString("N"));
            try
            {
                List<string> got = new List<string>();
                List<AlarmInfo> infos = new List<AlarmInfo>();
                bool deliverOk = true;
                Func<int, string, string, AlarmInfo, bool> sink = delegate (int id, string level, string msg, AlarmInfo info)
                {
                    if (!deliverOk) return false;
                    got.Add(id + " " + level + " " + msg.Split('\n')[0]); infos.Add(info); return true;
                };
                AlarmEngine e = new AlarmEngine(dir, delegate () { return st; });
                e.Zone = "cube"; e.Deliver = sink; e.OnLog = delegate (string s) { };
                e.Use(OneRule("5550200"));
                DateTime at = new DateTime(2026, 9, 28, 10, 30, 0);
                e.Evaluate(at);
                Eq(got.Count, 1, "one event on the breach");
                Eq(got.Count > 0 ? got[0] : "", "1001 Error Greater than 1% call failures to DNIS 5550200 on 10.9.9.1", "the alarm's description");
                Check(infos.Count > 0 && infos[0].Calls == 60 && infos[0].Failed == 2 && infos[0].Pct == "3.33" && infos[0].Key == "10.9.9.1|5550200", "its counts and key");
                e.Evaluate(at);
                Eq(got.Count, 1, "nothing more while it stays over");

                AlarmEngine e2 = new AlarmEngine(dir, delegate () { return st; });
                e2.Zone = "cube"; e2.Deliver = sink; e2.OnLog = e.OnLog; e2.Load(); e2.Use(OneRule("5550200"));
                e2.Evaluate(at);
                Eq(got.Count, 1, "a restart remembers the open alarm (no second alarm)");

                e2.Evaluate(at.AddHours(2));
                Eq(got.Count, 2, "one event on the clear");
                Check(got.Count > 1 && got[1].StartsWith("1002 Information Cleared: no longer greater than 1% call failures to DNIS 5550200 on 10.9.9.1", StringComparison.Ordinal), "the clear's description");
                e2.Evaluate(at.AddHours(2));
                Eq(got.Count, 2, "nothing more once clear");

                // a failed delivery is tried again, not lost
                deliverOk = false; e2.Evaluate(at);
                deliverOk = true; e2.Evaluate(at);
                Eq(got.Count, 3, "an alarm that could not be delivered is raised on the next check");

                // the rule removed while its alarm is open: the alarm gets its clear
                AlarmConfig none = OneRule("5550200"); none.Rules.Clear();
                e2.Use(none); e2.Evaluate(at);
                Check(got.Count == 4 && got[3].StartsWith("1002 Information Cleared: no longer watching", StringComparison.Ordinal), "removing the rule clears its open alarm");

                // alarms turned off with one open: cleared too
                e2.Use(OneRule("5550200")); e2.Evaluate(at);
                AlarmConfig off = OneRule("5550200"); off.Enabled = false;
                e2.Use(off); e2.Evaluate(at);
                Check(got.Count == 6 && got[5].Contains("no longer watching"), "turning alarms off clears the open alarm");

                // a rule naming a CUBE that is not read: cannot check, then checking again
                AlarmConfig bad = OneRule("5550200"); bad.Rules[0].Cubes.Add("10.9.9.99");
                e2.Use(bad); e2.Evaluate(at.AddHours(2));
                Check(got.Count == 7 && got[6].StartsWith("1003 Warning", StringComparison.Ordinal), "an unknown CUBE is a cannot-check warning");
                e2.Evaluate(at.AddHours(2));
                Eq(got.Count, 7, "the warning is not repeated");
                e2.Use(OneRule("5550200")); e2.Evaluate(at.AddHours(2));
                Check(got.Count == 8 && got[7].StartsWith("1004 Information", StringComparison.Ordinal), "checking again once the CUBE is right");

                // too few calls is never an alarm; a higher limit is not crossed
                AlarmConfig few = OneRule("5550200"); few.Rules[0].MinCalls = 61;
                e2.Use(few); e2.Evaluate(at);
                Eq(got.Count, 8, "60 calls under a 61-call minimum: no alarm");
                AlarmConfig high = OneRule("5550200"); high.Rules[0].ThresholdPercent = 5;
                e2.Use(high); e2.Evaluate(at);
                Eq(got.Count, 8, "3.33% under a 5% limit: no alarm");
                AlarmConfig other = OneRule("5550201");
                e2.Use(other); e2.Evaluate(at);
                Eq(got.Count, 8, "another DNIS has no calls: no alarm");
                AlarmConfig suf = OneRule("550200"); suf.Rules[0].Match = "suffix";
                e2.Use(suf); e2.Evaluate(at);
                Check(got.Count == 9 && got[8].Contains("DNIS 550200"), "a suffix rule matches the end of the number");
                Json j = new Json(); e2.WriteStatusJson(j);
                Check(j.ToString().Contains("\"state\":\"alarm\"") && j.ToString().Contains("\"cube\":\"10.9.9.1\""), "the status for the settings window");
            }
            finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
        }

        // the test button's traps, caught on a loopback port: the alarm, then its clear
        static void TestAlarmTest()
        {
            Group("alarm test button");
            System.Net.Sockets.UdpClient rx = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
            try
            {
                int port = ((System.Net.IPEndPoint)rx.Client.LocalEndPoint).Port;
                rx.Client.ReceiveTimeout = 5000;
                AlarmEngine e = new AlarmEngine(Path.GetTempPath(), delegate () { return null; });
                e.OnLog = delegate (string s) { };
                AlarmConfig c = OneRule("5550200");
                c.TrapDestinations.Add("127.0.0.1:" + port); c.TrapCommunity = "testcomm";
                string said = e.Test(c, 1);
                Check(said.Contains("1 of 1 destination"), "the button says where the trap went: " + said);
                System.Net.IPEndPoint from = null;
                string a = Snmp.Hex(rx.Receive(ref from));
                string b = Snmp.Hex(rx.Receive(ref from));
                Check(a.Contains(Snmp.Hex(Snmp.Oid("1.3.6.1.4.1.9999.1.3000.1001"))) && a.Contains(Snmp.Hex(Snmp.OctetString("TEST|5550200"))), "first the test alarm, on the TEST key");
                Check(b.Contains(Snmp.Hex(Snmp.Oid("1.3.6.1.4.1.9999.1.3000.1002"))) && b.Contains(Snmp.Hex(Snmp.OctetString("TEST|5550200"))), "then its clear, on the same key");
                Check(a.Contains(Snmp.Hex(Snmp.OctetString("testcomm"))), "with the community given");
                AlarmConfig nothing = OneRule("1");
                Check(e.Test(nothing, 1).StartsWith("Nothing to test", StringComparison.Ordinal), "nothing set up: says so");
            }
            finally { rx.Close(); }
        }

        // the rule's note on the alarm (trap .13, the event's last line) and the history of what was sent
        static void TestAlarmNoteAndHistory()
        {
            Group("alarm note and history");
            string err;
            AlarmConfig c = AlarmConfig.FromJson(JsonRead.Parse("{\"enabled\":true,\"eventLog\":false,\"rules\":[{\"dnis\":\"5550200\",\"note\":\"Call the NOC\\r\\nthen open a ticket\"}]}") as Dictionary<string, object>, out err);
            Check(c != null && c.Rules[0].Note == "Call the NOC  then open a ticket", "a note is one line: " + (c != null ? c.Rules[0].Note : err));
            Check(c != null && c.ToJsonText().Contains("\"note\":\"Call the NOC  then open a ticket\""), "the note is saved with the rule");
            string longNote = new string('x', AlarmRule.NoteMax + 1);
            Check(AlarmConfig.FromJson(JsonRead.Parse("{\"rules\":[{\"dnis\":\"1\",\"note\":\"" + longNote + "\"}]}") as Dictionary<string, object>, out err) == null && err.Contains("at most"), "a note over 500 characters is refused");

            AlarmInfo ai = new AlarmInfo(); ai.Cube = "10.9.9.1"; ai.Dnis = "5550200"; ai.Key = "10.9.9.1|5550200"; ai.Note = "Call the NOC";
            byte[] pkt = AlarmEngine.BuildTrap(new AlarmConfig(), 1001, c.Rules[0].WithNote("Greater than 1% call failures to DNIS 5550200 on 10.9.9.1"), ai, 1, 1);
            string h = Snmp.Hex(pkt);
            string tail = Snmp.Hex(Snmp.OctetString("Call the NOC"));
            Check(h.EndsWith(Snmp.Hex(Snmp.Oid("1.3.6.1.4.1.9999.1.3000.13")) + " " + tail, StringComparison.Ordinal), "the note is the trap's last varbind, .13");
            Check(h.Contains(Snmp.Hex(Snmp.OctetString("Greater than 1% call failures to DNIS 5550200 on 10.9.9.1"))), ".1 is still the description alone");
            Check(h.Contains(Snmp.Hex(Snmp.OctetString("Greater than 1% call failures to DNIS 5550200 on 10.9.9.1 | Note: Call the NOC  then open a ticket"))), ".12 ends with the note");

            // the real delivery path, events to a file: the event's last line and the history
            Store st = AlarmFixture();
            string dir = Path.Combine(Path.GetTempPath(), "cs_hist_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dir);
                AlarmEngine e = new AlarmEngine(dir, delegate () { return st; });
                e.Zone = "cube"; e.OnLog = delegate (string s) { }; e.EventFile = Path.Combine(dir, "events.txt");
                e.Use(c);
                DateTime at = new DateTime(2026, 9, 28, 10, 30, 0);
                e.Evaluate(at);
                string ev = File.ReadAllText(e.EventFile);
                Check(ev.Contains("event 1001") && ev.TrimEnd().EndsWith("Note: Call the NOC  then open a ticket", StringComparison.Ordinal), "the alarm event ends with the note");
                List<AlarmHistoryEntry> hist = e.History(10);
                Check(hist.Count == 1 && hist[0].Id == 1001 && hist[0].State == "alarm" && hist[0].Cube == "10.9.9.1" && hist[0].Dnis == "5550200" &&
                      hist[0].Text == "Greater than 1% call failures to DNIS 5550200 on 10.9.9.1" && hist[0].EventLog == "written" && hist[0].Traps == "none set",
                      "the history records the alarm and where it went");
                e.Evaluate(at.AddHours(2));
                AlarmEngine e2 = new AlarmEngine(dir, delegate () { return st; });
                e2.Load();
                hist = e2.History(10);
                Check(hist.Count == 2 && hist[0].State == "clear" && hist[1].State == "alarm", "the history survives a restart, newest first");
                Json j = new Json(); e2.WriteStatusJson(j);
                Check(j.ToString().Contains("\"history\":[{") && j.ToString().Contains("\"state\":\"clear\""), "the window gets the history");
            }
            finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
        }

        // "canceled while ringing counts only after N seconds": quick hang-ups drop out, slow ones count
        static void TestAlarmCancelMin()
        {
            Group("alarm cancel minimum");
            List<string> all = new List<string>();
            for (int i = 0; i < 54; i++)
            {
                string g = (0xC0000000L + i).ToString("X8") + "C2C211F1D3D3E4E4E4E4E4E4";
                if (i < 2) all.AddRange(Pair(g, "10", "10", false, "10:00:01.503", "10:00:01.501", 0, 0, "5550100"));        // canceled at 1.5 s
                else if (i < 4) all.AddRange(Pair(g, "10", "10", false, "10:00:05.050", "10:00:05.010", 0, 0, "5550100"));   // canceled at 5.05 s
                else all.AddRange(Pair(g, "10", "10", true, "10:01:00.000", "10:01:00.004", 2750, 2750, "5550100"));
            }
            Store st = Feed(all.ToArray());
            DateTime at = new DateTime(2026, 9, 28, 10, 30, 0);
            Func<double, AlarmInfo> run = delegate (double min)
            {
                AlarmInfo got = null; string msg = null;
                AlarmEngine e = new AlarmEngine(Path.Combine(Path.GetTempPath(), "cs_cmin_" + Guid.NewGuid().ToString("N")), delegate () { return st; });
                e.Zone = "cube"; e.OnLog = delegate (string s) { };
                e.Deliver = delegate (int id, string level, string m, AlarmInfo info) { if (id == 1001) { got = info; msg = m; } return true; };
                AlarmConfig c = OneRule("5550200");
                c.Rules[0].MinCalls = 10; c.Rules[0].MinFailures = 1; c.Rules[0].CancelMinSeconds = min;
                c.Rules[0].FailResults = new List<string>(new string[] { "rejected", "failed", "abandoned" });
                e.Use(c); e.Evaluate(at);
                if (got != null) got.Pct = msg;   // carry the message out for the checks below
                return got;
            };
            AlarmInfo every = run(0);
            Check(every != null && every.Failed == 4 && every.Calls == 54, "0 s: every cancel counts (4 of 54)");
            AlarmInfo four = run(4);
            Check(four != null && four.Failed == 2, "4 s: only the two 5-second cancels count");
            Check(four != null && four.Pct.Contains("canceled while ringing 2 (after 4 s or more)"), "the alarm says so");
            Check(four != null && four.Pct.Contains("called=\"5550200\" result=rejected,failed  and  called=\"5550200\" result=abandoned dur>=4"), "and gives the filters that list them");
            Check(run(6) == null, "6 s: no cancel counts, no alarm");

            string err;
            AlarmConfig back = AlarmConfig.FromJson(JsonRead.Parse("{\"rules\":[{\"dnis\":\"1\",\"failResults\":[\"abandoned\"],\"cancelMinSeconds\":4}]}") as Dictionary<string, object>, out err);
            Check(back != null && back.Rules[0].CancelMinSeconds == 4 && back.ToJsonText().Contains("\"cancelMinSeconds\":4"), "saved with the rule");
            Check(AlarmConfig.FromJson(JsonRead.Parse("{\"rules\":[{\"dnis\":\"1\",\"cancelMinSeconds\":700}]}") as Dictionary<string, object>, out err) == null && err.Contains("0 to 600"), "over 600 s is refused");
        }

        /// <summary>A call the CUBE sent to a customer, who answered, then transferred on: one more outbound leg per try.</summary>
        static string[] Transferred(string g, string customer, params string[][] tries)
        {
            List<string> l = new List<string>();
            l.Add(CH("10:01:00.000", g, "10:00:00.000", "5550100", "10", "x (16)", "10:00:05.000", "10:01:00.000", 2, 2750, 550000, 2750, 550000));
            l.Add(Twc("10:01:00.000", "10:00:00.000", "5550100", "5550300", 1, g, "10", g));
            l.Add(CH("10:01:00.004", g, "10:00:00.010", customer, "10", "x (16)", "10:00:05.000", "10:01:00.004", 1, 2750, 550000, 2750, 550000));
            l.Add(Twc("10:01:00.004", "10:00:00.010", "5550100", customer, 2, g, "11", g));
            int id = 12;
            foreach (string[] t in tries)   // { setup, connect (null: not answered), disconnect, cause in hex }
            {
                string hc = t[1] ?? t[2];
                l.Add(CH(t[2], g, t[0], "5550200", t[3], "x (" + Convert.ToInt32(t[3], 16) + ")", hc, t[2], 1, 100, 20000, 100, 20000));
                l.Add(Twc(t[2], t[0], customer, "5550200", id, g, (id++).ToString(), g));
            }
            return l.ToArray();
        }

        static void TestAlarmByLeg()
        {
            Group("alarm number: connected and any leg");
            List<string> all = new List<string>();
            int n = 0;
            Func<string> guid = delegate () { return (0xD0000000L + n++).ToString("X8") + "C2C211F1D3D3E4E4E4E4E4E4"; };
            // sent straight to the number: ten answered, one failed
            for (int i = 0; i < 10; i++) all.AddRange(Pair(guid(), "10", "10", true, "10:01:00.000", "10:01:00.004", 2750, 2750, "5550100"));
            all.AddRange(Pair(guid(), "2F", "29", false, "10:00:03.050", "10:00:03.000", 0, 0, "5550100"));
            // transferred to the number: eight answered
            for (int i = 0; i < 8; i++)
                all.AddRange(Transferred(guid(), "55509" + (10 + i), new string[] { "10:00:06.000", "10:00:06.300", "10:01:00.000", "10" }));
            // and four that are not plain: refused by the far end, canceled at 6 s, canceled at 1 s, a retry that answered
            all.AddRange(Transferred(guid(), "5550930", new string[] { "10:00:06.000", null, "10:00:06.400", "7F" }));
            all.AddRange(Transferred(guid(), "5550931", new string[] { "10:00:06.000", null, "10:00:12.000", "10" }));
            all.AddRange(Transferred(guid(), "5550932", new string[] { "10:00:06.000", null, "10:00:07.000", "10" }));
            all.AddRange(Transferred(guid(), "5550933", new string[] { "10:00:06.000", null, "10:00:06.500", "22" }, new string[] { "10:00:06.600", "10:00:06.900", "10:01:00.000", "10" }));
            Store st = Feed(all.ToArray());

            QueryParams all0 = new QueryParams();
            Func<string, int> count = delegate (string text)
            {
                QueryParams q = new QueryParams(); q.Filter = Filter.Parse(text);
                Check(q.Filter.Error == null, "filter " + text + " parses");
                return Query.Run(st, q).Matched.Count;
            };
            Eq(Query.Run(st, all0).Matched.Count, 23, "23 calls in all");
            Eq(count("called=\"5550200\""), 11, "the Called table's count");
            Eq(count("connected=\"5550200\""), 9, "the Connected-to table's: answered ones only");
            Eq(count("connected=\"5550200\" result=answered"), 9, "connected can only be answered");
            Eq(count("called=\"5550930\" result=answered"), 1, "a transfer nobody answered still reads as answered overall");

            // the rule against the window, with cancels counting after 4 s
            Func<string, double, string[]> run = delegate (string field, double cancelMin)
            {
                AlarmInfo got = null; string msg = "";
                string[] state = new string[1];
                AlarmEngine e = new AlarmEngine(Path.Combine(Path.GetTempPath(), "cs_leg_" + Guid.NewGuid().ToString("N")), delegate () { return st; });
                e.Zone = "cube"; e.OnLog = delegate (string x) { if (x.Contains("calls failed")) state[0] = x; };
                e.Deliver = delegate (int id, string level, string m, AlarmInfo info) { if (id == 1001) { got = info; msg = m; } return true; };
                AlarmConfig c = OneRule("5550200");
                c.Rules[0].Field = field; c.Rules[0].MinCalls = 5; c.Rules[0].MinFailures = 1; c.Rules[0].CancelMinSeconds = cancelMin;
                c.Rules[0].FailResults = new List<string>(new string[] { "rejected", "failed", "abandoned" });
                e.Use(c); e.Evaluate(new DateTime(2026, 9, 28, 10, 30, 0));
                return new string[] { got == null ? "none" : got.Failed + " of " + got.Calls, msg, state[0] ?? "" };
            };
            Eq(run("called", 4)[0], "1 of 11", "called: only calls first sent there, as before");
            Eq(run("connected", 4)[0], "2 of 12", "connected: the refused one and the 6 s cancel, of 12 transfers");
            Eq(run("connected", 0)[0], "3 of 12", "connected, every cancel: the 1 s one too");
            Eq(run("anyleg", 4)[0], "3 of 23", "any leg: called and connected together");
            Check(run("connected", 0)[2].Contains("3 of 12 calls failed"), "the last check says the same");
            string conn = run("connected", 4)[1], any = run("anyleg", 4)[1];
            Check(conn.StartsWith("Greater than 1% call failures to DNIS 5550200 on ", StringComparison.Ordinal), "the alarm's description is unchanged");
            Check(conn.Contains("Counts calls transferred to this number, judged by that leg."), "says what it counted");
            Check(conn.Contains("127 "), "the cause is the leg's");
            Check(conn.Contains("canceled while ringing 1 (after 4 s or more)"), "the cancel's length is the leg's, not the call's");
            Check(conn.Contains("filter  legfail=\"5550200\" !called=\"5550200\""), "and gives a filter that lists them");
            Check(any.Contains("Counts every call with a leg to this number") && any.Contains("filter  legfail=\"5550200\""), "any leg says so too");

            // the filter that lists them
            Eq(count("legfail=\"5550200\""), 4, "legfail: the direct failure and three transfers; the retry that answered is not one");
            Eq(count("legfail=\"5550200\" !called=\"5550200\""), 3, "legfail on transfers only");
            Eq(count("legfail=*0200"), 4, "legfail takes a pattern");
            Eq(count("!legfail=\"5550200\""), 19, "and !");
            Eq(count("peer=\"5550200\""), 23, "any leg's calls");
            AlarmRule tr = new AlarmRule(); tr.Dnis = "5550200"; tr.Field = "connected";
            Eq(count(tr.FilterText()), 12, "the connected rule's filter: its 12 calls");
            tr.Field = "anyleg";
            Eq(count(tr.FilterText()), 23, "the any-leg rule's filter: its 23 calls");

            // a call the CUBE refused before placing a leg stays with called and any leg
            Result res; int cause; long len;
            Call refused = new Call();
            refused.Called = "5550200"; refused.Result = Result.Rejected; refused.FinalCause = 28;
            tr.Field = "anyleg";
            Check(tr.Judge(refused, out res, out cause, out len) && res == Result.Rejected && cause == 28, "refused, no leg: any leg counts it as a call");
            tr.Field = "connected";
            Check(!tr.Judge(refused, out res, out cause, out len), "connected does not");

            string err;
            Func<string, AlarmConfig> parse = delegate (string field)
            {
                return AlarmConfig.FromJson(JsonRead.Parse("{\"rules\":[{\"dnis\":\"1\",\"field\":\"" + field + "\"}]}") as Dictionary<string, object>, out err);
            };
            Check(parse("connected") != null && parse("anyleg") != null && parse("called") != null && parse("dialed") != null, "the four choices are accepted");
            Check(parse("anyleg").ToJsonText().Contains("\"field\":\"anyleg\""), "and saved");
            Check(parse("nonsense") == null, "anything else is refused");
            Check(AlarmConfig.FromJson(JsonRead.Parse("{\"rules\":[{\"dnis\":\"1\"}]}") as Dictionary<string, object>, out err).Rules[0].Field == "called", "an older settings file still means called");
        }

        static void TestNoZoneTrace()
        {
            Group("failure trace on a CUBE that prints no zone");
            string guid = "7A7A7A7AB2B211F1ABAB0102A30405F6";
            string iec = "%VOICE_IEC-3-GW: C SCRIPTS: Internal Error (No dialpeer match): IEC=1.1.128.11.5.0 on callID 7027088 GUID=" + guid;
            string other = "%SIP-3-BADPAIR: Unexpected event 8 (SIPSPI_EV_CC_CALL_DISCONNECT) in state 10 (STATE_DEAD)";
            Func<string, string> bare = delegate (string clock) { return H(clock).Replace(" EDT", ""); };
            Func<string, string[]> trace = delegate (string utc)
            {
                return new string[] {
                    "9221321: 401959390: Sep 28 " + utc + ": //7027088/7A7A7A7AABAB/",
                    "------------------ Cover Buffer ---------------",
                    "Search-key       = :5550100999:7027088",
                    "  Timestamp      = Sep 28 " + utc,
                    "  CallID         = 7027088",
                    "  Called-Number  = 5550100999",
                    "  Calling-Number = ",
                    "  GUID           = 7A7A7A7AABAB",
                    "  Cause-code     = unassigned number (1)",
                    "-----------------------------------------------" };
            };
            Func<string[], string[], string[], List<string>> lines = delegate (string[] a, string[] b, string[] c)
            {
                List<string> l = new List<string>(a); l.AddRange(b); l.AddRange(c); return l;
            };
            string[] none = new string[0];

            // six hours behind UTC, as a Mountain CUBE in summer: the error is stamped on its clock, the trace in UTC
            Store st = Feed(lines(new string[] { bare("15:01:08.379") + iec }, trace("21:01:08.377"), new string[] { bare("15:01:09.000") + other }).ToArray());
            Call c1 = Only(st, guid);
            Check(c1 != null && c1.Dumps != null && c1.Dumps.Count == 1, "the trace is on its call");
            TraceDump d1 = c1.Dumps[0];
            Eq(TimeUtil.Fmt(d1.Ts), "2026-09-28 15:01:08.377", "the trace is on the CUBE's clock");
            Check(!d1.TsIsUtc, "converted");
            Eq(TimeUtil.Fmt(st.Sources[0].LastMs), "2026-09-28 15:01:09.000", "the newest record stays on the CUBE's clock, not six hours on");
            Check(c1.DurMs < 1000, "the call lasts under a second, not six hours (" + c1.DurMs + " ms)");
            bool onLane = false;
            foreach (SysEvent ev in st.Sources[0].Events) if (ev.Kind == "dump") onLane = TimeUtil.Fmt(ev.Ts) == "2026-09-28 15:01:08.377";
            Check(onLane, "on the events lane at that time too");

            // the first thing in a file: the next message tells
            st = Feed(lines(none, trace("21:01:08.377"), new string[] { bare("15:01:12.000") + other }).ToArray());
            Eq(TimeUtil.Fmt(st.Sources[0].FirstMs), "2026-09-28 15:01:08.377", "first in the file: settled by the next message");
            Eq(TimeUtil.Fmt(st.Sources[0].LastMs), "2026-09-28 15:01:12.000", "and the newest record is that message");

            // a half-hour zone
            st = Feed(lines(new string[] { bare("20:31:08.379") + iec }, trace("15:01:08.377"), none).ToArray());
            Eq(TimeUtil.Fmt(Only(st, guid).Dumps[0].Ts), "2026-09-28 20:31:08.377", "a half-hour zone");

            // a quiet CUBE: the last message was eight minutes earlier
            st = Feed(lines(new string[] { bare("14:53:08.000") + other }, trace("21:01:08.377"), none).ToArray());
            Eq(TimeUtil.Fmt(st.Sources[0].LastMs), "2026-09-28 15:01:08.377", "a message eight minutes before still gives the zone");

            // a clock that really is UTC
            st = Feed(lines(new string[] { bare("21:01:08.379") + iec }, trace("21:01:08.377"), none).ToArray());
            Eq(TimeUtil.Fmt(Only(st, guid).Dumps[0].Ts), "2026-09-28 21:01:08.377", "a CUBE on UTC: unchanged");
            Check(!Only(st, guid).Dumps[0].TsIsUtc, "and on its own clock");

            // nothing to convert by: it stays marked UTC and does not count as the CUBE's newest record
            st = Feed(lines(none, trace("21:01:08.377"), none).ToArray());
            Eq(st.Sources[0].LastMs, -1L, "only a trace: no record time taken from it");
            Check(st.Calls.Count == 1 && st.Calls[0].Dumps[0].TsIsUtc, "still marked UTC");

            // a zone printed later in the stream does not change a CUBE that does print one
            st = Feed(lines(new string[] { H("15:01:08.379") + iec }, trace("19:01:08.377"), new string[] { H("15:01:09.000") + other }).ToArray());
            Eq(TimeUtil.Fmt(Only(st, guid).Dumps[0].Ts), "2026-09-28 15:01:08.377", "a CUBE that prints its zone: by the zone, as before");
        }

        static void TestServiceBits()
        {
            Group("service");
            // the newest 24 hours of files per CUBE
            List<LogFile> files = new List<LogFile>();
            for (int i = 0; i < 72; i++)
            {
                LogFile f = new LogFile(); f.SourceName = "10.9.9.1"; f.Path = "x-" + i + ".log";
                f.NameMs = TimeUtil.Ms(2026, 10, 20, 0, 0, 0, 0) + i * 3600000L;
                files.Add(f);
            }
            LoadOptions o = new LoadOptions(); o.Hours = 24;
            SortedDictionary<string, List<LogFile>> plan = Loader.Plan(files, o);
            Eq(plan["10.9.9.1"].Count, 25, "the newest file and the 24 hours before it");
            Eq(plan["10.9.9.1"][0].Path, "x-47.log", "the oldest kept");

            string err;
            HostOptions h = Program.ParseHost(new string[] { "--logs", "D:\\Syslog Server\\Logs", "--serve", "8792", "--local-only", "--alarms" }, out err);
            Check(h != null && h.Load.Root == "D:\\Syslog Server\\Logs" && h.Port == 8792 && h.LocalOnly && h.Alarms && h.Load.Hours == 24, "the service's arguments (24 h by default)");
            Check(Program.ParseHost(new string[] { "--logs", "x", "--frobnicate" }, out err) == null && err.Contains("frobnicate"), "an unknown argument is refused");
            Check(Program.ParseHost(new string[] { "--serve" }, out err) == null && err.Contains("--logs"), "--logs is required");
            string bin = CubeScopeService.BinPath("D:\\bats\\CubeScope.exe", h, null);
            Eq(bin, "\\\"D:\\bats\\CubeScope.exe\\\" --service --logs \\\"D:\\Syslog Server\\Logs\\\" --serve 8792 --hours 24 --local-only --alarms", "the service command line");

            Check(WebServer.WriteAllowed("1", null, 8792), "a change from the page (no Origin) is allowed");
            Check(WebServer.WriteAllowed("1", "http://localhost:8792", 8792) && WebServer.WriteAllowed("1", "http://127.0.0.1:8792/", 8792), "from its own origin too");
            Check(!WebServer.WriteAllowed(null, null, 8792), "no header: refused");
            Check(!WebServer.WriteAllowed("1", "http://evil.example", 8792), "another site's origin: refused");
            Check(!WebServer.WriteAllowed("1", "http://localhost:9999", 8792), "another port's origin: refused");
        }

        static bool Balanced(string json)
        {
            int depth = 0;
            bool str = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (str) { if (c == '\\') i++; else if (c == '"') str = false; continue; }
                if (c == '"') str = true;
                else if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') { depth--; if (depth < 0) return false; }
            }
            return depth == 0 && !str;
        }

        static void TestReport()
        {
            Group("report");
            Store st = Feed(FiveLeg());
            StringWriter w = new StringWriter();
            Report.Csv(st, st.Calls, w);
            string[] rows = w.ToString().Replace("\r", "").TrimEnd('\n').Split('\n');
            Eq(rows.Length, 2, "header and one call");
            Eq(rows[0].Split(',').Length, Report.CsvHead.Length, "header width");
            Check(rows[1].IndexOf("=\"5550100100\"", StringComparison.Ordinal) >= 0, "numbers kept as text for spreadsheets");
            Check(rows[1].IndexOf(",answered,called,16,", StringComparison.Ordinal) >= 0, "result, released by and cause");
            w = new StringWriter();
            Report.Summary(st, new QueryParams(), w, 0.1);
            string sum = w.ToString();
            Check(sum.IndexOf("2026-09-28 Mon", StringComparison.Ordinal) >= 0, "summary has the day row");
            Check(sum.IndexOf("Released by", StringComparison.Ordinal) >= 0, "summary has who released");
            w = new StringWriter();
            Eq(Report.Trace(st, "9A9A9A9AFFFF", null, w, 5), 1, "a short GUID that matches nothing");
            w = new StringWriter();
            Eq(Report.Trace(st, G5, null, w, 5), 0, "trace by GUID");
            Check(w.ToString().IndexOf("6C754A", StringComparison.Ordinal) >= 0, "trace lists the legs");
            w = new StringWriter();
            Eq(Report.Trace(st, "9A9A9A9AC2C2", null, w, 5), 0, "trace by the 12-character trace-dump form");
            w = new StringWriter();
            Eq(Report.Trace(st, "5550188888", null, w, 5), 0, "trace by number");
            w = new StringWriter();
            Eq(Report.Trace(st, "transfers>=1", null, w, 5), 0, "trace by filter");
        }

        static void TestYear()
        {
            Group("year");
            LineParser p = new LineParser(0, 2026, 12);
            Header h = Header.Parse("1: 2: Jan  1 00:00:05.000 EST: %SIP-5-TLS_CONNECTION: x");
            Eq(TimeUtil.Fmt(p.HeaderMs(h)), "2027-01-01 00:00:05.000", "January after December is next year");
            p = new LineParser(0, 2027, 1);
            h = Header.Parse("1: 2: Dec 31 23:59:59.000 EST: %SIP-5-TLS_CONNECTION: x");
            Eq(TimeUtil.Fmt(p.HeaderMs(h)), "2026-12-31 23:59:59.000", "December before January is last year");
            // an accounting record moves the reference year forward
            p = new LineParser(0, 2026, 12);
            p.Feed("1: 2: Jan  1 00:00:05.000 EST: %VOIPAAA-5-VOIP_CALL_HISTORY: CallLegType 2, ConnectionId 1A2B, SetupTime 23:59:00.000 EST Thu Dec 31 2026, PeerAddress , PeerSubAddress , DisconnectCause 10  , DisconnectText normal call clearing (16), ConnectTime 23:59:01.000 EST Thu Dec 31 2026, DisconnectTime 00:00:05.000 EST Fri Jan 1 2027, CallOrigin 2, ChargedUnits 0, InfoType 2, TransmitPackets 0, TransmitBytes 0, ReceivePackets 0, ReceiveBytes 0", 1);
            Eq(p.RefYear, 2027, "year taken from the record");
            ParsedChunk c = p.Take();
            Leg g = (Leg)c.Records[0];
            Eq(g.TalkMs, 64000L, "a call across midnight on New Year");
        }

        static void TestPrefixes()
        {
            Group("other prefixes");
            // a receiver that writes its own prefix still yields the accounting, which carries its own times
            string core = CH("10:00:10.000", "A6A6A6A6C2C211F1D3D3E4E4E4E4E4E4", "10:00:00.000", "5550100", "10", Normal,
                             "10:00:01.000", "10:00:10.000", 2, 450, 90000, 450, 90000);
            string odd = "2026-09-28T14:00:10Z 10.9.9.1 local7.notice " + core.Substring(core.IndexOf("%VOIPAAA", StringComparison.Ordinal));
            LineParser p = new LineParser(0, 2026, 9);
            p.Feed(odd, 1);
            ParsedChunk c = p.Take();
            Eq(c.Accounting, 1L, "accounting found behind an unknown prefix");
            // the ios sequence counter: gaps are missing messages, a drop is a restart
            p = new LineParser(0, 2026, 9);
            p.Feed("1: 100: Sep 28 10:00:00.000 EDT: %SIP-5-TLS_CONNECTION: a", 1);
            p.Feed("2: 101: Sep 28 10:00:01.000 EDT: %SIP-5-TLS_CONNECTION: b", 2);
            p.Feed("3: 105: Sep 28 10:00:02.000 EDT: %SIP-5-TLS_CONNECTION: c", 3);
            p.Feed("4: 7: Sep 28 10:00:03.000 EDT: %SIP-5-TLS_CONNECTION: d", 4);
            c = p.Take();
            Eq(c.IosGaps, 1L, "one gap");
            Eq(c.IosMissing, 3L, "three messages missing");
            Eq(c.IosRestarts, 1L, "a counter that goes backwards restarted");
            Eq(LineParser.KindOf("CUBE-5-LICENSE_INFO"), "licence", "licence kind");
            Eq(LineParser.KindOf("SIP-2-TLS_HANDSHAKE_FAILED"), "tls-fail", "tls failure kind");
            Eq(LineParser.KindOf("SEC_LOGIN-5-LOGIN_SUCCESS"), "login", "login kind");
            Eq(LineParser.KindOf("PARSER-5-CFGLOG_LOGGEDCMD"), "config", "config kind");
            p = new LineParser(0, 2026, 9);
            p.Feed("1: 2: Sep 28 03:00:01.273 EDT: %CUBE-5-LICENSE_INFO: Requesting for 17 CUBE Enhanced trunk licenses", 1);
            SysEvent ev = (SysEvent)p.Take().Records[0];
            Eq(ev.Value, 17.0, "licence count read");
        }

        // ---------------------------------------------------------------- files

        static string TempDir(string name)
        {
            string d = Path.Combine(Path.GetTempPath(), "cubescope-selftest-" + name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(d);
            return d;
        }

        static void TestLoader()
        {
            Group("loader");
            string root = TempDir("loader");
            try
            {
                string a = Path.Combine(root, "10.9.9.1"), b = Path.Combine(root, "10.9.9.2");
                Directory.CreateDirectory(a); Directory.CreateDirectory(b);
                File.WriteAllLines(Path.Combine(a, "10.9.9.1-2026-09-28-12.log"), FiveLeg());
                File.WriteAllText(Path.Combine(a, "10.9.9.1-2026-09-28-12.sha256"), "00");
                File.WriteAllLines(Path.Combine(a, "10.9.9.1-2026-09-28-12.merged.log"), FiveLeg());
                File.WriteAllLines(Path.Combine(a, "10.9.9.1-2026-09-01-12.log"), Pair("A7A7A7A7C2C211F1D3D3E4E4E4E4E4E4", "10", "10", true, "10:01:00.000", "10:01:00.004", 2750, 2750, "5550100"));
                File.WriteAllLines(Path.Combine(b, "10.9.9.2-2026-09-28-13.log"), Pair("A8A8A8A8C2C211F1D3D3E4E4E4E4E4E4", "10", "10", true, "10:01:00.000", "10:01:00.004", 2750, 2750, "5550100"));
                List<LogFile> found = Loader.Find(root, "*.log");
                Eq(found.Count, 3, "sha256 and the merged copy skipped");
                LogFile d = Loader.Describe(Path.Combine(a, "10.9.9.1-2026-09-28-12.log"));
                Eq(d.SourceName, "10.9.9.1", "source name from the file name");
                Eq(TimeUtil.Fmt(d.NameMs), "2026-09-28 12:00:00.000", "hour from the file name");
                LoadOptions o = new LoadOptions(); o.Root = root; o.Quiet = true; o.Days = 14;
                Store st = Loader.LoadAll(o, null);
                Eq(st.Sources.Count, 2, "two CUBEs");
                Eq(st.Sources[0].Name, "10.9.9.1", "first source");
                Eq(st.Sources[0].FilesRead, 1, "the September 1 file is older than 14 days");
                Eq(st.Calls.Count, 2, "a call from each CUBE");
                o.Days = 0;
                st = Loader.LoadAll(o, null);
                Eq(st.Sources[0].FilesRead, 2, "--all reads it");
                o.Cubes.Add("10.9.9.2");
                st = Loader.LoadAll(o, null);
                Eq(st.Sources.Count, 1, "--cube picks one");
                LoadOptions r = new LoadOptions(); r.Root = root; r.Quiet = true;
                r.FromMs = TimeUtil.Ms(2026, 9, 1, 0, 0, 0, 0); r.ToMs = TimeUtil.Ms(2026, 9, 2, 0, 0, 0, 0);
                st = Loader.LoadAll(r, null);
                Eq(st.Calls.Count, 1, "a range reads only the files near it");
            }
            finally { try { Directory.Delete(root, true); } catch (Exception) { } }
        }

        static void TestTailer()
        {
            Group("tailer");
            string root = TempDir("tail");
            try
            {
                string f1 = Path.Combine(root, "10.9.9.1-2026-09-28-14.log");
                string[] five = FiveLeg();
                // the receiver is mid-way through writing the second leg's history line
                File.WriteAllText(f1, five[0] + "\r\n" + five[1] + "\r\n" + five[2] + "\r\n" + five[3].Substring(0, 120));
                LoadOptions o = new LoadOptions(); o.Root = root; o.Quiet = true; o.Days = 0; o.Live = true;
                Store st = Loader.LoadAll(o, null);
                Eq(Only(st, G5).Legs.Count, 1, "first leg loaded, the unfinished line left alone");
                Eq(st.Sources[0].Skipped + st.Sources[0].BadRecords, 0L, "nothing read cut short");
                Tailer t = new Tailer(st, o);
                t.IntervalMs = 3600000;
                t.Start();
                // the receiver finishes that line and appends more; the last line is again unfinished
                using (StreamWriter w = new StreamWriter(f1, true))
                {
                    w.Write(five[3].Substring(120) + "\r\n" + five[4] + "\r\n");
                    w.Write(five[5].Substring(0, 40));
                }
                t.Poll();
                lock (st.Sync) { st.DeriveDirty(); Eq(Only(st, G5).Legs.Count, 2, "appended leg read"); }
                Check(!Only(st, G5).Legs[1].Bound, "its TWC is still a partial line");
                using (StreamWriter w = new StreamWriter(f1, true)) w.Write(five[5].Substring(40) + "\r\n");
                t.Poll();
                lock (st.Sync) Check(Only(st, G5).Legs[1].Bound, "the partial line completed and bound");
                // the next hour's file appears
                File.WriteAllLines(Path.Combine(root, "10.9.9.1-2026-09-28-15.log"), new string[] { five[6], five[7], five[8], five[9], five[10], five[11] });
                t.Poll();
                lock (st.Sync)
                {
                    st.DeriveDirty();
                    Call c = Only(st, G5);
                    Eq(c.Legs.Count, 5, "the new hour's file followed");
                    Eq(c.A2tMs, 1618L, "and the call completes across the two files");
                    Eq(st.Sources[0].Files.Count, 2, "file list grew");
                }
                t.Stop();
            }
            finally { try { Directory.Delete(root, true); } catch (Exception) { } }
        }

        static void TestLauncher()
        {
            Group("launcher");
            string root = TempDir("launch");
            try
            {
                Launcher.FolderResolution r = Launcher.Resolve(root, 2);
                Eq(r.Matches.Count, 0, "empty folder");
                Check(r.Message.IndexOf("No .log files", StringComparison.Ordinal) >= 0, "says why");
                string sub = Path.Combine(root, "10.9.9.1");
                Directory.CreateDirectory(sub);
                File.WriteAllText(Path.Combine(sub, "10.9.9.1-2026-09-28-12.log"), "x");
                r = Launcher.Resolve(root, 2);
                Eq(r.Matches.Count, 1, "found one level down");
                Eq(r.Files, 1, "one file");
                Eq(Launcher.Resolve(Path.Combine(root, "missing"), 2).Matches.Count, 0, "missing folder");
                Eq(Launcher.FreePort(8792, 5, new List<int> { 8792, 8793 }), 8794, "first free port");
                Eq(Launcher.FreePort(8792, 2, new List<int> { 8792, 8793 }), 8792, "all taken: the first");
            }
            finally { try { Directory.Delete(root, true); } catch (Exception) { } }
        }

        // ---------------------------------------------------------------- 2026-10-08 Codex sweep
        // One check per reproduced defect, built like the sweep's own probes.

        static readonly long T10 = TimeUtil.Ms(2026, 9, 28, 10, 0, 0, 0);

        static Store SweepStore() { Store s = new Store(); s.AddSource("cube", ""); return s; }

        // a leg does not carry its call key; the sweep helpers remember it beside the leg
        static readonly Dictionary<Leg, string> SweepKeys = new Dictionary<Leg, string>();

        static void SweepIngest(Store st, params object[] records)
        {
            ParsedChunk ch = new ParsedChunk();
            for (int i = 0; i < records.Length; i++)
            {
                Leg g = records[i] as Leg;
                if (g != null) ch.AddLeg(g, SweepKeys[g]); else ch.Records.Add(records[i]);
            }
            st.Ingest(st.Sources[0], ch);
            st.DeriveDirty();
        }

        static Leg SweepLeg(string key, long start, long end)
        {
            Leg g = new Leg();
            SweepKeys[g] = key;
            g.Setup = start; g.Connect = start + 1000; g.Disconnect = end;
            g.Origin = 1; g.Peer = "5550100100"; g.Cause = 16; g.CauseText = "normal";
            return g;
        }

        static string SweepEvent(int seq, string clock)
        {
            return seq + ": " + seq + ": Sep 28 " + clock + " EDT: %SYS-5-CONFIG_I: made-up configuration event";
        }

        static void TestSweep20261008()
        {
            const string K = "AAAAAAAA11111111BBBBBBBB22222222", K2 = "CCCCCCCC11111111BBBBBBBB22222222";

            Group("sweep: range is not widened by the bucket");
            Store st = SweepStore();
            SweepIngest(st, SweepLeg(K, T10, T10 + 10000), SweepLeg(K2, T10 + 1800000, T10 + 1810000));
            QueryParams q = new QueryParams();
            q.From = T10 + 1800000; q.To = T10 + 1860000; q.Bucket = 3600;
            QueryResult r = Query.Run(st, q);
            Eq(r.Calls, 1, "10:30-10:31 with hourly points holds only the 10:30 call");
            Eq(r.From, q.From, "the requested start is kept");
            Eq(r.To, q.To, "the requested end is kept");
            Eq(TimeUtil.Fmt(r.GridFrom), "2026-09-28 10:00:00.000", "the points still start on the hour");
            StringWriter sw = new StringWriter();
            Report.Summary(st, q, sw, 0);
            Check(Regex.IsMatch(sw.ToString(), "Total\\s+1\\s"), "the console summary's day table counts 1 call, not the whole day");

            Group("sweep: every timed record extends the data span");
            st = SweepStore();
            LineParser p = new LineParser(0, 2026, 9);
            p.Feed(SweepEvent(1, "10:00:00.000"), 1);
            st.Ingest(st.Sources[0], p.Take());
            Check(st.Sources[0].FirstMs >= 0, "an event alone sets the span");
            Eq(Query.Run(st, new QueryParams()).Lanes.Count, 1, "and is shown by the default range");
            st = SweepStore();
            TraceDump dump = new TraceDump();
            dump.Ts = T10; dump.CallId = "123"; dump.ShortGuid = "AAAAAAAABBBB"; dump.Called = "5550100100";
            dump.CauseCode = 21; dump.Text = "made-up"; dump.LineCount = 1;
            SweepIngest(st, dump);
            st.Settle(st.Sources[0], true); st.DeriveDirty();
            Eq(Query.Run(st, new QueryParams()).Calls, 1, "a call known only from its failure trace is shown");
            st = SweepStore();
            Feature lone = new Feature();
            lone.Key = K; lone.Padded = K; lone.Fn = "TWC"; lone.Ft = T10; lone.Fid = 5; lone.LegId = 1; lone.Cgn = "1"; lone.Cdn = "2";
            SweepIngest(st, lone);
            Eq(st.Sources[0].FirstMs, T10, "a TWC-only stub leg sets the span");

            Group("sweep: IOS gaps between polls counted once");
            st = SweepStore();
            p = new LineParser(0, 2026, 9);
            p.Feed(SweepEvent(100, "10:00:00.000"), 1);
            st.Ingest(st.Sources[0], p.Take());
            p.Feed(SweepEvent(103, "10:00:01.000"), 2);
            st.Ingest(st.Sources[0], p.Take());
            Eq(st.Sources[0].IosMissing, 2L, "100 then 103 is two messages missing");
            Eq(st.Sources[0].IosGaps, 1L, "in one gap");

            Group("sweep: a repeated TWC record is not a second leg");
            st = SweepStore();
            SweepIngest(st, SweepLeg(K, T10, T10 + 10000));
            Feature twc = new Feature();
            twc.Key = K; twc.Padded = K; twc.Fn = "TWC"; twc.Ft = T10; twc.Fid = 123; twc.LegId = 12; twc.Cgn = "5550199000"; twc.Cdn = "5550100100";
            Feature twc2 = new Feature();
            twc2.Key = K; twc2.Padded = K; twc2.Fn = "TWC"; twc2.Ft = T10; twc2.Fid = 123; twc2.LegId = 12; twc2.Cgn = "5550199000"; twc2.Cdn = "5550100100";
            SweepIngest(st, twc);
            SweepIngest(st, twc2);
            Eq(st.Calls[0].Legs.Count, 1, "one leg");
            Eq(st.Sources[0].Stubs, 0L, "no stub");
            Eq(st.Sources[0].DuplicateFeatures, 1L, "the repeat counted as a duplicate");

            Group("sweep: a reused GUID opens the right call");
            st = SweepStore();
            SweepIngest(st, SweepLeg(K, T10, T10 + 10000));
            Call old = st.Calls[0];
            SweepIngest(st, SweepLeg(K, T10 + 18000000, T10 + 18010000));
            Eq(st.Calls.Count, 2, "two calls under one GUID");
            Check(old.Id != st.Calls[1].Id, "with different ids");
            Eq(WebServer.FindCall(st, old.Id), old, "the 10:00 row opens the 10:00 call");
            Check(Query.CallsJson(st, st.Calls, "start", false, 0, 10).IndexOf("\"id\":" + old.Id, StringComparison.Ordinal) >= 0,
                  "the call list sends the id");

            Group("sweep: concurrency at a bucket boundary");
            st = SweepStore();
            SweepIngest(st, SweepLeg(K, T10, T10 + 60000));
            q = new QueryParams(); q.From = T10; q.To = T10 + 120000; q.Bucket = 60;
            r = Query.Run(st, q);
            Eq(r.SConcCalls[0], 1.0, "up in the first minute");
            Eq(r.SConcCalls[1], 0.0, "a call that ended at 10:01:00.000 is not up in the next minute");
            Eq(r.SConcLegs[1], 0.0, "nor is its leg");

            Group("sweep: negation");
            st = SweepStore();
            Leg a = SweepLeg(K, T10, T10 + 10000), b = SweepLeg(K, T10 + 1, T10 + 10001);
            b.Origin = 2; b.Cause = 47;
            SweepIngest(st, a, b);
            Call two = st.Calls[0];
            Check(Filter.Parse("legcause=16").Match(two, st.Rules), "legcause=16: some leg had 16");
            Check(!Filter.Parse("legcause!=16").Match(two, st.Rules), "legcause!=16: no leg may have 16");
            Check(!Filter.Parse("!legcause=16").Match(two, st.Rules), "!legcause=16 the same");
            Check(Filter.Parse("legcause!=17").Match(two, st.Rules), "legcause!=17 holds");
            Check(Filter.Parse("!talk>=30").Match(two, st.Rules), "!talk>=30 on a 9 s call");
            Check(!Filter.Parse("!talk<30").Match(two, st.Rules), "!talk<30 does not");
            Check(Filter.Parse("!called!=555").Match(two, st.Rules), "!called!=555 is a double negative: called starts with 555");

            Group("sweep: trace honours src= and --filter");
            Eq(Report.Find(st, "src=cube", new QueryParams()).Count, 1, "src= in a trace expression");
            QueryParams busy = new QueryParams(); busy.Filter = Filter.Parse("result=busy");
            Eq(Report.Find(st, "5550100100", busy).Count, 0, "--filter result=busy removes an answered call");
            QueryParams ans = new QueryParams(); ans.Filter = Filter.Parse("result=answered");
            Eq(Report.Find(st, "5550100100", ans).Count, 1, "--filter result=answered keeps it");
            Check(Filter.CurrentSources == null, "trace leaves no source context behind");

            Group("sweep: the tailer finishes a file before the next");
            string root = TempDir("rollover");
            long piece = Tailer.PieceBytes;
            try
            {
                string f1 = Path.Combine(root, "cube-2026-09-28-10.log"), f2 = Path.Combine(root, "cube-2026-09-28-11.log");
                File.WriteAllText(f1, "");
                LoadOptions o = new LoadOptions(); o.Root = root; o.Live = true; o.Days = 0; o.Quiet = true;
                st = Loader.LoadAll(o, null);
                Tailer t = new Tailer(st, o);
                t.IntervalMs = 3600000;
                t.Start();
                Tailer.PieceBytes = 4096;               // many pieces, as a 64 MiB backlog would be
                StringBuilder filler = new StringBuilder();
                for (int i = 0; i < 40; i++) filler.Append(new string('x', 1023)).Append('\n');
                File.AppendAllText(f1, filler.ToString() + string.Join("\r\n", FiveLeg()) + "\r\n");
                File.WriteAllText(f2, SweepEvent(9999, "16:00:00.000") + "\r\n");
                t.Poll();
                t.Poll();
                t.Stop();
                Eq(st.Sources[0].LegsAdded, 5L, "all five legs read although the next hour's file appeared");
                Store fresh = Loader.LoadAll(new LoadOptions { Root = root, Days = 0, Quiet = true }, null);
                Eq(fresh.Sources[0].LegsAdded, 5L, "same as a fresh read of the finished files");
            }
            finally
            {
                Tailer.PieceBytes = piece;
                try { Directory.Delete(root, true); } catch (Exception) { }
            }

            Group("sweep: raw evidence includes a TWC record in the next file");
            root = TempDir("rawtwc");
            try
            {
                string[] five = FiveLeg();
                File.WriteAllLines(Path.Combine(root, "cube-2026-09-28-14.log"), new string[] { five[0] });
                File.WriteAllLines(Path.Combine(root, "cube-2026-09-28-15.log"), new string[] { five[1], five[2] });
                st = Loader.LoadAll(new LoadOptions { Root = root, Days = 0, Quiet = true }, null);
                Check(st.Calls[0].Legs[0].Bound, "the leg binds across the file boundary");
                List<string> raw = Query.RawLines(st, st.Calls[0]);
                Check(raw.Exists(delegate (string l) { return l.IndexOf("fn:TWC", StringComparison.Ordinal) >= 0; }), "its TWC record is in the raw lines");
                Check(raw.Exists(delegate (string l) { return l.IndexOf("fn:BXFER", StringComparison.Ordinal) >= 0; }), "and so is the transfer record");
            }
            finally { try { Directory.Delete(root, true); } catch (Exception) { } }

            Group("sweep: the dashboard drops superseded answers");
            string html = WebServer.EmbeddedDashboard();
            Check(html.IndexOf("if(my !== stateGen) return;", StringComparison.Ordinal) >= 0, "state answers carry their generation");
            Check(html.IndexOf("if(my !== listGen) return;", StringComparison.Ordinal) >= 0, "call-list answers too");
            Check(html.IndexOf("my === detailGen", StringComparison.Ordinal) >= 0, "and call detail");
            Check(html.IndexOf("data-id=\"'+r.id+'\"", StringComparison.Ordinal) >= 0, "rows open calls by id");
        }

        // ---------------------------------------------------------------- dashboard

        static void TestDashboard()
        {
            Group("dashboard");
            string html = WebServer.EmbeddedDashboard();
            Check(html.Length > 5000, "dashboard resource embedded");
            Check(html.IndexOf("<!--STATIC-->", StringComparison.Ordinal) >= 0, "static report marker");
            Check(html.IndexOf("api/state", StringComparison.Ordinal) >= 0, "polls the state");
            Check(html.IndexOf("window.__STATIC", StringComparison.Ordinal) >= 0, "reads a static report's state");
            Check(html.IndexOf("http://cdn", StringComparison.OrdinalIgnoreCase) < 0 &&
                  html.IndexOf("https://", StringComparison.OrdinalIgnoreCase) < 0, "no external references");
            Check(html.IndexOf("fonts.googleapis", StringComparison.OrdinalIgnoreCase) < 0, "no web fonts");
            Eq(Count(html, "<script"), Count(html, "</script>"), "script tags balanced");
            string[] fns = { "function tsChart(", "function setHTML(", "function renderTiles(", "function renderCharts(",
                             "function renderLanes(", "function renderTables(", "function renderList(", "function showCall(",
                             "function load(", "function tipInit(", "function brush(" };
            for (int i = 0; i < fns.Length; i++) Check(html.IndexOf(fns[i], StringComparison.Ordinal) >= 0, "has " + fns[i]);
            Store st = Feed(FiveLeg());
            QueryParams q = new QueryParams();
            QueryResult r = Query.Run(st, q);
            string rep = Report.StaticHtml(st, q, r, "t");
            Check(rep.IndexOf("window.__STATIC = {state:", StringComparison.Ordinal) > 0, "static report carries its state");
            Check(rep.IndexOf("<!--STATIC-->", StringComparison.Ordinal) < 0, "marker replaced");
            Check(rep.IndexOf("};</script>", StringComparison.Ordinal) > 0, "injected script closed");
        }

        static int Count(string s, string what)
        {
            int n = 0, i = 0;
            while ((i = s.IndexOf(what, i, StringComparison.OrdinalIgnoreCase)) >= 0) { n++; i++; }
            return n;
        }

        static void TestTips()
        {
            Group("hover tips");
            string html = WebServer.EmbeddedDashboard();
            int b = html.IndexOf("var TIPS = {", StringComparison.Ordinal);
            Check(b >= 0, "TIPS table present");
            if (b < 0) return;
            int e = html.IndexOf("\n};", b, StringComparison.Ordinal);
            Check(e > b, "TIPS table closed");
            string table = html.Substring(b, e - b);
            string rest = html.Substring(0, b) + html.Substring(e);
            Dictionary<string, string> have = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(table, "\"([a-z]+\\.[A-Za-z0-9]+)\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\""))
                have[m.Groups[1].Value] = m.Groups[2].Value;
            Check(have.Count >= 40, "the dashboard is described, " + have.Count + " tips");
            List<string> missing = new List<string>();
            foreach (Match m in Regex.Matches(rest, "\"((?:tile|panel|col|ui|lane|leg)\\.[A-Za-z0-9]+)\""))
                if (!have.ContainsKey(m.Groups[1].Value) && !missing.Contains(m.Groups[1].Value)) missing.Add(m.Groups[1].Value);
            Check(missing.Count == 0, "every key the page asks for has a description" +
                  (missing.Count > 0 ? " (missing: " + string.Join(", ", missing.ToArray()) + ")" : ""));
            foreach (KeyValuePair<string, string> kv in have)
            {
                Check(kv.Value.Length >= 40 && kv.Value.Length <= 360, "tip " + kv.Key + " is 40-360 characters (" + kv.Value.Length + ")");
                Check(kv.Value.IndexOf('<') < 0, "tip " + kv.Key + " has no markup");
            }
        }

        // ---------------------------------------------------------------- 2026-10-08 low-memory round

        /// <summary>The most intervals up at once per bucket the old way: every start and end sorted.</summary>
        static double[] SortedSweep(List<long[]> iv, int n, long bms, out int peak)
        {
            List<long> ev = new List<long>();
            foreach (long[] x in iv) { ev.Add((x[0] << 1) | 1); ev.Add(x[1] << 1); }
            long[] a = ev.ToArray();
            Array.Sort(a);
            double[] outv = new double[n];
            int level = 0, k = 0;
            peak = 0;
            for (int b = 0; b < n; b++)
            {
                long start = b * bms, end = start + bms;
                while (k < a.Length && (a[k] >> 1) == start && (a[k] & 1) == 0) { level--; k++; }
                int max = level;
                while (k < a.Length && (a[k] >> 1) < end)
                {
                    if ((a[k] & 1) == 1) { level++; if (level > max) max = level; if (level > peak) peak = level; }
                    else level--;
                    k++;
                }
                outv[b] = max;
            }
            return outv;
        }

        static void TestLowMemory()
        {
            Group("low memory: streamed concurrency equals the sorted sweep");
            Random rnd = new Random(20261008);
            for (int round = 0; round < 6; round++)
            {
                Store st = SweepStore();
                List<object> recs = new List<object>();
                for (int i = 0; i < 400; i++)
                {
                    string key = "C0FFEE" + round + "0000000000000000000" + i.ToString("D6");
                    long start = T10 + rnd.Next(0, 2 * 3600) * 1000L + rnd.Next(0, 1000);
                    int legs = 1 + rnd.Next(4);
                    for (int k = 0; k < legs; k++)
                    {
                        // later legs can start minutes after the call, after many later calls began;
                        // some end exactly on a minute
                        long s0 = start + (k == 0 ? 0 : rnd.Next(0, 300) * 1000L);
                        long e0 = rnd.Next(5) == 0 ? ((s0 / 60000) + 1 + rnd.Next(3)) * 60000 : s0 + 2000 + rnd.Next(0, 1800) * 1000L;
                        recs.Add(SweepLeg(key, s0, e0));
                    }
                }
                SweepIngest(st, recs.ToArray());
                QueryParams q = new QueryParams();
                q.Bucket = round % 2 == 0 ? 60 : 300;
                if (round >= 2) { q.From = T10 + 1800000 + round * 7000; q.To = q.From + 3600000 + round * 13000; }
                QueryResult r = Query.Run(st, q);
                List<long[]> civ = new List<long[]>(), liv = new List<long[]>();
                foreach (Call c in st.Calls)
                {
                    if (c.Start >= r.To || (c.Start < r.From && c.End <= r.From)) continue;
                    long cs = Math.Max(c.Start, r.From) - r.GridFrom, ce = Math.Min(Math.Max(c.End, c.Start + 1), r.To) - r.GridFrom;
                    if (ce > cs) civ.Add(new long[] { cs, ce });
                    foreach (Leg g in c.Legs)
                    {
                        long ls = Math.Max(g.Setup, r.From) - r.GridFrom, le = Math.Min(Math.Max(g.Disconnect, g.Setup + 1), r.To) - r.GridFrom;
                        if (le > ls) liv.Add(new long[] { ls, le });
                    }
                }
                int pc, pl;
                double[] ec = SortedSweep(civ, r.N, r.Bucket * 1000L, out pc), el = SortedSweep(liv, r.N, r.Bucket * 1000L, out pl);
                int bad = 0;
                for (int b = 0; b < r.N; b++) if (ec[b] != r.SConcCalls[b] || el[b] != r.SConcLegs[b]) bad++;
                Eq(bad, 0, "round " + round + ": every bucket's calls and legs agree (" + r.N + " buckets)");
                Eq(r.PeakCalls, pc, "round " + round + ": peak calls");
                Eq(r.PeakLegs, pl, "round " + round + ": peak legs");
            }

            Group("low memory: lean records");
            Leg lg = new Leg();
            lg.File = 7;
            Eq(lg.TwcFile, -1, "no TWC record yet");
            lg.TwcFile = 8;
            Eq(lg.TwcFile, 8, "TWC record in the next file");
            lg.TwcFile = 7;
            Eq(lg.TwcFile, 7, "TWC record in the leg's own file");
            Eq(Leg.Bytes(5000000000L), uint.MaxValue, "byte count capped at 4 GB");
            Eq(Leg.Bytes(-1), 0u, "negative byte count is 0");

            Group("low memory: events are capped and counted");
            Store es = SweepStore();
            Source src = es.Sources[0];
            for (int i = 0; i < Source.EventCap + 5; i++)
            {
                SysEvent e = new SysEvent();
                e.Ts = T10 + i; e.Kind = "other"; e.Mnemonic = "SYS-5-X"; e.Text = "made-up " + i;
                src.AddEvent(e);
            }
            Eq(src.Events.Count, Source.EventCap, "kept up to the cap");
            Eq(src.EventsDropped, 5L, "the rest counted");
            LineParser lp = new LineParser(0, 2026, 9);
            lp.Feed("1: 2: Sep 28 10:00:00.000 EDT: %SYS-5-CONFIG_I: " + new string('x', 5000), 1);
            es = SweepStore();
            es.Ingest(es.Sources[0], lp.Take());
            Check(es.Sources[0].Events[0].Text.Length <= Source.EventTextMax + 3, "a long event text is clipped");

            Group("find: audio problems and the pickers' counts");
            Store fs = SweepStore();
            int[][] pk = { new int[] { 0, 0 }, new int[] { 3000, 0 }, new int[] { 1000, 1000 }, new int[] { 3000, 3000 } };
            List<object> frecs = new List<object>();
            for (int i = 0; i < pk.Length; i++)
            {
                Leg g = SweepLeg("F1ND0000000000000000000000000" + i, T10 + i * 120000, T10 + i * 120000 + 61000);
                g.TxPackets = pk[i][0]; g.RxPackets = pk[i][1];
                frecs.Add(g);
            }
            Leg in47 = SweepLeg("F1ND00000000000000000000000000", T10, T10 + 61000);
            in47.Origin = 2; in47.Cause = 47; in47.TxPackets = 1; in47.RxPackets = 1;
            in47.Connect = in47.Disconnect;              // unanswered: not judged for media
            frecs.Add(in47);
            SweepIngest(fs, frecs.ToArray());
            Func<string, int> hits = delegate (string ft)
            {
                Filter fl = Filter.Parse(ft);
                int n = 0;
                foreach (Call c in fs.Calls) if (fl.Match(c, fs.Rules)) n++;
                return n;
            };
            Eq(hits("media=problem"), 3, "problem: none, one-way and low rate");
            Eq(hits("media=bad"), 2, "bad: none and one-way");
            Eq(hits("audio=lowrate"), 1, "audio= is media=");
            Eq(hits("!media=problem"), 1, "not a problem: the two-way call");
            Check(Filter.Parse("media=awful").Error.IndexOf("problem", StringComparison.Ordinal) >= 0, "the error lists problem");
            QueryParams fq = new QueryParams();
            fq.Filter = Filter.Parse("media=twoway");
            QueryResult fr = Query.Run(fs, fq);
            Eq(fr.Calls, 1, "the filter applies to the page");
            Eq(fr.ChoiceCalls, 4, "the pickers count every call in range");
            Eq(fr.ChoiceMedia[(int)Media.None] + fr.ChoiceMedia[(int)Media.OneWay] + fr.ChoiceMedia[(int)Media.LowRate], 3, "three problems offered");
            int c16 = 0, c47 = 0;
            foreach (KeyValuePair<int, int> kv in fr.ChoiceLegCauses.Items()) { if (kv.Key == 16) c16 = kv.Value; if (kv.Key == 47) c47 = kv.Value; }
            Eq(c16, 4, "leg cause 16: once per call, not per leg");
            Eq(c47, 1, "leg cause 47 on one call");
            string fjson = Query.StateJson(fs, fq, fr, "");
            Check(fjson.IndexOf("\"choices\":{\"calls\":4", StringComparison.Ordinal) >= 0, "state JSON carries the choices");
            string page = WebServer.EmbeddedDashboard();
            Check(page.IndexOf("id=\"findRow\"", StringComparison.Ordinal) >= 0, "the page has the Find row");

            Group("command line: a path with spaces typed without quotes");
            string[] argv = { "--logs", @"D:\Syslog", @"Server\Logs", "--all" };
            int ai = 0;
            TextWriter keep = Console.Error;
            Console.SetError(TextWriter.Null);
            try { Eq(Program.PathArg(argv, ref ai), @"D:\Syslog Server\Logs", "the words are one path"); }
            finally { Console.SetError(keep); }
            Eq(ai, 2, "and the next option is read next");
            argv = new string[] { "--logs", @"D:\logs", "--all" };
            ai = 0;
            Eq(Program.PathArg(argv, ref ai), @"D:\logs", "a plain path is left alone");
        }

        /// <summary>A file the receiver named for one hour, whose messages ran from firstAfter to
        /// lastAfter into it, stamped by a CUBE clock offset ms ahead of the receiver's.</summary>
        static void ClockFile(Source s, long nameMs, long offset, long firstAfter, long lastAfter, string label)
        {
            FileClock f = new FileClock();
            f.File = s.Clocks.Count; f.NameMs = nameMs; f.Lines = 100;
            f.First = nameMs + offset + firstAfter; f.Last = nameMs + offset + lastAfter;
            s.Clocks.Add(f);
            if (s.ZoneRuns.Count == 0 || s.ZoneRuns[s.ZoneRuns.Count - 1].Tz != label)
            {
                ZoneRun r; r.From = f.First; r.Tz = label;
                s.ZoneRuns.Add(r);
            }
            long n;
            s.ZoneCounts.TryGetValue(label, out n);
            s.ZoneCounts[label] = n + 100;
        }

        static void TestClock()
        {
            Group("clock: the parser keeps header times, labels and marks");
            LineParser p = new LineParser(0, 2026, 9);
            p.Feed("1: 2: Sep 28 15:00:05.000 EDT: %SYS-5-CONFIG_I: Configured from console", 1);
            p.Feed("3: 3: *Sep 28 15:10:00.000 EDT: %SIP-2-TLS_HANDSHAKE_FAILED: x", 2);
            p.Feed("5: 4: Sep 28 15:59:50.500: %SYS-5-CONFIG_I: Configured from console", 3);
            p.Feed("7: Sep 28 15:59:59.000: //1/A1A1A1A1B2B2/SIP/Msg/ccsipDisplayMsg:", 4);
            ParsedChunk c = p.Take();
            Eq(c.ClockLines, 3L, "three syslog messages timed; the trace line is not one");
            Eq(TimeUtil.Fmt(c.ClockFirst), "2026-09-28 15:00:05.000", "earliest header time");
            Eq(TimeUtil.Fmt(c.ClockLast), "2026-09-28 15:59:50.500", "latest header time");
            Eq(c.ClockNotSet, 1L, "one message marked *");
            Eq(c.ZoneCounts["EDT"], 2L, "two labelled EDT");
            Eq(c.ZoneCounts[""], 1L, "one with no label");
            Eq(c.ZoneRuns.Count, 2, "the label changed once");
            Eq(c.FileIndex, 0, "the chunk knows its file");

            Group("clock: the store keeps one entry per file");
            Store ms = new Store();
            Source m = ms.AddSource("10.9.9.3", "");
            m.Files.Add(Path.Combine("x", "10.9.9.3-2026-09-28-13.log"));
            ms.Ingest(m, c);
            LineParser p2 = new LineParser(0, 2026, 9);
            p2.Feed("9: 5: Sep 28 15:20:00.000 EDT: %SYS-5-CONFIG_I: Configured from console", 5);
            ms.Ingest(m, p2.Take());
            Eq(m.Clocks.Count, 1, "a second stretch of the same file joins its entry");
            Eq(m.Clocks[0].Lines, 4L, "lines added up");
            Eq(TimeUtil.Fmt(m.Clocks[0].NameMs), "2026-09-28 13:00:00.000", "the receiver's hour from the file name");
            Eq(m.ClockNotSet, 1L, "marks carried to the source");
            Eq(m.ZoneRuns.Count, 3, "label runs: EDT, none, EDT");

            Group("clock: offsets from the file hours");
            long H = Clock.Hour, day = TimeUtil.Ms(2026, 9, 28, 0, 0, 0, 0);
            Store st = new Store();
            Source s = st.AddSource("10.9.9.1", "");
            // two hours ahead of the receiver and 30 s fast, for four hours; then an hour more ahead
            for (int i = 0; i < 4; i++) ClockFile(s, day + (10 + i) * H, 2 * H + 30000, 200, H - 500, "EDT");
            for (int i = 4; i < 6; i++) ClockFile(s, day + (10 + i) * H, 3 * H + 30000, 200, H - 500, "EDT");
            List<ClockSegment> segs = Clock.Segments(s);
            Eq(segs.Count, 2, "the jump starts a second stretch");
            Eq(segs[0].Files, 4, "four files in the first");
            Eq(segs[0].ZoneDiff, 2 * H, "zone part: two hours");
            Eq(segs[0].Offset, 2 * H + 29500, "best estimate: the least error the bounds allow");
            Check(segs[0].DriftLo > Clock.DriftNoteMs, "a clock proven fast");
            Eq(segs[1].ZoneDiff, 3 * H, "then three hours");
            Eq(segs[0].Tz, "EDT", "the stretch's label");

            Group("clock: the receiver's zone from the labels");
            ServerZone z = Clock.Server(st);
            Eq(z.How, "labels", "worked out from the labels");
            Eq(z.HoursAt(day + 11 * H), -6.0, "EDT two hours ahead: the receiver is UTC-6");
            Eq(z.HoursAt(day + 15 * H), -7.0, "an hour further ahead later: UTC-7 then");
            Eq(z.HoursAt(day + 40 * H), -7.0, "outside every stretch: the nearest one");
            ClockReport r = Clock.Report(st, s, z);
            Check(r.Measured, "measured");
            Eq(r.ClockHours, -4.0, "its clock reads UTC-4, as its label says");
            Check(Has(r.Notes, "fast against the syslog server"), "the fast clock is reported");
            Check(Has(r.Notes, "The offset changed 1 time"), "the change is reported");
            Check(!Has(r.Notes, "but its clock reads"), "label and clock agree");

            Group("clock: labels that do not fit");
            Store st2 = new Store();
            st2.ServerZoneId = "UTC-6";
            Source a = st2.AddSource("10.9.9.4", "");
            // an hour behind a UTC-6 receiver in July, labelled MST: right clock, no summer time
            long jul = TimeUtil.Ms(2026, 7, 14, 0, 0, 0, 0);
            for (int i = 0; i < 3; i++) ClockFile(a, jul + (9 + i) * H, -H, 300, H - 300, "MST");
            ServerZone z2 = Clock.Server(st2);
            Eq(z2.How, "option", "the option wins");
            ClockReport r2 = Clock.Report(st2, a, z2);
            Eq(r2.ClockHours, -7.0, "clock reads UTC-7");
            Check(Has(r2.Notes, "'clock summer-time' is probably missing"), "MST in July noticed");
            Check(!Has(r2.Notes, "fast against") && !Has(r2.Notes, "slow against"), "no clock error");
            Source b = st2.AddSource("10.9.9.5", "");
            for (int i = 0; i < 3; i++) ClockFile(b, jul + (9 + i) * H, 0, 300, H - 300, "");
            ClockReport r3 = Clock.Report(st2, b, z2);
            Check(Has(r3.Notes, "add show-timezone"), "no label: say how to add one");
            Eq(r3.ClockHours, -6.0, "an unlabelled clock still gets its zone from the receiver's");
            Source e = st2.AddSource("10.9.9.6", "");
            ClockFile(e, -1, 0, 300, H - 300, "EDT");
            ClockFile(e, jul + 9 * H, 2 * H, 300, 3 * H, "EDT");
            int timed, unplaced;
            Eq(Clock.Segments(e, out timed, out unplaced).Count, 0, "files without an hour, or over an hour long, place nothing");
            Eq(unplaced, 2, "both counted as unplaced");

            Group("clock: words and zones");
            Eq(Clock.ErrorText(29500, 30200), "30 s fast", "fast, a range under 2 s shown as one figure");
            Eq(Clock.ErrorText(-45000, -41000), "41 s to 45 s slow", "slow range");
            Eq(Clock.ErrorText(-1054, 3648), "no error seen (could be up to 3.6 s off)", "within the bounds");
            Eq(Clock.ZoneWords(2 * H), "2 h ahead of the server", "ahead");
            Eq(Clock.ZoneWords(-H - H / 2), "1 h 30 min behind the server", "behind by a half");
            Eq(Clock.ZoneWords(0), "on the server's hour", "same");
            Eq(Clock.Utc(-6), "UTC-6", "UTC-6"); Eq(Clock.Utc(5.5), "UTC+5:30", "UTC+5:30"); Eq(Clock.Utc(0), "UTC", "UTC");
            TimeZoneInfo tzi; double fh;
            Check(Clock.ParseZone("UTC+5:30", out tzi, out fh) && fh == 5.5, "UTC+5:30 read");
            Check(Clock.ParseZone("utc", out tzi, out fh) && fh == 0, "utc read");
            Check(Clock.ParseZone("Mountain Standard Time", out tzi, out fh) && tzi != null, "a Windows zone name");
            Check(!Clock.ParseZone("Nowhere Time", out tzi, out fh), "an unknown name refused");
            Check(Clock.UsDaylight(new DateTime(2026, 7, 4)) && !Clock.UsDaylight(new DateTime(2026, 12, 25)), "US summer time");

            Group("clock: in the state and the summary");
            QueryResult qr = Query.Run(st, new QueryParams());
            string json = Query.StateJson(st, new QueryParams(), qr, "");
            Check(json.IndexOf("\"clock\":{\"serverHow\":\"labels\"", StringComparison.Ordinal) >= 0, "state JSON has the clock section");
            Check(Balanced(json), "and it is well formed");
            StringWriter sw = new StringWriter();
            Clock.WriteText(st, sw);
            Check(sw.ToString().IndexOf("2 h", StringComparison.Ordinal) >= 0 || sw.ToString().IndexOf("3 h ahead of the server", StringComparison.Ordinal) >= 0,
                  "summary says how far ahead");

            TestZoneViews();
            TestLiveSpan();
            TestConfigHistory();
        }

        /// <summary>Two CUBEs on different clocks: an EDT one and a CEST one, both read by a UTC-6 receiver.</summary>
        static void TestZoneViews()
        {
            Group("zone views: CUBEs in different zones line up");
            string root = TempDir("zones");
            try
            {
                // EDT CUBE: the receiver's hour 13 holds 15:00-15:59 EDT (two hours ahead)
                List<string> a = new List<string>();
                a.Add(H("15:00:01.000") + "%SYS-5-CONFIG_I: Configured from console by admin");
                a.AddRange(FiveLeg());
                a.Add(H("15:59:59.000") + "%SYS-5-CONFIG_I: Configured from console by admin");
                File.WriteAllLines(Path.Combine(root, "10.9.9.1-2026-09-28-13.log"), a.ToArray());
                // CEST CUBE: the receiver's hour 08 holds 16:00-16:59 CEST (eight hours ahead); its call
                // starts after the EDT one on its own clock but five hours earlier in real time
                List<string> b = new List<string>();
                b.Add(H("16:00:01.000") + "%SYS-5-CONFIG_I: Configured from console by admin");
                foreach (string l in Pair("B4B4B4B4C2C211F1D3D3E4E4E4E4E4E4", "10", "10", true, "16:00:10.000", "16:00:10.004", 2750, 2750, "5550100"))
                    b.Add(l.Replace("10:00:", "16:00:"));
                b.Add(H("16:59:59.000") + "%SYS-5-CONFIG_I: Configured from console by admin");
                for (int i = 0; i < b.Count; i++) b[i] = b[i].Replace(" EDT", " CEST");
                File.WriteAllLines(Path.Combine(root, "10.9.9.2-2026-09-28-08.log"), b.ToArray());
                LoadOptions o = new LoadOptions(); o.Root = root; o.Quiet = true; o.Days = 0;
                Store st = Loader.LoadAll(o, null);
                Eq(st.Sources.Count, 2, "two CUBEs");
                Eq(Clock.Server(st).HoursAt(TimeUtil.Ms(2026, 9, 28, 10, 0, 0, 0)), -6.0, "both labels say the receiver is UTC-6");
                QueryParams q = new QueryParams(); q.Bucket = 3600;
                QueryResult own = Query.Run(st, q);
                Eq(own.Matched.Count, 2, "two calls");
                Eq(own.Matched[0].Src, 0, "on their own clocks the EDT call comes first (14:59 before 16:00)");

                q.Zone = "server";
                QueryResult sv = Query.Run(st, q);
                Eq(sv.Matched[0].Src, 1, "on the receiver's clock the CEST call comes first");
                Eq(TimeUtil.Fmt(sv.Zone.ToDisplay(1, sv.Matched[0].Start)), "2026-09-28 08:00:00.000", "CEST 16:00 is 08:00 on the receiver");
                Eq(TimeUtil.Fmt(sv.Zone.ToDisplay(0, sv.Matched[1].Start)), "2026-09-28 12:59:43.013", "EDT 14:59:43 is 12:59:43 on the receiver");
                Eq(TimeUtil.Fmt(sv.DataFirst), "2026-09-28 08:00:00.000", "the data span is on the receiver's clock");
                Eq(sv.Zone.NotConverted.Count, 0, "every CUBE converted");
                Check(sv.Zone.Label(sv.DataFirst).StartsWith("server clock (UTC-6)", StringComparison.Ordinal), "label names the receiver's zone");
                int b08 = -1, b12 = -1;
                for (int i = 0; i < sv.N; i++)
                {
                    if (TimeUtil.Fmt(sv.T[i] * 1000) == "2026-09-28 08:00:00.000") b08 = i;
                    if (TimeUtil.Fmt(sv.T[i] * 1000) == "2026-09-28 12:00:00.000") b12 = i;
                }
                Check(b08 >= 0 && sv.SCalls[b08] == 1 && b12 >= 0 && sv.SCalls[b12] == 1, "each call counted in its receiver-clock hour");

                q.Zone = "UTC";
                QueryResult u = Query.Run(st, q);
                Eq(TimeUtil.Fmt(u.Zone.ToDisplay(1, u.Matched[0].Start)), "2026-09-28 14:00:00.000", "CEST 16:00 is 14:00 UTC");
                Eq(TimeUtil.Fmt(u.Zone.ToDisplay(0, u.Matched[1].Start)), "2026-09-28 18:59:43.013", "EDT 14:59:43 is 18:59:43 UTC");
                q.Zone = "Eastern Standard Time";
                QueryResult e = Query.Run(st, q);
                Eq(TimeUtil.Fmt(e.Zone.ToDisplay(0, e.Matched[1].Start)), "2026-09-28 14:59:43.013", "an EDT CUBE in US Eastern: unchanged");
                Eq(e.Zone.Label(e.DataFirst), "EDT", "September in US Eastern is EDT");

                // a range on the display clock picks calls by their display time
                q.Zone = "server"; q.From = TimeUtil.Ms(2026, 9, 28, 12, 0, 0, 0); q.To = TimeUtil.Ms(2026, 9, 28, 13, 0, 0, 0);
                QueryResult part = Query.Run(st, q);
                Eq(part.Matched.Count, 1, "12:00-13:00 on the receiver's clock: the EDT call only");
                Eq(part.Matched[0].Src, 0, "and it is the EDT one");

                // the list, the CSV and the detail follow the display clock
                q.From = -1; q.To = -1;
                QueryResult all = Query.Run(st, q);
                List<Call> desc = Query.Sorted(all.Matched, "start", true, all.Zone);
                Eq(desc[0].Src, 0, "newest first on the receiver's clock");
                Check(Query.CallsJson(st, desc, "start", true, 0, 50, all.Zone).IndexOf("\"cubeStart\":", StringComparison.Ordinal) > 0, "rows keep the CUBE's own start");
                StringWriter csv = new StringWriter();
                Report.Csv(st, Query.Sorted(all.Matched, "start", false, all.Zone), csv, all.Zone);
                string[] rows = csv.ToString().Split('\n');
                Check(rows[0].TrimEnd().EndsWith(",start_on_cube_clock", StringComparison.Ordinal), "CSV adds the CUBE's own start");
                Check(rows[1].StartsWith("2026-09-28 08:00:00.000,", StringComparison.Ordinal), "CSV start on the display clock");
                string detail = Query.CallJson(st, all.Matched[0], false, all.Zone);
                Check(detail.IndexOf("\"zoneStart\":", StringComparison.Ordinal) > 0, "detail adds the start on the display clock");
                string state = Query.StateJson(st, q, all, "");
                Check(state.IndexOf("\"zone\":{\"id\":\"server\"", StringComparison.Ordinal) >= 0 && Balanced(state), "state names the display clock");
                Check(ZoneMap.Valid("server") && ZoneMap.Valid("UTC-6") && !ZoneMap.Valid("Mars Time"), "zone names checked");
            }
            finally { try { Directory.Delete(root, true); } catch (Exception) { } }
        }

        static void TestLiveSpan()
        {
            Group("live: a CUBE whose clock runs ahead cannot empty \"Last hour\"");
            string root = TempDir("livespan");
            try
            {
                // CUBE 1 is right: the receiver's hour 13 holds 15:00-15:59 EDT
                List<string> a = new List<string>();
                a.Add(H("15:00:01.000") + "%SYS-5-CONFIG_I: Configured from console by admin");
                a.AddRange(FiveLeg());
                foreach (string l in Pair("B5B5B5B5C2C211F1D3D3E4E4E4E4E4E4", "10", "10", true, "15:30:10.000", "15:30:10.004", 2750, 2750, "5550100"))
                    a.Add(l.Replace("10:00:", "15:30:"));
                a.Add(H("15:59:59.000") + "%SYS-5-CONFIG_I: Configured from console by admin");
                File.WriteAllLines(Path.Combine(root, "10.9.9.1-2026-09-28-13.log"), a.ToArray());
                // CUBE 2 sent one message stamped seven hours ahead
                List<string> b = new List<string>();
                b.Add(H("15:05:00.000") + "%SYS-5-CONFIG_I: Configured from console by admin");
                b.Add(H("22:30:00.000") + "%SYS-5-CONFIG_I: Configured from console by admin");
                File.WriteAllLines(Path.Combine(root, "10.9.9.2-2026-09-28-13.log"), b.ToArray());
                LoadOptions o = new LoadOptions(); o.Root = root; o.Quiet = true; o.Days = 0;
                Store st = Loader.LoadAll(o, null);
                st.ServerZoneId = "UTC-6";
                Eq(st.Sources.Count, 2, "two CUBEs");
                DateTime now = new DateTime(2026, 9, 28, 20, 0, 30, DateTimeKind.Utc);   // 16:00:30 EDT
                long aLast = TimeUtil.Ms(2026, 9, 28, 15, 59, 59, 0), bLast = TimeUtil.Ms(2026, 9, 28, 22, 30, 0, 0);

                ZoneMap own = ZoneMap.Build(st, "cube");
                long first, last;
                Query.Span(st, -1, own, out first, out last);
                Eq(TimeUtil.Fmt(last), TimeUtil.Fmt(bLast), "the plain span ends at the stray future record");
                Eq(own.NowDisplay(now), -1L, "each CUBE's own clock has no single now");
                Eq(TimeUtil.Fmt(ZoneMap.NowOnCube(st, 0, now, Clock.Server(st))), "2026-09-28 16:00:30.000", "now on the EDT CUBE");
                Query.SpanLive(st, -1, own, now, out first, out last);
                Eq(TimeUtil.Fmt(last), TimeUtil.Fmt(aLast), "live: the CUBE seven hours ahead is left out of the newest record");
                Eq(Query.LiveEnd(own, last, now), last, "on the CUBEs' own clocks the range ends at the newest believable record");
                Query.SpanLive(st, 1, own, now, out first, out last);
                Eq(TimeUtil.Fmt(last), TimeUtil.Fmt(bLast), "that CUBE on its own keeps its newest record");

                ZoneMap utc = ZoneMap.Build(st, "UTC");
                Eq(TimeUtil.Fmt(utc.NowDisplay(now)), "2026-09-28 20:00:30.000", "now in UTC");
                Eq(TimeUtil.Fmt(ZoneMap.Build(st, "UTC-6").NowDisplay(now)), "2026-09-28 14:00:30.000", "now at UTC-6");
                Eq(TimeUtil.Fmt(ZoneMap.Build(st, "Eastern Standard Time").NowDisplay(now)), "2026-09-28 16:00:30.000", "now in US Eastern (summer time)");
                Eq(TimeUtil.Fmt(ZoneMap.Build(st, "server").NowDisplay(now)), "2026-09-28 14:00:30.000", "now on the receiver's clock");
                Query.SpanLive(st, -1, utc, now, out first, out last);
                Eq(TimeUtil.Fmt(last), "2026-09-28 19:59:59.000", "live in UTC: the newest believable record");
                Eq(TimeUtil.Fmt(Query.LiveEnd(utc, last, now)), "2026-09-28 20:00:30.000", "current data: the range ends now");
                long old = TimeUtil.Ms(2026, 9, 27, 6, 0, 0, 0);
                Eq(Query.LiveEnd(utc, old, now), old, "data older than 12 hours: the range ends at its newest record");

                // the web server's "Last hour"
                WebServer ws = new WebServer(st, 0);
                ws.UtcNow = delegate () { return now; };
                System.Collections.Specialized.NameValueCollection qs = new System.Collections.Specialized.NameValueCollection();
                qs["last"] = "3600";
                QueryParams q;
                Eq(ws.ParseParams(qs, out q), null, "parsed");
                Eq(TimeUtil.Fmt(q.To - 1), TimeUtil.Fmt(bLast), "an archive counts back from the newest record");
                ws.Following = true;
                Eq(ws.ParseParams(qs, out q), null, "parsed live");
                Eq(TimeUtil.Fmt(q.To - 1), TimeUtil.Fmt(aLast), "live, own clocks: ends at the right CUBE's newest record");
                Eq(TimeUtil.Fmt(q.From), "2026-09-28 14:59:59.001", "and starts an hour before");
                Check(q.Live && q.LiveNow == now, "the query carries live and now");
                QueryResult r = Query.Run(st, q);
                Check(r.Matched.Count >= 1, "the right CUBE's 15:30 call is in the window");
                Eq(TimeUtil.Fmt(r.DataLast), TimeUtil.Fmt(aLast), "the data span reported to the page leaves the stray record out");
                qs["zone"] = "UTC";
                Eq(ws.ParseParams(qs, out q), null, "parsed live in UTC");
                Eq(TimeUtil.Fmt(q.To - 1), "2026-09-28 20:00:30.000", "live in UTC: ends now");
                Eq(TimeUtil.Fmt(q.From), "2026-09-28 19:00:30.001", "and starts an hour before now");
                Check(Query.Run(st, q).Matched.Count >= 1, "the call is in that hour");
            }
            finally { try { Directory.Delete(root, true); } catch (Exception) { } }
        }

        static string Cfg(string clock, int n)
        {
            return H(clock) + "%SYS-5-CONFIG_I: Configured from console by admin" + n + " on vty0";
        }

        static void TestConfigHistory()
        {
            Group("configuration changes: the newest ten, kept past the window");
            string root = TempDir("cfghist"), root2 = TempDir("cfghist2"), data = TempDir("cfgdata");
            try
            {
                // CUBE 1: twelve changes 15:01-15:12 EDT; CUBE 2: two, at 15:30 and 15:45
                List<string> a = new List<string>();
                for (int i = 1; i <= 12; i++) a.Add(Cfg("15:" + (i < 10 ? "0" : "") + i + ":00.000", i));
                a.Add(H("15:20:00.000") + "%LINK-3-UPDOWN: Interface GigabitEthernet0/0/1, changed state to up");
                File.WriteAllLines(Path.Combine(root, "10.9.9.1-2026-09-28-13.log"), a.ToArray());
                File.WriteAllLines(Path.Combine(root, "10.9.9.2-2026-09-28-13.log"), new string[] { Cfg("15:30:00.000", 30), Cfg("15:45:00.000", 45) });
                LoadOptions o = new LoadOptions(); o.Root = root; o.Quiet = true; o.Days = 0;
                Store st = Loader.LoadAll(o, null);
                st.ServerZoneId = "UTC-6";
                Eq(st.Sources.Count, 2, "two CUBEs");

                ConfigHistory mem = ConfigHistory.FromStore(st);
                List<ConfigHistory.Entry> all = mem.Newest(10, null);
                Eq(all.Count, 10, "ten of all");
                Check(all[0].Text.IndexOf("admin45", StringComparison.Ordinal) >= 0, "newest first: CUBE 2 at 15:45");
                Check(all[2].Text.IndexOf("admin12", StringComparison.Ordinal) >= 0, "then CUBE 1 from 15:12 down");
                Check(all[9].Text.IndexOf("admin5 ", StringComparison.Ordinal) >= 0, "the tenth is CUBE 1 at 15:05");
                List<ConfigHistory.Entry> one = mem.Newest(10, st.Sources[0].Name);
                Eq(one.Count, 10, "ten of CUBE 1 (it has twelve)");
                Check(one[9].Text.IndexOf("admin3 ", StringComparison.Ordinal) >= 0, "CUBE 1's tenth newest is 15:03");
                Eq(one[0].Mnemonic, "SYS-5-CONFIG_I", "the message kind");
                Eq(TimeUtil.Fmt(one[0].Utc), "2026-09-28 19:12:00.000", "converted to UTC (EDT + 4 h)");

                // the state lists them on the page's clock, for the CUBE picked
                QueryParams q = new QueryParams();
                q.From = TimeUtil.Ms(2026, 9, 28, 15, 0, 0, 0); q.To = TimeUtil.Ms(2026, 9, 28, 15, 2, 0, 0);
                QueryResult r = Query.Run(st, q);
                string json = Query.StateJson(st, q, r, "");
                Check(Balanced(json), "state still balanced");
                int at = json.IndexOf("\"configChanges\":[", StringComparison.Ordinal);
                Check(at > 0 && json.IndexOf("admin45", at, StringComparison.Ordinal) > 0, "outside the range shown, still listed");
                Check(json.IndexOf("\"t\":" + TimeUtil.Ms(2026, 9, 28, 15, 45, 0, 0) / 1000, at, StringComparison.Ordinal) > 0, "on the CUBE's own clock");
                q.Src = 0;
                r = Query.Run(st, q);
                json = Query.StateJson(st, q, r, "");
                at = json.IndexOf("\"configChanges\":[", StringComparison.Ordinal);
                int end = json.IndexOf("]", at, StringComparison.Ordinal);
                Check(json.IndexOf("admin45", at, StringComparison.Ordinal) < 0 || json.IndexOf("admin45", at, StringComparison.Ordinal) > end, "one CUBE picked: only its own");
                q.Src = -1; q.Zone = "UTC";
                r = Query.Run(st, q);
                json = Query.StateJson(st, q, r, "");
                at = json.IndexOf("\"configChanges\":[", StringComparison.Ordinal);
                Check(json.IndexOf("\"t\":" + TimeUtil.Ms(2026, 9, 28, 19, 45, 0, 0) / 1000, at, StringComparison.Ordinal) > 0, "on UTC: 15:45 EDT is 19:45");

                // kept in a file: a later window without the old records still lists them
                string file = Path.Combine(data, "config-history.json");
                ConfigHistory kept = new ConfigHistory(file);
                kept.Merge(st);
                Check(File.Exists(file), "written to the data folder");
                File.WriteAllLines(Path.Combine(root2, "10.9.9.1-2026-09-28-14.log"), new string[] {
                    Cfg("16:10:00.000", 70),
                    H("16:11:00.000") + "%PARSER-5-CFGLOG_LOGGEDCMD: User:admin  logged command:dial-peer voice 100 voip" });
                LoadOptions o2 = new LoadOptions(); o2.Root = root2; o2.Quiet = true; o2.Days = 0;
                Store st2 = Loader.LoadAll(o2, null);
                st2.ServerZoneId = "UTC-6";     // two lines are too few to tell the receiver's zone by
                ConfigHistory again = new ConfigHistory(file);
                again.Load();
                Eq(again.Newest(10, null).Count, 10, "read back after a restart");
                again.Merge(st2);
                List<ConfigHistory.Entry> after = again.Newest(10, null);
                Eq(after[0].Mnemonic, "PARSER-5-CFGLOG_LOGGEDCMD", "a logged command counts as a change, newest first");
                Check(after[1].Text.IndexOf("admin70", StringComparison.Ordinal) >= 0, "then the new window's 16:10");
                Check(after[2].Text.IndexOf("admin45", StringComparison.Ordinal) >= 0, "and the old window's, no longer in the store");
                Eq(again.Newest(10, st.Sources[0].Name).Count, 10, "still ten per CUBE");
                ConfigHistory third = new ConfigHistory(file);
                third.Load();
                Eq(third.Newest(1, null)[0].Mnemonic, "PARSER-5-CFGLOG_LOGGEDCMD", "the file has the new ones too");
                DateTime w = File.GetLastWriteTimeUtc(file);
                File.SetLastWriteTimeUtc(file, w.AddMinutes(-5));
                again.Merge(st2);
                Eq(File.GetLastWriteTimeUtc(file), w.AddMinutes(-5), "nothing new: the file is not rewritten");
                File.WriteAllText(file, "not json");
                ConfigHistory bad = new ConfigHistory(file);
                bool echo = Log.Echo;
                Log.Echo = false;
                try { bad.Load(); } finally { Log.Echo = echo; }
                Eq(bad.Newest(10, null).Count, 0, "a damaged file starts empty");
            }
            finally
            {
                foreach (string d in new string[] { root, root2, data })
                    try { Directory.Delete(d, true); } catch (Exception) { }
            }
        }

        static bool Has(List<string> l, string part)
        {
            for (int i = 0; i < l.Count; i++) if (l[i].IndexOf(part, StringComparison.Ordinal) >= 0) return true;
            return false;
        }
    }
}
