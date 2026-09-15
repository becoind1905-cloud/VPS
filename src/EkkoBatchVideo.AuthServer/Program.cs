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
    using var create = new NpgsqlCommand("CREATE TABLE IF NOT EXISTS accounts (email text PRIMARY KEY, password_hash text NOT NULL, expires_at timestamptz NOT NULL, device_id text NULL)", connection);
    create.ExecuteNonQuery();
    using var alter = new NpgsqlCommand("ALTER TABLE accounts ADD COLUMN IF NOT EXISTS device_id text NULL", connection);
    alter.ExecuteNonQuery();
    using var select = new NpgsqlCommand("SELECT email,password_hash,expires_at,device_id FROM accounts", connection);
    using var reader = select.ExecuteReader();
    while (reader.Read()) users[reader.GetString(0)] = new User(reader.GetString(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2), reader.IsDBNull(3) ? null : reader.GetString(3));
}
else if (File.Exists(database))
    foreach (var user in System.Text.Json.JsonSerializer.Deserialize<List<User>>(File.ReadAllText(database)) ?? []) users[user.Email] = user;

void Save() {
    if (string.IsNullOrWhiteSpace(databaseUrl)) { File.WriteAllText(database, System.Text.Json.JsonSerializer.Serialize(users.Values)); return; }
    using var connection = new NpgsqlConnection(NormalizeConnectionString(databaseUrl)); connection.Open();
    foreach (var user in users.Values) { using var command = new NpgsqlCommand("INSERT INTO accounts(email,password_hash,expires_at,device_id) VALUES($1,$2,$3,$4) ON CONFLICT(email) DO UPDATE SET password_hash=EXCLUDED.password_hash,expires_at=EXCLUDED.expires_at,device_id=EXCLUDED.device_id", connection); command.Parameters.AddWithValue(user.Email); command.Parameters.AddWithValue(user.PasswordHash); command.Parameters.AddWithValue(user.ExpiresAt); command.Parameters.AddWithValue((object?)user.DeviceId ?? DBNull.Value); command.ExecuteNonQuery(); }
}
static string NormalizeConnectionString(string value) {
    if (!value.StartsWith("postgres", StringComparison.OrdinalIgnoreCase)) return value.Replace("user=", "Username=", StringComparison.OrdinalIgnoreCase);
    var uri = new Uri(value); var userInfo = uri.UserInfo.Split(':', 2);
    return new NpgsqlConnectionStringBuilder { Host = uri.Host, Port = uri.Port > 0 ? uri.Port : 5432, Username = Uri.UnescapeDataString(userInfo[0]), Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "", Database = uri.AbsolutePath.Trim('/'), SslMode = SslMode.Require }.ConnectionString;
}
static string Hash(string value) => Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(value, "EkkoAuth"u8.ToArray(), 120_000, HashAlgorithmName.SHA256, 32));
static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

