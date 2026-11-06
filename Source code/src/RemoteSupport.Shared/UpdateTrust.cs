using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace RemoteSupport.Shared;

/// <summary>
/// Whether a file may be installed by an update (2026-10-16, auto-update stage B, docs/NEXT-SESSION-AUTOUPDATE.md).
///
/// <para><b>The server is not trusted to say what is safe; the signature is.</b> Whoever controlled the server could tell
/// every machine "here is an update", so a file is installed only when ALL of these hold:</para>
/// <list type="number">
/// <item>its Authenticode signature is valid — Windows' own <c>WinVerifyTrust</c>: the chain to a trusted root,
///   timestamped, nothing in it revoked (the revocation check needs the internet; a machine that cannot make it does not
///   update until it can, and says why);</item>
/// <item>the signer is US — organisation <see cref="Publisher"/> in <see cref="PublisherCountry"/>, read as fields of the
///   certificate's subject, never as a substring. Not one certificate's thumbprint: the second certificate of the term
///   (from November 2027) must keep working. And not "signed by anyone": any company can buy a certificate;</item>
/// <item>for a program, it is a STAMPED build newer than what is installed (<see cref="AppVersion"/>) — a compromised
///   server could otherwise push an OLD, genuinely signed build with a known fault. The only way back to an older version
///   is the machine's own rollback, never the server.</item>
/// </list>
///
/// <para>The signer is taken from the verification itself (the provider's signer chain), so a script's signature — a
/// comment block, which no certificate loader reads — is checked exactly like a program's.</para>
///
/// <para><b>There is no test mode.</b> A switch that relaxed any of this would be a back door into every machine.
/// tools/updatetrust proves the refusals with real files: unsigned, signed by somebody else (Microsoft), one byte changed,
/// older, and unstamped.</para>
/// </summary>
public static class UpdateTrust
{
    /// <summary>The organisation on our code-signing certificate (Sectigo OV, docs/CODE-SIGNING.md).</summary>
    public const string Publisher = "The Remote Support Program (Pty) Ltd";
    public const string PublisherCountry = "ZA";

    /// <summary>Yes or no, with the reason in words a log or the dashboard can show, and the file's version when it has one.</summary>
    public sealed record Verdict(bool Ok, string Why, string Version = "")
    {
        public static Verdict No(string why, string version = "") => new(false, why, version);
        /// <summary>
        /// Not a "no" about the FILE but about this moment: whether the certificate was revoked could not be checked (no
        /// internet, or the certificate authority blocked). The file is not refused for ever — the update tries again later.
        /// </summary>
        public bool TryLater { get; init; }
    }

    /// <summary>Signed, the signature valid, and the signer us. Says nothing about the version.</summary>
    public static Verdict Signature(string path)
    {
        if (!OperatingSystem.IsWindows()) return Verdict.No("only Windows can check a signature");
        if (!File.Exists(path)) return Verdict.No($"{Path.GetFileName(path)} is not there");
        try
        {
            var (code, signer) = Verify(path);
            if (code is 0x800B010E or 0x80092013)   // CERT_E_REVOCATION_FAILURE, CRYPT_E_REVOCATION_OFFLINE: could not ask
                return Verdict.No($"{Path.GetFileName(path)}: {Describe(code)}") with { TryLater = true };
            if (code != 0) return Verdict.No($"{Path.GetFileName(path)}: {Describe(code)}");
            if (signer == null) return Verdict.No($"{Path.GetFileName(path)}: no signer could be read from its signature");
            using (signer)
            {
                var (org, country) = Fields(signer.SubjectName);
                if (org != Publisher || country != PublisherCountry)
                    return Verdict.No($"{Path.GetFileName(path)} is signed by \"{org}\" ({country}), not by us");
            }
            return new Verdict(true, "signed by us");
        }
        catch (Exception ex) { return Verdict.No($"{Path.GetFileName(path)}: the signature could not be checked ({ex.Message})"); }
    }

    /// <summary>
    /// A program an update may install: <see cref="Signature"/>, a stamped build, NEWER than <paramref name="installed"/>,
    /// and — when given — exactly <paramref name="expected"/> (every program of one release carries the release's version).
    /// </summary>
    public static Verdict Accept(string path, string installed, string? expected = null)
    {
        var sig = Signature(path);
        if (!sig.Ok) return sig;
        string version;
        try { version = FileVersion(path); }
        catch (Exception ex) { return Verdict.No($"{Path.GetFileName(path)}: its version could not be read ({ex.Message})"); }
        if (!AppVersion.IsStamped(version)) return Verdict.No($"{Path.GetFileName(path)} is not a released build (version \"{version}\")", version);
        if (expected != null && AppVersion.Compare(version, expected) != 0)
            return Verdict.No($"{Path.GetFileName(path)} is version {version}, not the {expected} it was offered as", version);
        if (AppVersion.Compare(version, installed) <= 0)
            return Verdict.No($"{Path.GetFileName(path)} is version {version}, not newer than the {AppVersion.Label(installed)} installed", version);
        return new Verdict(true, "signed by us, and newer", version);
    }

