using System.Text.Json;

namespace ZsxqForwarder.Core.Services;

public class AuthService
{
    private const string TokenFile = "auth_token.json";
    private AuthData? _authData;

    public string? AccessToken => _authData?.AccessToken;
    public bool IsLoggedIn => !string.IsNullOrEmpty(_authData?.AccessToken);
    public string? UserName => _authData?.UserName;

    public void SaveToken(string accessToken, string? userName = null)
    {
        _authData = new AuthData
        {
            AccessToken = accessToken,
            UserName = userName,
            LoginTime = DateTime.Now
        };

        var json = JsonSerializer.Serialize(_authData, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(TokenFile, json);
    }

    public bool LoadToken()
    {
        if (!File.Exists(TokenFile))
            return false;

        try
        {
            var json = File.ReadAllText(TokenFile);
            _authData = JsonSerializer.Deserialize<AuthData>(json);
            return IsLoggedIn;
        }
        catch
        {
            return false;
        }
    }

    public void Logout()
    {
        _authData = null;
        if (File.Exists(TokenFile))
        {
            File.Delete(TokenFile);
        }
    }

    private class AuthData
    {
        public string AccessToken { get; set; } = string.Empty;
        public string? UserName { get; set; }
        public DateTime LoginTime { get; set; }
    }
}
