using BatomonHelper.Core;

namespace BatomonHelper.Overlay;

public partial class ShoppingSettingsWindow : Window
{
    private readonly ShoppingSettings _original;
    public ShoppingSettings? AcceptedSettings { get; private set; }
    public ShoppingSettingsWindow(ShoppingSettings settings)
    {
        InitializeComponent(); _original = settings;
        ModelBox.Text = settings.Model; EndpointBox.Text = settings.Endpoint; ContextBox.Text = settings.ContextTokens.ToString();
        ThinkingBox.IsChecked = settings.Thinking;
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = _original with { Model = ModelBox.Text.Trim(), Endpoint = EndpointBox.Text.Trim(), ContextTokens = int.Parse(ContextBox.Text), Thinking = ThinkingBox.IsChecked == true };
            settings.Validate(); AcceptedSettings = settings; DialogResult = true;
        }
        catch (Exception error) when (error is FormatException or OverflowException or InvalidDataException) { ErrorText.Text = error.Message; }
    }
}
