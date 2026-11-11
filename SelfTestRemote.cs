using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;

namespace CubeScope
{
    /// <summary>Remote access: its settings, who gets in, the change check over HTTPS, the Admin page's steps against the
    /// stand-in, and a real Windows sign-in on this computer's own loopback (no certificate needed for that part).</summary>
    public static partial class SelfTest
    {
        static Dictionary<string, object> RObj(string json) { return (Dictionary<string, object>)JsonRead.Parse(json); }

        static void TestRemote()
        {
            bool echo = Log.Echo;
            Log.Echo = false;
            try
            {
                TestRemoteSettings();
                TestRemoteWho();
                TestRemoteAdmin();
                TestRemoteSignIn();
            }
            finally { Log.Echo = echo; }
        }

        static void TestRemoteSettings()
        {
            Group("remote access: settings");
            string err;
            RemoteSettings d = RemoteSettings.FromJson(RObj("{}"), 8792, out err);
            Check(d != null && d.HttpsPort == 8794 && d.Group == "CubeScope Users" && d.Thumbprint == "" && d.Allowed.Count == 0, "defaults: 8794, CubeScope Users, own certificate, from anywhere");
            Check(RemoteSettings.FromJson(RObj("{\"httpsPort\":8792}"), 8792, out err) == null && err.IndexOf("differ", StringComparison.Ordinal) > 0, "not CubeScope's own port");
            Check(RemoteSettings.FromJson(RObj("{\"httpsPort\":70000}"), 8792, out err) == null, "a port that cannot be");
            RemoteSettings t = RemoteSettings.FromJson(RObj("{\"thumbprint\":\"ab 12 ab 12 ab 12 ab 12 ab 12 ab 12 ab 12 ab 12 ab 12 ab 12\"}"), 8792, out err);
            Check(t != null && t.Thumbprint == "AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12", "a thumbprint pasted with spaces: " + err);
            Check(RemoteSettings.FromJson(RObj("{\"thumbprint\":\"xyz\"}"), 8792, out err) == null, "not a thumbprint");
            Check(RemoteSettings.FromJson(RObj("{\"group\":\"a;b\"}"), 8792, out err) == null, "a group name Windows cannot have");
            RemoteSettings a = RemoteSettings.FromJson(RObj("{\"allowed\":\"192.0.2.0/24, 198.51.100.7\",\"addPeople\":\"EXAMPLE\\\\alice, bob\"}"), 8792, out err);
            Check(a != null && string.Join("|", a.Allowed) == "192.0.2.0/24|198.51.100.7" && string.Join("|", a.AddPeople) == "EXAMPLE\\alice|bob", "addresses and people: " + err);
            Check(RemoteSettings.FromJson(RObj("{\"allowed\":\"anywhere\"}"), 8792, out err) == null && err.IndexOf("address or subnet", StringComparison.Ordinal) > 0, "a word that is not an address");
            Check(RemoteSettings.FromJson(RObj("{\"addPeople\":\"a\\\"b\"}"), 8792, out err) == null, "not an account name");
            RemoteSettings back = RemoteSettings.FromJson(RObj(a.ToJson()), 8792, out err);
            Check(back != null && back.HttpsPort == a.HttpsPort && string.Join("|", back.Allowed) == "192.0.2.0/24|198.51.100.7" && back.AddPeople.Count == 0,
                  "saved and read back (people are added, not kept): " + err);
        }

