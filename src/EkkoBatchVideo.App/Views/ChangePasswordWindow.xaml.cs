using System.Windows;
using System.Windows.Input;
using EkkoBatchVideo.Services;

namespace EkkoBatchVideo.Views;

public partial class ChangePasswordWindow : Window
{
    private readonly AuthService _auth;

    public ChangePasswordWindow(AuthService auth)
    {
        InitializeComponent();
        _auth = auth;
        Loaded += (_, _) => Current.Focus();
    }

    private async void Save_Click(object sender, RoutedEventArgs e) =>
        await SaveAsync();

    private async void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SaveAsync();
    }

    private async Task SaveAsync()
    {
        Error.Text = "";
        if (New.Password != Confirm.Password || New.Password.Length < 1)
        {
            Error.Text = "Mật khẩu mới không được để trống và phải khớp nhau.";
            return;
        }

        try
        {
            if (await _auth.ChangePasswordAsync(Current.Password, New.Password))
            {
                MessageBox.Show(
                    "Đã đổi mật khẩu.",
                    "Ekko Tools",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                DialogResult = true;
                Close();
                return;
            }

            Error.Text = "Mật khẩu hiện tại không đúng.";
        }
        catch (Exception ex)
        {
            Error.Text = ex.Message;
        }
    }
}
