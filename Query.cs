using System;
using System.Collections.Generic;
using System.Globalization;

namespace CubeScope
{
    public sealed class QueryParams
    {
        public long From = -1, To = -1;      // ms, CUBE clock; -1 = edge of the data
        public int Bucket;                   // seconds; 0 = automatic
        public Filter Filter = new Filter();
        public int Src = -1;                 // source index, -1 = all
        public int MaxPoints = 720;
        /// <summary>The clock times are shown on (ZoneMap): "cube" = each CUBE's own; then From and To
        /// are on that clock too.</summary>
        public string Zone = "cube";
        /// <summary>The server follows live records: the data span leaves out CUBEs whose newest record is in the future.</summary>
        public bool Live;
        /// <summary>"Now" for Live (the self-test sets it); unset means the real time.</summary>
        public DateTime LiveNow;

        public string CacheKey()
        {
            return From + "|" + To + "|" + Bucket + "|" + (Filter == null ? "" : Filter.Text) + "|" + Src + "|" + MaxPoints + "|" + Zone + "|" + Live;
        }
    }

    /// <summary>A row of a top-numbers table.</summary>
    public sealed class NumberRow
    {
        public string Number;
        public int Calls, Answered, Failed, Abandoned, NoMedia;
        public long TalkMs;
    }

    /// <summary>A pile of cancels: calls that ran From to To whole seconds (To 0: From or more).</summary>
    public struct CancelClump
    {
        public int From, To, Calls;
    }

    public sealed class CauseRow
    {
        public int Cause;
        public int Calls, Answered;
        public readonly int[] Rel = new int[5];
        public readonly int[] Res = new int[7];
    }

    /// <summary>How many calls carry each cause: Q.850 in an array, anything else in a dictionary.</summary>
    public sealed class CauseCounts
    {
        readonly int[] _q = new int[128];
        Dictionary<int, int> _more;

        public void Add(int cause)
        {
            if (cause >= 0 && cause < 128) { _q[cause]++; return; }
            if (_more == null) _more = new Dictionary<int, int>();
            int n;
            _more.TryGetValue(cause, out n);
            _more[cause] = n + 1;
        }

        /// <summary>(cause, calls), most calls first.</summary>
        public List<KeyValuePair<int, int>> Items()
        {
            List<KeyValuePair<int, int>> l = new List<KeyValuePair<int, int>>();
            for (int i = 0; i < _q.Length; i++) if (_q[i] > 0) l.Add(new KeyValuePair<int, int>(i, _q[i]));
            if (_more != null) l.AddRange(_more);
            l.Sort(delegate (KeyValuePair<int, int> a, KeyValuePair<int, int> b)
            {
                int c = b.Value.CompareTo(a.Value);
                return c != 0 ? c : a.Key.CompareTo(b.Key);
            });
            return l;
        }
    }

    public sealed class CauseSeries
    {
        public int Cause;               // -1 = every other cause
        public int Count;
        public double[] V;
    }

    public sealed class EventLane
    {
        public string Kind;
        public int Count;
        public double[] PerBucket;
        public double[] Values;         // licence: the count requested, carried forward
        public readonly List<SysEvent> Samples = new List<SysEvent>();
        public readonly List<long> SampleTs = new List<long>();      // each sample's time on the display clock
    }

    /// <summary>Everything the dashboard and the reports show for one range, bucket size and filter.</summary>
    public sealed class QueryResult
    {
        public long Version;
        public long DataFirst = -1, DataLast = -1;
        public long From, To;            // the requested range: what is counted
        public long GridFrom;            // first bucket's start, at or before From: where it is drawn
        public int Bucket, N;
        public long[] T;                 // bucket start, unix seconds (CUBE clock)

        // totals
        public int Calls, Unanswered, Dialer;
        public readonly int[] ByResult = new int[7];
        public readonly int[] ByRelease = new int[5];
        public readonly int[] ByMedia = new int[5];
        public int Transfers, WithTransfer, LegLess, WithDump, WithIec, Legs;
        public long TalkSum; public int TalkN;
        public long RingP50 = -1, RingP95 = -1, TalkP50 = -1, TalkP95 = -1, A2tP50 = -1, A2tP95 = -1;
        public int A2tN;
        public int PeakCalls, PeakLegs;
        public long PeakCallsAt = -1, PeakLegsAt = -1;

        // series, N long
        public double[][] SResult;       // [result][bucket]
        public double[] SCalls, SAnswered, STalkSum, SConcCalls, SConcLegs, SRingP50, SRingP95, STalkP50, STalkP95,
                        SNoMedia, SOneWay, SLowRate, STransfers, SA2tP50, SA2tP95, SAsr;
        public readonly List<CauseSeries> SCauses = new List<CauseSeries>();
        public readonly List<EventLane> Lanes = new List<EventLane>();

        // canceled while ringing (the Abandoned result) by the whole seconds the call ran before the
        // cancel (the last bin is CancelMaxSec or more), split by who stopped it: the dialer on a
        // dialer call, the caller on any other. Whole seconds, because a dialer's no-answer timeout
        // shows as a pile one to four seconds wide and fixed bands cut those piles in two.
        public const int CancelMaxSec = 120;
        public readonly int[] CancelDialer = new int[CancelMaxSec + 1], CancelCaller = new int[CancelMaxSec + 1];

        // tables
        public List<NumberRow> TopCalled, TopCalling, TopConnected, TopDialed;
        public List<CauseRow> CauseRows;

        // only from call record files (empty on a syslog feed)
        public List<NumberRow> TopDialPeers, TopMedia, TopCodecs;
        public long AlertP50 = -1, AlertP95 = -1;
        public int AlertN;
        public int QualityCalls, QualityPoor, MosN;
        public double MosSum, WorstMos = -1, WorstLoss = -1, WorstJitter = -1;
        public readonly Dictionary<string, int> ChoiceDialPeers = new Dictionary<string, int>(), ChoiceCodecs = new Dictionary<string, int>();

        /// <summary>Calls in range that pass the filter, in start order (the call list pages these).</summary>
        public List<Call> Matched = new List<Call>();

        // what the Find pickers offer: every call in range, the filter NOT applied, so choosing one
        // cause still lists the others
        public readonly CauseCounts ChoiceCauses = new CauseCounts(), ChoiceLegCauses = new CauseCounts();
        public readonly int[] ChoiceMedia = new int[5], ChoiceResult = new int[7], ChoiceReleased = new int[5];
        public int ChoiceCalls, ChoiceDialerCanceled;   // canceled while ringing by the dialer (the rest: by the caller)
        public double Ms;               // how long the query took
        /// <summary>The clock the times are on; Matched holds calls in that clock's start order.</summary>
        public ZoneMap Zone = new ZoneMap();
    }

    public static class Query
    {
        public static readonly int[] BucketChoices = { 60, 300, 900, 1800, 3600, 10800, 21600, 86400 };
        const long MaxCallMs = 12L * 3600 * 1000;      // calls longer than this are not looked back for

        public static int AutoBucket(long spanMs, int maxPoints)
        {
            for (int i = 0; i < BucketChoices.Length; i++)
                if (spanMs / (BucketChoices[i] * 1000L) <= maxPoints) return BucketChoices[i];
            return 86400;
        }

        /// <summary>Data span of the selected sources (record times).</summary>
        public static void Span(Store st, int src, out long first, out long last)
        {
            Span(st, src, null, out first, out last);
        }

        /// <summary>Data span of the selected sources, on a display clock.</summary>
        public static void Span(Store st, int src, ZoneMap zm, out long first, out long last)
        {
            first = -1; last = -1;
            for (int i = 0; i < st.Sources.Count; i++)
            {
                if (src >= 0 && i != src) continue;
                Source s = st.Sources[i];
                long a = zm == null ? s.FirstMs : zm.ToDisplay(i, s.FirstMs), b = zm == null ? s.LastMs : zm.ToDisplay(i, s.LastMs);
                if (a >= 0 && (first < 0 || a < first)) first = a;
                if (b > last) last = b;
            }
        }

        /// <summary>A record this far past "now" on its own clock is from a clock that runs ahead.</summary>
        public const long FutureSlackMs = 10 * 60000L;
        /// <summary>Data whose newest believable record is within this of now is current: ranges end at now.</summary>
        public const long CurrentWithinMs = 12 * 3600000L;

        /// <summary>
        /// The data span while following live records. A CUBE whose newest record lies in the future on
        /// its own clock (a clock running ahead, or a wrong zone) is left out of "the newest record", so
        /// it cannot drag "Last hour" for every CUBE into a stretch with nothing in it. When every CUBE
        /// would be left out, it is the plain span.
        /// </summary>
        public static void SpanLive(Store st, int src, ZoneMap zm, DateTime utcNow, out long first, out long last)
        {
            Span(st, src, zm, out first, out last);
            long nowAll = zm.NowDisplay(utcNow);
            ServerZone sz = zm.Identity ? Clock.Server(st) : null;
            long best = -1;
            for (int i = 0; i < st.Sources.Count; i++)
            {
                if (src >= 0 && i != src) continue;
                Source s = st.Sources[i];
                long b = zm.ToDisplay(i, s.LastMs);
                if (b < 0) continue;
                long now = zm.Identity ? ZoneMap.NowOnCube(st, i, utcNow, sz) : nowAll;
                if (now >= 0 && b > now + FutureSlackMs) continue;
                if (b > best) best = b;
            }
            if (best >= 0) last = best;
        }

