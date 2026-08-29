using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FolderDock
{
    public partial class MainWindow : Window
    {
        private readonly string _folderPath;
        private readonly string _title;
        private bool _ready;
        private bool _dismissing;

        private double _cardWidth;
        private double _maxCardHeight;
        private List<FolderTab> _tabs = new();

        private static readonly string[] Palette =
        {
            "#4C6FFF", "#FF6B6B", "#6BCB77", "#FFB84C", "#845EC2",
            "#00C2A8", "#FF9F43", "#2EC4B6", "#F76E9C", "#5A72FF"
        };

        // These drive the icon grid from XAML. FitGrid() sets them per tab so the
        // whole folder fits the panel without a scrollbar.
        public static readonly DependencyProperty GridColumnsProperty =
            DependencyProperty.Register(nameof(GridColumns), typeof(int), typeof(MainWindow), new PropertyMetadata(6));
        public static readonly DependencyProperty IconSizeProperty =
            DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(MainWindow), new PropertyMetadata(76.0));
        public static readonly DependencyProperty TileWidthProperty =
            DependencyProperty.Register(nameof(TileWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(118.0));
        public static readonly DependencyProperty FallbackFontSizeProperty =
            DependencyProperty.Register(nameof(FallbackFontSize), typeof(double), typeof(MainWindow), new PropertyMetadata(26.0));

        public int GridColumns
        {
            get => (int)GetValue(GridColumnsProperty);
            set => SetValue(GridColumnsProperty, value);
        }
        public double IconSize
        {
            get => (double)GetValue(IconSizeProperty);
            set => SetValue(IconSizeProperty, value);
        }
        public double TileWidth
        {
            get => (double)GetValue(TileWidthProperty);
            set => SetValue(TileWidthProperty, value);
        }
        public double FallbackFontSize
        {
            get => (double)GetValue(FallbackFontSizeProperty);
            set => SetValue(FallbackFontSizeProperty, value);
        }

        public MainWindow(string folderPath, string title)
        {
            InitializeComponent();
            _folderPath = folderPath;
            _title = title;

            // Cover only the primary monitor. Windows always puts the primary
            // monitor's top-left at (0,0), so the popup lands centered on the
            // main screen instead of stretched across a multi-monitor setup.
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;

            // Screen-shaped, moderate: about 60% of the main monitor's width. The
            // panel shrinks to fit a small folder (down to ~2 rows) but grows no
            // taller than the cap. It sits inside a fixed-height area anchored from
            // the top, so the tab pills never move when the panel resizes.
            _cardWidth = Math.Max(560, SystemParameters.PrimaryScreenWidth * 0.60);
            _maxCardHeight = Math.Min(SystemParameters.PrimaryScreenHeight * 0.74, _cardWidth * 0.62);

            Card.Width = _cardWidth;
            Card.MaxHeight = _maxCardHeight;
            Card.MinHeight = 300;
            CardArea.Height = _maxCardHeight;

            Loaded += MainWindow_Loaded;

            LoadTabs();
        }

        // Builds one tab for loose shortcuts sitting directly in the folder, plus
        // one tab for each subfolder that contains shortcuts.
        private void LoadTabs()
        {
            _tabs = new List<FolderTab>();

            var loose = App.GetLaunchableItems(_folderPath);
            if (loose.Length > 0)
                _tabs.Add(new FolderTab(_title, _folderPath, BuildItems(loose)));

            // Subfolders become tabs. A leading number ("1 Games", "2. Work")
            // controls the order and is hidden from the tab label; unnumbered
            // folders follow, alphabetically.
            var dirs = Directory.GetDirectories(_folderPath)
                .Select(d => (Path: d, Name: Path.GetFileName(d)))
                .OrderBy(d => OrderPrefix(d.Name))
                .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var dir in dirs)
            {
                var items = App.GetLaunchableItems(dir.Path);
                if (items.Length > 0)
                    _tabs.Add(new FolderTab(DisplayName(dir.Name), dir.Path, BuildItems(items)));
            }

            if (_tabs.Count == 0)
            {
                // Startup already guarantees there's something to show; this is
                // just a defensive guard.
                Application.Current.Shutdown();
                return;
            }

            TabBar.ItemsSource = _tabs;
            SelectTab(0);
        }

        // Matches an optional leading order number: "1 Games", "2. Work",
        // "10) Media", "3 - Fun". Group 1 = the number, group 2 = the real name.
        private static readonly Regex NumberPrefix =
            new(@"^\s*(\d{1,4})\s*[.)\-_]?\s+(\S.*?)\s*$", RegexOptions.Compiled);

        private static int OrderPrefix(string folderName)
        {
            var m = NumberPrefix.Match(folderName);
            return m.Success ? int.Parse(m.Groups[1].Value) : int.MaxValue;
        }

        private static string DisplayName(string folderName)
        {
            var m = NumberPrefix.Match(folderName);
            return m.Success ? m.Groups[2].Value : folderName;
        }

        private ObservableCollection<AppItem> BuildItems(string[] files)
        {
            var items = new ObservableCollection<AppItem>();
            int i = 0;
            foreach (var file in files)
            {
                string name = Path.GetFileNameWithoutExtension(file);
                var icon = IconHelper.GetIcon(file, out string reason);
                if (icon == null && !string.IsNullOrEmpty(reason))
                    LogIssue(file, reason);

                items.Add(new AppItem
                {
                    Name = name.Length > 18 ? name[..16] + "…" : name,
                    Path = file,
                    Icon = icon,
                    FallbackText = name.Length > 0 ? name[..1].ToUpper() : "?",
                    FallbackColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString(Palette[i % Palette.Length]))
                });
                i++;
            }
            return items;
        }

        private int _selectedIndex;

        private void SelectTab(int index)
        {
            if (index < 0 || index >= _tabs.Count) return;
            _selectedIndex = index;

            for (int i = 0; i < _tabs.Count; i++)
                _tabs[i].Selected = (i == index);

            var tab = _tabs[index];
            AppsGrid.ItemsSource = tab.Items;
            FitGrid(tab.Items.Count);
        }

        // Choose a column count AND an icon size so every app in the tab fits the
        // panel with no scrolling. Bigger icons are preferred; a very full folder
        // just gets smaller ones (down to a floor) instead of a scrollbar.
        private void FitGrid(int count)
        {
            if (count < 1) count = 1;

            const double labelPad = 30;   // tile is wider than the icon, for the label
            const double colGap = labelPad + 22;   // + StackPanel horizontal margin
            const double rowGap = 8 + 16 + 20;     // icon->label gap + label + vertical margin
            const double maxIcon = 76;
            const double minIcon = 44;

            double availW = _cardWidth - 62;        // card padding + border
            double availH = _maxCardHeight - 62;

            for (double s = maxIcon; ; s -= 2)
            {
                int maxCols = Math.Max(1, (int)(availW / (s + colGap)));
                int cols = Math.Min(maxCols, count);
                int rows = (int)Math.Ceiling(count / (double)cols);
                cols = (int)Math.Ceiling(count / (double)rows);   // balance the last row
                double neededH = rows * (s + rowGap);

                if (neededH <= availH || s <= minIcon)
                {
                    IconSize = s;
                    TileWidth = s + labelPad;
                    FallbackFontSize = Math.Round(s * 0.42);
                    GridColumns = Math.Max(1, cols);
                    return;
                }
            }
        }

        private static void LogIssue(string path, string reason)
        {
            try
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon_debug.log");
                File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {path} | {reason}\n");
            }
            catch
            {
                // Best-effort logging only.
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            var scaleX = new DoubleAnimation(0.6, 1.0, TimeSpan.FromMilliseconds(260))
            { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.2 } };
            var scaleY = new DoubleAnimation(0.6, 1.0, TimeSpan.FromMilliseconds(260))
            { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.2 } };
            var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180));

            PopupScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
            PopupScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
            PopupGroup.BeginAnimation(OpacityProperty, fadeIn);

            Activate();
            _ready = true;
        }

        // Every dismissal path funnels through here. Launching an app steals focus,
        // which fires OnDeactivated, which also wants to close - without this guard
        // the second Close() lands while the first is still unwinding and WPF throws
        // "Cannot call Close while a Window is closing", wedging the dispatcher.
        private void Dismiss()
        {
            if (_dismissing) return;
            _dismissing = true;
            Close();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key == Key.Escape)
                Dismiss();
        }

        protected override void OnDeactivated(EventArgs e)
        {
            base.OnDeactivated(e);
            if (_ready)
                Dismiss();
        }

        private void RootGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Dismiss();

        private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

        private void CloseButton_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            Dismiss();
        }

        // Opens the current tab's real folder in Explorer so shortcuts can be
        // dragged in or removed - handy when the folder itself is hidden.
        private void OpenFolder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            try
            {
                string path = _selectedIndex >= 0 && _selectedIndex < _tabs.Count
                    ? _tabs[_selectedIndex].Path
                    : _folderPath;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Couldn't open the folder:\n{ex.Message}", "My Apps");
            }
            Dismiss();
        }

        // Tabs and app tiles act only on a full click - button pressed AND released
        // on the SAME element. Switching a tab on mouse-down would swap the grid out
        // from under the cursor, so the following mouse-up would land on whatever
        // icon appeared there and launch it.
        private object? _pressed;

        private void Tab_Down(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            _pressed = (sender as FrameworkElement)?.DataContext as FolderTab;
        }

        private void Tab_Up(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if ((sender as FrameworkElement)?.DataContext is FolderTab tab && ReferenceEquals(tab, _pressed))
            {
                int index = _tabs.IndexOf(tab);
                if (index >= 0) SelectTab(index);
            }
            _pressed = null;
        }

        private void AppIcon_Down(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            _pressed = (sender as FrameworkElement)?.DataContext as AppItem;
            _dragItem = _pressed as AppItem;
            _pressPoint = e.GetPosition(this);
            _dragging = false;
        }

        private void AppIcon_Up(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            bool launch = !_dragging
                          && (sender as FrameworkElement)?.DataContext is AppItem item
                          && ReferenceEquals(item, _pressed);
            var target = _pressed as AppItem;
            _pressed = null;
            _dragItem = null;

            if (!launch || target == null) return;

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(target.Path) { UseShellExecute = true };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Couldn't open that:\n{ex.Message}", "My Apps");
            }
            Dismiss();
        }

        // --- Drag an icon to reorder it within its tab -------------------------

        private Point _pressPoint;
        private AppItem? _dragItem;
        private bool _dragging;

        private void AppIcon_Move(object sender, MouseEventArgs e)
        {
            if (_dragItem == null || _dragging || e.LeftButton != MouseButtonState.Pressed)
                return;

            // A bit past the system threshold, so a slightly shaky click still
            // launches the app instead of starting a drag.
            Point p = e.GetPosition(this);
            if (Math.Abs(p.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance * 2 &&
                Math.Abs(p.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance * 2)
                return;

            _dragging = true;
            _pressed = null;                 // this gesture is a drag, not a launch
            var src = _dragItem;
            src.Dragging = true;

            try
            {
                DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(AppItem), src), DragDropEffects.Move);
            }
            finally
            {
                src.Dragging = false;
                _dragItem = null;
                PersistCurrentOrder();
                // Let the trailing mouse-up land before we re-enable launching.
                Dispatcher.BeginInvoke(new Action(() => _dragging = false),
                                       System.Windows.Threading.DispatcherPriority.Input);
            }
        }

        private void AppsGrid_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;

            if (_selectedIndex < 0 || _selectedIndex >= _tabs.Count) return;
            if (e.Data.GetData(typeof(AppItem)) is not AppItem drag) return;

            var items = _tabs[_selectedIndex].Items;
            int from = items.IndexOf(drag);
            if (from < 0) return;

            int to = DropIndex(e.GetPosition(AppsGrid), items);
            if (to >= 0 && to != from)
                items.Move(from, to);
        }

        private void AppsGrid_Drop(object sender, DragEventArgs e) => e.Handled = true;

        // Which slot is the cursor over? Walk up from the hit visual to the tile
        // bound to an AppItem; if the cursor is past the last tile, target the end.
        private int DropIndex(Point pos, ObservableCollection<AppItem> items)
        {
            DependencyObject? el = VisualTreeHelper.HitTest(AppsGrid, pos)?.VisualHit;
            while (el != null && !(el is FrameworkElement fe && fe.DataContext is AppItem))
                el = VisualTreeHelper.GetParent(el);

            if (el is FrameworkElement f && f.DataContext is AppItem over)
                return items.IndexOf(over);

            return items.Count - 1;
        }

        private void PersistCurrentOrder()
        {
            if (_selectedIndex < 0 || _selectedIndex >= _tabs.Count) return;
            var tab = _tabs[_selectedIndex];
            App.SaveOrder(tab.Path, tab.Items.Select(it => Path.GetFileName(it.Path)));
        }
    }

    public class FolderTab : System.ComponentModel.INotifyPropertyChanged
    {
        public FolderTab(string name, string path, ObservableCollection<AppItem> items)
        {
            Name = name;
            Path = path;
            Items = items;
        }

        public string Name { get; }
        public string Path { get; }
        public ObservableCollection<AppItem> Items { get; }

        private bool _selected;
        public bool Selected
        {
            get => _selected;
            set
            {
                if (_selected == value) return;
                _selected = value;
                Raise(nameof(Selected));
                Raise(nameof(TabBackground));
                Raise(nameof(TabForeground));
            }
        }

        public Brush TabBackground => Selected
            ? new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF))
            : Brushes.Transparent;

        public Brush TabForeground => Selected
            ? Brushes.Black
            : new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string name) =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }

    public class AppItem : INotifyPropertyChanged
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public ImageSource? Icon { get; set; }
        public Visibility IconVisibility => Icon != null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility FallbackVisibility => Icon == null ? Visibility.Visible : Visibility.Collapsed;
        public string FallbackText { get; set; } = "";
        public Brush FallbackColor { get; set; } = Brushes.Gray;

        // No coloured square behind a real icon; only the letter fallback needs it.
        public Brush TileBackground => Icon != null ? Brushes.Transparent : FallbackColor;

        private bool _dragging;
        public bool Dragging
        {
            get => _dragging;
            set
            {
                if (_dragging == value) return;
                _dragging = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TileOpacity)));
            }
        }

        // The tile fades while it's the one being dragged.
        public double TileOpacity => _dragging ? 0.35 : 1.0;

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
