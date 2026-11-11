using System;
using System.Collections.Generic;
using System.IO;

namespace CubeScope
{
    /// <summary>One CUBE: the hourly files that hold its syslog, and everything read from them.</summary>
    /// <summary>
    /// A feed's place in joining a leg to its TWC record. Syslog and call record files each keep their own:
    /// they are read in turns, and one must not close the leg the other is still waiting on.
    /// </summary>
    public sealed class FeedBinding
    {
        public Leg Pending;
        public Call PendingCall;
        public string SkipKey;
        /// <summary>A leg the other feed already gave, while this feed's copy of its features goes by.</summary>
        public Call SharedCall;
    }

    public sealed class Source
    {
        public int Index;
        public string Name;                                   // the IP or host the file names start with
        public string Folder;
        public readonly List<string> Files = new List<string>();      // index = Leg.File etc.
        public readonly Dictionary<string, Call> ByKey = new Dictionary<string, Call>(StringComparer.Ordinal);
        /// <summary>Calls by the short GUID trace dumps carry, as a number (GuidForm.ShortKey).</summary>
        public readonly Dictionary<long, Call> ByShort = new Dictionary<long, Call>();
        public readonly Dictionary<string, List<TraceDump>> PendingDumps = new Dictionary<string, List<TraceDump>>(StringComparer.Ordinal);
        public readonly List<SysEvent> Events = new List<SysEvent>();
        readonly HashSet<long> _eventSeen = new HashSet<long>();

        // binding state, carried across files: a leg's TWC can land in the next hour's file
        public readonly FeedBinding SyslogBinding = new FeedBinding(), FileBinding = new FeedBinding();

        public string Tz;
        public long FirstMs = -1, LastMs = -1;
        /// <summary>The newest call record from each feed. LastMs moves with any syslog message, so a CUBE still logging can have stopped sending these.</summary>
        public long LastSyslogRecordMs = -1, LastFileRecordMs = -1;
        public long TailPos = -1;          // where the load stopped reading the newest syslog file
        public int TailLine;
        public long CdrTailPos = -1;       // the same for the newest call record file
        public int CdrTailLine;

        /// <summary>The other names this CUBE's logs arrive under: its syslog files carry an address, its call record files its host name.</summary>
        public readonly List<string> AlsoNames = new List<string>();
        /// <summary>This CUBE sends both syslog and call record files, so a leg can arrive twice, and not in time order.</summary>
        public bool TwoFeeds;
        /// <summary>Earlier calls whose GUID has come back since, so a feed read later can still find them. Only kept with TwoFeeds.</summary>
        public Dictionary<string, List<Call>> OlderCalls;
        /// <summary>Legs reported by each feed, and by both. The rest of each count was reported by that feed alone.</summary>
        public long LegsFromSyslog, LegsFromFiles, LegsInBoth;
        /// <summary>A stats block can come in the chunk after its leg: the last leg merged into one the other feed gave.</summary>
        public Leg LastMerged, LastMergedInto;

        public void NoteAlso(string name)
        {
            for (int i = 0; i < AlsoNames.Count; i++) if (string.Equals(AlsoNames[i], name, StringComparison.OrdinalIgnoreCase)) return;
            AlsoNames.Add(name);
        }

        // health
        public int FilesFound, FilesRead;
        public long Lines, Bytes, Skipped, BadRecords, IosGaps, IosMissing, IosRestarts;
        public long LastIos = -1;
        public long LegsAdded, Unbound, Stubs, DuplicateLegs, DuplicateFeatures, Features, Dumps, Iecs;
        public readonly List<string> Warnings = new List<string>();
        public string Status = "";
        public double LoadSeconds;

        /// <summary>
        /// Events kept per source. A busy router logging into the same folder can send hundreds of
        /// thousands of messages that are only context here; past the cap they are counted, not kept.
        /// </summary>
        public const int EventCap = 100000;
        /// <summary>Longest event text kept; the dashboard shows 300 characters.</summary>
        public const int EventTextMax = 600;
        public long EventsDropped;

        /// <summary>The extra columns of call record files, by leg. Null on a syslog feed, which costs nothing.</summary>
        public Dictionary<Leg, LegDetail> Details;

        public void Warn(string w) { if (Warnings.Count < 40) Warnings.Add(w); }

        // the CUBE's clock against the receiver's (Clock.cs): one entry per file, in file order
        public readonly List<FileClock> Clocks = new List<FileClock>();
        public readonly Dictionary<string, long> ZoneCounts = new Dictionary<string, long>(StringComparer.Ordinal);
        public readonly List<ZoneRun> ZoneRuns = new List<ZoneRun>();
        public long ClockNotSet, ClockNotSynced, NotSetFirst = -1, NotSetLast = -1;
        /// <summary>Label changes kept; a CUBE flapping between labels would otherwise grow this forever.</summary>
        public const int ZoneRunCap = 2000;

