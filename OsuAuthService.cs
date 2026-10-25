using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace OsuCollectorLazer;

internal static class OsuAuthService
{
    private const int ClientId = 72922;
    private const string ClientSecret = "oarip1JLIyzV9tCBuUbm6dgGuyqmu1XuaES6hF0I";
    private const string RedirectUri = "http://localhost:7270/callback";
    private const string TokenUrl = "https://osu.ppy.sh/oauth/token";
    private const string MeUrl = "https://osu.ppy.sh/api/v2/me";

    private static readonly HttpClient AuthHttp = new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
    });

    public static async Task<bool> LoginInteractiveAsync(ChronosConfig config)
    {
        string state = Guid.NewGuid().ToString("N");
        string authUrl = $"https://osu.ppy.sh/oauth/authorize?client_id={ClientId}&redirect_uri={Uri.EscapeDataString(RedirectUri)}&response_type=code&scope=public%20identify&state={state}";

        using var listener = new HttpListener();
        try
        {
            listener.Prefixes.Add("http://localhost:7270/");
            listener.Start();
        }
        catch (Exception ex)
        {
            ChronosUi.WriteLine($"  ✖ Не удалось запустить локальный приёмник (порт 7270): {ex.Message}", ConsoleColor.Red);
            ChronosUi.WaitEnter();
            return false;
        }

        Console.WriteLine();
        ChronosUi.WriteLine("  Открываю браузер для авторизации через официальный osu! аккаунт...", ConsoleColor.Cyan);
        ChronosUi.WriteLine($"  Если браузер не открылся, перейдите по ссылке:", ConsoleColor.DarkGray);
        ChronosUi.WriteLine($"  {authUrl}", ConsoleColor.DarkCyan);

        try
        {
            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
        }
        catch
        {
            // fallback
        }

        ChronosUi.WriteLine("  Ожидание подтверждения в браузере (нажмите Ctrl+C для отмены)...", ConsoleColor.Yellow);

        HttpListenerContext ctx;
        try
        {
            ctx = await listener.GetContextAsync();
        }
        catch
        {
            return false;
        }

        string? code = ctx.Request.QueryString["code"];
        string? returnedState = ctx.Request.QueryString["state"];

        string htmlResponse = @"<!DOCTYPE html><html><head><meta charset='utf-8'><title>CHRONOS</title>
<style>body{font-family:sans-serif;background:#1e1e2e;color:#cdd6f4;display:flex;justify-content:center;align-items:center;height:100vh;margin:0;}
.card{background:#313244;padding:30px;border-radius:12px;text-align:center;box-shadow:0 8px 24px rgba(0,0,0,0.4);}
h1{color:#a6e3a1;margin-bottom:10px;}p{color:#bac2de;font-size:16px;}</style></head>
<body><div class='card'><h1>✔ Успешный вход!</h1><p>Аккаунт osu! успешно подключён к CHRONOS.<br>Можете закрыть эту вкладку и вернуться в консоль.</p></div></body></html>";

        byte[] respBytes = System.Text.Encoding.UTF8.GetBytes(htmlResponse);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = respBytes.Length;
        await ctx.Response.OutputStream.WriteAsync(respBytes);
        ctx.Response.OutputStream.Close();
        listener.Stop();

        if (string.IsNullOrEmpty(code) || returnedState != state)
        {
            ChronosUi.WriteLine("  ✖ Ошибка: код авторизации не получен или отклонён.", ConsoleColor.Red);
            ChronosUi.WaitEnter();
            return false;
        }

        ChronosUi.WriteLine("  Получен код, запрашиваю токен авторизации...", ConsoleColor.Cyan);
        var postData = new Dictionary<string, string>
        {
            ["client_id"] = ClientId.ToString(),
            ["client_secret"] = ClientSecret,
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = RedirectUri,
        };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl) { Content = new FormUrlEncodedContent(postData) };
            using var resp = await AuthHttp.SendAsync(req);
            string json = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                ChronosUi.WriteLine($"  ✖ Ошибка получения токена (HTTP {(int)resp.StatusCode}): {json}", ConsoleColor.Red);
                ChronosUi.WaitEnter();
                return false;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            config.OsuAccessToken = root.GetProperty("access_token").GetString();
            config.OsuRefreshToken = root.TryGetProperty("refresh_token", out var rf) ? rf.GetString() : null;
            if (root.TryGetProperty("expires_in", out var exp))
                config.OsuTokenExpiry = DateTime.UtcNow.AddSeconds(exp.GetInt32() - 60);

            // Получаем имя пользователя и аватар
            await FetchUserIdentityAsync(config);
            config.Save();

            ChronosUi.WriteLine($"  ✔ Успешный вход! Пользователь: {config.OsuUsername ?? "osu!player"}", ConsoleColor.Green);
            ChronosUi.WaitEnter("Нажмите Enter, чтобы продолжить...");
            return true;
        }
        catch (Exception ex)
        {
            ChronosUi.WriteLine($"  ✖ Ошибка подключения: {ex.Message}", ConsoleColor.Red);
            ChronosUi.WaitEnter();
            return false;
        }
    }

    public static async Task<string?> GetValidAccessTokenAsync(ChronosConfig config)
    {
        if (string.IsNullOrEmpty(config.OsuAccessToken))
            return null;

        if (config.OsuTokenExpiry.HasValue && config.OsuTokenExpiry.Value > DateTime.UtcNow)
            return config.OsuAccessToken;

        if (string.IsNullOrEmpty(config.OsuRefreshToken))
            return config.OsuAccessToken;

        // Refresh token
        var postData = new Dictionary<string, string>
        {
            ["client_id"] = ClientId.ToString(),
            ["client_secret"] = ClientSecret,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = config.OsuRefreshToken,
        };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl) { Content = new FormUrlEncodedContent(postData) };
            using var resp = await AuthHttp.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
                return config.OsuAccessToken;

            string json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            config.OsuAccessToken = root.GetProperty("access_token").GetString();
            if (root.TryGetProperty("refresh_token", out var rf))
                config.OsuRefreshToken = rf.GetString();
            if (root.TryGetProperty("expires_in", out var exp))
                config.OsuTokenExpiry = DateTime.UtcNow.AddSeconds(exp.GetInt32() - 60);
            config.Save();
            return config.OsuAccessToken;
        }
        catch
        {
            return config.OsuAccessToken;
        }
    }

    public static async Task FetchUserIdentityAsync(ChronosConfig config)
    {
        try
        {
            string? token = await GetValidAccessTokenAsync(config);
            if (string.IsNullOrEmpty(token)) return;

            using var req = new HttpRequestMessage(HttpMethod.Get, MeUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await AuthHttp.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return;

            string json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("username", out var u))
                config.OsuUsername = u.GetString();
            if (root.TryGetProperty("id", out var uid))
                config.OsuUserId = uid.GetInt32();
            if (root.TryGetProperty("is_supporter", out var sup))
                config.OsuIsSupporter = sup.GetBoolean();
        }
        catch
        {
            // ignore
        }
    }

    public static void Logout(ChronosConfig config)
    {
        config.OsuAccessToken = null;
        config.OsuRefreshToken = null;
        config.OsuTokenExpiry = null;
        config.OsuUsername = null;
        config.OsuUserId = null;
        config.OsuIsSupporter = false;
        config.Save();
        ChronosUi.WriteLine("  ✔ Вы вышли из osu! аккаунта.", ConsoleColor.Green);
        ChronosUi.WaitEnter();
    }
}
