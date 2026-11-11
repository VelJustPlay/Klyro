using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Interactivity;

namespace LightweightBrowser;

public sealed class MainWindow : Window
{
    private readonly string _dataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Klyro");
    private readonly ObservableCollection<BrowserHistoryEntry> _history = [];
    private readonly List<BrowserTab> _tabs = [];
    private readonly StackPanel _tabHeaders = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly Grid _pageHost = new();
    private readonly TextBox _address = new() { PlaceholderText = "Search or enter an address" };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center };
    private KlyroSearchIndex? _searchIndex;
    private BrowserTab? _selectedTab;
    private double _zoom = 1;
    private bool _fullscreen;

    private string HistoryFilePath => Path.Combine(_dataFolder, "history.json");

    public MainWindow()
    {
        Title = "Klyro";
        Width = 1180;
        Height = 760;
        MinWidth = 640;
        MinHeight = 420;
        Background = Brush("#09080D");
        Foreground = Brush("#F5F2FA");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        try
        {
            Directory.CreateDirectory(_dataFolder);
            LoadHistory();
            _searchIndex = new KlyroSearchIndex(_dataFolder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            SetStatus($"Could not initialize browser data: {exception.Message}");
        }

        Content = BuildLayout();
        CreateTab();
        KeyDown += Window_KeyDown;
        Opened += (_, _) => _address.Focus();
    }

    private Control BuildLayout()
    {
        var root = new DockPanel();
        var tabBar = new DockPanel { Margin = new Thickness(10, 8, 10, 5), LastChildFill = false };
        var brand = new TextBlock
        {
            Text = "◈  Klyro",
            FontSize = 17,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 14, 0)
        };
        DockPanel.SetDock(brand, Dock.Left);
        tabBar.Children.Add(brand);
        var tabScroll = new ScrollViewer
        {
            Content = _tabHeaders,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Width = 640,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        DockPanel.SetDock(tabScroll, Dock.Left);
        tabBar.Children.Add(tabScroll);
        AddButton(tabBar, "+", "New tab", (_, _) => CreateTab(), Dock.Right);
        AddButton(tabBar, "▤", "New notes tab", (_, _) => CreateNotesTab(), Dock.Right);
        DockPanel.SetDock(tabBar, Dock.Top);
        root.Children.Add(tabBar);

        var navigation = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,*,Auto,Auto,Auto"),
            Margin = new Thickness(9, 0, 9, 8)
        };
        AddButton(navigation, "←", "Back", (_, _) => _selectedTab?.WebView?.GoBack(), 0);
        AddButton(navigation, "→", "Forward", (_, _) => _selectedTab?.WebView?.GoForward(), 1);
        AddButton(navigation, "↻", "Reload", (_, _) => _selectedTab?.WebView?.Refresh(), 2);
        AddButton(navigation, "⌂", "New blank tab", (_, _) => CreateTab(), 3);
        _address.Margin = new Thickness(5, 0);
        _address.VerticalContentAlignment = VerticalAlignment.Center;
        _address.Padding = new Thickness(12, 8);
        _address.Background = Brush("#191721");
        _address.Foreground = Brushes.White;
        _address.BorderBrush = Brush("#393342");
        _address.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Navigate(_address.Text ?? "");
                e.Handled = true;
            }
        };
        Grid.SetColumn(_address, 4);
        navigation.Children.Add(_address);
        AddButton(navigation, "↓", "Downloads (not supported by the native WebView API)", (_, _) =>
            SetStatus("Downloads are not exposed by Avalonia.Controls.WebView; no download manager is available."), 5);
        AddButton(navigation, "◷", "History", (_, _) => ShowHistory(), 6);
        AddButton(navigation, "⚙", "Settings and search index", (_, _) => ShowSettings(), 7);
        DockPanel.SetDock(navigation, Dock.Top);
        root.Children.Add(navigation);

        var footer = new Border
        {
            Background = Brush("#111017"),
            BorderBrush = Brush("#282431"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(10, 4),
            Child = _status
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(_pageHost);
        return root;
    }

    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));

    private void AddButton(Panel panel, string text, string tooltip, EventHandler<RoutedEventArgs> handler, Dock dock)
    {
        var button = MakeButton(text, tooltip, handler);
        DockPanel.SetDock(button, dock);
        panel.Children.Add(button);
    }

    private void AddButton(Grid grid, string text, string tooltip, EventHandler<RoutedEventArgs> handler, int column)
    {
        var button = MakeButton(text, tooltip, handler);
        button.Margin = new Thickness(2, 0);
        Grid.SetColumn(button, column);
        grid.Children.Add(button);
    }

    private static Button MakeButton(string text, string tooltip, EventHandler<RoutedEventArgs> handler)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(10, 6),
            MinWidth = 36,
            Background = Brush("#191721"),
            Foreground = Brushes.White,
            BorderBrush = Brush("#393342")
        };
        ToolTip.SetTip(button, tooltip);
        button.Click += handler;
        return button;
    }

    private void CreateTab(string? address = null)
    {
        var tab = new BrowserTab();
        tab.Content = tab.BrowserHost = new Grid();
        AddTab(tab);
        if (!string.IsNullOrWhiteSpace(address))
            Navigate(address);
    }

    private NativeWebView EnsureWebView(BrowserTab tab)
    {
        if (tab.WebView is { } existing)
            return existing;

        var view = new NativeWebView();
        tab.WebView = view;
        view.NavigationStarted += (_, _) => SetStatus("Loading...");
        view.NavigationCompleted += (_, e) =>
        {
            var uri = view.Source;
            if (uri is not null && uri.Scheme is "http" or "https")
            {
                tab.Address = uri.AbsoluteUri;
                tab.Title = uri.Host;
                tab.HeaderButton!.Content = tab.Title;
                AddHistoryEntry(tab.Title, tab.Address);
                if (ReferenceEquals(_selectedTab, tab))
                    _address.Text = tab.Address;
            }

            if (!e.IsSuccess)
                SetStatus($"Could not load {uri}: the native web engine reported a navigation failure.");
            else
            {
                SetStatus(uri?.ToString() ?? "Ready");
                _ = ApplyZoomAsync(tab);
            }
        };
        view.AdapterCreated += (_, _) => SetStatus("Native web engine ready.");
        tab.BrowserHost!.Children.Add(view);
        return view;
    }

    private void CreateNotesTab()
    {
        var tab = new BrowserTab { IsNotes = true, Title = "Notes" };
        var editor = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = Brush("#111017"),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(16),
            PlaceholderText = "Write a note..."
        };
        editor.TextChanged += (_, _) => tab.NoteModified = true;
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(8) };
        toolbar.Children.Add(MakeButton("Open", "Open a text note", async (_, _) => await OpenNoteAsync(tab)));
        toolbar.Children.Add(MakeButton("Save", "Save note", async (_, _) => await SaveNoteAsync(tab, false)));
        toolbar.Children.Add(MakeButton("Save as", "Save note to a file", async (_, _) => await SaveNoteAsync(tab, true)));
        tab.Editor = editor;
        tab.Content = new DockPanel { Children = { toolbar, editor } };
        DockPanel.SetDock(toolbar, Dock.Top);
        AddTab(tab);
        editor.Focus();
    }

    private void AddTab(BrowserTab tab)
    {
        var header = new Button
        {
            Content = tab.Title,
            Padding = new Thickness(9, 5),
            MinWidth = 100,
            Background = Brush("#191721"),
            Foreground = Brushes.White
        };
        ToolTip.SetTip(header, tab.IsNotes ? "Notes" : "Browser tab");
        header.Click += (_, _) => SelectTab(tab);
        var close = new Button { Content = "×", Padding = new Thickness(5, 3), MinWidth = 25 };
        close.Click += async (_, _) => await CloseTabAsync(tab);
        var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        item.Children.Add(header);
        item.Children.Add(close);
        tab.Header = item;
        tab.HeaderButton = header;
        _tabHeaders.Children.Add(item);
        _pageHost.Children.Add(tab.Content!);
        _tabs.Add(tab);
        SelectTab(tab);
    }

    private void SelectTab(BrowserTab tab)
    {
        _selectedTab = tab;
        foreach (var item in _tabs)
        {
            item.Content!.IsVisible = ReferenceEquals(item, tab);
            item.HeaderButton!.Background = ReferenceEquals(item, tab) ? Brush("#393044") : Brush("#191721");
        }

        _address.Text = tab.Address;
        SetStatus(tab.IsNotes ? "Notes" : tab.Address);
    }

    private async Task CloseTabAsync(BrowserTab tab)
    {
        if (tab.NoteModified && !await ShowConfirmationAsync("Discard unsaved note changes?", "Close note"))
            return;

        _tabs.Remove(tab);
        if (tab.Header is { } header)
            _tabHeaders.Children.Remove(header);
        tab.WebView = null;
        tab.BrowserHost?.Children.Clear();
        _pageHost.Children.Remove(tab.Content!);
        if (_tabs.Count == 0)
            CreateTab();
        else if (ReferenceEquals(_selectedTab, tab))
            SelectTab(_tabs[^1]);
    }

    private void Navigate(string input)
    {
        if (_selectedTab is null || _selectedTab.IsNotes || string.IsNullOrWhiteSpace(input))
            return;

        input = input.Trim();
        if (input.StartsWith("klyro:", StringComparison.OrdinalIgnoreCase))
        {
            ShowLocalSearch(input["klyro:".Length..].Trim());
            return;
        }

        Uri target;
        if (Uri.TryCreate(input, UriKind.Absolute, out var parsed) &&
            parsed.Scheme is "http" or "https")
        {
            target = parsed;
        }
        else if (!input.Contains(' ') && input.Contains('.') &&
                 Uri.TryCreate("https://" + input, UriKind.Absolute, out parsed))
        {
            target = parsed;
        }
        else
        {
            target = new Uri("https://duckduckgo.com/?q=" + Uri.EscapeDataString(input));
        }

        try
        {
            if (_selectedTab.LocalResults is not null)
            {
                _selectedTab.LocalResults.IsVisible = false;
                if (_selectedTab.WebView is { } existing)
                    existing.IsVisible = true;
            }
            EnsureWebView(_selectedTab).Navigate(target);
            _selectedTab.Address = target.AbsoluteUri;
            _address.Text = target.AbsoluteUri;
        }
        catch (Exception exception)
        {
            SetStatus($"Navigation failed: {exception.Message}");
        }
    }

    private void ShowLocalSearch(string query)
    {
        if (_selectedTab is null)
            return;
        if (_searchIndex is null)
        {
            SetStatus("Local search is unavailable because its index could not be loaded.");
            return;
        }

        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = $"Klyro Search: {query}", FontSize = 20 });
        var results = _searchIndex.Search(query);
        if (results.Count == 0)
            panel.Children.Add(new TextBlock { Text = "No matching pages. Add and crawl sites in Settings → Search index." });
        foreach (var result in results)
        {
            var card = new StackPanel { Spacing = 3, Margin = new Thickness(0, 4) };
            var linkButton = new Button
            {
                Content = result.Title,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent,
                Foreground = Brush("#D184FF")
            };
            linkButton.Click += (_, _) => Navigate(result.Url);
            card.Children.Add(linkButton);
            card.Children.Add(new TextBlock { Text = result.Url, Foreground = Brush("#8E899A") });
            card.Children.Add(new TextBlock { Text = result.Snippet, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(card);
        }
        if (_selectedTab.BrowserHost is not null)
        {
            if (_selectedTab.LocalResults is not null)
                _selectedTab.BrowserHost.Children.Remove(_selectedTab.LocalResults);
            _selectedTab.LocalResults = panel;
            _selectedTab.BrowserHost.Children.Add(panel);
            _selectedTab.WebView!.IsVisible = false;
        }
        _selectedTab.Address = $"klyro:{query}";
        _address.Text = _selectedTab.Address;
    }

    private async Task ApplyZoomAsync(BrowserTab tab)
    {
        if (tab.WebView is null)
            return;
        try
        {
            var percent = Math.Round(_zoom * 100);
            await tab.WebView.InvokeScript($"document.documentElement.style.zoom='{percent}%'");
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_selectedTab, tab))
                SetStatus($"Page zoom is unavailable: {exception.Message}");
        }
    }

    private async Task SaveNoteAsync(BrowserTab tab, bool forcePicker)
    {
        try
        {
            string? path = tab.NotePath;
            if (forcePicker || path is null)
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Save note",
                    SuggestedFileName = "Klyro note.txt",
                    FileTypeChoices = [new FilePickerFileType("Text file") { Patterns = ["*.txt", "*.md"] }]
                });
                path = file?.TryGetLocalPath();
                if (path is null)
                    return;
            }

            await File.WriteAllTextAsync(path, tab.Editor?.Text ?? "");
            tab.NotePath = path;
            tab.NoteModified = false;
            tab.Title = Path.GetFileName(path);
            tab.HeaderButton!.Content = tab.Title;
            SetStatus($"Saved note: {path}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Could not save note: {exception.Message}");
        }
    }

    private async Task OpenNoteAsync(BrowserTab tab)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open note",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Text files") { Patterns = ["*.txt", "*.md"] }]
            });
            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
                return;
            if (tab.NoteModified && !await ShowConfirmationAsync("Discard unsaved note changes?", "Open note"))
                return;
            tab.Editor!.Text = await File.ReadAllTextAsync(path);
            tab.NotePath = path;
            tab.NoteModified = false;
            tab.Title = Path.GetFileName(path);
            tab.HeaderButton!.Content = tab.Title;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Could not open note: {exception.Message}");
        }
    }

    private async Task<bool> ShowConfirmationAsync(string message, string title)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 380,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = message },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children =
                        {
                            new Button { Content = "Cancel", IsCancel = true },
                            new Button { Content = "Discard", IsDefault = true, Tag = true }
                        }
                    }
                }
            }
        };
        var discard = ((StackPanel)((StackPanel)dialog.Content).Children[1]).Children[1] as Button;
        if (discard is null)
            return false;
        discard.Click += (_, _) => dialog.Close(true);
        var result = await dialog.ShowDialog<bool>(this);
        return result;
    }

    private void ShowHistory()
    {
        var list = new StackPanel { Spacing = 5 };
        foreach (var entry in _history)
        {
            var item = MakeButton($"{entry.Title} — {entry.Url} · {entry.VisitedAt:g}", "Open history entry",
                (_, _) => Navigate(entry.Url));
            item.HorizontalContentAlignment = HorizontalAlignment.Left;
            list.Children.Add(item);
        }
        var clear = MakeButton("Clear history", "Delete all saved history", (_, _) =>
        {
            _history.Clear();
            SaveHistory();
            list.Children.Clear();
            SetStatus("History cleared.");
        });
        var panel = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(clear, Dock.Bottom);
        panel.Children.Add(clear);
        panel.Children.Add(list);
        _ = new Window
        {
            Title = "Klyro History",
            Width = 760,
            Height = 500,
            Content = panel,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        }.ShowDialog(this);
    }

    private void ShowSettings()
    {
        var indexButton = MakeButton("Manage local search index", "Manage seed sites and crawl", (_, _) => OpenSearchIndex());
        var historyButton = MakeButton("View history", "View or clear browsing history", (_, _) => ShowHistory());
        var downloads = MakeButton("Downloads", "Downloads are not supported", (_, _) =>
            SetStatus("Downloads are not exposed by Avalonia.Controls.WebView."));
        var zoomValue = new TextBlock { Text = $"{_zoom:P0}", VerticalAlignment = VerticalAlignment.Center };
        var zoomOut = MakeButton("−", "Zoom out", (_, _) =>
        {
            ChangeZoom(-.1);
            zoomValue.Text = $"{_zoom:P0}";
        });
        var zoomIn = MakeButton("+", "Zoom in", (_, _) =>
        {
            ChangeZoom(.1);
            zoomValue.Text = $"{_zoom:P0}";
        });
        var fullscreen = MakeButton("Toggle fullscreen (F11)", "Toggle fullscreen", (_, _) => ToggleFullscreen());
        var layout = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
        layout.Children.Add(new TextBlock { Text = "Klyro Settings", FontSize = 21 });
        layout.Children.Add(new TextBlock { Text = $"Browser data: {_dataFolder}", TextWrapping = TextWrapping.Wrap });
        layout.Children.Add(new TextBlock { Text = "Zoom applies a CSS document scale to the current page; native zoom settings are not exposed." });
        var zoomRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        zoomRow.Children.Add(zoomOut);
        zoomRow.Children.Add(zoomValue);
        zoomRow.Children.Add(zoomIn);
        layout.Children.Add(zoomRow);
        layout.Children.Add(fullscreen);
        layout.Children.Add(historyButton);
        layout.Children.Add(indexButton);
        layout.Children.Add(downloads);
        layout.Children.Add(new TextBlock { Text = "Privacy and startup controls are not exposed by the native web engine. Automatic updates are not built into this cross-platform version." });
        var window = new Window
        {
            Title = "Klyro Settings",
            Width = 540,
            Height = 430,
            Content = layout,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        window.ShowDialog(this);
    }

    private void ChangeZoom(double amount)
    {
        _zoom = Math.Clamp(_zoom + amount, .25, 3);
        if (_selectedTab is not null)
            _ = ApplyZoomAsync(_selectedTab);
    }

    private void ToggleFullscreen()
    {
        _fullscreen = !_fullscreen;
        WindowState = _fullscreen ? WindowState.FullScreen : WindowState.Normal;
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
            ToggleFullscreen();
        else if (e.Key == Key.L && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _address.Focus();
            _address.SelectAll();
        }
        else if (e.Key == Key.T && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            CreateTab();
        else if (e.Key == Key.W && e.KeyModifiers.HasFlag(KeyModifiers.Control) && _selectedTab is not null)
            _ = CloseTabAsync(_selectedTab);
        else if (e.Key == Key.R && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            _selectedTab?.WebView?.Refresh();
        else if ((e.Key is Key.Add or Key.OemPlus) && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            ChangeZoom(.1);
        else if ((e.Key is Key.Subtract or Key.OemMinus) && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            ChangeZoom(-.1);
    }

    private void OpenSearchIndex()
    {
        if (_searchIndex is null)
        {
            SetStatus("Local search index is unavailable.");
            return;
        }
        new SearchIndexWindow(_searchIndex).ShowDialog(this);
    }

    private void AddHistoryEntry(string title, string url)
    {
        if (_history.FirstOrDefault(x => x.Url.Equals(url, StringComparison.OrdinalIgnoreCase)) is { } existing)
            _history.Remove(existing);
        _history.Insert(0, new BrowserHistoryEntry(title, url, DateTimeOffset.Now));
        while (_history.Count > 2000)
            _history.RemoveAt(_history.Count - 1);
        SaveHistory();
    }

    private void LoadHistory()
    {
        if (!File.Exists(HistoryFilePath))
            return;
        var entries = JsonSerializer.Deserialize<List<BrowserHistoryEntry>>(File.ReadAllText(HistoryFilePath));
        if (entries is not null)
            foreach (var entry in entries.Take(2000))
                _history.Add(entry);
    }

    private void SaveHistory()
    {
        try
        {
            File.WriteAllText(HistoryFilePath, JsonSerializer.Serialize(_history, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Could not save history: {exception.Message}");
        }
    }

    private void SetStatus(string message) => _status.Text = message;

    private sealed class BrowserTab
    {
        public string Title { get; set; } = "New tab";
        public string Address { get; set; } = "";
        public bool IsNotes { get; init; }
        public bool NoteModified { get; set; }
        public string? NotePath { get; set; }
        public Control? Content { get; set; }
        public Grid? BrowserHost { get; set; }
        public Control? LocalResults { get; set; }
        public StackPanel? Header { get; set; }
        public Button? HeaderButton { get; set; }
        public TextBox? Editor { get; set; }
        public NativeWebView? WebView { get; set; }
    }

    private sealed record BrowserHistoryEntry(string Title, string Url, DateTimeOffset VisitedAt);
}
