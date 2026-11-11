using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace CubeScope
{
    /// <summary>
    /// Remote access: the CubeScope service also answers other computers over HTTPS on a port of its own, and only
    /// people who sign in with a Windows account in one local group (or an administrator of this server) get in.
    /// Plain HTTP stays this computer only. The settings live in remote.json in the data folder and are read when the
    /// service starts; the Admin page writes them, makes or checks the certificate, binds it to the port, adds the
    /// firewall rule and the group, and restarts the service. Kept in one file so DialerScope can take the same code.
    /// </summary>
    public sealed class RemoteSettings
    {
        public const int DefaultPort = 8794;
        public const string DefaultGroup = "CubeScope Users";
        public const string FileName = "remote.json";

        public int HttpsPort = DefaultPort;
        /// <summary>A certificate chosen by thumbprint; empty for CubeScope's own.</summary>
        public string Thumbprint = "";
        public string Group = DefaultGroup;
        /// <summary>Addresses or subnets the firewall lets in; empty for anywhere (the sign-in still applies).</summary>
        public List<string> Allowed = new List<string>();
        /// <summary>Windows accounts to add to the group (DOMAIN\name or name); kept only for the step, not saved.</summary>
        public List<string> AddPeople = new List<string>();

        static readonly Regex Thumb = new Regex("^[0-9A-Fa-f]{40}$", RegexOptions.CultureInvariant);
        static readonly Regex GroupName = new Regex(@"^[^""/\\\[\]:;|=,+*?<>@]{1,64}$", RegexOptions.CultureInvariant);
        static readonly Regex Account = new Regex(@"^([A-Za-z0-9._-]{1,63}\\)?[^""/\\\[\]:;|=,+*?<>@\s][^""/\\\[\]:;|=,+*?<>@]{0,63}$", RegexOptions.CultureInvariant);

        public string ToJson()
        {
            Json j = new Json(512);
            j.Obj().P("httpsPort", HttpsPort).P("thumbprint", Thumbprint).P("group", Group);
            j.Key("allowed").Arr(); foreach (string a in Allowed) j.Val(a); j.EndArr();
            j.EndObj();
            return j.ToString();
        }

        /// <summary>The settings from the page or the file; null with the reason in words.</summary>
        public static RemoteSettings FromJson(Dictionary<string, object> o, int httpPort, out string error)
        {
            error = null;
            if (o == null) { error = "The remote access settings are not readable."; return null; }
            RemoteSettings s = new RemoteSettings();
            double p = JsonRead.GetNumber(o, "httpsPort", DefaultPort);
            if (p != Math.Floor(p) || p < 1 || p > 65535) { error = "The HTTPS port must be a whole number from 1 to 65535."; return null; }
            s.HttpsPort = (int)p;
            if (s.HttpsPort == httpPort) { error = "The HTTPS port must differ from CubeScope's own port " + httpPort + "."; return null; }
            s.Thumbprint = JsonRead.GetString(o, "thumbprint", "").Replace(" ", "").Replace("‎", "").Trim().ToUpperInvariant();
            if (s.Thumbprint.Length > 0 && !Thumb.IsMatch(s.Thumbprint)) { error = "A certificate thumbprint is 40 letters and digits (0-9, A-F); leave it empty for CubeScope's own."; return null; }
            s.Group = JsonRead.GetString(o, "group", DefaultGroup).Trim();
            if (s.Group.Length == 0) s.Group = DefaultGroup;
            if (!GroupName.IsMatch(s.Group)) { error = "The group name cannot hold \" / \\ [ ] : ; | = , + * ? < > or @."; return null; }
            // the page sends the addresses as typed; the saved file keeps a list. Empty: from anywhere (the sign-in still applies)
            object av;
            string typed = o.TryGetValue("allowed", out av) ? av as string : null;
            List<object> kept = av as List<object>;
            if (kept != null) { List<string> parts = new List<string>(); foreach (object x in kept) if (x is string) parts.Add((string)x); typed = string.Join(",", parts.ToArray()); }
            if (!string.IsNullOrEmpty(typed) && typed.Trim().Length > 0)
            {
                string bad;
                if (!ReceiverSettings.ParseAllowed(typed, s.Allowed, out bad)) { error = bad.Replace("a CUBE address", "an address or subnet such as 192.0.2.10 or 192.0.2.0/24"); return null; }
            }
            foreach (string who in JsonRead.GetString(o, "addPeople", "").Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string w = who.Trim();
                if (w.Length == 0) continue;
                if (!Account.IsMatch(w)) { error = "\"" + w + "\" is not a Windows account name (DOMAIN\\name, or name)."; return null; }
                if (!s.AddPeople.Contains(w)) s.AddPeople.Add(w);
            }
            return s;
        }

        /// <summary>The saved settings in the data folder, or null when remote access is off (no file, or unreadable).</summary>
        public static RemoteSettings Load(string dataDir, int httpPort)
        {
            string path = Path.Combine(dataDir, FileName);
            try
            {
                if (!File.Exists(path)) return null;
                string err;
                RemoteSettings s = FromJson(JsonRead.Parse(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>, httpPort, out err);
                if (s == null) Log.Warn("remote access: " + path + " is not usable (" + err + "), so CubeScope answers this computer only");
                return s;
            }
            catch (Exception ex) { Log.Warn("remote access: cannot read " + path + ": " + ex.Message); return null; }
        }
    }

    /// <summary>Who may use a request that came over HTTPS, and the checks a change must pass there.</summary>
    public static class RemoteAccess
    {
        static readonly object Gate = new object();
        static readonly Dictionary<string, SecurityIdentifier> Sids = new Dictionary<string, SecurityIdentifier>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, DateTime> Seen = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The group's identity: a name as given (BUILTIN\Users, DOMAIN\Group), else this computer's own group.</summary>
        public static SecurityIdentifier GroupSid(string group)
        {
            lock (Gate)
            {
                SecurityIdentifier sid;
                if (Sids.TryGetValue(group, out sid)) return sid;
                sid = null;
                try { sid = (SecurityIdentifier)new NTAccount(Environment.MachineName, group).Translate(typeof(SecurityIdentifier)); }
                catch (Exception) { }
                if (sid == null)
                {
                    try { sid = (SecurityIdentifier)new NTAccount(group).Translate(typeof(SecurityIdentifier)); }
                    catch (Exception) { }
                }
                if (sid != null) Sids[group] = sid;      // a group made later is found on the next request
                return sid;
            }
        }

        /// <summary>Null when this signed-in person may use CubeScope; otherwise what to tell them.</summary>
        public static string Refusal(IPrincipal user, string group)
        {
            if (user == null || user.Identity == null || !user.Identity.IsAuthenticated)
                return "Sign in with your Windows account to use CubeScope from another computer.";
            WindowsPrincipal wp = user as WindowsPrincipal;
            bool ok;
            if (wp != null)
            {
                SecurityIdentifier sid = GroupSid(group);
                ok = wp.IsInRole(WindowsBuiltInRole.Administrator) || (sid != null && wp.IsInRole(sid));
            }
            else ok = user.IsInRole(group) || user.IsInRole("Administrators");
            if (ok) return null;
            return user.Identity.Name + " is not in the \"" + group + "\" group on " + Environment.MachineName +
                   ". An administrator of that server can add you on CubeScope's Admin page, or with:  net localgroup \"" + group + "\" " + user.Identity.Name + " /add";
        }

        /// <summary>A change over HTTPS: the X-CubeScope header and, when the browser names an origin, this page's own.</summary>
        public static bool WriteAllowed(string header, string origin, string host)
        {
            if (header != "1") return false;
            if (string.IsNullOrEmpty(origin)) return true;
            if (string.IsNullOrEmpty(host)) return false;
            return string.Equals(origin.TrimEnd('/'), "https://" + host.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Each person's first visit an hour goes in the log (who used it from where), not every request.</summary>
        public static void NoteVisit(IPrincipal user, IPEndPoint from, DateTime now)
        {
            string who = user != null && user.Identity != null ? user.Identity.Name : "?";
            lock (Gate)
            {
                DateTime last;
                if (Seen.TryGetValue(who, out last) && now - last < TimeSpan.FromHours(1)) return;
                Seen[who] = now;
            }
            Log.Info("remote: " + who + " from " + (from != null ? from.Address.ToString() : "?"));
        }
    }

    /// <summary>What the remote-access set-up does to this computer. For each change: null when done, or the reason in words.</summary>
    public interface IRemoteOps
    {
        /// <summary>CubeScope's own certificate (made by this page, still good for 30 days), or null.</summary>
        string OwnCertificate();
        /// <summary>Null when the certificate can serve HTTPS here; otherwise why not.</summary>
        string CertificateProblem(string thumbprint);
        /// <summary>"CN=..., names ..., until yyyy-MM-dd".</summary>
        string CertificateInfo(string thumbprint);
        string MakeCertificate(string[] names, out string thumbprint);
        /// <summary>The certificate HTTPS on this port uses, and whose binding it is; null thumbprint when none.</summary>
        void BoundCertificate(int port, out string thumbprint, out bool ours);
        string BindCertificate(int port, string thumbprint);
        string UnbindCertificate(int port);
        /// <summary>The remote-access firewall rule's port and allowed addresses ("" for anywhere); null when there is none.</summary>
        string RemoteFirewall(out int port);
        string SetRemoteFirewall(int port, string[] allowed);
        string RemoveRemoteFirewall();
        bool LocalGroupExists(string group);
        string CreateLocalGroup(string group);
        string AddToLocalGroup(string group, string account);
        /// <summary>remote.json's text, or null when there is none.</summary>
        string RemoteSettingsText();
        string SaveRemoteSettings(string jsonOrNullToDelete);
        /// <summary>Null when the CubeScope service restarted; "not installed" in words when there is none.</summary>
        string RestartCubeScopeService();
        bool CubeScopeServiceInstalled();
        /// <summary>Null when HTTPS on the port answers with this certificate and asks for a Windows sign-in.</summary>
        string HttpsAnswers(int port, string thumbprint);
        /// <summary>Whether something other than an HTTPS binding already listens on the port.</summary>
        bool PortInUse(int port);
        /// <summary>This server's names (computer name, full name) for the certificate and the page's links.</summary>
        List<string> HostNames();
    }

    public sealed partial class ReceiverAdmin
    {
        /// <summary>CubeScope's own registration with Windows' HTTPS server, so its certificate binding can be told apart.</summary>
        public const string AppId = "{6f3a1c52-8d4e-4b7a-9c21-5e0f7b3d2a64}";

        static string Words(List<string> l) { return l.Count == 0 ? "anywhere" : string.Join(", ", l.ToArray()); }

        /// <summary>The steps that turn remote access on with these settings; Blocked says what is in the way. Changes nothing.</summary>
        public ReceiverReview RemoteReview(RemoteSettings s, int httpPort)
        {
            ReceiverReview r = new ReceiverReview();
            IReceiverOps o = Ops;
            if (!o.CubeScopeServiceInstalled())
                r.Blocked.Add("Remote access is served by the CubeScope service, which is not installed here. Tick \"Keep CubeScope running as a service\" above and Apply first.");
            string own = s.Thumbprint.Length == 0 ? o.OwnCertificate() : null;
            if (s.Thumbprint.Length > 0)
            {
                string why = o.CertificateProblem(s.Thumbprint);
                if (why != null) r.Blocked.Add("The certificate " + s.Thumbprint + " cannot be used: " + why);
            }
            string bound; bool ours;
            o.BoundCertificate(s.HttpsPort, out bound, out ours);
            if (bound != null && !ours) r.Blocked.Add("Port " + s.HttpsPort + " already serves HTTPS for another program. Choose another port.");
            else if (bound == null && o.PortInUse(s.HttpsPort)) r.Blocked.Add("Port " + s.HttpsPort + " is already in use by another program. Choose another port.");
            if (r.Blocked.Count > 0) return r;

            string[] thumb = { s.Thumbprint.Length > 0 ? s.Thumbprint : own };
            List<string> names = o.HostNames();
            string port = s.HttpsPort.ToString(CultureInfo.InvariantCulture);
            if (s.Thumbprint.Length > 0) Step(r, "Use the certificate " + o.CertificateInfo(s.Thumbprint), false, null);
            else if (own != null) Step(r, "Use CubeScope's own certificate " + o.CertificateInfo(own), false, null);
            else
            {
                List<string> all = new List<string>(names);
                foreach (string a in o.LocalAddresses()) if (!all.Contains(a)) all.Add(a);
                Step(r, "Make CubeScope's own certificate for " + string.Join(", ", all.ToArray()) + ", good for 5 years (browsers warn about it until it is trusted; see the README)", true,
                     delegate () { string t; string e = o.MakeCertificate(all.ToArray(), out t); thumb[0] = t; return e; });
            }
            bool groupThere = o.LocalGroupExists(s.Group);
            Step(r, groupThere ? "The local group \"" + s.Group + "\" exists" : "Create the local group \"" + s.Group + "\": its members (and this server's administrators) may use CubeScope over HTTPS",
                 !groupThere, delegate () { return o.CreateLocalGroup(s.Group); });
            foreach (string who in s.AddPeople)
            {
                string w = who;
                Step(r, "Add " + w + " to \"" + s.Group + "\"", true, delegate () { return o.AddToLocalGroup(s.Group, w); });
            }
            bool sameCert = bound != null && ours && thumb[0] != null && string.Equals(bound, thumb[0], StringComparison.OrdinalIgnoreCase);
            Step(r, sameCert ? "HTTPS on port " + port + " already uses that certificate" : "Use that certificate for HTTPS on port " + port, !sameCert,
                 delegate () { return thumb[0] == null ? "There is no certificate to use." : o.BindCertificate(s.HttpsPort, thumb[0]); });
            int fwPort;
            string fw = o.RemoteFirewall(out fwPort);
            bool fwOk = fw != null && fwPort == s.HttpsPort && SameList(fw, s.Allowed);
            Step(r, fwOk ? "Windows Firewall already allows TCP " + port + " from " + Words(s.Allowed) : "Allow TCP " + port + " from " + Words(s.Allowed) + " through Windows Firewall",
                 !fwOk, delegate () { return o.SetRemoteFirewall(s.HttpsPort, s.Allowed.ToArray()); });
            string saved = o.RemoteSettingsText();
            Step(r, "Save the remote access settings and restart the CubeScope service (the dashboard is away for a few seconds while it rereads its files)", true, delegate ()
            {
                RemoteSettings keep = new RemoteSettings();
                keep.HttpsPort = s.HttpsPort; keep.Thumbprint = s.Thumbprint; keep.Group = s.Group; keep.Allowed = s.Allowed;
                string e = o.SaveRemoteSettings(keep.ToJson());
                return e ?? o.RestartCubeScopeService();
            });
            Step(r, "Check that https://localhost:" + port + "/ answers with the certificate and asks for a Windows sign-in", true, delegate ()
            {
                string last = null;
                for (int i = 0; i < 40; i++)
                {
                    last = o.HttpsAnswers(s.HttpsPort, thumb[0]);
                    if (last == null) return null;
                    o.Wait(500);
                }
                return last;
            });
            if (saved != null) r.Notes.Add("Remote access is on now; Apply replaces its settings.");
            return r;
        }

        public ReceiverReview RemoteApply(RemoteSettings s, int httpPort, out bool busy)
        {
            busy = Interlocked.CompareExchange(ref _busy, 1, 0) != 0;
            if (busy) return null;
            try
            {
                ReceiverReview r = RemoteReview(s, httpPort);
                if (r.Blocked.Count > 0) return r;
                Log.Info("admin: remote access on: HTTPS port " + s.HttpsPort + ", group " + s.Group + ", certificate " + (s.Thumbprint.Length > 0 ? s.Thumbprint : "CubeScope's own") +
                         ", allowed " + Words(s.Allowed) + (s.AddPeople.Count > 0 ? ", adding " + string.Join(", ", s.AddPeople.ToArray()) : ""));
                RunSteps(r, false);
                return r;
            }
            finally { Interlocked.Exchange(ref _busy, 0); }
        }

        /// <summary>Turns remote access off: the settings go, the service restarts answering this computer only,
        /// the certificate binding and the firewall rule are removed. The certificate and the group stay.</summary>
        public ReceiverReview RemoteOff(out bool busy)
        {
            busy = Interlocked.CompareExchange(ref _busy, 1, 0) != 0;
            if (busy) return null;
            try
            {
                ReceiverReview r = new ReceiverReview();
                IReceiverOps o = Ops;
                string text = o.RemoteSettingsText();
                RemoteSettings s = null;
                if (text != null)
                {
                    string e;
                    try { s = RemoteSettings.FromJson(JsonRead.Parse(text) as Dictionary<string, object>, 0, out e); }
                    catch (FormatException) { }
                }
                int port = s != null ? s.HttpsPort : RemoteSettings.DefaultPort;
                if (text == null) r.Notes.Add("Remote access is already off.");
                Step(r, "Remove the remote access settings and restart the CubeScope service", text != null, delegate ()
                {
                    string e = o.SaveRemoteSettings(null);
                    return e ?? (o.CubeScopeServiceInstalled() ? o.RestartCubeScopeService() : null);
                });
                string bound; bool ours;
                o.BoundCertificate(port, out bound, out ours);
                Step(r, "Stop using the certificate for HTTPS on port " + port, bound != null && ours, delegate () { return o.UnbindCertificate(port); });
                int fwPort;
                Step(r, "Remove the Windows Firewall rule for port " + port, o.RemoteFirewall(out fwPort) != null, delegate () { return o.RemoveRemoteFirewall(); });
                Log.Info("admin: remote access off");
                RunSteps(r, true);
                return r;
            }
            finally { Interlocked.Exchange(ref _busy, 0); }
        }

        public string RemoteJson(string action, RemoteSettings s, int httpPort)
        {
            bool busy = false;
            ReceiverReview r = action == "review" ? RemoteReview(s, httpPort) : action == "apply" ? RemoteApply(s, httpPort, out busy) : RemoteOff(out busy);
            Json j = new Json(2048);
            if (busy) { j.Obj().P("ok", false).P("error", "Another change is still running.").EndObj(); return j.ToString(); }
            bool ok = r.Blocked.Count == 0;
            for (int i = 0; i < r.Steps.Count; i++) if (r.Steps[i].Status == "failed") ok = false;
            j.Obj().P("ok", ok);
            WriteSteps(j, r);
            j.EndObj();
            return j.ToString();
        }

        /// <summary>Remote access as it is now, for the Admin page.</summary>
        void WriteRemoteJson(Json j, int httpPort)
        {
            IReceiverOps o = Ops;
            string text = o.RemoteSettingsText();
            RemoteSettings s = null;
            if (text != null)
            {
                string e;
                try { s = RemoteSettings.FromJson(JsonRead.Parse(text) as Dictionary<string, object>, httpPort, out e); }
                catch (FormatException) { }
            }
            RemoteSettings show = s ?? new RemoteSettings();
            string thumb = show.Thumbprint.Length > 0 ? show.Thumbprint : o.OwnCertificate();
            j.Key("remote").Obj().P("on", s != null).P("port", show.HttpsPort).P("group", show.Group).P("thumbprint", show.Thumbprint)
             .P("allowed", string.Join(", ", show.Allowed.ToArray())).P("cert", thumb != null ? o.CertificateInfo(thumb) : "")
             .P("ownCert", show.Thumbprint.Length == 0).P("service", o.CubeScopeServiceInstalled()).P("groupExists", o.LocalGroupExists(show.Group));
            j.Key("links").Arr();
            foreach (string n in o.HostNames()) j.Val("https://" + n + ":" + show.HttpsPort.ToString(CultureInfo.InvariantCulture) + "/");
            j.EndArr();
            j.EndObj();
        }
    }

    public sealed partial class WindowsReceiverOps
    {
        const string RemoteFirewallName = "CubeScope HTTPS";
        const string OwnCertName = "CubeScope HTTPS";

        static X509Certificate2 FindInStore(string thumbprint)
        {
            X509Store st = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            st.Open(OpenFlags.ReadOnly);
            try
            {
                X509Certificate2Collection c = st.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false);
                return c.Count > 0 ? c[0] : null;
            }
            finally { st.Close(); }
        }

        public string OwnCertificate()
        {
            X509Store st = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            try
            {
                st.Open(OpenFlags.ReadOnly);
                X509Certificate2 best = null;
                foreach (X509Certificate2 c in st.Certificates)
                    if (c.FriendlyName == OwnCertName && c.HasPrivateKey && c.NotAfter > DateTime.Now.AddDays(30) && (best == null || c.NotAfter > best.NotAfter)) best = c;
                return best != null ? best.Thumbprint : null;
            }
            catch (Exception) { return null; }
            finally { st.Close(); }
        }

        public string CertificateProblem(string thumbprint)
        {
            try
            {
                X509Certificate2 c = FindInStore(thumbprint);
                if (c == null) return "it is not in this computer's certificate store (Local Computer, Personal).";
                if (!c.HasPrivateKey) return "this server does not hold its private key.";
                if (c.NotAfter < DateTime.Now) return "it ran out on " + c.NotAfter.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".";
                foreach (X509Extension e in c.Extensions)
                {
                    X509EnhancedKeyUsageExtension eku = e as X509EnhancedKeyUsageExtension;
                    if (eku == null) continue;
                    bool server = false;
                    foreach (System.Security.Cryptography.Oid u in eku.EnhancedKeyUsages) if (u.Value == "1.3.6.1.5.5.7.3.1") server = true;
                    if (!server) return "it is not made for web servers (no Server Authentication purpose).";
                }
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        public string CertificateInfo(string thumbprint)
        {
            try
            {
                X509Certificate2 c = FindInStore(thumbprint);
                if (c == null) return thumbprint + " (not found)";
                return c.GetNameInfo(X509NameType.SimpleName, false) + ", until " + c.NotAfter.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                       (c.Issuer == c.Subject ? ", made on this server" : ", from " + c.GetNameInfo(X509NameType.SimpleName, true)) + " (" + c.Thumbprint + ")";
            }
            catch (Exception ex) { return thumbprint + " (" + ex.Message + ")"; }
        }

        public string MakeCertificate(string[] names, out string thumbprint)
        {
            thumbprint = null;
            List<string> dns = new List<string>(), ips = new List<string>();
            foreach (string n in names)
            {
                IPAddress ip;
                if (IPAddress.TryParse(n, out ip)) ips.Add(n);
                else if (Regex.IsMatch(n, @"^[A-Za-z0-9.-]{1,253}$")) dns.Add(n);
            }
            if (dns.Count == 0) dns.Add(Environment.MachineName);
            string san = string.Join("&", dns.ConvertAll(delegate (string d) { return "DNS=" + d; }).ToArray()) +
                         (ips.Count > 0 ? "&" + string.Join("&", ips.ConvertAll(delegate (string a) { return "IPAddress=" + a; }).ToArray()) : "");
            // the full form (names and addresses, 5 years, web-server purpose); Windows Server 2012 R2 knows only -DnsName
            string full = "$c = New-SelfSignedCertificate -CertStoreLocation Cert:\\LocalMachine\\My -Subject 'CN=" + dns[0] + "' " +
                          "-TextExtension @('2.5.29.17={text}" + san + "','2.5.29.37={text}1.3.6.1.5.5.7.3.1') -NotAfter (Get-Date).AddYears(5) " +
                          "-KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 -KeyExportPolicy NonExportable; " +
                          "$c.FriendlyName = '" + OwnCertName + "'; $c.Thumbprint";
            string basic = "$c = New-SelfSignedCertificate -CertStoreLocation Cert:\\LocalMachine\\My -DnsName " +
                           string.Join(",", dns.ConvertAll(delegate (string d) { return "'" + d + "'"; }).ToArray()) + "; $c.FriendlyName = '" + OwnCertName + "'; $c.Thumbprint";
            foreach (string script in new[] { full, basic })
            {
                string output;
                int rc = Run(Sys(@"WindowsPowerShell\v1.0\powershell.exe"), "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + script.Replace("\"", "\\\"") + "\"", 60000, out output);
                Match m = Regex.Match(output ?? "", "(?m)^([0-9A-F]{40})\\s*$");
                if (rc == 0 && m.Success) { thumbprint = m.Groups[1].Value; return null; }
                Log.Warn("admin: certificate not made that way: " + (output ?? "").Trim());
            }
            return "Windows did not make the certificate. The log has what it said.";
        }

        public void BoundCertificate(int port, out string thumbprint, out bool ours)
        {
            thumbprint = null; ours = false;
            string output;
            int rc = Run(Sys("netsh.exe"), "http show sslcert ipport=0.0.0.0:" + port.ToString(CultureInfo.InvariantCulture), 15000, out output);
            if (rc != 0) return;
            Match h = Regex.Match(output, @"Certificate Hash\s*:\s*([0-9A-Fa-f]{40})");
            if (!h.Success) return;
            thumbprint = h.Groups[1].Value.ToUpperInvariant();
            ours = output.IndexOf(ReceiverAdmin.AppId, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public string BindCertificate(int port, string thumbprint)
        {
            string output;
            string ip = "ipport=0.0.0.0:" + port.ToString(CultureInfo.InvariantCulture);
            Run(Sys("netsh.exe"), "http delete sslcert " + ip, 15000, out output);
            int rc = Run(Sys("netsh.exe"), "http add sslcert " + ip + " certhash=" + thumbprint + " appid=" + ReceiverAdmin.AppId + " certstorename=MY", 15000, out output);
            return rc == 0 ? null : "Windows did not bind the certificate to the port: " + output;
        }

        public string UnbindCertificate(int port)
        {
            string output;
            int rc = Run(Sys("netsh.exe"), "http delete sslcert ipport=0.0.0.0:" + port.ToString(CultureInfo.InvariantCulture), 15000, out output);
            return rc == 0 ? null : "Windows did not remove the binding: " + output;
        }

        public string RemoteFirewall(out int port)
        {
            port = 0;
            string output;
            int rc = Run(Sys("netsh.exe"), "advfirewall firewall show rule name=\"" + RemoteFirewallName + "\"", 15000, out output);
            if (rc != 0) return null;
            string list = ReceiverAdmin.ParseFirewall(output, out port);
            return list.Equals("Any", StringComparison.OrdinalIgnoreCase) ? "" : list;
        }

        public string SetRemoteFirewall(int port, string[] allowed)
        {
            string output;
            Run(Sys("netsh.exe"), "advfirewall firewall delete rule name=\"" + RemoteFirewallName + "\"", 15000, out output);
            int rc = Run(Sys("netsh.exe"), "advfirewall firewall add rule name=\"" + RemoteFirewallName + "\" dir=in action=allow protocol=TCP localport=" +
                         port.ToString(CultureInfo.InvariantCulture) + " remoteip=" + (allowed.Length == 0 ? "any" : string.Join(",", allowed)) + " profile=any", 15000, out output);
            return rc == 0 ? null : "Windows Firewall refused the rule: " + output;
        }

        public string RemoveRemoteFirewall()
        {
            string output;
            int rc = Run(Sys("netsh.exe"), "advfirewall firewall delete rule name=\"" + RemoteFirewallName + "\"", 15000, out output);
            return rc == 0 ? null : "Windows Firewall did not remove the rule: " + output;
        }

        public bool LocalGroupExists(string group)
        {
            string output;
            return Run(Sys("net.exe"), "localgroup \"" + group + "\"", 15000, out output) == 0;
        }

        public string CreateLocalGroup(string group)
        {
            string output;
            int rc = Run(Sys("net.exe"), "localgroup \"" + group + "\" /add /comment:\"May use CubeScope from other computers (HTTPS)\"", 15000, out output);
            return rc == 0 ? null : "Windows did not create the group: " + output;
        }

        public string AddToLocalGroup(string group, string account)
        {
            string output;
            int rc = Run(Sys("net.exe"), "localgroup \"" + group + "\" \"" + account + "\" /add", 30000, out output);
            if (rc == 0 || output.IndexOf("1378", StringComparison.Ordinal) >= 0) return null;           // already a member
            return "Windows did not add " + account + ": " + output;
        }

        string RemoteFile() { return Path.Combine(HostOptions.DefaultDataDir(), RemoteSettings.FileName); }

        public string RemoteSettingsText()
        {
            try { return File.Exists(RemoteFile()) ? File.ReadAllText(RemoteFile(), Encoding.UTF8) : null; }
            catch (Exception) { return null; }
        }

        public string SaveRemoteSettings(string json)
        {
            if (json == null) { try { File.Delete(RemoteFile()); } catch (Exception) { } return null; }
            Directory.CreateDirectory(HostOptions.DefaultDataDir());
            File.WriteAllText(RemoteFile(), json, new UTF8Encoding(false));
            return null;
        }

        public bool CubeScopeServiceInstalled()
        {
            foreach (ServiceController s in ServiceController.GetServices())
                if (string.Equals(s.ServiceName, CubeScopeService.Name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public string RestartCubeScopeService()
        {
            if (!CubeScopeServiceInstalled()) return "The CubeScope service is not installed.";
            using (ServiceController c = new ServiceController(CubeScopeService.Name))
            {
                if (c.Status != ServiceControllerStatus.Stopped)
                {
                    c.Stop();
                    c.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
                }
                c.Start();
                c.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
            }
            return null;
        }

        public string HttpsAnswers(int port, string thumbprint)
        {
            try
            {
                using (TcpClient c = new TcpClient())
                {
                    IAsyncResult ar = c.BeginConnect(IPAddress.Loopback, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(3000)) return "Nothing answers on port " + port + " yet.";
                    c.EndConnect(ar);
                    string seen = null;
                    using (SslStream ssl = new SslStream(c.GetStream(), false, delegate (object s, X509Certificate cert, X509Chain ch, SslPolicyErrors e)
                    {
                        seen = cert != null ? new X509Certificate2(cert).Thumbprint : null;
                        return true;                       // only reading which certificate it is
                    }))
                    {
                        ssl.ReadTimeout = 5000; ssl.WriteTimeout = 5000;
                        ssl.AuthenticateAsClient("localhost");
                        if (thumbprint != null && !string.Equals(seen, thumbprint, StringComparison.OrdinalIgnoreCase))
                            return "Port " + port + " answers with another certificate (" + seen + ").";
                        byte[] req = Encoding.ASCII.GetBytes("GET /api/health HTTP/1.1\r\nHost: localhost:" + port + "\r\nConnection: close\r\n\r\n");
                        ssl.Write(req, 0, req.Length);
                        byte[] buf = new byte[256];
                        int n = ssl.Read(buf, 0, buf.Length);
                        string status = Encoding.ASCII.GetString(buf, 0, n);
                        if (status.StartsWith("HTTP/1.1 401", StringComparison.Ordinal)) return null;
                        int nl = status.IndexOf('\r');
                        return "Port " + port + " answered \"" + (nl > 0 ? status.Substring(0, nl) : status) + "\" instead of asking for a sign-in.";
                    }
                }
            }
            catch (Exception ex) { return "HTTPS on port " + port + " does not answer yet: " + ex.Message; }
        }

        public bool PortInUse(int port)
        {
            try
            {
                foreach (IPEndPoint e in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()) if (e.Port == port) return true;
            }
            catch (Exception) { }
            return false;
        }

        public List<string> HostNames()
        {
            List<string> l = new List<string>();
            l.Add(Environment.MachineName);
            try
            {
                string domain = IPGlobalProperties.GetIPGlobalProperties().DomainName;
                if (!string.IsNullOrEmpty(domain)) l.Add(Environment.MachineName + "." + domain);
            }
            catch (Exception) { }
            return l;
        }
    }

    /// <summary>The pretend server's part: changes nothing on this computer.</summary>
    public sealed partial class SimulatedReceiverOps
    {
        public string OwnCert, BoundThumb, RemoteText, HttpsProblem;
        public bool BoundOurs = true, PortBusy, ServiceInstalled;
        public readonly List<string> Groups = new List<string>();
        public readonly Dictionary<string, string> Certs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public int RemoteFwPort;
        public string RemoteFwList;

        public string OwnCertificate() { return OwnCert; }
        public string CertificateProblem(string t) { string p; return Certs.TryGetValue(t, out p) ? (p.Length == 0 ? null : p) : "it is not in this computer's certificate store (Local Computer, Personal)."; }
        public string CertificateInfo(string t) { return "CN=example-server, until 2031-11-10 (" + t + ")"; }
        public string MakeCertificate(string[] names, out string thumbprint)
        {
            Calls.Add("make-cert"); thumbprint = "AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12"; OwnCert = thumbprint; Certs[thumbprint] = ""; return null;
        }
        public void BoundCertificate(int port, out string thumbprint, out bool ours) { thumbprint = BoundThumb; ours = BoundOurs; }
        public string BindCertificate(int port, string t) { Calls.Add("bind " + port); BoundThumb = t; BoundOurs = true; return null; }
        public string UnbindCertificate(int port) { Calls.Add("unbind " + port); BoundThumb = null; return null; }
        public string RemoteFirewall(out int port) { port = RemoteFwPort; return RemoteFwList; }
        public string SetRemoteFirewall(int port, string[] allowed) { Calls.Add("https-firewall"); RemoteFwPort = port; RemoteFwList = string.Join(",", allowed); return null; }
        public string RemoveRemoteFirewall() { Calls.Add("https-firewall-off"); RemoteFwList = null; RemoteFwPort = 0; return null; }
        public bool LocalGroupExists(string g) { return Groups.Contains(g); }
        public string CreateLocalGroup(string g) { Calls.Add("group"); Groups.Add(g); return null; }
        public string AddToLocalGroup(string g, string who) { Calls.Add("member " + who); return null; }
        public string RemoteSettingsText() { return RemoteText; }
        public string SaveRemoteSettings(string json) { Calls.Add(json == null ? "remote-off" : "remote-save"); RemoteText = json; return null; }
        public string RestartCubeScopeService() { Calls.Add("restart-service"); return CubeScopeServiceInstalled() ? null : "The CubeScope service is not installed."; }
        public bool CubeScopeServiceInstalled() { return ServiceInstalled || ServiceFolder != null; }
        public string HttpsAnswers(int port, string t) { return HttpsProblem; }
        public bool PortInUse(int port) { return PortBusy; }
        public List<string> HostNames() { return new List<string> { "example-server", "example-server.example.local" }; }
    }
}
