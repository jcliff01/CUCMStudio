using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;

namespace CubeScope
{
    /// <summary>
    /// Serves the dashboard and its JSON. Every answer is computed from the store under its lock;
    /// the last query result is cached by its parameters and the store version, so the dashboard's
    /// state, call list and CSV for the same view cost one pass over the calls.
    /// </summary>
    public sealed class WebServer
    {
        // the store being served; the service swaps in a freshly read one every few hours (SwapStore),
        // so every request reads it once into a local named _st and uses only that
        volatile Store _cur;
        readonly int _port;
        HttpListener _listener;
        Thread _thread;
        volatile bool _stopping;
        public string DashboardOverride;
        public Func<string> Status;
        /// <summary>True when a Tailer feeds new records in (not --no-live): the page goes Live by default.</summary>
        public bool Following;
        /// <summary>The clock "now" is read from (the self-test sets it).</summary>
        public Func<DateTime> UtcNow = delegate () { return DateTime.UtcNow; };
        /// <summary>Kept configuration changes (the host's); null takes them from the store each time.</summary>
        public ConfigHistory Configs;
        public bool LocalOnly;
        /// <summary>Bind to this computer only and refuse requests from any other (the service).</summary>
        public bool ForceLocal;
        /// <summary>The alarm engine when this CubeScope runs alarms (the service, or --alarms).</summary>
        public AlarmEngine Alarms;
        /// <summary>Only in a window opened as administrator (--admin): the Admin page's work.</summary>
        public ReceiverAdmin Admin;
        /// <summary>How this CubeScope was started, for the Settings window; null outside a Host.</summary>
        public HostOptions Options;

        readonly object _cacheLock = new object();
        string _cacheKey;
        QueryResult _cache;
        Store _cacheStore;
        string _sortedKey;
        List<Call> _sorted;
        QueryResult _sortedFrom;

        public WebServer(Store st, int port) { _cur = st; _port = port; }

        public Store CurrentStore { get { return _cur; } }

        /// <summary>Serves a new store from the next request on; cached answers of the old one are dropped.</summary>
        public void SwapStore(Store st)
        {
            lock (_cacheLock)
            {
                _cur = st;
                _cache = null; _cacheKey = null; _cacheStore = null;
                _sorted = null; _sortedKey = null; _sortedFrom = null;
            }
        }

        /// <summary>Remote access: HTTPS on this port for other computers, with a Windows sign-in; 0 = off.</summary>
        public int HttpsPort;
        /// <summary>The local group whose members may use the HTTPS side (this server's administrators always may).</summary>
        public string AllowGroup = RemoteSettings.DefaultGroup;
        /// <summary>Self-test only: ask for a Windows sign-in on plain HTTP too, so the sign-in path runs without a certificate.</summary>
        public bool SignInOverHttpForTest;

        /// <summary>A Windows sign-in for HTTPS (and the self-test's HTTP); none for this computer's own plain HTTP.</summary>
        AuthenticationSchemes SchemeFor(HttpListenerRequest r)
        {
            return r.IsSecureConnection || SignInOverHttpForTest ? AuthenticationSchemes.IntegratedWindowsAuthentication : AuthenticationSchemes.Anonymous;
        }

        public void Start()
        {
            if (HttpsPort > 0 || SignInOverHttpForTest)
            {
                // other computers over HTTPS with a sign-in; plain HTTP stays this computer's own
                try
                {
                    _listener = new HttpListener();
                    if (HttpsPort > 0) _listener.Prefixes.Add("https://+:" + HttpsPort + "/");
                    _listener.Prefixes.Add("http://localhost:" + _port + "/");
                    _listener.Prefixes.Add("http://127.0.0.1:" + _port + "/");
                    _listener.AuthenticationSchemeSelectorDelegate = SchemeFor;
                    _listener.UnsafeConnectionNtlmAuthentication = true;    // one sign-in per connection, not per request
                    _listener.Start();
                    LocalOnly = true;
                    if (HttpsPort > 0) Log.Info("remote access: HTTPS on port " + HttpsPort + " for members of \"" + AllowGroup + "\" and this server's administrators");
                    StartLoop();
                    return;
                }
                catch (HttpListenerException ex)
                {
                    Log.Error("remote access: HTTPS on port " + HttpsPort + " did not start (" + ex.Message + "); answering this computer only. " +
                              "Set it up again on the Admin page, which binds the certificate to the port.", ex);
                    HttpsPort = 0;
                    ForceLocal = true;
                }
            }
            _listener = new HttpListener();
            _listener.Prefixes.Add("http://+:" + _port + "/");
            try
            {
                if (ForceLocal) throw new HttpListenerException(5, "local only");
                _listener.Start();
            }
            catch (HttpListenerException)
            {
                // No URL ACL for "+"; fall back to loopback, which never needs one.
                _listener = new HttpListener();
                _listener.Prefixes.Add("http://localhost:" + _port + "/");
                _listener.Prefixes.Add("http://127.0.0.1:" + _port + "/");
                _listener.Start();
                LocalOnly = true;
                if (!ForceLocal)
                {
                    Console.WriteLine("  note: bound to localhost only. To allow viewing from other PCs, run once as admin:");
                    Console.WriteLine("        netsh http add urlacl url=http://+:" + _port + "/ user=" +
                                      Environment.UserDomainName + "\\" + Environment.UserName);
                }
            }
            StartLoop();
        }

