using System.Windows;

namespace AutomaticBackups;

public partial class CloseChoiceWindow : Window
{
    public CloseChoice Result { get; private set; } = CloseChoice.KeepOpen;

    public CloseChoiceWindow() => InitializeComponent();

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        Result = CloseChoice.MinimizeToTray;
        DialogResult = true;
    }

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        Result = CloseChoice.Quit;
        DialogResult = true;
    }

    private void KeepOpen_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

public enum CloseChoice
{
    KeepOpen,
    MinimizeToTray,
    Quit
}