        /// <summary>Adds what one chunk saw of the CUBE's clock.</summary>
        public void AddClock(ParsedChunk c)
        {
            if (c.ClockLines == 0) return;
            FileClock f = null;
            for (int i = Clocks.Count - 1; i >= 0 && i >= Clocks.Count - 3; i--)
                if (Clocks[i].File == c.FileIndex) { f = Clocks[i]; break; }
            if (f == null)
            {
                f = new FileClock();
                f.File = c.FileIndex;
                f.NameMs = c.FileIndex >= 0 && c.FileIndex < Files.Count ? Loader.NameMsOf(Files[c.FileIndex]) : -1;
                Clocks.Add(f);
            }
            if (f.First < 0 || c.ClockFirst < f.First) f.First = c.ClockFirst;
            if (c.ClockLast > f.Last) f.Last = c.ClockLast;
            f.Lines += c.ClockLines;
            ClockNotSet += c.ClockNotSet; ClockNotSynced += c.ClockNotSynced;
            if (c.NotSetFirst >= 0 && (NotSetFirst < 0 || c.NotSetFirst < NotSetFirst)) NotSetFirst = c.NotSetFirst;
            if (c.NotSetLast > NotSetLast) NotSetLast = c.NotSetLast;
            if (c.ZoneCounts != null)
                foreach (KeyValuePair<string, long> kv in c.ZoneCounts)
                {
                    long n;
                    ZoneCounts.TryGetValue(kv.Key, out n);
                    ZoneCounts[kv.Key] = n + kv.Value;
                }
            if (c.ZoneRuns != null)
                for (int i = 0; i < c.ZoneRuns.Count; i++)
                {
                    ZoneRun r = c.ZoneRuns[i];
                    if (ZoneRuns.Count > 0 && ZoneRuns[ZoneRuns.Count - 1].Tz == r.Tz) continue;
                    if (ZoneRuns.Count >= ZoneRunCap) break;
                    ZoneRuns.Add(r);
                }
        }

        public bool AddEvent(SysEvent e)
        {
            // full: count it, and keep the repeat check from growing without end
            if (Events.Count >= EventCap) { EventsDropped++; return false; }
            long h = e.Ts * 31 + (e.Mnemonic ?? "").GetHashCode();
            h = h * 31 + (e.Text ?? "").GetHashCode();
            if (!_eventSeen.Add(h)) return false;
            Events.Add(e);
            return true;
        }

        public string FileName(int i) { return i >= 0 && i < Files.Count ? Path.GetFileName(Files[i]) : ""; }
    }

    /// <summary>
    /// Every call of every source, plus the rules that derive the call-level answers. All writes
    /// happen under Sync on one thread at a time (the loader, then the tailer); readers take Sync too.
    /// </summary>
    public sealed class Store
    {
        public readonly object Sync = new object();
        public readonly List<Source> Sources = new List<Source>();
        public readonly List<Call> Calls = new List<Call>();
        public Rules Rules = Rules.Default();
        public long Version;
        /// <summary>--server-zone: the syslog receiver's zone ("UTC-6", "Mountain Standard Time"). Null:
        /// work it out from the CUBEs' labels (Clock.Server).</summary>
        public string ServerZoneId;
        readonly List<Call> _dirty = new List<Call>();
        Dictionary<string, string> _intern = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>A GUID seen again this long after its call ended is a new call (CVP can reuse one).</summary>
        public const long ReuseGapMs = 4L * 3600 * 1000;

        /// <summary>
        /// One copy of a repeated short string (peers, numbers, cause texts). Only for values that
        /// repeat: an entry costs more than the string it saves when it never comes back.
        /// </summary>
        public string Intern(string s)
        {
            if (s == null) return null;
            if (s.Length == 0) return "";
            string v;
            if (_intern.TryGetValue(s, out v)) return v;
            // live: start again rather than grow without end (the strings themselves stay where used)
            if (_intern.Count >= InternMax) _intern = new Dictionary<string, string>(StringComparer.Ordinal);
            _intern[s] = s;
            return s;
        }

        public const int InternMax = 500000;

        /// <summary>
        /// The end of a load: every string is already shared where it is used, and the table itself is
        /// tens of megabytes. Records read later start a new one.
        /// </summary>
        public void ReleaseIntern() { _intern = new Dictionary<string, string>(StringComparer.Ordinal); }

        public Source AddSource(string name, string folder)
        {
            Source s = new Source();
            s.Index = Sources.Count; s.Name = name; s.Folder = folder;
            Sources.Add(s);
            return s;
        }

        void Touch(Call c)
        {
            if (!c.Dirty) { c.Dirty = true; _dirty.Add(c); }
        }

        Call NewCall(Source s, string key)
        {
            Call c = new Call();
            c.Src = s.Index; c.Key = key;
            // A GUID can come back hours later as a different call (ReuseGapMs), so the GUID alone does
            // not name a call: Id does, and it is what the call list hands to the detail view.
            c.Id = Calls.Count;
            s.ByKey[key] = c;
            Calls.Add(c);
            Touch(c);
            return c;
        }

        void LearnPadded(Source s, Call c, string padded)
        {
            if (string.IsNullOrEmpty(padded) || c.Padded != null) return;
            // 87% of GUIDs have no word with a leading zero: padded and key are then the same text
            c.Padded = padded == c.Key ? c.Key : padded;
            long sk = GuidForm.ShortKey(padded);
            if (sk < 0) return;
            s.ByShort[sk] = c;
            if (s.PendingDumps.Count == 0) return;
            string sh = GuidForm.Short(padded);
            List<TraceDump> waiting;
            if (s.PendingDumps.TryGetValue(sh, out waiting))
            {
                for (int i = 0; i < waiting.Count; i++) AttachDump(c, waiting[i]);
                s.PendingDumps.Remove(sh);
            }
        }