        /// <summary>Where a range counted back from the newest record ends while following: now on the
        /// display clock when the data is current, else the newest believable record.</summary>
        public static long LiveEnd(ZoneMap zm, long newest, DateTime utcNow)
        {
            long now = zm.NowDisplay(utcNow);
            if (now >= 0 && newest >= now - CurrentWithinMs) return now;
            return newest;
        }

        static int LowerBound(List<Call> l, long t)
        {
            int lo = 0, hi = l.Count;
            while (lo < hi) { int m = (lo + hi) >> 1; if (l[m].Start < t) lo = m + 1; else hi = m; }
            return lo;
        }

        /// <summary>Runs under the store lock.</summary>
        public static QueryResult Run(Store st, QueryParams q)
        {
            DateTime t0 = DateTime.UtcNow;
            QueryResult r = new QueryResult();
            List<Call> calls = st.ByStart();
            r.Version = st.Version;
            ZoneMap zm = ZoneMap.Build(st, q.Zone);
            r.Zone = zm;
            if (q.Live) SpanLive(st, q.Src, zm, q.LiveNow == default(DateTime) ? DateTime.UtcNow : q.LiveNow, out r.DataFirst, out r.DataLast);
            else Span(st, q.Src, zm, out r.DataFirst, out r.DataLast);
            long from = q.From, to = q.To;
            if (from < 0) from = r.DataFirst >= 0 ? r.DataFirst : 0;
            if (to < 0) to = r.DataLast >= 0 ? r.DataLast + 1 : from + 3600000;
            if (to <= from) to = from + 60000;
            int bucket = q.Bucket > 0 ? q.Bucket : AutoBucket(to - from, q.MaxPoints);
            long bms = bucket * 1000L;
            // a hand-picked bucket that would draw too many points is widened
            while ((to - from) / bms > Math.Max(q.MaxPoints, 3000) && bucket < 86400) { bucket = NextBucket(bucket); bms = bucket * 1000L; }
            // The requested range decides which calls count; the bucket grid only decides where they are
            // drawn. Rounding the range itself out to whole buckets pulled in calls nobody asked for
            // (an hourly bucket turned 10:30-10:31 into 10:00-11:00).
            long grid = (from / bms) * bms;
            int n = (int)((to - grid + bms - 1) / bms);
            if (n < 1) n = 1;
            r.From = from; r.To = to; r.GridFrom = grid; r.Bucket = bucket; r.N = n;
            r.T = new long[n];
            for (int i = 0; i < n; i++) r.T[i] = (grid + i * bms) / 1000;

            r.SResult = new double[7][];
            for (int k = 0; k < 7; k++) r.SResult[k] = new double[n];
            r.SCalls = new double[n]; r.SAnswered = new double[n]; r.STalkSum = new double[n];
            r.SNoMedia = new double[n]; r.SOneWay = new double[n]; r.SLowRate = new double[n]; r.STransfers = new double[n];
            List<int>[] ring = new List<int>[n], talk = new List<int>[n], a2t = new List<int>[n];
            List<int> allRing = new List<int>(), allTalk = new List<int>(), allA2t = new List<int>();
            Dictionary<int, int[]> causeBuckets = new Dictionary<int, int[]>();
            Dictionary<int, CauseRow> causeRows = new Dictionary<int, CauseRow>();
            NumberCounter called = new NumberCounter(), calling = new NumberCounter(),
                          connected = new NumberCounter(), dialed = new NumberCounter(),
                          dialPeers = new NumberCounter(), mediaAddrs = new NumberCounter(), codecs = new NumberCounter();
            List<int> allAlert = new List<int>();
            List<string> once = new List<string>(8);
            // concurrency, streamed in start order: memory is the peak level, not the number of calls
            Concurrency cConc = new Concurrency(n, bms), lConc = new Concurrency(n, bms);
            PairHeap pendingLegs = new PairHeap();

            Filter f = q.Filter ?? new Filter();
            Filter.CurrentSources = st.Sources;
            // On a display clock other than the CUBEs' own, each CUBE's calls move by their own shift,
            // so the calls near the range are put in that clock's start order first.
            Call[] candC = null; long[] candS = null;
            int startIdx, total;
            if (zm.Identity) { startIdx = LowerBound(calls, from - MaxCallMs); total = calls.Count; }
            else { Candidates(calls, zm, q.Src, from, to, out candC, out candS); startIdx = 0; total = candC.Length; }
            for (int i = startIdx; i < total; i++)
            {
                Call c; long sh;
                if (candC == null) { c = calls[i]; sh = 0; } else { c = candC[i]; sh = candS[i]; }
                long cStart = c.Start + sh, cEnd = c.End + sh;
                if (cStart >= to) break;
                // over before the range began (a zero-length call AT the start still counts: a
                // rejected INVITE known only from its failure trace starts and ends at one instant)
                if (cStart < from && cEnd <= from) continue;
                if (q.Src >= 0 && c.Src != q.Src) continue;
                Dictionary<Leg, LegDetail> det = st.Sources[c.Src].Details;
                if (cStart >= from) { CountChoices(r, c); if (det != null) CountDetailChoices(r, det, c, once); }
                if (!f.IsEmpty && !f.Match(c, st.Rules)) continue;

                // concurrency: every call and leg up at some point in the range, on the grid's clock.
                // Calls arrive in start order. A leg starts at or after its call, so every leg still
                // waiting that starts no later than this call can be played now, in order.
                long cs = Math.Max(cStart, from) - grid, ce = Math.Min(Math.Max(cEnd, cStart + 1), to) - grid;
                while (pendingLegs.Count > 0 && pendingLegs.TopKey <= cs) { long pk, pv; pendingLegs.Pop(out pk, out pv); lConc.Add(pk, pv); }
                if (ce > cs) cConc.Add(cs, ce);
                for (int k = 0; k < c.Legs.Count; k++)
                {
                    Leg g = c.Legs[k];
                    if (g.FeatureOnly) continue;
                    long gs = g.Setup + sh, gd = g.Disconnect + sh;
                    long ls = Math.Max(gs, from) - grid, le = Math.Min(Math.Max(gd, gs + 1), to) - grid;
                    if (le > ls) pendingLegs.Push(ls, le);
                }
                if (cStart < from) continue;

                int b = (int)((cStart - grid) / bms);
                if (b >= n) b = n - 1;
                r.Matched.Add(c);
                r.Calls++;
                r.Legs += c.Legs.Count;
                r.SCalls[b]++;
                r.ByResult[(int)c.Result]++;
                r.SResult[(int)c.Result][b]++;
                r.ByRelease[(int)c.Released]++;
                r.ByMedia[(int)c.Media]++;
                if (c.Dialer) r.Dialer++;
                if (c.Legs.Count == 0) r.LegLess++;
                if (c.Dumps != null) r.WithDump++;
                if (c.Errors != null) r.WithIec++;
                if (c.Media == Media.None) r.SNoMedia[b]++;
                else if (c.Media == Media.OneWay) r.SOneWay[b]++;
                else if (c.Media == Media.LowRate) r.SLowRate[b]++;
                if (c.Transfers > 0) { r.WithTransfer++; r.Transfers += c.Transfers; r.STransfers[b] += c.Transfers; }
                bool ans = c.Result == Result.Answered;
                if (ans)
                {
                    r.SAnswered[b]++;
                    long tk = c.TalkMs;
                    r.TalkSum += tk; r.TalkN++;
                    r.STalkSum[b] += tk;
                    int tki = (int)Math.Min(int.MaxValue, tk);
                    Add(ref talk[b], tki); allTalk.Add(tki);
                    if (c.RingMs >= 0)
                    {
                        int rg = (int)Math.Min(int.MaxValue, c.RingMs);
                        Add(ref ring[b], rg); allRing.Add(rg);
                    }
                    if (c.A2tMs >= 0)
                    {
                        int a = (int)Math.Min(int.MaxValue, c.A2tMs);
                        Add(ref a2t[b], a); allA2t.Add(a);
                    }
                }
                else
                {
                    r.Unanswered++;
                    if (c.Result == Result.Abandoned)
                    {
                        int cbin = CancelBin(c.DurMs);
                        if (c.Dialer) r.CancelDialer[cbin]++; else r.CancelCaller[cbin]++;
                    }
                    int cause = c.FinalCause;
                    int[] cb;
                    if (!causeBuckets.TryGetValue(cause, out cb)) { cb = new int[n + 1]; causeBuckets[cause] = cb; }
                    cb[b]++; cb[n]++;
                }
                CauseRow cr;
                int fc = c.FinalCause;
                if (!causeRows.TryGetValue(fc, out cr)) { cr = new CauseRow(); cr.Cause = fc; causeRows[fc] = cr; }
                cr.Calls++;
                if (ans) cr.Answered++;
                cr.Rel[(int)c.Released]++;
                cr.Res[(int)c.Result]++;

                called.Count(c.Called, c);
                calling.Count(c.Calling, c);
                dialed.Count(c.Dialed, c);
                if (!string.IsNullOrEmpty(c.ConnectedTo)) connected.Count(c.ConnectedTo, c);
                if (det != null) CountDetails(r, det, c, dialPeers, mediaAddrs, codecs, once, allAlert);
            }
            Filter.CurrentSources = null;
            while (pendingLegs.Count > 0) { long pk, pv; pendingLegs.Pop(out pk, out pv); lConc.Add(pk, pv); }

            // percentiles
            r.SRingP50 = new double[n]; r.SRingP95 = new double[n]; r.STalkP50 = new double[n]; r.STalkP95 = new double[n];
            r.SA2tP50 = new double[n]; r.SA2tP95 = new double[n]; r.SAsr = new double[n];
            for (int b = 0; b < n; b++)
            {
                Pct(ring[b], out r.SRingP50[b], out r.SRingP95[b]);
                Pct(talk[b], out r.STalkP50[b], out r.STalkP95[b]);
                Pct(a2t[b], out r.SA2tP50[b], out r.SA2tP95[b]);
                r.SAsr[b] = r.SCalls[b] > 0 ? 100.0 * r.SAnswered[b] / r.SCalls[b] : double.NaN;
            }
            double p50, p95;
            Pct(allRing, out p50, out p95); r.RingP50 = (long)p50; r.RingP95 = (long)p95;
            Pct(allTalk, out p50, out p95); r.TalkP50 = (long)p50; r.TalkP95 = (long)p95;
            Pct(allA2t, out p50, out p95); r.A2tP50 = (long)p50; r.A2tP95 = (long)p95;
            r.A2tN = allA2t.Count;
            if (allRing.Count == 0) r.RingP50 = r.RingP95 = -1;
            if (allTalk.Count == 0) r.TalkP50 = r.TalkP95 = -1;
            if (allA2t.Count == 0) r.A2tP50 = r.A2tP95 = -1;
            r.AlertN = allAlert.Count;
            if (allAlert.Count > 0) { Pct(allAlert, out p50, out p95); r.AlertP50 = (long)p50; r.AlertP95 = (long)p95; }

            // concurrency: the most calls (legs) up at once in each bucket
            r.SConcCalls = cConc.Finish(out r.PeakCalls, out r.PeakCallsAt);
            r.SConcLegs = lConc.Finish(out r.PeakLegs, out r.PeakLegsAt);
            if (r.PeakCallsAt >= 0) r.PeakCallsAt += grid;
            if (r.PeakLegsAt >= 0) r.PeakLegsAt += grid;

            // causes of the calls that were not answered: the top six, then the rest together
            List<KeyValuePair<int, int[]>> cl = new List<KeyValuePair<int, int[]>>(causeBuckets);
            cl.Sort(delegate (KeyValuePair<int, int[]> a, KeyValuePair<int, int[]> b2) { return b2.Value[n].CompareTo(a.Value[n]); });
            CauseSeries other = null;
            for (int i = 0; i < cl.Count; i++)
            {
                if (i < 6)
                {
                    CauseSeries cs = new CauseSeries();
                    cs.Cause = cl[i].Key; cs.Count = cl[i].Value[n]; cs.V = new double[n];
                    for (int b = 0; b < n; b++) cs.V[b] = cl[i].Value[b];
                    r.SCauses.Add(cs);
                }
                else
                {
                    if (other == null) { other = new CauseSeries(); other.Cause = -1; other.V = new double[n]; }
                    other.Count += cl[i].Value[n];
                    for (int b = 0; b < n; b++) other.V[b] += cl[i].Value[b];
                }
            }
            if (other != null) r.SCauses.Add(other);

            r.CauseRows = new List<CauseRow>(causeRows.Values);
            r.CauseRows.Sort(delegate (CauseRow a, CauseRow b2) { return b2.Calls.CompareTo(a.Calls); });
            r.TopCalled = called.Top(15);
            r.TopCalling = calling.Top(15);
            r.TopConnected = connected.Top(15);
            r.TopDialed = dialed.Top(15);
            r.TopDialPeers = dialPeers.Top(30);
            r.TopMedia = mediaAddrs.Top(30);
            r.TopCodecs = codecs.Top(10);

            Lanes(st, q, r);
            r.Ms = (DateTime.UtcNow - t0).TotalMilliseconds;
            return r;
        }

