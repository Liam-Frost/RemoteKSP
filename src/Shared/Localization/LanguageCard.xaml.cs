using System.Windows.Controls;

namespace RemoteKSP.Shared.Localization;

/// <summary>Settings card for choosing the UI language. DataContext is a <see cref="LanguageSettingsViewModel"/>.</summary>
public partial class LanguageCard : UserControl
{
    public LanguageCard()
    {
        InitializeComponent();
    }
}
