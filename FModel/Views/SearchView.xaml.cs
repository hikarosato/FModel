using CUE4Parse.FileProvider.Objects;
using FModel.Services;
using FModel.Settings;
using FModel.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FModel.Views;

public enum ESearchViewTab
{
    SearchView,
    RefView,
    TextSearchView
}

public partial class SearchView
{
    private ThreadWorkerViewModel _threadWorkerView => ApplicationService.ThreadWorkerView;
    private ApplicationViewModel _applicationView => ApplicationService.ApplicationView;
    private SearchViewModel _searchViewModel => _applicationView.CUE4Parse.SearchVm;
    private SearchViewModel _refViewModel => _applicationView.CUE4Parse.RefVm;
    private TextSearchViewModel _textSearchViewModel => _applicationView.CUE4Parse.TextSearchVm;

    private ESearchViewTab _currentTab = ESearchViewTab.SearchView;
    private CancellationTokenSource _searchCancellation;
    private CancellationTokenSource _indexCancellation;

    public SearchView()
    {
        DataContext = new
        {
            mainApplication = _applicationView,
            SearchTab = _searchViewModel,
            RefTab = _refViewModel,
            TextSearchTab = _textSearchViewModel,
        };
        InitializeComponent();

        _textSearchViewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(_textSearchViewModel.IsSearching)
             || e.PropertyName == nameof(_textSearchViewModel.IsIndexing))
            {
                UpdateTextSearchButtonIcon();
            }
        };

        Activate();
        _textSearchViewModel.RefreshAvailableIndexes();
        SearchTextBox.Focus();
        SearchTextBox.SelectAll();
    }

    public void FocusTab(ESearchViewTab view)
    {
        _currentTab = view;
        SearchTabControl.SelectedIndex = view switch
        {
            ESearchViewTab.SearchView => 0,
            ESearchViewTab.RefView => 1,
            ESearchViewTab.TextSearchView => 2,
            _ => SearchTabControl.SelectedIndex
        };
        WindowState = WindowState.Normal;
        CurrentTextBox?.Focus();
        CurrentTextBox?.SelectAll();
    }

    public void ChangeCollection(ESearchViewTab view, IEnumerable<GameFile> files, GameFile refFile)
    {
        var vm = view switch
        {
            ESearchViewTab.SearchView => _searchViewModel,
            ESearchViewTab.RefView => _refViewModel,
            _ => null
        };
        vm?.ChangeCollection(files, refFile);
    }

    private async void OnFindRefs(object sender, RoutedEventArgs e)
    {
        if (CurrentListView?.SelectedItem is not GameFile entry)
            return;

        await _threadWorkerView.Begin(_ => _applicationView.CUE4Parse.FindReferences(entry));
    }

    private void OnTabItemChange(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource is not TabControl tabControl)
            return;

        _currentTab = tabControl.SelectedIndex switch
        {
            0 => ESearchViewTab.SearchView,
            1 => ESearchViewTab.RefView,
            2 => ESearchViewTab.TextSearchView,
            _ => _currentTab
        };
        CurrentTextBox?.Focus();
        CurrentTextBox?.SelectAll();
    }

    private void OnDeleteSearchClick(object sender, RoutedEventArgs e)
    {
        var viewModel = CurrentViewModel;
        if (viewModel == null) return;
        viewModel.FilterText = string.Empty;
        viewModel.RefreshFilter();
    }

    // Text Search
    private async void OnTextSearchClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_textSearchViewModel.SearchText)) return;
        if (_textSearchViewModel.IsSearching) return;

        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();

        _textSearchViewModel.SearchTime = string.Empty;

        try
        {
            // If an index exists, search it; otherwise fall back to live scan
            if (!string.IsNullOrEmpty(_textSearchViewModel.SelectedIndexPath) && System.IO.File.Exists(_textSearchViewModel.SelectedIndexPath))
            {
                await _threadWorkerView.Begin(async cancellationToken =>
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken, _searchCancellation.Token);
                    await _textSearchViewModel.SearchInIndex(linked.Token);
                });
            }
            else
            {
                var allFiles = _applicationView.CUE4Parse.Provider.Files.Values.ToList();
                await _threadWorkerView.Begin(async cancellationToken =>
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken, _searchCancellation.Token);
                    await _textSearchViewModel.SearchInFiles(allFiles, linked.Token);
                });
            }
        }
        catch (OperationCanceledException) { }
    }

    private void OnTextSearchClearOrStopClick(object sender, RoutedEventArgs e)
    {
        if (_textSearchViewModel.IsSearching || _textSearchViewModel.IsIndexing)
        {
            _searchCancellation?.Cancel();
            _indexCancellation?.Cancel();
        }
        else
        {
            _textSearchViewModel.SearchText = string.Empty;
        }
    }

    // Index Build
    private async void OnBuildIndexClick(object sender, RoutedEventArgs e)
    {
        if (_textSearchViewModel.IsIndexing) return;

        _indexCancellation?.Cancel();
        _indexCancellation?.Dispose();
        _indexCancellation = new CancellationTokenSource();

        var allFiles = _applicationView.CUE4Parse.Provider.Files.Values.ToList();
        var gameName = _applicationView.GameDisplayName;
        try
        {
            await _threadWorkerView.Begin(async cancellationToken =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, _indexCancellation.Token);
                var ueVersion = UserSettings.Default.CurrentDir.UeVersion.ToString();
                await _textSearchViewModel.BuildIndex(allFiles, ueVersion, gameName, linked.Token);
            });
        }
        catch (OperationCanceledException) { }
    }

    // Icon helpers
    private void UpdateTextSearchButtonIcon()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(UpdateTextSearchButtonIcon);
            return;
        }

        var button = FindName("TextSearchClearStopButton") as Button;
        if (button?.Content is Grid grid && grid.Children.Count >= 2)
        {
            var clearIcon = grid.Children[0] as Viewbox;
            var stopIcon = grid.Children[1] as Viewbox;
            bool busy = _textSearchViewModel.IsSearching || _textSearchViewModel.IsIndexing;

            clearIcon!.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
            stopIcon!.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // Result navigation
    private async void OnTextResultDoubleClick(object sender, RoutedEventArgs e)
    {
        if (TextSearchListView?.SelectedItem is not TextSearchResult result || result.File == null)
            return;

        await NavigateToAssetAndSelect(result.File);
    }

    private void OnCopyMatchedText(object sender, RoutedEventArgs e)
    {
        if (TextSearchListView?.SelectedItem is not TextSearchResult result)
            return;

        try
        {
            Clipboard.SetText(result.MatchedText);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to copy text: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private SearchViewModel CurrentViewModel => _currentTab switch
    {
        ESearchViewTab.SearchView => _applicationView.CUE4Parse.SearchVm,
        ESearchViewTab.RefView => _applicationView.CUE4Parse.RefVm,
        _ => null
    };

    private ListView CurrentListView => _currentTab switch
    {
        ESearchViewTab.SearchView => SearchListView,
        ESearchViewTab.RefView => RefListView,
        ESearchViewTab.TextSearchView => TextSearchListView,
        _ => null
    };

    private TextBox CurrentTextBox => _currentTab switch
    {
        ESearchViewTab.SearchView => SearchTextBox,
        ESearchViewTab.RefView => RefSearchTextBox,
        ESearchViewTab.TextSearchView => TextSearchBox,
        _ => null
    };

    private async void OnSearchSortClick(object sender, RoutedEventArgs e)
    {
        await CurrentViewModel?.CycleSortSizeMode();
    }

    private async void OnAssetDoubleClick(object sender, RoutedEventArgs e)
    {
        if (CurrentListView?.SelectedItem is not GameFile entry)
            return;

        await NavigateToAssetAndSelect(entry);
    }

    private async void OnGoToRefPackage(object sender, RoutedEventArgs e)
    {
        if (_refViewModel.RefFile is not GameFile entry)
            return;

        await NavigateToAssetAndSelect(entry);
    }

    private async Task NavigateToAssetAndSelect(GameFile entry)
    {
        WindowState = WindowState.Minimized;
        MainWindow.YesWeCats.AssetsListName.ItemsSource = null;
        var folder = _applicationView.CustomDirectories.GoToCommand.JumpTo(entry.Directory);
        if (folder == null)
            return;

        MainWindow.YesWeCats.Activate();

        do
        { await Task.Delay(100); } while (MainWindow.YesWeCats.AssetsListName.Items.Count < folder.AssetsList.Assets.Count);

        while (!folder.IsSelected || MainWindow.YesWeCats.AssetsFolderName.SelectedItem != folder)
            await Task.Delay(50); // stops assets tab from opening too early

        ApplicationService.ApplicationView.SelectedLeftTabIndex = 2; // assets tab
        do
        {
            await Task.Delay(100);
            var vm = MainWindow.YesWeCats.AssetsListName.Items
                .OfType<GameFileViewModel>()
                .FirstOrDefault(x => x.Asset == entry);
            MainWindow.YesWeCats.AssetsListName.SelectedItem = vm;
            MainWindow.YesWeCats.AssetsListName.ScrollIntoView(vm);
        } while (MainWindow.YesWeCats.AssetsListName.SelectedItem == null);
    }

    private async void OnAssetExtract(object sender, RoutedEventArgs e)
    {
        if (CurrentListView?.SelectedItem is not GameFile entry)
            return;

        WindowState = WindowState.Minimized;
        await _threadWorkerView.Begin(cancellationToken => _applicationView.CUE4Parse.Extract(cancellationToken, entry, true));

        MainWindow.YesWeCats.Activate();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        if (_currentTab == ESearchViewTab.TextSearchView)
            OnTextSearchClick(sender, e);
        else
            CurrentViewModel?.RefreshFilter();
    }

    private void OnStateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Normal)
        {
            Activate();
            CurrentTextBox?.Focus();
            CurrentTextBox?.SelectAll();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _indexCancellation?.Cancel();
        _indexCancellation?.Dispose();
        base.OnClosed(e);
    }
}