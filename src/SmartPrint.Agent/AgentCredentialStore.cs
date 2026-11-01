using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace SmartPrint.Agent;

public sealed class AgentCredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("QRtoPrint.Agent.Identity.v1");
    private readonly string identityPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "QRtoPrint",
        "agent.identity");

    public AgentCredentials? Load()
    {
        if (!File.Exists(identityPath))
        {
            return null;
        }

        var protectedBytes = File.ReadAllBytes(identityPath);
        var plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.LocalMachine);
        try
        {
            return JsonSerializer.Deserialize<AgentCredentials>(plainBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    public void Delete()
    {
        if (File.Exists(identityPath))
        {
            File.Delete(identityPath);
        }

        var temporaryPath = identityPath + ".tmp";
        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }
    }

    public void Save(AgentCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var directory = Path.GetDirectoryName(identityPath)
            ?? throw new InvalidOperationException("Could not determine the agent identity directory.");
        Directory.CreateDirectory(directory);
        SecurePermissions(directory);

        var plainBytes = JsonSerializer.SerializeToUtf8Bytes(credentials);
        try
        {
            var protectedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.LocalMachine);
            try
            {
                var tempPath = identityPath + ".tmp";
                File.WriteAllBytes(tempPath, protectedBytes);
                if (File.Exists(identityPath))
                {
                    File.Replace(tempPath, identityPath, null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, identityPath);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    private void SecurePermissions(string directory)
    {
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        var directorySecurity = new DirectorySecurity();
        directorySecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        directorySecurity.SetOwner(administratorsSid);
        directorySecurity.AddAccessRule(new FileSystemAccessRule(
            systemSid,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        directorySecurity.AddAccessRule(new FileSystemAccessRule(
            administratorsSid,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(directorySecurity);

        if (File.Exists(identityPath))
        {
            var fileSecurity = new FileSecurity();
            fileSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            fileSecurity.SetOwner(administratorsSid);
            fileSecurity.AddAccessRule(new FileSystemAccessRule(systemSid, FileSystemRights.FullControl, AccessControlType.Allow));
            fileSecurity.AddAccessRule(new FileSystemAccessRule(administratorsSid, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(identityPath).SetAccessControl(fileSecurity);
        }
    }
}
