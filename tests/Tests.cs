using System;
using System.Net;
using System.Text;
using SharpADIDNS;

internal sealed class TestRunner
{
    private static int failed;
    private static int passed;

    public static int Main(string[] args)
    {
        Console.WriteLine("SharpADIDNS unit tests");
        Console.WriteLine();

        Run("Bin LE round-trip",                    TestBinLeRoundTrip);
        Run("Bin BE round-trip",                    TestBinBeRoundTrip);
        Run("Header constants (version/rank/etc.)", TestHeaderConstants);
        Run("TTL endianness (big-endian)",          TestTtlEndianness);
        Run("BuildA round-trip",                    TestBuildA);
        Run("BuildAaaa round-trip",                 TestBuildAaaa);
        Run("BuildCname round-trip",                TestBuildCname);
        Run("BuildTxt round-trip + 255 boundary",   TestBuildTxt);
        Run("BuildPtr round-trip",                  TestBuildPtr);
        Run("BuildSrv round-trip",                  TestBuildSrv);
        Run("BuildMx round-trip",                   TestBuildMx);
        Run("BuildTombstone format",                TestBuildTombstone);
        Run("DNS_COUNT_NAME 63-byte label",         TestCountName63);
        Run("DNS_COUNT_NAME multi-label",           TestCountNameMulti);
        Run("DNS_COUNT_NAME empty/oversize reject", TestCountNameRejected);
        Run("Json.Escape control chars",            TestJsonEscape);

        Console.WriteLine();
        if (failed == 0)
            Console.WriteLine("[+] {0} passed, 0 failed", passed);
        else
            Console.WriteLine("[-] {0} passed, {1} FAILED", passed, failed);
        return failed == 0 ? 0 : 1;
    }

    // ---------------- runner / asserts ----------------

    private static void Run(string name, Action body)
    {
        try
        {
            body();
            passed++;
            Console.WriteLine("  ok    {0}", name);
        }
        catch (Exception ex)
        {
            failed++;
            Console.WriteLine("  FAIL  {0}: {1}", name, ex.Message);
        }
    }

    private static void AssertEq<T>(T expected, T actual, string what)
    {
        if (!object.Equals(expected, actual))
            throw new Exception(string.Format(
                "{0}: expected {1}, got {2}", what, expected, actual));
    }

    private static void AssertByte(byte expected, byte actual, string what)
    {
        if (expected != actual)
            throw new Exception(string.Format(
                "{0}: expected 0x{1:X2}, got 0x{2:X2}", what, expected, actual));
    }

    private static void AssertThrows<TException>(Action body, string what)
        where TException : Exception
    {
        try { body(); }
        catch (TException) { return; }
        catch (Exception ex)
        {
            throw new Exception(string.Format(
                "{0}: expected {1}, got {2}", what, typeof(TException).Name, ex.GetType().Name));
        }
        throw new Exception(string.Format("{0}: did not throw", what));
    }

    // ---------------- tests ----------------

    private static void TestBinLeRoundTrip()
    {
        byte[] b = new byte[4];
        Bin.WriteU16Le(b, 0, 0xCAFE);
        AssertByte(0xFE, b[0], "U16Le[0]");
        AssertByte(0xCA, b[1], "U16Le[1]");
        AssertEq((ushort)0xCAFE, Bin.ReadU16Le(b, 0), "ReadU16Le");

        byte[] b2 = new byte[4];
        Bin.WriteU32Le(b2, 0, 0xDEADBEEF);
        AssertByte(0xEF, b2[0], "U32Le[0]");
        AssertByte(0xBE, b2[1], "U32Le[1]");
        AssertByte(0xAD, b2[2], "U32Le[2]");
        AssertByte(0xDE, b2[3], "U32Le[3]");
        AssertEq(0xDEADBEEFu, Bin.ReadU32Le(b2, 0), "ReadU32Le");
    }