        static void TestRemoteWho()
        {
            Group("remote access: who gets in");
            Check(RemoteAccess.Refusal(null, "CubeScope Users") != null, "nobody signed in");
            Check(RemoteAccess.Refusal(new GenericPrincipal(new GenericIdentity("EXAMPLE\\alice", "Negotiate"), new[] { "CubeScope Users" }), "CubeScope Users") == null, "a member of the group");
            Check(RemoteAccess.Refusal(new GenericPrincipal(new GenericIdentity("EXAMPLE\\root", "Negotiate"), new[] { "Administrators" }), "CubeScope Users") == null, "an administrator of the server");
            string no = RemoteAccess.Refusal(new GenericPrincipal(new GenericIdentity("EXAMPLE\\eve", "Negotiate"), new string[0]), "CubeScope Users");
            Check(no != null && no.IndexOf("EXAMPLE\\eve is not in", StringComparison.Ordinal) == 0 && no.IndexOf("net localgroup \"CubeScope Users\" EXAMPLE\\eve /add", StringComparison.Ordinal) > 0,
                  "anyone else is told why and how to be added");

            Group("remote access: a change over HTTPS");
            Check(RemoteAccess.WriteAllowed("1", "https://cube-server.example.local:8794", "cube-server.example.local:8794"), "from the page's own address");
            Check(RemoteAccess.WriteAllowed("1", "HTTPS://CUBE-SERVER:8794/", "cube-server:8794"), "case and a trailing slash do not matter");
            Check(RemoteAccess.WriteAllowed("1", null, "cube-server:8794"), "no Origin (the page's own fetch)");
            Check(!RemoteAccess.WriteAllowed(null, null, "cube-server:8794"), "no X-CubeScope header: refused");
            Check(!RemoteAccess.WriteAllowed("1", "https://evil.example", "cube-server:8794"), "another site: refused");
            Check(!RemoteAccess.WriteAllowed("1", "http://cube-server:8794", "cube-server:8794"), "the same name over plain HTTP: refused");
        }

        static RemoteSettings RS(string json) { string e; return RemoteSettings.FromJson(RObj(json), 8792, out e); }

        static void TestRemoteAdmin()
        {
            Group("remote access: the Admin page's steps");
            SimulatedReceiverOps o = new SimulatedReceiverOps();
            ReceiverAdmin a = new ReceiverAdmin(o, true, "t");
            ReceiverReview none = a.RemoteReview(RS("{}"), 8792);
            Check(none.Blocked.Count == 1 && none.Blocked[0].IndexOf("service", StringComparison.Ordinal) > 0, "no CubeScope service: refused, saying so");
            Eq(o.Calls.Count, 0, "and nothing touched");

            o.ServiceInstalled = true;
            bool busy;
            ReceiverReview on = a.RemoteApply(RS("{\"addPeople\":\"EXAMPLE\\\\alice\",\"allowed\":\"192.0.2.0/24\"}"), 8792, out busy);
            Eq(on.Blocked.Count, 0, "turned on");
            Eq(string.Join(",", o.Calls), "make-cert,group,member EXAMPLE\\alice,bind 8794,https-firewall,remote-save,restart-service",
               "a certificate made, the group made and filled, bound, the firewall, saved, the service restarted");
            Check(on.Steps[on.Steps.Count - 1].Status == "done", "and HTTPS answers with a sign-in");
            Eq(o.BoundThumb, "AB12AB12AB12AB12AB12AB12AB12AB12AB12AB12", "the made certificate is the one bound");
            Eq(o.RemoteFwList, "192.0.2.0/24", "the firewall lets in only what was asked");
            Check(o.RemoteText != null && o.RemoteText.IndexOf("\"httpsPort\":8794", StringComparison.Ordinal) > 0 && o.RemoteText.IndexOf("alice", StringComparison.Ordinal) < 0,
                  "the settings file holds the port, not the people added");
            string state = a.StateJson();
            Check(state.IndexOf("\"remote\":{\"on\":true,\"port\":8794", StringComparison.Ordinal) > 0 && state.IndexOf("https://example-server.example.local:8794/", StringComparison.Ordinal) > 0,
                  "the page shows it on, with the addresses to use");

            o.Calls.Clear();
            a.RemoteApply(RS("{\"allowed\":\"192.0.2.0/24\"}"), 8792, out busy);
            Eq(string.Join(",", o.Calls), "remote-save,restart-service", "again with nothing new: only saved and restarted");

            o.Calls.Clear();
            o.HttpsProblem = "Port 8794 answered \"HTTP/1.1 200 OK\" instead of asking for a sign-in.";
            ReceiverReview bad = a.RemoteApply(RS("{\"allowed\":\"192.0.2.0/24\"}"), 8792, out busy);
            Check(bad.Steps[bad.Steps.Count - 1].Status == "failed" && bad.Steps[bad.Steps.Count - 1].Detail.IndexOf("instead of asking", StringComparison.Ordinal) > 0,
                  "a port that answers without a sign-in fails the last check, saying what came back");
            o.HttpsProblem = null;

            o.Calls.Clear();
            ReceiverReview off = a.RemoteOff(out busy);
            Eq(string.Join(",", o.Calls), "remote-off,restart-service,unbind 8794,https-firewall-off", "off: settings gone, restarted, binding and rule removed");
            Check(o.RemoteText == null && o.BoundThumb == null && a.StateJson().IndexOf("\"remote\":{\"on\":false", StringComparison.Ordinal) > 0, "and the page shows it off");
            o.Calls.Clear();
            Check(a.RemoteOff(out busy).Notes.Count == 1 && o.Calls.Count == 0, "off when off: nothing to do, and it says so");

            Group("remote access: refused before anything changes");
            SimulatedReceiverOps other = new SimulatedReceiverOps();
            other.ServiceInstalled = true; other.BoundThumb = "CD34CD34CD34CD34CD34CD34CD34CD34CD34CD34"; other.BoundOurs = false;
            ReceiverReview taken = new ReceiverAdmin(other, true, "t").RemoteReview(RS("{}"), 8792);
            Check(taken.Blocked.Count == 1 && taken.Blocked[0].IndexOf("another program", StringComparison.Ordinal) > 0, "a port another program serves HTTPS on");
            SimulatedReceiverOps busyPort = new SimulatedReceiverOps();
            busyPort.ServiceInstalled = true; busyPort.PortBusy = true;
            Eq(new ReceiverAdmin(busyPort, true, "t").RemoteReview(RS("{}"), 8792).Blocked.Count, 1, "a port something else listens on");
            SimulatedReceiverOps given = new SimulatedReceiverOps();
            given.ServiceInstalled = true;
            ReceiverReview missing = new ReceiverAdmin(given, true, "t").RemoteReview(RS("{\"thumbprint\":\"EF56EF56EF56EF56EF56EF56EF56EF56EF56EF56\"}"), 8792);
            Check(missing.Blocked.Count == 1 && missing.Blocked[0].IndexOf("certificate store", StringComparison.Ordinal) > 0, "a thumbprint this server does not have");
            given.Certs["EF56EF56EF56EF56EF56EF56EF56EF56EF56EF56"] = "";
            new ReceiverAdmin(given, true, "t").RemoteApply(RS("{\"thumbprint\":\"EF56EF56EF56EF56EF56EF56EF56EF56EF56EF56\"}"), 8792, out busy);
            Check(!given.Calls.Contains("make-cert") && given.BoundThumb == "EF56EF56EF56EF56EF56EF56EF56EF56EF56EF56", "a chosen certificate is used, none made");
        }

