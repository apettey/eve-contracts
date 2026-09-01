using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EveContracts.Core.Data;
using EveContracts.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EveContracts.Core.Esi;

/// <summary>
/// ESI SSO for a native app: OAuth2 authorization-code + PKCE via the system
/// browser and a localhost callback. Refresh tokens are DPAPI-encrypted at rest.
/// </summary>
public class EsiAuthService
{
    private const string AuthorizeUrl = "https://login.eveonline.com/v2/oauth/authorize";
    private const string TokenUrl = "https://login.eveonline.com/v2/oauth/token";
    private const string CallbackUrl = "http://localhost:8635/callback/";
    public const string Scopes = "esi-contracts.read_character_contracts.v1 esi-universe.read_structures.v1";

    private readonly IServiceScopeFactory _scopes;
    private readonly IHttpClientFactory _httpFactory;
    private readonly Services.SettingsService _settings;
    private readonly ILogger<EsiAuthService> _log;

    public EsiAuthService(IServiceScopeFactory scopes, IHttpClientFactory httpFactory,
        Services.SettingsService settings, ILogger<EsiAuthService> log)
    {
        _scopes = scopes;
        _httpFactory = httpFactory;
        _settings = settings;
        _log = log;
    }

    /// <summary>Runs the full browser login flow and stores the character. Returns the character name.</summary>
    public async Task<string> AddCharacterAsync(CancellationToken ct = default)
    {
        var clientId = _settings.EsiClientId;
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException("Set your ESI application Client ID in Settings first (developers.eveonline.com, callback http://localhost:8635/callback/).");

        var verifierBytes = RandomNumberGenerator.GetBytes(32);
        var verifier = Base64Url(verifierBytes);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        var url = $"{AuthorizeUrl}?response_type=code&redirect_uri={Uri.EscapeDataString(CallbackUrl)}" +
                  $"&client_id={Uri.EscapeDataString(clientId)}&scope={Uri.EscapeDataString(Scopes)}" +
                  $"&code_challenge={challenge}&code_challenge_method=S256&state={state}";

        using var listener = new HttpListener();
        listener.Prefixes.Add(CallbackUrl);
        listener.Start();
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });

        var ctxTask = listener.GetContextAsync();
        var done = await Task.WhenAny(ctxTask, Task.Delay(TimeSpan.FromMinutes(5), ct));
        if (done != ctxTask) throw new TimeoutException("ESI login timed out.");
        var ctx = await ctxTask;

        var code = ctx.Request.QueryString["code"];
        var gotState = ctx.Request.QueryString["state"];
        var ok = code is not null && gotState == state;
        var html = ok ? "<html><body style='font-family:sans-serif;background:#1a1817;color:#e8e4df'><h3>Authorized — you can close this tab.</h3></body></html>"
                      : "<html><body><h3>Authorization failed.</h3></body></html>";
        var buf = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html";
        await ctx.Response.OutputStream.WriteAsync(buf, ct);
        ctx.Response.Close();
        if (!ok) throw new InvalidOperationException("ESI authorization failed or was cancelled.");

        var http = _httpFactory.CreateClient("sso");
        using var tokenResp = await http.PostAsync(TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code!,
            ["client_id"] = clientId,
            ["code_verifier"] = verifier,
        }), ct);
        tokenResp.EnsureSuccessStatusCode();
        var tokens = JsonSerializer.Deserialize<TokenResponse>(await tokenResp.Content.ReadAsStringAsync(ct), EsiClient.JsonOpts)!;

        var (charId, charName) = ParseJwt(tokens.AccessToken);

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var row = await db.Characters.FindAsync([charId], ct);
        if (row is null) { row = new Character { CharacterId = charId }; db.Characters.Add(row); }
        row.Name = charName;
        row.RefreshTokenEncrypted = Protect(tokens.RefreshToken);
        row.AccessToken = tokens.AccessToken;
        row.TokenExpiry = DateTime.UtcNow.AddSeconds(tokens.ExpiresIn - 60);
        row.AuthStatus = "ok";
        await db.SaveChangesAsync(ct);
        _log.LogInformation("Authorized character {Name} ({Id})", charName, charId);
        return charName;
    }

    /// <summary>Returns a valid access token for the character, refreshing if needed. Null if re-auth required.</summary>
    public async Task<string?> GetAccessTokenAsync(int characterId, CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var row = await db.Characters.FindAsync([characterId], ct);
        if (row is null || row.AuthStatus != "ok") return null;
        if (row.TokenExpiry > DateTime.UtcNow && !string.IsNullOrEmpty(row.AccessToken)) return row.AccessToken;

        try
        {
            var http = _httpFactory.CreateClient("sso");
            using var resp = await http.PostAsync(TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = Unprotect(row.RefreshTokenEncrypted),
                ["client_id"] = _settings.EsiClientId,
            }), ct);
            if (!resp.IsSuccessStatusCode)
            {
                row.AuthStatus = "expired";
                await db.SaveChangesAsync(ct);
                _log.LogWarning("Refresh token rejected for {Name}; re-auth required", row.Name);
                return null;
            }
            var tokens = JsonSerializer.Deserialize<TokenResponse>(await resp.Content.ReadAsStringAsync(ct), EsiClient.JsonOpts)!;
            row.AccessToken = tokens.AccessToken;
            if (!string.IsNullOrEmpty(tokens.RefreshToken))
                row.RefreshTokenEncrypted = Protect(tokens.RefreshToken);
            row.TokenExpiry = DateTime.UtcNow.AddSeconds(tokens.ExpiresIn - 60);
            await db.SaveChangesAsync(ct);
            return row.AccessToken;
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning("Token refresh failed for {Name}: {Error}", row.Name, ex.Message);
            return null;
        }
    }

    private static (int id, string name) ParseJwt(string jwt)
    {
        var payload = jwt.Split('.')[1];
        payload = payload.Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
        var sub = doc.RootElement.GetProperty("sub").GetString()!; // "CHARACTER:EVE:12345"
        var id = int.Parse(sub.Split(':')[^1]);
        var name = doc.RootElement.GetProperty("name").GetString()!;
        return (id, name);
    }

    private static byte[] Protect(string s) =>
        ProtectedData.Protect(Encoding.UTF8.GetBytes(s), null, DataProtectionScope.CurrentUser);

    private static string Unprotect(byte[] b) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(b, null, DataProtectionScope.CurrentUser));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private class TokenResponse
    {
        public string AccessToken { get; set; } = "";
        public string RefreshToken { get; set; } = "";
        public int ExpiresIn { get; set; }
    }
}