        void AttachDump(Call c, TraceDump d)
        {
            if (c.Dumps == null) c.Dumps = new List<TraceDump>(1);
            for (int i = 0; i < c.Dumps.Count; i++)
                if (c.Dumps[i].Ts == d.Ts && c.Dumps[i].LineCount == d.LineCount) return;
            c.Dumps.Add(d);
            Touch(c);
        }

        void ClosePending(Source s, FeedBinding b)
        {
            // a leg the other feed gave is that feed's to count
            if (b.Pending != null && !b.Pending.Bound && b.Pending.FromFile == (b == s.FileBinding)) s.Unbound++;
            b.Pending = null; b.PendingCall = null;
        }

        /// <summary>
        /// Adds one parsed chunk in log order (DESIGN.md 4). Binding: the TWC record whose GUID
        /// matches arrives after its history line and before the next one; BXFER/CFA between them.
        /// </summary>
        public void Ingest(Source s, ParsedChunk chunk)
        {
            s.Lines += chunk.Lines; s.Bytes += chunk.Bytes; s.Skipped += chunk.Skipped;
            s.BadRecords += chunk.BadRecords;
            s.IosGaps += chunk.IosGaps; s.IosMissing += chunk.IosMissing; s.IosRestarts += chunk.IosRestarts;
            // the gap between the previous chunk's last message and this one's first
            if (s.LastIos >= 0 && chunk.FirstIos >= 0 && chunk.FirstIos != s.LastIos + 1)
            {
                long miss = chunk.FirstIos - s.LastIos - 1;
                if (miss > 0 && miss < 1000000) { s.IosGaps++; s.IosMissing += miss; }
                else s.IosRestarts++;
            }
            if (chunk.LastIos >= 0) s.LastIos = chunk.LastIos;
            for (int i = 0; i < chunk.Warnings.Count; i++) s.Warn(chunk.Warnings[i]);
            if (chunk.LastTz != null) s.Tz = chunk.LastTz;
            s.AddClock(chunk);
            List<object> recs = chunk.Records;
            FeedBinding bind = chunk.OneRowPerFeature ? s.FileBinding : s.SyslogBinding;
            Dictionary<Leg, Leg> mergedInto = null;
            int legNo = 0;
            for (int i = 0; i < recs.Count; i++)
            {
                object o = recs[i];
                Leg g = o as Leg;
                if (g != null)
                {
                    if (legNo >= chunk.LegKeys.Count) throw new InvalidOperationException("a leg was added to a chunk without its key (use AddLeg)");
                    g.FromFile = chunk.OneRowPerFeature;
                    Leg kept;
                    bool added = AddLeg(s, bind, g, chunk.LegKeys[legNo++], chunk.OneRowPerFeature, out kept);
                    if (kept != null)
                    {
                        if (mergedInto == null) mergedInto = new Dictionary<Leg, Leg>();
                        mergedInto[g] = kept;
                        s.LastMerged = g; s.LastMergedInto = kept;
                    }
                    if ((added || kept != null) && chunk.Details != null) AddDetail(s, g, kept ?? g, chunk.Details);
                    continue;
                }
                Feature f = o as Feature;
                if (f != null) { AddFeature(s, bind, f); continue; }
                SysEvent e = o as SysEvent;
                if (e != null)
                {
                    // the mnemonic repeats, the text rarely does
                    e.Mnemonic = Intern(e.Mnemonic);
                    if (e.Text != null && e.Text.Length > Source.EventTextMax) e.Text = e.Text.Substring(0, Source.EventTextMax) + "...";
                    s.AddEvent(e); Span(s, e.Ts); continue;
                }
                IecError x = o as IecError;
                if (x != null) { AddIec(s, x); continue; }
                TraceDump d = o as TraceDump;
                if (d != null) { AddDump(s, d); continue; }
            }
            if (chunk.MeasuredLegs != null)
            {
                foreach (KeyValuePair<Leg, LegDetail> kv in chunk.MeasuredLegs)
                {
                    Leg onto = kv.Key, into;
                    if (mergedInto != null && mergedInto.TryGetValue(onto, out into)) onto = into;
                    else if (onto == s.LastMerged) onto = s.LastMergedInto;
                    if (onto == null) continue;
                    onto.Measured = true;
                    if (kv.Value != null) PutDetail(s, onto, kv.Value);
                }
            }
            Version++;
        }

        /// <summary>Stores what a feed adds about a leg. The other feed may have said something already: nothing is overwritten.</summary>
        static void PutDetail(Source s, Leg g, LegDetail d)
        {
            if (s.Details == null) s.Details = new Dictionary<Leg, LegDetail>();
            LegDetail had;
            if (s.Details.TryGetValue(g, out had)) had.Absorb(d);
            else s.Details[g] = d;
        }

