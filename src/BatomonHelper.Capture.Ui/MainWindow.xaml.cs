namespace BatomonHelper.Capture.Ui;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new CaptureViewModel();
    }
}
