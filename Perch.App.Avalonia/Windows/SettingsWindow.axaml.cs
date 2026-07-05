using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Perch.Avalonia.Windows;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