        void StartLoop()
        {
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "http";
            _thread.Start();
        }

        public void Stop()
        {
            _stopping = true;
            try { if (_listener != null) _listener.Stop(); }
            catch (ObjectDisposedException) { }
        }

        void Loop()
        {
            while (!_stopping)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch (Exception) { if (_stopping) return; continue; }
                ThreadPool.QueueUserWorkItem(delegate (object o) { Handle((HttpListenerContext)o); }, ctx);
            }
        }

        /// <summary>Runs a query, or returns the cached result when nothing it depends on changed.</summary>
        public QueryResult Get(QueryParams q) { return Get(_cur, q); }

        QueryResult Get(Store _st, QueryParams q)
        {
            lock (_cacheLock)
            {
                lock (_st.Sync)
                {
                    _st.DeriveDirty();
                    string key = q.CacheKey() + "|" + _st.Version;
                    if (_cache != null && key == _cacheKey && ReferenceEquals(_cacheStore, _st)) return _cache;
                    QueryResult r = Query.Run(_st, q);
                    _cache = r;
                    _cacheStore = _st;
                    _cacheKey = q.CacheKey() + "|" + _st.Version;
                    MemoryGuard.Breathe();      // a whole-archive query leaves tens of megabytes behind
                    return r;
                }
            }
        }

