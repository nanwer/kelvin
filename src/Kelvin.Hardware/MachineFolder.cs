using System.Security.AccessControl;
using System.Security.Principal;

namespace Kelvin.Hardware;

/// <summary>
/// Locks down the folder where the service keeps its settings. Windows lets every signed-in user
/// create files in a new folder under ProgramData, and the service runs as LocalSystem and trusts
/// what it finds there, so the folder gets permissions of its own: the system and administrators
/// have full control, everyone else can only read. Anything already inside is put back under those
/// permissions, and links planted there are removed.
/// </summary>
public static class MachineFolder
{
    /// <summary>Best effort: a failure here must never stop the service from starting.</summary>
    public static bool Secure(string directory)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            const InheritanceFlags down = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

            var info = new DirectoryInfo(directory);
            // A link here would send the service's files somewhere else. Remove the link, not its target.
            if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint)) { info.Delete(); info.Refresh(); }

            var rules = new DirectorySecurity();
            rules.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            rules.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, down, PropagationFlags.None, AccessControlType.Allow));
            rules.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, down, PropagationFlags.None, AccessControlType.Allow));
            rules.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, down, PropagationFlags.None, AccessControlType.Allow));
            if (info.Exists) info.SetAccessControl(rules); else info.Create(rules);
            TakeOwnership(info, admins);

            foreach (var entry in info.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            {
                try
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) { entry.Delete(); continue; }
                    // No rules of its own: it takes exactly what the folder gives it.
                    if (entry is DirectoryInfo dir)
                    {
                        var inherit = new DirectorySecurity();
                        inherit.SetAccessRuleProtection(false, false);
                        dir.SetAccessControl(inherit);
                    }
                    else if (entry is FileInfo file)
                    {
                        var inherit = new FileSecurity();
                        inherit.SetAccessRuleProtection(false, false);
                        file.SetAccessControl(inherit);
                    }
                    TakeOwnership(entry, admins);
                }
                catch (Exception) { /* one stubborn entry mustn't stop the rest */ }
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>An owner can always rewrite permissions, so nothing in the folder stays owned by a user.</summary>
    private static void TakeOwnership(FileSystemInfo entry, SecurityIdentifier owner)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (entry is DirectoryInfo dir)
            {
                var s = new DirectorySecurity();
                s.SetOwner(owner);
                dir.SetAccessControl(s);
            }
            else if (entry is FileInfo file)
            {
                var s = new FileSecurity();
                s.SetOwner(owner);
                file.SetAccessControl(s);
            }
        }
        catch (Exception) { /* only an administrator or the system account may do this */ }
    }
}
