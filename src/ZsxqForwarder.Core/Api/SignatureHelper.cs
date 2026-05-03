using System.Security.Cryptography;
using System.Text;

namespace ZsxqForwarder.Core.Api;

public static class SignatureHelper
{
    private const string Secret = "zsxqapi2020";
    private const string AppVersion = "3.11.0";
    private const string Platform = "ios";

    public static (string Signature, long Timestamp) GenerateSignature(string path, Dictionary<string, string>? businessParams = null)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var allParams = new Dictionary<string, string>
        {
            ["app_version"] = AppVersion,
            ["platform"] = Platform,
            ["timestamp"] = timestamp.ToString()
        };

        if (businessParams != null)
        {
            foreach (var kvp in businessParams)
            {
                allParams[kvp.Key] = kvp.Value;
            }
        }

        var sortedParams = allParams
            .OrderBy(p => p.Key)
            .Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}");

        var paramsStr = string.Join("&", sortedParams);
        var signStr = $"{path}&{paramsStr}&{Secret}";

        return (Md5Hash(signStr), timestamp);
    }

    private static string Md5Hash(string input)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
