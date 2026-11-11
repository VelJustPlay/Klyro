using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LightweightBrowser;

internal sealed class SearchIndexWindow : Window
{
    private readonly KlyroSearchIndex _index;
    private readonly ListBox _seedList = new();
    private readonly TextBox _seedInput = new();
    private readonly TextBlock _status = new();
    private readonly Button _crawlButton = new() { Content = "Crawl sites" };
    private readonly Button _stopButton = new() { Content = "Stop", IsEnabled = false };
    private CancellationTokenSource? _crawlCancellation;

    public SearchIndexWindow(KlyroSearchIndex index)
    {
        _index = index;
        Title = "Klyro Search Index";
        Width = 720;
        Height = 500;
        MinWidth = 560;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(17, 16, 23));
        Foreground = new SolidColorBrush(Color.FromRgb(245, 242, 250));
        Closed += (_, _) => _crawlCancellation?.Cancel();

        var layout = new Grid { Margin = new Thickness(18) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        heading.Children.Add(new TextBlock
        {
            Text = "Klyro Search",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold
        });
        heading.Children.Add(new TextBlock
        {
            Text = "Local index · user-added sites only · respects robots.txt · 50 pages per site",
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(142, 137, 154)),
            TextWrapping = TextWrapping.Wrap
        });
        layout.Children.Add(heading);

        _seedList.Background = new SolidColorBrush(Color.FromRgb(25, 23, 33));
        _seedList.BorderBrush = new SolidColorBrush(Color.FromRgb(40, 36, 49));
        _seedList.BorderThickness = new Thickness(1);
        _seedList.ItemsSource = _index.Seeds;
        Grid.SetRow(_seedList, 1);
        layout.Children.Add(_seedList);

        var seedControls = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var addButton = new Button
        {
            Content = "Add site",
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(8, 0, 0, 0)
        };
        addButton.Click += (_, _) => AddSeed();
        DockPanel.SetDock(addButton, Dock.Right);
        seedControls.Children.Add(addButton);
        _seedInput.Text = "https://";
        _seedInput.Padding = new Thickness(9, 6, 9, 6);
        seedControls.Children.Add(_seedInput);
        Grid.SetRow(seedControls, 2);
        layout.Children.Add(seedControls);

        var footer = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        footer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var closeButton = new Button
        {
            Content = "Close",
            Padding = new Thickness(12, 6, 12, 6),
            IsCancel = true
        };
        Grid.SetColumn(closeButton, 1);
        footer.Children.Add(closeButton);

        var actionButtons = new StackPanel { Orientation = Orientation.Horizontal };
        var removeButton = new Button
        {
            Content = "Remove selected",
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 8, 0)
        };
        removeButton.Click += (_, _) => RemoveSelectedSeed();
        _crawlButton.Padding = new Thickness(12, 6, 12, 6);
        _crawlButton.Click += async (_, _) => await CrawlAsync();
        _stopButton.Padding = new Thickness(12, 6, 12, 6);
        _stopButton.Margin = new Thickness(8, 0, 0, 0);
        _stopButton.Click += (_, _) => _crawlCancellation?.Cancel();
        actionButtons.Children.Add(removeButton);
        actionButtons.Children.Add(_crawlButton);
        actionButtons.Children.Add(_stopButton);
        Grid.SetRow(actionButtons, 0);
        footer.Children.Add(actionButtons);
        Grid.SetRow(footer, 3);
        layout.Children.Add(footer);

        var statusPanel = new Border
        {
            Padding = new Thickness(9, 6, 9, 6),
            Margin = new Thickness(0, 8, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(25, 23, 33)),
            Child = _status
        };
        _status.Text = $"{_index.PageCount} pages indexed locally.";
        _status.Foreground = new SolidColorBrush(Color.FromRgb(190, 184, 203));
        Grid.SetRow(statusPanel, 1);
        Grid.SetColumnSpan(statusPanel, 2);
        footer.Children.Add(statusPanel);

        Content = layout;
    }

    private void AddSeed()
    {
        try
        {
            _index.AddSeed(_seedInput.Text);
            RefreshSeeds();
            _seedInput.Clear();
            _status.Text = "Site added. Crawling begins when you choose Crawl sites.";
        }
        catch (ArgumentException exception)
        {
            _status.Text = exception.Message;
        }
        catch (IOException exception)
        {
            _status.Text = $"Could not save the site list: {exception.Message}";
        }
        catch (UnauthorizedAccessException exception)
        {
            _status.Text = $"Could not save the site list: {exception.Message}";
        }
    }

    private void RemoveSelectedSeed()
    {
        if (_seedList.SelectedItem is not string seed)
            return;

        try
        {
            _index.RemoveSeed(seed);
            RefreshSeeds();
            _status.Text = $"Removed {seed} and its indexed pages.";
        }
        catch (IOException exception)
        {
            _status.Text = $"Could not update the local index: {exception.Message}";
        }
        catch (UnauthorizedAccessException exception)
        {
            _status.Text = $"Could not update the local index: {exception.Message}";
        }
    }

    private async Task CrawlAsync()
    {
        _crawlCancellation = new CancellationTokenSource();
        _crawlButton.IsEnabled = false;
        _stopButton.IsEnabled = true;
        var progress = new Progress<string>(message => _status.Text = message);
        try
        {
            var count = await _index.CrawlAllAsync(progress, _crawlCancellation.Token);
            _status.Text = $"Finished. Added or refreshed {count} pages; {_index.PageCount} pages indexed locally.";
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Crawl stopped.";
        }
        catch (HttpRequestException exception)
        {
            _status.Text = $"Crawl failed: {exception.Message}";
        }
        catch (IOException exception)
        {
            _status.Text = $"Could not save the local index: {exception.Message}";
        }
        catch (UnauthorizedAccessException exception)
        {
            _status.Text = $"Could not save the local index: {exception.Message}";
        }
        finally
        {
            _crawlCancellation.Dispose();
            _crawlCancellation = null;
            _crawlButton.IsEnabled = true;
            _stopButton.IsEnabled = false;
        }
    }

    private void RefreshSeeds() => _seedList.ItemsSource = _index.Seeds;
}
