using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DarkSync.Services;

public static class CredentialService
{
    private static string CredentialPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DarkSync", "sftp_credentials.bin");

    public static void SavePassword(string password)
    {
        try
        {
            var dir = Path.GetDirectoryName(CredentialPath)!;
            Directory.CreateDirectory(dir);

            var entropy = Encoding.UTF8.GetBytes("DarkSyncProxmoxArchive");
            var plainBytes = Encoding.UTF8.GetBytes(password);
            var encrypted = ProtectedData.Protect(plainBytes, entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(CredentialPath, encrypted);
        }
        catch { }
    }

    public static string? LoadPassword()
    {
        if (!File.Exists(CredentialPath)) return null;

        try
        {
            var encrypted = File.ReadAllBytes(CredentialPath);
            var entropy = Encoding.UTF8.GetBytes("DarkSyncProxmoxArchive");
            var plainBytes = ProtectedData.Unprotect(encrypted, entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return null;
        }
    }

    public static void DeletePassword()
    {
        try { File.Delete(CredentialPath); } catch { }
    }
}