        /// <summary>
        /// The calls that may start in [from, to) on the display clock (from MaxCallMs earlier, for the
        /// calls still up at from), with their shifts, in display start order; ties keep store order.
        /// </summary>
        static void Candidates(List<Call> calls, ZoneMap zm, int src, long from, long to, out Call[] cc, out long[] cs)
        {
            int a = LowerBound(calls, from - MaxCallMs - zm.MaxAbsShift);
            long hi = to + zm.MaxAbsShift;
            List<int> at = new List<int>();
            List<long> keys = new List<long>(), shifts = new List<long>();
            for (int i = a; i < calls.Count; i++)
            {
                Call c = calls[i];
                if (c.Start >= hi) break;
                if (src >= 0 && c.Src != src) continue;
                long sh = zm.Shift(c.Src, c.Start), ds = c.Start + sh;
                if (ds >= to || ds < from - MaxCallMs) continue;
                at.Add(i); keys.Add(ds); shifts.Add(sh);
            }
            int n = at.Count;
            int[] order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            long[] k = keys.ToArray();
            Array.Sort(k, order);
            // Array.Sort is not stable: put equal starts back in store order
            for (int i = 0; i < n; )
            {
                int j = i + 1;
                while (j < n && k[j] == k[i]) j++;
                if (j - i > 1) Array.Sort(order, i, j - i);
                i = j;
            }
            cc = new Call[n]; cs = new long[n];
            for (int i = 0; i < n; i++) { cc[i] = calls[at[order[i]]]; cs[i] = shifts[order[i]]; }
        }

        /// <summary>The whole seconds a call ran, capped at CancelMaxSec (the "or more" bin).</summary>
        public static int CancelBin(long durMs)
        {
            long s = durMs / 1000;
            return s <= 0 ? 0 : s >= QueryResult.CancelMaxSec ? QueryResult.CancelMaxSec : (int)s;
        }

        /// <summary>
        /// The piles in per-second cancel counts: runs of seconds that each hold at least 1% of the
        /// side's cancels and at least 3 calls (so a handful of cancels is not a row of one-call
        /// piles). At most eight, the biggest kept, in order of length. The "or more" bin
        /// stands alone (its To is 0). What the piles leave out is spread over other lengths.
        /// </summary>
        public static List<CancelClump> Clumps(int[] bins)
        {
            int total = 0, last = bins.Length - 1;
            for (int i = 0; i < bins.Length; i++) total += bins[i];
            List<CancelClump> all = new List<CancelClump>();
            int thr = Math.Max(3, (total + 99) / 100);
            for (int i = 0; i < bins.Length; i++)
            {
                if (bins[i] < thr) continue;
                CancelClump k = new CancelClump(); k.From = i;
                if (i == last) { k.Calls = bins[i]; k.To = 0; all.Add(k); break; }
                while (i < last && bins[i] >= thr) { k.Calls += bins[i]; i++; }
                k.To = i; i--;
                all.Add(k);
            }
            if (all.Count > 8)
            {
                all.Sort(delegate (CancelClump a, CancelClump b) { return b.Calls != a.Calls ? b.Calls.CompareTo(a.Calls) : a.From.CompareTo(b.From); });
                all.RemoveRange(8, all.Count - 8);
                all.Sort(delegate (CancelClump a, CancelClump b) { return a.From.CompareTo(b.From); });
            }
            return all;
        }

        public static void WriteClumps(Json j, string key, int[] bins)
        {
            j.Key(key).Arr();
            foreach (CancelClump k in Clumps(bins)) j.Arr().Val(k.From).Val(k.To).Val(k.Calls).EndArr();
            j.EndArr();
        }

        /// <summary>MOS under this, or loss above PoorLoss percent, counts a measured call as poor.</summary>
        public const double PoorMos = 3.5, PoorLoss = 1.0;

        static bool FirstTime(List<string> once, string key)
        {
            if (once.Contains(key)) return false;
            once.Add(key);
            return true;
        }

        /// <summary>What the Find pickers offer: each value once per call, the filter not applied.</summary>
        static void CountDetailChoices(QueryResult r, Dictionary<Leg, LegDetail> det, Call c, List<string> once)
        {
            once.Clear();
            for (int i = 0; i < c.Legs.Count; i++)
            {
                LegDetail d;
                if (!det.TryGetValue(c.Legs[i], out d)) continue;
                int n;
                if (d.DialPeer >= 0)
                {
                    string p = d.DialPeer.ToString(CultureInfo.InvariantCulture);
                    if (FirstTime(once, "p" + p)) { r.ChoiceDialPeers.TryGetValue(p, out n); r.ChoiceDialPeers[p] = n + 1; }
                }
                if (d.Codec != null && FirstTime(once, "c" + d.Codec)) { r.ChoiceCodecs.TryGetValue(d.Codec, out n); r.ChoiceCodecs[d.Codec] = n + 1; }
            }
        }

