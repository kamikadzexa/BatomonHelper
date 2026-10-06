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
    private readonly ShopCaptureResult? _result;
    private readonly BoardCaptureResult? _boardResult;
    private readonly CatalogSnapshot _catalog;

    public CaptureReviewWindow(ShopCaptureResult result, CatalogSnapshot catalog)
    {
        InitializeComponent();
        _result = result;
        _catalog = catalog;
        DataContext = new CaptureReviewViewModel(result);
    }

    public ShopOddsSnapshot? AcceptedSnapshot { get; private set; }
    public BoardSnapshot? AcceptedBoard { get; private set; }
    public CaptureReviewWindow(BoardCaptureResult result, CatalogSnapshot catalog)
    {
        InitializeComponent(); _boardResult = result; _catalog = catalog;
        Title = "Correct board capture";
        DataContext = new CaptureReviewViewModel(result, catalog);
    }
    public ImmutableArray<RecognizedField> AcceptedFields { get; private set; } = [];

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CaptureReviewViewModel viewModel) return;
        AcceptedFields = viewModel.Fields.Select(field => field.ToField()).ToImmutableArray();
        if (_boardResult?.Snapshot is not null)
        {
            try { AcceptedBoard = BoardSnapshotCorrections.Apply(_boardResult.Snapshot, AcceptedFields, _catalog); }
            catch (FormatException exception) { MessageBox.Show(this, exception.Message, "Correct highlighted board values", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        }
        else if (_result?.Snapshot is not null) AcceptedSnapshot = ShopOddsSnapshotCorrections.Apply(_result.Snapshot, AcceptedFields, _catalog);
        else return;
        DialogResult = true;
    }
}

public sealed class CaptureReviewViewModel
{
    private readonly CatalogSnapshot? _catalog;
    public CaptureReviewViewModel(ShopCaptureResult result)
    {
        if (result.Frame is not null)
        {
            var image = new BitmapImage();
            using var stream = new MemoryStream(result.Frame.Png);
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
            Image = image;
        }
        Fields = new(result.Fields.OrderBy(entry => entry.IsResolved).Select(entry => new ReviewField(entry, Image)));
    }

    public BitmapSource? Image { get; }
    public ObservableCollection<ReviewField> Fields { get; }
    public CaptureReviewViewModel(BoardCaptureResult result, CatalogSnapshot catalog)
        : this(new ShopCaptureResult(null, result.Frame, result.Fields, result.Diagnostics))
    {
        _catalog = catalog;
        foreach (var field in Fields) Configure(field);
    }
    private void Configure(ReviewField field)
    {
        if (_catalog is null) return;
        var choices = field.Name switch
        {
            "TrainerId" => _catalog.Trainers.OrderBy(entry => entry.Name).Select(entry => entry.Id),
            "ShopLocked" or "ItemUsesCounterVisible" => new[] { "True", "False" },
            "RerollMode" => new[] { "Free", "Paid" },
            _ when field.Name.EndsWith(".Variant", StringComparison.Ordinal) => new[] { "Normal", "Shiny" },
            _ when field.Name.EndsWith(".Kind", StringComparison.Ordinal) => Enum.GetNames<VisibleStatKind>().Where(kind => kind != "Unknown").Append("Absent"),
            _ when field.Name.EndsWith(".Occupant", StringComparison.Ordinal) => new[] { "empty" }.Concat(_catalog.Monsters.OrderBy(entry => entry.Name).Select(entry => "monster:" + entry.Id)).Concat(_catalog.Items.OrderBy(entry => entry.Name).Select(entry => "item:" + entry.Id)),
            _ => field.Candidates
        };
        field.SetChoices(choices);
        field.SetDisplay(value => field.Name == "TrainerId" ? _catalog.Trainers.FirstOrDefault(trainer => trainer.Id == value)?.Name ?? value :
            value == "empty" ? "Empty" : value.StartsWith("monster:", StringComparison.Ordinal) ? _catalog.Monsters.FirstOrDefault(monster => monster.Id == value[8..])?.Name ?? value :
            value.StartsWith("item:", StringComparison.Ordinal) ? (_catalog.Items.FirstOrDefault(item => item.Id == value[5..])?.Name ?? value) + " (item)" :
            value == "CooldownSpeed" ? "Cooldown speed" : value);
        if (!field.Name.EndsWith(".Occupant", StringComparison.Ordinal)) return;
        var prefix = field.Name[..^9];
        field.AddBadgeCommand = new RelayCommand(() => AddBadge(prefix, field.Region));
        field.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(ReviewField.Value)) return;
            if (field.Value.StartsWith("monster:", StringComparison.Ordinal))
            {
                Add(prefix + ".Variant", "Unknown", field.Region);
                if (!prefix.StartsWith("Shop", StringComparison.Ordinal)) Add(prefix + ".Level", "", field.Region);
                if (!Fields.Any(entry => entry.Name.StartsWith(prefix + ".Stats[", StringComparison.Ordinal))) AddBadge(prefix, field.Region);
            }
            if (prefix.StartsWith("Shop", StringComparison.Ordinal) && field.Value != "empty") Add(prefix + ".Price", "", field.Region);
            if (field.Value.StartsWith("item:", StringComparison.Ordinal))
            { Add("ItemUsesMaximum", "", BoardLayout.ItemUses); Add("ItemUsesRemaining", "", BoardLayout.ItemUses); }
        };
    }
    private void AddBadge(string prefix, NormalizedRect? region)
    {
        var index = 0;
        while (Fields.Any(field => field.Name == $"{prefix}.Stats[{index}].Kind")) index++;
        Add($"{prefix}.Stats[{index}].Kind", "Unknown", region);
        Add($"{prefix}.Stats[{index}].Value", "", region);
    }
    private void Add(string name, string value, NormalizedRect? region)
    {
        if (Fields.Any(field => field.Name == name)) return;
        var field = new ReviewField(new(name, value, 0, false, [], "Confirm this visible value.") { Region = region }, Image);
        Fields.Add(field); Configure(field);
    }
}

