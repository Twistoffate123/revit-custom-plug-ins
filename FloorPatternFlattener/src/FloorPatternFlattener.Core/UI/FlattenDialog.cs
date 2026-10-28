using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using FloorPatternFlattener.Geometry;

namespace FloorPatternFlattener.UI
{
    public enum ApplyScope
    {
        SelectedFloors = 0,
        AllVisibleOfType = 1
    }

    public enum DialogAction
    {
        Cancel,
        Discard,
        Preview,
        Apply,
        PickEdge,
        PickLine
    }

    /// <summary>Everything the Flatten / Edit dialog edits; survives the dialog being closed for picks.</summary>
    public sealed class FlattenDialogState
    {
        public FlattenStyle Style = FlattenStyle.Lines;
        public PatternPlacement Placement = PatternPlacement.Identity;
        public ApplyScope Scope = ApplyScope.SelectedFloors;
        public string Title = "Flatten Floor Patterns";
        public string Info = string.Empty;
        public string Status = string.Empty;
        public bool PreviewLive;
        public bool AllowScope = true;

        public FlattenDialogState Clone() => new FlattenDialogState
        {
            Style = Style, Placement = Placement.Clone(), Scope = Scope, Title = Title, Info = Info,
            Status = Status, PreviewLive = PreviewLive, AllowScope = AllowScope
        };

        /// <summary>Same geometry-relevant settings (style, placement, scope).</summary>
        public bool SameSettings(FlattenDialogState o) =>
            o != null && o.Style == Style && o.Scope == Scope && o.Placement.SameAs(Placement);
    }

    /// <summary>Modal WPF dialog built in code (no XAML, so every Revit year compiles the same file).</summary>
    public sealed class FlattenDialog : Window
    {
        private readonly FlattenDialogState _state;
        private readonly RadioButton _scopeSel, _scopeType;
        private readonly TextBox _angle;
        private readonly TextBlock _anchor, _status;
        public DialogAction Action { get; private set; } = DialogAction.Cancel;

        public FlattenDialog(FlattenDialogState state, IntPtr owner)
        {
            _state = state;
            Title = state.Title;
            Width = 520;
            MinWidth = 380;
            MinHeight = 300;
            SizeToContent = SizeToContent.Height; // initial height only; user resizing turns this off
            ResizeMode = ResizeMode.CanResizeWithGrip;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = 40;
            Top = 120;
            ShowInTaskbar = false;
            if (owner != IntPtr.Zero) new WindowInteropHelper(this).Owner = owner;

            // Layout: action buttons docked at the bottom, everything else in a scrollable panel that stretches
            // with the window (horizontally and vertically).
            var dock = new DockPanel { LastChildFill = true };
            Content = dock;
            var root = new StackPanel { Margin = new Thickness(12, 12, 12, 0) };

            if (!string.IsNullOrEmpty(state.Info))
                root.Children.Add(Text(state.Info, true));

            root.Children.Add(Header("Rotate / align"));
            // WrapPanel: the buttons flow onto the next line when the window is narrow, so none is cut off.
            var row = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock { Text = "Angle (deg, anticlockwise):", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 0, 2) });
            _angle = new TextBox
            {
                Width = 70, Margin = new Thickness(6, 2, 6, 2), VerticalAlignment = VerticalAlignment.Center,
                Text = (state.Placement.NormalizedAngle * 180.0 / Math.PI).ToString("0.###", CultureInfo.InvariantCulture)
            };
            row.Children.Add(_angle);
            row.Children.Add(Button("Align to Edge...", () => Close(DialogAction.PickEdge),
                "Pick a straight model edge (slab edge, kerb, wall) with a direction in plan."));
            row.Children.Add(Button("Align to Line...", () => Close(DialogAction.PickLine),
                "Pick a straight model line, detail line or grid."));
            row.Children.Add(Button("Clear", () =>
            {
                _state.Placement = PatternPlacement.Identity;
                _angle.Text = "0";
                UpdateAnchor();
            }, "Back to the live floor's pattern: no rotation, project origin."));
            root.Children.Add(row);
            _anchor = Text(string.Empty, true);
            root.Children.Add(_anchor);
            UpdateAnchor();

            root.Children.Add(Header("Apply to"));
            _scopeSel = new RadioButton { GroupName = "scope", Content = "Only the selected floor(s)" };
            _scopeType = new RadioButton { GroupName = "scope", Content = "All visible floors of this type (active view)" };
            _scopeSel.IsChecked = state.Scope == ApplyScope.SelectedFloors;
            _scopeType.IsChecked = state.Scope == ApplyScope.AllVisibleOfType;
            _scopeSel.IsEnabled = _scopeType.IsEnabled = state.AllowScope;
            root.Children.Add(_scopeSel);
            root.Children.Add(_scopeType);

