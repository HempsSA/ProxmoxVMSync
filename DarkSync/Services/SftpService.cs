using System.IO;
using Renci.SshNet;

namespace DarkSync.Services;

public static class SftpService
{
    public static (string Host, int Port, string User, string Root) ParseUri(string uri)
    {
        var u = new Uri(uri);
        if (!u.Scheme.Equals("sftp", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(u.Host))
            throw new ArgumentException("Use sftp://user@host:22/path");

        var host = u.Host;
        var port = u.Port > 0 ? u.Port : 22;
        var user = Uri.UnescapeDataString(u.UserInfo ?? "");
        var root = Uri.UnescapeDataString(u.AbsolutePath ?? "/");

        return (host, port, user, root);
    }

    public static ConnectionInfo BuildConnectionInfo(string host, int port, string user, string password, string keyFile = "")
    {
        var auth = new List<AuthenticationMethod>();
        if (!string.IsNullOrEmpty(keyFile) && File.Exists(keyFile))
            auth.Add(new PrivateKeyAuthenticationMethod(user, new PrivateKeyFile(keyFile)));
        if (!string.IsNullOrEmpty(password))
            auth.Add(new PasswordAuthenticationMethod(user, password));
        if (auth.Count == 0)
            auth.Add(new PasswordAuthenticationMethod(user, password));

        var ci = new ConnectionInfo(host, port, user, auth.ToArray());
        ci.Timeout = TimeSpan.FromSeconds(20);
        return ci;
    }

    public static SshClient CreateClient(string host, int port, string user, string password, string keyFile = "")
        => new SshClient(BuildConnectionInfo(host, port, user, password, keyFile));

    public static SftpClient CreateSftpClient(string host, int port, string user, string password, string keyFile = "")
        => new SftpClient(BuildConnectionInfo(host, port, user, password, keyFile));
}
