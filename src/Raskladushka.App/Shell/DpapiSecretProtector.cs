using System.Security.Cryptography;
using System.Text;
using Raskladushka.Core.Dictation;

namespace Raskladushka.App.Shell;

public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Raskladushka.VoiceServer");

    public string Protect(string plain)
    {
        var bytes = Encoding.UTF8.GetBytes(plain);
        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception) { throw new CryptographicException("Не удалось зашифровать ключ."); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public string? Unprotect(string protectedValue)
    {
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception) { return null; }
    }
}