        void AddDetail(Source s, Leg g, Leg onto, Dictionary<Leg, LegDetail> details)
        {
            LegDetail d;
            if (!details.TryGetValue(g, out d)) return;
            d.MediaAddress = Intern(d.MediaAddress);
            d.Codec = Intern(d.Codec);
            d.ReceivedCalled = Intern(d.ReceivedCalled);
            d.ReceivedCalling = Intern(d.ReceivedCalling);
            d.SentCalled = Intern(d.SentCalled);
            d.SentCalling = Intern(d.SentCalling);
            if (onto != g) { d.RowFile = g.File; d.RowLine = g.Line; }
            PutDetail(s, onto, d);
        }

        void Span(Source s, long ms)
        {
            if (ms < 0) return;
            if (s.FirstMs < 0 || ms < s.FirstMs) s.FirstMs = ms;
            if (ms > s.LastMs) s.LastMs = ms;
        }

        static void CountRecord(Source s, Leg g)
        {
            long at = Math.Max(g.Disconnect, g.Setup);
            if (g.FromFile) { s.LegsFromFiles++; s.LastFileRecordMs = Math.Max(s.LastFileRecordMs, at); }
            else { s.LegsFromSyslog++; s.LastSyslogRecordMs = Math.Max(s.LastSyslogRecordMs, at); }
        }

        static void KeepOlder(Source s, string key, Call c)
        {
            if (!s.TwoFeeds) return;
            if (s.OlderCalls == null) s.OlderCalls = new Dictionary<string, List<Call>>(StringComparer.Ordinal);
            List<Call> l;
            if (!s.OlderCalls.TryGetValue(key, out l)) { l = new List<Call>(1); s.OlderCalls[key] = l; }
            l.Add(c);
        }

        /// <summary>A leg from the other feed that is much older than the GUID's newest call belongs to an earlier use of that GUID.</summary>
        Call OlderCall(Source s, string key, Leg g)
        {
            List<Call> l;
            if (s.OlderCalls != null && s.OlderCalls.TryGetValue(key, out l))
                for (int i = 0; i < l.Count; i++)
                    if (g.Setup - l[i].LastSeen <= ReuseGapMs && l[i].Legs.Count > 0 && l[i].Legs[0].Setup - g.Disconnect <= ReuseGapMs) return l[i];
            Call newest = s.ByKey[key];
            Call c = NewCall(s, key);
            s.ByKey[key] = newest;
            KeepOlder(s, key, c);
            return c;
        }

        /// <summary>
        /// Adds a leg. False when it was not added; kept is then the leg the CUBE's other feed already gave for it, if that
        /// is why. Which feed comes first depends only on when its files are read, so neither is the "main" one.
        /// </summary>
        bool AddLeg(Source s, FeedBinding b, Leg g, string key, bool oneRowPerFeature, out Leg kept)
        {
            kept = null;
            // a call record file repeats a leg for each of its features: the next row is this leg's next feature
            if (oneRowPerFeature && b.Pending != null && b.PendingCall.Key == key && b.Pending.SameAs(g)) return false;
            ClosePending(s, b);
            b.SharedCall = null;
            Call c;
            if (s.ByKey.TryGetValue(key, out c))
            {
                if (c.LastSeen >= 0 && g.Setup - c.LastSeen > ReuseGapMs) { KeepOlder(s, key, c); c = null; }   // the same GUID much later: a new call
                else if (c.Legs.Count > 0 && c.Legs[0].FromFile != g.FromFile && c.Legs[0].Setup - g.Disconnect > ReuseGapMs) c = OlderCall(s, key, g);
            }
            if (c == null) c = NewCall(s, key);
            Leg twin = null;
            long twinGap = long.MaxValue;
            for (int i = 0; i < c.Legs.Count; i++)
            {
                Leg had = c.Legs[i];
                if (had.FromFile != g.FromFile)
                {
                    if (!had.SameAsOtherFeed(g)) continue;
                    // the closest one, and one not yet matched before one that is (a file repeats a row for each feature)
                    long gap = Math.Abs(had.Setup - g.Setup) + Math.Abs(had.Disconnect - g.Disconnect) + (had.InBoth ? 1L << 40 : 0);
                    if (gap < twinGap) { twin = had; twinGap = gap; }
                    continue;
                }
                if (!had.SameAs(g)) continue;
                if (oneRowPerFeature) { b.SkipKey = null; return false; }    // already bound: its feature row still counts
                s.DuplicateLegs++;
                b.SkipKey = key;                               // its features are duplicates too
                return false;
            }
            if (twin != null)
            {
                // the same leg from the CUBE's other feed: one leg, with what each feed adds
                if (!twin.InBoth) { twin.InBoth = true; s.LegsInBoth++; CountRecord(s, g); }
                kept = twin;
                b.SkipKey = null;
                if (!twin.Bound) { b.Pending = twin; b.PendingCall = c; }
                else b.SharedCall = c;
                return false;
            }
            b.SkipKey = null;
            g.Peer = Intern(g.Peer);
            g.CauseText = Intern(g.CauseText);
            c.Legs.Add(g);
            if (g.Disconnect > c.LastSeen) c.LastSeen = g.Disconnect;
            Touch(c);
            b.Pending = g; b.PendingCall = c;
            s.LegsAdded++;
            CountRecord(s, g);
            Span(s, g.Disconnect);
            Span(s, g.Setup);                  // a call set up before the first record still belongs to the data
            return true;
        }

