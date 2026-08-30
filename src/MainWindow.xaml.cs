// ─────────────────────────────────────────────────────────────────────────────
//  MainWindow.xaml.cs
//
//  The popup itself. MainWindow.xaml lays out the visuals (tab bar + card +
//  icon grid); this file is the behaviour behind it:
//    • build the tabs from the folder + its subfolders          → LoadTabs / BuildItems
//    • size the grid so a whole tab fits with no scrollbar      → FitGrid
//    • click a tab to switch, click an icon to launch           → Tab_* / AppIcon_*
//    • drag an icon to reorder it, and remember the order       → AppIcon_Move / AppsGrid_DragOver
//    • close on Esc / click-away / ✕ / focus loss               → Dismiss + overrides
//
//  Two little data classes at the bottom (FolderTab, AppItem) are what the XAML
//  binds to.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;                 // List<>
using System.Collections.ObjectModel;             // ObservableCollection<> — a list the UI watches for add/remove/move
using System.ComponentModel;                       // INotifyPropertyChanged — lets a bound value tell the UI it changed
using System.IO;                                   // Path
using System.Linq;                                 // Select / OrderBy / IndexOf helpers
using System.Text.RegularExpressions;              // Regex — for the "1 Games" tab-name prefix
using System.Windows;                              // Window, Point, SystemParameters, DependencyProperty…
using System.Windows.Input;                        // mouse / key event args
using System.Windows.Media;                        // Brush, Color, VisualTreeHelper
using System.Windows.Media.Animation;              // DoubleAnimation — the open/scale/fade

namespace FolderDock
{
    /// <summary>The floating launcher window. One instance, created by App at startup.</summary>
    public partial class MainWindow : Window
    {
        // ── State this window keeps ─────────────────────────────────────────
        private readonly string _folderPath;   // root folder we read (e.g. %APPDATA%\MyApps)
        private readonly string _title;        // label for the "loose shortcuts" tab
        private bool _ready;                    // true once the open animation has run (see MainWindow_Loaded)
        private bool _dismissing;              // true once we've started closing — stops Close() being called twice

        private double _cardWidth;             // fixed width of the panel, worked out from the screen size
        private double _maxCardHeight;         // tallest the panel may get before icons start shrinking
        private List<FolderTab> _tabs = new(); // one entry per tab (each holds its own list of icons)

        // Fallback tile colours, cycled through for apps whose real icon can't be read.
        private static readonly string[] Palette =
        {
            "#4C6FFF", "#FF6B6B", "#6BCB77", "#FFB84C", "#845EC2",
            "#00C2A8", "#FF9F43", "#2EC4B6", "#F76E9C", "#5A72FF"
        };

        // ── Values the XAML grid binds to ──────────────────────────────────
        // These are "dependency properties": normal-looking properties that WPF
        // data-binding can watch. FitGrid() sets them per tab and the grid in
        // MainWindow.xaml updates itself (column count, icon size, label size).
        // Pattern for each: a static XxxProperty registration + a plain wrapper.

