using Wpf.Ui.Abstractions.Controls;
using RemoteKSP.Server.ViewModels;

namespace RemoteKSP.Server.Views.Pages;

public partial class CertificatesPage : INavigableView<CertificatesViewModel>
{
    public CertificatesViewModel ViewModel { get; }

    public CertificatesPage(CertificatesViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
        Loaded += async (s, e) => await ViewModel.InitializeAsync();
    }
}
