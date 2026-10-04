using System.Security.Cryptography;
using System.Text;
using ConeXion.Core.Models;

namespace ConeXion.Core.Services;

public sealed class CredentialStore
{
    private readonly AppPaths _paths;
    public CredentialStore(AppPaths paths) => _paths = paths;

    public void SaveSecret(string secret)
    {
        _paths.EnsureSharedDirectories();
        var plain = Encoding.UTF8.GetBytes(secret);
        try
        {
            var protectedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.LocalMachine);
            var temp = _paths.CredentialsFile + ".tmp";
            File.WriteAllBytes(temp, protectedBytes);
            File.Move(temp, _paths.CredentialsFile, true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public string LoadSecret()
    {
        if (!File.Exists(_paths.CredentialsFile)) throw new FileNotFoundException("No existen credenciales locales.");
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(_paths.CredentialsFile), null, DataProtectionScope.LocalMachine);
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public InstallationCredentials Load(InstallationConfig config) => new(config.InstallationId, LoadSecret());
    public bool Exists => File.Exists(_paths.CredentialsFile);
    public void Clear() { if (File.Exists(_paths.CredentialsFile)) File.Delete(_paths.CredentialsFile); }
}
