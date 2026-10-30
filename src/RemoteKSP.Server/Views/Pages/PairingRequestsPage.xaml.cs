using Wpf.Ui.Abstractions.Controls;
using System.Windows.Controls;
using RemoteKSP.Server.ViewModels;
using Wpf.Ui.Controls;

namespace RemoteKSP.Server.Views.Pages;

public partial class PairingRequestsPage : INavigableView<PairingRequestsViewModel>
{
    public PairingRequestsViewModel ViewModel { get; }

    public PairingRequestsPage(PairingRequestsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
        Loaded += async (s, e) => await ViewModel.InitializeAsync();
    }
}