            _status = Text(state.Status, true);
            _status.Margin = new Thickness(0, 10, 0, 0);
            root.Children.Add(_status);

            var buttons = new WrapPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(12, 8, 20, 12) // right margin leaves room for the resize grip
            };
            buttons.Children.Add(Button(state.PreviewLive ? "Update Preview" : "Preview", () => Close(DialogAction.Preview),
                "Build the result temporarily and show it in the view; nothing is kept until Apply."));
            buttons.Children.Add(Button("Apply", () => Close(DialogAction.Apply), "Create / replace the flat pattern."));
            buttons.Children.Add(Button(state.PreviewLive ? "Discard" : "Cancel",
                () => Close(state.PreviewLive ? DialogAction.Discard : DialogAction.Cancel),
                "Close without changing the model (a live preview is rolled back)."));
            DockPanel.SetDock(buttons, Dock.Bottom);
            dock.Children.Add(buttons);
            dock.Children.Add(new ScrollViewer
            {
                Content = root,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            });
        }

        private void UpdateAnchor()
        {
            _anchor.Text = _state.Placement.HasAnchor
                ? "Anchored on a picked edge (pattern starts on it). Clear removes it."
                : "No anchor: pattern starts at the project internal origin, like the live floor.";
        }

        private void Close(DialogAction action)
        {
            if (action != DialogAction.Cancel && action != DialogAction.Discard)
            {
                if (!double.TryParse(_angle.Text.Trim().Replace(',', '.'), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var deg) || double.IsNaN(deg) || double.IsInfinity(deg))
                {
                    MessageBox.Show(this, "Enter the angle as a number of degrees.", Title);
                    return;
                }
                // Keep the exact picked angle if the box still shows it (rounded display).
                var shown = (_state.Placement.NormalizedAngle * 180.0 / Math.PI).ToString("0.###", CultureInfo.InvariantCulture);
                if (_angle.Text.Trim() != shown) _state.Placement.AngleDeg = deg;
                _state.Style = FlattenStyle.Lines;
                _state.Scope = _scopeType.IsChecked == true ? ApplyScope.AllVisibleOfType : ApplyScope.SelectedFloors;
            }
            Action = action;
            DialogResult = true;
        }

        private static TextBlock Header(string t) =>
            new TextBlock { Text = t, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 10, 0, 4) };

        private static TextBlock Text(string t, bool wrap) =>
            new TextBlock { Text = t, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, Margin = new Thickness(0, 2, 0, 2) };

        private static Button Button(string text, Action onClick, string tip)
        {
            var b = new Button { Content = text, Margin = new Thickness(4, 2, 0, 2), Padding = new Thickness(8, 2, 8, 2), ToolTip = tip };
            b.Click += (s, e) => onClick();
            return b;
        }
    }

    /// <summary>Small modeless progress window with Cancel, pumped between floors.</summary>
    public sealed class ProgressWindow : Window
    {
        private readonly ProgressBar _bar;
        private readonly TextBlock _text;
        public bool Cancelled { get; private set; }

        public ProgressWindow(string title, int max, IntPtr owner)
        {
            Title = title;
            Width = 380;
            MinWidth = 300;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            if (owner != IntPtr.Zero) new WindowInteropHelper(this).Owner = owner;
            var root = new StackPanel { Margin = new Thickness(12) };
            _text = new TextBlock { Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
            _bar = new ProgressBar { Height = 16, Minimum = 0, Maximum = Math.Max(1, max) };
            var cancel = new Button { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 2, 10, 2) };
            cancel.Click += (s, e) => { Cancelled = true; _text.Text = "Cancelling after the current floor..."; };
            root.Children.Add(_text);
            root.Children.Add(_bar);
            root.Children.Add(cancel);
            Content = root;
            Closing += (s, e) => Cancelled = true;
        }

        /// <summary>Updates the bar and lets the window process clicks; returns false when cancelled.</summary>
        public bool Step(int done, string text)
        {
            _bar.Value = done;
            if (!Cancelled) _text.Text = text;
            DoEvents();
            return !Cancelled;
        }

        private static void DoEvents()
        {
            try
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                    System.Windows.Threading.DispatcherPriority.Background, new Action(() => { }));
            }
            catch
            {
                // ignore
            }
        }
    }
}