        /// <summary>One call's dial-peers, far-end addresses and codecs (each once), time to ringing and worst quality.</summary>
        static void CountDetails(QueryResult r, Dictionary<Leg, LegDetail> det, Call c, NumberCounter peers, NumberCounter media, NumberCounter codecs,
                                 List<string> once, List<int> allAlert)
        {
            once.Clear();
            long ringing = -1;
            double mos = -1, loss = -1;
            bool measured = false;
            for (int i = 0; i < c.Legs.Count; i++)
            {
                Leg g = c.Legs[i];
                LegDetail d;
                if (!det.TryGetValue(g, out d)) continue;
                if (d.DialPeer >= 0)
                {
                    string p = d.DialPeer.ToString(CultureInfo.InvariantCulture);
                    if (FirstTime(once, "p" + p)) peers.Count(p, c);
                }
                if (d.MediaAddress != null && FirstTime(once, "m" + d.MediaAddress)) media.Count(d.MediaAddress, c);
                if (d.Codec != null && FirstTime(once, "c" + d.Codec)) codecs.Count(d.Codec, c);
                if (d.Alert >= 0 && d.Alert >= g.Setup && (ringing < 0 || d.Alert - g.Setup < ringing)) ringing = d.Alert - g.Setup;
                LegQuality q = d.Quality;
                if (q == null) continue;
                measured = true;
                if (q.Mos > 0 && (mos < 0 || q.Mos < mos)) mos = q.Mos;
                if (q.LossRate > loss) loss = q.LossRate;
                if (q.Jitter > r.WorstJitter) r.WorstJitter = q.Jitter;
            }
            if (ringing >= 0) allAlert.Add((int)Math.Min(int.MaxValue, ringing));
            if (!measured) return;
            r.QualityCalls++;
            if (mos > 0) { r.MosSum += mos; r.MosN++; if (r.WorstMos < 0 || mos < r.WorstMos) r.WorstMos = mos; }
            if (loss > r.WorstLoss) r.WorstLoss = loss;
            if ((mos > 0 && mos < PoorMos) || loss > PoorLoss) r.QualityPoor++;
        }

        static void CountChoices(QueryResult r, Call c)
        {
            r.ChoiceCalls++;
            r.ChoiceCauses.Add(c.FinalCause);
            r.ChoiceMedia[(int)c.Media]++;
            r.ChoiceResult[(int)c.Result]++;
            if (c.Result == Result.Abandoned && c.Dialer) r.ChoiceDialerCanceled++;
            r.ChoiceReleased[(int)c.Released]++;
            // a call counts once for each cause any of its legs had
            for (int i = 0; i < c.Legs.Count; i++)
            {
                Leg g = c.Legs[i];
                if (g.FeatureOnly) continue;
                bool seen = false;
                for (int k = 0; k < i && !seen; k++) seen = !c.Legs[k].FeatureOnly && c.Legs[k].Cause == g.Cause;
                if (!seen) r.ChoiceLegCauses.Add(g.Cause);
            }
        }

        static int NextBucket(int b)
        {
            for (int i = 0; i < BucketChoices.Length; i++) if (BucketChoices[i] > b) return BucketChoices[i];
            return 86400;
        }

        static void Add(ref List<int> l, int v)
        {
            if (l == null) l = new List<int>(8);
            l.Add(v);
        }

        static void Pct(List<int> l, out double p50, out double p95)
        {
            if (l == null || l.Count == 0) { p50 = double.NaN; p95 = double.NaN; return; }
            l.Sort();
            p50 = l[(int)Math.Floor((l.Count - 1) * 0.5)];
            p95 = l[(int)Math.Floor((l.Count - 1) * 0.95)];
        }

        /// <summary>
        /// Per-number counts for the top-numbers tables. A whole archive can hold a million distinct
        /// calling numbers, so a number costs one dictionary entry and a slot in a few arrays, not an
        /// object; only the rows shown become NumberRows.
        /// </summary>
        sealed class NumberCounter
        {
            readonly Dictionary<string, int> _at = new Dictionary<string, int>(StringComparer.Ordinal);
            string[] _num = new string[64];
            int[] _calls = new int[64], _ans = new int[64], _aband = new int[64], _failed = new int[64], _noMedia = new int[64];
            long[] _talk = new long[64];
            int _n;

            public void Count(string num, Call c)
            {
                string k = string.IsNullOrEmpty(num) ? "" : num;
                int i;
                if (!_at.TryGetValue(k, out i))
                {
                    if (_n == _num.Length) Grow();
                    i = _n++;
                    _num[i] = k;
                    _at[k] = i;
                }
                _calls[i]++;
                if (c.Result == Result.Answered) { _ans[i]++; _talk[i] += c.TalkMs; }
                else if (c.Result == Result.Abandoned) _aband[i]++;
                else _failed[i]++;
                if (c.Media == Media.None || c.Media == Media.OneWay) _noMedia[i]++;
            }

            void Grow()
            {
                int m = _num.Length * 2;
                Array.Resize(ref _num, m); Array.Resize(ref _calls, m); Array.Resize(ref _ans, m); Array.Resize(ref _aband, m);
                Array.Resize(ref _failed, m); Array.Resize(ref _noMedia, m); Array.Resize(ref _talk, m);
            }

            /// <summary>The n busiest numbers: most calls first, then by number.</summary>
            public List<NumberRow> Top(int n)
            {
                int[] best = new int[Math.Min(n, _n)];
                int have = 0;
                for (int i = 0; i < _n; i++)
                {
                    if (have == best.Length && !Before(i, best[have - 1])) continue;
                    int at = have < best.Length ? have++ : have - 1;
                    while (at > 0 && Before(i, best[at - 1])) { best[at] = best[at - 1]; at--; }
                    best[at] = i;
                }
                List<NumberRow> l = new List<NumberRow>(have);
                for (int k = 0; k < have; k++)
                {
                    int i = best[k];
                    NumberRow r = new NumberRow();
                    r.Number = _num[i]; r.Calls = _calls[i]; r.Answered = _ans[i]; r.Abandoned = _aband[i];
                    r.Failed = _failed[i]; r.NoMedia = _noMedia[i]; r.TalkMs = _talk[i];
                    l.Add(r);
                }
                return l;
            }

            bool Before(int a, int b)
            {
                if (_calls[a] != _calls[b]) return _calls[a] > _calls[b];
                return string.CompareOrdinal(_num[a], _num[b]) < 0;
            }
        }

        /// <summary>A min-heap of (key, value) pairs of longs.</summary>
        sealed class PairHeap
        {
            long[] _k = new long[64], _v = new long[64];
            int _n;
            public int Count { get { return _n; } }
            public long TopKey { get { return _k[0]; } }

            public void Push(long k, long v)
            {
                if (_n == _k.Length) { Array.Resize(ref _k, _n * 2); Array.Resize(ref _v, _n * 2); }
                int i = _n++;
                while (i > 0)
                {
                    int p = (i - 1) >> 1;
                    if (_k[p] <= k) break;
                    _k[i] = _k[p]; _v[i] = _v[p]; i = p;
                }
                _k[i] = k; _v[i] = v;
            }

            public void Pop(out long k, out long v)
            {
                k = _k[0]; v = _v[0];
                long lk = _k[--_n], lv = _v[_n];
                int i = 0;
                while (true)
                {
                    int c = 2 * i + 1;
                    if (c >= _n) break;
                    if (c + 1 < _n && _k[c + 1] < _k[c]) c++;
                    if (_k[c] >= lk) break;
                    _k[i] = _k[c]; _v[i] = _v[c]; i = c;
                }
                _k[i] = lk; _v[i] = lv;
            }
        }

        /// <summary>
        /// The most intervals up at once in each bucket, with intervals [start, end) on the grid's clock
        /// fed in order of their start. Ends wait in a heap and are played before any start at or after
        /// them, so something that ended exactly as another began is not counted with it, and one that
        /// ended exactly as a bucket began was not up in that bucket.
        /// </summary>
        sealed class Concurrency
        {
            readonly int _n;
            readonly long _bms;
            readonly double[] _max;
            readonly PairHeap _ends = new PairHeap();
            int _level, _cur, _curMax, _peak;
            long _peakAt = -1;

            public Concurrency(int n, long bms) { _n = n; _bms = bms; _max = new double[n]; }

            public void Add(long start, long end)
            {
                while (_ends.Count > 0 && _ends.TopKey <= start) { long k, v; _ends.Pop(out k, out v); End(k); }
                if (!MoveTo(start)) return;
                _level++;
                if (_level > _curMax) _curMax = _level;
                if (_level > _peak) { _peak = _level; _peakAt = start; }
                _ends.Push(end, 0);
            }

            void End(long t)
            {
                if (!MoveTo(t)) return;
                _level--;
                // ends at the very start of a bucket come before anything else in it
                if (t == _cur * _bms) _curMax = _level;
            }

            /// <summary>Closes the buckets before t's; false when t is past the last bucket.</summary>
            bool MoveTo(long t)
            {
                long b = t / _bms;
                if (b >= _n) { Close(_n); return false; }
                if (b > _cur) Close((int)b);
                return true;
            }

            void Close(int upTo)
            {
                if (_cur >= _n) return;
                _max[_cur] = _curMax;
                for (int i = _cur + 1; i < upTo && i < _n; i++) _max[i] = _level;
                _cur = upTo;
                _curMax = _level;
            }

            public double[] Finish(out int peak, out long peakAt)
            {
                while (_ends.Count > 0) { long k, v; _ends.Pop(out k, out v); End(k); }
                Close(_n);
                peak = _peak; peakAt = _peakAt;
                return _max;
            }
        }

        static readonly string[] LaneOrder = { "dump", "iec", "tls-fail", "tls", "sip-state", "sip", "licence", "login", "config", "other" };

        /// <summary>The newest ten configuration changes of the CUBE picked (or all), whatever the range,
        /// on the page's clock ("own": left on the CUBE's clock, nothing to convert it by).</summary>
        static void WriteConfigChanges(Json j, Store st, QueryParams q, ZoneMap zm, ConfigHistory configs)
        {
            string src = q.Src >= 0 && q.Src < st.Sources.Count ? st.Sources[q.Src].Name : null;
            j.Key("configChanges").Arr();
            foreach (ConfigHistory.Entry e in configs.Newest(ConfigHistory.PerSource, src))
            {
                bool own = zm == null || zm.Identity || e.Utc < 0;
                j.Obj().P("t", (own ? e.Ts : zm.FromUtc(e.Utc)) / 1000).P("src", e.Src).P("m", e.Mnemonic).P("x", Clip(e.Text, 300));
                if (own && zm != null && !zm.Identity) j.P("own", true);
                j.EndObj();
            }
            j.EndArr();
        }

