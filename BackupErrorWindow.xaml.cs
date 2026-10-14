using System.Windows;

namespace AutomaticBackups;

public partial class BackupErrorWindow : Window
{
    public BackupErrorWindow(string profileName, string error)
    {
        InitializeComponent();
        ProfileLabel.Text = $"Profile: {profileName}";
        DetailsBox.Text = error;
        HelpfulMessage.Text = error.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
            ? "A file is currently open or locked by another app. Close the app using it, then choose Run now to try again."
            : "Check that the source and destination are available, then choose Run now to try again.";
    }

    private void CopyDetails_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText($"Automatic Backups · {ProfileLabel.Text}\n\n{DetailsBox.Text}");
            CopyButton.Content = "Copied";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"The error details couldn't be copied.\n\n{ex.Message}", "Copy details", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

}
