using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CubeScope
{
    /// <summary>One log file on disk, with the hour its name says it covers (receiver's clock).</summary>
    public sealed class LogFile
    {
        public string Path;
        public string SourceName;
        public long NameMs = -1;        // from <name>-yyyy-mm-dd-hh.log; -1 when the name has no date
        public long Size;
        public DateTime Mtime;
        /// <summary>A CUBE's call record file (cdr.host.time), not a syslog file.</summary>
        public bool IsCdr;
    }

    public sealed class LoadOptions
    {
        public string Root;
        public string Pattern = "*.log";
        public List<string> Cubes = new List<string>();
        public int Days = 14;            // newest file's day minus this many; 0 = everything
        public int Hours = 0;            // when set: only each CUBE's newest this-many hours of files (the service)
        public long FromMs = -1, ToMs = -1;
        public int Threads = 0;
        public bool Quiet;
        /// <summary>Which names are the same CUBE. Null: LoadAll finds out for itself and forgets afterwards.</summary>
        public CubeLinks Links;
        /// <summary>The files will be followed after the load: the newest file's unfinished last line
        /// is left for the tailer, and a leg still waiting for its TWC record keeps waiting.</summary>
        public bool Live;
    }

    /// <summary>
    /// Finds the hourly files, groups them into sources by the name before -yyyy-mm-dd-hh, and reads
    /// them: closed files in parallel (parsing is the cost), ingested strictly in time order (binding
    /// a leg to its TWC record needs log order, and that can cross an hour boundary).
    /// </summary>
    public static class Loader
    {
        static readonly Regex Hourly = new Regex(@"^(?<src>.+?)-(?<y>\d{4})-(?<m>\d{2})-(?<d>\d{2})-(?<h>\d{2})(?:\.[A-Za-z0-9]+)?\.log$",
                                                 RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        // a CUBE's call record file: <name>.<hostname>.<MM_dd_yyyy_HH_mm_ss.mmm>, the name being what primary sftp gave
        static readonly Regex CdrFile = new Regex(@"^(?<prefix>[^.]+)\.(?<src>.+)\.(?<mo>\d{2})_(?<d>\d{2})_(?<y>\d{4})_(?<h>\d{2})_(?<mi>\d{2})_(?<s>\d{2})\.(?<ms>\d{1,3})$",
                                                  RegexOptions.CultureInvariant);
        /// <summary>What Windows is asked for: it narrows the listing, and IsCdrName decides.</summary>
        public const string CdrPattern = "*_*_*_*_*_*.*";

        public static bool IsCdrName(string name) { return CdrFile.IsMatch(name ?? ""); }

        /// <summary>The source an hourly log belongs to by its name (name-yyyy-mm-dd-hh.log), or null when it is not named that way.</summary>
        public static string HourlySource(string name)
        {
            Match m = Hourly.Match(name ?? "");
            return m.Success ? m.Groups["src"].Value : null;
        }

        /// <summary>Whether --cube lets this source in: no list means all, otherwise a full name or the start of one.</summary>
        public static bool Wanted(LoadOptions o, string source)
        {
            if (CubeWanted(o.Cubes, source)) return true;
            if (o.Links == null) return false;
            string name = o.Links.Resolve(source);
            if (CubeWanted(o.Cubes, name)) return true;
            foreach (string other in o.Links.OtherNames(name)) if (CubeWanted(o.Cubes, other)) return true;
            return false;
        }

        public static bool CubeWanted(List<string> cubes, string source)
        {
            if (cubes.Count == 0) return true;
            for (int k = 0; k < cubes.Count; k++)
                if (source.Equals(cubes[k], StringComparison.OrdinalIgnoreCase) ||
                    source.StartsWith(cubes[k], StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static long CdrMs(Match m)
        {
            Func<string, int> n = delegate (string g) { return int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture); };
            try { return TimeUtil.Ms(n("y"), n("mo"), n("d"), n("h"), n("mi"), n("s"), n("ms")); }
            catch (ArgumentException) { return -1; }
        }

        static LogFile CdrEntry(string path, Match m)
        {
            LogFile f = new LogFile();
            f.Path = path; f.IsCdr = true; f.SourceName = m.Groups["src"].Value; f.NameMs = CdrMs(m);
            return f;
        }

        /// <summary>
        /// The call record files under root, at most two folder levels down. near limits it to a day either side
        /// of that date, by the file's name: a CUBE writes a file every few seconds, so a folder can hold hundreds of
        /// thousands, and listing only today's keeps following it cheap. Sizes are not read here.
        /// </summary>
        public static List<LogFile> FindCdr(string root, DateTime? near)
        {
            List<LogFile> r = new List<LogFile>();
            if (!Directory.Exists(root)) return r;
            CollectCdr(root, near, r, 0);
            return r;
        }

        static void CollectCdr(string dir, DateTime? near, List<LogFile> r, int depth)
        {
            List<string> patterns = new List<string>();
            if (near == null) patterns.Add(CdrPattern);
            else for (int d = -1; d <= 1; d++) patterns.Add("*." + near.Value.AddDays(d).ToString("MM_dd_yyyy", CultureInfo.InvariantCulture) + "_*");
            for (int p = 0; p < patterns.Count; p++)
            {
                try
                {
                    foreach (string path in Directory.EnumerateFiles(dir, patterns[p]))
                    {
                        Match m = CdrFile.Match(Path.GetFileName(path));
                        if (m.Success) r.Add(CdrEntry(path, m));
                    }
                }
                catch (Exception) { return; }
            }
            if (depth >= 2) return;
            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch (Exception) { return; }
            for (int i = 0; i < subs.Length; i++) CollectCdr(subs[i], near, r, depth + 1);
        }

        /// <summary>Every matching file under root, at most two folder levels down.</summary>
        public static List<LogFile> Find(string root, string pattern)
        {
            List<LogFile> r = new List<LogFile>();
            if (File.Exists(root)) { r.Add(Describe(root)); return r; }
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("no such folder: " + root);
            Collect(root, pattern, r, 0);
            return r;
        }

        static void Collect(string dir, string pattern, List<LogFile> r, int depth)
        {
            string[] files;
            try { files = Directory.GetFiles(dir, pattern); }
            catch (Exception) { return; }
            for (int i = 0; i < files.Length; i++)
            {
                string n = Path.GetFileName(files[i]);
                // the receiver's companion files are not logs
                if (n.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)) continue;
                // a .merged.log beside its .log repeats it; the hourly file wins
                if (n.EndsWith(".merged.log", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(Path.Combine(dir, n.Substring(0, n.Length - 11) + ".log"))) continue;
                r.Add(Describe(files[i]));
            }
            if (depth >= 2) return;
            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch (Exception) { return; }
            Array.Sort(subs, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < subs.Length; i++) Collect(subs[i], pattern, r, depth + 1);
        }

        /// <summary>The hour a receiver's file name gives (its own clock), or -1 when the name has none.</summary>
        public static long NameMsOf(string path)
        {
            Match m = Hourly.Match(Path.GetFileName(path) ?? "");
            if (!m.Success) return -1;
            return TimeUtil.Ms(int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture),
                               int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture),
                               int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture),
                               int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture), 0, 0, 0);
        }

        public static LogFile Describe(string path)
        {
            LogFile f = new LogFile();
            f.Path = path;
            string n = Path.GetFileName(path);
            Match cm = CdrFile.Match(n);
            if (cm.Success) return CdrEntry(path, cm);
            Match m = Hourly.Match(n);
            if (m.Success)
            {
                f.SourceName = m.Groups["src"].Value;
                f.NameMs = TimeUtil.Ms(int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture),
                                       int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture),
                                       int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture),
                                       int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture), 0, 0, 0);
            }
            else f.SourceName = Path.GetFileName(Path.GetDirectoryName(path));
            try
            {
                FileInfo fi = new FileInfo(path);
                f.Size = fi.Length;
                f.Mtime = fi.LastWriteTime;
            }
            catch (Exception) { }
            return f;
        }

        /// <summary>Files grouped per source in time order, trimmed to the requested days or range.</summary>
        public static SortedDictionary<string, List<LogFile>> Plan(List<LogFile> files, LoadOptions o)
        {
            SortedDictionary<string, List<LogFile>> g = new SortedDictionary<string, List<LogFile>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < files.Count; i++)
            {
                LogFile f = files[i];
                if (!Wanted(o, f.SourceName)) continue;
                string name = o.Links != null ? o.Links.Resolve(f.SourceName) : f.SourceName;
                List<LogFile> l;
                if (!g.TryGetValue(name, out l)) { l = new List<LogFile>(); g[name] = l; }
                l.Add(f);
            }
            // The file name is the receiver's hour, which can be hours off the CUBE's clock: keep a
            // day either side of a requested range, and trim by the records themselves later.
            const long Day = 86400000L;
            foreach (List<LogFile> l in g.Values)
            {
                l.Sort(delegate (LogFile a, LogFile b)
                {
                    long ta = a.NameMs >= 0 ? a.NameMs : TimeUtil.FromDate(a.Mtime);
                    long tb = b.NameMs >= 0 ? b.NameMs : TimeUtil.FromDate(b.Mtime);
                    int c = ta.CompareTo(tb);
                    return c != 0 ? c : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
                });
                // each feed by its own newest: a syslog file is named on the receiver's clock, a call record file on the CUBE's
                long newestLog = -1, newestCdr = -1;
                for (int i = 0; i < l.Count; i++)
                {
                    if (l[i].IsCdr) newestCdr = Math.Max(newestCdr, l[i].NameMs);
                    else newestLog = Math.Max(newestLog, l[i].NameMs);
                }
                long loLog = Earliest(newestLog, o), loCdr = Earliest(newestCdr, o);
                long hi = o.ToMs >= 0 ? o.ToMs + Day : long.MaxValue;
                l.RemoveAll(delegate (LogFile f) { return f.NameMs >= 0 && (f.NameMs < (f.IsCdr ? loCdr : loLog) || f.NameMs > hi); });
            }
            return g;
        }

        static long Earliest(long newest, LoadOptions o)
        {
            const long Day = 86400000L;
            if (o.FromMs >= 0) return o.FromMs - Day;
            if (o.ToMs >= 0 || newest < 0) return long.MinValue;
            if (o.Hours > 0) return newest - o.Hours * 3600000L;          // the newest file plus the Hours before it
            if (o.Days > 0) return TimeUtil.FromDate(TimeUtil.ToDate(newest).Date.AddDays(-o.Days + 1)) - Day;
            return long.MinValue;
        }

        /// <summary>Reads every planned file into the store. Progress goes to the console and to Status.</summary>
        public static void Load(Store st, SortedDictionary<string, List<LogFile>> plan, LoadOptions o)
        {
            int threads = o.Threads > 0 ? o.Threads : Math.Max(1, Math.Min(8, Environment.ProcessorCount));
            foreach (KeyValuePair<string, List<LogFile>> kv in plan)
            {
                Source s;
                lock (st.Sync)
                {
                    s = null;
                    for (int i = 0; i < st.Sources.Count; i++) if (st.Sources[i].Name == kv.Key) s = st.Sources[i];
                    if (s == null) s = st.AddSource(kv.Key, kv.Value.Count > 0 ? Path.GetDirectoryName(kv.Value[0].Path) : "");
                }
                Stopwatch sw = Stopwatch.StartNew();
                List<LogFile> files = kv.Value;
                s.FilesFound = files.Count;
                int first, lastLog = -1, lastCdr = -1;
                lock (st.Sync)
                {
                    first = s.Files.Count;
                    for (int i = 0; i < files.Count; i++)
                    {
                        s.Files.Add(files[i].Path);
                        if (files[i].IsCdr) lastCdr = i; else lastLog = i;
                        if (!string.Equals(files[i].SourceName, kv.Key, StringComparison.OrdinalIgnoreCase)) s.NoteAlso(files[i].SourceName);
                    }
                    s.TwoFeeds = lastLog >= 0 && lastCdr >= 0;
                }
                // batches of `threads` files: parse in parallel, ingest in order
                for (int b = 0; b < files.Count; b += threads)
                {
                    int n = Math.Min(threads, files.Count - b);
                    ParsedChunk[] chunks = new ParsedChunk[n];
                    Exception[] errs = new Exception[n];
                    Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = threads }, delegate (int k)
                    {
                        LogFile f = files[b + k];
                        int ry = 0, rm = 0;
                        if (f.NameMs >= 0) { DateTime d = TimeUtil.ToDate(f.NameMs); ry = d.Year; rm = d.Month; }
                        else { ry = f.Mtime.Year; rm = f.Mtime.Month; }
                        try
                        {
                            bool newest = o.Live && (b + k == lastLog || b + k == lastCdr);
                            chunks[k] = f.IsCdr ? CdrParser.ParseFile(f.Path, first + b + k, 0, 0, newest)
                                                : LineParser.ParseFile(f.Path, first + b + k, ry, rm, newest);
                        }
                        catch (Exception ex) { errs[k] = ex; }
                    });
                    lock (st.Sync)
                    {
                        for (int k = 0; k < n; k++)
                        {
                            if (errs[k] != null) { s.Warn(Path.GetFileName(files[b + k].Path) + ": " + errs[k].Message); continue; }
                            st.Ingest(s, chunks[k]);
                            s.FilesRead++;
                            if (b + k == lastLog) { s.TailPos = chunks[k].EndPos; s.TailLine = chunks[k].EndLine; }
                            if (b + k == lastCdr) { s.CdrTailPos = chunks[k].EndPos; s.CdrTailLine = chunks[k].EndLine; }
                        }
                        s.Status = "reading " + s.FilesRead + " / " + s.FilesFound + " files";
                    }
                    MemoryGuard.Breathe();
                    if (!o.Quiet && (b / threads) % 4 == 3)
                        Console.Error.Write("\r  " + s.Name + ": " + s.FilesRead + " / " + s.FilesFound + " files, " +
                                            s.LegsAdded.ToString("N0", CultureInfo.InvariantCulture) + " legs   ");
                }
                lock (st.Sync)
                {
                    st.Settle(s, false);
                    st.DeriveDirty();
                    s.LoadSeconds = sw.Elapsed.TotalSeconds;
                    s.Status = "read " + s.FilesRead + " files in " + s.LoadSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
                }
                if (!o.Quiet)
                    Console.Error.WriteLine("\r  " + s.Name + ": " + s.FilesRead + " files, " +
                                            s.LegsAdded.ToString("N0", CultureInfo.InvariantCulture) + " legs in " +
                                            s.LoadSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s          ");
            }
        }

        /// <summary>
        /// Parsing leaves a lot of short-lived garbage behind; collect it once and give the memory back,
        /// since this may run on the syslog server itself for days.
        /// </summary>
        public static void Compact() { MemoryGuard.Compact(); }

        /// <summary>A whole load as the CLI does it: find, plan, read, settle. A CUBE found under two names is read again as one.</summary>
        public static Store LoadAll(LoadOptions o, Rules rules)
        {
            if (o.Links == null) o.Links = new CubeLinks(null);
            Store st = LoadOnce(o, rules);
            if (!o.Links.Learn(st)) return st;
            st = null;
            Compact();
            return LoadOnce(o, rules);
        }

        static Store LoadOnce(LoadOptions o, Rules rules)
        {
            Store st = new Store();
            if (rules != null) st.Rules = rules;
            List<LogFile> files = Find(o.Root, o.Pattern);
            files.AddRange(FindCdr(o.Root, null));
            SortedDictionary<string, List<LogFile>> plan = Plan(files, o);
            Load(st, plan, o);
            lock (st.Sync)
            {
                for (int i = 0; i < st.Sources.Count; i++) st.Settle(st.Sources[i], !o.Live);
                st.DeriveDirty();
                st.ReleaseIntern();
            }
            Compact();
            return st;
        }
    }

    /// <summary>
    /// Deletes old hourly syslog files from the folder CubeScope reads. A file goes only when CubeScope would read it
    /// (--pattern and --cube), it has the hourly name, and both the hour in that name and its last change are past the
    /// cut. So the file being written, a log copied in today and anything named differently all stay, however old.
    /// Its .sha256 check file goes with it.
    /// </summary>
    public static class SyslogRetention
    {
        public const int MaxPerPass = 200000;

        public static int Run(LoadOptions feed, int keepDays, DateTime nowUtc, out long bytes, out int failed)
        {
            bytes = 0; failed = 0;
            if (keepDays < 1 || feed == null || string.IsNullOrEmpty(feed.Root) || !Directory.Exists(feed.Root)) return 0;
            DateTime cut = nowUtc.AddDays(-keepDays);
            int deleted = 0;
            Walk(new DirectoryInfo(feed.Root), feed, cut, 0, ref deleted, ref bytes, ref failed);
            return deleted;
        }

        // The hour in the name is on the receiver's clock, so it is a second check only: the last change sets the exact moment.
        static bool Expired(FileInfo f, LoadOptions feed, DateTime cut)
        {
            string source = Loader.HourlySource(f.Name);
            if (source == null || !Loader.Wanted(feed, source) || f.LastWriteTimeUtc >= cut) return false;
            try
            {
                long named = Loader.NameMsOf(f.Name);
                return named >= 0 && named + 3600000 < TimeUtil.Ms(cut.Year, cut.Month, cut.Day, cut.Hour, cut.Minute, cut.Second, 0);
            }
            catch (Exception) { return false; }      // a name that only looks like a date: leave it
        }

        static void Walk(DirectoryInfo dir, LoadOptions feed, DateTime cut, int depth, ref int deleted, ref long bytes, ref int failed)
        {
            try
            {
                foreach (FileInfo f in dir.EnumerateFiles(feed.Pattern))
                {
                    if (deleted >= MaxPerPass) return;
                    if (!Expired(f, feed, cut)) continue;
                    string check = Path.ChangeExtension(f.FullName, ".sha256");
                    try { long size = f.Length; f.Delete(); deleted++; bytes += size; }
                    catch (Exception) { failed++; continue; }
                    try
                    {
                        FileInfo c = new FileInfo(check);
                        if (c.Exists) { long size = c.Length; c.Delete(); bytes += size; }
                    }
                    catch (Exception) { failed++; }
                }
                if (depth >= 2) return;
                foreach (DirectoryInfo d in dir.EnumerateDirectories())
                {
                    if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue;      // a link can lead out of the log folder
                    Walk(d, feed, cut, depth + 1, ref deleted, ref bytes, ref failed);
                }
            }
            catch (Exception) { failed++; }
        }
    }

    /// <summary>
    /// Keeps the heap close to what is really in use. Reading leaves records behind that lived just
    /// long enough to reach the collector's oldest generation (a TWC record until its leg is bound,
    /// a call key until it is looked up), and left alone the collector let that pile up to more than
    /// twice the data before running (953 MB at the peak of a load that ends at 400 MB). On a server
    /// short of memory that peak is what pushes it into paging, so the loader collects as it goes.
    /// </summary>
    public static class MemoryGuard
    {
        static long _live = -1;
        public static int Collections;

        /// <summary>Collects when the heap has grown past what was live by half again (at least 128 MB).</summary>
        public static void Breathe()
        {
            long now = GC.GetTotalMemory(false);
            if (_live < 0) { _live = now; return; }
            if (now - _live < Math.Max(128L << 20, _live / 2)) return;
            GC.Collect(2, GCCollectionMode.Forced, true);
            Collections++;
            _live = GC.GetTotalMemory(false);
        }

        /// <summary>A full compacting collection, large objects included, at the end of a load.</summary>
        public static void Compact()
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, true);
            Collections++;
            _live = GC.GetTotalMemory(false);
        }

        /// <summary>Managed heap in use and the process's private bytes, in bytes.</summary>
        public static void Sample(out long managed, out long privateBytes, out long workingSet)
        {
            managed = GC.GetTotalMemory(false);
            using (Process p = Process.GetCurrentProcess())
            {
                privateBytes = p.PrivateMemorySize64;
                workingSet = p.WorkingSet64;
            }
        }

        public static string MB(long bytes) { return (bytes / 1048576.0).ToString("N0", CultureInfo.InvariantCulture) + " MB"; }
    }

    /// <summary>
    /// Follows the newest file of every source while the dashboard is up. Each poll reads what was
    /// appended since the last one; when the receiver starts the next hour's file the old one is read
    /// to its end first. Partial last lines wait for their line ending.
    /// </summary>
    public sealed class Tailer
    {
        readonly Store _st;
        readonly LoadOptions _o;
        Thread _thread;
        volatile bool _stop;
        public int IntervalMs = 2000;
        public string Status = "";
        public long Polls;

        sealed class Follow
        {
            public Source Src;
            public string Path;
            public long Pos;
            public int FileIndex;
            public int LineNo;
            public LineParser Parser;
            public string Partial = "";
        }
        readonly Dictionary<string, Follow> _follow = new Dictionary<string, Follow>(StringComparer.OrdinalIgnoreCase);

        /// <summary>A call record file already read: how far, and where its rows are counted.</summary>
        sealed class CdrRead
        {
            public Source Src;
            public int Index, Line;
            public long Pos;
        }
        readonly Dictionary<string, CdrRead> _cdr = new Dictionary<string, CdrRead>(StringComparer.OrdinalIgnoreCase);
        /// <summary>A new file this fresh may still be arriving.</summary>
        const double CdrSettleSeconds = 2;
        /// <summary>A CUBE on flash, or an FTP server that appends, grows its newest files instead of making new ones.</summary>
        const int CdrWatchNewest = 3;

        public Tailer(Store st, LoadOptions o) { _st = st; _o = o; }

        /// <summary>Called when two names turn out to be one CUBE while following.</summary>
        public Action NewLink;

        /// <summary>The files the load read: nothing to read again, except what the newest may still gain.</summary>
        void NoteCdrFiles(Source s)
        {
            int seen = 0;
            for (int i = s.Files.Count - 1; i >= 0; i--)
            {
                string p = s.Files[i];
                if (!Loader.IsCdrName(Path.GetFileName(p))) continue;
                CdrRead r = new CdrRead();
                r.Src = s; r.Index = i; r.Pos = long.MaxValue;
                if (seen < CdrWatchNewest)
                {
                    if (seen == 0 && s.CdrTailPos >= 0) { r.Pos = s.CdrTailPos; r.Line = s.CdrTailLine; }
                    else
                    {
                        try { r.Pos = new FileInfo(p).Length; } catch (Exception) { r.Pos = 0; }
                        r.Line = CountLines(p, r.Pos);
                    }
                }
                seen++;
                _cdr[p] = r;
            }
        }

        /// <summary>Reads call record files that are new, or that have grown. Public so the self-test can drive it.</summary>
        public void PollCdr(bool everyDay)
        {
            List<LogFile> all = Loader.FindCdr(_o.Root, everyDay ? (DateTime?)null : DateTime.UtcNow);
            if (all.Count == 0) return;
            SortedDictionary<string, List<LogFile>> plan = Loader.Plan(all, new LoadOptions { Cubes = _o.Cubes, Days = 0, Links = _o.Links });
            foreach (KeyValuePair<string, List<LogFile>> kv in plan)
            {
                Source src = null;
                List<LogFile> files = kv.Value;
                for (int i = 0; i < files.Count; i++)
                {
                    string p = files[i].Path;
                    CdrRead r;
                    bool known = _cdr.TryGetValue(p, out r);
                    if (known && i < files.Count - CdrWatchNewest) continue;
                    FileInfo fi = new FileInfo(p);
                    if (!fi.Exists) continue;
                    if (known && fi.Length <= r.Pos) continue;
                    if (!known)
                    {
                        if ((DateTime.UtcNow - fi.LastWriteTimeUtc).TotalSeconds < CdrSettleSeconds) continue;
                        if (src == null) src = SourceFor(kv.Key, Path.GetDirectoryName(p));
                        r = new CdrRead();
                        r.Src = src;
                        lock (_st.Sync) { src.Files.Add(p); r.Index = src.Files.Count - 1; src.FilesFound++; src.FilesRead++; }
                        _cdr[p] = r;
                    }
                    ParsedChunk c;
                    try { c = CdrParser.ParseFile(p, r.Index, r.Pos, r.Line, true); }
                    catch (Exception ex) { Status = "cannot read " + Path.GetFileName(p) + ": " + ex.Message; continue; }
                    r.Pos = c.EndPos; r.Line = c.EndLine;
                    lock (_st.Sync) _st.Ingest(r.Src, c);
                }
            }
        }

        Source SourceFor(string name, string folder)
        {
            lock (_st.Sync)
            {
                for (int i = 0; i < _st.Sources.Count; i++) if (string.Equals(_st.Sources[i].Name, name, StringComparison.OrdinalIgnoreCase)) return _st.Sources[i];
                return _st.AddSource(name, folder);
            }
        }

        public void Start()
        {
            // pick up where the load ended: the newest file of each source, from its current end
            lock (_st.Sync)
            {
                for (int i = 0; i < _st.Sources.Count; i++)
                {
                    Source s = _st.Sources[i];
                    if (s.Files.Count == 0) continue;
                    NoteCdrFiles(s);
                    int newest = s.Files.Count - 1;
                    while (newest >= 0 && Loader.IsCdrName(Path.GetFileName(s.Files[newest]))) newest--;
                    if (newest < 0) continue;                  // only call record files: PollCdr has them
                    string p = s.Files[newest];
                    Follow f = new Follow();
                    f.Src = s; f.Path = p; f.FileIndex = newest;
                    if (s.TailPos >= 0) { f.Pos = s.TailPos; f.LineNo = s.TailLine; }
                    else
                    {
                        try { f.Pos = new FileInfo(p).Length; } catch (Exception) { f.Pos = 0; }
                        f.LineNo = CountLines(p, f.Pos);
                    }
                    DateTime d = s.LastMs >= 0 ? TimeUtil.ToDate(s.LastMs) : DateTime.Now;
                    f.Parser = new LineParser(f.FileIndex, d.Year, d.Month);
                    f.Parser.Tz = s.Tz;
                    _follow[s.Name] = f;
                }
            }
            _thread = new Thread(Run);
            _thread.IsBackground = true;
            _thread.Name = "tail";
            _thread.Start();
        }

        public void Stop() { _stop = true; }

        static int CountLines(string path, long upTo)
        {
            int n = 0;
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16))
                {
                    byte[] buf = new byte[1 << 16];
                    long left = upTo;
                    int r;
                    while (left > 0 && (r = fs.Read(buf, 0, (int)Math.Min(buf.Length, left))) > 0)
                    {
                        for (int i = 0; i < r; i++) if (buf[i] == (byte)'\n') n++;
                        left -= r;
                    }
                }
            }
            catch (Exception) { }
            return n;
        }

        void Run()
        {
            while (!_stop)
            {
                // the load has just read everything, so the first poll waits a whole interval
                Thread.Sleep(IntervalMs);
                if (_stop) break;
                try { Poll(); }
                catch (Exception ex) { Status = "tail error: " + ex.Message; }
            }
        }

        /// <summary>One pass over every source. Public so the self-test can drive it without a thread.</summary>
        public void Poll()
        {
            Polls++;
            // every 10 s, not every 2: a file per CUBE every few seconds is plenty. A full listing every 5 minutes
            // finds a CUBE whose clock puts its file names outside today.
            if (Polls % 5 == 1)
            {
                try { PollCdr(Polls % 150 == 1); }
                catch (Exception ex) { Status = "cannot read the call record files: " + ex.Message; }
            }
            List<LogFile> all;
            // a folder of nothing but call record files need not be searched for syslog files every 2 seconds
            if (_cdr.Count > 0 && _follow.Count == 0 && Polls % 15 != 1) all = new List<LogFile>();
            else
            try { all = Loader.Find(_o.Root, _o.Pattern); }
            catch (Exception ex) { Status = "cannot list " + _o.Root + ": " + ex.Message; return; }
            SortedDictionary<string, List<LogFile>> plan = Loader.Plan(all, new LoadOptions { Cubes = _o.Cubes, Days = 0, Links = _o.Links });
            foreach (KeyValuePair<string, List<LogFile>> kv in plan)
            {
                List<LogFile> files = kv.Value;
                if (files.Count == 0) continue;
                Follow f;
                if (!_follow.TryGetValue(kv.Key, out f))
                {
                    // a CUBE that started logging after the load
                    Source s;
                    lock (_st.Sync) s = _st.AddSource(kv.Key, Path.GetDirectoryName(files[0].Path));
                    f = new Follow();
                    f.Src = s; f.Path = null;
                    _follow[kv.Key] = f;
                }
                // read the current file to its end, then move to any newer ones in order
                int at = -1;
                for (int i = 0; i < files.Count; i++)
                    if (f.Path != null && string.Equals(files[i].Path, f.Path, StringComparison.OrdinalIgnoreCase)) at = i;
                if (f.Path != null) ReadMore(f);
                for (int i = at + 1; i < files.Count; i++)
                {
                    if (f.Path == null && i < files.Count - 1 && at < 0)
                    {
                        // a brand-new source: begin with its newest file only
                        i = files.Count - 1;
                    }
                    Open(f, files[i].Path);
                    ReadMore(f);
                }
            }
            lock (_st.Sync)
            {
                for (int i = 0; i < _st.Sources.Count; i++) _st.Settle(_st.Sources[i], false);
                _st.DeriveDirty();
            }
            MemoryGuard.Breathe();
            // a feed turned on while running: the two names are one CUBE from now on, and the calls read so far need reading as one
            if (Polls % 150 == 2 && _o.Links != null && NewLink != null && _o.Links.Learn(_st)) NewLink();
            int followed = _follow.Count;
            foreach (CdrRead r in _cdr.Values) { if (!_follow.ContainsKey(r.Src.Name)) { followed++; break; } }
            Status = "following " + followed + " source" + (followed == 1 ? "" : "s") + ", last poll " +
                     DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }

        void Open(Follow f, string path)
        {
            if (f.Parser != null)
            {
                if (f.Partial.Length > 0) { f.Parser.Feed(f.Partial, ++f.LineNo); f.Partial = ""; }
                f.Parser.Flush();
                ParsedChunk rest = f.Parser.Take();
                lock (_st.Sync) _st.Ingest(f.Src, rest);
            }
            lock (_st.Sync)
            {
                f.Src.Files.Add(path);
                f.FileIndex = f.Src.Files.Count - 1;
                f.Src.FilesFound++;
                f.Src.FilesRead++;
            }
            LogFile d = Loader.Describe(path);
            DateTime when = d.NameMs >= 0 ? TimeUtil.ToDate(d.NameMs) : DateTime.Now;
            string tz = f.Parser != null ? f.Parser.Tz : f.Src.Tz;
            f.Parser = new LineParser(f.FileIndex, when.Year, when.Month);
            f.Parser.Tz = tz;
            f.Path = path; f.Pos = 0; f.LineNo = 0; f.Partial = "";
        }

        /// <summary>
        /// Reads everything appended up to the file's length now, in pieces of at most 64 MiB so a
        /// backlog never needs one huge buffer. It always drains to that length: Poll moves on to a
        /// newer file right after, and an older file is never read again, so stopping at the first
        /// piece would lose the rest of it for good.
        /// </summary>
        void ReadMore(Follow f)
        {
            long len;
            try { len = new FileInfo(f.Path).Length; }
            catch (Exception) { return; }
            if (len < f.Pos) { f.Pos = 0; f.LineNo = 0; f.Partial = ""; }      // truncated or replaced
            while (f.Pos < len)
            {
                if (!ReadPiece(f, len)) break;
            }
        }

        /// <summary>Largest single read; a field so the self-test can force many pieces.</summary>
        public static long PieceBytes = 64L << 20;

        bool ReadPiece(Follow f, long len)
        {
            byte[] buf;
            using (FileStream fs = new FileStream(f.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                fs.Seek(f.Pos, SeekOrigin.Begin);
                long want = Math.Min(len - f.Pos, PieceBytes);
                buf = new byte[want];
                int got = 0;
                while (got < want)
                {
                    int r = fs.Read(buf, got, (int)(want - got));
                    if (r <= 0) break;
                    got += r;
                }
                if (got < want) Array.Resize(ref buf, got);
            }
            if (buf.Length == 0) return false;
            f.Pos += buf.Length;
            string text = f.Partial + LineParser.Latin1.GetString(buf);
            int start = 0;
            while (true)
            {
                int nl = text.IndexOf('\n', start);
                if (nl < 0) break;
                int end = nl > start && text[nl - 1] == '\r' ? nl - 1 : nl;
                f.Parser.Feed(text.Substring(start, end - start), ++f.LineNo);
                start = nl + 1;
            }
            f.Partial = text.Substring(start);
            ParsedChunk c = f.Parser.Take();
            if (c.Records.Count > 0 || c.Lines > 0) lock (_st.Sync) _st.Ingest(f.Src, c);
            return true;
        }
    }
}
