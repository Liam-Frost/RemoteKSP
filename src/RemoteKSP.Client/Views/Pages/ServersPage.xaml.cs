using Wpf.Ui.Abstractions.Controls;
using RemoteKSP.Client.ViewModels;

namespace RemoteKSP.Client.Views.Pages;

public partial class ServersPage : INavigableView<ServersViewModel>
{
    public ServersViewModel ViewModel { get; }

    public ServersPage(ServersViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
        Loaded += async (s, e) => await ViewModel.InitializeAsync();
    }
}