        public static readonly DependencyProperty GridColumnsProperty =
            DependencyProperty.Register(nameof(GridColumns), typeof(int), typeof(MainWindow), new PropertyMetadata(6));
        public static readonly DependencyProperty IconSizeProperty =
            DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(MainWindow), new PropertyMetadata(76.0));
        public static readonly DependencyProperty TileWidthProperty =
            DependencyProperty.Register(nameof(TileWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(118.0));
        public static readonly DependencyProperty FallbackFontSizeProperty =
            DependencyProperty.Register(nameof(FallbackFontSize), typeof(double), typeof(MainWindow), new PropertyMetadata(26.0));

        /// <summary>How many columns the icon grid uses.</summary>
        public int GridColumns
        {
            get => (int)GetValue(GridColumnsProperty);      // read from WPF's property store
            set => SetValue(GridColumnsProperty, value);    // write to it (this is what notifies the binding)
        }
        /// <summary>Width/height of each icon, in device-independent pixels.</summary>
        public double IconSize
        {
            get => (double)GetValue(IconSizeProperty);
            set => SetValue(IconSizeProperty, value);
        }
        /// <summary>Width of a whole tile (icon + a bit extra for the label).</summary>
        public double TileWidth
        {
            get => (double)GetValue(TileWidthProperty);
            set => SetValue(TileWidthProperty, value);
        }
        /// <summary>Font size for the first-letter fallback drawn when there's no real icon.</summary>
        public double FallbackFontSize
        {
            get => (double)GetValue(FallbackFontSizeProperty);
            set => SetValue(FallbackFontSizeProperty, value);
        }

        /// <summary>
        /// Built by App at startup. <paramref name="folderPath"/> is the root
        /// folder; <paramref name="title"/> labels the loose-shortcuts tab.
        /// </summary>
        public MainWindow(string folderPath, string title)
        {
            InitializeComponent();     // WPF-generated: builds everything defined in MainWindow.xaml
            _folderPath = folderPath;
            _title = title;

            // Make the window cover the whole primary monitor. The visible panel is
            // centred inside it; the rest is an invisible catch-area so a click
            // anywhere outside the panel can close the popup.
            // Windows always places the primary monitor's top-left at (0,0), so this
            // stays on the main screen even with a second monitor attached.
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;

            // Panel size: ~60% of the screen width (with a sane minimum), and a
            // height cap that's the smaller of "74% of the screen" and "0.62 × its
            // own width" (keeps it roughly screen-shaped). A small tab can shrink
            // down to MinHeight; a big one grows to _maxCardHeight and then FitGrid
            // shrinks the icons instead of adding a scrollbar.
            _cardWidth = Math.Max(560, SystemParameters.PrimaryScreenWidth * 0.60);
            _maxCardHeight = Math.Min(SystemParameters.PrimaryScreenHeight * 0.74, _cardWidth * 0.62);

            Card.Width = _cardWidth;             // Card / CardArea are named elements from the XAML
            Card.MaxHeight = _maxCardHeight;
            Card.MinHeight = 300;
            CardArea.Height = _maxCardHeight;    // fixed-height wrapper: keeps the tab pills from moving when the card resizes

            Loaded += MainWindow_Loaded;         // run the open animation once the window is up

            LoadTabs();                          // read the folder and build the tabs now
        }

        /// <summary>
        /// Reads the folder and fills <see cref="_tabs"/>: one tab for shortcuts
        /// sitting loose in the root, then one per subfolder that contains shortcuts.
        /// </summary>
        private void LoadTabs()
        {
            _tabs = new List<FolderTab>();

            // Loose shortcuts directly in the root → a first tab named after _title.
            var loose = App.GetLaunchableItems(_folderPath);
            if (loose.Length > 0)
                _tabs.Add(new FolderTab(_title, _folderPath, BuildItems(loose)));

            // Each subfolder → a tab. Order by a leading number on the folder name
            // ("1 Games" before "2 Work"); folders with no number come after, A–Z.
            var dirs = Directory.GetDirectories(_folderPath)
                .Select(d => (Path: d, Name: Path.GetFileName(d)))        // keep the full path + just the folder name
                .OrderBy(d => OrderPrefix(d.Name))                       // primary sort: the leading number (or "no number")
                .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase);  // tie-break: alphabetical

            foreach (var dir in dirs)
            {
                var items = App.GetLaunchableItems(dir.Path);
                if (items.Length > 0)                                     // skip empty subfolders
                    _tabs.Add(new FolderTab(DisplayName(dir.Name), dir.Path, BuildItems(items)));  // label = name minus the number
            }

            if (_tabs.Count == 0)
            {
                // App already checked the folder isn't empty, so this shouldn't
                // happen — but if it somehow does, just quit rather than show a
                // blank popup.
                Application.Current.Shutdown();
                return;
            }

            TabBar.ItemsSource = _tabs;   // hand the tab list to the pill strip in the XAML
            SelectTab(0);                 // show the first tab
        }

        // Optional leading order-number on a folder name:
        //   "1 Games"  "2. Work"  "10) Media"  "3 - Fun"  "01_Stuff"
        // Group 1 = the number, Group 2 = the rest (the label to show).
        //   ^\s*          leading spaces
        //   (\d{1,4})     1–4 digits            → group 1
        //   \s*[.)\-_]?   optional . ) - or _
        //   \s+           at least one space
        //   (\S.*?)       the real name          → group 2
        //   \s*$          trailing spaces
        private static readonly Regex NumberPrefix =
            new(@"^\s*(\d{1,4})\s*[.)\-_]?\s+(\S.*?)\s*$", RegexOptions.Compiled);