    /// <summary>A program's version as stamped in its signed version resource: four numbers, or "" when it has none.</summary>
    public static string FileVersion(string path)
    {
        var fv = FileVersionInfo.GetVersionInfo(path);
        return new Version(fv.FileMajorPart, fv.FileMinorPart, fv.FileBuildPart, fv.FilePrivatePart) is { } v && v != new Version(0, 0, 0, 0)
            ? v.ToString() : "";
    }

    /// <summary>
    /// The version stamped in the file AT <paramref name="path"/> now, read from its bytes — for a program asking about
    /// its OWN path (the tray app, after the agent has swapped a newer copy in under it).
    ///
    /// <para><b>Why not <see cref="FileVersion"/></b> (measured 2026-10-19, DESKTOP-2N6FVGT and this PC): Windows answers
    /// <c>GetFileVersionInfo</c> from a module already loaded at that path, and a running program that has been renamed
    /// aside still goes by its ORIGINAL path (which is what lets the swap work at all). So the running tray app asked about
    /// "RemoteSupport.Client.exe" and was told its own old version — never anything newer — and stayed on the build it
    /// started with for three days, through four updates of everything else. Reading the file itself cannot be answered
    /// from memory.</para>
    ///
    /// <para>Only the PE headers, the section table and the version resource are read: the fixed-size VS_FIXEDFILEINFO
    /// that <see cref="FileVersion"/> also reports. "" when the file has none or is not a program.</para>
    /// </summary>
    public static string FileVersionOnDisk(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var br = new BinaryReader(fs);
        long len = fs.Length;
        uint U32(long at) { if (at < 0 || at + 4 > len) throw new InvalidDataException("past the end"); fs.Position = at; return br.ReadUInt32(); }
        ushort U16(long at) { if (at < 0 || at + 2 > len) throw new InvalidDataException("past the end"); fs.Position = at; return br.ReadUInt16(); }

        if (len < 0x40 || U16(0) != 0x5A4D) return "";                        // "MZ"
        long pe = U32(0x3C);
        if (U32(pe) != 0x00004550) return "";                                   // "PE\0\0"
        int sections = U16(pe + 6), optSize = U16(pe + 20);
        long opt = pe + 24;
        int dirs = U16(opt) switch { 0x20B => 112, 0x10B => 96, _ => -1 };     // PE32+ / PE32: where the data directories start
        if (dirs < 0 || U32(opt + dirs - 4) < 3) return "";
        uint rsrcRva = U32(opt + dirs + 2 * 8);                                 // directory 2: resources
        if (rsrcRva == 0) return "";

        var table = new List<(uint va, uint size, uint raw)>();
        for (int i = 0; i < sections; i++)
        {
            long s = opt + optSize + i * 40L;
            uint vsize = U32(s + 8), va = U32(s + 12), rawSize = U32(s + 16), raw = U32(s + 20);
            table.Add((va, Math.Max(vsize, rawSize), raw));
        }
        long Offset(uint rva)
        {
            foreach (var (va, size, raw) in table) if (rva >= va && rva < va + size) return raw + (rva - va);
            throw new InvalidDataException("an address outside every section");
        }

        long root = Offset(rsrcRva);
        // A resource directory: 16 bytes, then its named entries and its numbered entries, 8 bytes each. An entry's
        // second half is an offset from the root; its top bit says it is another directory rather than the data.
        long? Entry(long dir, uint? id)
        {
            int named = U16(dir + 12), numbered = U16(dir + 14);
            for (int i = 0; i < named + numbered; i++)
            {
                long e = dir + 16 + i * 8L;
                if (id == null || (i >= named && U32(e) == id)) return root + (U32(e + 4) & 0x7FFFFFFF);
            }
            return null;
        }
        if (Entry(root, 16) is not { } byType) return "";                       // RT_VERSION
        if (Entry(byType, null) is not { } byName) return "";                   // the first (VS_VERSION_INFO)
        if (Entry(byName, null) is not { } data) return "";                     // the first language: the data entry
        long at = Offset(U32(data)), size = Math.Min(U32(data + 4), 64 * 1024);
        for (long p = at; p + 16 <= at + size; p += 4)
            if (U32(p) == 0xFEEF04BD)                                           // VS_FIXEDFILEINFO's signature
            {
                uint ms = U32(p + 8), ls = U32(p + 12);
                var v = new Version((int)(ms >> 16), (int)(ms & 0xFFFF), (int)(ls >> 16), (int)(ls & 0xFFFF));
                return v != new Version(0, 0, 0, 0) ? v.ToString() : "";
            }
        return "";
    }