    private static void TestBinBeRoundTrip()
    {
        byte[] b = new byte[4];
        Bin.WriteU16Be(b, 0, 0xCAFE);
        AssertByte(0xCA, b[0], "U16Be[0]");
        AssertByte(0xFE, b[1], "U16Be[1]");
        AssertEq((ushort)0xCAFE, Bin.ReadU16Be(b, 0), "ReadU16Be");

        byte[] b2 = new byte[4];
        Bin.WriteU32Be(b2, 0, 0xDEADBEEF);
        AssertByte(0xDE, b2[0], "U32Be[0]");
        AssertByte(0xAD, b2[1], "U32Be[1]");
        AssertByte(0xBE, b2[2], "U32Be[2]");
        AssertByte(0xEF, b2[3], "U32Be[3]");
        AssertEq(0xDEADBEEFu, Bin.ReadU32Be(b2, 0), "ReadU32Be");
    }

    private static void TestHeaderConstants()
    {
        byte[] rec = DnsRecord.BuildA(IPAddress.Parse("1.2.3.4"), 600);
        AssertByte(0x05, rec[4],                              "Version");
        AssertByte(0xF0, rec[5],                              "Rank == DNS_RANK_ZONE");
        AssertEq((ushort)0, Bin.ReadU16Le(rec, 6),            "Flags");
        AssertEq(1u,        Bin.ReadU32Le(rec, 8),            "Serial");
        AssertEq(0u,        Bin.ReadU32Le(rec, 20),           "Timestamp (0 = static)");
    }

    private static void TestTtlEndianness()
    {
        byte[] rec = DnsRecord.BuildA(IPAddress.Parse("1.2.3.4"), 0x12345678);
        AssertByte(0x12, rec[12], "TTL[0]");
        AssertByte(0x34, rec[13], "TTL[1]");
        AssertByte(0x56, rec[14], "TTL[2]");
        AssertByte(0x78, rec[15], "TTL[3]");
    }

    private static void TestBuildA()
    {
        byte[] rec = DnsRecord.BuildA(IPAddress.Parse("10.0.0.66"), 600);
        AssertEq(28,                  rec.Length,               "rec.Length");
        AssertEq(DnsRecord.TypeA,     DnsRecord.GetType(rec),   "type");
        AssertEq((ushort)4,           Bin.ReadU16Le(rec, 0),    "DataLength");
        AssertEq(600u,                Bin.ReadU32Be(rec, 12),   "TTL");
        AssertByte(10, rec[24], "A[0]");
        AssertByte(0,  rec[25], "A[1]");
        AssertByte(0,  rec[26], "A[2]");
        AssertByte(66, rec[27], "A[3]");
    }

    private static void TestBuildAaaa()
    {
        byte[] rec = DnsRecord.BuildAaaa(IPAddress.Parse("fe80::1"), 1200);
        AssertEq(40,                  rec.Length,               "rec.Length");
        AssertEq(DnsRecord.TypeAaaa,  DnsRecord.GetType(rec),   "type");
        AssertEq((ushort)16,          Bin.ReadU16Le(rec, 0),    "DataLength");
        AssertEq(1200u,               Bin.ReadU32Be(rec, 12),   "TTL");
        AssertByte(0xFE, rec[24], "AAAA[0]");
        AssertByte(0x80, rec[25], "AAAA[1]");
        AssertByte(0x00, rec[26], "AAAA[2]");
    }

    private static void TestBuildCname()
    {
        byte[] rec = DnsRecord.BuildCname("foo.bar.example", 300);
        AssertEq(DnsRecord.TypeCname, DnsRecord.GetType(rec),   "type");
        AssertEq("foo.bar.example",   DnsRecord.DecodeCountName(rec, 24), "decoded");
    }

