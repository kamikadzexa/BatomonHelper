using BatomonHelper.Core;
using BatomonHelper.ScreenReader;
using System.Collections.Immutable;
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

    public ShopOddsSnapshot? AcceptedSnapshot { get; private set; }
    public ImmutableArray<RecognizedField> AcceptedFields { get; private set; } = [];

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (_result.Snapshot is null || DataContext is not CaptureReviewViewModel viewModel) return;
        AcceptedFields = viewModel.Fields.Select(field => field.ToField()).ToImmutableArray();
        AcceptedSnapshot = ShopOddsSnapshotCorrections.Apply(_result.Snapshot, AcceptedFields, _catalog);
        DialogResult = true;
    }
}

public sealed class CaptureReviewViewModel
{
    public CaptureReviewViewModel(ShopCaptureResult result)
    {
        Fields = new(result.Fields.Select(field => new ReviewField(field)));
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
}

public sealed class ReviewField : INotifyPropertyChanged
{
    private string _value;
    private readonly double _confidence;
    public ReviewField(RecognizedField field)
    {
        Name = field.Name; _value = field.Value; _confidence = field.Confidence;
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Name { get; }
    public string DisplayName => Name switch
    {
        "ShopRank" => "Shop rank",
        "Gold" => "Gold",
        "Rarity" => "Rarity",
        "ItemAvailable" => "Item in the fifth slot",
        _ => System.Text.RegularExpressions.Regex.Replace(Name, "([a-z])([A-Z])", "$1 $2")
    };
    public string Value { get => _value; set { if (_value == value) return; _value = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value))); } }
    public RecognizedField ToField() => new(Name, Value, _confidence, true, [], null);
}
