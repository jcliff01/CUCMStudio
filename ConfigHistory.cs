using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CubeScope
{
    /// <summary>
    /// The newest configuration changes (SYS-5-CONFIG_I, PARSER-*) of each CUBE, so the page can always
    /// list the last ten whatever range it shows. The service keeps only 24 hours of records, so it keeps
    /// these in config-history.json in its data folder too: they outlive the window and a restart.
    /// Without a file it is filled from the store alone (an archive has every record anyway).
    /// </summary>
    public sealed class ConfigHistory
    {
        /// <summary>Changes kept per CUBE; the page shows the newest ten of all, or of one CUBE.</summary>
        public const int PerSource = 10;

        public sealed class Entry
        {
            public string Src = "", Mnemonic = "", Text = "";
            public long Ts;          // the CUBE's own clock
            public long Utc = -1;    // when its clock could be converted
            public long Order { get { return Utc >= 0 ? Utc : Ts; } }
        }

        public readonly string FilePath;     // null: in memory only
        readonly object _lock = new object(), _saveLock = new object();
        readonly List<Entry> _items = new List<Entry>();
        readonly HashSet<string> _keys = new HashSet<string>(StringComparer.Ordinal);
        // events already looked at, per source of the store looked at last (a swapped-in store starts over)
        readonly Dictionary<Source, int> _scanned = new Dictionary<Source, int>();
        Store _scannedStore;

        public ConfigHistory(string filePath) { FilePath = filePath; }

        public static ConfigHistory FromStore(Store st)
        {
            ConfigHistory h = new ConfigHistory(null);
            h.Merge(st);
            return h;
        }

        static string Key(Entry e) { return e.Src + "|" + e.Ts + "|" + e.Mnemonic + "|" + e.Text; }

        /// <summary>Adds the store's configuration changes not seen yet; saves the file when one is kept.</summary>
        public void Merge(Store st)
        {
            bool changed = false;
            lock (st.Sync)
                lock (_lock)
                {
                    if (_scannedStore != st) { _scanned.Clear(); _scannedStore = st; }
                    List<Entry> fresh = new List<Entry>();
                    ZoneMap utc = null;
                    for (int i = 0; i < st.Sources.Count; i++)
                    {
                        Source s = st.Sources[i];
                        int from;
                        _scanned.TryGetValue(s, out from);
                        List<SysEvent> evs = s.Events;
                        for (int k = from; k < evs.Count; k++)
                        {
                            SysEvent e = evs[k];
                            if (e.Kind != "config") continue;
                            Entry x = new Entry();
                            x.Src = s.Name ?? ""; x.Ts = e.Ts; x.Mnemonic = e.Mnemonic ?? ""; x.Text = e.Text ?? "";
                            if (_keys.Contains(Key(x))) continue;
                            if (utc == null) utc = ZoneMap.Build(st, "UTC");
                            if (!utc.NotConverted.Contains(s.Name)) x.Utc = utc.ToDisplay(i, e.Ts);
                            fresh.Add(x);
                        }
                        _scanned[s] = evs.Count;
                    }
                    if (fresh.Count == 0) return;
                    _items.AddRange(fresh);
                    Trim();
                    for (int i = 0; i < fresh.Count && !changed; i++) changed = _keys.Contains(Key(fresh[i]));
                }
            if (changed) Save();
        }

        /// <summary>Keeps the newest PerSource of each CUBE, newest first. Under _lock.</summary>
        void Trim()
        {
            _items.Sort(delegate (Entry a, Entry b) { return b.Order.CompareTo(a.Order); });
            Dictionary<string, int> per = new Dictionary<string, int>(StringComparer.Ordinal);
            List<Entry> keep = new List<Entry>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < _items.Count; i++)
            {
                Entry e = _items[i];
                int n;
                per.TryGetValue(e.Src, out n);
                if (n >= PerSource || !seen.Add(Key(e))) continue;
                per[e.Src] = n + 1;
                keep.Add(e);
            }
            _items.Clear(); _items.AddRange(keep);
            _keys.Clear();
            for (int i = 0; i < _items.Count; i++) _keys.Add(Key(_items[i]));
        }

        /// <summary>The newest n, of one CUBE (by name) or of all (null), newest first.</summary>
        public List<Entry> Newest(int n, string src)
        {
            List<Entry> r = new List<Entry>();
            lock (_lock)
                for (int i = 0; i < _items.Count && r.Count < n; i++)
                    if (src == null || _items[i].Src == src) r.Add(_items[i]);
            return r;
        }

        public void Load()
        {
            if (FilePath == null || !File.Exists(FilePath)) return;
            try
            {
                List<object> arr = JsonRead.Parse(File.ReadAllText(FilePath, Encoding.UTF8)) as List<object>;
                if (arr == null) { Log.Warn(FilePath + " is not a list; starting with no saved configuration changes"); return; }
                lock (_lock)
                {
                    foreach (object o in arr)
                    {
                        Dictionary<string, object> d = o as Dictionary<string, object>;
                        if (d == null) continue;
                        Entry e = new Entry();
                        e.Src = JsonRead.GetString(d, "src", "");
                        e.Ts = (long)JsonRead.GetNumber(d, "ts", -1);
                        e.Utc = (long)JsonRead.GetNumber(d, "utc", -1);
                        e.Mnemonic = JsonRead.GetString(d, "m", "");
                        e.Text = JsonRead.GetString(d, "x", "");
                        if (e.Src.Length > 0 && e.Ts >= 0) _items.Add(e);
                    }
                    Trim();
                }
            }
            catch (Exception ex) { Log.Error("cannot read " + FilePath + "; starting with no saved configuration changes", ex); }
        }

        void Save()
        {
            if (FilePath == null) return;
            Json j = new Json(4096);
            j.Arr();
            lock (_lock)
                foreach (Entry e in _items)
                    j.Obj().P("src", e.Src).P("ts", e.Ts).P("utc", e.Utc).P("m", e.Mnemonic).P("x", e.Text).EndObj();
            j.EndArr();
            lock (_saveLock)
            {
                try { AlarmEngine.WriteAtomically(FilePath, j.ToString()); }
                catch (Exception ex) { Log.Error("cannot write " + FilePath, ex); }
            }
        }
    }
}
