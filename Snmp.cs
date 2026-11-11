using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;

namespace CubeScope
{
    /// <summary>
    /// A minimal BER encoder and SNMPv2c trap builder, sent over a plain UDP socket - no Windows SNMP
    /// service, evntwin or external library, so it works on a box where Cisco's agent owns the SNMP
    /// ports. A C# copy of the PowerShell encoder in C:\Claude\CvpErrorLogWatch (and tools\
    /// Watch-DnisFailures.ps1); the self-test checks the same hand-computed reference bytes.
    /// </summary>
    public static class Snmp
    {
        public static byte[] Length(int length)
        {
            if (length < 0x80) return new byte[] { (byte)length };
            List<byte> tmp = new List<byte>();
            int v = length;
            while (v > 0) { tmp.Insert(0, (byte)(v & 0xFF)); v >>= 8; }
            tmp.Insert(0, (byte)(0x80 | tmp.Count));
            return tmp.ToArray();
        }

        public static byte[] Tlv(byte tag, byte[] value)
        {
            if (value == null) value = new byte[0];
            List<byte> o = new List<byte>(value.Length + 4);
            o.Add(tag);
            o.AddRange(Length(value.Length));
            o.AddRange(value);
            return o.ToArray();
        }

        /// <summary>0x02 INTEGER, 0x41 Counter32, 0x42 Gauge32, 0x43 TimeTicks; non-negative values.</summary>
        public static byte[] UInt(long value, byte tag)
        {
            if (value < 0) throw new ArgumentOutOfRangeException("value", "non-negative values only");
            List<byte> tmp = new List<byte>();
            if (value == 0) tmp.Add(0);
            else
            {
                long v = value;
                while (v > 0) { tmp.Insert(0, (byte)(v & 0xFF)); v >>= 8; }
                if ((tmp[0] & 0x80) != 0) tmp.Insert(0, 0);   // keep it positive
            }
            return Tlv(tag, tmp.ToArray());
        }

        public static byte[] Integer(long value) { return UInt(value, 0x02); }

        public static byte[] OctetString(string text)
        {
            return Tlv(0x04, Encoding.UTF8.GetBytes(text ?? ""));
        }

        public static byte[] Oid(string oid)
        {
            string[] parts = (oid ?? "").Split(new char[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) throw new ArgumentException("OID '" + oid + "' needs at least two arcs.");
            long[] arcs = new long[parts.Length];
            for (int i = 0; i < parts.Length; i++) arcs[i] = long.Parse(parts[i], System.Globalization.CultureInfo.InvariantCulture);
            List<byte> o = new List<byte>();
            o.Add((byte)(40 * arcs[0] + arcs[1]));
            for (int i = 2; i < arcs.Length; i++)
            {
                long v = arcs[i];
                List<byte> chunk = new List<byte>();
                chunk.Add((byte)(v & 0x7F));                      // low group, continuation bit clear
                v >>= 7;
                while (v > 0) { chunk.Insert(0, (byte)((v & 0x7F) | 0x80)); v >>= 7; }
                o.AddRange(chunk);
            }
            return Tlv(0x06, o.ToArray());
        }

        /// <summary>True for a dotted OID of at least two numeric arcs.</summary>
        public static bool ValidOid(string oid)
        {
            if (string.IsNullOrEmpty(oid)) return false;
            string[] p = oid.Split('.');
            if (p.Length < 2) return false;
            for (int i = 0; i < p.Length; i++)
            {
                if (p[i].Length == 0 || p[i].Length > 10) return false;
                for (int k = 0; k < p[i].Length; k++) if (p[i][k] < '0' || p[i][k] > '9') return false;
            }
            return true;
        }

        public sealed class VarBind
        {
            public string Oid;
            public bool Number;      // Gauge32 when true, else OctetString
            public long Value;
            public string Text;
            public static VarBind Str(string oid, string text) { VarBind v = new VarBind(); v.Oid = oid; v.Text = text; return v; }
            public static VarBind Gauge(string oid, long value) { VarBind v = new VarBind(); v.Oid = oid; v.Number = true; v.Value = value; return v; }
        }

        static byte[] Seq(params byte[][] parts)
        {
            List<byte> o = new List<byte>();
            for (int i = 0; i < parts.Length; i++) o.AddRange(parts[i]);
            return Tlv(0x30, o.ToArray());
        }

        /// <summary>RFC 3416 SNMPv2-Trap-PDU: sysUpTime.0 and snmpTrapOID.0 first, then the varbinds.</summary>
        public static byte[] V2cTrap(string community, string trapOid, long upTimeHundredths, int requestId, IList<VarBind> binds)
        {
            List<byte> vbs = new List<byte>();
            vbs.AddRange(Seq(Oid("1.3.6.1.2.1.1.3.0"), UInt(upTimeHundredths, 0x43)));
            vbs.AddRange(Seq(Oid("1.3.6.1.6.3.1.1.4.1.0"), Oid(trapOid)));
            if (binds != null)
                for (int i = 0; i < binds.Count; i++)
                {
                    VarBind b = binds[i];
                    vbs.AddRange(Seq(Oid(b.Oid), b.Number ? UInt(b.Value < 0 ? 0 : b.Value, 0x42) : OctetString(b.Text)));
                }
            List<byte> pdu = new List<byte>();
            pdu.AddRange(Integer(requestId < 0 ? 0 : requestId));
            pdu.AddRange(Integer(0));
            pdu.AddRange(Integer(0));
            pdu.AddRange(Tlv(0x30, vbs.ToArray()));
            List<byte> msg = new List<byte>();
            msg.AddRange(Integer(1));                              // version: v2c
            msg.AddRange(OctetString(community));
            msg.AddRange(Tlv(0xA7, pdu.ToArray()));
            return Tlv(0x30, msg.ToArray());
        }

        /// <summary>Hands the packet to the socket for each destination; returns how many took it.
        /// A destination may be "host" or "host:port".</summary>
        public static int Send(byte[] packet, IList<string> destinations, int port, Action<string> log)
        {
            int sent = 0;
            if (destinations == null) return 0;
            for (int i = 0; i < destinations.Count; i++)
            {
                string d = (destinations[i] ?? "").Trim();
                if (d.Length == 0) continue;
                string host = d; int p = port;
                int colon = d.LastIndexOf(':');
                if (colon > 0 && d.IndexOf(':') == colon)
                {
                    int pp;
                    if (int.TryParse(d.Substring(colon + 1), out pp) && pp > 0 && pp < 65536) { host = d.Substring(0, colon); p = pp; }
                }
                UdpClient udp = null;
                try
                {
                    udp = new UdpClient();
                    udp.Send(packet, packet.Length, host, p);
                    sent++;
                }
                catch (Exception ex)
                {
                    if (log != null) log("trap to " + host + ":" + p + " failed: " + ex.Message);
                }
                finally { if (udp != null) try { udp.Close(); } catch (Exception) { } }
            }
            return sent;
        }

        public static string Hex(byte[] b)
        {
            StringBuilder sb = new StringBuilder(b.Length * 3);
            for (int i = 0; i < b.Length; i++) { if (i > 0) sb.Append(' '); sb.Append(b[i].ToString("X2")); }
            return sb.ToString();
        }
    }
}