    /// <summary>The organisation and country of a subject, as fields — so "O=Us, O=Them" or a CN that says our name cannot pass.</summary>
    private static (string org, string country) Fields(X500DistinguishedName name)
    {
        var orgs = new List<string>(); var countries = new List<string>();
        foreach (var rdn in name.EnumerateRelativeDistinguishedNames())
        {
            var oid = rdn.GetSingleElementType().Value;
            var value = rdn.GetSingleElementValue() ?? "";
            if (oid == "2.5.4.10") orgs.Add(value);
            else if (oid == "2.5.4.6") countries.Add(value);
        }
        // Exactly one of each, or it is not a subject we issued ourselves a certificate under.
        return (orgs.Count == 1 ? orgs[0] : string.Join(" + ", orgs), countries.Count == 1 ? countries[0] : string.Join(" + ", countries));
    }

    private static string Describe(uint code) => code switch
    {
        0x800B0100 => "it is not signed",                                         // TRUST_E_NOSIGNATURE
        0x80096010 => "its signature does not match its contents — it has been changed",   // TRUST_E_BAD_DIGEST
        0x800B0109 => "its signature does not lead to a trusted root",              // CERT_E_UNTRUSTEDROOT
        0x800B010A => "its signature's chain is incomplete",                        // CERT_E_CHAINING
        0x800B010C => "its certificate has been REVOKED",                           // CERT_E_REVOKED
        0x800B010E => "whether its certificate was revoked could not be checked",    // CERT_E_REVOCATION_FAILURE
        0x80092013 => "whether its certificate was revoked could not be checked (offline)", // CRYPT_E_REVOCATION_OFFLINE
        0x800B0101 => "its certificate had expired when it was signed, and it has no timestamp", // CERT_E_EXPIRED
        0x800B0111 => "its publisher is explicitly distrusted on this machine",     // TRUST_E_EXPLICIT_DISTRUST
        0x80096019 => "its signature is malformed",                                 // TRUST_E_BASIC_CONSTRAINTS (and kin)
        0x800B0004 => "it is not signed in a way Windows recognises",               // TRUST_E_SUBJECT_NOT_TRUSTED
        _ => $"its signature is not valid (0x{code:X8})"
    };

    // ------------------------------------------------------------------ WinVerifyTrust, by hand

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    /// <summary>The verification's answer (0 is trusted) and, when it had one, the signer's certificate.</summary>
    private static (uint code, X509Certificate2? signer) Verify(string path)
    {
        var file = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        var pData = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
        var action = GenericVerifyV2;
        try
        {
            Marshal.StructureToPtr(file, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,                 // WTD_UI_NONE
                fdwRevocationChecks = 1,        // WTD_REVOKE_WHOLECHAIN
                dwUnionChoice = 1,              // WTD_CHOICE_FILE
                pFile = pFile,
                dwStateAction = 1,              // WTD_STATEACTION_VERIFY — keeps the state, so the signer can be read
                dwProvFlags = 0x80,             // WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT
            };
            Marshal.StructureToPtr(data, pData, false);
            uint code = unchecked((uint)WinVerifyTrust(new IntPtr(-1), ref action, pData));
            X509Certificate2? signer = null;
            try
            {
                var state = Marshal.PtrToStructure<WINTRUST_DATA>(pData).hWVTStateData;
                var prov = state == IntPtr.Zero ? IntPtr.Zero : WTHelperProvDataFromStateData(state);
                var sgnr = prov == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvSignerFromChain(prov, 0, false, 0);
                if (sgnr != IntPtr.Zero)
                {
                    // CRYPT_PROVIDER_SGNR: cbStruct (4), sftVerifyAsOf (FILETIME, 8), csCertChain (4), pasCertChain (pointer).
                    int count = Marshal.ReadInt32(sgnr, 12);
                    var chain = Marshal.ReadIntPtr(sgnr, 16);   // 4 + 8 + 4 on both widths
                    // CRYPT_PROVIDER_CERT: cbStruct (4, padded to the pointer), pCert — the leaf is element 0.
                    var ctx = count > 0 && chain != IntPtr.Zero ? Marshal.ReadIntPtr(chain, IntPtr.Size) : IntPtr.Zero;
                    if (ctx != IntPtr.Zero) signer = new X509Certificate2(ctx);   // duplicates the context; ours to dispose
                }
            }
            finally
            {
                var closing = Marshal.PtrToStructure<WINTRUST_DATA>(pData);
                closing.dwStateAction = 2;      // WTD_STATEACTION_CLOSE
                Marshal.StructureToPtr(closing, pData, false);
                WinVerifyTrust(new IntPtr(-1), ref action, pData);
            }
            return (code, signer);
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
            Marshal.FreeHGlobal(pData);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, IntPtr pWVTData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr hStateData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr pProvData, uint idxSigner,
        [MarshalAs(UnmanagedType.Bool)] bool fCounterSigner, uint idxCounterSigner);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
