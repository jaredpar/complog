using MessagePack.Formatters;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Basic.CompilerLog.UnitTests;

internal sealed class ReadOnlyDirectoryScope : IDisposable
{
    public string DirectoryPath { get; }
    public bool IsReadOnly { get; private set; }
    private readonly SecurityIdentifier? _userSid;
    private readonly FileSystemAccessRule? _denyRule;

    public ReadOnlyDirectoryScope(string directoryPath, bool setReadOnly)
    {
        DirectoryPath = directoryPath;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _userSid = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("Could not determine current Windows user SID.");

            // Deny "write-ish" operations. This makes tests fail if they try to write/modify/delete.
            const FileSystemRights denyRights =
                FileSystemRights.CreateFiles |
                FileSystemRights.WriteData |
                FileSystemRights.AppendData |
                FileSystemRights.WriteAttributes |
                FileSystemRights.WriteExtendedAttributes |
                FileSystemRights.Delete |
                FileSystemRights.DeleteSubdirectoriesAndFiles |
                FileSystemRights.Write |                 // umbrella
                FileSystemRights.ChangePermissions |
                FileSystemRights.TakeOwnership;

            _denyRule = new FileSystemAccessRule(
                _userSid,
                denyRights,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Deny);
        }

        if (setReadOnly)
        {
            SetReadOnly();
        }
    }

    public void Dispose()
    {
        if (IsReadOnly)
        {
            ClearReadOnly();
        }
    }

    /// <summary>
    /// Make directory and all files under it effectively read-only by enforcing NTFS ACLs.
    /// </summary>
    public void SetReadOnly()
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException();
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Debug.Assert(_denyRule is not null);
            Debug.Assert(_userSid is not null);

            SetReadOnlyAttributesRecursively();

            var directoryInfo = new DirectoryInfo(DirectoryPath);
            var security = directoryInfo.GetAccessControl(AccessControlSections.Access);

            // If you're worried about inherited permissions re-granting write via groups, you can protect the ACL.
            // This keeps inheritance, but makes it explicit.
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);

            security.AddAccessRule(_denyRule);
            directoryInfo.SetAccessControl(security);

            void SetReadOnlyAttributesRecursively()
            {
                foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*", SearchOption.AllDirectories))
                {
                    var attr = File.GetAttributes(file);
                    File.SetAttributes(file, attr | FileAttributes.ReadOnly);
                }
            }
        }

        IsReadOnly = true;
    }

    /// <summary>
    /// Remove the lockdown so the directory can be deleted.
    /// </summary>
    public void ClearReadOnly()
    {
        if (!IsReadOnly)
        {
            throw new InvalidOperationException();
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Debug.Assert(_denyRule is not null);
            Debug.Assert(_userSid is not null);

            var directoryInfo = new DirectoryInfo(DirectoryPath);
            var security = directoryInfo.GetAccessControl(AccessControlSections.Access);

            // Must match the rule to remove it.
            security.RemoveAccessRule(_denyRule);
            directoryInfo.SetAccessControl(security);

            // Also clear ReadOnly attributes on files, if any tooling set them.
            ClearReadOnlyAttributesRecursively();
        }

        IsReadOnly = false;

        void ClearReadOnlyAttributesRecursively()
        {
            foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*", SearchOption.AllDirectories))
            {
                var attr = File.GetAttributes(file);
                if ((attr & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(file, attr & ~FileAttributes.ReadOnly);
            }
        }
    }
}
