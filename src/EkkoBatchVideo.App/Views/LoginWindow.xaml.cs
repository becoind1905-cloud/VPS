using System.Windows;
using EkkoBatchVideo.Services;
namespace EkkoBatchVideo.Views;
public partial class LoginWindow : Window
{
    private readonly AuthService _auth;
    public AuthStatus? AuthStatus { get; private set; }
    public LoginWindow(AuthService auth) { InitializeComponent(); _auth = auth; }
    private async void Login_Click(object sender, RoutedEventArgs e) => await Submit(false);
    private async void Register_Click(object sender, RoutedEventArgs e) => await Submit(true);
    private async void Forgot_Click(object sender, RoutedEventArgs e)
    { if (string.IsNullOrWhiteSpace(EmailBox.Text)) { ErrorText.Text = "Nhập tài khoản trước rồi bấm Quên mật khẩu."; return; } try { var sent = await _auth.ForgotPasswordAsync(EmailBox.Text); ErrorText.Text = sent ? "Đã gửi liên kết đặt lại mật khẩu." : "Chưa cấu hình email; hãy liên hệ quản trị viên để đặt mật khẩu tạm thời."; } catch (Exception ex) { ErrorText.Text = ex.Message; } }
    private async Task Submit(bool register)
    {
        ErrorText.Text = "Đang kết nối máy chủ…";
        try { AuthStatus = await _auth.LoginAsync(EmailBox.Text, PasswordBox.Password, register); if (AuthStatus is null) { ErrorText.Text = "Email hoặc mật khẩu không đúng, hoặc máy chủ không khả dụng."; return; } DialogResult = true; Close(); }
        catch (Exception ex) { ErrorText.Text = $"Không thể kết nối máy chủ: {ex.Message}"; }
    }
}
