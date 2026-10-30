using CommunityToolkit.Mvvm.ComponentModel;

namespace RemoteKSP.Client.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _version = typeof(SettingsViewModel).Assembly.GetName().Version!.ToString(3);

    public RemoteKSP.Shared.Localization.LanguageSettingsViewModel Language { get; } = new();

    public SettingsViewModel()
    {
    }
}
