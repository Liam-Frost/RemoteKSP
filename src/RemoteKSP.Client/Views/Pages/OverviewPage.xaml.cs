using Wpf.Ui.Abstractions.Controls;
using System.Windows.Controls;
using RemoteKSP.Client.ViewModels;
using Wpf.Ui.Controls;

namespace RemoteKSP.Client.Views.Pages;

public partial class OverviewPage : INavigableView<OverviewViewModel>
{
    public OverviewViewModel ViewModel { get; }

    public OverviewPage(OverviewViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
        Loaded += async (s, e) => await ViewModel.InitializeAsync();
        // 页面是 Transient：离开页面时取消 NetworkChange 静态事件订阅，避免页面无法回收
        Unloaded += (s, e) => ViewModel.Network.Deactivate();
    }
}
