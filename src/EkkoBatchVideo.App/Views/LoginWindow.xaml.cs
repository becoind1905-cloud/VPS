using System.Windows;
using System.Windows.Input;
using EkkoBatchVideo.Services;
namespace EkkoBatchVideo.Views;
public partial class LoginWindow : Window
{
    private readonly AuthService _auth;
    public AuthStatus? AuthStatus { get; private set; }
    public LoginWindow(AuthService auth)
    {
        InitializeComponent();
        _auth = auth;
        EmailBox.Text = auth.SavedAccount;
        EmailBox.KeyDown += LoginInput_KeyDown;
        PasswordBox.KeyDown += LoginInput_KeyDown;
        Loaded += (_, _) =>
        {
            Topmost = true;
            Activate();
            EmailBox.Focus();
            Topmost = false;
        };
    }
    private async void Login_Click(object sender, RoutedEventArgs e) => await Submit(false);
    private async void Register_Click(object sender, RoutedEventArgs e) => await Submit(true);
    private async void LoginInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await Submit(false);
    }
    private async void Forgot_Click(object sender, RoutedEventArgs e)
    { if (string.IsNullOrWhiteSpace(EmailBox.Text)) { ErrorText.Text = "Nhập tài khoản trước rồi bấm Quên mật khẩu."; return; } try { var sent = await _auth.ForgotPasswordAsync(EmailBox.Text); ErrorText.Text = sent ? "Đã gửi liên kết đặt lại mật khẩu." : "Chưa cấu hình email; hãy liên hệ quản trị viên để đặt mật khẩu tạm thời."; } catch (Exception ex) { ErrorText.Text = ex.Message; } }
    private async Task Submit(bool register)
    {
        ErrorText.Text = "Đang kết nối máy chủ…";
        try
        {
            AuthStatus = await _auth.LoginAsync(EmailBox.Text, PasswordBox.Password, register);
            if (AuthStatus is null)
            {
                ErrorText.Text = string.IsNullOrWhiteSpace(_auth.LastError)
                    ? "Tài khoản hoặc mật khẩu không đúng, hoặc máy chủ không khả dụng."
                    : _auth.LastError;
                return;
            }
            if (!AuthStatus.Valid)
            {
                ErrorText.Text = register
                    ? "Đăng ký thành công. Hãy chờ admin cấp thời gian sử dụng rồi bấm Đăng nhập lại."
                    : "Tài khoản đang chờ admin cấp thời gian sử dụng.";
                return;
            }
            DialogResult = true;
            Close();
        }
        catch (Exception ex) { ErrorText.Text = $"Không thể kết nối máy chủ: {ex.Message}"; }
    }
}
