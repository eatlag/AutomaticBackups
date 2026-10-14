using System.Windows;

namespace AutomaticBackups;

public partial class ValidationNoticeWindow : Window
{
    public ValidationNoticeWindow(string title, string message)
    {
        InitializeComponent();
        Title = title;
        Heading.Text = title;
        Message.Text = message;
    }
}
