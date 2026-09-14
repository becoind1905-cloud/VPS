using System.Net.Http.Json;
using System.Net.Http;
using System.Text.Json;
using System.Security.Cryptography;

namespace EkkoBatchVideo.Services;

public sealed record AuthStatus(bool Valid, string Email, DateTimeOffset ExpiresAt);
public sealed class AuthService : IDisposable
{
    private readonly HttpClient _http = new();
    private readonly string _tokenFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EkkoBatchVideo", "auth-token");
    public string ServerUrl { get; } = Environment.GetEnvironmentVariable("EKKO_AUTH_SERVER") ?? "https://vps-x317.onrender.com";
    public string? Token { get; private set; }
    public AuthStatus? Status { get; private set; }
    public AuthService() { if (File.Exists(_tokenFile)) Token = File.ReadAllText(_tokenFile).Trim(); }
    public async Task<AuthStatus?> LoginAsync(string email, string password, bool register, CancellationToken ct = default)
    {
        var path = register ? "register" : "login";
        using var response = await _http.PostAsJsonAsync($"{ServerUrl}/api/auth/{path}", new { email, password }, ct);
        if (!response.IsSuccessStatusCode) return null;
        var data = await response.Content.ReadFromJsonAsync<AuthResponse>(cancellationToken: ct) ?? throw new InvalidOperationException("Phản hồi xác thực không hợp lệ.");
        Token = data.Token; Directory.CreateDirectory(Path.GetDirectoryName(_tokenFile)!); File.WriteAllText(_tokenFile, Token); Status = new(data.Valid, data.Email, data.ExpiresAt); return Status;
    }
    public async Task<AuthStatus?> CheckAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(Token)) return null;
        try { using var response = await _http.PostAsJsonAsync($"{ServerUrl}/api/auth/check", new { token = Token }, ct); if (!response.IsSuccessStatusCode) return null; var d = await response.Content.ReadFromJsonAsync<CheckResponse>(cancellationToken: ct); return d is null ? null : Status = new(d.Valid, d.Email, d.ExpiresAt); } catch { return null; }
    }
    public async Task<bool> ForgotPasswordAsync(string email, CancellationToken ct = default)
    { using var response = await _http.PostAsJsonAsync($"{ServerUrl}/api/auth/forgot-password", new { email }, ct); if (!response.IsSuccessStatusCode) return false; var result = await response.Content.ReadFromJsonAsync<ForgotResponse>(cancellationToken: ct); return result?.EmailSent == true; }
    public void Dispose() => _http.Dispose();
    private sealed record AuthResponse(string Token, bool Valid, string Email, DateTimeOffset ExpiresAt);
    private sealed record CheckResponse(bool Valid, string Email, DateTimeOffset ExpiresAt);
    private sealed record ForgotResponse(string Message, bool EmailSent);
}