        void AddFeature(Source s, FeedBinding b, Feature f)
        {
            string key = f.Key;
            f.Key = null;
            if (b.SkipKey != null && b.SkipKey == key) { s.DuplicateFeatures++; return; }
            Leg p = b.Pending;
            Call pc = b.PendingCall;
            Call c;
            bool shared = false;
            if (p != null && pc.Key == key) c = pc;
            else if (b.SharedCall != null && b.SharedCall.Key == key) { c = b.SharedCall; shared = true; }
            else if (!s.ByKey.TryGetValue(key, out c)) c = null;

            if (f.Fn == "TWC")
            {
                // the other feed's copy of a TWC record this leg already has; syslog's alone carries the full GUID
                if (shared) { LearnPadded(s, c, f.Padded); return; }
                // the same TWC record twice (a repeated syslog message) is not a second leg
                if (c != null && HasFid(c, f.Fid)) { s.DuplicateFeatures++; return; }
                if (p != null && pc.Key == key && !p.Bound)
                {
                    Bind(s, p, pc, f);
                    b.Pending = null; b.PendingCall = null;
                    return;
                }
                // a TWC with no history line before it: keep what it says as a stub leg
                if (c == null) c = NewCall(s, key);
                Leg stub = new Leg();
                stub.FeatureOnly = true;
                stub.Origin = 0;
                stub.Setup = stub.Connect = stub.Disconnect = f.Ft;
                stub.File = f.File; stub.Line = f.Line;
                stub.Peer = "";
                stub.CauseText = "";
                Bind(s, stub, c, f);
                c.Legs.Add(stub);
                if (f.Ft > c.LastSeen) c.LastSeen = f.Ft;
                Touch(c);
                s.Stubs++;
                Span(s, f.Ft);
                return;
            }
            if (c == null) c = NewCall(s, key);
            if (HasFid(c, f.Fid)) { if (!shared) s.DuplicateFeatures++; return; }
            if (c.Features == null) c.Features = new List<CallFeature>(2);
            CallFeature cf = new CallFeature();
            cf.Fn = Intern(f.Fn); cf.Xto = Intern(f.Get("Xto"));
            cf.Ft = f.Ft; cf.Fid = f.Fid; cf.LegId = f.LegId; cf.File = f.File; cf.Line = f.Line;
            c.Features.Add(cf);
            LearnPadded(s, c, f.Padded);
            s.Features++;
            Touch(c);
        }

        /// <summary>Has this call already got the feature record with this id, TWC ones included.</summary>
        static bool HasFid(Call c, long fid)
        {
            if (fid < 0) return false;
            if (c.Features != null)
                for (int i = 0; i < c.Features.Count; i++) if (c.Features[i].Fid == fid) return true;
            int low = (int)fid;                              // a leg keeps the low 32 bits
            for (int i = 0; i < c.Legs.Count; i++) if (c.Legs[i].TwcFid == low) return true;
            return false;
        }

        void Bind(Source s, Leg g, Call c, Feature twc)
        {
            g.Bound = true;
            g.Cgn = Intern(twc.Cgn ?? "");
            g.Cdn = Intern(twc.Cdn ?? "");
            g.LegId = twc.LegId;
            g.TwcFid = (int)twc.Fid;
            g.TwcFile = twc.File; g.TwcLine = twc.Line;    // where it was, for the raw evidence
            LearnPadded(s, c, twc.Padded);
            Touch(c);
        }

        void AddIec(Source s, IecError e)
        {
            s.Iecs++;
            if (string.IsNullOrEmpty(e.Key)) return;
            string key = e.Key;
            Call c;
            if (!s.ByKey.TryGetValue(key, out c)) c = NewCall(s, key);
            if (c.Errors == null) c.Errors = new List<IecError>(1);
            for (int i = 0; i < c.Errors.Count; i++)
                if (c.Errors[i].Ts == e.Ts && c.Errors[i].Code == e.Code) return;
            e.Key = null;
            e.Sub = Intern(e.Sub); e.Text = Intern(e.Text); e.Code = Intern(e.Code);
            c.Errors.Add(e);
            if (e.Ts > c.LastSeen) c.LastSeen = e.Ts;
            Span(s, e.Ts);
            LearnPadded(s, c, e.Padded);
            Touch(c);
        }

        void AddDump(Source s, TraceDump d)
        {
            s.Dumps++;
            if (!d.TsIsUtc) Span(s, d.Ts);     // a UTC time is not on this CUBE's clock
            SysEvent ev = new SysEvent();
            ev.Ts = d.Ts; ev.Kind = "dump"; ev.Mnemonic = "Cover Buffer";
            ev.Text = "call failure trace: " + (d.CauseText ?? "") + (string.IsNullOrEmpty(d.Called) ? "" : " to " + d.Called);
            ev.File = d.File; ev.Line = d.Line;
            if (d.Ts >= 0) s.AddEvent(ev);
            string sh = d.ShortGuid;
            Call c;
            long sk = GuidForm.ShortKeyOf(sh);
            if (sk >= 0 && s.ByShort.TryGetValue(sk, out c)) { AttachDump(c, d); return; }
            string k = string.IsNullOrEmpty(sh) ? "~" + d.CallId + "~" + d.Ts : sh;
            List<TraceDump> l;
            if (!s.PendingDumps.TryGetValue(k, out l)) { l = new List<TraceDump>(1); s.PendingDumps[k] = l; }
            l.Add(d);
        }

