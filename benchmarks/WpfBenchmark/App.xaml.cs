using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace WpfBenchmark;

public partial class App : Application
{
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();

    public sealed record GameRow(List<GameItem> Cells);
    public sealed class GameItem
    {
        public string Title { get; init; } = "";
        public string Status { get; init; } = "";
        public BitmapImage? Cover { get; set; }
    }

    private void OnStartup(object sender, StartupEventArgs e)
    {
        string? outJson = e.Args.Length > 0 ? e.Args[0] : null;
        string sampleCover = @"C:\Program Files (x86)\Steam\appcache\librarycache\4126040\093428100a3d889c1abf7f85a74a1ab1d86965cd\library_capsule.jpg";

        var win = new Window
        {
            Title = "Project Luma WPF Benchmark",
            Width = 1280,
            Height = 760,
            Background = new SolidColorBrush(Color.FromRgb(10, 12, 16))
        };

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var sidebar = new Border { Background = new SolidColorBrush(Color.FromRgb(14, 17, 24)), Padding = new Thickness(16) };
        sidebar.Child = new TextBlock { Text = "PROJECT LUMA", Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.Bold };
        Grid.SetColumn(sidebar, 0);
        root.Children.Add(sidebar);

        var contentGrid = new Grid { Margin = new Thickness(16) };
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var searchBox = new TextBox { Height = 32, Margin = new Thickness(0, 0, 0, 12), Background = new SolidColorBrush(Color.FromRgb(8, 10, 14)), Foreground = Brushes.White };
        contentGrid.Children.Add(searchBox);

        var rowsControl = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        VirtualizingPanel.SetIsVirtualizing(rowsControl, true);
        VirtualizingPanel.SetVirtualizationMode(rowsControl, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(rowsControl, true);
        Grid.SetRow(rowsControl, 1);
        contentGrid.Children.Add(rowsControl);

        Grid.SetColumn(contentGrid, 1);
        root.Children.Add(contentGrid);
        win.Content = root;

        double coldStartupMs = 0;
        long idleWsBytes = 0;

        win.ContentRendered += (_, _) =>
        {
            coldStartupMs = Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
            using (var p = Process.GetCurrentProcess())
            {
                p.Refresh();
                idleWsBytes = p.WorkingSet64;
            }

            // Load 1,000 games with cached DecodePixelWidth=200 artwork
            var swArt = Stopwatch.StartNew();
            BitmapImage? cachedCover = null;
            if (File.Exists(sampleCover))
            {
                cachedCover = new BitmapImage();
                cachedCover.BeginInit();
                cachedCover.UriSource = new Uri(sampleCover, UriKind.Absolute);
                cachedCover.DecodePixelWidth = 200;
                cachedCover.CacheOption = BitmapCacheOption.OnLoad;
                cachedCover.EndInit();
                cachedCover.Freeze();
            }
            swArt.Stop();

            var allGames = new List<GameItem>(1000);
            for (int i = 0; i < 1000; i++)
            {
                allGames.Add(new GameItem
                {
                    Title = $"Sample Game Title #{i:D4}",
                    Status = i % 3 == 0 ? "Ready to Enhance" : "Enhanced",
                    Cover = cachedCover
                });
            }

            // Virtualized rows (5 columns x 200 rows)
            var swRealize = Stopwatch.StartNew();
            var rows = new ObservableCollection<GameRow>();
            for (int i = 0; i < allGames.Count; i += 5)
                rows.Add(new GameRow(allGames.Skip(i).Take(5).ToList()));
            rowsControl.ItemsSource = rows;
            win.UpdateLayout();
            swRealize.Stop();

            // Search benchmark over 1,000 games
            var swSearch = Stopwatch.StartNew();
            var filtered = allGames.Where(g => g.Title.Contains("042", StringComparison.OrdinalIgnoreCase)).ToList();
            swSearch.Stop();

            // Animation transition check
            var swAnim = Stopwatch.StartNew();
            var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
            contentGrid.BeginAnimation(UIElement.OpacityProperty, anim);
            swAnim.Stop();

            long loadedWsBytes;
            TimeSpan cpuTime;
            using (var p = Process.GetCurrentProcess())
            {
                p.Refresh();
                loadedWsBytes = p.WorkingSet64;
                cpuTime = p.TotalProcessorTime;
            }

            string report = $$"""
                {
                  "framework": "NET10_WPF_Virtualized",
                  "coldStartupMs": {{coldStartupMs:0.00}},
                  "idleWorkingSetMb": {{idleWsBytes / (1024.0 * 1024.0):0.00}},
                  "loaded1000WorkingSetMb": {{loadedWsBytes / (1024.0 * 1024.0):0.00}},
                  "artworkDecode200pxMs": {{swArt.Elapsed.TotalMilliseconds:0.00}},
                  "virtualize1000LayoutMs": {{swRealize.Elapsed.TotalMilliseconds:0.00}},
                  "search1000Ms": {{swSearch.Elapsed.TotalMilliseconds:0.00}},
                  "filteredMatches": {{filtered.Count}},
                  "cpuTimeMs": {{cpuTime.TotalMilliseconds:0.00}}
                }
                """;
            if (!string.IsNullOrWhiteSpace(outJson))
                File.WriteAllText(outJson, report);

            win.Close();
            Shutdown(0);
        };

        win.Show();
    }
}