app.MapPost("/api/auth/register", (RegisterRequest request) => {
    var email = request.Email.Trim().ToLowerInvariant();
    if (email.Length < 1 || request.Password.Length < 1) return Results.BadRequest(new { message = "Tài khoản hoặc mật khẩu đang để trống." });
    if (users.ContainsKey(email)) return Results.Conflict(new { message = "Tài khoản đã tồn tại." });
    // Không tự cấp thời gian. Tài khoản chờ admin duyệt và cấp hạn.
    users[email] = new User(email, Hash(request.Password), DateTimeOffset.UtcNow, NormalizeDevice(request.DeviceId)); Save();
    return Login(email, request.Password, request.DeviceId);
});
app.MapPost("/api/auth/login", (LoginRequest request) => Login(request.Email.Trim().ToLowerInvariant(), request.Password, request.DeviceId));
app.MapPost("/api/auth/change-password", (ChangePasswordRequest request) => {
    if (!tokens.TryGetValue(request.Token ?? "", out var session) || !users.TryGetValue(session.Email, out var user) || user.PasswordHash != Hash(request.CurrentPassword) || request.NewPassword.Length < 1) return Results.BadRequest(new { message = "Mật khẩu hiện tại không đúng hoặc mật khẩu mới đang để trống." });
    user.PasswordHash = Hash(request.NewPassword); Save(); return Results.Ok(new { message = "Đã đổi mật khẩu." });
});
app.MapPost("/api/auth/forgot-password", async (ForgotRequest request) => {
    var email = request.Email.Trim().ToLowerInvariant();
    var sent = false;
    if (users.TryGetValue(email, out _)) {
        var token = NewToken(); resetTokens[token] = new ResetToken(email, DateTimeOffset.UtcNow.AddMinutes(30));
        var key = Environment.GetEnvironmentVariable("RESEND_API_KEY");
        var from = Environment.GetEnvironmentVariable("MAIL_FROM") ?? "onboarding@resend.dev";
        var publicUrl = Environment.GetEnvironmentVariable("PUBLIC_URL") ?? "https://vps-x317.onrender.com";
        if (!string.IsNullOrWhiteSpace(key)) { using var client = new HttpClient(); client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key); var response = await client.PostAsJsonAsync("https://api.resend.com/emails", new { from, to = new[] { email }, subject = "Đặt lại mật khẩu Ekko Tools", html = $"<p>Bấm vào liên kết để đặt lại mật khẩu (có hiệu lực 30 phút):</p><p><a href=\"{publicUrl}/reset.html?token={token}\">Đặt lại mật khẩu</a></p>" }); sent = response.IsSuccessStatusCode; }
    }
    return Results.Ok(new { message = sent ? "Đã gửi liên kết đặt lại mật khẩu." : "Chưa cấu hình email; hãy liên hệ quản trị viên để đặt mật khẩu tạm thời.", emailSent = sent });
});
app.MapPost("/api/auth/reset-password", (ResetRequest request) => {
    if (!resetTokens.TryRemove(request.Token, out var reset) || reset.ExpiresAt <= DateTimeOffset.UtcNow || !users.TryGetValue(reset.Email, out var user) || request.Password.Length < 1) return Results.BadRequest(new { message = "Liên kết không hợp lệ hoặc mật khẩu đang để trống." });
    user.PasswordHash = Hash(request.Password); Save(); return Results.Ok(new { message = "Đã đổi mật khẩu." });
});
app.MapPost("/api/auth/check", (CheckRequest request) => {
    if (!tokens.TryGetValue(request.Token ?? "", out var session) || session.ExpiresAt <= DateTimeOffset.UtcNow) return Results.Unauthorized();
    if (!users.TryGetValue(session.Email, out var user)) return Results.Unauthorized();
    if (!DeviceMatches(user, session.DeviceId) || !DeviceMatches(user, request.DeviceId)) return Results.Unauthorized();
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
    return Results.Ok(users.Values.OrderBy(u => u.Email).Select(u => new { u.Email, u.ExpiresAt, active = u.ExpiresAt > DateTimeOffset.UtcNow, deviceLocked = !string.IsNullOrWhiteSpace(u.DeviceId) }));
});
app.MapGet("/api/admin/backup", (HttpRequest request) => {
    if (!IsAdmin(request)) return Results.Unauthorized();
    var json = System.Text.Json.JsonSerializer.Serialize(users.Values.OrderBy(u => u.Email));
    return Results.File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", $"ekko-accounts-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
});
app.MapPost("/api/admin/users/{email}/expiry", (string email, ExpiryRequest request, HttpRequest http) => {
    if (!IsAdmin(http)) return Results.Unauthorized();
    if (!users.TryGetValue(email.Trim().ToLowerInvariant(), out var user)) return Results.NotFound();
    user.ExpiresAt = request.ExpiresAt ?? (request.Hours != 0
        ? DateTimeOffset.UtcNow.AddHours(Math.Clamp(request.Hours, -87600, 87600))
        : DateTimeOffset.UtcNow.AddDays(Math.Clamp(request.Days, -3650, 3650)));
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
app.MapPost("/api/admin/users/{email}/reset-device", (string email, HttpRequest http) => {
    if (!IsAdmin(http)) return Results.Unauthorized();
    if (!users.TryGetValue(email.Trim().ToLowerInvariant(), out var user)) return Results.NotFound();
    user.DeviceId = null; Save();
    foreach (var pair in tokens.Where(p => p.Value.Email.Equals(user.Email, StringComparison.OrdinalIgnoreCase))) tokens.TryRemove(pair.Key, out _);
    return Results.Ok(new { user.Email, deviceLocked = false });
});
app.MapDelete("/api/admin/users/{email}", (string email, HttpRequest http) => {
    if (!IsAdmin(http)) return Results.Unauthorized();
    var normalized = email.Trim().ToLowerInvariant();
    if (!users.TryRemove(normalized, out var removed)) return Results.NotFound();
    foreach (var pair in tokens.Where(p => p.Value.Email.Equals(removed.Email, StringComparison.OrdinalIgnoreCase)))
        tokens.TryRemove(pair.Key, out _);
    if (!string.IsNullOrWhiteSpace(databaseUrl))
    {
        using var connection = new NpgsqlConnection(NormalizeConnectionString(databaseUrl));
        connection.Open();
        using var command = new NpgsqlCommand("DELETE FROM accounts WHERE email=$1", connection);
        command.Parameters.AddWithValue(normalized);
        command.ExecuteNonQuery();
    }
    else Save();
    return Results.Ok(new { removed = removed.Email });
});
app.MapPost("/api/admin/users/{email}/password", (string email, PasswordRequest request, HttpRequest http) => {
    if (!IsAdmin(http)) return Results.Unauthorized();
    if (request.Password.Length < 1 || !users.TryGetValue(email.Trim().ToLowerInvariant(), out var user)) return Results.BadRequest(new { message = "Tài khoản không tồn tại hoặc mật khẩu đang để trống." });
    user.PasswordHash = Hash(request.Password); Save();
    return Results.Ok(new { message = "Đã đặt lại mật khẩu." });
});
app.Run();

static string? NormalizeDevice(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
static bool DeviceMatches(User user, string? deviceId) => string.IsNullOrWhiteSpace(user.DeviceId) || user.DeviceId == NormalizeDevice(deviceId);
IResult Login(string email, string password, string? deviceId) {
    if (!users.TryGetValue(email, out var user) || user.PasswordHash != Hash(password)) return Results.Unauthorized();
    var normalizedDevice = NormalizeDevice(deviceId);
    if (string.IsNullOrWhiteSpace(normalizedDevice)) return Results.BadRequest(new { message = "Không nhận diện được máy đang đăng nhập." });
    if (!string.IsNullOrWhiteSpace(user.DeviceId) && user.DeviceId != normalizedDevice) return Results.Json(new { message = "Tài khoản này đã được đăng nhập trên máy khác. Hãy liên hệ admin để reset máy." }, statusCode: StatusCodes.Status403Forbidden);
    if (string.IsNullOrWhiteSpace(user.DeviceId)) { user.DeviceId = normalizedDevice; Save(); }
    foreach (var pair in tokens.Where(p => p.Value.Email.Equals(email, StringComparison.OrdinalIgnoreCase))) tokens.TryRemove(pair.Key, out _);
    var token = NewToken(); tokens[token] = new Session(email, DateTimeOffset.UtcNow.AddDays(30), normalizedDevice);
    return Results.Ok(new { token, valid = user.ExpiresAt > DateTimeOffset.UtcNow, email, expiresAt = user.ExpiresAt });
}
record RegisterRequest(string Email, string Password, string? DeviceId = null);
record LoginRequest(string Email, string Password, string? DeviceId = null);
record ChangePasswordRequest(string? Token, string CurrentPassword, string NewPassword);
record CheckRequest(string? Token, string? DeviceId = null);
record ForgotRequest(string Email);
record ResetRequest(string Token, string Password);
record PasswordRequest(string Password);
record ExpiryRequest(int Days = 0, double Hours = 0, DateTimeOffset? ExpiresAt = null);
record Session(string Email, DateTimeOffset ExpiresAt, string? DeviceId);
record ResetToken(string Email, DateTimeOffset ExpiresAt);
sealed class User(string email, string passwordHash, DateTimeOffset expiresAt, string? deviceId = null) { public string Email { get; set; } = email; public string PasswordHash { get; set; } = passwordHash; public DateTimeOffset ExpiresAt { get; set; } = expiresAt; public string? DeviceId { get; set; } = deviceId; }