        /// <summary>
        /// End of a load: the last pending leg closes, and dumps whose call never appeared become calls
        /// of their own (a rejected INVITE leaves only the dump). They are matched by CallID to an
        /// IEC-only call first.
        /// </summary>
        public void Settle(Source s, bool final)
        {
            if (final) { ClosePending(s, s.SyslogBinding); ClosePending(s, s.FileBinding); }
            if (s.PendingDumps.Count == 0) return;
            long newest = s.LastMs;
            List<string> done = new List<string>();
            foreach (KeyValuePair<string, List<TraceDump>> kv in s.PendingDumps)
            {
                List<TraceDump> l = kv.Value;
                // in a live feed the call's records may still be on their way; give them ten minutes
                if (!final && l[0].Ts >= 0 && newest - l[0].Ts < 600000) continue;
                for (int i = 0; i < l.Count; i++)
                {
                    TraceDump d = l[i];
                    Call c = FindByCallId(s, d);
                    if (c == null)
                    {
                        c = NewCall(s, Intern("dump-" + (d.ShortGuid ?? "") + "-" + d.CallId));
                        c.LastSeen = d.Ts;
                    }
                    AttachDump(c, d);
                }
                done.Add(kv.Key);
            }
            for (int i = 0; i < done.Count; i++) s.PendingDumps.Remove(done[i]);
            Version++;
        }

        Call FindByCallId(Source s, TraceDump d)
        {
            if (string.IsNullOrEmpty(d.CallId)) return null;
            // IEC-only calls are few; scan the newest calls backwards
            for (int i = Calls.Count - 1, n = 0; i >= 0 && n < 20000; i--, n++)
            {
                Call c = Calls[i];
                if (c.Src != s.Index || c.Errors == null || c.Legs.Count > 0) continue;
                for (int j = 0; j < c.Errors.Count; j++)
                    if (c.Errors[j].CallId == d.CallId && Math.Abs(c.Errors[j].Ts - d.Ts) < 120000) return c;
            }
            return null;
        }

        // ------------------------------------------------------------ derivation

        /// <summary>Recomputes every call touched since the last call. Cheap: each call is small.</summary>
        public int DeriveDirty()
        {
            int n = _dirty.Count;
            for (int i = 0; i < _dirty.Count; i++)
            {
                Call c = _dirty[i];
                c.Dirty = false;
                Derive(c, Rules);
            }
            _dirty.Clear();
            if (n > 0) { Version++; _sortedVersion = -1; }
            return n;
        }

        List<Call> _sorted = new List<Call>();
        long _sortedVersion = -1;

        /// <summary>All calls ordered by start time; rebuilt only when something changed.</summary>
        public List<Call> ByStart()
        {
            if (_sortedVersion == Version) return _sorted;
            DeriveDirty();
            List<Call> l = new List<Call>(Calls.Count);
            for (int i = 0; i < Calls.Count; i++) if (Calls[i].Start >= 0) l.Add(Calls[i]);
            l.Sort(delegate (Call a, Call b)
            {
                int c = a.Start.CompareTo(b.Start);
                return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
            });
            _sorted = l;
            _sortedVersion = Version;
            return l;
        }