        static void Lanes(Store st, QueryParams q, QueryResult r)
        {
            long bms = r.Bucket * 1000L;
            Dictionary<string, EventLane> lanes = new Dictionary<string, EventLane>();
            for (int s = 0; s < st.Sources.Count; s++)
            {
                if (q.Src >= 0 && s != q.Src) continue;
                List<SysEvent> evs = st.Sources[s].Events;
                for (int i = 0; i < evs.Count; i++)
                {
                    SysEvent e = evs[i];
                    long ets = r.Zone.ToDisplay(s, e.Ts);
                    if (ets < r.From || ets >= r.To) continue;          // the requested range, not the grid
                    EventLane l;
                    if (!lanes.TryGetValue(e.Kind, out l))
                    {
                        l = new EventLane(); l.Kind = e.Kind; l.PerBucket = new double[r.N];
                        if (e.Kind == "licence") { l.Values = new double[r.N]; for (int b = 0; b < r.N; b++) l.Values[b] = double.NaN; }
                        lanes[e.Kind] = l;
                    }
                    int bi = (int)((ets - r.GridFrom) / bms);
                    l.Count++;
                    l.PerBucket[bi]++;
                    if (l.Values != null && !double.IsNaN(e.Value)) l.Values[bi] = Math.Max(double.IsNaN(l.Values[bi]) ? 0 : l.Values[bi], e.Value);
                    if (l.Samples.Count < 400) { l.Samples.Add(e); l.SampleTs.Add(ets); }
                }
            }
            for (int i = 0; i < LaneOrder.Length; i++)
            {
                EventLane l;
                if (lanes.TryGetValue(LaneOrder[i], out l)) r.Lanes.Add(l);
            }
        }

        // ------------------------------------------------------------ JSON

        public static string StateJson(Store st, QueryParams q, QueryResult r, string status)
        {
            return StateJson(st, q, r, status, false);
        }

        /// <summary>following: the server is reading new records as they arrive, so the page turns Live on
        /// by default and a range counted back from the newest record moves with them.</summary>
        public static string StateJson(Store st, QueryParams q, QueryResult r, string status, bool following)
        {
            return StateJson(st, q, r, status, following, null);
        }

        /// <summary>configs: the kept configuration changes (the service's outlive its 24-hour window);
        /// null takes them from the store.</summary>
        public static string StateJson(Store st, QueryParams q, QueryResult r, string status, bool following, ConfigHistory configs)
        {
            Json j = new Json(1 << 18);
            j.Obj();
            j.P("version", r.Version);
            j.P("status", status ?? "");
            j.P("following", following);
            j.P("queryMs", r.Ms);
            j.Key("sources").Arr();
            for (int i = 0; i < st.Sources.Count; i++)
            {
                Source s = st.Sources[i];
                j.Obj().P("i", i).P("name", s.Name).P("folder", s.Folder ?? "").P("files", s.Files.Count)
                 .P("first", s.FirstMs / 1000).P("last", s.LastMs / 1000).P("tz", s.Tz ?? "").P("status", s.Status ?? "").EndObj();
            }
            j.EndArr();
            j.Key("range").Obj().P("from", r.From / 1000).P("to", r.To / 1000).P("first", r.DataFirst / 1000)
             .P("last", r.DataLast / 1000).P("bucket", r.Bucket).P("n", r.N).P("src", q.Src)
             .P("fromMs", r.From).P("toMs", r.To).EndObj();
            j.Key("zone").Obj().P("id", r.Zone.Id).P("name", r.Zone.Name).P("label", r.Zone.Label(r.From + (r.To - r.From) / 2));
            j.Key("notConverted").Arr();
            for (int i = 0; i < r.Zone.NotConverted.Count; i++) j.Val(r.Zone.NotConverted[i]);
            j.EndArr();
            j.Key("choices").Arr();
            for (int i = 0; i < ZoneMap.Choices.Length; i++) j.Obj().P("id", ZoneMap.Choices[i][0]).P("name", ZoneMap.Choices[i][1]).EndObj();
            j.EndArr();
            j.EndObj();
            j.Key("filter").Obj().P("text", q.Filter == null ? "" : q.Filter.Text)
             .P("error", q.Filter == null ? "" : (q.Filter.Error ?? "")).EndObj();

            j.Key("tiles").Obj();
            j.P("calls", r.Calls).P("legs", r.Legs);
            for (int k = 1; k < 7; k++) j.P(Names.Results[k], r.ByResult[k]);
            j.P("unknown", r.ByResult[0]);
            j.P("unanswered", r.Unanswered);
            j.P("asr", r.Calls > 0 ? 100.0 * r.ByResult[(int)Result.Answered] / r.Calls : double.NaN);
            j.P("acdMs", r.TalkN > 0 ? (double)r.TalkSum / r.TalkN : double.NaN);
            j.P("talkP50", r.TalkP50).P("talkP95", r.TalkP95).P("ringP50", r.RingP50).P("ringP95", r.RingP95);
            j.P("a2tP50", r.A2tP50).P("a2tP95", r.A2tP95).P("a2tN", r.A2tN);
            for (int k = 0; k < 5; k++) j.P("media_" + Names.Medias[k], r.ByMedia[k]);
            for (int k = 0; k < 5; k++) j.P("rel_" + Names.Releases[k], r.ByRelease[k]);
            j.P("transfers", r.Transfers).P("withTransfer", r.WithTransfer);
            j.P("legLess", r.LegLess).P("withDump", r.WithDump).P("withIec", r.WithIec).P("dialer", r.Dialer);
            j.P("peakCalls", r.PeakCalls).P("peakCallsAt", r.PeakCallsAt / 1000);
            j.P("peakLegs", r.PeakLegs).P("peakLegsAt", r.PeakLegsAt / 1000);
            j.EndObj();

            j.Key("series").Obj();
            j.PArr("t", r.T);
            j.Key("result").Obj();
            for (int k = 0; k < 7; k++) j.PArr(Names.Results[k], r.SResult[k]);
            j.EndObj();
            j.PArr("calls", r.SCalls).PArr("answered", r.SAnswered).PArr("asr", r.SAsr);
            double[] acd = new double[r.N];
            for (int b = 0; b < r.N; b++) acd[b] = r.SAnswered[b] > 0 ? r.STalkSum[b] / r.SAnswered[b] : double.NaN;
            j.PArr("acd", acd);
            j.PArr("concCalls", r.SConcCalls).PArr("concLegs", r.SConcLegs);
            j.PArr("ringP50", r.SRingP50).PArr("ringP95", r.SRingP95).PArr("talkP50", r.STalkP50).PArr("talkP95", r.STalkP95);
            j.PArr("noMedia", r.SNoMedia).PArr("oneWay", r.SOneWay).PArr("lowRate", r.SLowRate);
            j.PArr("transfers", r.STransfers).PArr("a2tP50", r.SA2tP50).PArr("a2tP95", r.SA2tP95);
            j.Key("causes").Arr();
            for (int i = 0; i < r.SCauses.Count; i++)
            {
                CauseSeries cs = r.SCauses[i];
                j.Obj().P("cause", cs.Cause).P("name", cs.Cause < 0 ? "Other causes" : Q850.Name(cs.Cause)).P("count", cs.Count).PArr("v", cs.V).EndObj();
            }
            j.EndArr();
            j.EndObj();

            j.Key("lanes").Arr();
            for (int i = 0; i < r.Lanes.Count; i++)
            {
                EventLane l = r.Lanes[i];
                j.Obj().P("kind", l.Kind).P("count", l.Count).PArr("v", l.PerBucket);
                if (l.Values != null) j.PArr("values", l.Values);
                j.Key("samples").Arr();
                for (int k = 0; k < l.Samples.Count; k++)
                {
                    SysEvent e = l.Samples[k];
                    j.Obj().P("t", l.SampleTs[k] / 1000).P("m", e.Mnemonic ?? "").P("x", Clip(e.Text, 300)).EndObj();
                }
                j.EndArr();
                j.EndObj();
            }
            j.EndArr();

            WriteConfigChanges(j, st, q, r.Zone, configs ?? ConfigHistory.FromStore(st));

            j.Key("tables").Obj();
            WriteNumbers(j, "called", r.TopCalled);
            WriteNumbers(j, "calling", r.TopCalling);
            WriteNumbers(j, "connected", r.TopConnected);
            WriteNumbers(j, "dialed", r.TopDialed);
            WriteNumbers(j, "dialpeer", r.TopDialPeers);
            WriteNumbers(j, "mediaip", r.TopMedia);
            WriteNumbers(j, "codec", r.TopCodecs);
            j.Key("causes").Arr();
            for (int i = 0; i < r.CauseRows.Count && i < 40; i++)
            {
                CauseRow c = r.CauseRows[i];
                j.Obj().P("cause", c.Cause).P("name", c.Cause < 0 ? "(none)" : Q850.Name(c.Cause)).P("calls", c.Calls).P("answered", c.Answered);
                j.PArr("rel", c.Rel).PArr("res", c.Res);
                j.EndObj();
            }
            j.EndArr();
            j.EndObj();

            j.Key("cube").Obj().P("alertN", r.AlertN).P("alertP50", r.AlertP50).P("alertP95", r.AlertP95)
             .P("qualityCalls", r.QualityCalls).P("qualityPoor", r.QualityPoor).P("mosN", r.MosN).P("mosAvg", r.MosN > 0 ? r.MosSum / r.MosN : double.NaN)
             .P("mosWorst", r.WorstMos).P("lossWorst", r.WorstLoss).P("jitterWorst", r.WorstJitter).P("poorMos", PoorMos).P("poorLoss", PoorLoss).EndObj();

            j.Key("canceled").Obj().P("maxSec", QueryResult.CancelMaxSec).PArr("dialer", r.CancelDialer).PArr("caller", r.CancelCaller);
            WriteClumps(j, "dialerPiles", r.CancelDialer);
            WriteClumps(j, "callerPiles", r.CancelCaller);
            j.EndObj();

            j.Key("choices").Obj().P("calls", r.ChoiceCalls);
            WriteCauseChoices(j, "causes", r.ChoiceCauses);
            WriteCauseChoices(j, "legCauses", r.ChoiceLegCauses);
            j.PArr("media", r.ChoiceMedia).PArr("result", r.ChoiceResult).PArr("released", r.ChoiceReleased).P("dialerCanceled", r.ChoiceDialerCanceled);
            WriteCounts(j, "dialPeers", r.ChoiceDialPeers);
            WriteCounts(j, "codecs", r.ChoiceCodecs);
            j.EndObj();

            WriteHealth(j, st);
            Clock.WriteJson(j, st);
            long managed, priv, ws;
            MemoryGuard.Sample(out managed, out priv, out ws);
            j.Key("memory").Obj().P("managed", managed).P("private", priv).P("workingSet", ws).EndObj();
            j.Key("rules").Obj().P("path", st.Rules.Path ?? "");
            j.Key("tags").Arr(); for (int i = 0; i < st.Rules.TagNames.Count; i++) j.Val(st.Rules.TagNames[i]); j.EndArr();
            j.Key("errors").Arr(); for (int i = 0; i < st.Rules.Errors.Count; i++) j.Val(st.Rules.Errors[i]); j.EndArr();
            j.EndObj();
            j.EndObj();
            return j.ToString();
        }

