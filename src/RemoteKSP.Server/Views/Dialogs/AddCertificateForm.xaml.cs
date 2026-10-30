using System.Windows;
using System.Windows.Controls;
using RemoteKSP.Server.ViewModels;

namespace RemoteKSP.Server.Views.Dialogs;

public partial class AddCertificateForm : UserControl
{
    public AddCertificateViewModel ViewModel { get; }

    public AddCertificateForm(AddCertificateViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        // PasswordBox 不支持绑定：手动同步到 ViewModel
        viewModel.PfxPasswordGenerated += password => PfxPasswordBox.Password = password;
        Loaded += async (_, _) => await viewModel.InitializeAsync();
    }

    private void PinBox_PasswordChanged(object sender, RoutedEventArgs e) =>
        ViewModel.Pin = PinBox.Password;

    private void PfxPasswordBox_PasswordChanged(object sender, RoutedEventArgs e) =>
        ViewModel.PfxPassword = PfxPasswordBox.Password;
}
