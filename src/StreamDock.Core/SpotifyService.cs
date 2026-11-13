using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StreamDock.Core;

public interface ISecretStore
{
    string? Read();
    void Write(string value);
    void Delete();
}
public sealed record SpotifyToken(string AccessToken, string RefreshToken, DateTimeOffset Expires, string ClientId);

public sealed class SpotifyService : IDisposable
{
    public const string RedirectUri = "http://127.0.0.1:43829/callback/";
    private readonly HttpClient http;
    private readonly ISecretStore secrets;
    private readonly SemaphoreSlim tokenLock = new(1, 1);
    private SpotifyToken? token;
    private string clientId;
    public bool Connected => token is not null && token.ClientId == clientId;
    public SpotifyService(HttpClient http, ISecretStore secrets, string clientId)
    {
        this.http = http; this.secrets = secrets; this.clientId = clientId.Trim();
        try { token = JsonSerializer.Deserialize<SpotifyToken>(secrets.Read() ?? "null"); }
        catch (Exception ex) when (ex is JsonException or CryptographicException or IOException or UnauthorizedAccessException) { token = null; }
    }
    public void SetClientId(string value)
    {
        value = value.Trim();
        if (value == clientId) return;
        Disconnect(); clientId = value;
    }
    public static (string Verifier, string Challenge, string State) CreatePkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        return (verifier, Challenge(verifier), Base64Url(RandomNumberGenerator.GetBytes(32)));
    }
    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public async Task ConnectAsync(Action<string> openBrowser, CancellationToken cancellation)
    {
        if (clientId.Length != 32 || !clientId.All(Uri.IsHexDigit)) throw new InvalidOperationException("Укажите Client ID приложения из Spotify Developer Dashboard (32 символа) в настройках. Client Secret не нужен.");
        var pkce = CreatePkce();
        using var listener = new HttpListener(); listener.Prefixes.Add(RedirectUri);
        try { listener.Start(); }
        catch (HttpListenerException ex) { throw new InvalidOperationException("Не удалось открыть локальный адрес Spotify. Проверьте, свободен ли порт 43829.", ex); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var url = "https://accounts.spotify.com/authorize?" + FormQuery(new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["response_type"] = "code", ["redirect_uri"] = RedirectUri,
            ["scope"] = "user-read-currently-playing", ["code_challenge_method"] = "S256", ["code_challenge"] = pkce.Challenge, ["state"] = pkce.State
        });
        openBrowser(url);
        string? code = null;
        while (code is null)
        {
            var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
            var query = context.Request.QueryString;
            var valid = query["state"] == pkce.State;
            var denied = query["error"];
            code = valid ? query["code"] : null;
            var text = valid && code is not null ? "<h2>Spotify подключён</h2><p>Можно закрыть вкладку и вернуться в StreamDock.</p>" : "<h2>Вход не завершён</h2><p>Вернитесь в StreamDock и попробуйте ещё раз.</p>";
            var bytes = Encoding.UTF8.GetBytes("<!doctype html><meta charset=utf-8><title>StreamDock</title><body style='background:#10141e;color:#e9eef6;font:18px system-ui;padding:64px'>" + text);
            context.Response.ContentType = "text/html; charset=utf-8"; context.Response.StatusCode = valid ? 200 : 400;
            await context.Response.OutputStream.WriteAsync(bytes, timeout.Token); context.Response.Close();
            if (valid && denied is not null) throw new InvalidOperationException("Вход в Spotify отменён.");
        }
        var fields = new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = RedirectUri, ["client_id"] = clientId, ["code_verifier"] = pkce.Verifier };
        token = await Exchange(fields, null, timeout.Token); Save();
    }
    public async Task<SpotifyTrack?> CurrentTrackAsync(CancellationToken cancellation)
    {
        var access = await AccessToken(cancellation);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me/player/currently-playing");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            using var response = await http.SendAsync(request, cancellation);
            if (response.StatusCode == HttpStatusCode.NoContent) return null;
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) { access = await AccessToken(cancellation, true); continue; }
            if ((int)response.StatusCode == 429) throw new InvalidOperationException("Spotify ограничил частоту запросов. Повторите позже.");
            if (response.StatusCode == HttpStatusCode.Forbidden) throw new InvalidOperationException("Spotify не разрешил доступ. Добавьте аккаунт в список пользователей вашего Spotify приложения и проверьте требования Spotify к аккаунту.");
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation)); var root = doc.RootElement;
            if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) return null;
            if (item.GetProperty("type").GetString() != "track") throw new InvalidOperationException("Сейчас играет подкаст или другой объект. Для поиска нужна музыкальная запись.");
            var artists = string.Join(", ", item.GetProperty("artists").EnumerateArray().Select(a => a.GetProperty("name").GetString()));
            string? image = null;
            if (item.TryGetProperty("album", out var album) && album.TryGetProperty("images", out var images) && images.GetArrayLength() > 0) image = images[0].GetProperty("url").GetString();
            return new(item.GetProperty("id").GetString() ?? "", item.GetProperty("name").GetString() ?? "", artists, item.GetProperty("duration_ms").GetInt32(), image);
        }
        throw new InvalidOperationException("Сессия Spotify истекла. Подключите аккаунт заново.");
    }
    private async Task<string> AccessToken(CancellationToken ct, bool refresh = false)
    {
        await tokenLock.WaitAsync(ct);
        try
        {
            if (!Connected) throw new InvalidOperationException("Сначала подключите Spotify.");
            if (!refresh && token!.Expires > DateTimeOffset.UtcNow.AddMinutes(1)) return token.AccessToken;
            token = await Exchange(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = token!.RefreshToken, ["client_id"] = clientId }, token.RefreshToken, ct);
            Save(); return token.AccessToken;
        }
        finally { tokenLock.Release(); }
    }
    private async Task<SpotifyToken> Exchange(Dictionary<string, string> fields, string? oldRefresh, CancellationToken ct)
    {
        using var response = await http.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(fields), ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Spotify не выдал токен. Проверьте Client ID и точное совпадение Redirect URI в Developer Dashboard, затем подключитесь заново.");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); var root = doc.RootElement;
        return new(root.GetProperty("access_token").GetString()!, root.TryGetProperty("refresh_token", out var r) ? r.GetString()! : oldRefresh ?? "", DateTimeOffset.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32()), clientId);
    }
    private void Save() => secrets.Write(JsonSerializer.Serialize(token));
    public void Disconnect() { secrets.Delete(); token = null; }
    private static string FormQuery(Dictionary<string, string> values) => string.Join("&", values.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
    public void Dispose() => tokenLock.Dispose();
}