        /// <summary>
        /// The call-level answers (DESIGN.md 5.2). Kept static and pure so the self-test can drive it
        /// on hand-built calls.
        /// </summary>
        public static void Derive(Call c, Rules rules)
        {
            LegList legs = c.Legs;
            c.Start = -1; c.End = -1; c.Answer = -1; c.A2tMs = -1;
            c.Calling = c.Called = c.Dialed = c.ConnectedTo = "";
            c.Result = Result.Unknown; c.Released = Released.Unknown; c.ReleaseLeg = -1;
            c.FinalCause = -1; c.Media = Media.Unknown; c.Bpp = 0; c.Transfers = 0; c.Tags = 0; c.Dialer = false;

            Leg firstIn = null, firstOut = null, lastOutSetup = null, lastConnOut = null;
            for (int i = 0; i < legs.Count; i++)
            {
                Leg g = legs[i];
                g.MediaFrom = -1;
                if (rules != null) rules.Apply(g);
                c.Tags |= g.Tags;
                if (c.Start < 0 || g.Setup < c.Start) c.Start = g.Setup;
                if (g.Disconnect > c.End) c.End = g.Disconnect;
                if (g.FeatureOnly) continue;
                if (g.In) { if (firstIn == null || g.Setup < firstIn.Setup) firstIn = g; }
                else if (g.Out)
                {
                    if (firstOut == null || g.Setup < firstOut.Setup) firstOut = g;
                    if (lastOutSetup == null || g.Setup >= lastOutSetup.Setup) lastOutSetup = g;
                    if (g.Answered)
                    {
                        if (c.Answer < 0 || g.Connect < c.Answer) c.Answer = g.Connect;
                        if (lastConnOut == null || g.Connect >= lastConnOut.Connect) lastConnOut = g;
                    }
                }
            }
            if (c.Errors != null)
                for (int i = 0; i < c.Errors.Count; i++)
                {
                    long t = c.Errors[i].Ts;
                    if (c.Start < 0 || t < c.Start) c.Start = t;
                    if (t > c.End) c.End = t;
                }
            if (c.Dumps != null)
                for (int i = 0; i < c.Dumps.Count; i++)
                {
                    long t = c.Dumps[i].Ts;
                    if (t < 0 || (c.Dumps[i].TsIsUtc && c.Start >= 0)) continue;
                    if (c.Start < 0 || t < c.Start) c.Start = t;
                    if (t > c.End) c.End = t;
                }
            if (c.Start < 0 && legs.Count > 0) { c.Start = legs[0].Setup; c.End = legs[0].Disconnect; }

            // numbers
            if (firstIn != null)
            {
                c.Calling = !string.IsNullOrEmpty(firstIn.Cgn) ? firstIn.Cgn : (firstIn.Peer ?? "");
                c.Dialed = firstIn.Cdn ?? "";
                c.Dialer = firstIn.SignalOnly;
            }
            else if (firstOut != null) c.Calling = firstOut.Cgn ?? "";
            if (firstOut != null) c.Called = !string.IsNullOrEmpty(firstOut.Cdn) ? firstOut.Cdn : (firstOut.Peer ?? "");
            else c.Called = c.Dialed;
            if (string.IsNullOrEmpty(c.Calling) && firstOut != null && !string.IsNullOrEmpty(firstOut.Cgn)) c.Calling = firstOut.Cgn;
            if (lastConnOut != null && !string.IsNullOrEmpty(lastConnOut.Peer) && lastConnOut.Peer != c.Called)
                c.ConnectedTo = lastConnOut.Peer;
            if (string.IsNullOrEmpty(c.Called) && c.Dumps != null && c.Dumps.Count > 0) c.Called = c.Dumps[0].Called ?? "";
            if (string.IsNullOrEmpty(c.Calling) && c.Dumps != null && c.Dumps.Count > 0) c.Calling = c.Dumps[0].Calling ?? "";

            // result: the outbound leg's cause decides, the inbound one is the CUBE's mapping
            bool answered = c.Answer >= 0;
            if (!answered && firstOut == null && firstIn != null && firstIn.Answered)
            {
                answered = true;                     // the CUBE itself answered (no leg placed)
                c.Answer = firstIn.Connect;
            }
            int cause = -1;
            if (answered) { c.Result = Result.Answered; }
            else if (lastOutSetup != null) cause = lastOutSetup.Cause;
            else if (firstIn != null) cause = firstIn.Cause;
            if (!answered)
            {
                bool noDialPeer = false;
                if (c.Errors != null)
                    for (int i = 0; i < c.Errors.Count; i++)
                        if (c.Errors[i].Text != null && c.Errors[i].Text.IndexOf("dialpeer", StringComparison.OrdinalIgnoreCase) >= 0)
                            noDialPeer = true;
                if (cause < 0 && c.Dumps != null && c.Dumps.Count > 0) cause = c.Dumps[0].CauseCode;
                if (noDialPeer && lastOutSetup == null) c.Result = Result.Rejected;
                else if (cause < 0) c.Result = (c.Errors != null || c.Dumps != null) ? Result.Failed : Result.Unknown;
                else c.Result = ResultOf(cause);
                c.FinalCause = (short)cause;
            }

            // who released
            if (answered) ReleasedAnswered(c);
            else
            {
                switch (c.Result)
                {
                    case Result.Abandoned: c.Released = Released.Caller; break;
                    case Result.Busy: case Result.NoAnswer: case Result.Rejected: c.Released = Released.Called; break;
                    case Result.Failed: c.Released = Released.Network; break;
                }
                if (lastOutSetup != null) c.ReleaseLeg = legs.IndexOf(lastOutSetup);
                else if (firstIn != null) c.ReleaseLeg = legs.IndexOf(firstIn);
            }

            // transfers: a BXFER is logged once per bridged leg, so group by target and time
            if (c.Features != null)
            {
                List<CallFeature> bx = new List<CallFeature>();
                for (int i = 0; i < c.Features.Count; i++) if (c.Features[i].Fn == "BXFER") bx.Add(c.Features[i]);
                bx.Sort(delegate (CallFeature a, CallFeature b) { return a.Ft.CompareTo(b.Ft); });
                long firstXfer = -1;
                for (int i = 0; i < bx.Count; i++)
                {
                    bool same = i > 0 && bx[i].Ft - bx[i - 1].Ft < 1000 && bx[i].Xto == bx[i - 1].Xto;
                    if (!same) c.Transfers++;
                    if (firstXfer < 0) firstXfer = bx[i].Ft;
                }
                // answer to transfer connect: from the first answer to the last outbound leg that
                // connected after the transfer started - the moment the transferee had someone again
                if (firstXfer >= 0 && c.Answer >= 0 && c.Answer <= firstXfer)
                {
                    long last = -1;
                    for (int i = 0; i < legs.Count; i++)
                    {
                        Leg g = legs[i];
                        if (g.Out && g.Answered && g.Setup >= firstXfer - 50 && g.Connect > last) last = g.Connect;
                    }
                    if (last > c.Answer) c.A2tMs = last - c.Answer;
                }
            }

            // On a dialer call the CUBE counts no packets on the customer leg while it is bridged to the
            // dialer's signalling-only leg (answer detection); counting starts at the transfer. A leg
            // that answered before the first transfer is judged from it, and with no transfer at all it
            // never carried counted media.
            if (c.Dialer)
            {
                long firstXfer = FirstTransfer(c);
                for (int i = 0; i < legs.Count; i++)
                {
                    Leg g = legs[i];
                    if (!g.Out || !g.Answered) continue;
                    if (firstXfer < 0) g.MediaFrom = g.Disconnect;
                    else if (g.Connect <= firstXfer) g.MediaFrom = firstXfer;
                }
            }
            // media: worst over answered talking legs that are not signalling-only
            Leg busiest = null;
            for (int i = 0; i < legs.Count; i++)
            {
                Leg g = legs[i];
                Media m = g.MediaClass;
                if (m == Media.Unknown) continue;
                if ((int)m > (int)c.Media) c.Media = m;
                if (busiest == null || g.TxPackets + g.RxPackets > busiest.TxPackets + busiest.RxPackets) busiest = g;
            }
            if (busiest != null) c.Bpp = busiest.BytesPerPacket;

        }

