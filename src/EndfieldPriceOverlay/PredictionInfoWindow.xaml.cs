using System.Windows;

namespace EndfieldPriceOverlay;

public partial class PredictionInfoWindow : Window
{
    public PredictionInfoWindow()
    {
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
