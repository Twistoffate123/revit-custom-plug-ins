using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FloorPatternFlattener.UI
{
    public sealed class LiftMmDialog : Window
    {
        private readonly TextBox _box;

        public double ValueMm { get; private set; }

        public LiftMmDialog(double suggestedMm)
        {
            Title = FpfOptions.ProductName + " – lift";
            MinWidth = 420;
            Width = 440;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WpfDialog.Prepare(this, SizeToContent.Height);

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
                MinHeight = 24,
                Margin = new Thickness(0, 0, 0, 12)
            };
            root.Children.Add(_box);
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 16, 4)
            };
            var ok = new Button
            {
                Content = "OK",
                MinWidth = 80,
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true
            };
            ok.Click += (_, __) => Accept();
            var cancel = new Button
            {
                Content = "Cancel",
                MinWidth = 80,
                Padding = new Thickness(8, 4, 8, 4),
                IsCancel = true
            };
            cancel.Click += (_, __) => { DialogResult = false; };
            row.Children.Add(ok);
            row.Children.Add(cancel);
            root.Children.Add(row);
            Content = root;

            Loaded += (_, __) =>
            {
                _box.Focus();
                _box.SelectAll();
            };
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    Accept();
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    DialogResult = false;
                    e.Handled = true;
                }
            };
        }

        private void Accept()
        {
            if (!double.TryParse(_box.Text.Trim().Replace(',', '.'), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var v) || double.IsNaN(v))
            {
                MessageBox.Show(this, "Enter a number of millimetres.", Title);
                return;
            }
            ValueMm = v;
            DialogResult = true;
        }
    }
}