        /// <summary>The folder's order number, or int.MaxValue if it has none (sorts last).</summary>
        private static int OrderPrefix(string folderName)
        {
            var m = NumberPrefix.Match(folderName);
            return m.Success ? int.Parse(m.Groups[1].Value) : int.MaxValue;
        }

        /// <summary>The folder name with any leading order number stripped off.</summary>
        private static string DisplayName(string folderName)
        {
            var m = NumberPrefix.Match(folderName);
            return m.Success ? m.Groups[2].Value : folderName;
        }

        /// <summary>
        /// Turns a list of shortcut paths into <see cref="AppItem"/>s the grid can
        /// show: display name, resolved icon (or a coloured first-letter fallback).
        /// Returns an ObservableCollection so drag-reorder can Move() items and the
        /// grid updates live.
        /// </summary>
        private ObservableCollection<AppItem> BuildItems(string[] files)
        {
            var items = new ObservableCollection<AppItem>();
            int i = 0;
            foreach (var file in files)
            {
                string name = Path.GetFileNameWithoutExtension(file);        // "Discord.lnk" → "Discord"
                var icon = IconHelper.GetIcon(file, out string reason);      // try to get the real icon
                if (icon == null && !string.IsNullOrEmpty(reason))           // failed, and told us why →
                    LogIssue(file, reason);                                  // note it in icon_debug.log

                items.Add(new AppItem
                {
                    Name = name.Length > 18 ? name[..16] + "…" : name,       // clip long names to keep tiles tidy
                    Path = file,                                             // what to launch / what to save in .order
                    Icon = icon,                                             // null → the fallback tile shows instead
                    FallbackText = name.Length > 0 ? name[..1].ToUpper() : "?",   // first letter, uppercased
                    FallbackColor = new SolidColorBrush(                     // pick the next palette colour, wrapping round
                        (Color)ColorConverter.ConvertFromString(Palette[i % Palette.Length]))
                });
                i++;
            }
            return items;
        }

        private int _selectedIndex;   // which tab is showing

        /// <summary>Switches to tab <paramref name="index"/>: highlight its pill, show its icons, resize the grid.</summary>
        private void SelectTab(int index)
        {
            if (index < 0 || index >= _tabs.Count) return;   // ignore a bad index
            _selectedIndex = index;

            for (int i = 0; i < _tabs.Count; i++)
                _tabs[i].Selected = (i == index);            // only the chosen tab is "selected" (drives pill colours)

            var tab = _tabs[index];
            AppsGrid.ItemsSource = tab.Items;                // point the grid at this tab's icons
            FitGrid(tab.Items.Count);                        // choose columns + icon size for that many icons
        }

        /// <summary>
        /// Picks a column count AND an icon size so all <paramref name="count"/>
        /// icons fit the panel without scrolling. It prefers big icons and only
        /// shrinks them (down to a floor) when a folder is very full.
        /// </summary>
        private void FitGrid(int count)
        {
            if (count < 1) count = 1;   // guard against divide-by-zero below

            // Rough per-tile spacing, measured from the XAML template.
            const double labelPad = 30;             // a tile is this much wider than its icon (room for the label)
            const double colGap   = labelPad + 22;  // horizontal space each column needs beyond the icon itself
            const double rowGap   = 8 + 16 + 20;    // vertical space each row needs beyond the icon (gap + label + margin)
            const double maxIcon  = 76;             // biggest icon we'll use
            const double minIcon  = 44;             // smallest we'll shrink to before just accepting it

            double availW = _cardWidth - 62;        // usable width inside the card (minus padding + border)
            double availH = _maxCardHeight - 62;    // usable height

            // Try the biggest icon size, step down by 2px until the rows fit the
            // height (or we hit the minimum and accept it).
            for (double s = maxIcon; ; s -= 2)
            {
                int maxCols = Math.Max(1, (int)(availW / (s + colGap)));     // how many columns fit the width at size s
                int cols = Math.Min(maxCols, count);                        // don't use more columns than there are icons
                int rows = (int)Math.Ceiling(count / (double)cols);         // rows needed for that many columns
                cols = (int)Math.Ceiling(count / (double)rows);             // pull columns back in so the last row isn't lonely
                double neededH = rows * (s + rowGap);                       // total height those rows take

                if (neededH <= availH || s <= minIcon)                      // fits? or can't shrink further? → commit
                {
                    IconSize = s;                                          // (these four setters update the grid via binding)
                    TileWidth = s + labelPad;
                    FallbackFontSize = Math.Round(s * 0.42);               // scale the fallback letter with the icon
                    GridColumns = Math.Max(1, cols);
                    return;
                }
            }
        }

