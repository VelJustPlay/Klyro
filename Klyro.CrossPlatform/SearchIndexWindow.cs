using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace LightweightBrowser;

internal sealed class SearchIndexWindow : Window
{
    private readonly KlyroSearchIndex _index;
    private readonly ListBox _seeds = new();
    private readonly TextBox _input = new() { PlaceholderText = "https://example.com" };
    private readonly TextBlock _status = new();
    private readonly Button _crawl = new() { Content = "Crawl sites" };
    private readonly Button _stop = new() { Content = "Stop", IsEnabled = false };
    private CancellationTokenSource? _cancellation;

    public SearchIndexWindow(KlyroSearchIndex index)
    {
        _index = index;
        Title = "Klyro Search Index";
        Width = 700;
        Height = 470;
        MinWidth = 540;
        MinHeight = 350;
        Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#111017"));
        Closed += (_, _) => _cancellation?.Cancel();

        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            Margin = new Thickness(16)
        };
        var title = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 10) };
        title.Children.Add(new TextBlock { Text = "Klyro Search", FontSize = 21 });
        title.Children.Add(new TextBlock
        {
            Text = "Locally indexed, user-seeded sites only. Crawling respects robots.txt and is limited to 50 pages per site.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        layout.Children.Add(title);

        _seeds.ItemsSource = _index.Seeds.ToArray();
        Grid.SetRow(_seeds, 1);
        layout.Children.Add(_seeds);

        var seedControls = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 8)
        };
        seedControls.Children.Add(_input);
        var add = new Button { Content = "Add site", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 6) };
        add.Click += (_, _) => AddSeed();
        Grid.SetColumn(add, 1);
        seedControls.Children.Add(add);
        Grid.SetRow(seedControls, 2);
        layout.Children.Add(seedControls);

        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            Margin = new Thickness(0, 4, 0, 0)
        };
        var remove = new Button { Content = "Remove selected", Padding = new Thickness(10, 5), Margin = new Thickness(0, 0, 6, 0) };
        remove.Click += (_, _) => RemoveSelectedSeed();
        footer.Children.Add(remove);
        _crawl.Padding = new Thickness(10, 5);
        _crawl.Click += async (_, _) => await CrawlAsync();
        Grid.SetColumn(_crawl, 1);
        footer.Children.Add(_crawl);
        _stop.Padding = new Thickness(10, 5);
        _stop.Margin = new Thickness(6, 0);
        _stop.Click += (_, _) => _cancellation?.Cancel();
        Grid.SetColumn(_stop, 2);
        footer.Children.Add(_stop);
        var close = new Button { Content = "Close", Padding = new Thickness(10, 5), IsCancel = true };
        Grid.SetColumn(close, 3);
        footer.Children.Add(close);
        var statusPanel = new Border
        {
            Padding = new Thickness(8, 6),
            Margin = new Thickness(0, 8, 0, 0),
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#191721")),
            Child = _status
        };
        _status.Text = $"{_index.PageCount} pages indexed locally.";
        Grid.SetRow(statusPanel, 1);
        Grid.SetColumnSpan(statusPanel, 5);
        footer.Children.Add(statusPanel);
        Grid.SetRow(footer, 3);
        layout.Children.Add(footer);
        Content = layout;
    }

    private void AddSeed()
    {
        try
        {
            _index.AddSeed(_input.Text ?? "");
            RefreshSeeds();
            _input.Clear();
            _status.Text = "Site added. Crawl sites when you are ready.";
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            _status.Text = $"Could not add site: {exception.Message}";
        }
    }

    private void RemoveSelectedSeed()
    {
        if (_seeds.SelectedItem is not string seed)
            return;
        try
        {
            _index.RemoveSeed(seed);
            RefreshSeeds();
            _status.Text = $"Removed {seed} and its indexed pages.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _status.Text = $"Could not update local index: {exception.Message}";
        }
    }

    private async Task CrawlAsync()
    {
        _cancellation = new CancellationTokenSource();
        _crawl.IsEnabled = false;
        _stop.IsEnabled = true;
        try
        {
            var progress = new Progress<string>(message => _status.Text = message);
            var count = await _index.CrawlAllAsync(progress, _cancellation.Token);
            _status.Text = $"Finished. Added or refreshed {count} pages; {_index.PageCount} indexed locally.";
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Crawl stopped.";
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            _status.Text = $"Could not crawl/save local index: {exception.Message}";
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            _crawl.IsEnabled = true;
            _stop.IsEnabled = false;
        }
    }

    private void RefreshSeeds() => _seeds.ItemsSource = _index.Seeds.ToArray();
}
