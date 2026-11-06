using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using RemoteSupport.Shared;

namespace RemoteSupport.Host;

/// <summary>
/// This machine's identity key for end-to-end encryption (2026-10-07; docs/NEXT-SESSION-E2E.md, HANDOVER §112).
///
/// <para>An ECDSA P-256 key the host signs each viewer's session key with, so a console can tell THIS machine from a
/// server (or another computer) pretending to be it — and pin it (<see cref="MachinePins"/>).</para>
///
/// <list type="bullet">
/// <item><b>Only as SYSTEM.</b> Made the first time a host runs as SYSTEM (the agent's), in
/// <c>%ProgramData%\RemoteSupport\identity.key</c>, readable by SYSTEM and Administrators only. A host running as
/// the person (the tray app's, for an old agent) gets null: still encrypted, "machine identity not checked".</item>
/// <item><b>Bound to this machine</b> by DPAPI's machine scope as well as the ACL: a disk image or a copied file
/// opens nowhere else, so a clone cannot carry the identity with it.</item>
/// <item><b>An uninstall deletes the folder</b>, so a reinstall is a new identity — which the console asks about,
/// as §111 asks the office.</item>
/// </list>
/// </summary>
internal static class MachineIdentityStore
{
    private static readonly byte[] Entropy = "rsp-identity v1"u8.ToArray();
    private static string PathOf => Path.Combine(DeviceIdentity.MachineDir, "identity.key");

    /// <summary>The identity key, made if there is none — or null when this host cannot hold one.</summary>
    public static ECDsa? TryLoad()
    {
        if (!UserContext.IsSystem) { HostTrace.Write("identity: not SYSTEM — sessions are encrypted, the machine's identity is not signed"); return null; }
        try
        {
            if (File.Exists(PathOf))
            {
                var pkcs8 = ProtectedData.Unprotect(File.ReadAllBytes(PathOf), Entropy, DataProtectionScope.LocalMachine);
                var k = ECDsa.Create();
                k.ImportPkcs8PrivateKey(pkcs8, out _);
                return k;
            }
            var made = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Directory.CreateDirectory(DeviceIdentity.MachineDir);
            var tmp = PathOf + ".tmp";
            File.WriteAllBytes(tmp, ProtectedData.Protect(made.ExportPkcs8PrivateKey(), Entropy, DataProtectionScope.LocalMachine));
            LockDown(tmp);
            File.Move(tmp, PathOf, overwrite: true);
            HostTrace.Write($"identity: made this machine's identity key, {E2eHandshake.Fingerprint(E2eHandshake.PubOf(made))}");
            return made;
        }
        catch (Exception ex)
        {
            // A key that cannot be read is NOT replaced: replacing it would make every console warn that this machine
            // has changed, for a fault here. Unsigned instead, which a pinned console also asks about.
            HostTrace.Write("identity: could not use the identity key — " + ex.Message);
            return null;
        }
    }

    /// <summary>SYSTEM and Administrators, nothing inherited: the person at the machine cannot read it.</summary>
    private static void LockDown(string file)
    {
        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(file).SetAccessControl(acl);
    }
}
