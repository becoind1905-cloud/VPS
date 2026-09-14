using System.Windows;
using EkkoBatchVideo.Services;
namespace EkkoBatchVideo.Views;
public partial class ChangePasswordWindow : Window
{ private readonly AuthService _auth; public ChangePasswordWindow(AuthService auth){InitializeComponent();_auth=auth;} private async void Save_Click(object sender,RoutedEventArgs e){if(New.Password!=Confirm.Password||New.Password.Length<8){Error.Text="Mật khẩu mới phải giống nhau và có ít nhất 8 ký tự.";return;} try{if(await _auth.ChangePasswordAsync(Current.Password,New.Password)){MessageBox.Show("Đã đổi mật khẩu.","Ekko Tools",MessageBoxButton.OK,MessageBoxImage.Information);DialogResult=true;Close();}else Error.Text="Mật khẩu hiện tại không đúng.";}catch(Exception ex){Error.Text=ex.Message;}}}