        /// <summary>
        /// The transferor's leg: of the two legs a BXFER pair names, the one that ended first. The
        /// CUBE clears it because of the transfer, not because anyone hung up, so on a short call it
        /// must not be mistaken for the side that released.
        /// </summary>
        public static HashSet<int> TransferredAway(Call c)
        {
            if (c.Features == null) return null;
            HashSet<int> away = null;
            for (int i = 0; i < c.Features.Count; i++)
            {
                CallFeature f = c.Features[i];
                if (f.Fn != "BXFER" || f.LegId < 0) continue;
                int best = -1, named = 0;
                for (int j = 0; j < c.Features.Count; j++)
                {
                    CallFeature o = c.Features[j];
                    if (o.Fn != "BXFER" || Math.Abs(o.Ft - f.Ft) >= 1000 || o.Xto != f.Xto) continue;
                    int li = LegIndex(c, o.LegId);
                    if (li < 0) continue;
                    named++;
                    if (best < 0 || c.Legs[li].Disconnect < c.Legs[best].Disconnect) best = li;
                }
                if (best < 0 || named < 2) continue;      // with one leg named there is no telling which side it was
                if (away == null) away = new HashSet<int>();
                away.Add(best);
            }
            return away;
        }

        static long FirstTransfer(Call c)
        {
            long t = -1;
            if (c.Features == null) return t;
            for (int i = 0; i < c.Features.Count; i++)
                if (c.Features[i].Fn == "BXFER" && c.Features[i].Ft >= 0 && (t < 0 || c.Features[i].Ft < t)) t = c.Features[i].Ft;
            return t;
        }

        static int LegIndex(Call c, int legId)
        {
            for (int i = 0; i < c.Legs.Count; i++) if (c.Legs[i].LegId == legId) return i;
            return -1;
        }

        public static Result ResultOf(int cause)
        {
            switch (cause)
            {
                case 16: return Result.Abandoned;
                case 17: return Result.Busy;
                case 18: case 19: case 20: return Result.NoAnswer;
                case 1: case 21: case 22: case 28: return Result.Rejected;
                default: return Result.Failed;
            }
        }

        /// <summary>
        /// The teardown is a cascade: the side that hung up has its leg cleared first, then the CUBE
        /// clears the others within milliseconds. Walk back from the last disconnect while the gaps are
        /// short; the earliest leg in that cluster is the side that released (DESIGN.md 1.11).
        /// </summary>
        static void ReleasedAnswered(Call c)
        {
            LegList legs = c.Legs;
            HashSet<int> away = TransferredAway(c);
            List<int> order = new List<int>();
            for (int i = 0; i < legs.Count; i++)
                if (!legs[i].FeatureOnly && legs[i].Origin != 0 && (away == null || !away.Contains(i))) order.Add(i);
            if (order.Count == 0)
                for (int i = 0; i < legs.Count; i++) if (!legs[i].FeatureOnly && legs[i].Origin != 0) order.Add(i);
            if (order.Count == 0) return;
            order.Sort(delegate (int a, int b) { return legs[b].Disconnect.CompareTo(legs[a].Disconnect); });
            int k = 0;
            while (k + 1 < order.Count && legs[order[k]].Disconnect - legs[order[k + 1]].Disconnect <= 2000) k++;
            Leg rel = legs[order[k]];
            c.ReleaseLeg = order[k];
            c.FinalCause = rel.Cause;
            if (k >= 1 && legs[order[k - 1]].Disconnect - rel.Disconnect <= 1 &&
                legs[order[k - 1]].Origin != rel.Origin)
            {
                c.Released = Released.Tie;
                return;
            }
            if (Q850.IsNetwork(rel.Cause)) c.Released = Released.Network;
            else c.Released = rel.In ? Released.Caller : Released.Called;
        }
    }
}