        static void WriteCounts(Json j, string key, Dictionary<string, int> counts)
        {
            j.Key(key).Arr();
            foreach (KeyValuePair<string, int> kv in counts) j.Obj().P("v", kv.Key).P("calls", kv.Value).EndObj();
            j.EndArr();
        }

        static void WriteCauseChoices(Json j, string key, CauseCounts cc)
        {
            j.Key(key).Arr();
            List<KeyValuePair<int, int>> l = cc.Items();
            for (int i = 0; i < l.Count; i++)
                j.Obj().P("cause", l[i].Key).P("name", l[i].Key < 0 ? "(none)" : Q850.Name(l[i].Key)).P("calls", l[i].Value).EndObj();
            j.EndArr();
        }

        static string Clip(string s, int n)
        {
            if (s == null) return "";
            return s.Length <= n ? s : s.Substring(0, n) + "...";
        }

        static void WriteNumbers(Json j, string key, List<NumberRow> rows)
        {
            j.Key(key).Arr();
            for (int i = 0; i < rows.Count; i++)
            {
                NumberRow r = rows[i];
                j.Obj().P("n", r.Number).P("calls", r.Calls).P("ans", r.Answered).P("aband", r.Abandoned).P("failed", r.Failed)
                 .P("acdMs", r.Answered > 0 ? (double)r.TalkMs / r.Answered : double.NaN).P("noMedia", r.NoMedia).EndObj();
            }
            j.EndArr();
        }

        public static void WriteHealth(Json j, Store st)
        {
            j.Key("health").Arr();
            for (int i = 0; i < st.Sources.Count; i++)
            {
                Source s = st.Sources[i];
                j.Obj().P("name", s.Name).P("filesFound", s.FilesFound).P("filesRead", s.FilesRead)
                 .P("lines", s.Lines).P("bytes", s.Bytes).P("skipped", s.Skipped).P("bad", s.BadRecords).P("iosGaps", s.IosGaps).P("iosMissing", s.IosMissing).P("iosRestarts", s.IosRestarts)
                 .P("legs", s.LegsAdded).P("unbound", s.Unbound).P("stubs", s.Stubs).P("dupLegs", s.DuplicateLegs)
                 .P("dupFeatures", s.DuplicateFeatures).P("features", s.Features).P("dumps", s.Dumps).P("iecs", s.Iecs)
                 .P("events", s.Events.Count).P("eventsDropped", s.EventsDropped).P("first", s.FirstMs / 1000).P("last", s.LastMs / 1000)
                 .P("loadSec", s.LoadSeconds).P("status", s.Status ?? "").P("lastFile", s.Files.Count > 0 ? s.FileName(s.Files.Count - 1) : "")
                 .P("also", string.Join(", ", s.AlsoNames.ToArray())).P("inBoth", s.LegsInBoth).P("fileLegs", s.LegsFromFiles).P("syslogLegs", s.LegsFromSyslog);
                j.Key("warnings").Arr();
                for (int k = 0; k < s.Warnings.Count; k++) j.Val(s.Warnings[k]);
                j.EndArr();
                j.EndObj();
            }
            j.EndArr();
        }

        // ------------------------------------------------------------ call list and detail

        public static readonly string[] SortKeys = { "start", "dur", "talk", "ring", "legs", "result", "cause", "calling", "called", "connected", "media", "a2t" };

        public static List<Call> Sorted(List<Call> matched, string sort, bool desc)
        {
            return Sorted(matched, sort, desc, null);
        }

        /// <summary>Sorted on a display clock: start order and ties follow the shifted start.</summary>
        public static List<Call> Sorted(List<Call> matched, string sort, bool desc, ZoneMap zm)
        {
            if (zm != null && !zm.Identity) return SortedShifted(matched, sort, desc, zm);
            List<Call> l = new List<Call>(matched);
            Comparison<Call> cmp;
            switch (sort)
            {
                case "dur": cmp = delegate (Call a, Call b) { return a.DurMs.CompareTo(b.DurMs); }; break;
                case "talk": cmp = delegate (Call a, Call b) { return a.TalkMs.CompareTo(b.TalkMs); }; break;
                case "ring": cmp = delegate (Call a, Call b) { return a.RingMs.CompareTo(b.RingMs); }; break;
                case "legs": cmp = delegate (Call a, Call b) { return a.Legs.Count.CompareTo(b.Legs.Count); }; break;
                case "result": cmp = delegate (Call a, Call b) { return ((int)a.Result).CompareTo((int)b.Result); }; break;
                case "cause": cmp = delegate (Call a, Call b) { return a.FinalCause.CompareTo(b.FinalCause); }; break;
                case "calling": cmp = delegate (Call a, Call b) { return string.CompareOrdinal(a.Calling, b.Calling); }; break;
                case "called": cmp = delegate (Call a, Call b) { return string.CompareOrdinal(a.Called, b.Called); }; break;
                case "connected": cmp = delegate (Call a, Call b) { return string.CompareOrdinal(a.ConnectedTo, b.ConnectedTo); }; break;
                case "media": cmp = delegate (Call a, Call b) { return ((int)a.Media).CompareTo((int)b.Media); }; break;
                case "a2t": cmp = delegate (Call a, Call b) { return a.A2tMs.CompareTo(b.A2tMs); }; break;
                default: cmp = delegate (Call a, Call b) { return a.Start.CompareTo(b.Start); }; break;
            }
            // ties keep start order: the list comes in (start, key) order, so compare by that next
            // rather than carry every call's position through the sort
            l.Sort(delegate (Call a, Call b)
            {
                int c = cmp(a, b);
                if (desc) c = -c;
                if (c != 0) return c;
                c = a.Start.CompareTo(b.Start);
                if (c != 0) return c;
                c = string.CompareOrdinal(a.Key, b.Key);
                return c != 0 ? c : a.Id.CompareTo(b.Id);
            });
            return l;
        }

        static List<Call> SortedShifted(List<Call> matched, string sort, bool desc, ZoneMap zm)
        {
            int n = matched.Count;
            long[] ds = new long[n];
            int[] idx = new int[n];
            for (int i = 0; i < n; i++) { ds[i] = zm.ToDisplay(matched[i].Src, matched[i].Start); idx[i] = i; }
            Comparison<Call> same = sort == "start" ? null : Same(sort);
            Array.Sort(idx, delegate (int a, int b)
            {
                int c = same == null ? ds[a].CompareTo(ds[b]) : same(matched[a], matched[b]);
                if (desc) c = -c;
                if (c != 0) return c;
                c = ds[a].CompareTo(ds[b]);
                if (c != 0) return c;
                c = string.CompareOrdinal(matched[a].Key, matched[b].Key);
                return c != 0 ? c : matched[a].Id.CompareTo(matched[b].Id);
            });
            List<Call> l = new List<Call>(n);
            for (int i = 0; i < n; i++) l.Add(matched[idx[i]]);
            return l;
        }

