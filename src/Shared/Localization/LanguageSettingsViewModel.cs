using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RemoteKSP.Shared.Localization;

/// <summary>Language picker on the settings page. The choice is saved at once and applied on restart.</summary>
public partial class LanguageSettingsViewModel : ObservableObject
{
    public IReadOnlyList<UiLanguageOption> Options => UiLanguage.Supported;

    [ObservableProperty] private UiLanguageOption _selected;
    [ObservableProperty] private bool _restartRequired;
    [ObservableProperty] private string _errorText = "";

    public LanguageSettingsViewModel()
    {
        var saved = UiLanguage.LoadPreference();
        _selected = Options.FirstOrDefault(o => o.Code == saved) ?? Options[0];
        _restartRequired = _selected.Code != UiLanguage.Current;
    }

    partial void OnSelectedChanged(UiLanguageOption value)
    {
        ErrorText = "";
        try
        {
            UiLanguage.SavePreference(value.Code);
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
        RestartRequired = value.Code != UiLanguage.Current;
    }

    [RelayCommand]
    private void Restart()
    {
        try
        {
            UiLanguage.StartNewInstance();
        }
        catch (Exception ex)
        {
            ErrorText = string.Format(SharedStrings.Lang_RestartFailed, ex.Message);
            return;
        }
        Application.Current.Shutdown();
    }
}