        /// <summary>Appends one line to icon_debug.log (next to the .exe) explaining why an icon failed.</summary>
        private static void LogIssue(string path, string reason)
        {
            try
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon_debug.log");
                File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {path} | {reason}\n");
            }
            catch
            {
                // If even the log can't be written, there's nothing useful to do.
            }
        }

        /// <summary>Runs once when the window first appears: the grow-and-fade-in animation.</summary>
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Scale from 60% → 100% with a slight overshoot ("BackEase") for a lively pop.
            var scaleX = new DoubleAnimation(0.6, 1.0, TimeSpan.FromMilliseconds(260))
            { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.2 } };
            var scaleY = new DoubleAnimation(0.6, 1.0, TimeSpan.FromMilliseconds(260))
            { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.2 } };
            var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180));   // opacity 0 → 1

            PopupScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);  // PopupScale / PopupGroup are named in the XAML
            PopupScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
            PopupGroup.BeginAnimation(OpacityProperty, fadeIn);

            Activate();        // make sure the window has keyboard focus so Esc works
            _ready = true;     // from now on, losing focus is a real "click away" → allow close-on-deactivate
        }

        /// <summary>
        /// The single place the window closes from. Launching an app steals focus,
        /// which fires OnDeactivated, which also wants to close — without the
        /// <see cref="_dismissing"/> guard the second Close() would land while the
        /// first is still unwinding and WPF throws "Cannot call Close while a
        /// Window is closing", wedging the app.
        /// </summary>
        private void Dismiss()
        {
            if (_dismissing) return;   // already closing → do nothing
            _dismissing = true;
            Close();
        }

        /// <summary>Esc closes the popup. (WPF calls this for any key press while the window has focus.)</summary>
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key == Key.Escape)
                Dismiss();
        }

        /// <summary>Clicking away to another window closes the popup — but only after it's finished opening.</summary>
        protected override void OnDeactivated(EventArgs e)
        {
            base.OnDeactivated(e);
            if (_ready)          // ignore the transient focus wobble during startup
                Dismiss();
        }

        /// <summary>A click on the full-screen background (outside the panel) closes the popup.</summary>
        private void RootGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Dismiss();

        /// <summary>
        /// A click that lands on the panel is swallowed here (e.Handled = true) so
        /// it doesn't bubble up to RootGrid and close the popup.
        /// </summary>
        private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

        /// <summary>The ✕ button.</summary>
        private void CloseButton_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;   // don't let it also count as a background click
            Dismiss();
        }

        /// <summary>
        /// The 📁 button: opens the current tab's real folder in Explorer so you
        /// can drag shortcuts in or delete them. Handy since the folder is tucked
        /// away in AppData.
        /// </summary>
        private void OpenFolder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            try
            {
                string path = _selectedIndex >= 0 && _selectedIndex < _tabs.Count
                    ? _tabs[_selectedIndex].Path          // the selected tab's folder…
                    : _folderPath;                        // …or the root as a fallback
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Couldn't open the folder:\n{ex.Message}", "My Apps");
            }
            Dismiss();   // opening Explorer takes focus anyway; close the popup cleanly
        }

        // ── Clicks on tabs and icons ───────────────────────────────────────
        //
        // Both act only on a *full* click — pressed AND released on the same
        // element — tracked via _pressed. If we switched tabs on mouse-DOWN, the
        // grid would swap out from under the cursor and the following mouse-UP
        // would land on whatever icon appeared there and launch it.

        private object? _pressed;   // the FolderTab or AppItem the mouse went down on

        /// <summary>Mouse pressed on a tab pill — remember which one.</summary>
        private void Tab_Down(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            _pressed = (sender as FrameworkElement)?.DataContext as FolderTab;   // the tab this pill represents
        }

        /// <summary>Mouse released on a tab pill — if it's the same one, switch to it.</summary>
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

        /// <summary>Mouse pressed on an app tile — remember it, and record the spot in case this becomes a drag.</summary>
        private void AppIcon_Down(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            _pressed = (sender as FrameworkElement)?.DataContext as AppItem;   // the app this tile represents
            _dragItem = _pressed as AppItem;                                  // candidate for a drag-reorder
            _pressPoint = e.GetPosition(this);                                // where the press happened (drag threshold)
            _dragging = false;                                                // not dragging yet
        }

        /// <summary>Mouse released on an app tile — if it was a clean click (no drag), launch the app.</summary>
        private void AppIcon_Up(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            bool launch = !_dragging                                           // not the tail of a drag,
                          && (sender as FrameworkElement)?.DataContext is AppItem item
                          && ReferenceEquals(item, _pressed);                  // and released on the same tile we pressed
            var target = _pressed as AppItem;
            _pressed = null;
            _dragItem = null;

            if (!launch || target == null) return;                            // a drag, or released elsewhere → do nothing

            try
            {
                // UseShellExecute = true → let Windows open it the normal way
                // (runs .exe, follows .lnk, opens steam:// from a .url, …).
                var psi = new System.Diagnostics.ProcessStartInfo(target.Path) { UseShellExecute = true };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Couldn't open that:\n{ex.Message}", "My Apps");
            }
            Dismiss();                                                        // launched → close the popup
        }

        // ── Drag an icon to reorder it within its tab ──────────────────────

        private Point _pressPoint;    // where the mouse went down (set in AppIcon_Down)
        private AppItem? _dragItem;   // the icon that might be dragged
        private bool _dragging;       // true once a drag has actually started

        /// <summary>
        /// Mouse moved with the button held. Once it's moved far enough, start a
        /// drag: fade the tile, then run WPF's drag loop. While the drag is live,
        /// <see cref="AppsGrid_DragOver"/> does the actual reordering.
        /// </summary>
        private void AppIcon_Move(object sender, MouseEventArgs e)
        {
            if (_dragItem == null || _dragging || e.LeftButton != MouseButtonState.Pressed)
                return;   // nothing pressed, already dragging, or button not down → ignore

            // Only treat it as a drag once the pointer leaves a small dead-zone
            // around the press point — doubled from the system default so a
            // slightly shaky click still launches instead of dragging.
            Point p = e.GetPosition(this);
            if (Math.Abs(p.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance * 2 &&
                Math.Abs(p.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance * 2)
                return;

            _dragging = true;
            _pressed = null;           // this gesture is a drag, not a launch — cancel the pending click
            var src = _dragItem;
            src.Dragging = true;       // fade this tile to ~35% while it's being dragged

            try
            {
                // Blocks here running WPF's modal drag loop until the button is
                // released. The payload is just the AppItem being moved.
                DragDrop.DoDragDrop((DependencyObject)sender,
                                    new DataObject(typeof(AppItem), src),
                                    DragDropEffects.Move);
            }
            finally
            {
                src.Dragging = false;      // un-fade
                _dragItem = null;
                PersistCurrentOrder();     // write the new order to the tab's .order file
                // The button-up that ended the drag is still coming; let it land
                // before we clear _dragging, so AppIcon_Up doesn't treat it as a click.
                Dispatcher.BeginInvoke(new Action(() => _dragging = false),
                                       System.Windows.Threading.DispatcherPriority.Input);
            }
        }

        /// <summary>
        /// Fires continuously while an icon is dragged over the grid. Works out
        /// which slot the cursor is over and moves the dragged item there, so the
        /// grid reflows live (iOS-style).
        /// </summary>
        private void AppsGrid_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = DragDropEffects.Move;   // show the "move" cursor
            e.Handled = true;

            if (_selectedIndex < 0 || _selectedIndex >= _tabs.Count) return;
            if (e.Data.GetData(typeof(AppItem)) is not AppItem drag) return;   // not our drag payload → ignore

            var items = _tabs[_selectedIndex].Items;
            int from = items.IndexOf(drag);         // where the dragged icon is now
            if (from < 0) return;

            int to = DropIndex(e.GetPosition(AppsGrid), items);   // slot under the cursor
            if (to >= 0 && to != from)
                items.Move(from, to);               // ObservableCollection.Move → the grid animates the reflow
        }

        /// <summary>Drop landed — the reordering already happened in DragOver, so just mark it handled.</summary>
        private void AppsGrid_Drop(object sender, DragEventArgs e) => e.Handled = true;

        /// <summary>
        /// Which grid slot is <paramref name="pos"/> over? Hit-test the visual
        /// under the cursor, then walk up its parents until we reach the element
        /// bound to an AppItem. If the cursor is past the last tile, target the end.
        /// </summary>
        private int DropIndex(Point pos, ObservableCollection<AppItem> items)
        {
            DependencyObject? el = VisualTreeHelper.HitTest(AppsGrid, pos)?.VisualHit;   // deepest visual at that point
            while (el != null && !(el is FrameworkElement fe && fe.DataContext is AppItem))
                el = VisualTreeHelper.GetParent(el);                                     // climb toward the tile root

            if (el is FrameworkElement f && f.DataContext is AppItem over)
                return items.IndexOf(over);        // over a tile → that tile's index

            return items.Count - 1;                // over empty space past the tiles → the last slot
        }

        /// <summary>Writes the current tab's icon order to its hidden .order file.</summary>
        private void PersistCurrentOrder()
        {
            if (_selectedIndex < 0 || _selectedIndex >= _tabs.Count) return;
            var tab = _tabs[_selectedIndex];
            App.SaveOrder(tab.Path, tab.Items.Select(it => Path.GetFileName(it.Path)));   // just the leaf names, in order
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  Data the XAML binds to
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// One tab: a display name, the folder it maps to, and its icons. Implements
    /// INotifyPropertyChanged so toggling <see cref="Selected"/> repaints the pill
    /// (its background/foreground brushes depend on it).
    /// </summary>
    public class FolderTab : System.ComponentModel.INotifyPropertyChanged
    {
        public FolderTab(string name, string path, ObservableCollection<AppItem> items)
        {
            Name = name;
            Path = path;
            Items = items;
        }

        public string Name { get; }                          // pill label (number prefix already stripped)
        public string Path { get; }                          // the folder this tab reads / saves .order in
        public ObservableCollection<AppItem> Items { get; }  // this tab's icons (watched by the grid)

        private bool _selected;
        /// <summary>True for the one visible tab. Setting it repaints this pill.</summary>
        public bool Selected
        {
            get => _selected;
            set
            {
                if (_selected == value) return;   // no change → don't bother notifying
                _selected = value;
                Raise(nameof(Selected));
                Raise(nameof(TabBackground));     // these two are computed from _selected, so the
                Raise(nameof(TabForeground));     // UI needs to re-read them too
            }
        }

        /// <summary>Pill fill: solid white when selected, transparent otherwise.</summary>
        public Brush TabBackground => Selected
            ? new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF))
            : Brushes.Transparent;

        /// <summary>Pill text: black when selected, faint white otherwise.</summary>
        public Brush TabForeground => Selected
            ? Brushes.Black
            : new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));

        // INotifyPropertyChanged plumbing: raise this and any binding to <name> re-reads it.
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string name) =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// One app tile. Holds the display name, the path to launch, and either a real
    /// <see cref="Icon"/> or the pieces for a coloured first-letter fallback. The
    /// Visibility/Brush members are what the XAML template binds to so it can show
    /// one or the other. Implements INotifyPropertyChanged just for the drag fade.
    /// </summary>
    public class AppItem : INotifyPropertyChanged
    {
        public string Name { get; set; } = "";          // label under the icon (possibly clipped with "…")
        public string Path { get; set; } = "";          // full path to the shortcut — launched, and stored in .order
        public ImageSource? Icon { get; set; }          // the real icon, or null if it couldn't be read

        /// <summary>Show the real icon only when we have one.</summary>
        public Visibility IconVisibility => Icon != null ? Visibility.Visible : Visibility.Collapsed;
        /// <summary>Show the fallback tile only when we don't.</summary>
        public Visibility FallbackVisibility => Icon == null ? Visibility.Visible : Visibility.Collapsed;

        public string FallbackText { get; set; } = "";                 // the first letter
        public Brush FallbackColor { get; set; } = Brushes.Gray;       // its background colour (from the palette)

        /// <summary>No coloured square behind a real icon; only the letter fallback needs one.</summary>
        public Brush TileBackground => Icon != null ? Brushes.Transparent : FallbackColor;

        private bool _dragging;
        /// <summary>True while this exact tile is the one being dragged (drives <see cref="TileOpacity"/>).</summary>
        public bool Dragging
        {
            get => _dragging;
            set
            {
                if (_dragging == value) return;
                _dragging = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TileOpacity)));  // tell the binding to re-read
            }
        }

        /// <summary>Full opacity normally; faded while being dragged.</summary>
        public double TileOpacity => _dragging ? 0.35 : 1.0;

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
