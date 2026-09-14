using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
var users = new ConcurrentDictionary<string, User>(StringComparer.OrdinalIgnoreCase);
var tokens = new ConcurrentDictionary<string, Session>();
var resetTokens = new ConcurrentDictionary<string, ResetToken>();
var database = Path.Combine(AppContext.BaseDirectory, "accounts.json");
var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
var adminKey = Environment.GetEnvironmentVariable("EKKO_ADMIN_KEY") ?? "change-this-admin-key";
if (!string.IsNullOrWhiteSpace(databaseUrl))
{
    using var connection = new NpgsqlConnection(NormalizeConnectionString(databaseUrl));
    connection.Open();
    using var create = new NpgsqlCommand("CREATE TABLE IF NOT EXISTS accounts (email text PRIMARY KEY, password_hash text NOT NULL, expires_at timestamptz NOT NULL)", connection);
    create.ExecuteNonQuery();
    using var select = new NpgsqlCommand("SELECT email,password_hash,expires_at FROM accounts", connection);
    using var reader = select.ExecuteReader();
    while (reader.Read()) users[reader.GetString(0)] = new User(reader.GetString(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2));
}
else if (File.Exists(database))
    foreach (var user in System.Text.Json.JsonSerializer.Deserialize<List<User>>(File.ReadAllText(database)) ?? []) users[user.Email] = user;

void Save() {
    if (string.IsNullOrWhiteSpace(databaseUrl)) { File.WriteAllText(database, System.Text.Json.JsonSerializer.Serialize(users.Values)); return; }
    using var connection = new NpgsqlConnection(NormalizeConnectionString(databaseUrl)); connection.Open();
    foreach (var user in users.Values) { using var command = new NpgsqlCommand("INSERT INTO accounts(email,password_hash,expires_at) VALUES($1,$2,$3) ON CONFLICT(email) DO UPDATE SET password_hash=EXCLUDED.password_hash,expires_at=EXCLUDED.expires_at", connection); command.Parameters.AddWithValue(user.Email); command.Parameters.AddWithValue(user.PasswordHash); command.Parameters.AddWithValue(user.ExpiresAt); command.ExecuteNonQuery(); }
}
static string NormalizeConnectionString(string value) {
    if (!value.StartsWith("postgres", StringComparison.OrdinalIgnoreCase)) return value;
    var uri = new Uri(value); var userInfo = uri.UserInfo.Split(':', 2);
    return new NpgsqlConnectionStringBuilder { Host = uri.Host, Port = uri.Port > 0 ? uri.Port : 5432, Username = Uri.UnescapeDataString(userInfo[0]), Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "", Database = uri.AbsolutePath.Trim('/'), SslMode = SslMode.Require }.ConnectionString;
}
static string Hash(string value) => Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(value, "EkkoAuth"u8.ToArray(), 120_000, HashAlgorithmName.SHA256, 32));
static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