        /// <summary>The value comparison of one sort column, without tie-breaks.</summary>
        static Comparison<Call> Same(string sort)
        {
            switch (sort)
            {
                case "dur": return delegate (Call a, Call b) { return a.DurMs.CompareTo(b.DurMs); };
                case "talk": return delegate (Call a, Call b) { return a.TalkMs.CompareTo(b.TalkMs); };
                case "ring": return delegate (Call a, Call b) { return a.RingMs.CompareTo(b.RingMs); };
                case "legs": return delegate (Call a, Call b) { return a.Legs.Count.CompareTo(b.Legs.Count); };
                case "result": return delegate (Call a, Call b) { return ((int)a.Result).CompareTo((int)b.Result); };
                case "cause": return delegate (Call a, Call b) { return a.FinalCause.CompareTo(b.FinalCause); };
                case "calling": return delegate (Call a, Call b) { return string.CompareOrdinal(a.Calling, b.Calling); };
                case "called": return delegate (Call a, Call b) { return string.CompareOrdinal(a.Called, b.Called); };
                case "connected": return delegate (Call a, Call b) { return string.CompareOrdinal(a.ConnectedTo, b.ConnectedTo); };
                case "media": return delegate (Call a, Call b) { return ((int)a.Media).CompareTo((int)b.Media); };
                case "a2t": return delegate (Call a, Call b) { return a.A2tMs.CompareTo(b.A2tMs); };
                default: return delegate (Call a, Call b) { return 0; };
            }
        }

        public static void WriteCallRow(Json j, Store st, Call c)
        {
            WriteCallRow(j, st, c, null);
        }

        public static void WriteCallRow(Json j, Store st, Call c, ZoneMap zm)
        {
            long start = zm == null ? c.Start : zm.ToDisplay(c.Src, c.Start);
            j.Obj();
            j.P("id", c.Id).P("src", c.Src).P("key", c.Key).P("start", start).P("dur", c.DurMs).P("talk", c.TalkMs).P("ring", c.RingMs)
             .P("calling", c.Calling ?? "").P("called", c.Called ?? "").P("dialed", c.Dialed ?? "").P("connected", c.ConnectedTo ?? "")
             .P("legs", c.Legs.Count).P("result", Names.Results[(int)c.Result]).P("rel", Names.Releases[(int)c.Released])
             .P("media", Names.Medias[(int)c.Media]).P("xfers", c.Transfers).P("a2t", c.A2tMs).P("cause", c.FinalCause)
             .P("causeName", c.FinalCause < 0 ? "" : Q850.Name(c.FinalCause)).P("bpp", c.Bpp)
             .P("tags", st.Rules.TagList(c.Tags)).P("dialer", c.Dialer)
             .P("dump", c.Dumps != null).P("iec", c.Errors != null).P("feeds", Feeds(st, c));
            if (start != c.Start) j.P("cubeStart", c.Start);
            if (c.Media == Media.LowRate || c.Media == Media.OneWay)
            {
                // the leg behind the verdict, as packets a second each way over the time the CUBE counted them
                Leg w = MediaLeg(c);
                if (w != null && w.MediaMs > 0)
                {
                    double sec = w.MediaMs / 1000.0;
                    j.P("ptx", Math.Round(w.TxPackets / sec, 1)).P("prx", Math.Round(w.RxPackets / sec, 1)).P("psec", Math.Round(sec))
                     .P("pdir", w.In ? "in" : "out").P("ppeer", w.Peer ?? "");
                }
            }
            j.EndObj();
        }

        public const int FeedSyslog = 1, FeedFiles = 2, FeedMedia = 4;

        /// <summary>What reported the call: FeedSyslog and FeedFiles by where its legs came from, FeedMedia when the
        /// CUBE sent media statistics for any leg (a syslog stats block, even all zero, or measured values in a file).</summary>
        public static int Feeds(Store st, Call c)
        {
            int f = 0;
            Source s = c.Src >= 0 && c.Src < st.Sources.Count ? st.Sources[c.Src] : null;
            for (int i = 0; i < c.Legs.Count; i++)
            {
                Leg g = c.Legs[i];
                if (!g.FromFile || g.InBoth) f |= FeedSyslog;
                if (g.FromFile || g.InBoth) f |= FeedFiles;
                LegDetail d;
                if (g.Measured || (s != null && s.Details != null && s.Details.TryGetValue(g, out d) && d.Quality != null)) f |= FeedMedia;
            }
            return f;
        }

        /// <summary>The leg that gave the call its media verdict (the first leg judged the same way).</summary>
        public static Leg MediaLeg(Call c)
        {
            for (int i = 0; i < c.Legs.Count; i++) if (c.Legs[i].MediaClass == c.Media) return c.Legs[i];
            return null;
        }

        /// <summary>How the calls to the same called number came out, across everything loaded.</summary>
        public static void WriteSameNumber(Json j, Store st, Call c)
        {
            if (string.IsNullOrEmpty(c.Called)) return;
            int[] by = new int[Names.Results.Length];
            int n = 0, here = 0, hereFailed = 0;
            long from = long.MaxValue, to = long.MinValue;
            for (int i = 0; i < st.Calls.Count; i++)
            {
                Call o = st.Calls[i];
                if (o.Start < 0 || !string.Equals(o.Called, c.Called, StringComparison.Ordinal)) continue;
                n++;
                by[(int)o.Result]++;
                if (o.Start < from) from = o.Start;
                if (o.Start > to) to = o.Start;
                if (o.Src == c.Src)
                {
                    here++;
                    if (o.Result == Result.Rejected || o.Result == Result.Failed) hereFailed++;
                }
            }
            if (n == 0) return;
            j.Key("same").Obj().P("number", c.Called).P("calls", n).P("from", from).P("to", to)
             .P("failed", by[(int)Result.Rejected] + by[(int)Result.Failed]).P("answered", by[(int)Result.Answered])
             .P("thisCube", here).P("thisCubeFailed", hereFailed).P("cubes", st.Sources.Count);
            j.Key("byResult").Obj();
            for (int r = 0; r < by.Length; r++) if (by[r] > 0) j.P(Names.Results[r], by[r]);
            j.EndObj();
            j.EndObj();
        }

        /// <summary>One page of an already sorted call list.</summary>
        public static string CallsJson(Store st, List<Call> l, string sort, bool desc, int page, int size)
        {
            return CallsJson(st, l, sort, desc, page, size, null);
        }

        public static string CallsJson(Store st, List<Call> l, string sort, bool desc, int page, int size, ZoneMap zm)
        {
            int pages = Math.Max(1, (l.Count + size - 1) / size);
            if (page >= pages) page = pages - 1;
            if (page < 0) page = 0;
            Json j = new Json(1 << 16);
            j.Obj().P("total", l.Count).P("page", page).P("pages", pages).P("size", size).P("sort", sort).P("desc", desc);
            j.Key("rows").Arr();
            for (int i = page * size; i < l.Count && i < (page + 1) * size; i++) WriteCallRow(j, st, l[i], zm);
            j.EndArr();
            j.EndObj();
            return j.ToString();
        }

        /// <summary>Everything about one call, with the raw log lines it came from.</summary>
        public static string CallJson(Store st, Call c, bool withRaw)
        {
            return CallJson(st, c, withRaw, null);
        }

