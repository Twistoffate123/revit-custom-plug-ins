using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace FloorPatternFlattener.UI
{
    public sealed class LiftMmDialog : Window
    {
        private readonly TextBox _box;

        public double ValueMm { get; private set; }

        public LiftMmDialog(double suggestedMm)
        {
            Title = FpfOptions.ProductName + " – lift";
            Width = 320;
            Height = 140;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            var root = new StackPanel { Margin = new Thickness(12) };
            root.Children.Add(new TextBlock
            {
                Text = "Lift amount in millimetres (positive = up, negative = down):",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });
            _box = new TextBox
            {
                Text = suggestedMm.ToString("0.##", CultureInfo.InvariantCulture),
                Margin = new Thickness(0, 0, 0, 12)
            };
            root.Children.Add(_box);
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new Button { Content = "OK", Width = 70, Margin = new Thickness(0, 0, 8, 0) };
            ok.Click += (_, __) =>
            {
                if (!double.TryParse(_box.Text.Trim().Replace(',', '.'), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var v) || double.IsNaN(v))
                {
                    MessageBox.Show(this, "Enter a number of millimetres.", Title);
                    return;
                }
                ValueMm = v;
                DialogResult = true;
            };
            var cancel = new Button { Content = "Cancel", Width = 70 };
            cancel.Click += (_, __) => { DialogResult = false; };
            row.Children.Add(ok);
            row.Children.Add(cancel);
            root.Children.Add(row);
            Content = root;
        }
    }
}