using BatomonHelper.Core;
using BatomonHelper.ScreenReader;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace BatomonHelper.Overlay;

public partial class CaptureReviewWindow : Window
{
    private readonly ShopCaptureResult _result;
    private readonly CatalogSnapshot _catalog;

    public CaptureReviewWindow(ShopCaptureResult result, CatalogSnapshot catalog)
    {
        InitializeComponent();
        _result = result;
        _catalog = catalog;
        DataContext = new CaptureReviewViewModel(result);
    }

    public ShopState? AcceptedState { get; private set; }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (_result.State is null || DataContext is not CaptureReviewViewModel viewModel) return;
        AcceptedState = ShopStateCorrections.Apply(_result.State, viewModel.Fields.Select(field => field.ToField()), _catalog);
        DialogResult = true;
    }
}

public sealed class CaptureReviewViewModel
{
    public CaptureReviewViewModel(ShopCaptureResult result)
    {
        Fields = new(result.Fields.Select(field => new ReviewField(field)));
        Diagnostics = string.Join(" ", result.Diagnostics.Select(diagnostic => diagnostic.Message));
        if (result.Frame is not null)
        {
            var image = new BitmapImage();
            using var stream = new MemoryStream(result.Frame.Png);
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
            Image = image;
        }
    }

    public BitmapSource? Image { get; }
    public ObservableCollection<ReviewField> Fields { get; }
    public string Diagnostics { get; }
}

public sealed class ReviewField : INotifyPropertyChanged
{
    private string _value;
    public ReviewField(RecognizedField field)
    {
        Name = field.Name; _value = field.Value; Confidence = field.Confidence;
        Detail = string.Join(", ", field.Candidates.Select(candidate => candidate.Value));
        if (!string.IsNullOrWhiteSpace(field.Diagnostic)) Detail = string.IsNullOrWhiteSpace(Detail) ? field.Diagnostic : $"{Detail} — {field.Diagnostic}";
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Name { get; }
    public string Value { get => _value; set { if (_value == value) return; _value = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value))); } }
    public double Confidence { get; }
    public string Detail { get; }
    public RecognizedField ToField() => new(Name, Value, Confidence, true, [], null);
}
