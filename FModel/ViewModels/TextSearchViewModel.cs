using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse_Conversion.UEFormat.Structs;
using FModel.Framework;
using FModel.Services;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;

namespace FModel.ViewModels;

public class TextSearchResult
{
    public GameFile File { get; set; }
    public string PropertyPath { get; set; }
    public string MatchedText { get; set; }
    public int LineNumber { get; set; }
}

public class TextSearchViewModel : ViewModel
{
    // Constants
    private static readonly HashSet<string> _excludedTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".uexp", ".ubulk", ".png", ".svg", ".psd", ".uptnl", ".bin", ".uplugin", ".upluginmanifest", ".uproject", ".res", ".dict", ".icu", ".tps", ".locmeta",
        ".ushaderbytecode", ".upipelinecache", ".hlsl", ".glsl"
    };

    private const int UI_UPDATE_BATCH = 100;

    // Properties
    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    private string _filterText = string.Empty;
    public string FilterText
    {
        get => _filterText;
        set => SetProperty(ref _filterText, value);
    }

    private bool _hasRegexEnabled;
    public bool HasRegexEnabled
    {
        get => _hasRegexEnabled;
        set => SetProperty(ref _hasRegexEnabled, value);
    }

    private bool _hasMatchCaseEnabled;
    public bool HasMatchCaseEnabled
    {
        get => _hasMatchCaseEnabled;
        set => SetProperty(ref _hasMatchCaseEnabled, value);
    }

    private bool _searchUasset = true;
    public bool SearchUasset
    {
        get => _searchUasset;
        set => SetProperty(ref _searchUasset, value);
    }

    private bool _searchUmap = true;
    public bool SearchUmap
    {
        get => _searchUmap;
        set => SetProperty(ref _searchUmap, value);
    }

    private bool _searchLocres = true;
    public bool SearchLocres
    {
        get => _searchLocres;
        set => SetProperty(ref _searchLocres, value);
    }

    private bool _searchTextFiles = true;
    public bool SearchTextFiles
    {
        get => _searchTextFiles;
        set => SetProperty(ref _searchTextFiles, value);
    }

    private bool _isSearching;
    public bool IsSearching
    {
        get => _isSearching;
        set => SetProperty(ref _isSearching, value);
    }

    private bool _isIndexing;
    public bool IsIndexing
    {
        get => _isIndexing;
        set => SetProperty(ref _isIndexing, value);
    }

    private bool _isIndexed;
    public bool IsIndexed
    {
        get => _isIndexed;
        set => SetProperty(ref _isIndexed, value);
    }

    private int _resultsCount;
    public int ResultsCount
    {
        get => _resultsCount;
        private set => SetProperty(ref _resultsCount, value);
    }

    private int _filesScanned;
    public int FilesScanned
    {
        get => _filesScanned;
        private set => SetProperty(ref _filesScanned, value);
    }

    private int _totalFiles;
    public int TotalFiles
    {
        get => _totalFiles;
        set => SetProperty(ref _totalFiles, value);
    }

    private string _searchTime = string.Empty;
    public string SearchTime
    {
        get => _searchTime;
        set => SetProperty(ref _searchTime, value);
    }

    private string _indexStatus = string.Empty;
    public string IndexStatus
    {
        get => _indexStatus;
        set => SetProperty(ref _indexStatus, value);
    }

    // Index selection
    public RangeObservableCollection<string> AvailableIndexes { get; } = new();
    private string _selectedIndexPath;
    public string SelectedIndexPath
    {
        get => _selectedIndexPath;
        set => SetProperty(ref _selectedIndexPath, value);
    }

    // Results
    public RangeObservableCollection<TextSearchResult> SearchResults { get; }
    public ListCollectionView SearchResultsView { get; }

    // Cached compiled regex for the result filter
    private Regex _cachedFilterRegex;
    private string _cachedFilterText;
    private bool _cachedFilterCase;

    // Constructor
    public TextSearchViewModel()
    {
        SearchResults = new RangeObservableCollection<TextSearchResult>();
        SearchResultsView = new ListCollectionView(SearchResults)
        {
            Filter = e => ItemFilter(e, FilterText.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)),
        };
        ResultsCount = 0;
    }

    // Filter
    public void RefreshFilter()
    {
        SearchResultsView.Refresh();
        ResultsCount = SearchResultsView.Count;
    }

    private bool ItemFilter(object item, IEnumerable<string> filters)
    {
        if (item is not TextSearchResult result)
            return true;

        if (!HasRegexEnabled)
        {
            var comp = HasMatchCaseEnabled ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            return filters.All(x => result.File.Path.Contains(x, comp));
        }

        var currentText = FilterText;
        var currentCase = HasMatchCaseEnabled;
        if (_cachedFilterRegex == null
            || _cachedFilterText != currentText
            || _cachedFilterCase != currentCase)
        {
            var o = RegexOptions.Compiled;
            if (!currentCase) o |= RegexOptions.IgnoreCase;
            _cachedFilterRegex = new Regex(currentText, o);
            _cachedFilterText = currentText;
            _cachedFilterCase = currentCase;
        }

        return _cachedFilterRegex.IsMatch(result.File.Path);
    }

    // Live Search
    public async Task SearchInFiles(IEnumerable<GameFile> files, CancellationToken cancellationToken)
    {
        IsSearching = true;
        var startTime = DateTime.Now;

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => SearchResults.Clear());

        int scannedRaw = 0;
        FilesScanned = 0;

        var filesList = files.ToList();
        TotalFiles = filesList.Count;

        try
        {
            var searchPattern = SearchText.Trim();
            if (string.IsNullOrWhiteSpace(searchPattern))
                return;

            var comparisonType = HasMatchCaseEnabled ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            Regex regex = null;
            if (HasRegexEnabled)
            {
                var opts = HasMatchCaseEnabled ? RegexOptions.None : RegexOptions.IgnoreCase;
                regex = new Regex(searchPattern, opts | RegexOptions.Compiled);
            }

            var provider = ApplicationService.ApplicationView.CUE4Parse.Provider;

            var pendingResults = new List<TextSearchResult>();
            var lockObj = new object();

            try
            {
                await Task.Run(() =>
                {
                    var parallelOptions = new ParallelOptions
                    {
                        CancellationToken = cancellationToken,
                        MaxDegreeOfParallelism = Environment.ProcessorCount
                    };

                    Parallel.ForEach(filesList, parallelOptions, file =>
                    {
                        if (cancellationToken.IsCancellationRequested) return;

                        try
                        {
                            var matches = SearchInFile(file, searchPattern, regex, comparisonType, provider);
                            if (matches is { Count: > 0 })
                            {
                                List<TextSearchResult> batch = null;

                                lock (lockObj)
                                {
                                    pendingResults.AddRange(matches);
                                    if (pendingResults.Count >= UI_UPDATE_BATCH)
                                    {
                                        batch = [.. pendingResults];
                                        pendingResults.Clear();
                                    }
                                }

                                if (batch != null)
                                {
                                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                                    {
                                        SearchResults.AddRange(batch);
                                        ResultsCount = SearchResultsView.Count;
                                    });
                                }
                            }
                        }
                        catch { }

                        var current = Interlocked.Increment(ref scannedRaw);
                        if (current % 100 == 0 || current == filesList.Count)
                        {
                            var captured = current;
                            System.Windows.Application.Current.Dispatcher.InvokeAsync(
                                () => FilesScanned = captured);
                        }
                    });
                }, cancellationToken);

                List<TextSearchResult> finalBatch = null;
                lock (lockObj)
                {
                    if (pendingResults.Count > 0)
                    {
                        finalBatch = [.. pendingResults];
                        pendingResults.Clear();
                    }
                }

                if (finalBatch != null)
                {
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        SearchResults.AddRange(finalBatch);
                        ResultsCount = SearchResultsView.Count;
                    });
                }

                await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                    () => FilesScanned = filesList.Count);
            }
            catch (OperationCanceledException) { }
            finally
            {
                IsSearching = false;
                SearchTime = FormatElapsed(DateTime.Now - startTime);
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                    () => ResultsCount = SearchResultsView.Count);
            }
        }
        catch
        {
            throw;
        }
    }

    // SQLite Index
    public static string GetIndexDirectory()
    {
        var dir = Path.Combine(Path.GetDirectoryName(Environment.GetCommandLineArgs()[0])!, "Text_Indexes");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string BuildIndexPath(string ueVersion, string gameName)
    {
        static string Safe(string s) =>
            string.Concat(s.Split(Path.GetInvalidFileNameChars())).Trim();

        var safeVer = Safe(ueVersion);
        var safeName = Safe(gameName);
        var date = DateTime.Now.ToString("yyyy-MM-dd");
        var fileName = $"{safeVer}_{safeName}_{date}.db";
        return Path.Combine(GetIndexDirectory(), fileName);
    }

    public static IEnumerable<string> GetAvailableIndexes()
    {
        var dir = GetIndexDirectory();
        return Directory.GetFiles(dir, "*.db")
            .OrderByDescending(File.GetLastWriteTime);
    }

    public void RefreshAvailableIndexes()
    {
        AvailableIndexes.Clear();
        foreach (var path in GetAvailableIndexes())
            AvailableIndexes.Add(path);

        if (string.IsNullOrEmpty(SelectedIndexPath) || !File.Exists(SelectedIndexPath))
            SelectedIndexPath = AvailableIndexes.FirstOrDefault();
    }

    public async Task BuildIndex(IEnumerable<GameFile> files, string ueVersion, string gameName, CancellationToken cancellationToken)
    {
        IsIndexing = true;
        IsIndexed = false;
        IndexStatus = "Building index...";

        var dbPath = BuildIndexPath(ueVersion, gameName);
        var filesList = files.ToList();
        TotalFiles = filesList.Count;

        int scannedRaw = 0;
        FilesScanned = 0;

        var provider = ApplicationService.ApplicationView.CUE4Parse.Provider;

        try
        {
            var tmpPath = dbPath + ".tmp";
            if (File.Exists(tmpPath)) File.Delete(tmpPath);

            await Task.Run(() =>
            {
                using var conn = new SqliteConnection($"Data Source={tmpPath}");
                conn.Open();

                using var setup = conn.CreateCommand();
                setup.CommandText = """
                    PRAGMA journal_mode = WAL;
                    PRAGMA synchronous  = NORMAL;

                    CREATE TABLE IF NOT EXISTS strings (
                        id           INTEGER PRIMARY KEY,
                        file_path    TEXT NOT NULL,
                        source       TEXT NOT NULL,
                        namespace    TEXT,
                        key          TEXT,
                        property     TEXT,
                        value        TEXT NOT NULL
                    );

                    CREATE INDEX IF NOT EXISTS idx_strings_source ON strings(source);
                    """;
                setup.ExecuteNonQuery();

                const int BATCH_SIZE = 500;
                var batch = new List<(string filePath, string source, string ns, string key, string prop, string value)>(BATCH_SIZE);

                void FlushBatch(SqliteConnection c)
                {
                    if (batch.Count == 0) return;
                    using var tx = c.BeginTransaction();
                    using var ins = c.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText = """
                        INSERT INTO strings (file_path, source, namespace, key, property, value)
                        VALUES ($fp, $src, $ns, $k, $prop, $val)
                        """;
                    var pFp = ins.Parameters.Add("$fp", SqliteType.Text);
                    var pSrc = ins.Parameters.Add("$src", SqliteType.Text);
                    var pNs = ins.Parameters.Add("$ns", SqliteType.Text);
                    var pK = ins.Parameters.Add("$k", SqliteType.Text);
                    var pProp = ins.Parameters.Add("$prop", SqliteType.Text);
                    var pVal = ins.Parameters.Add("$val", SqliteType.Text);

                    foreach (var (fp, src, ns, k, prop, val) in batch)
                    {
                        pFp.Value = fp;
                        pSrc.Value = src;
                        pNs.Value = (object)ns ?? DBNull.Value;
                        pK.Value = (object)k ?? DBNull.Value;
                        pProp.Value = (object)prop ?? DBNull.Value;
                        pVal.Value = val;
                        ins.ExecuteNonQuery();
                    }
                    tx.Commit();
                    batch.Clear();
                }

                foreach (var file in filesList)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        string source = null;
                        var entries = new List<(string ns, string key, string prop, string value)>();

                        if (file.Path.EndsWith(".locres", StringComparison.OrdinalIgnoreCase))
                        {
                            source = "locres";
                            ExtractLocres(file, entries);
                        }
                        else if (file.Path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
                              || file.Path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
                        {
                            source = file.Path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase) ? "umap" : "uasset";
                            ExtractUassetStrings(file, provider, entries);
                        }
                        else
                        {
                            var ext = Path.GetExtension(file.Path);
                            if (!_excludedTextExtensions.Contains(ext))
                            {
                                source = "text";
                                ExtractTextFile(file, entries);
                            }
                        }

                        if (source != null)
                        {
                            foreach (var (ns, key, prop, value) in entries)
                                batch.Add((file.Path, source, ns, key, prop, value));
                        }

                        if (batch.Count >= BATCH_SIZE)
                            FlushBatch(conn);
                    }
                    catch { }

                    var current = Interlocked.Increment(ref scannedRaw);
                    if (current % 200 == 0 || current == filesList.Count)
                    {
                        var captured = current;
                        System.Windows.Application.Current.Dispatcher.InvokeAsync(
                            () => FilesScanned = captured);
                    }
                }

                FlushBatch(conn);

                using var ckpt = conn.CreateCommand();
                ckpt.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                ckpt.ExecuteNonQuery();

            }, cancellationToken);

            SqliteConnection.ClearAllPools();

            if (File.Exists(dbPath)) File.Delete(dbPath);
            File.Move(tmpPath, dbPath);
            foreach (var ext in new[] { "-wal", "-shm" })
            {
                var f = tmpPath + ext;
                if (File.Exists(f)) File.Delete(f);
            }

            IsIndexed = true;
            IndexStatus = $"Index ready: {scannedRaw:N0} files scanned";

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                RefreshAvailableIndexes();
                SelectedIndexPath = dbPath;
            });
        }
        catch (OperationCanceledException)
        {
            IndexStatus = "Index build cancelled";
        }
        catch (Exception ex)
        {
            IndexStatus = $"Index error: {ex.Message}";
        }
        finally
        {
            IsIndexing = false;
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                () => FilesScanned = scannedRaw);
        }
    }

    public async Task SearchInIndex(CancellationToken cancellationToken)
    {
        var dbPath = SelectedIndexPath;
        if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
        {
            IndexStatus = "No index selected";
            return;
        }

        IsSearching = true;
        var startTime = DateTime.Now;

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => SearchResults.Clear());
        ResultsCount = 0;

        var searchPattern = SearchText.Trim();
        if (string.IsNullOrWhiteSpace(searchPattern))
        {
            IsSearching = false;
            return;
        }

        var provider = ApplicationService.ApplicationView.CUE4Parse.Provider;

        try
        {
            var allowedSources = new List<string>();
            if (SearchUasset) allowedSources.Add("uasset");
            if (SearchUmap) allowedSources.Add("umap");
            if (SearchLocres) allowedSources.Add("locres");
            if (SearchTextFiles) allowedSources.Add("text");

            if (allowedSources.Count == 0)
            {
                IsSearching = false;
                return;
            }

            var srcPlaceholders = string.Join(",", allowedSources.Select((_, i) => $"$src{i}"));

            var results = new List<TextSearchResult>();

            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
                conn.Open();

                using var cmd = conn.CreateCommand();

                if (HasRegexEnabled)
                {
                    cmd.CommandText = $"""
                        SELECT file_path, source, namespace, key, property, value
                        FROM strings
                        WHERE source IN ({srcPlaceholders})
                        """;
                    for (int i = 0; i < allowedSources.Count; i++)
                        cmd.Parameters.AddWithValue($"$src{i}", allowedSources[i]);
                    var regexOpts = HasMatchCaseEnabled ? RegexOptions.None : RegexOptions.IgnoreCase;
                    var rx = new Regex(searchPattern, regexOpts | RegexOptions.Compiled);

                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var value = reader.GetString(5);
                        if (!rx.IsMatch(value)) continue;
                        AddIndexResult(reader, provider, results);
                    }
                }
                else
                {
                    var srcFilter = string.Join(" OR ", allowedSources.Select((s, i) => $"s.source = $src{i}"));
                    var likePattern = "%" + searchPattern.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
                    cmd.CommandText = $"""
                        SELECT s.file_path, s.source, s.namespace, s.key, s.property, s.value
                        FROM strings s
                        WHERE s.value LIKE $pattern ESCAPE '\'
                          AND ({srcFilter})
                        """;
                    for (int i = 0; i < allowedSources.Count; i++)
                        cmd.Parameters.AddWithValue($"$src{i}", allowedSources[i]);
                    cmd.Parameters.AddWithValue("$pattern", likePattern);

                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (HasMatchCaseEnabled)
                        {
                            var value = reader.GetString(5);
                            if (!value.Contains(searchPattern, StringComparison.Ordinal)) continue;
                        }

                        AddIndexResult(reader, provider, results);
                    }
                }
            }, cancellationToken);

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                SearchResults.AddRange(results);
                ResultsCount = SearchResultsView.Count;
            });
        }
        catch (OperationCanceledException) { }
        finally
        {
            IsSearching = false;
            SearchTime = FormatElapsed(DateTime.Now - startTime);
        }
    }

    // Extraction helpers
    private static void ExtractLocres(GameFile file, List<(string ns, string key, string prop, string value)> out_)
    {
        try
        {
            var archive = file.CreateReader();
            var locres = new CUE4Parse.UE4.Localization.FTextLocalizationResource(archive);
            foreach (var nsEntry in locres.Entries)
            {
                var ns = nsEntry.Key.Str;
                foreach (var kv in nsEntry.Value)
                {
                    var localized = kv.Value.LocalizedString;
                    if (!string.IsNullOrEmpty(localized))
                        out_.Add((ns, kv.Key.Str, null, localized));
                }
            }
        }
        catch { }
    }

    private static void ExtractUassetStrings(GameFile file, CUE4Parse.FileProvider.Vfs.AbstractVfsFileProvider provider, List<(string ns, string key, string prop, string value)> out_)
    {
        try
        {
            var package = provider.LoadPackage(file);
            if (package == null) return;

            for (int i = 0; i < package.ExportMapLength; i++)
            {
                try
                {
                    var pointer = new FPackageIndex(package, i + 1).ResolvedObject;
                    if (pointer?.Object?.Value is UObject uobj)
                        WalkUObjectProperties(uobj, uobj.Name, out_);
                }
                catch { }
            }
        }
        catch { }
    }

    private static void WalkUObjectProperties(UObject obj, string basePath,
        List<(string ns, string key, string prop, string value)> out_, int depth = 0)
    {
        if (obj == null || depth > 8) return;

        foreach (var prop in obj.Properties)
        {
            if (prop?.Tag == null) continue;
            var propPath = $"{basePath}.{prop.Name.Text}";
            try { WalkTag(prop.Tag, propPath, out_, depth); }
            catch { }
        }
    }

    private static void WalkStructFallback(FStructFallback fallback, string basePath,
        List<(string ns, string key, string prop, string value)> out_, int depth)
    {
        foreach (var inner in fallback.Properties)
        {
            if (inner?.Tag == null) continue;
            var innerPath = $"{basePath}.{inner.Name.Text}";
            try { WalkTag(inner.Tag, innerPath, out_, depth); }
            catch { }
        }
    }

    private static void WalkTag(FPropertyTagType tag, string propPath,
        List<(string ns, string key, string prop, string value)> out_, int depth)
    {
        if (tag == null || depth > 8) return;

        switch (tag)
        {
            case FPropertyTagType<FText> textTag:
                {
                    var ftext = textTag.Value;
                    string str = ftext.HistoryType switch
                    {
                        ETextHistoryType.Base when ftext.TextHistory is FTextHistory.Base b =>
                            !string.IsNullOrWhiteSpace(b.LocalizedString) ? b.LocalizedString : b.SourceString,
                        ETextHistoryType.StringTableEntry when ftext.TextHistory is FTextHistory.StringTableEntry s =>
                            !string.IsNullOrWhiteSpace(s.LocalizedString) ? s.LocalizedString :
                            !string.IsNullOrWhiteSpace(s.SourceString) ? s.SourceString : s.Key,
                        _ => ftext.Text ?? ftext.ToString()
                    };
                    if (!string.IsNullOrWhiteSpace(str))
                        out_.Add((null, null, propPath, str));
                    break;
                }
            case FPropertyTagType<FString> strTag:
                {
                    var str = strTag.Value.Text;
                    if (!string.IsNullOrWhiteSpace(str))
                        out_.Add((null, null, propPath, str));
                    break;
                }
            case FPropertyTagType<FName> nameTag:
                {
                    var str = nameTag.Value.Text;
                    if (!string.IsNullOrWhiteSpace(str) && !str.StartsWith("None"))
                        out_.Add((null, null, propPath, str));
                    break;
                }
            case FPropertyTagType<UScriptArray> arrTag:
                {
                    var items = arrTag.Value.Properties;
                    for (int i = 0; i < items.Count; i++)
                        WalkTag(items[i], $"{propPath}[{i}]", out_, depth + 1);
                    break;
                }
            case FPropertyTagType<UScriptMap> mapTag:
                {
                    int i = 0;
                    foreach (var kv in mapTag.Value.Properties)
                    {
                        WalkTag(kv.Key, $"{propPath}[{i}].Key", out_, depth + 1);
                        if (kv.Value != null)
                            WalkTag(kv.Value, $"{propPath}[{i}].Value", out_, depth + 1);
                        i++;
                    }
                    break;
                }
            case FPropertyTagType<UScriptSet> setTag:
                {
                    var items = setTag.Value.Properties;
                    for (int i = 0; i < items.Count; i++)
                        WalkTag(items[i], $"{propPath}[{i}]", out_, depth + 1);
                    break;
                }
            case FPropertyTagType<FScriptStruct> structTag:
                {
                    switch (structTag.Value.StructType)
                    {
                        case UObject nested:
                            WalkUObjectProperties(nested, propPath, out_, depth + 1);
                            break;
                        case FStructFallback fallback:
                            WalkStructFallback(fallback, propPath, out_, depth + 1);
                            break;
                    }
                    break;
                }
            default:
                {
                    if (tag.GenericValue is UObject nestedObj)
                        WalkUObjectProperties(nestedObj, propPath, out_, depth + 1);
                    break;
                }
        }
    }

    private static void ExtractTextFile(GameFile file, List<(string ns, string key, string prop, string value)> out_)
    {
        try
        {
            var data = file.Read();
            if (data == null || data.Length == 0) return;
            var text = System.Text.Encoding.UTF8.GetString(data);
            if (string.IsNullOrWhiteSpace(text) || !IsLikelyText(text)) return;

            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (!string.IsNullOrEmpty(line))
                    out_.Add((null, null, $"Line {i + 1}", line));
            }
        }
        catch { }
    }

    // SearchInFile (live scan)
    private List<TextSearchResult> SearchInFile(GameFile file, string searchPattern, Regex regex, StringComparison comparison, CUE4Parse.FileProvider.Vfs.AbstractVfsFileProvider provider)
    {
        if (file.Path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) && !SearchUasset) return null;
        if (file.Path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase) && !SearchUmap) return null;
        if (file.Path.EndsWith(".locres", StringComparison.OrdinalIgnoreCase) && !SearchLocres) return null;

        var results = new List<TextSearchResult>();

        if ((file.Path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) && SearchUasset) ||
            (file.Path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase) && SearchUmap))
        {
            var strings = new List<(string ns, string key, string prop, string value)>();
            ExtractUassetStrings(file, provider, strings);
            foreach (var (_, _, prop, value) in strings)
            {
                if (!IsMatch(value, searchPattern, regex, comparison)) continue;
                results.Add(new TextSearchResult { File = file, PropertyPath = prop, MatchedText = TruncateText(value, 200) });
            }
            return results.Count > 0 ? results : null;
        }

        if (file.Path.EndsWith(".locres", StringComparison.OrdinalIgnoreCase) && SearchLocres)
        {
            var strings = new List<(string ns, string key, string prop, string value)>();
            ExtractLocres(file, strings);
            foreach (var (ns, key, _, value) in strings)
            {
                if (!IsMatch(value, searchPattern, regex, comparison)) continue;
                results.Add(new TextSearchResult { File = file, PropertyPath = $"{ns}.{key}", MatchedText = TruncateText(value, 200) });
            }
            return results.Count > 0 ? results : null;
        }

        if (SearchTextFiles
            && !file.Path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
            && !file.Path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)
            && !file.Path.EndsWith(".locres", StringComparison.OrdinalIgnoreCase))
        {
            var ext = Path.GetExtension(file.Path);
            if (_excludedTextExtensions.Contains(ext)) return null;

            var strings = new List<(string ns, string key, string prop, string value)>();
            ExtractTextFile(file, strings);
            foreach (var (_, _, prop, value) in strings)
            {
                if (!IsMatch(value, searchPattern, regex, comparison)) continue;
                results.Add(new TextSearchResult
                {
                    File = file,
                    PropertyPath = prop,
                    MatchedText = TruncateText(value, 200),
                    LineNumber = int.TryParse(prop.Replace("Line ", ""), out var ln) ? ln : 0
                });
            }
            return results.Count > 0 ? results : null;
        }

        return null;
    }

    // Utilities
    private static void AddIndexResult(SqliteDataReader reader, CUE4Parse.FileProvider.Vfs.AbstractVfsFileProvider provider, List<TextSearchResult> results)
    {
        var filePath = reader.GetString(0);
        var prop = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
        var value = reader.GetString(5);

        provider.Files.TryGetValue(filePath, out var gameFile);
        results.Add(new TextSearchResult
        {
            File = gameFile,
            PropertyPath = prop,
            MatchedText = TruncateText(value, 200),
        });
    }

    private static bool IsMatch(string text, string searchPattern, Regex regex, StringComparison comparison)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return regex != null ? regex.IsMatch(text) : text.Contains(searchPattern, comparison);
    }

    private static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length > maxLength ? text[..maxLength] + "…" : text;
    }

    private static bool IsLikelyText(string text)
    {
        if (text.Length == 0) return false;
        int checkLength = Math.Min(text.Length, 1000);
        int printable = 0;
        for (int i = 0; i < checkLength; i++)
        {
            char c = text[i];
            if (!char.IsControl(c) || c == '\n' || c == '\r' || c == '\t')
                printable++;
        }
        return (printable / (double)checkLength) > 0.7;
    }

    private static string FormatElapsed(TimeSpan t)
    {
        if (t.TotalMinutes >= 1)
            return $"{(int)t.TotalMinutes} m {t.Seconds:D2} s {t.Milliseconds:D3} ms";
        if (t.TotalSeconds >= 1)
            return $"{t.Seconds:D2} s {t.Milliseconds:D3} ms";
        return $"{t.Milliseconds} ms";
    }
}