using Wpf.Ui.Abstractions.Controls;
using System.Windows.Controls;
using RemoteKSP.Server.ViewModels;
using Wpf.Ui.Controls;

namespace RemoteKSP.Server.Views.Pages;

public partial class ClientsPage : INavigableView<ClientsViewModel>
{
    public ClientsViewModel ViewModel { get; }

    public ClientsPage(ClientsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
        Loaded += async (s, e) => await ViewModel.InitializeAsync();
    }
}
