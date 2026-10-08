using System.Windows;

namespace CodexAccountBar;

public partial class JsonImportWindow : Window
{
    #region Properties

    public string JsonText => JsonTextBox.Text;

    #endregion

    #region Initialization

    public JsonImportWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => JsonTextBox.Focus();
    }

    #endregion

    #region Event Handlers

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Clipboard.ContainsText()) JsonTextBox.Text = System.Windows.Clipboard.GetText();
        JsonTextBox.Focus();
        JsonTextBox.CaretIndex = JsonTextBox.Text.Length;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(JsonText))
        {
            System.Windows.MessageBox.Show(this, "Paste account JSON before importing.", "Import Codex account JSON", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }

    #endregion
}
