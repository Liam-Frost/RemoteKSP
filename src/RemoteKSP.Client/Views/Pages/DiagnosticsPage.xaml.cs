using Wpf.Ui.Abstractions.Controls;
using System.Windows.Controls;
using RemoteKSP.Client.ViewModels;
using Wpf.Ui.Controls;

namespace RemoteKSP.Client.Views.Pages;

public partial class DiagnosticsPage : INavigableView<DiagnosticsViewModel>
{
    public DiagnosticsViewModel ViewModel { get; }

    public DiagnosticsPage(DiagnosticsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
        Loaded += async (s, e) => await ViewModel.InitializeAsync();
    }
}