        /// <summary>The detail stays on the CUBE's clock; with a display clock, the start on it is added.</summary>
        public static string CallJson(Store st, Call c, bool withRaw, ZoneMap zm)
        {
            Json j = new Json(1 << 15);
            Source s = st.Sources[c.Src];
            j.Obj();
            j.Key("call"); WriteCallRow(j, st, c);
            j.P("padded", c.Padded ?? "").P("short", c.Padded != null ? GuidForm.Short(c.Padded) : "");
            j.P("source", s.Name).P("tz", Clock.LabelAt(s, c.Start).Length > 0 ? Clock.LabelAt(s, c.Start) : (s.Tz ?? ""));
            if (zm != null && !zm.Identity)
            {
                long ds = zm.ToDisplay(c.Src, c.Start);
                j.P("zoneStart", ds).P("zoneLabel", zm.Label(ds));
            }
            j.P("releaseLeg", c.ReleaseLeg).P("resultLabel", Names.ResultLabels[(int)c.Result]);
            WriteSameNumber(j, st, c);
            HashSet<int> away = Store.TransferredAway(c);
            // the features' keys and values are not kept in memory: read back with the raw lines
            CallLineSet lines = withRaw || c.Features != null ? CallLines(st, c) : null;
            j.Key("legs").Arr();
            for (int i = 0; i < c.Legs.Count; i++)
            {
                Leg g = c.Legs[i];
                j.Obj().P("i", i).P("legId", g.LegId >= 0 ? g.LegId.ToString("X", CultureInfo.InvariantCulture) : "")
                 .P("dir", g.FeatureOnly ? "?" : (g.In ? "in" : "out")).P("setup", g.Setup).P("connect", g.Connect).P("disc", g.Disconnect)
                 .P("answered", g.Answered).P("ring", g.RingMs).P("talk", g.TalkMs)
                 .P("peer", g.Peer ?? "").P("cgn", g.Cgn ?? "").P("cdn", g.Cdn ?? "").P("cause", g.Cause).P("causeText", g.CauseText ?? "")
                 .P("causeName", g.FeatureOnly ? "" : Q850.Name(g.Cause)).P("fromFile", g.FromFile || g.InBoth).P("fromSyslog", !g.FromFile || g.InBoth).P("measured", g.Measured)
                 .P("txp", g.TxPackets).P("txb", g.TxBytes).P("rxp", g.RxPackets).P("rxb", g.RxBytes).P("bpp", g.BytesPerPacket)
                 .P("media", Names.Medias[(int)g.MediaClass]).P("bound", g.Bound).P("stub", g.FeatureOnly).P("signal", g.SignalOnly)
                 .P("tags", st.Rules.TagList(g.Tags)).P("away", away != null && away.Contains(i))
                 .P("file", s.FileName(g.File)).P("line", g.Line);
                LegDetail ld;
                if (s.Details != null && s.Details.TryGetValue(g, out ld))
                {
                    // a leg known only from gw-accounting syslog stats has quality and nothing else
                    if (ld.DialPeer >= 0 || ld.Codec != null || ld.MediaAddress != null || ld.Alert >= 0 || ld.ReceivedCalled != null)
                        j.Key("d").Obj().P("dialPeer", ld.DialPeer).P("mediaIp", ld.MediaAddress ?? "").P("mediaPort", ld.MediaPort).P("codec", ld.Codec ?? "")
                         .P("alertMs", ld.Alert >= 0 && ld.Alert >= g.Setup ? ld.Alert - g.Setup : -1L)
                         .P("rxCalled", ld.ReceivedCalled ?? "").P("rxCalling", ld.ReceivedCalling ?? "")
                         .P("sentCalled", ld.SentCalled ?? "").P("sentCalling", ld.SentCalling ?? "").EndObj();
                    LegQuality lq = ld.Quality;
                    if (lq != null)
                        j.Key("q").Obj().P("mos", lq.Mos).P("loss", lq.LossRate).P("ooo", lq.OutOfOrderRate).P("lost", lq.LostPackets)
                         .P("early", lq.EarlyPackets).P("late", lq.LatePackets).P("rtt", lq.RoundTripMs).P("delay", lq.ReceiveDelayMs).P("icpif", lq.Icpif).P("jitter", lq.Jitter).EndObj();
                    string rowText;
                    bool elsewhere = ld.RowFile >= 0;
                    string[] named = lines != null && lines.Lines.TryGetValue(At(elsewhere ? ld.RowFile : g.File, elsewhere ? ld.RowLine : g.Line), out rowText) ? CdrParser.NamedColumns(rowText) : null;
                    if (named != null)
                    {
                        j.Key("cols").Arr();
                        for (int k = 0; k < named.Length; k++) j.Val(named[k]);
                        j.EndArr();
                    }
                }
                j.EndObj();
            }
            j.EndArr();
            j.Key("features").Arr();
            if (c.Features != null)
                for (int i = 0; i < c.Features.Count; i++)
                {
                    CallFeature f = c.Features[i];
                    j.Obj().P("fn", f.Fn).P("ft", f.Ft).P("legId", f.LegId >= 0 ? f.LegId.ToString("X", CultureInfo.InvariantCulture) : "")
                     .P("file", s.FileName(f.File)).P("line", f.Line);
                    string[] kv = lines != null ? lines.FeatureKv(f) : null;
                    j.Key("kv").Arr();
                    if (kv != null) for (int k = 0; k < kv.Length; k++) j.Val(kv[k]);
                    else if (f.Xto != null) { j.Val("Xto"); j.Val(f.Xto); }
                    j.EndArr();
                    j.EndObj();
                }
            j.EndArr();
            j.Key("errors").Arr();
            if (c.Errors != null)
                for (int i = 0; i < c.Errors.Count; i++)
                {
                    IecError e = c.Errors[i];
                    j.Obj().P("t", e.Ts).P("sub", e.Sub ?? "").P("text", e.Text ?? "").P("code", e.Code ?? "").P("callId", e.CallId ?? "")
                     .P("file", s.FileName(e.File)).P("line", e.Line).EndObj();
                }
            j.EndArr();
            j.Key("dumps").Arr();
            if (c.Dumps != null)
                for (int i = 0; i < c.Dumps.Count; i++)
                {
                    TraceDump d = c.Dumps[i];
                    j.Obj().P("t", d.Ts).P("utc", d.TsIsUtc).P("callId", d.CallId ?? "").P("guid", d.ShortGuid ?? "")
                     .P("called", d.Called ?? "").P("calling", d.Calling ?? "").P("sipCallId", d.SipCallId ?? "")
                     .P("cause", d.CauseText ?? "").P("lines", d.LineCount).P("truncated", d.Truncated)
                     .P("file", s.FileName(d.File)).P("line", d.Line).P("text", d.Text ?? "").EndObj();
                }
            j.EndArr();
            if (withRaw)
            {
                j.Key("raw").Arr();
                List<string> raw = RawLines(st, c, lines);
                for (int i = 0; i < raw.Count; i++) j.Val(raw[i]);
                j.EndArr();
            }
            j.EndObj();
            return j.ToString();
        }

        /// <summary>
        /// The log lines a call was read from, re-read from disk by file and line number (nothing raw
        /// is kept in memory). Each file is read once; dump bodies are not repeated here.
        /// </summary>
        public static List<string> RawLines(Store st, Call c)
        {
            return RawLines(st, c, CallLines(st, c));
        }

        public static List<string> RawLines(Store st, Call c, CallLineSet lines)
        {
            Source s = st.Sources[c.Src];
            List<string> outl = new List<string>();
            foreach (KeyValuePair<long, string> kv in lines.Lines)
            {
                int file = (int)(kv.Key >> 32), line = (int)(kv.Key & 0xffffffffL);
                outl.Add(s.FileName(file) + ":" + line + ": " + kv.Value);
            }
            for (int i = 0; i < lines.Problems.Count; i++) outl.Add(lines.Problems[i]);
            return outl;
        }

        /// <summary>Where a line is: file index in the high half, line number in the low half.</summary>
        public static long At(int file, int line) { return ((long)file << 32) | (uint)line; }

        /// <summary>The lines of one call, by place (At), and the files that could not be read.</summary>
        public sealed class CallLineSet
        {
            public readonly SortedDictionary<long, string> Lines = new SortedDictionary<long, string>();
            public readonly List<string> Problems = new List<string>();

            /// <summary>A feature record's keys and values, parsed again from its line; null when the line is gone.</summary>
            public string[] FeatureKv(CallFeature f)
            {
                string line;
                if (!Lines.TryGetValue(At(f.File, f.Line), out line)) return null;
                int k = line.IndexOf("FEAT_VSA=", StringComparison.Ordinal);
                if (k < 0)
                {
                    // a row of a call record file: the text it stands for
                    line = CdrParser.FeatureText(line);
                    if (line == null) return null;
                    k = 0;
                }
                Feature p = LineParser.ParseFeature(line, k);
                if (p == null || p.Fid != f.Fid) return null;
                return p.Kv;
            }
        }

        /// <summary>
        /// Reads the lines a call's records came from: legs, their TWC records (which can be in the next
        /// hour's file), features and internal errors, one pass per file.
        /// </summary>
        public static CallLineSet CallLines(Store st, Call c)
        {
            Source s = st.Sources[c.Src];
            CallLineSet set = new CallLineSet();
            SortedDictionary<int, SortedDictionary<int, bool>> want = new SortedDictionary<int, SortedDictionary<int, bool>>();
            Action<int, int> need = delegate (int file, int line)
            {
                if (line <= 0) return;
                SortedDictionary<int, bool> l;
                if (!want.TryGetValue(file, out l)) { l = new SortedDictionary<int, bool>(); want[file] = l; }
                l[line] = true;
            };
            for (int i = 0; i < c.Legs.Count; i++)
            {
                Leg g = c.Legs[i];
                need(g.File, g.Line);
                LegDetail row;
                if (s.Details != null && s.Details.TryGetValue(g, out row) && row.RowFile >= 0) need(row.RowFile, row.RowLine);
                // the TWC record's own place, which can be in the next hour's file
                int tf = g.TwcFile;
                if (tf >= 0 && (tf != g.File || g.TwcLine != g.Line)) need(tf, g.TwcLine);
            }
            if (c.Features != null) for (int i = 0; i < c.Features.Count; i++) need(c.Features[i].File, c.Features[i].Line);
            if (c.Errors != null) for (int i = 0; i < c.Errors.Count; i++) need(c.Errors[i].File, c.Errors[i].Line);
            string guidA = c.Key, guidB = c.Padded;
            foreach (KeyValuePair<int, SortedDictionary<int, bool>> kv in want)
            {
                if (kv.Key < 0 || kv.Key >= s.Files.Count) continue;
                string path = s.Files[kv.Key];
                try
                {
                    using (System.IO.FileStream fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read,
                                                                               System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete, 1 << 16))
                    using (System.IO.StreamReader rd = new System.IO.StreamReader(fs, LineParser.Latin1, false, 1 << 16))
                    {
                        int max = 0;
                        foreach (int ln in kv.Value.Keys) max = ln;
                        string line;
                        int n = 0;
                        while ((line = rd.ReadLine()) != null && n < max)
                        {
                            n++;
                            if (!kv.Value.ContainsKey(n)) continue;
                            // the neighbours read for features belong to this call only if they name it
                            string flat = line.Replace(" ", "");       // a call record file writes the GUID's words apart
                            if (flat.IndexOf(guidA, StringComparison.OrdinalIgnoreCase) < 0 &&
                                (guidB == null || flat.IndexOf(guidB, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                            set.Lines[At(kv.Key, n)] = Redact.Secrets(line);
                        }
                    }
                }
                catch (Exception ex) { set.Problems.Add(System.IO.Path.GetFileName(path) + ": cannot read (" + ex.Message + ")"); }
            }
            return set;
        }
    }
}
