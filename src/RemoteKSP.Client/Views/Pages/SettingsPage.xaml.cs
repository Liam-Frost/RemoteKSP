using Wpf.Ui.Abstractions.Controls;
using System.Windows.Controls;
using RemoteKSP.Client.ViewModels;
using Wpf.Ui.Controls;

namespace RemoteKSP.Client.Views.Pages;

public partial class SettingsPage : INavigableView<SettingsViewModel>
{
    public SettingsViewModel ViewModel { get; }

    public SettingsPage(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
    }
}
