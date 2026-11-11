using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CubeScope
{
    /// <summary>
    /// Which names are the same CUBE. A syslog file is named by the address the receiver saw and a call record file by the
    /// CUBE's host name, and nothing in either says the other name, so they are matched by what they hold: the same leg
    /// reported in both, to the millisecond and the packet. A CUBE further along the same call has the same GUID but never
    /// those numbers. What is found is kept, so a restart reads them as one straight away and the link survives a stretch
    /// when the two feeds do not overlap.
    /// </summary>
    public sealed class CubeLinks
    {
        /// <summary>A single match could be chance; this many cannot.</summary>
        public const int MinSharedLegs = 3;

        readonly object _gate = new object();
        readonly Dictionary<string, string> _nameOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly string _file;

        /// <param name="file">Where the links are kept; null keeps them for this run only.</param>
        public CubeLinks(string file)
        {
            _file = file;
            if (file == null || !File.Exists(file)) return;
            try
            {
                Dictionary<string, object> o = JsonRead.Parse(File.ReadAllText(file, Encoding.UTF8)) as Dictionary<string, object>;
                if (o != null) foreach (KeyValuePair<string, object> kv in o) _nameOf[kv.Key] = Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) { Log.Error("cannot read " + file, ex); }
        }

        /// <summary>The name this source is shown under: the host name when it is known to be the same CUBE, otherwise itself.</summary>
        public string Resolve(string source)
        {
            lock (_gate)
            {
                string name;
                return _nameOf.TryGetValue(source, out name) ? name : source;
            }
        }

        public List<string> OtherNames(string name)
        {
            List<string> r = new List<string>();
            lock (_gate)
                foreach (KeyValuePair<string, string> kv in _nameOf)
                    if (string.Equals(kv.Value, name, StringComparison.OrdinalIgnoreCase)) r.Add(kv.Key);
            return r;
        }

        static int SharedLegs(Call a, Call b)
        {
            int n = 0;
            for (int i = 0; i < a.Legs.Count; i++)
                for (int k = 0; k < b.Legs.Count; k++)
                    if (!a.Legs[i].FeatureOnly && a.Legs[i].SameAs(b.Legs[k])) { n++; break; }
            return n;
        }

        /// <summary>Looks for a syslog feed and a call record feed that are one CUBE. True when it found a new pair.</summary>
        public bool Learn(Store st)
        {
            List<Source> logs = new List<Source>(), files = new List<Source>();
            int[,] shared;
            lock (st.Sync)
            {
                Dictionary<int, int> fileAt = new Dictionary<int, int>();
                for (int i = 0; i < st.Sources.Count; i++)
                {
                    Source s = st.Sources[i];
                    if (s.LegsFromFiles > 0 && s.LegsFromSyslog == 0) { fileAt[s.Index] = files.Count; files.Add(s); }
                    else if (s.LegsFromSyslog > 0 && s.LegsFromFiles == 0) logs.Add(s);
                }
                if (logs.Count == 0 || files.Count == 0) return false;
                shared = new int[logs.Count, files.Count];
                for (int c = 0; c < st.Calls.Count; c++)
                {
                    Call call = st.Calls[c];
                    int f;
                    if (!fileAt.TryGetValue(call.Src, out f)) continue;
                    for (int l = 0; l < logs.Count; l++)
                    {
                        Call other;
                        if (shared[l, f] < MinSharedLegs && logs[l].ByKey.TryGetValue(call.Key, out other)) shared[l, f] += SharedLegs(call, other);
                    }
                }
            }
            bool changed = false;
            lock (_gate)
            {
                for (int l = 0; l < logs.Count; l++)
                {
                    int match = -1, matches = 0;
                    for (int f = 0; f < files.Count; f++) if (shared[l, f] >= MinSharedLegs) { match = f; matches++; }
                    if (matches == 1)
                    {
                        _nameOf[logs[l].Name] = files[match].Name;
                        changed = true;
                        Log.Info("syslog from " + logs[l].Name + " and the call record files from " + files[match].Name + " hold the same calls: treated as one CUBE from now on");
                    }
                    else if (matches > 1 && _warned.Add(logs[l].Name))
                        Log.Warn("syslog from " + logs[l].Name + " holds calls from " + matches + " CUBEs' call record files, so it is not joined to any of them");
                }
                if (changed && _file != null) Save();
            }
            return changed;
        }

        void Save()
        {
            Json j = new Json(1024);
            j.Obj();
            foreach (KeyValuePair<string, string> kv in _nameOf) j.P(kv.Key, kv.Value);
            j.EndObj();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file));
                AlarmEngine.WriteAtomically(_file, j.ToString());
            }
            catch (Exception ex) { Log.Error("cannot write " + _file, ex); }
        }
    }
}