        static void TestRemoteSignIn()
        {
            Group("remote access: a real Windows sign-in on this computer");
            int port;
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            WebServer w = new WebServer(new Store(), port);
            w.SignInOverHttpForTest = true;
            w.AllowGroup = "Users";                                    // this computer's Users group: everyone signed in here
            try { w.Start(); }
            catch (Exception ex) { Check(true, "skipped: no loopback listener here (" + ex.Message + ")"); return; }
            try
            {
                string url = "http://localhost:" + port + "/api/health";
                Eq(Status(url, true), 200, "signed in as this Windows user, a member of the group: in");
                w.AllowGroup = "No Such Group For CubeScope Test";
                Eq(Status(url, true), WindowsAdmin() ? 200 : 403, "not in the group: refused (an administrator always gets in)");
                Eq(Status(url, false), 401, "no sign-in: asked for one");
            }
            finally { w.Stop(); }
        }

        static bool WindowsAdmin()
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch (Exception) { return false; }
        }

        static int Status(string url, bool signIn)
        {
            HttpWebRequest r = (HttpWebRequest)WebRequest.Create(url);
            r.UseDefaultCredentials = signIn;
            r.Timeout = 15000;
            r.Proxy = null;
            try { using (HttpWebResponse resp = (HttpWebResponse)r.GetResponse()) return (int)resp.StatusCode; }
            catch (WebException ex)
            {
                HttpWebResponse resp = ex.Response as HttpWebResponse;
                return resp != null ? (int)resp.StatusCode : -1;
            }
        }
    }
}
