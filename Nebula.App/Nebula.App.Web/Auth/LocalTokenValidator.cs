using System.Security.Cryptography;
using System.Text;

namespace Nebula.App.Web.Auth;

public static class LocalTokenValidator
{
    public static bool IsValid(string? expected, string? provided)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(provided))
        {
            return false;
        }

        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        return CryptographicOperations.FixedTimeEquals(expectedHash, providedHash);
    }
}