    private static void TestBuildTxt()
    {
        byte[] rec = DnsRecord.BuildTxt("hello world", 600);
        AssertEq(DnsRecord.TypeTxt, DnsRecord.GetType(rec), "type");
        ushort len = Bin.ReadU16Le(rec, 0);
        AssertEq("hello world", DnsRecord.DecodeTxt(rec, 24, len), "decoded");

        byte[] rec2 = DnsRecord.BuildTxt("", 600);
        ushort len2 = Bin.ReadU16Le(rec2, 0);
        AssertEq("", DnsRecord.DecodeTxt(rec2, 24, len2), "empty round-trip");

        string s255 = new string('a', 255);
        byte[] rec3 = DnsRecord.BuildTxt(s255, 600);
        ushort len3 = Bin.ReadU16Le(rec3, 0);
        AssertEq(s255, DnsRecord.DecodeTxt(rec3, 24, len3), "255-byte round-trip");

        AssertThrows<ArgumentException>(
            delegate { DnsRecord.BuildTxt(new string('a', 256), 600); },
            "256-byte TXT must throw");
    }

    private static void TestBuildPtr()
    {
        byte[] rec = DnsRecord.BuildPtr("host.corp.local", 600);
        AssertEq(DnsRecord.TypePtr,   DnsRecord.GetType(rec),                "type");
        AssertEq("host.corp.local",   DnsRecord.DecodeCountName(rec, 24),    "decoded");
    }

    private static void TestBuildSrv()
    {
        byte[] rec = DnsRecord.BuildSrv(10, 50, 389, "attacker.corp.local", 600);
        AssertEq(DnsRecord.TypeSrv,    DnsRecord.GetType(rec),               "type");
        AssertEq((ushort)10,           Bin.ReadU16Be(rec, 24),               "priority");
        AssertEq((ushort)50,           Bin.ReadU16Be(rec, 26),               "weight");
        AssertEq((ushort)389,          Bin.ReadU16Be(rec, 28),               "port");
        AssertEq("attacker.corp.local",DnsRecord.DecodeCountName(rec, 30),   "target");
    }

    private static void TestBuildMx()
    {
        byte[] rec = DnsRecord.BuildMx(20, "mail.corp.local", 600);
        AssertEq(DnsRecord.TypeMx,    DnsRecord.GetType(rec),                "type");
        AssertEq((ushort)20,          Bin.ReadU16Be(rec, 24),                "preference");
        AssertEq("mail.corp.local",   DnsRecord.DecodeCountName(rec, 26),    "exchange");
    }

    private static void TestBuildTombstone()
    {
        byte[] rec = DnsRecord.BuildTombstone();
        AssertEq(DnsRecord.TypeZero, DnsRecord.GetType(rec), "type");
        AssertEq(32, rec.Length, "rec.Length");
        AssertEq((ushort)8, Bin.ReadU16Le(rec, 0), "DataLength");
        ulong ft = Bin.ReadU64Le(rec, 24);
        DateTime dt = DateTime.FromFileTimeUtc((long)ft);
        double secs = Math.Abs((DateTime.UtcNow - dt).TotalSeconds);
        if (secs > 30.0) throw new Exception("FILETIME diff too large: " + secs + "s");
    }

    private static void TestCountName63()
    {
        string label = new string('a', 63);
        byte[] rec = DnsRecord.BuildCname(label, 600);
        AssertEq(label, DnsRecord.DecodeCountName(rec, 24), "63-byte round-trip");
    }

    private static void TestCountNameMulti()
    {
        byte[] rec = DnsRecord.BuildCname("a.b.c.d.example.com", 600);
        AssertEq("a.b.c.d.example.com", DnsRecord.DecodeCountName(rec, 24), "multi-label");
    }

    private static void TestCountNameRejected()
    {
        AssertThrows<ArgumentException>(
            delegate { DnsRecord.BuildCname("foo..bar", 600); },
            "empty label inside name");

        AssertThrows<ArgumentException>(
            delegate { DnsRecord.BuildCname(new string('a', 64), 600); },
            "64-byte label too long");
    }

    private static void TestJsonEscape()
    {
        AssertEq("hello", Json.Escape("hello"), "plain");
        AssertEq("a\\\"b", Json.Escape("a\"b"), "escape quote");
        AssertEq("a\\\\b", Json.Escape("a\\b"), "escape backslash");
        AssertEq("a\\nb",  Json.Escape("a\nb"), "escape LF");
        AssertEq("a\\u0001b", Json.Escape("ab"), "escape control");
    }
}
