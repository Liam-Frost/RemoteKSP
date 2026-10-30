using Wpf.Ui.Abstractions.Controls;
using System.Windows.Controls;
using RemoteKSP.Client.ViewModels;
using Wpf.Ui.Controls;

namespace RemoteKSP.Client.Views.Pages;

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