        void Handle(HttpListenerContext ctx)
        {
            Store _st = _cur;
            try
            {
                if (ctx.Request.IsSecureConnection || SignInOverHttpForTest)
                {
                    // HTTPS: only a signed-in member of the group, or an administrator of this server
                    string why = RemoteAccess.Refusal(ctx.User, AllowGroup);
                    if (why != null) { Send(ctx, 403, "text/plain; charset=utf-8", why); return; }
                    RemoteAccess.NoteVisit(ctx.User, ctx.Request.RemoteEndPoint, DateTime.UtcNow);
                }
                else if ((ForceLocal || LocalOnly) && !ctx.Request.IsLocal)
                {
                    // plain HTTP answers this computer only: a request that reached the port from another
                    // one (a Host: localhost header can get past the binding) is refused
                    Send(ctx, 403, "text/plain; charset=utf-8", HttpsPort > 0
                        ? "CubeScope answers other computers over HTTPS: https://" + Environment.MachineName + ":" + HttpsPort + "/"
                        : "CubeScope answers this computer only.");
                    return;
                }
                string path = ctx.Request.Url.AbsolutePath;
                if (path.StartsWith("/api/admin", StringComparison.Ordinal))
                {
                    HandleAdmin(ctx, path);
                    return;
                }
                if (path == "/api/settings")
                {
                    HandleSettings(ctx, _st);
                    return;
                }
                if (path.StartsWith("/api/alarms", StringComparison.Ordinal) || path == "/api/log")
                {
                    HandleAlarms(ctx, path, _st);
                    return;
                }
                if (path == "/" || path == "/index.html")
                {
                    Send(ctx, 200, "text/html; charset=utf-8", DashboardHtml());
                    return;
                }
                if (path == "/api/health") { Send(ctx, 200, "text/plain; charset=utf-8", "ok"); return; }
                if (path == "/api/rules")
                {
                    Send(ctx, 200, "text/plain; charset=utf-8", _st.Rules.Text ?? "");
                    return;
                }
                if (path == "/api/call")
                {
                    int src = QueryInt(ctx, "src", 0);
                    int id = QueryInt(ctx, "id", -1);
                    string key = ctx.Request.QueryString["key"] ?? "";
                    string json = null;
                    lock (_st.Sync)
                    {
                        // id names one call; a GUID can name several (reused hours apart), so the key
                        // is only a fallback for links made by hand
                        Call c = null;
                        if (id >= 0) c = FindCall(_st, id);
                        else if (src >= 0 && src < _st.Sources.Count) c = FindCall(_st.Sources[src], key);
                        if (c != null)
                        {
                            _st.DeriveDirty();
                            string zid = ctx.Request.QueryString["zone"];
                            ZoneMap zm = ZoneMap.Valid(zid) ? ZoneMap.Build(_st, zid) : null;
                            json = Query.CallJson(_st, c, QueryInt(ctx, "raw", 1) == 1, zm);
                        }
                    }
                    if (json == null) { Send(ctx, 404, "text/plain; charset=utf-8", "no such call"); return; }
                    Send(ctx, 200, "application/json; charset=utf-8", json);
                    return;
                }

                if (path == "/api/events")
                {
                    long from = ParseTime(ctx.Request.QueryString["from"]), to = ParseTime(ctx.Request.QueryString["to"]);
                    string kind = ctx.Request.QueryString["kind"] ?? "";
                    int src = QueryInt(ctx, "src", -1);
                    Send(ctx, 200, "application/json; charset=utf-8", EventsJson(_st, from, to, kind, src, 300, ctx.Request.QueryString["zone"]));
                    return;
                }

                QueryParams q;
                string err = ParseParams(ctx.Request.QueryString, out q, _st);
                if (err != null) { Send(ctx, 400, "text/plain; charset=utf-8", err); return; }

                if (path == "/api/state")
                {
                    QueryResult r = Get(_st, q);
                    string json;
                    ConfigHistory configs = Configs;
                    if (configs != null) configs.Merge(_st);
                    lock (_st.Sync) json = Query.StateJson(_st, q, r, Status != null ? Status() : "", Following, configs);
                    Send(ctx, 200, "application/json; charset=utf-8", json);
                    return;
                }
                if (path == "/api/calls")
                {
                    QueryResult r = Get(_st, q);
                    string sort = ctx.Request.QueryString["sort"] ?? "start";
                    bool desc = (ctx.Request.QueryString["desc"] ?? "1") != "0";
                    int page = QueryInt(ctx, "page", 0), size = Math.Max(10, Math.Min(1000, QueryInt(ctx, "size", 200)));
                    string json;
                    lock (_cacheLock)
                    {
                        // paging through a long list must not re-sort it for every page
                        string skey = (_cacheKey ?? "") + "|" + sort + "|" + desc;
                        if (_sortedKey != skey || _sorted == null || !ReferenceEquals(_sortedFrom, r))
                        {
                            lock (_st.Sync) _sorted = Query.Sorted(r.Matched, sort, desc, r.Zone);
                            _sortedKey = skey; _sortedFrom = r;
                        }
                        lock (_st.Sync) json = Query.CallsJson(_st, _sorted, sort, desc, page, size, r.Zone);
                    }
                    Send(ctx, 200, "application/json; charset=utf-8", json);
                    return;
                }
                if (path == "/csv")
                {
                    QueryResult r = Get(_st, q);
                    StringWriter w = new StringWriter(CultureInfo.InvariantCulture);
                    lock (_st.Sync) Report.Csv(_st, Query.Sorted(r.Matched, "start", false, r.Zone), w, r.Zone);
                    ctx.Response.AddHeader("Content-Disposition", "attachment; filename=\"" + FileStem(r) + ".csv\"");
                    Send(ctx, 200, "text/csv; charset=utf-8", w.ToString());
                    return;
                }
                if (path == "/report")
                {
                    QueryResult r = Get(_st, q);
                    string html = Report.StaticHtml(_st, q, r, "CubeScope report " + FileStem(r));
                    ctx.Response.AddHeader("Content-Disposition", "attachment; filename=\"" + FileStem(r) + ".html\"");
                    Send(ctx, 200, "text/html; charset=utf-8", html);
                    return;
                }
                Send(ctx, 404, "text/plain; charset=utf-8", "not found");
            }
            catch (Exception ex)
            {
                try { Send(ctx, 500, "text/plain; charset=utf-8", ex.GetType().Name + ": " + ex.Message); }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// The alarm settings window: GET /api/alarms (settings, last check, CUBE names), POST /api/alarms
        /// (save), POST /api/alarms/test (the test button, with the window's settings, saved or not), and
        /// GET /api/log (the end of the troubleshooting log). A POST must carry the header X-CubeScope: 1,
        /// which a page from another site cannot add, so a web page cannot change the alarms behind the
        /// user's back.
        /// </summary>
        void HandleAlarms(HttpListenerContext ctx, string path, Store st)
        {
            if (path == "/api/log")
            {
                Send(ctx, 200, "text/plain; charset=utf-8", Log.FilePath == null ? "(this CubeScope keeps no log file)" : Log.FilePath + "\n\n" + Log.Tail(400));
                return;
            }
            bool post = ctx.Request.HttpMethod == "POST";
            if (post && !WriteOk(ctx.Request))
            {
                Send(ctx, 403, "text/plain; charset=utf-8", "refused: a change needs the X-CubeScope header and this page's own origin");
                return;
            }
            AlarmEngine eng = Alarms;
            if (eng == null)
            {
                Json n = new Json(256);
                n.Obj().P("available", false).P("why", "This CubeScope does not run alarms. They run in the CubeScope service (CubeScope.exe --install-service), or with --alarms.").EndObj();
                Send(ctx, post ? 409 : 200, "application/json; charset=utf-8", n.ToString());
                return;
            }
            AlarmConfig posted = null;
            if (post)
            {
                string body;
                using (StreamReader r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                {
                    char[] buf = new char[256 * 1024 + 1];
                    int len = r.ReadBlock(buf, 0, buf.Length);
                    if (len > 256 * 1024) { Send(ctx, 413, "text/plain; charset=utf-8", "settings too large"); return; }
                    body = new string(buf, 0, len);
                }
                string error;
                Dictionary<string, object> raw = null;
                try { raw = JsonRead.Parse(body) as Dictionary<string, object>; posted = AlarmConfig.FromJson(raw, out error); }
                catch (FormatException fe) { error = "the settings are not readable: " + fe.Message; }
                if (posted == null)
                {
                    Json e = new Json(512); e.Obj().P("ok", false).P("error", error).EndObj();
                    Send(ctx, 400, "application/json; charset=utf-8", e.ToString());
                    return;
                }
                // the file clean-up lives in the Settings window: an alarm save that does not name it keeps it
                KeepRetention(raw, posted, eng.Config);
            }
            string text = null;
            if (post && path == "/api/alarms/test") text = eng.Test(posted, 10);
            else if (post && path == "/api/alarms") { eng.Save(posted); text = "Saved."; }
            else if (post) { Send(ctx, 404, "text/plain; charset=utf-8", "not found"); return; }

            Json j = new Json(8192);
            j.Obj().P("available", true).P("ok", true).P("text", text ?? "");
            j.Key("config"); eng.Config.WriteJson(j);
            j.Key("status"); eng.WriteStatusJson(j);
            j.Key("sources").Arr();
            if (st != null) lock (st.Sync) for (int i = 0; i < st.Sources.Count; i++) j.Val(st.Sources[i].Name);
            j.EndArr();
            j.P("dataDir", eng.Dir).P("logFile", Log.FilePath ?? "").P("computer", Environment.MachineName);
            j.EndObj();
            Send(ctx, 200, "application/json; charset=utf-8", j.ToString());
        }

        /// <summary>The retention a save leaves out stays as it was.</summary>
        public static void KeepRetention(Dictionary<string, object> raw, AlarmConfig posted, AlarmConfig current)
        {
            if (raw == null || posted == null || current == null) return;
            if (!raw.ContainsKey("keepDays")) posted.KeepDays = current.KeepDays;
            if (!raw.ContainsKey("keepSyslogDays")) posted.KeepSyslogDays = current.KeepSyslogDays;
        }

        /// <summary>A copy of the saved settings with only the clean-up changed to what was posted; null with the reason.</summary>
        public static AlarmConfig WithRetention(AlarmConfig current, Dictionary<string, object> o, out string error)
        {
            error = null;
            if (o == null) { error = "the settings are not readable"; return null; }
            AlarmConfig next = AlarmConfig.FromJson(JsonRead.Parse(current.ToJsonText()) as Dictionary<string, object>, out error);
            if (next == null) return null;
            double kd = JsonRead.GetNumber(o, "keepDays", next.KeepDays), ks = JsonRead.GetNumber(o, "keepSyslogDays", next.KeepSyslogDays);
            if (kd != Math.Floor(kd) || kd < 0 || kd > 3650) { error = "keep call record files for 0 (keep them all) to 3650 days"; return null; }
            if (ks != Math.Floor(ks) || ks < 0 || ks > 3650) { error = "keep syslog files for 0 (keep them all) to 3650 days"; return null; }
            next.KeepDays = (int)kd; next.KeepSyslogDays = (int)ks;
            return next;
        }

        /// <summary>
        /// The Settings window: how long old files are kept (changeable where the alarm engine runs, as it does the
        /// clean-up), and how this CubeScope was started (read only). POST takes keepDays and keepSyslogDays only.
        /// </summary>
        void HandleSettings(HttpListenerContext ctx, Store st)
        {
            bool post = ctx.Request.HttpMethod == "POST";
            if (post && !WriteOk(ctx.Request))
            {
                Send(ctx, 403, "text/plain; charset=utf-8", "refused: a change needs the X-CubeScope header and this page's own origin");
                return;
            }
            AlarmEngine eng = Alarms;
            string text = "";
            if (post)
            {
                if (eng == null)
                {
                    Json n = new Json(256);
                    n.Obj().P("ok", false).P("error", "This CubeScope does not clean up files: the CubeScope service does, or CubeScope started with --alarms.").EndObj();
                    Send(ctx, 409, "application/json; charset=utf-8", n.ToString());
                    return;
                }
                string body;
                using (StreamReader r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                {
                    char[] buf = new char[16 * 1024 + 1];
                    int len = r.ReadBlock(buf, 0, buf.Length);
                    if (len > 16 * 1024) { Send(ctx, 413, "text/plain; charset=utf-8", "settings too large"); return; }
                    body = new string(buf, 0, len);
                }
                string error = null;
                AlarmConfig next = null;
                try { next = WithRetention(eng.Config, JsonRead.Parse(body) as Dictionary<string, object>, out error); }
                catch (FormatException fe) { error = "the settings are not readable: " + fe.Message; }
                if (error != null || next == null)
                {
                    Json e = new Json(512); e.Obj().P("ok", false).P("error", error ?? "the settings are not readable").EndObj();
                    Send(ctx, 400, "application/json; charset=utf-8", e.ToString());
                    return;
                }
                eng.Save(next);
                text = "Saved.";
            }
            Json j = new Json(4096);
            j.Obj().P("ok", true).P("text", text).P("version", Program.Version).P("computer", Environment.MachineName)
             .P("service", !Environment.UserInteractive).P("logFile", Log.FilePath ?? "").P("cleans", eng != null)
             .P("httpsPort", HttpsPort).P("allowGroup", AllowGroup ?? "")
             .P("user", ctx.User != null && ctx.User.Identity != null && ctx.User.Identity.IsAuthenticated ? ctx.User.Identity.Name : "");
            if (eng != null)
            {
                AlarmConfig c = eng.Config;
                j.P("keepDays", c.KeepDays).P("keepSyslogDays", c.KeepSyslogDays).P("cleanup", eng.CleanupNote)
                 .P("alarmsOn", c.Enabled).P("rules", c.Rules.Count).P("staleMinutes", c.StaleMinutes).P("dataDir", eng.Dir);
            }
            HostOptions h = Options;
            if (h != null)
            {
                j.Key("host").Obj().P("logs", h.Load.Root ?? "").P("pattern", h.Load.Pattern ?? "").P("hours", h.Load.Hours).P("days", h.Load.Days)
                 .P("port", h.Port).P("localOnly", h.LocalOnly).P("live", h.Live).P("serverZone", h.ServerZone ?? "").P("dataDir", h.DataDir ?? "")
                 .P("alarms", h.Alarms).EndObj();
            }
            j.Key("cubes").Arr();
            if (st != null)
                lock (st.Sync)
                    for (int i = 0; i < st.Sources.Count; i++)
                    {
                        Source s = st.Sources[i];
                        j.Obj().P("name", s.Name).P("files", s.Files.Count).P("syslogLegs", s.LegsFromSyslog).P("fileLegs", s.LegsFromFiles)
                         .P("also", string.Join(", ", s.AlsoNames.ToArray())).EndObj();
                    }
            j.EndArr();
            j.EndObj();
            Send(ctx, 200, "application/json; charset=utf-8", j.ToString());
        }

        public static bool AdminHostAllowed(string host)
        {
            return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1";
        }

        public static bool AdminTokenOk(ReceiverAdmin admin, string token)
        {
            return admin != null && !string.IsNullOrEmpty(token) && string.Equals(token, admin.Token, StringComparison.Ordinal);
        }

        /// <summary>
        /// The Admin page. Reached from this computer only, under its own name, so a web page elsewhere cannot
        /// use it through the browser. Anything that shows or changes the server also needs the key the
        /// administrator window put in its link: the service answers everyone logged on here and never has one.
        /// </summary>
        void HandleAdmin(HttpListenerContext ctx, string path)
        {
            if (!ctx.Request.IsLocal || !AdminHostAllowed(ctx.Request.Url.Host))
            {
                string where = "The Admin page works on the server itself only: open http://localhost:" + _port + "/ there.";
                if (ctx.Request.HttpMethod == "GET" && path == "/api/admin/state")
                {
                    Json lj = new Json(256);
                    lj.Obj().P("mode", "locked").P("canElevate", false).P("why", where).EndObj();
                    Send(ctx, 200, "application/json; charset=utf-8", lj.ToString());
                    return;
                }
                Send(ctx, 403, "text/plain; charset=utf-8", where);
                return;
            }
            bool post = ctx.Request.HttpMethod == "POST";
            if (post && !WriteOk(ctx.Request))
            {
                Send(ctx, 403, "text/plain; charset=utf-8", "refused: a change needs the X-CubeScope header and this page's own origin");
                return;
            }
            const string JsonType = "application/json; charset=utf-8";
            ReceiverAdmin admin = Admin;
            if (!AdminTokenOk(admin, ctx.Request.Headers["X-CubeScope-Admin"]))
            {
                bool desk = Environment.UserInteractive;
                Json j = new Json(512);
                if (post && path == "/api/admin/elevate" && desk)
                {
                    string err = ReceiverAdmin.OpenElevated("--admin");
                    j.Obj().P("ok", err == null).P("error", err ?? "").EndObj();
                    Send(ctx, 200, JsonType, j.ToString());
                    return;
                }
                if (post) { Send(ctx, 403, "text/plain; charset=utf-8", "This needs a CubeScope window opened as administrator."); return; }
                j.Obj().P("mode", "locked").P("canElevate", desk)
                 .P("why", desk
                    ? "Setting up this server needs a CubeScope window with administrator rights."
                    : "The CubeScope service does not change this server. Right-click CubeScope.exe, choose Run as administrator, and cancel the folder window to open the Admin page.")
                 .EndObj();
                Send(ctx, 200, JsonType, j.ToString());
                return;
            }
            if (!post && path == "/api/admin/state") { Send(ctx, 200, JsonType, admin.StateJson()); return; }
            if (post && (path == "/api/admin/review" || path == "/api/admin/apply"))
            {
                string body;
                using (StreamReader r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                {
                    char[] buf = new char[64 * 1024 + 1];
                    int len = r.ReadBlock(buf, 0, buf.Length);
                    if (len > 64 * 1024) { Send(ctx, 413, "text/plain; charset=utf-8", "settings too large"); return; }
                    body = new string(buf, 0, len);
                }
                string error;
                ReceiverSettings s;
                try { s = ReceiverSettings.FromJson(JsonRead.Parse(body) as Dictionary<string, object>, out error); }
                catch (FormatException) { s = null; error = "The settings are not readable."; }
                if (s == null)
                {
                    Json e = new Json(512); e.Obj().P("ok", false).P("error", error).EndObj();
                    Send(ctx, 400, JsonType, e.ToString());
                    return;
                }
                Send(ctx, 200, JsonType, path == "/api/admin/review" ? admin.ReviewJson(s) : admin.ApplyJson(s));
                return;
            }
            if (post && path == "/api/admin/logging")
            {
                // {"action":"on","minutes":30} or {"action":"off"}
                Dictionary<string, object> o = null;
                using (StreamReader r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                {
                    char[] buf = new char[4096 + 1];
                    int len = r.ReadBlock(buf, 0, buf.Length);
                    if (len > 4096) { Send(ctx, 413, "text/plain; charset=utf-8", "too large"); return; }
                    try { o = JsonRead.Parse(new string(buf, 0, len)) as Dictionary<string, object>; }
                    catch (FormatException) { }
                }
                string action = o != null ? JsonRead.GetString(o, "action", "") : "";
                if (action != "on" && action != "off") { Send(ctx, 400, JsonType, "{\"ok\":false,\"error\":\"action is on or off\"}"); return; }
                int minutes = o != null ? (int)JsonRead.GetNumber(o, "minutes", 30) : 30;
                Send(ctx, 200, JsonType, admin.LoggingJson(action == "on", minutes, "turned off on the Admin page"));
                return;
            }
            if (post && path == "/api/admin/remote")
            {
                // {"action":"review"|"apply"|"off", "httpsPort":8794, "thumbprint":"", "group":"...", "allowed":"...", "addPeople":"..."}
                Dictionary<string, object> o = null;
                using (StreamReader r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                {
                    char[] buf = new char[16 * 1024 + 1];
                    int len = r.ReadBlock(buf, 0, buf.Length);
                    if (len > 16 * 1024) { Send(ctx, 413, "text/plain; charset=utf-8", "too large"); return; }
                    try { o = JsonRead.Parse(new string(buf, 0, len)) as Dictionary<string, object>; }
                    catch (FormatException) { }
                }
                string action = o != null ? JsonRead.GetString(o, "action", "") : "";
                if (action != "review" && action != "apply" && action != "off") { Send(ctx, 400, JsonType, "{\"ok\":false,\"error\":\"action is review, apply or off\"}"); return; }
                string error = null;
                RemoteSettings rs = action == "off" ? new RemoteSettings() : RemoteSettings.FromJson(o, _port, out error);
                if (rs == null) { Json e = new Json(512); e.Obj().P("ok", false).P("error", error).EndObj(); Send(ctx, 400, JsonType, e.ToString()); return; }
                Send(ctx, 200, JsonType, admin.RemoteJson(action, rs, _port));
                return;
            }
            if (!post && path == "/api/admin/logging/file")
            {
                string p = admin.Ops.LoggingFilePath(ctx.Request.QueryString["name"]);
                if (p == null || admin.Simulated) { Send(ctx, 404, "text/plain; charset=utf-8", admin.Simulated ? "simulated: no file" : "no such saved log"); return; }
                // streamed: a long session's log runs to tens of MB
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                ctx.Response.Headers["Cache-Control"] = "no-store, must-revalidate";
                ctx.Response.AddHeader("Content-Disposition", "attachment; filename=\"" + Path.GetFileName(p) + "\"");
                using (FileStream fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    ctx.Response.ContentLength64 = fs.Length;
                    fs.CopyTo(ctx.Response.OutputStream);
                }
                ctx.Response.OutputStream.Close();
                return;
            }
            Send(ctx, 404, "text/plain; charset=utf-8", "not found");
        }

        /// <summary>A change from this page: over plain HTTP from localhost on this port, over HTTPS from the address the page came from.</summary>
        bool WriteOk(HttpListenerRequest r)
        {
            if (r.IsSecureConnection) return RemoteAccess.WriteAllowed(r.Headers["X-CubeScope"], r.Headers["Origin"], r.Headers["Host"]);
            return WriteAllowed(r.Headers["X-CubeScope"], r.Headers["Origin"], _port);
        }

        /// <summary>A change is allowed with the X-CubeScope header and, when the browser names an origin,
        /// only this page's own (localhost or 127.0.0.1 on this port).</summary>
        public static bool WriteAllowed(string header, string origin, int port)
        {
            if (header != "1") return false;
            if (string.IsNullOrEmpty(origin)) return true;
            string o = origin.TrimEnd('/').ToLowerInvariant();
            return o == "http://localhost:" + port || o == "http://127.0.0.1:" + port;
        }

        /// <summary>The events of one kind in a stretch of time, for the events lane's click.</summary>
        public string EventsJson(long from, long to, string kind, int src, int max)
        {
            return EventsJson(from, to, kind, src, max, null);
        }

        /// <summary>from, to and the times given are on the zone's clock.</summary>
        public string EventsJson(long from, long to, string kind, int src, int max, string zone)
        {
            return EventsJson(_cur, from, to, kind, src, max, zone);
        }

        string EventsJson(Store _st, long from, long to, string kind, int src, int max, string zone)
        {
            Json j = new Json(1 << 14);
            j.Obj();
            int n = 0;
            bool more = false;
            j.Key("items").Arr();
            lock (_st.Sync)
            {
                ZoneMap zm = ZoneMap.Build(_st, ZoneMap.Valid(zone) ? zone : "cube");
                for (int s = 0; s < _st.Sources.Count; s++)
                {
                    if (src >= 0 && s != src) continue;
                    List<SysEvent> evs = _st.Sources[s].Events;
                    for (int i = 0; i < evs.Count; i++)
                    {
                        SysEvent e = evs[i];
                        long t = zm.ToDisplay(s, e.Ts);
                        if (e.Kind != kind || (from >= 0 && t < from) || (to >= 0 && t >= to)) continue;
                        if (n >= max) { more = true; break; }
                        j.Obj().P("t", t / 1000).P("m", e.Mnemonic ?? "").P("x", e.Text ?? "").P("src", _st.Sources[s].Name).EndObj();
                        n++;
                    }
                }
            }
            j.EndArr();
            j.P("more", more);
            j.EndObj();
            return j.ToString();
        }

        static string FileStem(QueryResult r)
        {
            return "cubescope-" + TimeUtil.ToDate(r.From).ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + "-" +
                   TimeUtil.ToDate(r.To).ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        }

        /// <summary>The call with this id (its place in Store.Calls).</summary>
        public static Call FindCall(Store st, int id)
        {
            return id >= 0 && id < st.Calls.Count ? st.Calls[id] : null;
        }

        /// <summary>The newest call under this GUID: with a reused GUID that is not the only one.</summary>
        public static Call FindCall(Source s, string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            Call c;
            if (s.ByKey.TryGetValue(key, out c)) return c;
            if (s.ByKey.TryGetValue(GuidForm.Key(key), out c)) return c;
            return null;
        }

        /// <summary>
        /// from/to: unix seconds on the CUBE's clock, or yyyy-MM-ddTHH:mm[:ss]. Neither: the last day
        /// of data. range=all: everything. bucket: seconds or 1m/5m/15m/30m/1h/3h/6h/1d, absent = auto.
        /// </summary>
        public string ParseParams(System.Collections.Specialized.NameValueCollection qs, out QueryParams q)
        {
            return ParseParams(qs, out q, _cur);
        }

        string ParseParams(System.Collections.Specialized.NameValueCollection qs, out QueryParams q, Store _st)
        {
            q = new QueryParams();
            long from = ParseTime(qs["from"]), to = ParseTime(qs["to"]);
            if (qs["from"] != null && qs["from"].Length > 0 && from < 0) return "from must be unix seconds or yyyy-MM-ddTHH:mm";
            if (qs["to"] != null && qs["to"].Length > 0 && to < 0) return "to must be unix seconds or yyyy-MM-ddTHH:mm";
            string src = qs["src"];
            if (!string.IsNullOrEmpty(src) && src != "-1" && src != "all")
            {
                int si;
                if (int.TryParse(src, NumberStyles.Integer, CultureInfo.InvariantCulture, out si)) q.Src = si;
                else
                {
                    lock (_st.Sync)
                        for (int i = 0; i < _st.Sources.Count; i++)
                            if (string.Equals(_st.Sources[i].Name, src, StringComparison.OrdinalIgnoreCase)) q.Src = i;
                }
            }
            string zone = qs["zone"];
            if (!string.IsNullOrEmpty(zone))
            {
                if (!ZoneMap.Valid(zone)) return "zone must be cube, server, UTC, UTC-6 or a Windows zone name";
                q.Zone = zone;
            }
            // last=<seconds>: counted back from the newest record, so an old archive opens on its own end
            long lastSec;
            if (!long.TryParse(qs["last"] ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out lastSec) || lastSec <= 0)
                lastSec = 86400;
            q.Live = Following;
            if (Following) q.LiveNow = UtcNow();
            if (from < 0 && to < 0 && qs["range"] != "all")
            {
                // Following live records, "Last hour" ends now on the page's clock (while the data is current),
                // and a CUBE whose clock runs ahead cannot pull the newest record into the future.
                long first, last;
                lock (_st.Sync)
                {
                    ZoneMap zm = ZoneMap.Build(_st, q.Zone);
                    if (Following)
                    {
                        DateTime now = q.LiveNow;
                        Query.SpanLive(_st, q.Src, zm, now, out first, out last);
                        if (last >= 0) last = Query.LiveEnd(zm, last, now);
                    }
                    else Query.Span(_st, q.Src, zm, out first, out last);
                }
                if (last >= 0) { to = last + 1; from = Math.Max(first, to - lastSec * 1000L); }
            }
            q.From = from; q.To = to;
            q.Bucket = ParseBucket(qs["bucket"]);
            int pts;
            if (int.TryParse(qs["points"] ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out pts) && pts >= 20)
                q.MaxPoints = Math.Min(pts, 3000);
            q.Filter = Filter.Parse(qs["filter"] ?? "");
            return null;
        }

        public static long ParseTime(string s)
        {
            if (string.IsNullOrEmpty(s)) return -1;
            long unix;
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out unix)) return unix * 1000;
            // seconds with a fraction, to the millisecond: a chart zoom sends buckets clipped to a
            // range that began or ended at a record's own time
            double fs;
            if (s.IndexOf('.') > 0 && double.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out fs))
                return (long)Math.Round(fs * 1000);
            DateTime d;
            string[] fmts = { "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd" };
            if (DateTime.TryParseExact(s, fmts, CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return TimeUtil.FromDate(d);
            return -1;
        }

        public static int ParseBucket(string s)
        {
            if (string.IsNullOrEmpty(s) || s == "auto" || s == "0") return 0;
            int v;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return Math.Max(60, v);
            switch (s.ToLowerInvariant())
            {
                case "1m": return 60; case "5m": return 300; case "15m": return 900; case "30m": return 1800;
                case "1h": return 3600; case "3h": return 10800; case "6h": return 21600; case "1d": return 86400;
            }
            return 0;
        }

        static int QueryInt(HttpListenerContext ctx, string key, int dflt)
        {
            string v = ctx.Request.QueryString[key];
            int n;
            if (v != null && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
            return dflt;
        }

        static void Send(HttpListenerContext ctx, int status, string type, string body)
        {
            byte[] b = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = type;
            // nothing here is cacheable, and a cached page would survive an upgrade of the exe
            ctx.Response.Headers["Cache-Control"] = "no-store, must-revalidate";
            ctx.Response.ContentLength64 = b.Length;
            ctx.Response.OutputStream.Write(b, 0, b.Length);
            ctx.Response.OutputStream.Close();
        }

        public string DashboardHtml()
        {
            if (!string.IsNullOrEmpty(DashboardOverride) && File.Exists(DashboardOverride))
                return File.ReadAllText(DashboardOverride, Encoding.UTF8);
            return EmbeddedDashboard();
        }

        public static string EmbeddedDashboard()
        {
            Assembly a = Assembly.GetExecutingAssembly();
            string[] names = a.GetManifestResourceNames();
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i].EndsWith("dashboard.html", StringComparison.OrdinalIgnoreCase))
                {
                    using (Stream s = a.GetManifestResourceStream(names[i]))
                    using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                        return r.ReadToEnd();
                }
            }
            return "<h1>dashboard.html was not embedded</h1><p>Rebuild with build.bat, or pass --dashboard &lt;file&gt;.</p>";
        }
    }
}
