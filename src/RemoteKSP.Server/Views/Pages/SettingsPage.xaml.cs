using Wpf.Ui.Abstractions.Controls;
using System.Windows.Controls;
using RemoteKSP.Server.ViewModels;
using Wpf.Ui.Controls;

namespace RemoteKSP.Server.Views.Pages;

public partial class SettingsPage : INavigableView<SettingsViewModel>
{
    public SettingsViewModel ViewModel { get; }

    public SettingsPage(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
        Loaded += async (s, e) => await ViewModel.InitializeAsync();
    }
}
