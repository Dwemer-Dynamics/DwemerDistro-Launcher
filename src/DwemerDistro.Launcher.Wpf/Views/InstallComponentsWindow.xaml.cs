using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;

namespace DwemerDistro.Launcher.Wpf.Views;

public partial class InstallComponentsWindow : Window
{
    private const string DefaultDescriptionText = "Hover over a component below to see its description.";

    public InstallComponentsWindow()
    {
        InitializeComponent();
    }

    private void ComponentButton_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is Button { ToolTip: string description } && !string.IsNullOrWhiteSpace(description))
        {
            DescriptionTextBlock.Text = description;
        }
    }

    private void ComponentButton_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        DescriptionTextBlock.Text = DefaultDescriptionText;
    }
}