app.MapPost("/api/auth/register", (RegisterRequest request) => {
    var email = request.Email.Trim().ToLowerInvariant();
    if (email.Length < 5 || !email.Contains('@') || request.Password.Length < 8) return Results.BadRequest(new { message = "Email hoặc mật khẩu không hợp lệ (mật khẩu tối thiểu 8 ký tự)." });
    if (users.ContainsKey(email)) return Results.Conflict(new { message = "Tài khoản đã tồn tại." });
    users[email] = new User(email, Hash(request.Password), DateTimeOffset.UtcNow.AddDays(3)); Save();
    return Login(email, request.Password);
});
app.MapPost("/api/auth/login", (LoginRequest request) => Login(request.Email.Trim().ToLowerInvariant(), request.Password));
app.MapPost("/api/auth/forgot-password", async (ForgotRequest request) => {
    var email = request.Email.Trim().ToLowerInvariant();
    if (users.TryGetValue(email, out _)) {
        var token = NewToken(); resetTokens[token] = new ResetToken(email, DateTimeOffset.UtcNow.AddMinutes(30));
        var key = Environment.GetEnvironmentVariable("RESEND_API_KEY");
        var from = Environment.GetEnvironmentVariable("MAIL_FROM") ?? "onboarding@resend.dev";
        var publicUrl = Environment.GetEnvironmentVariable("PUBLIC_URL") ?? "https://vps-x317.onrender.com";
        if (!string.IsNullOrWhiteSpace(key)) { using var client = new HttpClient(); client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key); await client.PostAsJsonAsync("https://api.resend.com/emails", new { from, to = new[] { email }, subject = "Đặt lại mật khẩu Ekko Tools", html = $"<p>Bấm vào liên kết để đặt lại mật khẩu (có hiệu lực 30 phút):</p><p><a href=\"{publicUrl}/reset.html?token={token}\">Đặt lại mật khẩu</a></p>" }); }
    }
    return Results.Ok(new { message = "Nếu email tồn tại, liên kết đặt lại mật khẩu đã được gửi." });
});
app.MapPost("/api/auth/reset-password", (ResetRequest request) => {
    if (!resetTokens.TryRemove(request.Token, out var reset) || reset.ExpiresAt <= DateTimeOffset.UtcNow || !users.TryGetValue(reset.Email, out var user) || request.Password.Length < 8) return Results.BadRequest(new { message = "Liên kết không hợp lệ hoặc đã hết hạn." });
    user.PasswordHash = Hash(request.Password); Save(); return Results.Ok(new { message = "Đã đổi mật khẩu." });
});
app.MapPost("/api/auth/check", (CheckRequest request) => {
    if (!tokens.TryGetValue(request.Token ?? "", out var session) || session.ExpiresAt <= DateTimeOffset.UtcNow) return Results.Unauthorized();
    if (!users.TryGetValue(session.Email, out var user)) return Results.Unauthorized();
    return Results.Ok(new { valid = user.ExpiresAt > DateTimeOffset.UtcNow, email = user.Email, expiresAt = user.ExpiresAt });
});
// Các API nghiệp vụ có thể dùng cùng quy tắc này: token hợp lệ và tài khoản còn hạn.
app.MapGet("/api/protected/ping", (HttpRequest request) => {
    var token = request.Headers.Authorization.ToString().Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase);
    if (!tokens.TryGetValue(token, out var session) || !users.TryGetValue(session.Email, out var user) || user.ExpiresAt <= DateTimeOffset.UtcNow)
        return Results.Json(new { message = "Tài khoản hết hạn hoặc token không hợp lệ." }, statusCode: StatusCodes.Status403Forbidden);
    return Results.Ok(new { authorized = true, email = user.Email, expiresAt = user.ExpiresAt });
});
bool IsAdmin(HttpRequest request) => request.Headers.TryGetValue("X-Admin-Key", out var key) && key == adminKey;
app.MapGet("/api/admin/users", (HttpRequest request) => {
    if (!IsAdmin(request)) return Results.Unauthorized();
    return Results.Ok(users.Values.OrderBy(u => u.Email).Select(u => new { u.Email, u.ExpiresAt, active = u.ExpiresAt > DateTimeOffset.UtcNow }));
});
app.MapGet("/api/admin/backup", (HttpRequest request) => {
    if (!IsAdmin(request)) return Results.Unauthorized();
    var json = System.Text.Json.JsonSerializer.Serialize(users.Values.OrderBy(u => u.Email));
    return Results.File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", $"ekko-accounts-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
});
app.MapPost("/api/admin/users/{email}/expiry", (string email, ExpiryRequest request, HttpRequest http) => {
    if (!IsAdmin(http)) return Results.Unauthorized();
    if (!users.TryGetValue(email.Trim().ToLowerInvariant(), out var user)) return Results.NotFound();
    user.ExpiresAt = request.ExpiresAt ?? (DateTimeOffset.UtcNow.AddDays(Math.Clamp(request.Days, -3650, 3650)));
    Save();
    return Results.Ok(new { user.Email, user.ExpiresAt, active = user.ExpiresAt > DateTimeOffset.UtcNow });
});
app.MapPost("/api/admin/users/{email}/revoke", (string email, HttpRequest http) => {
    if (!IsAdmin(http)) return Results.Unauthorized();
    if (!users.TryGetValue(email.Trim().ToLowerInvariant(), out var user)) return Results.NotFound();
    user.ExpiresAt = DateTimeOffset.UtcNow; Save();
    foreach (var pair in tokens.Where(p => p.Value.Email.Equals(user.Email, StringComparison.OrdinalIgnoreCase))) tokens.TryRemove(pair.Key, out _);
    return Results.Ok(new { user.Email, user.ExpiresAt, active = false });
});
app.Run();

IResult Login(string email, string password) {
    if (!users.TryGetValue(email, out var user) || user.PasswordHash != Hash(password)) return Results.Unauthorized();
    var token = NewToken(); tokens[token] = new Session(email, DateTimeOffset.UtcNow.AddDays(30));
    return Results.Ok(new { token, valid = user.ExpiresAt > DateTimeOffset.UtcNow, email, expiresAt = user.ExpiresAt });
}
record RegisterRequest(string Email, string Password);
record LoginRequest(string Email, string Password);
record CheckRequest(string? Token);
record ForgotRequest(string Email);
record ResetRequest(string Token, string Password);
record ExpiryRequest(int Days = 0, DateTimeOffset? ExpiresAt = null);
record Session(string Email, DateTimeOffset ExpiresAt);
record ResetToken(string Email, DateTimeOffset ExpiresAt);
sealed class User(string email, string passwordHash, DateTimeOffset expiresAt) { public string Email { get; set; } = email; public string PasswordHash { get; set; } = passwordHash; public DateTimeOffset ExpiresAt { get; set; } = expiresAt; }