public sealed class ReviewField : INotifyPropertyChanged
{
    private string _value;
    private readonly double _confidence;
    private Func<string, string> _display = value => value;
    public ReviewField(RecognizedField field, BitmapSource? source = null)
    {
        Name = field.Name; _value = field.Value; _confidence = field.Confidence;
        Region = field.Region;
        Diagnostic = field.Diagnostic;
        Candidates = field.Candidates.Select(entry => entry.Value).Where(value => value.Length > 0).Distinct().ToArray();
        if (source is not null && field.Region is not null)
        {
            var bounds = field.Region.Value.ToPixels(source.PixelWidth, source.PixelHeight);
            var crop = new CroppedBitmap(source, new Int32Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height)); crop.Freeze(); Crop = crop;
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Name { get; }
    public string? Diagnostic { get; }
    public IReadOnlyList<string> Candidates { get; private set; }
    public NormalizedRect? Region { get; }
    public System.Windows.Input.ICommand? AddBadgeCommand { get; set; }
    public Visibility AddBadgeVisibility => AddBadgeCommand is not null && Value.StartsWith("monster:", StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
    public void SetChoices(IEnumerable<string> choices)
    { Candidates = Candidates.Concat(choices).Distinct().ToArray(); PropertyChanged?.Invoke(this, new(nameof(CandidateLabels))); }
    public IEnumerable<string> CandidateLabels => Candidates.Select(_display);
    public string DisplayValue { get => _display(Value); set => Value = Candidates.FirstOrDefault(choice => string.Equals(_display(choice), value, StringComparison.OrdinalIgnoreCase)) ?? value; }
    public void SetDisplay(Func<string, string> display)
    { _display = display; PropertyChanged?.Invoke(this, new(nameof(DisplayValue))); PropertyChanged?.Invoke(this, new(nameof(CandidateLabels))); }
    public BitmapSource? Crop { get; }
    public string DisplayName => Name switch
    {
        "TrainerId" => "Trainer",
        "ShopRank" => "Shop rank",
        "Gold" => "Gold",
        "Rarity" => "Rarity",
        "ItemAvailable" => "Item in the fifth slot",
        _ => System.Text.RegularExpressions.Regex.Replace(Name, "([a-z])([A-Z])", "$1 $2")
    };
    public string Value { get => _value; set { if (_value == value) return; _value = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value))); PropertyChanged?.Invoke(this, new(nameof(DisplayValue))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AddBadgeVisibility))); } }
    public RecognizedField ToField() => new(Name, Value, _confidence, true, [], null) { Region = Region };
}
