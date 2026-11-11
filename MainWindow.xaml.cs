using System.IO;
using System.Text.Json;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Velopack;
using Velopack.Exceptions;

namespace LightweightBrowser;

public partial class MainWindow : Window
{
    private static readonly TimeSpan RapidSiteChangeWindow = TimeSpan.FromSeconds(5);
    private static readonly Uri UpdateFeedUri =
        new("https://github.com/VelJustPlay/Klyro/releases/latest/download");
    private const int RapidSiteChangeThreshold = 3;
    private const double BrowserTabWidth = 180;
    private const double FullscreenChromeHeight = 94;
    private static readonly HashSet<string> AuthenticationHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "apple.com", "icloud.com", "google.com", "microsoft.com", "live.com",
        "microsoftonline.com", "amazon.com"
    };

    private readonly List<BrowserTab> _browserTabs = [];
    private readonly HashSet<string> _listedNoteDownloads = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<BrowserHistoryEntry> _history = [];
    private readonly string _dataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LightBrowser");
    private string HistoryFilePath => Path.Combine(_dataFolder, "history.json");
    private KlyroSearchIndex? _searchIndex;
    private CoreWebView2Environment? _environment;
    private Task<CoreWebView2Environment>? _environmentTask;
    private int _downloadPanelAnimationVersion;
    private BrowserTab? _draggedTab;
    private Point _dragStartPoint;
    private readonly DispatcherTimer _scrollAnimationTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _updateCheckTimer = new() { Interval = TimeSpan.FromHours(6) };
    private readonly DispatcherTimer _fullscreenHideTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private readonly DispatcherTimer _fullscreenEdgeTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private ScrollViewer? _tabScrollViewer;
    private double _scrollAnimationStart;
    private double _scrollAnimationTarget;
    private DateTime _scrollAnimationStarted;
    private double _zoomFactor = 1.0;
    private bool _isFullscreen;
    private bool _fullscreenChromeVisible;
    private WindowState _previousWindowState;
    private WindowStyle _previousWindowStyle;
    private ResizeMode _previousResizeMode;
    private HwndSource? _windowSource;
    private readonly UpdateManager _updateManager = new(UpdateFeedUri.AbsoluteUri);
    private UpdateInfo? _availableUpdate;
    private bool _isUpdateCheckInProgress;
    private bool _isUpdateInstallInProgress;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    public MainWindow()
    {
        InitializeComponent();
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) =>
        SystemCommands.MinimizeWindow(this);

    private void ToggleWindowState_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void CloseWindow_Click(object sender, RoutedEventArgs e) =>
        SystemCommands.CloseWindow(this);

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        var isMaximized = WindowState == WindowState.Maximized;
        WindowStateButton.Content = isMaximized ? "❐" : "□";
        WindowStateButton.ToolTip = isMaximized ? "Restore" : "Maximize";
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource.AddHook(WindowMessageHook);
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message is not (0x0100 or 0x0104))
            return IntPtr.Zero;

        var key = wParam.ToInt32();
        if (key == 0x7A)
        {
            ToggleFullscreen();
            handled = true;
            return new IntPtr(1);
        }

        if (GetKeyState(0x11) >= 0 || GetKeyState(0x12) < 0)
            return IntPtr.Zero;

        if (key is 0xBB or 0x6B)
            AdjustZoom(0.1);
        else if (key is 0xBD or 0x6D)
            AdjustZoom(-0.1);
        else
            return IntPtr.Zero;

        handled = true;
        return new IntPtr(1);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_dataFolder);
            LoadHistory();
            try
            {
                _searchIndex = new KlyroSearchIndex(_dataFolder);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               JsonException)
            {
                StatusText.Text = $"Could not load the local Klyro Search index: {exception.Message}";
            }
            _scrollAnimationTimer.Tick += ScrollAnimationTimer_Tick;
            _updateCheckTimer.Tick += async (_, _) => await CheckForUpdatesAsync();
            if (_updateManager.IsInstalled)
            {
                _ = CheckForUpdatesAsync();
                _updateCheckTimer.Start();
            }
            _fullscreenHideTimer.Tick += (_, _) =>
            {
                _fullscreenHideTimer.Stop();
                _fullscreenEdgeTimer.Stop();
                if (_isFullscreen)
                    AnimateFullscreenChrome(false);
            };
            _fullscreenEdgeTimer.Tick += (_, _) => CheckFullscreenMousePosition();
            CreateTab("", true);
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not start WebView2: {exception.Message}";
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        foreach (var tab in _browserTabs.Where(tab => tab.IsNotes && tab.NoteIsModified))
        {
            if (ConfirmNoteChanges(tab))
                continue;
            e.Cancel = true;
            return;
        }

        _updateCheckTimer.Stop();
    }

    private async Task CheckForUpdatesAsync()
    {
        if (!_updateManager.IsInstalled || _isUpdateCheckInProgress)
            return;

        _isUpdateCheckInProgress = true;
        try
        {
            _availableUpdate = await _updateManager.CheckForUpdatesAsync();
            UpdateButton.Visibility = _availableUpdate is null ? Visibility.Collapsed : Visibility.Visible;
            if (_availableUpdate is not null)
                UpdateButton.ToolTip = $"Download and install Klyro {_availableUpdate.TargetFullRelease.Version}";
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or
                                           TaskCanceledException or FormatException or InvalidOperationException or
                                           AcquireLockFailedException or ChecksumFailedException or
                                           NotInstalledException)
        {
            StatusText.Text = $"Could not check for updates: {exception.Message}";
        }
        finally
        {
            _isUpdateCheckInProgress = false;
        }
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null || _isUpdateInstallInProgress)
            return;

        _isUpdateInstallInProgress = true;
        UpdateButton.IsEnabled = false;
        UpdateButton.Content = "Downloading update...";
        try
        {
            await _updateManager.DownloadUpdatesAsync(_availableUpdate);
            StatusText.Text = "Installing update and restarting Klyro...";
            _updateManager.ApplyUpdatesAndRestart(_availableUpdate.TargetFullRelease);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or
                                           TaskCanceledException or FormatException or InvalidOperationException or
                                           AcquireLockFailedException or ChecksumFailedException or
                                           NotInstalledException)
        {
            StatusText.Text = $"Could not install update: {exception.Message}";
            UpdateButton.Content = "Update available";
            UpdateButton.IsEnabled = true;
            _isUpdateInstallInProgress = false;
        }
    }

    private void CreateTab(
        string address,
        bool select,
        string? originatingSiteHost = null,
        bool isNotes = false)
    {
        var tab = new BrowserTab(address)
        {
            CurrentSiteHost = originatingSiteHost,
            IsNotes = isNotes
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        tab.FaviconImage = new Image
        {
            Width = 16,
            Height = 16,
            Margin = new Thickness(0, 0, 6, 0),
            Stretch = Stretch.Uniform,
            SnapsToDevicePixels = true,
            Visibility = Visibility.Collapsed
        };
        tab.TitleLabel = new TextBlock
        {
            Text = isNotes ? "Notes" : "New tab",
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 120,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        tab.CloseButton = new Button
        {
            Content = "x",
            Width = 18,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            ToolTip = "Close tab"
        };
        tab.CloseButton.Click += (_, _) => CloseTab(tab);
        var closeMenuItem = new MenuItem { Header = "Close tab" };
        closeMenuItem.Click += (_, _) => CloseTab(tab);
        tab.Item = new TabItem { Padding = new Thickness(4, 2, 4, 2), MinWidth = 42 };
        tab.Item.Opacity = 0;
        tab.Item.RenderTransform = new TranslateTransform { Y = 6 };
        tab.Item.ContextMenu = new ContextMenu();
        tab.Item.ContextMenu.Items.Add(closeMenuItem);
        tab.Item.PreviewMouseDown += (_, args) =>
        {
            if (args.ChangedButton == MouseButton.Middle)
            {
                CloseTab(tab);
                args.Handled = true;
            }
        };
        header.Children.Add(tab.FaviconImage);
        header.Children.Add(tab.TitleLabel);
        header.Children.Add(tab.CloseButton);
        tab.Item.Header = header;
        tab.Item.ToolTip = isNotes ? "Notes" : "New tab";
        if (isNotes)
        {
            tab.FaviconImage.Visibility = Visibility.Collapsed;
            tab.NotesPanel = BuildNotesPanel(tab);
            tab.TitleLabel.Text = tab.NoteNameBox?.Text ?? "Notes";
            tab.Item.ToolTip = tab.TitleLabel.Text;
        }
        _browserTabs.Add(tab);
        Tabs.Items.Add(tab.Item);
        UpdateTabWidths();
        AnimateElementIn(tab.Item, 6, 190);
        if (select)
        {
            Tabs.SelectedItem = tab.Item;
            tab.Item.BringIntoView();
            if (isNotes)
                tab.NoteEditor?.Focus();
            else
            {
                AddressBox.Focus();
                AddressBox.SelectAll();
            }
        }
    }

    private async Task ActivateTabAsync(BrowserTab tab)
    {
        BrowserHost.Children.Clear();
        if (tab.IsNotes)
        {
            EmptyPage.Visibility = Visibility.Collapsed;
            BrowserHost.Visibility = Visibility.Visible;
            AddressBox.Text = "Notes";
            AddressBox.IsEnabled = false;
            BrowserHost.Children.Add(tab.NotesPanel!);
            _ = Dispatcher.BeginInvoke(() => tab.NoteEditor?.Focus(), DispatcherPriority.Input);
            return;
        }

        AddressBox.IsEnabled = true;
        if (tab.SearchQuery is { } query)
        {
            EmptyPage.Visibility = Visibility.Collapsed;
            BrowserHost.Visibility = Visibility.Visible;
            if (tab.SearchResultsPanel is null)
            {
                tab.SearchResultsPanel = BuildSearchResultsPanel(tab, query);
                PopulateLocalSearchResults(tab, query);
            }
            BrowserHost.Children.Add(tab.SearchResultsPanel);
            AnimateElementIn(BrowserHost, 8, 220);
            UpdateAddress(tab);
            return;
        }

        if (string.IsNullOrWhiteSpace(tab.PendingAddress))
        {
            BrowserHost.Visibility = Visibility.Collapsed;
            EmptyPage.Visibility = Visibility.Visible;
            UpdateAddress(tab);
            return;
        }

        EmptyPage.Visibility = Visibility.Collapsed;
        BrowserHost.Visibility = Visibility.Visible;
        if (tab.WebView is null)
        {
            try
            {
                await EnsureBrowserInitializedAsync(tab);
                if (!tab.IsClosing && _browserTabs.Contains(tab) &&
                    tab.WebView is { } webView &&
                    Uri.TryCreate(tab.PendingAddress, UriKind.Absolute, out var initialUri))
                    webView.Source = initialUri;
            }
            catch (Exception exception)
            {
                if (!tab.IsClosing)
                    StatusText.Text = DescribeBrowserEngineError(exception);
            }
        }
        else
        {
            if (!BrowserHost.Children.Contains(tab.WebView))
                BrowserHost.Children.Add(tab.WebView);
            AnimateElementIn(BrowserHost, 8, 220);
        }

        UpdateAddress(tab);
    }

    private FrameworkElement BuildNotesPanel(BrowserTab tab)
    {
        var panel = new Grid { Margin = new Thickness(28, 20, 28, 22) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new TextBlock
        {
            Text = "Quick notes",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("PrimaryTextBrush")
        };
        panel.Children.Add(heading);

        var details = new Grid { Margin = new Thickness(0, 14, 0, 10) };
        details.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        details.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        details.Children.Add(new TextBlock
        {
            Text = "Note name",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            Foreground = (Brush)FindResource("MutedTextBrush")
        });
        tab.NoteNameBox = new TextBox
        {
            Text = "My note",
            MinWidth = 120,
            Padding = new Thickness(9, 6, 9, 6),
            Background = (Brush)FindResource("SurfaceAltBrush"),
            Foreground = (Brush)FindResource("PrimaryTextBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            CaretBrush = (Brush)FindResource("AccentBrush")
        };
        Grid.SetColumn(tab.NoteNameBox, 1);
        details.Children.Add(tab.NoteNameBox);
        var formatLabel = new TextBlock
        {
            Text = "Save as",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(18, 0, 10, 0),
            Foreground = (Brush)FindResource("MutedTextBrush")
        };
        Grid.SetColumn(formatLabel, 2);
        details.Children.Add(formatLabel);
        tab.NoteFormatBox = new ComboBox
        {
            Margin = new Thickness(0, 0, 0, 0),
            Padding = new Thickness(6, 4, 6, 4),
            Background = (Brush)FindResource("SurfaceAltBrush"),
            Foreground = (Brush)FindResource("PrimaryTextBrush")
        };
        tab.NoteFormatBox.Items.Add(new ComboBoxItem { Content = "Plain text (.txt)", Tag = "txt" });
        tab.NoteFormatBox.Items.Add(new ComboBoxItem { Content = "Rich text (.rtf)", Tag = "rtf" });
        tab.NoteFormatBox.SelectedIndex = 0;
        Grid.SetColumn(tab.NoteFormatBox, 3);
        details.Children.Add(tab.NoteFormatBox);
        Grid.SetRow(details, 1);
        panel.Children.Add(details);

        var editorArea = new DockPanel();
        var fileToolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        fileToolbar.Children.Add(CreateNoteActionButton("New", "New note (Ctrl+N)", () => NewNote(tab)));
        fileToolbar.Children.Add(CreateNoteActionButton("Open", "Open a text or rich-text file (Ctrl+O)",
            () => OpenNote(tab)));
        fileToolbar.Children.Add(CreateNoteActionButton("Save", "Save note (Ctrl+S)", () => SaveNote(tab)));
        fileToolbar.Children.Add(CreateNoteActionButton("Save as", "Save note with a new name",
            () => SaveNoteAs(tab)));
        fileToolbar.Children.Add(CreateNoteActionButton("Undo", "Undo (Ctrl+Z)",
            () => tab.NoteEditor?.Undo()));
        fileToolbar.Children.Add(CreateNoteActionButton("Redo", "Redo (Ctrl+Y)",
            () => tab.NoteEditor?.Redo()));
        fileToolbar.Children.Add(CreateNoteActionButton("Cut", "Cut", () => tab.NoteEditor?.Cut()));
        fileToolbar.Children.Add(CreateNoteActionButton("Copy", "Copy", () => tab.NoteEditor?.Copy()));
        fileToolbar.Children.Add(CreateNoteActionButton("Paste", "Paste", () => tab.NoteEditor?.Paste()));
        fileToolbar.Children.Add(CreateNoteActionButton("Select all", "Select all", () =>
            tab.NoteEditor?.SelectAll()));
        fileToolbar.Children.Add(CreateNoteActionButton("Find / replace", "Find and replace (Ctrl+H)",
            () => ShowFindReplace(tab)));
        var wrapToggle = new CheckBox
        {
            Content = "Word wrap",
            IsChecked = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 4, 0),
            Foreground = (Brush)FindResource("PrimaryTextBrush")
        };
        wrapToggle.Checked += (_, _) =>
        {
            if (tab.NoteEditor is { } editor)
                editor.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        };
        wrapToggle.Unchecked += (_, _) =>
        {
            if (tab.NoteEditor is { } editor)
                editor.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        };
        fileToolbar.Children.Add(wrapToggle);
        DockPanel.SetDock(fileToolbar, Dock.Top);
        editorArea.Children.Add(fileToolbar);

        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        toolbar.Children.Add(CreateNoteFormatButton(tab, "B", "Bold", FontWeights.Bold,
            FontStyles.Normal,
            editor => EditingCommands.ToggleBold.Execute(null, editor)));
        toolbar.Children.Add(CreateNoteFormatButton(tab, "I", "Italic", FontWeights.Normal,
            FontStyles.Italic,
            editor => EditingCommands.ToggleItalic.Execute(null, editor)));
        toolbar.Children.Add(CreateNoteFormatButton(tab, "U", "Underline", FontWeights.Normal,
            FontStyles.Normal,
            editor => EditingCommands.ToggleUnderline.Execute(null, editor)));

        var fontBox = new ComboBox
        {
            Width = 150,
            Margin = new Thickness(6, 0, 4, 0),
            Padding = new Thickness(5, 3, 5, 3),
            ToolTip = "Font",
            ItemsSource = new[] { "Segoe UI", "Arial", "Georgia", "Times New Roman", "Consolas" },
            SelectedIndex = 0
        };
        fontBox.SelectionChanged += (_, _) =>
        {
            if (fontBox.SelectedItem is string fontName && tab.NoteEditor is { } editor)
                editor.Selection.ApplyPropertyValue(TextElement.FontFamilyProperty, new FontFamily(fontName));
        };
        toolbar.Children.Add(fontBox);

        var sizeBox = new ComboBox
        {
            Width = 72,
            Margin = new Thickness(4, 0, 4, 0),
            Padding = new Thickness(5, 3, 5, 3),
            ToolTip = "Font size",
            ItemsSource = new[] { "10", "12", "14", "16", "18", "24", "32" },
            SelectedItem = "14"
        };
        sizeBox.SelectionChanged += (_, _) =>
        {
            if (sizeBox.SelectedItem is string sizeText &&
                double.TryParse(sizeText, out var size) && tab.NoteEditor is { } editor)
                editor.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, size);
        };
        toolbar.Children.Add(sizeBox);

        var colorBox = new ComboBox
        {
            Width = 120,
            Margin = new Thickness(4, 0, 4, 0),
            Padding = new Thickness(5, 3, 5, 3),
            ToolTip = "Text color"
        };
        foreach (var color in new[] { "White", "Lavender", "Sky blue", "Mint", "Yellow", "Pink" })
            colorBox.Items.Add(color);
        colorBox.SelectedIndex = 0;
        colorBox.SelectionChanged += (_, _) =>
        {
            if (tab.NoteEditor is not { } editor || colorBox.SelectedItem is not string colorName)
                return;
            var color = colorName switch
            {
                "Lavender" => Colors.Plum,
                "Sky blue" => Colors.LightSkyBlue,
                "Mint" => Colors.LightGreen,
                "Yellow" => Colors.Khaki,
                "Pink" => Colors.LightPink,
                _ => Colors.White
            };
            editor.Selection.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(color));
        };
        toolbar.Children.Add(colorBox);
        DockPanel.SetDock(toolbar, Dock.Top);
        editorArea.Children.Add(toolbar);

        tab.NoteEditor = new RichTextBox
        {
            AcceptsTab = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(14),
            Background = (Brush)FindResource("SurfaceBrush"),
            Foreground = (Brush)FindResource("PrimaryTextBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CaretBrush = (Brush)FindResource("AccentBrush"),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14
        };
        tab.NoteEditor.Document = new FlowDocument(new Paragraph())
        {
            PagePadding = new Thickness(0),
            TextAlignment = TextAlignment.Left
        };
        tab.NoteEditor.TextChanged += (_, _) =>
        {
            SetNoteModified(tab, true);
            UpdateNoteStatistics(tab);
        };
        tab.NoteEditor.SelectionChanged += (_, _) => UpdateNoteStatistics(tab);
        editorArea.Children.Add(tab.NoteEditor);
        Grid.SetRow(editorArea, 2);
        panel.Children.Add(editorArea);

        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var saveButton = new Button
        {
            Content = "Save to Downloads",
            Padding = new Thickness(14, 7, 14, 7),
            HorizontalAlignment = HorizontalAlignment.Right,
            IsDefault = false
        };
        saveButton.Click += (_, _) => SaveNote(tab);
        DockPanel.SetDock(saveButton, Dock.Right);
        footer.Children.Add(saveButton);
        tab.NoteStatusText = new TextBlock
        {
            Text = "0 words | 1 line",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("MutedTextBrush")
        };
        footer.Children.Add(tab.NoteStatusText);
        Grid.SetRow(footer, 3);
        panel.Children.Add(footer);

        tab.NoteNameBox.TextChanged += (_, _) =>
        {
            var noteName = string.IsNullOrWhiteSpace(tab.NoteNameBox.Text)
                ? "Untitled note"
                : tab.NoteNameBox.Text.Trim();
            SetNoteModified(tab, true);
            UpdateNoteTabTitle(tab);
        };
        tab.NoteFormatBox.SelectionChanged += (_, _) =>
        {
            if (tab.NoteEditor is not null)
                SetNoteModified(tab, true);
        };
        UpdateNoteStatistics(tab);
        return panel;
    }

    private Button CreateNoteActionButton(string text, string toolTip, Action action)
    {
        var button = new Button
        {
            Content = text,
            ToolTip = toolTip,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 0, 4, 0)
        };
        button.Click += (_, _) => action();
        return button;
    }

    private void SetNoteModified(BrowserTab tab, bool modified)
    {
        tab.NoteIsModified = modified;
        UpdateNoteTabTitle(tab);
    }

    private static void UpdateNoteTabTitle(BrowserTab tab)
    {
        var name = string.IsNullOrWhiteSpace(tab.NoteNameBox?.Text)
            ? "Untitled note"
            : tab.NoteNameBox.Text.Trim();
        tab.TitleLabel.Text = name + (tab.NoteIsModified ? " *" : "");
        tab.Item.ToolTip = tab.NoteIsModified ? $"{name} — unsaved changes" : name;
    }

    private static void UpdateNoteStatistics(BrowserTab tab)
    {
        if (tab.NoteEditor is not { } editor || tab.NoteStatusText is not { } status)
            return;
        var text = new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd).Text;
        var cursorText = new TextRange(editor.Document.ContentStart, editor.CaretPosition).Text;
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var lineNumber = cursorText.Count(character => character == '\n') + 1;
        status.Text = $"{words} words | {text.Count(character => character == '\n') + 1} lines | Line {lineNumber}";
    }

    private static Button CreateNoteFormatButton(
        BrowserTab tab,
        string text,
        string toolTip,
        FontWeight fontWeight,
        FontStyle fontStyle,
        Action<RichTextBox> applyFormatting)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = text,
                TextDecorations = toolTip == "Underline" ? TextDecorations.Underline : null
            },
            ToolTip = toolTip,
            MinWidth = 32,
            Height = 30,
            Margin = new Thickness(0, 0, 4, 0),
            FontWeight = fontWeight,
            FontStyle = fontStyle
        };
        button.Click += (_, _) =>
        {
            if (tab.NoteEditor is { } editor)
                applyFormatting(editor);
        };
        return button;
    }

    private bool SaveNote(BrowserTab tab)
    {
        var format = (tab.NoteFormatBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "txt";
        var downloadFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        try
        {
            Directory.CreateDirectory(downloadFolder);
            if (tab.NoteEditor is null)
                throw new InvalidOperationException("The notes editor is not available.");

            var filePath = tab.NoteFilePath;
            if (filePath is null || !string.Equals(
                    Path.GetExtension(filePath), $".{format}", StringComparison.OrdinalIgnoreCase))
                filePath = GetAvailableDownloadPath(downloadFolder, $"{GetSafeNoteFileName(tab)}.{format}");

            var content = new TextRange(
                tab.NoteEditor.Document.ContentStart,
                tab.NoteEditor.Document.ContentEnd);
            if (format == "rtf")
            {
                using var stream = File.Create(filePath);
                content.Save(stream, DataFormats.Rtf);
            }
            else
            {
                File.WriteAllText(filePath, content.Text, new System.Text.UTF8Encoding(false));
            }

            tab.NoteFilePath = filePath;
            SetNoteModified(tab, false);
            AddSavedNoteToDownloads(filePath);
            StatusText.Text = $"Note saved to {filePath}";
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           ArgumentException or NotSupportedException or InvalidOperationException)
        {
            StatusText.Text = $"Could not save note: {exception.Message}";
            return false;
        }
    }

    private static string GetSafeNoteFileName(BrowserTab tab)
    {
        var noteName = string.IsNullOrWhiteSpace(tab.NoteNameBox?.Text)
            ? "Untitled note"
            : tab.NoteNameBox.Text.Trim();
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var safeName = string.Concat(noteName.Select(character =>
            invalidCharacters.Contains(character) ? '-' : character)).Trim(' ', '.');
        return string.IsNullOrWhiteSpace(safeName) ? "Untitled note" : safeName;
    }

    private bool SaveNoteAs(BrowserTab tab)
    {
        var format = (tab.NoteFormatBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "txt";
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var dialog = new SaveFileDialog
        {
            Title = "Save note",
            InitialDirectory = Directory.Exists(folder) ? folder : Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile),
            FileName = $"{GetSafeNoteFileName(tab)}.{format}",
            Filter = "Plain text (*.txt)|*.txt|Rich text (*.rtf)|*.rtf",
            FilterIndex = format == "rtf" ? 2 : 1,
            DefaultExt = format,
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true)
            return false;

        var selectedFormat = Path.GetExtension(dialog.FileName).Equals(".rtf",
            StringComparison.OrdinalIgnoreCase) ? "rtf" : "txt";
        var targetPath = Path.ChangeExtension(dialog.FileName, $".{selectedFormat}");
        tab.NoteFormatBox!.SelectedIndex = selectedFormat == "rtf" ? 1 : 0;
        var oldPath = tab.NoteFilePath;
        tab.NoteFilePath = targetPath;
        if (SaveNote(tab))
            return true;
        tab.NoteFilePath = oldPath;
        return false;
    }

    private void OpenNote(BrowserTab tab)
    {
        if (!ConfirmNoteChanges(tab))
            return;

        var dialog = new OpenFileDialog
        {
            Title = "Open note",
            Filter = "Note files (*.txt;*.rtf)|*.txt;*.rtf|Plain text (*.txt)|*.txt|Rich text (*.rtf)|*.rtf|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var isRichText = Path.GetExtension(dialog.FileName).Equals(
                ".rtf", StringComparison.OrdinalIgnoreCase);
            if (isRichText)
            {
                using var stream = File.OpenRead(dialog.FileName);
                new TextRange(tab.NoteEditor!.Document.ContentStart,
                    tab.NoteEditor.Document.ContentEnd).Load(stream, DataFormats.Rtf);
            }
            else
            {
                var text = File.ReadAllText(dialog.FileName);
                tab.NoteEditor!.Document = new FlowDocument(new Paragraph(new Run(text)))
                {
                    PagePadding = new Thickness(0),
                    TextAlignment = TextAlignment.Left
                };
            }

            tab.NoteNameBox!.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
            tab.NoteFormatBox!.SelectedIndex = isRichText ? 1 : 0;
            tab.NoteFilePath = dialog.FileName;
            SetNoteModified(tab, false);
            UpdateNoteStatistics(tab);
            StatusText.Text = $"Opened {dialog.FileName}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           ArgumentException or NotSupportedException or
                                           System.Windows.Markup.XamlParseException)
        {
            StatusText.Text = $"Could not open note: {exception.Message}";
        }
    }

    private bool ConfirmNoteChanges(BrowserTab tab)
    {
        if (!tab.NoteIsModified)
            return true;

        var result = MessageBox.Show(this,
            $"Save changes to \"{GetSafeNoteFileName(tab)}\" before continuing?",
            "Unsaved note", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        return result switch
        {
            MessageBoxResult.Yes => SaveNote(tab),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    private void NewNote(BrowserTab tab)
    {
        if (!ConfirmNoteChanges(tab))
            return;
        tab.NoteEditor!.Document = new FlowDocument(new Paragraph())
        {
            PagePadding = new Thickness(0),
            TextAlignment = TextAlignment.Left
        };
        tab.NoteNameBox!.Text = "My note";
        tab.NoteFormatBox!.SelectedIndex = 0;
        tab.NoteFilePath = null;
        SetNoteModified(tab, false);
        UpdateNoteStatistics(tab);
        tab.NoteEditor.Focus();
    }

    private void AddSavedNoteToDownloads(string filePath)
    {
        if (!_listedNoteDownloads.Add(filePath))
            return;

        var nameText = new TextBlock
        {
            Text = Path.GetFileName(filePath),
            Foreground = (Brush)FindResource("PrimaryTextBrush"),
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var stateText = new TextBlock
        {
            Text = $"Saved to {Path.GetDirectoryName(filePath)}",
            Foreground = (Brush)FindResource("MutedTextBrush"),
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var openButton = new Button
        {
            Content = "Open",
            Padding = new Thickness(8, 2, 8, 2),
            ToolTip = "Open saved note"
        };
        openButton.Click += (_, _) => OpenFile(filePath, stateText);
        var row = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
        var heading = new DockPanel();
        DockPanel.SetDock(openButton, Dock.Right);
        heading.Children.Add(openButton);
        heading.Children.Add(nameText);
        row.Children.Add(heading);
        row.Children.Add(stateText);
        DownloadsList.Children.Insert(0, row);
        AnimateDownloadPanel(true);
        AnimateElementIn(row, 8, 190);
    }

    private void ShowFindReplace(BrowserTab tab)
    {
        var editor = tab.NoteEditor;
        if (editor is null)
            return;

        var dialog = new Window
        {
            Title = "Find and replace",
            Owner = this,
            Width = 430,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = (Brush)FindResource("SurfaceBrush"),
            Foreground = (Brush)FindResource("PrimaryTextBrush")
        };
        var layout = new Grid { Margin = new Thickness(16) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var index = 0; index < 4; index++)
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(new TextBlock
        {
            Text = "Find",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 8)
        });
        var findBox = new TextBox { MinWidth = 210, Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(6) };
        Grid.SetColumn(findBox, 1);
        layout.Children.Add(findBox);
        var findNextButton = new Button { Content = "Find next", Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(6, 0, 0, 8) };
        Grid.SetColumn(findNextButton, 2);
        layout.Children.Add(findNextButton);
        var replaceLabel = new TextBlock
        {
            Text = "Replace",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 8)
        };
        Grid.SetRow(replaceLabel, 1);
        layout.Children.Add(replaceLabel);
        var replaceBox = new TextBox { Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(6) };
        Grid.SetRow(replaceBox, 1);
        Grid.SetColumn(replaceBox, 1);
        layout.Children.Add(replaceBox);
        var replaceButton = new Button { Content = "Replace", Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(6, 0, 0, 8) };
        Grid.SetRow(replaceButton, 1);
        Grid.SetColumn(replaceButton, 2);
        layout.Children.Add(replaceButton);
        var replaceAllButton = new Button
        {
            Content = "Replace all",
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 0, 8, 0)
        };
        Grid.SetRow(replaceAllButton, 2);
        Grid.SetColumn(replaceAllButton, 1);
        replaceAllButton.HorizontalAlignment = HorizontalAlignment.Right;
        layout.Children.Add(replaceAllButton);
        var closeButton = new Button { Content = "Close", Padding = new Thickness(8, 4, 8, 4), IsCancel = true };
        Grid.SetRow(closeButton, 2);
        Grid.SetColumn(closeButton, 2);
        layout.Children.Add(closeButton);

        bool FindNext()
        {
            if (string.IsNullOrEmpty(findBox.Text))
                return false;
            var content = new TextRange(editor.Document.ContentStart, editor.Document.ContentEnd);
            var text = content.Text;
            var searchStart = Math.Clamp(
                new TextRange(editor.Document.ContentStart, editor.Selection.End).Text.Length,
                0, text.Length);
            var index = text.IndexOf(findBox.Text, searchStart, StringComparison.OrdinalIgnoreCase);
            if (index < 0 && searchStart > 0)
                index = text.IndexOf(findBox.Text, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return false;

            var start = editor.Document.ContentStart.GetPositionAtOffset(index, LogicalDirection.Forward);
            var end = start?.GetPositionAtOffset(findBox.Text.Length, LogicalDirection.Forward);
            if (start is null || end is null)
                return false;
            editor.Selection.Select(start, end);
            editor.Focus();
            return true;
        }

        findNextButton.Click += (_, _) =>
        {
            if (!FindNext())
                StatusText.Text = string.IsNullOrEmpty(findBox.Text) ? "Enter text to find." : "No match found.";
        };
        replaceButton.Click += (_, _) =>
        {
            if (editor.Selection.IsEmpty || !string.Equals(
                    new TextRange(editor.Selection.Start, editor.Selection.End).Text,
                    findBox.Text, StringComparison.OrdinalIgnoreCase))
            {
                if (!FindNext())
                {
                    StatusText.Text = "No match found.";
                    return;
                }
            }
            editor.Selection.Text = replaceBox.Text;
            SetNoteModified(tab, true);
            UpdateNoteStatistics(tab);
        };
        replaceAllButton.Click += (_, _) =>
        {
            if (string.IsNullOrEmpty(findBox.Text))
            {
                StatusText.Text = "Enter text to find.";
                return;
            }
            var count = 0;
            while (count < 10000 && FindNext())
            {
                editor.Selection.Text = replaceBox.Text;
                count++;
                if (replaceBox.Text.Length == 0)
                    editor.Selection.Select(editor.CaretPosition, editor.CaretPosition);
            }
            if (count > 0)
                SetNoteModified(tab, true);
            UpdateNoteStatistics(tab);
            StatusText.Text = $"Replaced {count} occurrence(s).";
        };
        dialog.Content = layout;
        dialog.Loaded += (_, _) => findBox.Focus();
        dialog.ShowDialog();
        editor.Focus();
    }

    private void OpenFile(string filePath, TextBlock? stateText = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            if (stateText is not null)
                stateText.Text = $"Could not open file: {exception.Message}";
            else
                StatusText.Text = $"Could not open file: {exception.Message}";
        }
    }

    private async Task EnsureBrowserInitializedAsync(BrowserTab tab)
    {
        if (tab.WebView is null)
        {
            tab.WebView = new WebView2();
            tab.WebView.CoreWebView2InitializationCompleted += (_, args) =>
            {
                if (!tab.IsClosing)
                    ConfigureBrowser(tab, args);
            };
        }

        if (!BrowserHost.Children.Contains(tab.WebView))
            BrowserHost.Children.Add(tab.WebView);

        var webView = tab.WebView;
        var initializationTask = tab.InitializationTask;
        if (initializationTask is null)
        {
            initializationTask = InitializeBrowserTabAsync(webView, tab.InitializationCancellation.Token);
            tab.InitializationTask = initializationTask;
        }

        try
        {
            await initializationTask;
        }
        catch
        {
            if (ReferenceEquals(tab.InitializationTask, initializationTask))
                tab.InitializationTask = null;
            throw;
        }
    }

    private async Task InitializeBrowserTabAsync(WebView2 webView, CancellationToken cancellationToken)
    {
        await WaitForWebViewLoadedAsync(webView, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var environmentTask = _environmentTask ??= CreateBrowserEnvironmentAsync();
        try
        {
            _environment = await environmentTask;
        }
        catch
        {
            if (ReferenceEquals(_environmentTask, environmentTask))
                _environmentTask = null;
            throw;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await webView.EnsureCoreWebView2Async(_environment);
    }

    private async Task<CoreWebView2Environment> CreateBrowserEnvironmentAsync()
    {
        var profileFolder = Path.Combine(_dataFolder, "Profile");
        Directory.CreateDirectory(profileFolder);
        return await CoreWebView2Environment.CreateAsync(null, profileFolder);
    }

    private static Task WaitForWebViewLoadedAsync(WebView2 webView, CancellationToken cancellationToken)
    {
        if (webView.IsLoaded)
            return Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler? loadedHandler = null;
        CancellationTokenRegistration cancellationRegistration = default;
        loadedHandler = (_, _) =>
        {
            webView.Loaded -= loadedHandler;
            cancellationRegistration.Dispose();
            completion.TrySetResult();
        };
        webView.Loaded += loadedHandler;
        cancellationRegistration = cancellationToken.Register(() =>
        {
            webView.Loaded -= loadedHandler;
            completion.TrySetCanceled(cancellationToken);
        });
        return completion.Task;
    }

    private void ConfigureBrowser(BrowserTab tab, CoreWebView2InitializationCompletedEventArgs args)
    {
        if (!args.IsSuccess || tab.WebView?.CoreWebView2 is not { } core)
        {
            StatusText.Text = DescribeBrowserEngineError(
                args.InitializationException ?? new InvalidOperationException("WebView2 initialization failed."));
            return;
        }

        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.FaviconChanged += (_, _) => _ = RefreshTabFaviconAsync(tab, core);
        core.SourceChanged += (_, _) => UpdateAddress(tab);
        core.DocumentTitleChanged += (_, _) => UpdateTabTitle(tab);
        tab.WebView.ZoomFactor = _zoomFactor;
        core.NavigationStarting += (_, navigation) =>
        {
            if (HandleNavigationStarting(tab, navigation))
            {
                StatusText.Text = "Loading...";
                if (SelectedTab() == tab)
                    BrowserHost.Opacity = 0;
            }
        };
        core.NavigationCompleted += (_, navigation) =>
        {
            if (tab.NavigationDeniedMessage is { } deniedMessage)
            {
                StatusText.Text = deniedMessage;
                tab.NavigationDeniedMessage = null;
            }

            else if (navigation.IsSuccess)
            {
                StatusText.Text = "Done";
                AddHistoryEntry(tab, core.Source);
                _ = RefreshTabFaviconAsync(tab, core);
            }
            else
            {
                StatusText.Text = "Page could not be loaded.";
            }
            UpdateTabTitle(tab);
            if (SelectedTab() == tab)
                AnimateElementIn(BrowserHost, 8, 240);
        };
        core.NewWindowRequested += (_, newWindowArgs) =>
        {
            newWindowArgs.Handled = true;
            CreateTab(newWindowArgs.Uri, true, tab.CurrentSiteHost);
        };
        core.DownloadStarting += (_, downloadArgs) =>
        {
            var operation = downloadArgs.DownloadOperation;
            try
            {
                var downloadFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                Directory.CreateDirectory(downloadFolder);
                var fileName = GetDownloadFileName(operation);
                var filePath = GetAvailableDownloadPath(downloadFolder, fileName);
                downloadArgs.ResultFilePath = filePath;
                downloadArgs.Handled = true;
                StatusText.Text = $"Downloading to {downloadFolder}...";
                TrackDownload(operation, filePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               ArgumentException or NotSupportedException)
            {
                downloadArgs.Cancel = true;
                StatusText.Text = $"Could not start download: {exception.Message}";
            }
        };
    }

    private string DescribeBrowserEngineError(Exception exception)
    {
        string runtimeVersion;
        try
        {
            runtimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception runtimeException) when (runtimeException is COMException or WebView2RuntimeNotFoundException)
        {
            runtimeVersion = $"not found ({runtimeException.Message})";
        }

        var details = $"WebView2 initialization failed. {exception.GetType().Name}: {exception.Message} " +
                      $"HRESULT: 0x{exception.HResult:X8}. Runtime: {runtimeVersion}.";
        if (runtimeVersion.StartsWith("not found", StringComparison.OrdinalIgnoreCase))
            details += " Install the Microsoft Edge WebView2 Evergreen Runtime to use Klyro on this Windows installation.";
        var logPath = Path.Combine(_dataFolder, "webview2-error.log");
        try
        {
            File.AppendAllText(logPath,
                $"{DateTimeOffset.Now:O}{Environment.NewLine}{details}{Environment.NewLine}" +
                $"{exception}{Environment.NewLine}{Environment.NewLine}");
            return $"{details} Details saved to {logPath}";
        }
        catch (Exception logException) when (logException is IOException or UnauthorizedAccessException)
        {
            return $"{details} Could not save diagnostic log: {logException.Message}";
        }
    }

    private void TrackDownload(CoreWebView2DownloadOperation operation, string filePath)
    {
        var nameText = new TextBlock
        {
            Text = Path.GetFileName(filePath),
            Foreground = (Brush)FindResource("PrimaryTextBrush"),
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var stateText = new TextBlock
        {
            Text = "Starting...",
            Foreground = (Brush)FindResource("MutedTextBrush"),
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 4)
        };
        var progressBar = new ProgressBar
        {
            Height = 6,
            Minimum = 0,
            Maximum = 100
        };
        var openButton = new Button
        {
            Content = "Open",
            Padding = new Thickness(8, 2, 8, 2),
            Visibility = Visibility.Collapsed
        };
        var row = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
        var heading = new DockPanel();
        DockPanel.SetDock(openButton, Dock.Right);
        heading.Children.Add(openButton);
        heading.Children.Add(nameText);
        row.Children.Add(heading);
        row.Children.Add(stateText);
        row.Children.Add(progressBar);
        DownloadsList.Children.Insert(0, row);
        row.Opacity = 0;
        row.RenderTransform = new TranslateTransform { Y = 8 };
        AnimateDownloadPanel(true);
        AnimateElementIn(row, 8, 190);

        var download = new BrowserDownload(operation, filePath, stateText, progressBar, openButton);
        openButton.Click += (_, _) => OpenDownloadedFile(download);
        operation.BytesReceivedChanged += (_, _) => Dispatcher.BeginInvoke(
            () => UpdateDownloadProgress(download));
        operation.StateChanged += (_, _) => Dispatcher.BeginInvoke(
            () => UpdateDownloadProgress(download));
        UpdateDownloadProgress(download);
    }

    private static string GetDownloadFileName(CoreWebView2DownloadOperation operation)
    {
        var fileName = Uri.TryCreate(operation.Uri, UriKind.Absolute, out var downloadUri)
            ? Path.GetFileName(downloadUri.LocalPath)
            : "";
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "download";

        foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(invalidCharacter, '_');
        return fileName.Trim().Length == 0 ? "download" : fileName;
    }

    private static string GetAvailableDownloadPath(string folder, string fileName)
    {
        var candidate = Path.Combine(folder, fileName);
        if (!File.Exists(candidate))
            return candidate;

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 1; ; index++)
        {
            candidate = Path.Combine(folder, $"{baseName} ({index}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    private void UpdateDownloadProgress(BrowserDownload download)
    {
        var operation = download.Operation;
        var totalBytes = operation.TotalBytesToReceive ?? 0;
        var receivedBytes = operation.BytesReceived;
        download.ProgressBar.IsIndeterminate = totalBytes <= 0 &&
                                               operation.State == CoreWebView2DownloadState.InProgress;
        if (totalBytes > 0)
        {
            download.ProgressBar.Value = Math.Clamp(
                receivedBytes / (double)totalBytes * 100, 0d, 100d);
        }

        switch (operation.State)
        {
            case CoreWebView2DownloadState.InProgress:
                download.StateText.Text = totalBytes > 0
                    ? $"Downloading... {download.ProgressBar.Value:0}%"
                    : "Downloading...";
                break;
            case CoreWebView2DownloadState.Completed:
                download.StateText.Text = $"Complete — {FormatDownloadSize(receivedBytes)}";
                download.ProgressBar.IsIndeterminate = false;
                download.ProgressBar.Value = 100;
                download.OpenButton.Visibility = Visibility.Visible;
                break;
            case CoreWebView2DownloadState.Interrupted:
                download.StateText.Text = $"Download failed: {operation.InterruptReason}";
                download.ProgressBar.IsIndeterminate = false;
                StatusText.Text = $"Download failed: {download.FilePath}";
                break;
        }
    }

    private static string FormatDownloadSize(double bytes) =>
        bytes >= 1024 * 1024
            ? $"{bytes / (1024d * 1024):0.##} MB"
            : $"{bytes / 1024d:0.##} KB";

    private void OpenDownloadedFile(BrowserDownload download)
    {
        OpenFile(download.FilePath, download.StateText);
    }

    private static TimeSpan MotionDuration(int milliseconds) =>
        SystemParameters.ClientAreaAnimation ? TimeSpan.FromMilliseconds(milliseconds) : TimeSpan.Zero;

    private static void AnimateElementIn(UIElement element, double offset, int durationMilliseconds)
    {
        var duration = MotionDuration(durationMilliseconds);
        if (element.RenderTransform is not TranslateTransform)
            element.RenderTransform = new TranslateTransform { Y = offset };

        if (duration == TimeSpan.Zero)
        {
            element.Opacity = 1;
            if (element.RenderTransform is TranslateTransform translate)
                translate.Y = 0;
            return;
        }

        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        if (element.RenderTransform is TranslateTransform transform)
            transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offset, 0, duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void AnimateDownloadPanel(bool show)
    {
        var version = ++_downloadPanelAnimationVersion;
        var duration = MotionDuration(180);
        if (duration == TimeSpan.Zero)
        {
            DownloadPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            DownloadPanel.Opacity = show ? 1 : 0;
            return;
        }

        if (DownloadPanel.RenderTransform is not TranslateTransform transform)
            DownloadPanel.RenderTransform = transform = new TranslateTransform();

        if (show)
        {
            DownloadPanel.Visibility = Visibility.Visible;
            if (DownloadPanel.Opacity == 0)
                transform.Y = 12;
            DownloadPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(DownloadPanel.Opacity, 1, duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
            transform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(transform.Y, 0, duration)
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            return;
        }

        var fadeOut = new DoubleAnimation(DownloadPanel.Opacity, 0, duration);
        fadeOut.Completed += (_, _) =>
        {
            if (version != _downloadPanelAnimationVersion)
                return;
            DownloadPanel.Visibility = Visibility.Collapsed;
            transform.Y = 0;
        };
        DownloadPanel.BeginAnimation(OpacityProperty, fadeOut);
        transform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(transform.Y, 12, duration));
    }

    private void Downloads_Click(object sender, RoutedEventArgs e) =>
        AnimateDownloadPanel(DownloadPanel.Visibility != Visibility.Visible);

    private void HideDownloads_Click(object sender, RoutedEventArgs e) =>
        AnimateDownloadPanel(false);

    private void Navigate(string input)
    {
        var tab = SelectedTab();
        if (tab is null || string.IsNullOrWhiteSpace(input))
            return;
        if (tab.IsNotes)
        {
            StatusText.Text = "Open a browser tab to visit a website.";
            return;
        }

        var text = input.Trim();
        if (text.StartsWith("klyro:", StringComparison.OrdinalIgnoreCase))
        {
            ShowLocalSearch(tab, text["klyro:".Length..].Trim());
            return;
        }

        string address;
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            address = uri.ToString();
        }
        else if (!text.Contains(' ') && text.Contains('.') && Uri.TryCreate("https://" + text, UriKind.Absolute, out uri))
        {
            address = uri.ToString();
        }
        else
        {
            address = "https://duckduckgo.com/?q=" + Uri.EscapeDataString(text);
        }

        ClearTabSearch(tab);
        if (tab.WebView?.CoreWebView2 is { } core)
        {
            tab.UserEnteredHost = NormalizeSiteHost(new Uri(address).IdnHost);
            EmptyPage.Visibility = Visibility.Collapsed;
            BrowserHost.Visibility = Visibility.Visible;
            BrowserHost.Children.Clear();
            BrowserHost.Children.Add(tab.WebView!);
            core.Navigate(address);
        }
        else
        {
            tab.PendingAddress = address;
            tab.UserEnteredHost = NormalizeSiteHost(new Uri(address).IdnHost);
            _ = ActivateTabAsync(tab);
        }
    }

    private static void ClearTabSearch(BrowserTab tab)
    {
        tab.SearchQuery = null;
        tab.SearchResultsPanel = null;
        tab.SearchSourceText = null;
        tab.SearchResultsList = null;
    }

    private bool HandleNavigationStarting(
        BrowserTab tab,
        CoreWebView2NavigationStartingEventArgs navigation)
    {
        if (!Uri.TryCreate(navigation.Uri, UriKind.Absolute, out var destination))
            return true;

        if (destination.Scheme == "about" && destination.AbsoluteUri == "about:blank")
            return true;

        if (destination.Scheme != Uri.UriSchemeHttp && destination.Scheme != Uri.UriSchemeHttps)
        {
            navigation.Cancel = true;
            tab.NavigationDeniedMessage = "Only web pages can be opened in Klyro.";
            StatusText.Text = tab.NavigationDeniedMessage;
            return false;
        }

        if (IsAppleOrICloudHost(destination.IdnHost))
        {
            navigation.Cancel = true;
            try
            {
                Process.Start(new ProcessStartInfo(destination.AbsoluteUri) { UseShellExecute = true });
                tab.NavigationDeniedMessage = "Opened Apple/iCloud in your default browser.";
            }
            catch (Win32Exception exception)
            {
                tab.NavigationDeniedMessage = $"Could not open your default browser: {exception.Message}";
            }
            catch (InvalidOperationException exception)
            {
                tab.NavigationDeniedMessage = $"Could not open your default browser: {exception.Message}";
            }

            StatusText.Text = tab.NavigationDeniedMessage;
            return false;
        }

        var host = NormalizeSiteHost(destination.IdnHost);
        if (!navigation.IsRedirected && string.Equals(tab.UserEnteredHost, host, StringComparison.OrdinalIgnoreCase))
        {
            tab.UserEnteredHost = null;
            tab.CurrentSiteHost = host;
            tab.RecentSiteChanges.Clear();
        }

        else if (tab.CurrentSiteHost is null)
        {
            tab.CurrentSiteHost = host;
        }
        else if (!string.Equals(tab.CurrentSiteHost, host, StringComparison.OrdinalIgnoreCase))
        {
            var authenticationRedirect = IsAuthenticationHost(host) ||
                                        (navigation.IsRedirected && IsAuthenticationHost(tab.CurrentSiteHost));
            if (authenticationRedirect)
                tab.RecentSiteChanges.Clear();
            else if (navigation.IsRedirected && IsRapidSiteChange(tab))
            {
                tab.RecentSiteChanges.Clear();
                navigation.Cancel = true;
                tab.NavigationDeniedMessage = $"Skipped a suspicious rapid redirect to {host}.";
                StatusText.Text = tab.NavigationDeniedMessage;
                UpdateAddress(tab);
                return false;
            }

            tab.CurrentSiteHost = host;
        }

        tab.PendingAddress = navigation.Uri;
        tab.NavigationDeniedMessage = null;
        return true;
    }

    private static bool IsRapidSiteChange(BrowserTab tab)
    {
        var now = DateTime.UtcNow;
        while (tab.RecentSiteChanges.TryPeek(out var changeTime) &&
               now - changeTime > RapidSiteChangeWindow)
            tab.RecentSiteChanges.Dequeue();

        tab.RecentSiteChanges.Enqueue(now);
        return tab.RecentSiteChanges.Count >= RapidSiteChangeThreshold;
    }

    private static string NormalizeSiteHost(string host) =>
        host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;

    private static bool IsAppleOrICloudHost(string host) =>
        host.Equals("apple.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".apple.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("icloud.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".icloud.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsAuthenticationHost(string? host) =>
        !string.IsNullOrEmpty(host) && AuthenticationHosts.Any(domain =>
            host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));

    private void AddHistoryEntry(BrowserTab tab, string? address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            return;

        var title = tab.WebView?.CoreWebView2?.DocumentTitle;
        var entry = new BrowserHistoryEntry(
            DateTimeOffset.Now,
            string.IsNullOrWhiteSpace(title) ? uri.IdnHost : title,
            uri.ToString());
        _history.Insert(0, entry);
        while (_history.Count > 5000)
            _history.RemoveAt(_history.Count - 1);

        try
        {
            File.WriteAllText(HistoryFilePath, JsonSerializer.Serialize(_history));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Could not save browsing history: {exception.Message}";
        }
    }

    private void LoadHistory()
    {
        if (!File.Exists(HistoryFilePath))
            return;

        try
        {
            var storedHistory = JsonSerializer.Deserialize<List<BrowserHistoryEntry>>(
                File.ReadAllText(HistoryFilePath));
            if (storedHistory is not null)
                foreach (var entry in storedHistory.OrderByDescending(entry => entry.VisitedAt).Take(5000))
                    _history.Add(entry);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           JsonException)
        {
            StatusText.Text = $"Could not load browsing history: {exception.Message}";
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        BrowserSettingsWindow? settings = null;
        settings = new BrowserSettingsWindow(_history, ClearHistory,
            () => OpenSearchIndex(settings))
        {
            Owner = this
        };
        settings.ShowDialog();
    }

    private void SearchIndex_Click(object sender, RoutedEventArgs e) => OpenSearchIndex(this);

    private void OpenSearchIndex(Window? owner = null)
    {
        if (_searchIndex is null)
            return;

        var window = new SearchIndexWindow(_searchIndex) { Owner = owner ?? this };
        window.ShowDialog();
        if (SelectedTab() is { SearchQuery: { } query } tab)
        {
            tab.SearchResultsPanel = BuildSearchResultsPanel(tab, query);
            PopulateLocalSearchResults(tab, query);
            if (Tabs.SelectedItem == tab.Item)
            {
                BrowserHost.Children.Clear();
                BrowserHost.Children.Add(tab.SearchResultsPanel);
            }
        }
    }

    private void ShowLocalSearch(BrowserTab tab, string query)
    {
        tab.PendingAddress = "";
        tab.CurrentSiteHost = null;
        tab.UserEnteredHost = null;
        tab.SearchQuery = query;
        tab.SearchResultsPanel = BuildSearchResultsPanel(tab, query);
        PopulateLocalSearchResults(tab, query);
        Tabs.SelectedItem = tab.Item;
        EmptyPage.Visibility = Visibility.Collapsed;
        BrowserHost.Visibility = Visibility.Visible;
        BrowserHost.Children.Clear();
        BrowserHost.Children.Add(tab.SearchResultsPanel);
        AnimateElementIn(BrowserHost, 8, 220);
        UpdateAddress(tab);
    }

    private StackPanel BuildSearchResultsPanel(BrowserTab tab, string query)
    {
        var content = new StackPanel { Margin = new Thickness(36, 28, 36, 24) };
        content.Children.Add(new TextBlock
        {
            Text = "Klyro Search",
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("PrimaryTextBrush")
        });
        var sourceText = new TextBlock
        {
            Text = $"Searching for “{query}”…",
            Margin = new Thickness(0, 4, 0, 14),
            Foreground = (Brush)FindResource("MutedTextBrush"),
            TextWrapping = TextWrapping.Wrap
        };
        content.Children.Add(sourceText);
        var resultsPanel = new StackPanel();
        content.Children.Add(resultsPanel);
        tab.SearchSourceText = sourceText;
        tab.SearchResultsList = resultsPanel;
        return content;
    }

    private void PopulateLocalSearchResults(BrowserTab tab, string query)
    {
        if (tab.SearchResultsList is null || tab.SearchSourceText is null)
            return;

        tab.SearchResultsList.Children.Clear();
        var localResults = _searchIndex?.Search(query) ?? [];
        tab.SearchSourceText.Text = localResults.Count == 0
            ? $"No local results for “{query}”. Klyro searches only sites you add and index."
            : $"Klyro local index · {localResults.Count} result{(localResults.Count == 1 ? "" : "s")}";
        AddSearchResultCards(tab, localResults.Select(result =>
            new SearchCard(result.Title, result.Url, result.Snippet)));

        if (localResults.Count == 0 && tab.SearchResultsList is not null)
        {
            var localButton = new Button
            {
                Content = "Manage local search index",
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 8, 0, 0)
            };
            localButton.Click += (_, _) => OpenSearchIndex();
            tab.SearchResultsList.Children.Add(localButton);
        }
    }

    private void AddSearchResultCards(BrowserTab tab, IEnumerable<SearchCard> results)
    {
        if (tab.SearchResultsList is null)
            return;

        foreach (var result in results)
        {
            var card = new StackPanel { Margin = new Thickness(0, 6, 0, 12) };
            var title = new Button
            {
                Content = result.Title,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.FromRgb(193, 145, 255)),
                FontSize = 16,
                Cursor = Cursors.Hand
            };
            title.Click += (_, _) => Navigate(result.Url);
            card.Children.Add(title);
            card.Children.Add(new TextBlock
            {
                Text = result.Url,
                Foreground = new SolidColorBrush(Color.FromRgb(132, 188, 145)),
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            card.Children.Add(new TextBlock
            {
                Text = result.Snippet,
                Foreground = (Brush)FindResource("PrimaryTextBrush"),
                FontSize = 12,
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            tab.SearchResultsList.Children.Add(card);
        }
    }

    private void ClearHistory()
    {
        _history.Clear();
        try
        {
            File.WriteAllText(HistoryFilePath, "[]");
            StatusText.Text = "Browsing history cleared.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Could not clear browsing history: {exception.Message}";
        }
    }

    private void UpdateAddress(BrowserTab tab)
    {
        if (Tabs.SelectedItem != tab.Item || AddressBox.IsKeyboardFocusWithin)
            return;
        if (tab.IsNotes)
        {
            AddressBox.Text = "Notes";
            return;
        }
        if (tab.SearchQuery is { } query)
        {
            AddressBox.Text = query;
            return;
        }
        var source = tab.WebView?.Source?.ToString();
        AddressBox.Text = string.IsNullOrEmpty(tab.PendingAddress) || source == "about:blank"
            ? ""
            : source ?? tab.PendingAddress;
    }

    private void UpdateTabTitle(BrowserTab tab)
    {
        if (tab.WebView?.CoreWebView2 is { } core)
        {
            var title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "New tab" : core.DocumentTitle;
            tab.TitleLabel.Text = title;
            tab.Item.ToolTip = title;
        }
    }

    private async Task UpdateTabFaviconAsync(BrowserTab tab, CoreWebView2 core)
    {
        var faviconUri = core.FaviconUri;
        if (string.IsNullOrWhiteSpace(faviconUri))
        {
            tab.FaviconImage.Source = null;
            tab.FaviconImage.Visibility = Visibility.Collapsed;
            return;
        }

        using var faviconStream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
        var favicon = new BitmapImage();
        favicon.BeginInit();
        favicon.CacheOption = BitmapCacheOption.OnLoad;
        favicon.DecodePixelWidth = 32;
        favicon.StreamSource = faviconStream;
        favicon.EndInit();
        favicon.Freeze();

        if (tab.WebView?.CoreWebView2 == core &&
            string.Equals(core.FaviconUri, faviconUri, StringComparison.Ordinal))
        {
            tab.FaviconImage.Source = favicon;
            tab.FaviconImage.Visibility = Visibility.Visible;
        }
    }

    private async Task RefreshTabFaviconAsync(BrowserTab tab, CoreWebView2 core)
    {
        try
        {
            await UpdateTabFaviconAsync(tab, core);
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or
                                           FileFormatException or IOException)
        {
            StatusText.Text = $"Could not load website icon: {exception.Message}";
        }
    }

    private void UpdateTabWidths()
    {
        foreach (var tab in _browserTabs)
        {
            tab.Item.Width = BrowserTabWidth;
            tab.Item.MinWidth = BrowserTabWidth;
            tab.TitleLabel.Visibility = Visibility.Visible;
            tab.CloseButton.Visibility = Visibility.Visible;
        }
    }

    private void Tabs_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTabWidths();

    private void Tabs_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Tabs.Template.FindName("TabScrollViewer", Tabs) is not ScrollViewer scrollViewer)
            return;

        _tabScrollViewer = scrollViewer;
        AnimateTabScroll(Math.Clamp(
            scrollViewer.HorizontalOffset - e.Delta,
            0,
            scrollViewer.ScrollableWidth));
        e.Handled = true;
    }

    private void AnimateTabScroll(double targetOffset)
    {
        if (_tabScrollViewer is null)
            return;

        _scrollAnimationTimer.Stop();
        _scrollAnimationStart = _tabScrollViewer.HorizontalOffset;
        _scrollAnimationTarget = Math.Clamp(targetOffset, 0, _tabScrollViewer.ScrollableWidth);
        _scrollAnimationStarted = DateTime.UtcNow;
        _scrollAnimationTimer.Start();
    }

    private void ScrollAnimationTimer_Tick(object? sender, EventArgs e)
    {
        if (_tabScrollViewer is null)
        {
            _scrollAnimationTimer.Stop();
            return;
        }

        var progress = Math.Clamp((DateTime.UtcNow - _scrollAnimationStarted).TotalMilliseconds / 220, 0, 1);
        var easedProgress = 1 - Math.Pow(1 - progress, 3);
        _tabScrollViewer.ScrollToHorizontalOffset(
            _scrollAnimationStart + (_scrollAnimationTarget - _scrollAnimationStart) * easedProgress);
        if (progress >= 1)
            _scrollAnimationTimer.Stop();
    }

    private BrowserTab? SelectedTab() => _browserTabs.FirstOrDefault(tab => tab.Item == Tabs.SelectedItem);

    private void CloseTab(BrowserTab tab)
    {
        if (tab.IsClosing)
            return;
        if (tab.IsNotes && !ConfirmNoteChanges(tab))
            return;

        tab.IsClosing = true;
        var duration = MotionDuration(140);
        if (duration != TimeSpan.Zero)
        {
            tab.CloseButton.IsEnabled = false;
            var fadeOut = new DoubleAnimation(tab.Item.Opacity, 0, duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            fadeOut.Completed += (_, _) => RemoveTab(tab);
            tab.Item.BeginAnimation(OpacityProperty, fadeOut);
            if (tab.Item.RenderTransform is TranslateTransform transform)
                transform.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(0, -5, duration));
            return;
        }

        RemoveTab(tab);
    }

    private void RemoveTab(BrowserTab tab)
    {
        tab.IsClosing = true;
        tab.InitializationCancellation.Cancel();
        if (tab.WebView is { } webView)
        {
            BrowserHost.Children.Remove(webView);
            tab.WebView = null;
            webView.Dispose();
        }
        tab.InitializationCancellation.Dispose();
        if (tab.NotesPanel is not null)
            BrowserHost.Children.Remove(tab.NotesPanel);
        Tabs.Items.Remove(tab.Item);
        _browserTabs.Remove(tab);
        UpdateTabWidths();
        if (_browserTabs.Count == 0)
            CreateTab("", true);
    }

    private void NewTab_Click(object sender, RoutedEventArgs e) => CreateTab("", true);

    private void NewNotesTab_Click(object sender, RoutedEventArgs e) =>
        CreateTab("", true, isNotes: true);

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == Tabs && SelectedTab() is { } tab)
        {
            _ = ActivateTabAsync(tab);
            tab.Item.BringIntoView();
        }
    }

    private void Tabs_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(Tabs);
        var item = e.OriginalSource is DependencyObject source
            ? ItemsControl.ContainerFromElement(Tabs, source) as TabItem
            : null;
        _draggedTab = item is null ? null : _browserTabs.FirstOrDefault(tab => tab.Item == item);

        if (FindVisualParent<Button>(e.OriginalSource as DependencyObject) is not null)
            _draggedTab = null;
    }

    private void Tabs_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggedTab is null || e.LeftButton != MouseButtonState.Pressed)
            return;

        var position = e.GetPosition(Tabs);
        if (Math.Abs(position.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var tab = _draggedTab;
        _draggedTab = null;
        DragDrop.DoDragDrop(Tabs, tab, DragDropEffects.Move);
    }

    private void Tabs_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(BrowserTab)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private static T? FindVisualParent<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match)
                return match;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private void Tabs_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(BrowserTab)) is not BrowserTab source)
            return;

        var targetItem = e.OriginalSource is DependencyObject target
            ? ItemsControl.ContainerFromElement(Tabs, target) as TabItem
            : null;
        var sourceIndex = Tabs.Items.IndexOf(source.Item);
        var targetIndex = targetItem is null ? Tabs.Items.Count - 1 : Tabs.Items.IndexOf(targetItem);
        if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex)
            return;

        var previousPositions = _browserTabs.ToDictionary(tab => tab, GetTabLeft);
        Tabs.Items.RemoveAt(sourceIndex);
        Tabs.Items.Insert(targetIndex, source.Item);
        _browserTabs.Remove(source);
        _browserTabs.Insert(targetIndex, source);
        UpdateTabWidths();
        Tabs.SelectedItem = source.Item;
        Dispatcher.BeginInvoke(() => AnimateTabReorder(previousPositions), DispatcherPriority.Loaded);
        e.Handled = true;
    }

    private double GetTabLeft(BrowserTab tab)
    {
        if (!tab.Item.IsLoaded || !Tabs.IsLoaded)
            return 0;
        return tab.Item.TransformToAncestor(Tabs).Transform(new Point(0, 0)).X;
    }

    private void AnimateTabReorder(IReadOnlyDictionary<BrowserTab, double> previousPositions)
    {
        foreach (var (tab, previousLeft) in previousPositions)
        {
            if (!tab.Item.IsLoaded)
                continue;

            var offset = previousLeft - GetTabLeft(tab);
            if (Math.Abs(offset) < 1)
                continue;

            if (tab.Item.RenderTransform is not TranslateTransform translate)
            {
                translate = new TranslateTransform();
                tab.Item.RenderTransform = translate;
            }

            translate.X = offset;
            translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromMilliseconds(240),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
    }

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Navigate(AddressBox.Text);
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 &&
            (Keyboard.Modifiers & ModifierKeys.Alt) == 0)
        {
            if (SelectedTab() is { IsNotes: true } notesTab)
            {
                switch (e.Key)
                {
                    case Key.S:
                        SaveNote(notesTab);
                        e.Handled = true;
                        return;
                    case Key.O:
                        OpenNote(notesTab);
                        e.Handled = true;
                        return;
                    case Key.N:
                        NewNote(notesTab);
                        e.Handled = true;
                        return;
                    case Key.F:
                    case Key.H:
                        ShowFindReplace(notesTab);
                        e.Handled = true;
                        return;
                }
            }

            if (e.Key is Key.OemPlus or Key.Add)
            {
                AdjustZoom(0.1);
                e.Handled = true;
            }
            else if (e.Key is Key.OemMinus or Key.Subtract)
            {
                AdjustZoom(-0.1);
                e.Handled = true;
            }
            else if (e.Key == Key.L)
            {
                AddressBox.Focus();
                AddressBox.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.T)
            {
                CreateTab("", true);
                e.Handled = true;
            }
            else if (e.Key == Key.W && SelectedTab() is { } tab)
            {
                CloseTab(tab);
                e.Handled = true;
            }
            else if (e.Key == Key.R)
            {
                Reload_Click(this, new RoutedEventArgs());
                e.Handled = true;
            }
        }
        else if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0 &&
                 (Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            if (e.Key == Key.Left) Back_Click(this, new RoutedEventArgs());
            if (e.Key == Key.Right) Forward_Click(this, new RoutedEventArgs());
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTab()?.WebView?.CoreWebView2 is { CanGoBack: true } core)
            core.GoBack();
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTab()?.WebView?.CoreWebView2 is { CanGoForward: true } core)
            core.GoForward();
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => SelectedTab()?.WebView?.CoreWebView2?.Reload();

    private void ToggleFullscreen()
    {
        if (_isFullscreen)
        {
            _fullscreenHideTimer.Stop();
            WindowState = WindowState.Normal;
            WindowStyle = _previousWindowStyle;
            ResizeMode = _previousResizeMode;
            MainLayout.RowDefinitions[0].Height = new GridLength(42);
            MainLayout.RowDefinitions[1].Height = new GridLength(52);
            MainLayout.RowDefinitions[3].Height = new GridLength(26);
            TabBar.Visibility = Visibility.Visible;
            NavigationBar.Visibility = Visibility.Visible;
            StatusText.Visibility = Visibility.Visible;
            Grid.SetRow(TabBar, 0);
            Grid.SetRow(NavigationBar, 1);
            TabBar.VerticalAlignment = VerticalAlignment.Stretch;
            NavigationBar.VerticalAlignment = VerticalAlignment.Stretch;
            NavigationBar.Margin = new Thickness(0);
            TabBar.RenderTransform = Transform.Identity;
            NavigationBar.RenderTransform = Transform.Identity;
            Panel.SetZIndex(TabBar, 0);
            Panel.SetZIndex(NavigationBar, 0);
            WindowState = _previousWindowState;
            _isFullscreen = false;
            _fullscreenChromeVisible = false;
            return;
        }

        _previousWindowState = WindowState;
        _previousWindowStyle = WindowStyle;
        _previousResizeMode = ResizeMode;
        Grid.SetRow(TabBar, 2);
        Grid.SetRow(NavigationBar, 2);
        TabBar.VerticalAlignment = VerticalAlignment.Top;
        NavigationBar.VerticalAlignment = VerticalAlignment.Top;
        NavigationBar.Margin = new Thickness(0, 42, 0, 0);
        TabBar.RenderTransform = new TranslateTransform { Y = -FullscreenChromeHeight };
        NavigationBar.RenderTransform = new TranslateTransform { Y = -FullscreenChromeHeight };
        Panel.SetZIndex(TabBar, 10);
        Panel.SetZIndex(NavigationBar, 10);
        MainLayout.RowDefinitions[0].Height = new GridLength(0);
        MainLayout.RowDefinitions[1].Height = new GridLength(0);
        MainLayout.RowDefinitions[3].Height = new GridLength(0);
        TabBar.Visibility = Visibility.Visible;
        NavigationBar.Visibility = Visibility.Visible;
        StatusText.Visibility = Visibility.Collapsed;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;
        _isFullscreen = true;
        _fullscreenChromeVisible = false;
        _fullscreenEdgeTimer.Start();
        if (Mouse.GetPosition(this).Y <= 10 && !_fullscreenChromeVisible)
            AnimateFullscreenChrome(true);
    }

    private void Window_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isFullscreen)
            return;

        if (e.GetPosition(this).Y <= 10)
        {
            _fullscreenHideTimer.Stop();
            if (!_fullscreenChromeVisible)
                AnimateFullscreenChrome(true);
        }
        else if (_fullscreenChromeVisible && e.GetPosition(this).Y > FullscreenChromeHeight + 4)
        {
            StartFullscreenHideTimer();
        }
    }

    private void CheckFullscreenMousePosition()
    {
        if (!_isFullscreen)
            return;

        var mouseY = Mouse.GetPosition(this).Y;
        if (mouseY <= 10)
        {
            _fullscreenHideTimer.Stop();
            if (!_fullscreenChromeVisible)
                AnimateFullscreenChrome(true);
        }
        else if (_fullscreenChromeVisible && mouseY > FullscreenChromeHeight + 4)
        {
            StartFullscreenHideTimer();
        }
    }

    private void StartFullscreenHideTimer()
    {
        if (!_fullscreenHideTimer.IsEnabled)
            _fullscreenHideTimer.Start();
    }

    private void AnimateFullscreenChrome(bool show)
    {
        _fullscreenChromeVisible = show;
        var target = show ? 0 : -FullscreenChromeHeight;
        var animation = new DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(260),
            EasingFunction = new CubicEase { EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn }
        };
        if (TabBar.RenderTransform is not TranslateTransform tabTransform ||
            NavigationBar.RenderTransform is not TranslateTransform navigationTransform)
            return;

        tabTransform.BeginAnimation(TranslateTransform.YProperty, animation);
        NavigationBar.RenderTransform.BeginAnimation(TranslateTransform.YProperty, animation);
    }

    private void AdjustZoom(double amount) => SetZoom(Math.Clamp(_zoomFactor + amount, 0.25, 5.0));

    private void SetZoom(double zoom)
    {
        _zoomFactor = zoom;
        foreach (var tab in _browserTabs)
        {
            if (tab.WebView is { } webView)
                webView.ZoomFactor = zoom;
        }
        StatusText.Text = $"Page zoom: {zoom:P0}";
    }

    private void Home_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTab() is not { } tab)
            return;
        if (tab.IsNotes)
            return;

        tab.PendingAddress = "";
        tab.CurrentSiteHost = null;
        tab.UserEnteredHost = null;
        ClearTabSearch(tab);
        tab.WebView?.CoreWebView2?.Navigate("about:blank");
        BrowserHost.Children.Clear();
        BrowserHost.Visibility = Visibility.Collapsed;
        EmptyPage.Visibility = Visibility.Visible;
        AddressBox.Clear();
    }

    private sealed record BrowserHistoryEntry(DateTimeOffset VisitedAt, string Title, string Address);

    private sealed record BrowserDownload(
        CoreWebView2DownloadOperation Operation,
        string FilePath,
        TextBlock StateText,
        ProgressBar ProgressBar,
        Button OpenButton);

    private sealed class BrowserSettingsWindow : Window
    {
        public BrowserSettingsWindow(
            ObservableCollection<BrowserHistoryEntry> history,
            Action clearHistory,
            Action openSearchIndex)
        {
            Title = "Klyro Settings";
            Width = 900;
            Height = 520;
            MinWidth = 640;
            MinHeight = 340;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(17, 16, 23));
            Foreground = new SolidColorBrush(Color.FromRgb(245, 242, 250));

            var layout = new Grid { Margin = new Thickness(18) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            heading.Children.Add(new TextBlock
            {
                Text = "Search and browsing history",
                FontSize = 20,
                FontWeight = FontWeights.SemiBold
            });
            heading.Children.Add(new TextBlock
            {
                Text = "History is stored locally on this device.",
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(142, 137, 154))
            });
            layout.Children.Add(heading);

            var historyList = new ListView
            {
                ItemsSource = history,
                Background = new SolidColorBrush(Color.FromRgb(25, 23, 33)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(40, 36, 49)),
                BorderThickness = new Thickness(1)
            };
            var columns = new GridView();
            columns.Columns.Add(new GridViewColumn
            {
                Header = "Added",
                Width = 160,
                DisplayMemberBinding = new System.Windows.Data.Binding(nameof(BrowserHistoryEntry.VisitedAt))
                {
                    StringFormat = "{0:g}"
                }
            });
            columns.Columns.Add(new GridViewColumn
            {
                Header = "Site",
                Width = 210,
                DisplayMemberBinding = new System.Windows.Data.Binding(nameof(BrowserHistoryEntry.Title))
            });
            columns.Columns.Add(new GridViewColumn
            {
                Header = "Address",
                Width = 470,
                DisplayMemberBinding = new System.Windows.Data.Binding(nameof(BrowserHistoryEntry.Address))
            });
            historyList.View = columns;
            Grid.SetRow(historyList, 1);
            layout.Children.Add(historyList);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            var clearButton = new Button
            {
                Content = "Clear history",
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 8, 0)
            };
            clearButton.Click += (_, _) => clearHistory();
            var closeButton = new Button
            {
                Content = "Close",
                Padding = new Thickness(12, 6, 12, 6),
                IsDefault = true
            };
            closeButton.Click += (_, _) => Close();
            buttons.Children.Add(clearButton);
            var searchIndexButton = new Button
            {
                Content = "Manage search index",
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 8, 0)
            };
            searchIndexButton.Click += (_, _) => openSearchIndex();
            buttons.Children.Add(searchIndexButton);
            buttons.Children.Add(closeButton);
            Grid.SetRow(buttons, 2);
            layout.Children.Add(buttons);

            Content = layout;
        }
    }

    private sealed class BrowserTab(string address)
    {
        public string PendingAddress { get; set; } = address;
        public bool IsNotes { get; set; }
        public string? CurrentSiteHost { get; set; }
        public string? UserEnteredHost { get; set; }
        public string? NavigationDeniedMessage { get; set; }
        public string? SearchQuery { get; set; }
        public StackPanel? SearchResultsPanel { get; set; }
        public TextBlock? SearchSourceText { get; set; }
        public StackPanel? SearchResultsList { get; set; }
        public Queue<DateTime> RecentSiteChanges { get; } = new();
        public TabItem Item { get; set; } = null!;
        public TextBlock TitleLabel { get; set; } = null!;
        public Image FaviconImage { get; set; } = null!;
        public Button CloseButton { get; set; } = null!;
        public FrameworkElement? NotesPanel { get; set; }
        public TextBox? NoteNameBox { get; set; }
        public ComboBox? NoteFormatBox { get; set; }
        public RichTextBox? NoteEditor { get; set; }
        public TextBlock? NoteStatusText { get; set; }
        public string? NoteFilePath { get; set; }
        public bool NoteIsModified { get; set; }
        public WebView2? WebView { get; set; }
        public Task? InitializationTask { get; set; }
        public CancellationTokenSource InitializationCancellation { get; } = new();
        public bool IsClosing { get; set; }
    }

    private sealed record SearchCard(string Title, string Url, string Snippet);
}
