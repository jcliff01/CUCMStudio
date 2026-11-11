using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CubeScope
{
    /// <summary>The console summary, the call trace, CSV and the static HTML report.</summary>
    public static class Report
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static string N(long v) { return v.ToString("N0", Inv); }
        static string Pct(double part, double whole) { return whole > 0 ? (100.0 * part / whole).ToString("0.0", Inv) + "%" : "-"; }

        // ------------------------------------------------------------ console summary

        public static void Summary(Store st, QueryParams q, TextWriter w, double loadSec)
        {
            QueryResult all;
            lock (st.Sync)
            {
                QueryParams day = Copy(q);
                day.Bucket = 86400;
                day.MaxPoints = 100000;
                all = Query.Run(st, day);
            }
            w.WriteLine();
            for (int i = 0; i < st.Sources.Count; i++)
            {
                Source s = st.Sources[i];
                w.WriteLine("Source " + s.Name + ": " + s.FilesRead + " files, " + N(s.Lines) + " lines, " +
                            (s.Bytes / 1048576.0).ToString("0", Inv) + " MB, records " + TimeUtil.FmtShort(s.FirstMs) + " to " +
                            TimeUtil.FmtShort(s.LastMs) + " " + (s.Tz ?? "") + ", read in " + s.LoadSeconds.ToString("0.0", Inv) + " s");
            }
            w.WriteLine();
            lock (st.Sync) CubeScope.Clock.WriteText(st, w);
            if (!all.Zone.Identity)
            {
                w.WriteLine("Times below are on " + all.Zone.Name + (all.Zone.NotConverted.Count > 0 ?
                            " (left on their own clock, nothing to convert by: " + string.Join(", ", all.Zone.NotConverted.ToArray()) + ")" : "") + ".");
            }
            if (!q.Filter.IsEmpty) w.WriteLine("Filter: " + q.Filter.Text);
            w.WriteLine();
            Table(w, all, "Day", "yyyy-MM-dd ddd");
            if (all.N <= 3 || q.Bucket > 0)
            {
                QueryResult hr;
                lock (st.Sync)
                {
                    QueryParams h = Copy(q);
                    h.Bucket = q.Bucket > 0 ? q.Bucket : 3600;
                    h.MaxPoints = 100000;
                    hr = Query.Run(st, h);
                }
                w.WriteLine();
                Table(w, hr, hr.Bucket >= 86400 ? "Day" : "Bucket", hr.Bucket >= 86400 ? "yyyy-MM-dd ddd" : "MM-dd HH:mm");
            }
            w.WriteLine();
            int tot = all.Calls;
            w.WriteLine("Released by (answered calls by the leg that cleared first; unanswered by result):");
            w.WriteLine("  caller " + N(all.ByRelease[1]) + " (" + Pct(all.ByRelease[1], tot) + ")   called " + N(all.ByRelease[2]) +
                        " (" + Pct(all.ByRelease[2], tot) + ")   network/CUBE " + N(all.ByRelease[3]) + " (" + Pct(all.ByRelease[3], tot) +
                        ")   tie " + N(all.ByRelease[4]) + "   unknown " + N(all.ByRelease[0]));
            int cd = 0, cc = 0;
            for (int i = 0; i < all.CancelDialer.Length; i++) { cd += all.CancelDialer[i]; cc += all.CancelCaller[i]; }
            if (cd + cc > 0)
            {
                w.WriteLine("Canceled while ringing: " + N(cd + cc) + " (" + Pct(cd + cc, tot) + " of calls): " + N(cd) + " stopped by the dialer, " +
                            N(cc) + " by the caller hanging up. Seconds from start to cancel:");
                w.WriteLine("  " + CancelLine("dialer", all.CancelDialer));
                w.WriteLine("  " + CancelLine("caller", all.CancelCaller));
            }
            int ans = all.ByResult[(int)Result.Answered];
            w.WriteLine("Media on answered calls: two-way " + N(all.ByMedia[1]) + " (" + Pct(all.ByMedia[1], ans) + "), low packet rate " +
                        N(all.ByMedia[2]) + " (" + Pct(all.ByMedia[2], ans) + "), one-way " + N(all.ByMedia[3]) + " (" + Pct(all.ByMedia[3], ans) +
                        "), none " + N(all.ByMedia[4]) + " (" + Pct(all.ByMedia[4], ans) + ") of " + N(ans) + " answered (signalling-only legs excluded)");
            w.WriteLine("Ring p50/p95 " + TimeUtil.FmtDur(all.RingP50) + " / " + TimeUtil.FmtDur(all.RingP95) +
                        "   talk p50/p95 " + TimeUtil.FmtDur(all.TalkP50) + " / " + TimeUtil.FmtDur(all.TalkP95) +
                        (all.A2tN > 0 ? "   answer to transfer connect p50/p95 " + TimeUtil.FmtDur(all.A2tP50) + " / " +
                                        TimeUtil.FmtDur(all.A2tP95) + " over " + N(all.A2tN) + " calls" : ""));
            w.WriteLine("Peak concurrent: " + N(all.PeakCalls) + " calls at " + TimeUtil.FmtShort(all.PeakCallsAt) + ", " +
                        N(all.PeakLegs) + " legs at " + TimeUtil.FmtShort(all.PeakLegsAt));
            w.WriteLine();
            w.WriteLine("Causes of calls not answered:");
            for (int i = 0; i < all.CauseRows.Count && i < 12; i++)
            {
                CauseRow c = all.CauseRows[i];
                if (c.Calls == c.Answered) continue;
                int un = c.Calls - c.Answered;
                w.WriteLine("  " + c.Cause.ToString().PadLeft(4) + "  " + (c.Cause < 0 ? "(none)" : Q850.Name(c.Cause)).PadRight(28) +
                            N(un).PadLeft(9) + "  " + ResultSplit(c));
            }
            w.WriteLine();
            WriteTop(w, "Top called numbers", all.TopCalled, 8, all.Calls);
            WriteTop(w, "Top calling numbers", all.TopCalling, 8, all.Calls);
            WriteTop(w, "Top connected-to (where the call actually went)", all.TopConnected, 8, all.Calls);
            if (all.TopDialPeers.Count > 0 || all.QualityCalls > 0)
            {
                if (all.TopDialPeers.Count > 0) WriteTop(w, "By dial-peer", all.TopDialPeers, 12, all.Calls);
                if (all.TopMedia.Count > 0) WriteTop(w, "By far-end media address", all.TopMedia, 8, all.Calls);
                if (all.TopCodecs.Count > 0) WriteTop(w, "By codec", all.TopCodecs, 6, all.Calls);
                if (all.AlertN > 0)
                    w.WriteLine("Time to ringing: median " + Sec(all.AlertP50) + " s, 95th percentile " + Sec(all.AlertP95) + " s, " + N(all.AlertN) + " calls");
                w.WriteLine(all.QualityCalls == 0 ? "Call quality: no measured numbers in these records." :
                            "Call quality: " + N(all.QualityCalls) + " calls measured, " + N(all.QualityPoor) + " poor" +
                            (all.MosN > 0 ? ", MOS average " + (all.MosSum / all.MosN).ToString("0.00", Inv) + ", worst " + all.WorstMos.ToString("0.00", Inv) : "") +
                            ", highest loss " + all.WorstLoss.ToString("0.00", Inv) + "%" +
                            (all.WorstJitter > 0 ? ", highest jitter " + all.WorstJitter.ToString("0.0000", Inv) : ""));
                w.WriteLine();
            }
            w.WriteLine("Events: " + LaneText(all));
            w.WriteLine();
            Health(st, w);
            w.WriteLine("Timing: load " + loadSec.ToString("0.0", Inv) + " s, query " + all.Ms.ToString("0", Inv) + " ms, memory " +
                        (GC.GetTotalMemory(true) / 1048576.0).ToString("0", Inv) + " MB in use, " +
                        (System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64 / 1048576.0).ToString("0", Inv) + " MB private");
        }

        static string ResultSplit(CauseRow c)
        {
            StringBuilder sb = new StringBuilder();
            for (int k = 2; k < 7; k++)
            {
                if (c.Res[k] == 0) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(Names.Results[k]).Append(' ').Append(N(c.Res[k]));
            }
            return sb.ToString();
        }

        static string LaneText(QueryResult r)
        {
            if (r.Lanes.Count == 0) return "none";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < r.Lanes.Count; i++)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(r.Lanes[i].Kind).Append(' ').Append(N(r.Lanes[i].Count));
            }
            return sb.ToString();
        }

        public static void Health(Store st, TextWriter w)
        {
            for (int i = 0; i < st.Sources.Count; i++)
            {
                Source s = st.Sources[i];
                w.WriteLine("Feed " + s.Name + ": legs " + N(s.LegsAdded) + ", without their TWC record " + N(s.Unbound) +
                            ", TWC without a history line " + N(s.Stubs) + ", duplicate legs dropped " + N(s.DuplicateLegs) +
                            ", features " + N(s.Features) + ", IEC " + N(s.Iecs) + ", failure traces " + N(s.Dumps) +
                            ", lines not used " + N(s.Skipped) + ", unreadable records " + N(s.BadRecords) +
                            ", messages missing by IOS sequence number " + N(s.IosMissing) + " in " + N(s.IosGaps) + " gaps" +
                            (s.IosRestarts > 0 ? ", counter restarts " + N(s.IosRestarts) : "") +
                            (s.LegsFromFiles > 0 && s.LegsFromSyslog > 0 ? ", legs in both feeds " + N(s.LegsInBoth) + ", only in syslog " + N(s.LegsFromSyslog - s.LegsInBoth) + ", only in call record files " + N(s.LegsFromFiles - s.LegsInBoth) : "") +
                            (s.AlsoNames.Count > 0 ? ", also logs as " + string.Join(", ", s.AlsoNames.ToArray()) : ""));
                for (int k = 0; k < s.Warnings.Count && k < 5; k++) w.WriteLine("  warning: " + s.Warnings[k]);
            }
        }

        /// <summary>"dialer  under 2 s 3, 2-5 s 0, ..., 60 s+ 1" over QueryResult.CancelEdgesSec.</summary>
        // "dialer  piles at 9-11 s: 3,793, 32-36 s: 1,975, other lengths: 275"
        static string CancelLine(string who, int[] bins)
        {
            int total = 0, piled = 0;
            for (int i = 0; i < bins.Length; i++) total += bins[i];
            StringBuilder sb = new StringBuilder(who.PadRight(8));
            if (total == 0) return sb.Append("none").ToString();
            List<CancelClump> piles = Query.Clumps(bins);
            if (piles.Count == 0) return sb.Append("no piles: ").Append(N(total)).Append(" spread over many lengths").ToString();
            sb.Append("piles at ");
            for (int i = 0; i < piles.Count; i++)
            {
                CancelClump k = piles[i];
                piled += k.Calls;
                sb.Append(i == 0 ? "" : ", ").Append(k.To == 0 ? k.From + " s or more" : k.From + "-" + k.To + " s").Append(": ").Append(N(k.Calls));
            }
            if (total > piled) sb.Append(", other lengths: ").Append(N(total - piled));
            return sb.ToString();
        }

        static void WriteTop(TextWriter w, string title, List<NumberRow> rows, int n, int allCalls)
        {
            if (rows == null || rows.Count == 0) return;
            w.WriteLine(title + ":");
            long calls = 0, ans = 0, talk = 0, bad = 0;
            int shown = 0;
            for (int i = 0; i < rows.Count && i < n; i++)
            {
                NumberRow r = rows[i];
                calls += r.Calls; ans += r.Answered; talk += r.TalkMs; bad += r.NoMedia; shown++;
                w.WriteLine(TopLine((r.Number.Length == 0 ? "(empty)" : r.Number), r.Calls, r.Answered, r.TalkMs, r.NoMedia, allCalls));
            }
            w.WriteLine(TopLine("these " + shown, calls, ans, talk, bad, allCalls));
        }

        /// <summary>Calls with their share of every call, answered, talk, unanswered and bad media with their shares.</summary>
        static string TopLine(string label, long calls, long ans, long talk, long bad, int allCalls)
        {
            return "  " + label.PadRight(22) + N(calls).PadLeft(9) + " calls (" + Pct(calls, allCalls) + " of all)  " +
                   Pct(ans, calls).PadLeft(6) + " answered  avg talk " + (ans > 0 ? TimeUtil.FmtDur(talk / ans) : "-") +
                   "  not answered " + N(calls - ans) + " (" + Pct(calls - ans, calls) + ")" +
                   (bad > 0 ? "  bad media " + N(bad) + " (" + Pct(bad, ans) + " of answered)" : "");
        }

        static void Table(TextWriter w, QueryResult r, string label, string fmt)
        {
            string[] head = { label, "Calls", "Answered", "ASR", "Avg talk", "Canceled ring", "No answer", "Busy", "Rejected",
                              "Failed", "No media", "One-way", "Transfers", "Peak calls" };
            int[] wid = { 16, 9, 9, 7, 9, 14, 10, 6, 9, 8, 9, 8, 10, 11 };
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < head.Length; i++) sb.Append(i == 0 ? head[i].PadRight(wid[i]) : head[i].PadLeft(wid[i]));
            w.WriteLine(sb.ToString());
            double tCalls = 0, tAns = 0, tTalk = 0;
            for (int b = 0; b < r.N; b++)
            {
                if (r.SCalls[b] == 0 && r.SConcCalls[b] == 0) continue;
                sb.Length = 0;
                sb.Append(TimeUtil.ToDate(r.T[b] * 1000).ToString(fmt, Inv).PadRight(wid[0]));
                double calls = r.SCalls[b], ans = r.SAnswered[b];
                tCalls += calls; tAns += ans; tTalk += r.STalkSum[b];
                sb.Append(N((long)calls).PadLeft(wid[1]));
                sb.Append(N((long)ans).PadLeft(wid[2]));
                sb.Append(Pct(ans, calls).PadLeft(wid[3]));
                sb.Append((ans > 0 ? TimeUtil.FmtDur((long)(r.STalkSum[b] / ans)) : "-").PadLeft(wid[4]));
                sb.Append(N((long)r.SResult[2][b]).PadLeft(wid[5]));
                sb.Append(N((long)r.SResult[3][b]).PadLeft(wid[6]));
                sb.Append(N((long)r.SResult[4][b]).PadLeft(wid[7]));
                sb.Append(N((long)r.SResult[5][b]).PadLeft(wid[8]));
                sb.Append(N((long)r.SResult[6][b]).PadLeft(wid[9]));
                sb.Append(N((long)r.SNoMedia[b]).PadLeft(wid[10]));
                sb.Append(N((long)r.SOneWay[b]).PadLeft(wid[11]));
                sb.Append(N((long)r.STransfers[b]).PadLeft(wid[12]));
                sb.Append(N((long)r.SConcCalls[b]).PadLeft(wid[13]));
                w.WriteLine(sb.ToString());
            }
            sb.Length = 0;
            sb.Append("Total".PadRight(wid[0]));
            sb.Append(N(r.Calls).PadLeft(wid[1]));
            sb.Append(N(r.ByResult[1]).PadLeft(wid[2]));
            sb.Append(Pct(r.ByResult[1], r.Calls).PadLeft(wid[3]));
            sb.Append((r.TalkN > 0 ? TimeUtil.FmtDur(r.TalkSum / r.TalkN) : "-").PadLeft(wid[4]));
            for (int k = 2; k < 7; k++) sb.Append(N(r.ByResult[k]).PadLeft(wid[3 + k]));
            sb.Append(N(r.ByMedia[4]).PadLeft(wid[10]));
            sb.Append(N(r.ByMedia[3]).PadLeft(wid[11]));
            sb.Append(N(r.Transfers).PadLeft(wid[12]));
            sb.Append(N(r.PeakCalls).PadLeft(wid[13]));
            w.WriteLine(sb.ToString());
        }

        public static QueryParams Copy(QueryParams q)
        {
            QueryParams c = new QueryParams();
            c.From = q.From; c.To = q.To; c.Bucket = q.Bucket; c.Filter = q.Filter; c.Src = q.Src; c.MaxPoints = q.MaxPoints;
            c.Zone = q.Zone;
            return c;
        }

        // ------------------------------------------------------------ trace

        /// <summary>Calls matching a GUID (any spelling) or a number, every record printed with its raw lines.</summary>
        public static int Trace(Store st, string what, QueryParams q, TextWriter w, int max)
        {
            List<Call> hits = Find(st, what, q);
            if (hits.Count == 0) { w.WriteLine("No call matches " + what + "."); return 1; }
            w.WriteLine(hits.Count + " call" + (hits.Count == 1 ? "" : "s") + " match " + what +
                        (hits.Count > max ? "; the newest " + max + " are shown" : "") + ".");
            int from = Math.Max(0, hits.Count - max);
            for (int i = from; i < hits.Count; i++) TraceOne(st, hits[i], w);
            return 0;
        }

        public static List<Call> Find(Store st, string what, QueryParams q)
        {
            List<Call> hits = new List<Call>();
            string w = (what ?? "").Trim();
            bool hex = w.Length >= 12 && GuidForm.IsHex(w);
            // a filter expression (called=251* has=dump ...) as well as a GUID or a number
            bool expr = !hex && w.IndexOfAny(new char[] { '=', '<', '>', ' ' }) >= 0;
            Filter f = hex ? null : Filter.Parse(expr ? w : "any=\"" + w + "\"");
            if (f != null && f.Error != null) throw new ArgumentException("trace: " + f.Error);
            // --filter narrows a trace the same way it narrows every other output
            Filter also = q != null && q.Filter != null && !q.Filter.IsEmpty && q.Filter.Error == null ? q.Filter : null;
            lock (st.Sync)
            {
                List<Call> calls = st.ByStart();
                string key = hex ? GuidForm.Key(w) : null, up = w.ToUpperInvariant();
                // src= in a filter needs the source names (Query.Run sets the same context)
                List<Source> before = Filter.CurrentSources;
                Filter.CurrentSources = st.Sources;
                try
                {
                    for (int i = 0; i < calls.Count; i++)
                    {
                        Call c = calls[i];
                        // dates: a trace wants every call that was up during the range, so a call that
                        // started before --from but was still going is included (the dashboard counts by start)
                        if (q != null && q.From >= 0 && c.End < q.From) continue;
                        if (q != null && q.To >= 0 && c.Start >= q.To) continue;
                        bool hit;
                        if (hex)
                            hit = c.Key == key || c.Key == up || (c.Padded != null && (c.Padded == up || GuidForm.Short(c.Padded) == up)) ||
                                  (c.Dumps != null && c.Dumps.Exists(delegate (TraceDump d) { return d.ShortGuid == up; }));
                        else hit = f.Match(c, st.Rules);
                        if (hit && also != null) hit = also.Match(c, st.Rules);
                        if (hit) hits.Add(c);
                    }
                }
                finally { Filter.CurrentSources = before; }
            }
            return hits;
        }

        public static void TraceOne(Store st, Call c, TextWriter w)
        {
            Source s = st.Sources[c.Src];
            // the features' keys and values are read back from the log with the raw lines
            Query.CallLineSet lines;
            lock (st.Sync) lines = Query.CallLines(st, c);
            w.WriteLine();
            w.WriteLine(new string('=', 100));
            w.WriteLine("Call " + c.Key + (c.Padded != null ? "  (padded " + c.Padded + ", trace id " + GuidForm.Short(c.Padded) + ")" : "") +
                        "  on " + s.Name);
            w.WriteLine("  " + TimeUtil.Fmt(c.Start) + " to " + TimeUtil.Fmt(c.End) + " " + (s.Tz ?? "") + ", " + TimeUtil.FmtDur(c.DurMs) +
                        "   " + (c.Calling.Length > 0 ? c.Calling : "(no calling number)") + " -> " + c.Called +
                        (c.ConnectedTo.Length > 0 ? " -> connected to " + c.ConnectedTo : ""));
            w.WriteLine("  result " + Names.ResultLabels[(int)c.Result] + (c.FinalCause >= 0 ? " (cause " + c.FinalCause + " " + Q850.Name(c.FinalCause) + ")" : "") +
                        ", released by " + Names.Releases[(int)c.Released] +
                        (c.ReleaseLeg >= 0 && c.ReleaseLeg < c.Legs.Count ? " (leg " + Hex(c.Legs[c.ReleaseLeg].LegId) + ", " + c.Legs[c.ReleaseLeg].Number + ")" : "") +
                        ", ring " + TimeUtil.FmtDur(c.RingMs) + ", talk " + TimeUtil.FmtDur(c.TalkMs) +
                        ", media " + Names.MediaLabels[(int)c.Media] + (c.Bpp > 0 ? " (" + c.Bpp + " bytes/packet)" : "") +
                        (c.Transfers > 0 ? ", " + c.Transfers + " transfer" + (c.Transfers == 1 ? "" : "s") : "") +
                        (c.A2tMs >= 0 ? ", answer to transfer connect " + TimeUtil.FmtDur(c.A2tMs) : "") +
                        (c.Tags != 0 ? ", tags " + st.Rules.TagList(c.Tags) : ""));
            w.WriteLine("  legs (setup order):");
            List<Leg> legs = c.Legs.ToList();
            legs.Sort(delegate (Leg a, Leg b) { return a.Setup.CompareTo(b.Setup); });
            for (int i = 0; i < legs.Count; i++)
            {
                Leg g = legs[i];
                w.WriteLine("    " + Hex(g.LegId).PadRight(7) + (g.FeatureOnly ? "?  " : (g.In ? "in " : "out")) + " " +
                            Clock(g.Setup) + " " + (g.Answered ? Clock(g.Connect) : "  (no answer) ") + " " + Clock(g.Disconnect) +
                            "  peer " + (g.Peer.Length > 0 ? g.Peer : "-").PadRight(19) + " " + (g.Cgn ?? "?") + " -> " + (g.Cdn ?? "?") +
                            "  cause " + g.Cause + "  pk " + g.TxPackets + "/" + g.RxPackets +
                            (g.BytesPerPacket > 0 ? " (" + g.BytesPerPacket + " B)" : "") +
                            (g.SignalOnly ? "  signalling-only" : "") + (!g.Bound && !g.FeatureOnly ? "  NO TWC" : ""));
            }
            if (c.Features != null)
                for (int i = 0; i < c.Features.Count; i++)
                {
                    CallFeature f = c.Features[i];
                    w.WriteLine("  feature " + f.Fn + " at " + Clock(f.Ft) + " leg " + Hex(f.LegId) + ": " + KvText(lines.FeatureKv(f), f));
                }
            if (c.Errors != null)
                for (int i = 0; i < c.Errors.Count; i++)
                {
                    IecError e = c.Errors[i];
                    w.WriteLine("  internal error at " + Clock(e.Ts) + ": " + e.Sub + " " + e.Text + " IEC=" + e.Code + " callID " + e.CallId);
                }
            if (c.Dumps != null)
                for (int i = 0; i < c.Dumps.Count; i++)
                {
                    TraceDump d = c.Dumps[i];
                    w.WriteLine("  failure trace at " + Clock(d.Ts) + (d.TsIsUtc ? " UTC" : "") + ", " + d.LineCount + " lines, cause " + d.CauseText +
                                ", " + s.FileName(d.File) + ":" + d.Line);
                    string[] dumpLines = (d.Text ?? "").Split('\n');
                    for (int k = 0; k < dumpLines.Length && k < 40; k++) w.WriteLine("    | " + dumpLines[k]);
                    if (dumpLines.Length > 40) w.WriteLine("    | ... " + (dumpLines.Length - 40) + " more lines");
                }
            List<string> raw;
            lock (st.Sync) raw = Query.RawLines(st, c, lines);
            if (raw.Count > 0)
            {
                w.WriteLine("  raw lines:");
                for (int i = 0; i < raw.Count; i++) w.WriteLine("    " + raw[i]);
            }
        }

        static string KvText(string[] kv, CallFeature f)
        {
            if (kv == null) return f.Xto != null ? "Xto=" + f.Xto + " (log line not readable)" : "(log line not readable)";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i + 1 < kv.Length; i += 2)
            {
                string k = kv[i];
                if (k == "fn" || k == "ft" || k == "fcid" || k == "bguid" || k == "legID") continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(k).Append('=').Append(kv[i + 1]);
            }
            return sb.ToString();
        }

        static string Hex(int v) { return v >= 0 ? v.ToString("X", Inv) : "-"; }
        static string Clock(long ms) { return ms < 0 ? "?" : TimeUtil.ToDate(ms).ToString("HH:mm:ss.fff", Inv); }

        // ------------------------------------------------------------ CSV

        public static readonly string[] CsvHead = {
            "start", "end", "duration_s", "ring_s", "talk_s", "calling", "called", "dialed", "connected_to", "legs", "result",
            "released_by", "final_cause", "cause_name", "media", "bytes_per_packet", "transfers", "answer_to_transfer_s",
            "tags", "guid", "padded_guid", "source", "failure_trace", "internal_error" };

        public static void Csv(Store st, List<Call> calls, TextWriter w)
        {
            Csv(st, calls, w, null);
        }

        /// <summary>With a display clock, start and end are on it and the CUBE's own start is added last.</summary>
        public static void Csv(Store st, List<Call> calls, TextWriter w, ZoneMap zm)
        {
            bool shifted = zm != null && !zm.Identity;
            // only with call record files, so a syslog-only export is exactly what it always was
            bool details = false;
            for (int i = 0; i < st.Sources.Count; i++) if (st.Sources[i].Details != null) details = true;
            w.WriteLine(string.Join(",", CsvHead) + (shifted ? ",start_on_cube_clock" : "") + (details ? "," + string.Join(",", CsvDetailHead) : ""));
            StringBuilder sb = new StringBuilder(512);
            for (int i = 0; i < calls.Count; i++)
            {
                Call c = calls[i];
                long sh = shifted ? zm.Shift(c.Src, c.Start) : 0;
                sb.Length = 0;
                sb.Append(TimeUtil.Fmt(c.Start + sh)).Append(',').Append(TimeUtil.Fmt(c.End + sh)).Append(',');
                sb.Append(Sec(c.DurMs)).Append(',').Append(c.RingMs >= 0 ? Sec(c.RingMs) : "").Append(',').Append(Sec(c.TalkMs)).Append(',');
                sb.Append(Cell(c.Calling)).Append(',').Append(Cell(c.Called)).Append(',').Append(Cell(c.Dialed)).Append(',');
                sb.Append(Cell(c.ConnectedTo)).Append(',').Append(c.Legs.Count).Append(',').Append(Names.Results[(int)c.Result]).Append(',');
                sb.Append(Names.Releases[(int)c.Released]).Append(',').Append(c.FinalCause >= 0 ? c.FinalCause.ToString(Inv) : "").Append(',');
                sb.Append(Cell(c.FinalCause >= 0 ? Q850.Name(c.FinalCause) : "")).Append(',').Append(Names.Medias[(int)c.Media]).Append(',');
                sb.Append(c.Bpp > 0 ? c.Bpp.ToString(Inv) : "").Append(',').Append(c.Transfers).Append(',');
                sb.Append(c.A2tMs >= 0 ? Sec(c.A2tMs) : "").Append(',').Append(Cell(st.Rules.TagList(c.Tags))).Append(',');
                sb.Append(c.Key).Append(',').Append(c.Padded ?? "").Append(',').Append(Cell(st.Sources[c.Src].Name)).Append(',');
                sb.Append(c.Dumps != null ? "yes" : "").Append(',');
                sb.Append(c.Errors != null ? Cell(c.Errors[0].Text) : "");
                if (shifted) sb.Append(',').Append(TimeUtil.Fmt(c.Start));
                if (details) AppendDetails(sb, st.Sources[c.Src].Details, c);
                w.WriteLine(sb.ToString());
            }
        }

        public static readonly string[] CsvDetailHead = {
            "dial_peers", "far_end_media", "codecs", "time_to_ringing_s", "called_received", "called_sent", "calling_received", "calling_sent", "worst_mos", "worst_loss_pct", "worst_jitter" };

        static void AddOnce(List<string> l, string v) { if (!string.IsNullOrEmpty(v) && !l.Contains(v)) l.Add(v); }

        static void AppendDetails(StringBuilder sb, Dictionary<Leg, LegDetail> det, Call c)
        {
            List<string> peers = new List<string>(2), media = new List<string>(2), codecs = new List<string>(2);
            string rxCalled = "", sentCalled = "", rxCalling = "", sentCalling = "";
            long ringing = -1;
            double mos = -1, loss = -1, jitter = -1;
            for (int i = 0; det != null && i < c.Legs.Count; i++)
            {
                Leg g = c.Legs[i];
                LegDetail d;
                if (!det.TryGetValue(g, out d)) continue;
                if (d.DialPeer >= 0) AddOnce(peers, d.DialPeer.ToString(Inv));
                if (d.MediaPort > 0 && d.MediaAddress != null) AddOnce(media, d.MediaAddress + ":" + d.MediaPort.ToString(Inv)); else AddOnce(media, d.MediaAddress);
                AddOnce(codecs, d.Codec);
                if (d.Alert >= 0 && d.Alert >= g.Setup && (ringing < 0 || d.Alert - g.Setup < ringing)) ringing = d.Alert - g.Setup;
                if (g.In) { if (rxCalled.Length == 0) rxCalled = d.ReceivedCalled ?? ""; if (rxCalling.Length == 0) rxCalling = d.ReceivedCalling ?? ""; }
                if (g.Out) { if (sentCalled.Length == 0) sentCalled = d.SentCalled ?? ""; if (sentCalling.Length == 0) sentCalling = d.SentCalling ?? ""; }
                LegQuality q = d.Quality;
                if (q == null) continue;
                if (q.Mos > 0 && (mos < 0 || q.Mos < mos)) mos = q.Mos;
                if (q.LossRate > loss) loss = q.LossRate;
                if (q.Jitter > jitter) jitter = q.Jitter;
            }
            sb.Append(',').Append(Cell(string.Join(";", peers))).Append(',').Append(Cell(string.Join(";", media))).Append(',').Append(Cell(string.Join(";", codecs)));
            sb.Append(',').Append(ringing >= 0 ? Sec(ringing) : "").Append(',').Append(Cell(rxCalled)).Append(',').Append(Cell(sentCalled));
            sb.Append(',').Append(Cell(rxCalling)).Append(',').Append(Cell(sentCalling));
            sb.Append(',').Append(mos > 0 ? mos.ToString("0.00", Inv) : "").Append(',').Append(loss >= 0 ? loss.ToString("0.00", Inv) : "").Append(',').Append(jitter > 0 ? jitter.ToString("0.0000", Inv) : "");
        }

        static string Sec(long ms) { return (ms / 1000.0).ToString("0.000", Inv); }

        /// <summary>Numbers are written as ="..." so a spreadsheet keeps leading digits and long numbers intact.</summary>
        static string Cell(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            bool digits = true;
            for (int i = 0; i < s.Length; i++) if (s[i] < '0' || s[i] > '9') { digits = false; break; }
            if (digits && s.Length > 0) return "=\"" + s + "\"";
            if (s.IndexOfAny(new char[] { ',', '"', '\n', '\r' }) >= 0) return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        // ------------------------------------------------------------ static HTML report

        /// <summary>The dashboard with the state and the first page of calls baked in: one file that opens anywhere.</summary>
        public static string StaticHtml(Store st, QueryParams q, QueryResult r, string title)
        {
            string html = WebServer.EmbeddedDashboard();
            string state, calls;
            lock (st.Sync)
            {
                state = Query.StateJson(st, q, r, "static report generated " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", Inv));
                calls = Query.CallsJson(st, Query.Sorted(r.Matched, "start", true), "start", true, 0, 500);
            }
            string inject = "<script>window.__STATIC = {state:" + state + ",calls:" + calls + ",title:" +
                            JsonString(title ?? "") + "};</script>";
            int at = html.IndexOf("<!--STATIC-->", StringComparison.Ordinal);
            if (at < 0) return html;
            return html.Substring(0, at) + inject + html.Substring(at + 13);
        }

        static string JsonString(string s)
        {
            Json j = new Json(64);
            j.Val(s);
            return j.ToString();
        }
    }
}
