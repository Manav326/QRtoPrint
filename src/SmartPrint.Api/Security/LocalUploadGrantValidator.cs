using System.Security.Cryptography;

namespace SmartPrint.Api.Security;

public static class LocalUploadGrantValidator
{
    public static bool IsValid(string? rawGrant, string? storedGrantHash)
    {
        if (string.IsNullOrWhiteSpace(rawGrant) ||
            rawGrant.Length is < 40 or > 128 ||
            string.IsNullOrWhiteSpace(storedGrantHash))
        {
            return false;
        }

        try
        {
            var suppliedHash = Convert.FromHexString(OpaqueTokenHasher.HashOpaqueToken(rawGrant));
            var expectedHash = Convert.FromHexString(storedGrantHash);
            return suppliedHash.Length == expectedHash.Length &&
                   CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
