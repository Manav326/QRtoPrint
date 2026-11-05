using System.Security.Cryptography;
using static SmartPrint.Api.Security.OpaqueTokenHasher;

namespace SmartPrint.Api.Security;

public static class AgentAuthentication
{
    public static bool IsSecretValid(string suppliedSecret, string storedHash)
    {
        if (string.IsNullOrWhiteSpace(suppliedSecret) || suppliedSecret.Length > 256 ||
            string.IsNullOrWhiteSpace(storedHash))
        {
            return false;
        }

        try
        {
            var suppliedHash = Convert.FromHexString(HashOpaqueToken(suppliedSecret));
            var expectedHash = Convert.FromHexString(storedHash);
            return suppliedHash.Length == expectedHash.Length &&
                   CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}