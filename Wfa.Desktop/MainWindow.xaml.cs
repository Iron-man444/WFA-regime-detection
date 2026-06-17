using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Win32;
using ScottPlot;
using ScottPlot.WPF;
using Wfa.Desktop.Models;
using Wfa.Desktop.Services;

namespace Wfa.Desktop;

public partial class MainWindow : Window
{
    private static readonly string StrategiesFolder =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Strategies");

    private static readonly string UserSettingsPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "user_settings.json");

    private UserSettings _userSettings = new();
    private System.Threading.CancellationTokenSource? _analysisCts;
    private bool _isCancelled = false;
    private bool _forceUseSummaryReport = false;

    // Last received analysis (for UI interactions like Monte Carlo on current trades)
    private AnalysisResultModel? _currentAnalysis;

    // Fullscreen helper state
    private Border? _originalParentBorder;
    private WpfPlot? _fullscreenPlot;
    private Window? _fullscreenWindow;

    // Helper DTO for DataGrid binding (anonymous types sometimes don't bind reliably in WPF)
    private sealed class TradeRow
    {
        public int TradeId { get; set; }
        public string Direction { get; set; } = string.Empty;
        public string EntryTime { get; set; } = "-";
        public string ExitTime { get; set; } = "-";
        public string Pnl { get; set; } = "-";
        public string ReturnPercent { get; set; } = "-";
    }

    public MainWindow()
    {
        InitializeComponent();

        LoadStrategiesFromDisk();
        // Load remote strategies from the Python backend when the window finishes loading
        this.Loaded += async (s, e) => await LoadStrategiesAsync();
        ApplyDateRangePreset();
        ConfigureEmptyChart();
        LoadUserSettings();
        ApplyExecutionModeUi();
        Closing += MainWindow_Closing;
        // Key handling for fullscreen toggle (F11) and Escape to exit
        this.KeyDown += MainWindow_KeyDown;
    }

    /// <summary>
    /// Scans the local <c>Strategies</c> folder for <c>*.py</c> files (UI listing).
    /// The API loads parameters from the Python backend <c>strategies/</c> folder by filename.
    /// </summary>
    private void LoadStrategiesFromDisk()
    {
        var strategies = new List<string>();

        try
        {
            // Eğer Strategies klasörü yoksa, programın çalıştığı yere otomatik oluştur.
            if (!Directory.Exists(StrategiesFolder))
            {
                Directory.CreateDirectory(StrategiesFolder);
            }

            // Sadece .py uzantılı gerçek Python stratejilerini bul.
            strategies.AddRange(
                Directory.EnumerateFiles(StrategiesFolder, "*.py", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileName)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Cast<string>());
        }
        catch
        {
            // Dosya okuma hatası olursa görmezden gel.
        }

        // Eğer klasör tamamen boşsa, kullanıcıya uyarı göster.
        if (strategies.Count == 0)
        {
            strategies.Add("Klasör boş. Lütfen .py stratejisi ekleyin.");
        }

        ExpertComboBox.ItemsSource = strategies;
        if (strategies.Count > 0)
        {
            ExpertComboBox.SelectedIndex = 0;
        }
    }

    /// <summary>
    /// Loads available strategies from the Python backend (<c>/api/v1/strategies</c>) and updates the combo box.
    /// Falls back to the on-disk list if the API is unavailable.
    /// </summary>
    private async System.Threading.Tasks.Task LoadStrategiesAsync()
    {
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri("http://localhost:8000/") };
            using var response = await client.GetAsync("api/v1/strategies").ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var strategies = await JsonSerializer.DeserializeAsync<List<string>>(stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            }).ConfigureAwait(false) ?? new List<string>();

            // Update UI on Dispatcher thread
            Dispatcher.Invoke(() =>
            {
                ExpertComboBox.ItemsSource = strategies;
                if (strategies.Count > 0)
                {
                    ExpertComboBox.SelectedIndex = 0;
                }
            });
        }
        catch
        {
            // Ignore API errors; keep disk-based strategies already loaded.
        }
    }

    private async void RefreshStrategiesButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadStrategiesAsync();
    }

    private async void ExpertComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ExpertComboBox.SelectedItem is not string strategyFileName)
        {
            InputsGrid.ItemsSource = null;
            return;
        }

        // Immediately clear any existing UI-bound collection so the DataGrid does not retain stale rows.
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (InputsGrid.ItemsSource is System.Collections.IList list)
            {
                try { list.Clear(); } catch { }
            }
            else
            {
                InputsGrid.ItemsSource = null;
            }
        });

        ExpertComboBox.IsEnabled = false;

        try
        {
            // Ensure the backend receives filenames with .py extension
            var strategyFileForApi = strategyFileName.EndsWith(".py", StringComparison.OrdinalIgnoreCase)
                ? strategyFileName
                : strategyFileName + ".py";

            // Prepare ObservableCollection for UI binding and clear previous items (on UI thread)
            System.Collections.ObjectModel.ObservableCollection<StrategyInputRow> uiCollection = null!;
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (InputsGrid.ItemsSource is System.Collections.ObjectModel.ObservableCollection<StrategyInputRow> existing)
                {
                    existing.Clear();
                    uiCollection = existing;
                }
                else
                {
                    uiCollection = new System.Collections.ObjectModel.ObservableCollection<StrategyInputRow>();
                    InputsGrid.ItemsSource = uiCollection;
                }
            });

            // Fetch raw JSON from backend
            using var client = new System.Net.Http.HttpClient { BaseAddress = new Uri("http://localhost:8000/") };
            var encoded = Uri.EscapeDataString(strategyFileForApi);
            using var response = await client.GetAsync($"api/v1/strategy/parameters/{encoded}").ConfigureAwait(true);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

            if (string.IsNullOrWhiteSpace(json))
            {
                // nothing returned, leave empty collection
                return;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
            {
                // unexpected shape - bail out
                return;
            }

            foreach (var el in root.EnumerateArray())
            {
                // Map snake_case keys to StrategyInputRow fields safely
                string variable = string.Empty;
                if (el.TryGetProperty("variable", out var v) && v.ValueKind == JsonValueKind.String)
                    variable = v.GetString() ?? string.Empty;

                JsonElement defaultJe = default;
                if (!el.TryGetProperty("default_value", out defaultJe))
                    el.TryGetProperty("defaultValue", out defaultJe);

                el.TryGetProperty("start", out var startJe);
                el.TryGetProperty("step", out var stepJe);
                el.TryGetProperty("stop", out var stopJe);

                static string Format(JsonElement je)
                {
                    return je.ValueKind switch
                    {
                        JsonValueKind.Undefined or JsonValueKind.Null => string.Empty,
                        JsonValueKind.String => je.GetString() ?? string.Empty,
                        JsonValueKind.Number => je.TryGetInt64(out var i) ? i.ToString(System.Globalization.CultureInfo.InvariantCulture) : je.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        _ => je.GetRawText(),
                    };
                }

                var row = new StrategyInputRow
                {
                    Variable = variable,
                    Value = Format(defaultJe),
                    Start = Format(startJe),
                    Step = Format(stepJe),
                    Stop = Format(stopJe),
                    Optimize = true,
                };

                // Add on UI thread so the DataGrid updates immediately
                Application.Current.Dispatcher.Invoke(() => uiCollection.Add(row));
            }

        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            MessageBox.Show(this, ex.Message, "Strategy parameters", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Strategy parameters", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ExpertComboBox.IsEnabled = true;
        }
    }

    private void DateRangeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyDateRangePreset();
    }

    private void CustomDate_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (ReadComboText(DateRangeCombo) == "Custom")
        {
            return;
        }

        DateRangeCombo.SelectedIndex = 2; // Custom
    }

    private void ApplyDateRangePreset()
    {
        if (StartDatePicker == null || EndDatePicker == null)
            return;
        var preset = ReadComboText(DateRangeCombo);
        var today = DateTime.Today;

        switch (preset)
        {
            case "Last Month":
                StartDatePicker.SelectedDate = today.AddMonths(-1);
                EndDatePicker.SelectedDate = today;
                StartDatePicker.IsEnabled = false;
                EndDatePicker.IsEnabled = false;
                break;
            case "Last Year":
                StartDatePicker.SelectedDate = today.AddYears(-1);
                EndDatePicker.SelectedDate = today;
                StartDatePicker.IsEnabled = false;
                EndDatePicker.IsEnabled = false;
                break;
            default: // Custom
                StartDatePicker.IsEnabled = true;
                EndDatePicker.IsEnabled = true;
                StartDatePicker.SelectedDate ??= today.AddMonths(-1);
                EndDatePicker.SelectedDate ??= today;
                break;
        }
    }


    private void LoadUserSettings()
    {
        try
        {
            if (!File.Exists(UserSettingsPath))
            {
                return;
            }

            var json = File.ReadAllText(UserSettingsPath);
            var settings = JsonSerializer.Deserialize<UserSettings>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            }) ?? new UserSettings();

            _userSettings = settings;

            if (!string.IsNullOrWhiteSpace(settings.SelectedStrategy) && ExpertComboBox.Items.Contains(settings.SelectedStrategy))
            {
                ExpertComboBox.SelectedItem = settings.SelectedStrategy;
            }
            else if (ExpertComboBox.Items.Count > 0)
            {
                ExpertComboBox.SelectedIndex = 0;
            }

            DepositTextBox.Text = settings.Deposit;
            CurrencyTextBox.Text = settings.Currency;
            LeverageCombo.Text = settings.Leverage;
            WindowCountTextBox.Text = settings.WindowCount.ToString(CultureInfo.InvariantCulture);
            LatencyCombo.Text = settings.LatencyMode;
            ModellingCombo.Text = settings.ModellingType;
            DateRangeCombo.Text = settings.DateRangePreset;

            if (!string.IsNullOrWhiteSpace(settings.OptimizationMode))
            {
                SelectOptimizationMode(settings.OptimizationMode);
            }

            if (!string.IsNullOrWhiteSpace(settings.FitnessCriterion))
            {
                SelectFitnessCriterion(settings.FitnessCriterion);
            }

            if (settings.DateRangePreset == "Custom")
            {
                ApplyDateRangePreset();
                StartDatePicker.SelectedDate = settings.StartDate;
                EndDatePicker.SelectedDate = settings.EndDate;
            }
            else
            {
                ApplyDateRangePreset();
            }

            SymbolCombo.Text = settings.Symbol;
            TimeframeCombo.Text = settings.Timeframe;
            DataSourceTabs.SelectedIndex = settings.DataSourceTabIndex;
            CsvPathTextBox.Text = settings.CsvPath;
            Mt5ReportPathTextBox.Text = settings.Mt5ReportPath;

            if (!string.IsNullOrWhiteSpace(settings.ExecutionMode))
            {
                SelectExecutionMode(settings.ExecutionMode);
            }

            ApplyExecutionModeUi();

            if (!string.IsNullOrWhiteSpace(settings.AssetClass))
            {
                var assetClassItem = AssetClassCombo.Items.Cast<ComboBoxItem>()
                    .FirstOrDefault(i => (i.Tag as string)?.Equals(settings.AssetClass, StringComparison.OrdinalIgnoreCase) == true);
                if (assetClassItem != null)
                {
                    AssetClassCombo.SelectedItem = assetClassItem;
                }
            }

        }
        catch
        {
            // Ignore settings load failures and continue with defaults.
        }
    }

    private void SaveUserSettings()
    {
        try
        {
            var settings = new UserSettings
            {
                SelectedStrategy = ExpertComboBox.SelectedItem as string ?? string.Empty,
                Deposit = DepositTextBox.Text ?? string.Empty,
                Currency = CurrencyTextBox.Text ?? string.Empty,
                Leverage = ReadComboText(LeverageCombo),
                WindowCount = int.TryParse(WindowCountTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var windows) ? windows : 0,
                
                LatencyMode = ReadComboText(LatencyCombo),
                ModellingType = ReadComboText(ModellingCombo),
                DateRangePreset = ReadComboText(DateRangeCombo),
                StartDate = StartDatePicker.SelectedDate,
                EndDate = EndDatePicker.SelectedDate,
                Symbol = SymbolCombo.Text ?? string.Empty,
                Timeframe = ReadComboText(TimeframeCombo),
                DataSourceTabIndex = DataSourceTabs.SelectedIndex,
                CsvPath = CsvPathTextBox.Text ?? string.Empty,
                AssetClass = ReadAssetClassTag(),
                ExecutionMode = ReadExecutionMode(),
                OptimizationMode = ReadOptimizationMode(),
                FitnessCriterion = ReadComboTagOrContent(FitnessCriterionComboBox),
                Mt5ReportPath = Mt5ReportPathTextBox.Text ?? string.Empty,
            };

            if (InputsGrid.ItemsSource is IEnumerable rawRows && ExpertComboBox.SelectedItem is string strategy && !string.IsNullOrWhiteSpace(strategy))
            {
                var typedRows = rawRows.OfType<StrategyInputRow>().ToList();
                if (typedRows.Any())
                {
                    settings.StrategyInputs[strategy] = typedRows;
                }
            }

            foreach (var kvp in _userSettings.StrategyInputs)
            {
                if (!settings.StrategyInputs.ContainsKey(kvp.Key))
                {
                    settings.StrategyInputs[kvp.Key] = kvp.Value;
                }
            }

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            File.WriteAllText(UserSettingsPath, json);
            _userSettings = settings;
        }
        catch
        {
            // Ignore settings save failures.
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        SaveUserSettings();
    }

    private static IEnumerable<StrategyInputRow> GetCurrentStrategyInputRows(DataGrid grid)
    {
        if (grid.ItemsSource is IEnumerable<StrategyInputRow> typed)
        {
            return typed;
        }

        if (grid.ItemsSource is IEnumerable raw)
        {
            return raw.OfType<StrategyInputRow>();
        }

        return Enumerable.Empty<StrategyInputRow>();
    }

    private void BrowseCsv_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dlg.ShowDialog(this) == true)
        {
            CsvPathTextBox.Text = dlg.FileName;
            DataSourceTabs.SelectedIndex = 1;
        }
    }

    private void BrowseMt5Report_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "MT5 reports (*.xml;*.htm;*.html;*.csv)|*.xml;*.htm;*.html;*.csv|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dlg.ShowDialog(this) == true)
        {
            Mt5ReportPathTextBox.Text = dlg.FileName;
        }
    }

    private async void DownloadHistoricalDataButton_Click(object sender, RoutedEventArgs e)
    {
        DownloadHistoricalDataButton.IsEnabled = false;
        var prevCursor = Mouse.OverrideCursor;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var assetText = AssetClassCombo.Text ?? string.Empty;
            string assetClass;
            if (assetText.IndexOf("Crypto", StringComparison.OrdinalIgnoreCase) >= 0) assetClass = "Crypto";
            else if (assetText.IndexOf("MT5", StringComparison.OrdinalIgnoreCase) >= 0) assetClass = "MT5";
            else assetClass = "Stocks";

            var symbol = (SymbolCombo.Text ?? string.Empty).Trim();
            var timeframe = MapTimeframeToApi(ReadComboText(TimeframeCombo));
            var start = StartDatePicker.SelectedDate;
            var end = EndDatePicker.SelectedDate;

            if (string.IsNullOrWhiteSpace(symbol) || start == null || end == null)
            {
                MessageBox.Show(this, "Please provide Symbol, Start date and End date in the settings above.", "Invalid input", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var payload = new
            {
                asset_class = assetClass,
                symbol = symbol,
                timeframe = timeframe,
                start_date = start.Value.ToString("yyyy-MM-dd"),
                end_date = end.Value.ToString("yyyy-MM-dd")
            };

            using var client = new HttpClient();
            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var resp = await client.PostAsync("http://127.0.0.1:8000/download-data", content).ConfigureAwait(true);
            var respBody = await resp.Content.ReadAsStringAsync().ConfigureAwait(true);

            if (!resp.IsSuccessStatusCode)
            {
                try
                {
                    using var doc = JsonDocument.Parse(respBody);
                    if (doc.RootElement.TryGetProperty("detail", out var det))
                    {
                        MessageBox.Show(this, det.GetString() ?? respBody, "Download failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                    else if (doc.RootElement.TryGetProperty("error", out var err))
                    {
                        MessageBox.Show(this, err.GetString() ?? respBody, "Download failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                    else
                    {
                        MessageBox.Show(this, respBody, "Download failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                catch
                {
                    MessageBox.Show(this, $"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}", "Download failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(respBody);
                if (doc.RootElement.TryGetProperty("file_path", out var fp))
                {
                    var filePath = fp.GetString() ?? string.Empty;
                    Dispatcher.Invoke(() =>
                    {
                        CsvPathTextBox.Text = filePath;
                        DataSourceTabs.SelectedIndex = 1; // switch to Local CSV tab
                    });

                    MessageBox.Show(this, $"Downloaded to {filePath}", "Download succeeded", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(this, "Download succeeded but server did not return file_path.", "Download", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not parse response: {ex.Message}", "Download failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Download failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Mouse.OverrideCursor = prevCursor;
            DownloadHistoricalDataButton.IsEnabled = true;
        }
    }

    private void ExecutionModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyExecutionModeUi();
    }

    private void ApplyExecutionModeUi()
    {
        if (ExecutionModeComboBox == null)
        {
            return;
        }

        var mode = ReadExecutionMode();
        var isSingle = mode == "SingleBacktest";
        var isMt5 = mode == "MT5Report";

        if (Mt5ReportPanel != null)
        {
            Mt5ReportPanel.Visibility = isMt5 ? Visibility.Visible : Visibility.Collapsed;
        }

        if (InputsTab != null)
        {
            InputsTab.Visibility = isMt5 ? Visibility.Collapsed : Visibility.Visible;
        }

        var optimizationVisibility = isSingle ? Visibility.Collapsed : Visibility.Visible;
        if (InputsStartColumn != null)
        {
            InputsStartColumn.Visibility = optimizationVisibility;
        }

        if (InputsStepColumn != null)
        {
            InputsStepColumn.Visibility = optimizationVisibility;
        }

        if (InputsStopColumn != null)
        {
            InputsStopColumn.Visibility = optimizationVisibility;
        }

        if (InputsOptimizeColumn != null)
        {
            InputsOptimizeColumn.Visibility = optimizationVisibility;
        }

        if (StartBacktestButton != null)
        {
            StartBacktestButton.Content = mode switch
            {
                "SingleBacktest" => "Run Single Backtest",
                "MonteCarlo" => "Run Monte Carlo Simulation",
                "MT5Report" => "Parse MT5 Report",
                _ => "Start WFA Optimization",
            };
        }
    }

    private string ReadExecutionMode()
    {
        if (ExecutionModeComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            return tag;
        }

        return "WFAOptimization";
    }

    private void SelectExecutionMode(string mode)
    {
        foreach (var obj in ExecutionModeComboBox.Items)
        {
            if (obj is ComboBoxItem item && string.Equals(item.Tag as string, mode, StringComparison.Ordinal))
            {
                ExecutionModeComboBox.SelectedItem = item;
                return;
            }
        }
    }

    private void SelectOptimizationMode(string mode)
    {
        foreach (var obj in OptimizationModeComboBox.Items)
        {
            if (obj is ComboBoxItem item && string.Equals(item.Tag as string, mode, StringComparison.Ordinal))
            {
                OptimizationModeComboBox.SelectedItem = item;
                return;
            }
        }
    }

    private string ReadOptimizationMode()
    {
        if (OptimizationModeComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            return tag;
        }

        return "Fast";
    }

    private void SelectFitnessCriterion(string crit)
    {
        foreach (var obj in FitnessCriterionComboBox.Items)
        {
            if (obj is ComboBoxItem item && string.Equals(item.Content as string, crit, StringComparison.Ordinal))
            {
                FitnessCriterionComboBox.SelectedItem = item;
                return;
            }
        }
    }

    private void MainWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            var plot = GetPlotUnderMouse() ?? EquityChart;
            if (plot != null)
                EnterFullscreen(plot);
        }
        else if (e.Key == Key.Escape)
        {
            ExitFullscreen();
        }
    }

    private void DeepAnalysisEnlargeButton_Click(object sender, RoutedEventArgs e)
    {
        var plot = GetPlotUnderMouse() ?? EquityChart;
        if (plot != null)
            EnterFullscreen(plot);
    }

    private WpfPlot? GetPlotUnderMouse()
    {
        var depObj = Mouse.DirectlyOver as DependencyObject;
        while (depObj != null)
        {
            if (depObj is WpfPlot wp)
                return wp;
            depObj = VisualTreeHelper.GetParent(depObj);
        }

        return null;
    }

    private void EnterFullscreen(WpfPlot plot)
    {
        if (plot == null || _fullscreenWindow != null)
            return;

        // locate parent Border to restore later
        var parent = VisualTreeHelper.GetParent(plot) as Border;
        if (parent == null)
            return;

        _originalParentBorder = parent;
        parent.Child = null;
        _fullscreenPlot = plot;

        _fullscreenWindow = new Window
        {
            WindowStyle = WindowStyle.None,
            WindowState = WindowState.Maximized,
            Background = Brushes.Black,
            Content = plot,
            Topmost = true,
        };

        _fullscreenWindow.PreviewKeyDown += (s, ev) =>
        {
            if (ev.Key == Key.Escape)
            {
                ExitFullscreen();
                ev.Handled = true;
            }
        };

        _fullscreenWindow.Closed += (s, ev) => ExitFullscreen();
        _fullscreenWindow.Show();
    }

    private void ExitFullscreen()
    {
        if (_fullscreenPlot == null)
            return;

        var plot = _fullscreenPlot;
        _fullscreenPlot = null;

        if (_fullscreenWindow != null)
        {
            try
            {
                _fullscreenWindow.Content = null;
                _fullscreenWindow.Close();
            }
            catch { }
            finally { _fullscreenWindow = null; }
        }

        if (_originalParentBorder != null)
        {
            _originalParentBorder.Child = plot;
            _originalParentBorder = null;
        }
    }

    public void RenderOptimizationSurface(double[,] data)
    {
        if (ParameterSurfacePlot == null)
            return;

        ParameterSurfacePlot.Plot.Clear();
        ParameterSurfacePlot.Plot.Add.Heatmap(data);
        ParameterSurfacePlot.Plot.Title("Parameter surface");
        ParameterSurfacePlot.Refresh();
    }


    private async void LoadMt5ReportButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "MT5 reports (*.xml;*.htm;*.html;*.csv)|*.xml;*.htm;*.html;*.csv|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dlg.ShowDialog(this) != true)
            return;

        var path = dlg.FileName;
        FetchProgressBar.Visibility = Visibility.Visible;

        try
        {
            var results = await ApiService.Instance.ParseMt5ReportAsync(path).ConfigureAwait(true);
            PlotWfaResults(results);
            MainTabControl.SelectedItem = ResultsTab;
        }
        catch (ApiException ex)
        {
            MessageBox.Show(
                this,
                FormatApiError("Could not parse MT5 report.", ex),
                "Parse failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Parse failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            FetchProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private void ConfigureEmptyChart()
    {
        // Main chart removed from Results tab — no-op.
    }

    private async void StartBacktestButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildPayload(out var payload, out var error))
        {
            MessageBox.Show(this, error, "Invalid settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        StartBacktestButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        LoadingProgressBar.Value = 0;
        LoadingProgressBar.Visibility = Visibility.Visible;
        StatusMessageText.Text = "Starting...";
        StatusMessageText.Visibility = Visibility.Visible;
        MainTabControl.IsEnabled = false;
        MainTabControl.SelectedItem = ResultsTab;

        _analysisCts?.Dispose();
        _analysisCts = new System.Threading.CancellationTokenSource();
        _isCancelled = false;

        var progressReporter = new Progress<(int Progress, string Status)>(update =>
        {
            LoadingProgressBar.Value = update.Progress;
            StatusMessageText.Text = update.Status;
        });

        try
        {
            var analysis = await ApiService.Instance
                .RunAnalysisAsync(payload, progressReporter, _analysisCts.Token)
                .ConfigureAwait(true);
            PlotAnalysisResults(analysis);
            MainTabControl.SelectedItem = analysis.WfaWindows.Count > 0 ? ResultsTab : DeepAnalysisTab;
        }
        catch (OperationCanceledException)
        {
            // Cancellation requested by user
            MessageBox.Show(this, "Backtest was cancelled.", "Cancelled", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (ApiException ex)
        {
            MessageBox.Show(
                this,
                FormatApiError("The backtest request was rejected by the API.", ex),
                "Backtest failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Backtest failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            StartBacktestButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            LoadingProgressBar.Value = 0;
            LoadingProgressBar.Visibility = Visibility.Collapsed;
            StatusMessageText.Text = string.Empty;
            StatusMessageText.Visibility = Visibility.Collapsed;
            MainTabControl.IsEnabled = true;
            _analysisCts?.Dispose();
            _analysisCts = null;
            _isCancelled = false;
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        // Mark cancelled locally and request server-side cancellation if possible
        _isCancelled = true;
        StopButton.IsEnabled = false;

        try
        {
            var jobId = ApiService.Instance.CurrentJobId;
            if (!string.IsNullOrWhiteSpace(jobId))
            {
                await ApiService.Instance.CancelJobAsync(jobId).ConfigureAwait(true);
            }
        }
        catch
        {
            // Ignore cancel errors; user requested cancellation anyway.
        }
        finally
        {
            // Cancel the local polling token to break out of RunAnalysisAsync loop
            _analysisCts?.Cancel();
            StartBacktestButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            LoadingProgressBar.Visibility = Visibility.Collapsed;
            StatusMessageText.Text = "Cancelled";
            StatusMessageText.Visibility = Visibility.Visible;
            MainTabControl.IsEnabled = true;
        }
    }

    private async void RunMonteCarloButton_Click(object sender, RoutedEventArgs e)
    {
        // Determine selected window's trades; if none selected, fall back to global trades
        List<TradeRecordModel> tradesForMc = new();

        var selected = ResultsDataGrid.SelectedItem;
        if (selected != null && _currentAnalysis != null && _currentAnalysis.WfaWindows != null)
        {
            int? selectedWindowId = null;
            try
            {
                var prop = selected.GetType().GetProperty("TestWindowId");
                if (prop != null)
                {
                    var val = prop.GetValue(selected);
                    if (val is int i) selectedWindowId = i;
                    else if (val is long l) selectedWindowId = (int)l;
                    else if (val is string s && int.TryParse(s, out var pi)) selectedWindowId = pi;
                }
                else if (selected is System.Data.DataRowView drv && drv.Row.Table.Columns.Contains("TestWindowId"))
                {
                    var o = drv["TestWindowId"];
                    if (o is int oi) selectedWindowId = oi;
                    else if (o is long ol) selectedWindowId = (int)ol;
                    else if (o is string os && int.TryParse(os, out var osi)) selectedWindowId = osi;
                }
            }
            catch { }

            if (selectedWindowId.HasValue)
            {
                var win = _currentAnalysis.WfaWindows.FirstOrDefault(w => w.TestWindowId == selectedWindowId.Value);
                if (win != null)
                {
                    // Filter global trades to those inside the window range
                    var start = Math.Min(win.IsStartIndex, win.OosStartIndex);
                    var end = Math.Max(win.IsEndIndex, win.OosEndIndex);
                    tradesForMc = _currentAnalysis.Trades?
                        .Where(t => t.EntryIndex.HasValue && t.EntryIndex.Value >= start && t.EntryIndex.Value <= end)
                        .Select(t => t)
                        .ToList() ?? new List<TradeRecordModel>();
                }
            }
        }

        if (tradesForMc.Count == 0 && _currentAnalysis?.Trades != null)
            tradesForMc = _currentAnalysis.Trades.ToList();

        if (tradesForMc.Count == 0)
        {
            MessageBox.Show(this, "No trades available for Monte Carlo. Run a backtest first.", "No data", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        RunMonteCarloButton.IsEnabled = false;
        MonteCarloSurvivalText.Text = "Running Monte Carlo...";

        try
        {
            var mc = await ApiService.Instance.RunMonteCarloAsync(tradesForMc, iterations: 200).ConfigureAwait(true);

            // Ensure plotting happens on UI thread
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (MonteCarloSimPlot != null)
                {
                    MonteCarloSimPlot.Plot.Clear();

                    // Plot simulated curves (thin, semi-transparent lines)
                    foreach (var curve in mc.SimulatedEquities ?? new List<List<double>>())
                    {
                        if (curve == null || curve.Count == 0) continue;
                        var xs = Enumerable.Range(0, curve.Count).Select(i => (double)i).ToArray();
                        var ys = curve.ToArray();
                        var line = MonteCarloSimPlot.Plot.Add.Scatter(xs, ys);
                        line.LineWidth = 1f;
                        line.MarkerSize = 0f;
                        line.Color = ScottPlot.Color.FromColor(System.Drawing.Color.FromArgb(30, 100, 100, 100)); // semi-transparent gray
                    }

                    // Plot original equity on top (thick blue)
                    if (mc.OriginalEquity != null && mc.OriginalEquity.Count > 0)
                    {
                        var xs = Enumerable.Range(0, mc.OriginalEquity.Count).Select(i => (double)i).ToArray();
                        var ys = mc.OriginalEquity.ToArray();
                        var orig = MonteCarloSimPlot.Plot.Add.Scatter(xs, ys);
                        orig.Color = ScottPlot.Colors.DodgerBlue;
                        orig.LineWidth = 3f;
                        orig.MarkerSize = 0f;
                        orig.LegendText = "Original equity";
                    }

                    MonteCarloSimPlot.Plot.Title("Monte Carlo robustness simulation");
                    MonteCarloSimPlot.Plot.Axes.Bottom.Label.Text = "Step";
                    MonteCarloSimPlot.Plot.Axes.Left.Label.Text = "Equity";

                    MonteCarloSimPlot.Refresh();

                    MonteCarloSurvivalText.Text = mc != null ? $"Survival probability: {mc.SurvivalProbability:F1}%" : "Survival probability: N/A";
                }
            });
        }
        catch (ApiException ex)
        {
            MessageBox.Show(this, FormatApiError("Monte Carlo request failed.", ex), "Monte Carlo failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Monte Carlo failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            RunMonteCarloButton.IsEnabled = true;
        }
    }

    private async void StartClusterMatrixButton_Click(object sender, RoutedEventArgs e)
    {
            if (!TryBuildPayload(out var payload, out var error))
            {
                MessageBox.Show(this, error, "Invalid settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Parse matrix inputs from text boxes
            List<int> parsedWindows = new();
            List<int> parsedIs = new();
            try
            {
                var wtxt = MatrixWindowsTextBox.Text ?? string.Empty;
                parsedWindows = wtxt.Split(new[] {','}, StringSplitOptions.RemoveEmptyEntries)
                                    .Select(s => s.Trim())
                                    .Where(s => s.Length > 0)
                                    .Select(s => int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0)
                                    .Where(v => v > 0)
                                    .ToList();

                var itxt = MatrixIsPercentTextBox.Text ?? string.Empty;
                parsedIs = itxt.Split(new[] {','}, StringSplitOptions.RemoveEmptyEntries)
                                    .Select(s => s.Trim())
                                    .Where(s => s.Length > 0)
                                    .Select(s => int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0)
                                    .Where(v => v > 0 && v < 100)
                                    .ToList();
            }
            catch
            {
                // parsing error
            }

            if (parsedWindows.Count == 0 || parsedIs.Count == 0)
            {
                MessageBox.Show(this, "Provide at least one valid window count and one valid IS percent.", "Invalid cluster matrix inputs", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // attach to payload
            payload.ExecutionMode = "ClusterMatrix";
            payload.MatrixWindows = parsedWindows;
            payload.MatrixIsPercents = parsedIs;

            StartClusterMatrixButton.IsEnabled = false;

            payload.ExecutionMode = "ClusterMatrix";
            payload.MatrixWindows = parsedWindows;
            payload.MatrixIsPercents = parsedIs;

            StartClusterMatrixButton.IsEnabled = false;
            
            // --- EKSİK OLAN KABLOLAR BAĞLANIYOR ---
            StopButton.IsEnabled = true; 
            LoadingProgressBar.Value = 0; 
            LoadingProgressBar.Visibility = Visibility.Visible; 
            MainTabControl.IsEnabled = false; 

            _analysisCts?.Dispose(); 
            _analysisCts = new System.Threading.CancellationTokenSource(); 
            _isCancelled = false; 
            // --------------------------------------

            var progressReporter = new Progress<(int Progress, string Status)>(update =>
            {
                LoadingProgressBar.Value = update.Progress;
                StatusMessageText.Text = update.Status;
                StatusMessageText.Visibility = Visibility.Visible;
            });

            

            try
            {
                var analysis = await ApiService.Instance.RunAnalysisAsync(payload, progressReporter).ConfigureAwait(true);

                // Convert analysis.ClusterMatrix (list of dicts) to DataTable
                var dt = new System.Data.DataTable();
                if (analysis.ClusterMatrix != null && analysis.ClusterMatrix.Count > 0)
                {
                    var keys = analysis.ClusterMatrix.SelectMany(d => d.Keys).Distinct().ToList();
                    foreach (var k in keys)
                        dt.Columns.Add(k, typeof(string));

                    foreach (var dict in analysis.ClusterMatrix)
                    {
                        var dr = dt.NewRow();
                        foreach (var k in keys)
                        {
                            if (dict.TryGetValue(k, out var je))
                            {
                                try
                                {
                                    if (je.ValueKind == JsonValueKind.Number)
                                    {
                                        dr[k] = je.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture);
                                    }
                                    else if (je.ValueKind == JsonValueKind.String)
                                    {
                                        dr[k] = je.GetString() ?? string.Empty;
                                    }
                                    else
                                    {
                                        dr[k] = je.GetRawText();
                                    }
                                }
                                catch
                                {
                                    dr[k] = je.GetRawText();
                                }
                            }
                            else
                            {
                                dr[k] = System.DBNull.Value;
                            }
                        }
                        dt.Rows.Add(dr);
                    }
                }

                // Ensure UI assignment on UI thread and clear previous content first
                Application.Current.Dispatcher.Invoke(() =>
                {
                    try
                    {
                            ClusterMatrixGrid.ItemsSource = null;
                        ClusterMatrixGrid.ItemsSource = dt.DefaultView;
                        ClusterMatrixGrid.Items.Refresh();
                    }
                    catch { }
                });
            }
            catch (ApiException ex)
            {
                MessageBox.Show(this, FormatApiError("Cluster analysis failed.", ex), "Cluster failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Cluster failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {   StopButton.IsEnabled = false; // <-- EKLENDİ
                MainTabControl.IsEnabled = true;
                StartClusterMatrixButton.IsEnabled = true;
                StatusMessageText.Text = string.Empty;
                StatusMessageText.Visibility = Visibility.Collapsed;
            }
    }


    private bool TryBuildPayload(out WfaRequestPayload payload, out string error)
    {
        payload = new WfaRequestPayload();
        error = string.Empty;

        var mode = ReadExecutionMode();
        payload.ExecutionMode = mode;

        if (mode == "MT5Report")
        {
            var reportPath = Mt5ReportPathTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(reportPath))
            {
                error = "Select an MT5 report file.";
                return false;
            }

            payload.DataSourceType = "CSV";
            payload.FilePath = reportPath;
            payload.InitialDeposit = 10_000d;
            return true;
        }

        if (!double.TryParse(DepositTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var deposit) || deposit <= 0)
        {
            error = "Deposit must be a positive number.";
            return false;
        }

        if (!int.TryParse(WindowCountTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var windows) || windows < 1)
        {
            error = "Number of WFA windows must be an integer ≥ 1.";
            return false;
        }

        double inSamplePercent = 50d;
        payload.InSamplePercent = inSamplePercent;

        var start = StartDatePicker.SelectedDate;
        var end = EndDatePicker.SelectedDate;
        if (start is null || end is null)
        {
            error = "Start and end dates are required.";
            return false;
        }

        if (end < start)
        {
            error = "End date must be on or after start date.";
            return false;
        }

        var symbol = (SymbolCombo.Text ?? string.Empty).Trim();
        var timeframe = MapTimeframeToApi(ReadComboText(TimeframeCombo));
        if (string.IsNullOrWhiteSpace(symbol))
        {
            error = "Symbol is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(timeframe))
        {
            error = "Timeframe is required.";
            return false;
        }

        payload.InitialDeposit = deposit;
        payload.Currency = (CurrencyTextBox.Text ?? "USD").Trim();
        payload.Leverage = ReadComboText(LeverageCombo);
        payload.WindowCount = windows;
        payload.ForwardSplit = "1/2";
        payload.LatencyMode = ReadComboText(LatencyCombo);
        payload.ModellingType = ReadComboText(ModellingCombo);
        payload.WfaType = ReadComboText(WfaTypeComboBox);
        // Ensure the backend receives filenames with .py extension
        var rawStrategy = ExpertComboBox.SelectedItem as string ?? (ExpertComboBox.Text ?? string.Empty);
        payload.StrategyName = rawStrategy.EndsWith(".py", StringComparison.OrdinalIgnoreCase)
            ? rawStrategy
            : rawStrategy + ".py";

        payload.StartDate = start.Value.Date;
        payload.EndDate = end.Value.Date;
        payload.DateRangePreset = ReadComboText(DateRangeCombo);
        payload.Symbol = symbol;
        payload.Timeframe = timeframe;
        payload.Commission = 5d;
        payload.Slippage = 2d;
        payload.OptimizationMode = ReadOptimizationMode();
        payload.ExecutionMode = ReadExecutionMode();
        payload.FitnessCriterion = ReadComboTagOrContent(FitnessCriterionComboBox);

        if (InputsGrid.ItemsSource is IEnumerable<StrategyInputRow> rows)
        {
            payload.InputParameters = rows.ToList();
        }

        // Tab 0 = API download, Tab 1 = Local CSV
        if (DataSourceTabs.SelectedIndex == 1)
        {
            var path = CsvPathTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                error = "Select a CSV file or switch to API download.";
                return false;
            }

            payload.DataSourceType = "CSV";
            payload.FilePath = path;
            payload.AssetClass = null;
        }
        else
        {
            payload.DataSourceType = "API";
            payload.FilePath = null;
            payload.AssetClass = ReadAssetClassTag();
        }

        return true;
    }

    private void PlotDeepAnalysis(AnalysisResultModel analysis)
    {
        try
        {
            if (analysis == null)
            {
                System.Diagnostics.Debug.WriteLine("PlotDeepAnalysis: analysis is null");
                return;
            }

            // Log available data counts for debugging blank charts/trades/report and write to temp log for easier capture
            try
            {
                var countsMsg = $"PlotDeepAnalysis: PriceCurve={analysis.PriceCurve?.Count}, OhlcCurve={analysis.OhlcCurve?.Count}, EquityCurve={analysis.EquityCurve?.Count}, DrawdownCurve={analysis.DrawdownCurve?.Count}, Trades={analysis.Trades?.Count}, WfaWindows={analysis.WfaWindows?.Count}, OptimizationResults={analysis.OptimizationResults?.Count}";
                System.Diagnostics.Debug.WriteLine(countsMsg);
                try
                {
                    var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wfa-ui-log.txt");
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"[{DateTime.Now:O}] {countsMsg}");
                    if (analysis.Trades != null && analysis.Trades.Count > 0)
                    {
                        try { sb.AppendLine("FirstTrade: " + System.Text.Json.JsonSerializer.Serialize(analysis.Trades[0])); } catch { }
                    }
                    if (analysis.Summary != null && analysis.Summary.Count > 0)
                    {
                        try { sb.AppendLine("SummaryKeys: " + string.Join(",", analysis.Summary.Keys.Take(10))); } catch { }
                    }
                    System.IO.File.AppendAllText(tmp, sb.ToString());
                }
                catch { }
            }
            catch { }

            // Build headline safely
            string headline;
            try
            {
                if (analysis.Summary != null && analysis.Summary.Count > 0)
                {
                    var caseInsensitiveSummary = new Dictionary<string, string>(analysis.Summary, StringComparer.OrdinalIgnoreCase);
                    caseInsensitiveSummary.TryGetValue("Net Profit", out var netProfitStr);
                    caseInsensitiveSummary.TryGetValue("Total Return", out var totalReturnStr);
                    caseInsensitiveSummary.TryGetValue("Sharpe Ratio", out var sharpeStr);
                    caseInsensitiveSummary.TryGetValue("Max Drawdown", out var maxDdStr);
                    caseInsensitiveSummary.TryGetValue("Profit Factor", out var pfStr);
                    caseInsensitiveSummary.TryGetValue("Win Rate", out var winRateStr);
                    caseInsensitiveSummary.TryGetValue("Total Trades", out var tradesStr);

                    headline = $"Mode: {analysis.ExecutionMode} | Net profit: {netProfitStr ?? "-"} | Return: {totalReturnStr ?? "-"} | Sharpe: {sharpeStr ?? "-"} | " +
                               $"Max DD: {maxDdStr ?? "-"} | PF: {pfStr ?? "-"} | Win rate: {winRateStr ?? "-"} | Trades: {tradesStr ?? "-"}";
                }
                else if (analysis.SummaryModel != null)
                {
                    var s = analysis.SummaryModel;
                    headline = $"Mode: {analysis.ExecutionMode} | Net profit: {s.NetProfit:F2} | Return: {s.TotalReturnPercent:F2}% | Sharpe: {s.SharpeRatio:F2} | " +
                               $"Max DD: {s.MaxDrawdownPercent:F2}% | PF: {s.ProfitFactor:F2} | Win rate: {s.WinRate * 100:F1}% | Trades: {s.TotalTrades}";
                }
                else
                {
                    headline = $"Mode: {analysis.ExecutionMode}";
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error building headline in PlotDeepAnalysis: {ex}");
                headline = $"Mode: {analysis.ExecutionMode}";
            }

            // Update headline on UI
            try
            {
                Application.Current.Dispatcher.Invoke(() => DeepAnalysisSummaryText.Text = headline);

                // MT5 Raporunu ve Bar Grafiklerini Doldur (YENİ MOTOR)
                // If trades exist but contain only zero PnL/Return, prefer Summary/SummaryModel for report values.
                _forceUseSummaryReport = false;
                try
                {
                    if (analysis.Trades != null && analysis.Trades.Count > 0)
                    {
                        int nz = 0;
                        foreach (var t in analysis.Trades)
                        {
                            try { if (double.IsFinite(t.Pnl) && Math.Abs(t.Pnl) > 1e-12) nz++; } catch { }
                            try { if (double.IsFinite(t.ReturnPercent) && Math.Abs(t.ReturnPercent) > 1e-12) nz++; } catch { }
                        }

                        bool summaryHasNet = false;
                        try
                        {
                            if (analysis.SummaryModel != null && double.IsFinite(analysis.SummaryModel.NetProfit) && Math.Abs(analysis.SummaryModel.NetProfit) > 1e-12)
                                summaryHasNet = true;
                            else if (analysis.Summary != null && analysis.Summary.TryGetValue("Net Profit", out var np) && !string.IsNullOrWhiteSpace(np))
                                summaryHasNet = true;
                        }
                        catch { }

                        if (nz == 0 && summaryHasNet)
                            _forceUseSummaryReport = true;
                    }
                }
                catch { }

                GenerateMt5ReportAndCharts(analysis);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating DeepAnalysisSummaryText: {ex}");
                MessageBox.Show($"Error in DeepAnalysis summary update: {ex.Message}", "UI Render Error");
            }

            // Price chart and trade markers

            // Price chart and trade markers
            try
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (PriceChart == null) return;

                    PriceChart.Plot.Clear();

                    // Prefer OHLC if available; if timestamps missing, fall back to close-line plot
                    if (analysis.OhlcCurve != null && analysis.OhlcCurve.Count > 0)
                    {
                        try
                        {
                            bool usedCandles = false;
                            var ohlcs = new List<ScottPlot.OHLC>();
                            foreach (var p in analysis.OhlcCurve)
                            {
                                DateTime barTime = DateTime.MinValue;
                                bool hasTime = false;
                                if (!string.IsNullOrWhiteSpace(p.Timestamp))
                                {
                                    if (DateTime.TryParse(p.Timestamp, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out barTime))
                                    {
                                        hasTime = true;
                                    }
                                    else
                                    {
                                        // try ISO format
                                        if (DateTime.TryParse(p.Timestamp, out barTime)) hasTime = true;
                                    }
                                }

                                if (hasTime)
                                {
                                    TimeSpan barWidth = TimeSpan.FromDays(0.8);
                                    ohlcs.Add(new ScottPlot.OHLC(p.Open, p.High, p.Low, p.Close, barTime, barWidth));
                                }
                                else
                                {
                                    // Can't create OHLC with index-based times; break and fall back
                                    ohlcs.Clear();
                                    break;
                                }
                            }

                            if (ohlcs.Count > 0)
                            {
                                PriceChart.Plot.Add.Candlestick(ohlcs);
                                usedCandles = true;
                            }

                            if (!usedCandles)
                            {
                                // Fall back to plotting close price vs index
                                var xs = analysis.OhlcCurve.Select(p => (double)p.Index).ToArray();
                                var ys = analysis.OhlcCurve.Select(p => p.Close).ToArray();
                                if (xs.Length > 0 && ys.Length > 0)
                                {
                                    var priceLine = PriceChart.Plot.Add.Scatter(xs, ys);
                                    priceLine.Color = ScottPlot.Colors.Black;
                                    priceLine.LineWidth = 1.5f;
                                }
                            }

                            PriceChart.Plot.Title("Price (OHLC)");
                            PriceChart.Plot.Axes.Bottom.Label.Text = "Bar Index";
                            PriceChart.Plot.Axes.Left.Label.Text = "Price";
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Error plotting OHLC in PlotDeepAnalysis: {ex}");
                            MessageBox.Show($"Error plotting OHLC: {ex.Message}", "UI Render Error");
                        }
                    }

                    // If no OHLC and no PriceCurve, try fallback to EquityCurve so charts aren't blank
                    try
                    {
                        bool hasOhlc = analysis.OhlcCurve != null && analysis.OhlcCurve.Count > 0;
                        bool hasPriceCurve = analysis.PriceCurve != null && analysis.PriceCurve.Count > 0;
                        if (!hasOhlc && !hasPriceCurve)
                        {
                            if (analysis.EquityCurve != null && analysis.EquityCurve.Count > 0)
                            {
                                try
                                {
                                    var xsEq = analysis.EquityCurve.Select(p => (double)p.Index).ToArray();
                                    var ysEq = analysis.EquityCurve.Select(p => p.Value).ToArray();
                                    if (xsEq.Length > 0 && ysEq.Length > 0)
                                    {
                                        var eqLine = PriceChart.Plot.Add.Scatter(xsEq, ysEq);
                                        eqLine.Color = ScottPlot.Colors.Purple;
                                        eqLine.LineWidth = 1.5f;
                                        PriceChart.Plot.Title("Price not provided — plotting equity as fallback");
                                    }
                                }
                                catch (Exception exEq)
                                {
                                    System.Diagnostics.Debug.WriteLine($"Fallback equity plotting failed: {exEq}");
                                }
                            }
                            else if (analysis.BaselineEquityCurve != null && analysis.BaselineEquityCurve.Count > 0)
                            {
                                try
                                {
                                    var xsB = analysis.BaselineEquityCurve.Select(p => (double)p.Index).ToArray();
                                    var ysB = analysis.BaselineEquityCurve.Select(p => p.Value).ToArray();
                                    if (xsB.Length > 0 && ysB.Length > 0)
                                    {
                                        var bLine = PriceChart.Plot.Add.Scatter(xsB, ysB);
                                        bLine.Color = ScottPlot.Colors.Gray;
                                        bLine.LineWidth = 1.0f;
                                        PriceChart.Plot.Title("Price not provided — plotting baseline equity as fallback");
                                    }
                                }
                                catch (Exception exBase)
                                {
                                    System.Diagnostics.Debug.WriteLine($"Fallback baseline equity plotting failed: {exBase}");
                                }
                            }
                            else
                            {
                                PriceChart.Plot.Title("Price (no data)");
                            }
                        }
                    }
                    catch (Exception exFallback)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error in price fallback logic: {exFallback}");
                    }

                    // Trade markers (grouped to avoid creating one plottable per trade)
                    try
                    {
                        if (analysis.Trades != null && analysis.Trades.Count > 0)
                        {
                            var longEntryXs = new List<double>();
                            var longEntryYs = new List<double>();
                            var shortEntryXs = new List<double>();
                            var shortEntryYs = new List<double>();
                            var longExitXs = new List<double>();
                            var longExitYs = new List<double>();
                            var shortExitXs = new List<double>();
                            var shortExitYs = new List<double>();

                            foreach (var t in analysis.Trades)
                            {
                                try
                                {
                                    if (t.EntryIndex.HasValue && double.IsFinite(t.EntryPrice))
                                    {
                                        if (string.Equals(t.Direction, "Long", StringComparison.OrdinalIgnoreCase))
                                        {
                                            longEntryXs.Add(t.EntryIndex.Value);
                                            longEntryYs.Add(t.EntryPrice);
                                        }
                                        else
                                        {
                                            shortEntryXs.Add(t.EntryIndex.Value);
                                            shortEntryYs.Add(t.EntryPrice);
                                        }
                                    }

                                    if (t.ExitIndex.HasValue && double.IsFinite(t.ExitPrice))
                                    {
                                        if (string.Equals(t.Direction, "Long", StringComparison.OrdinalIgnoreCase))
                                        {
                                            longExitXs.Add(t.ExitIndex.Value);
                                            longExitYs.Add(t.ExitPrice);
                                        }
                                        else
                                        {
                                            shortExitXs.Add(t.ExitIndex.Value);
                                            shortExitYs.Add(t.ExitPrice);
                                        }
                                    }
                                }
                                catch { }
                            }

                            if (longEntryXs.Count > 0)
                            {
                                var eMarker = PriceChart.Plot.Add.Scatter(longEntryXs.ToArray(), longEntryYs.ToArray());
                                eMarker.LineWidth = 0;
                                eMarker.MarkerSize = 10;
                                eMarker.MarkerShape = ScottPlot.MarkerShape.FilledTriangleUp;
                                eMarker.Color = ScottPlot.Colors.Blue;
                            }

                            if (shortEntryXs.Count > 0)
                            {
                                var eMarker = PriceChart.Plot.Add.Scatter(shortEntryXs.ToArray(), shortEntryYs.ToArray());
                                eMarker.LineWidth = 0;
                                eMarker.MarkerSize = 10;
                                eMarker.MarkerShape = ScottPlot.MarkerShape.FilledTriangleDown;
                                eMarker.Color = ScottPlot.Colors.Red;
                            }

                            if (longExitXs.Count > 0)
                            {
                                var xMarker = PriceChart.Plot.Add.Scatter(longExitXs.ToArray(), longExitYs.ToArray());
                                xMarker.LineWidth = 0;
                                xMarker.MarkerSize = 10;
                                xMarker.MarkerShape = ScottPlot.MarkerShape.FilledTriangleDown;
                                xMarker.Color = ScottPlot.Colors.Red;
                            }

                            if (shortExitXs.Count > 0)
                            {
                                var xMarker = PriceChart.Plot.Add.Scatter(shortExitXs.ToArray(), shortExitYs.ToArray());
                                xMarker.LineWidth = 0;
                                xMarker.MarkerSize = 10;
                                xMarker.MarkerShape = ScottPlot.MarkerShape.FilledTriangleUp;
                                xMarker.Color = ScottPlot.Colors.Blue;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error plotting trade marker in PlotDeepAnalysis: {ex}");
                        MessageBox.Show($"Error plotting trade marker: {ex.Message}", "UI Render Error");
                    }

                    try { PriceChart.Refresh(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"PriceChart.Refresh error: {ex}"); }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating PriceChart in PlotDeepAnalysis: {ex}");
                MessageBox.Show($"Error updating PriceChart: {ex.Message}", "UI Render Error");
            }

            // Equity curve
            try
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (EquityChart == null) return;

                    EquityChart.Plot.Clear();
                    if (analysis.EquityCurve != null && analysis.EquityCurve.Count > 0)
                    {
                        var xs = analysis.EquityCurve.Select(p => (double)p.Index).ToArray();
                        var ys = analysis.EquityCurve.Select(p => p.Value).ToArray();

                        if (xs.Length > 0 && ys.Length > 0)
                        {
                            try
                            {
                                var n = xs.Length;
                                var bx = new double[n * 2];
                                var by = new double[n * 2];
                                for (int i = 0; i < n; i++)
                                {
                                    bx[i] = xs[i];
                                    by[i] = ys[i];
                                    bx[n + i] = xs[n - 1 - i];
                                    by[n + i] = 0;
                                }

                                dynamic add = EquityChart.Plot.Add;
                                var baseBlue = ScottPlot.Colors.Blue;
                                var fillBlue = System.Drawing.Color.FromArgb(255, baseBlue.R, baseBlue.G, baseBlue.B);
                                // Attempt reflective polygon invocation; fall back to FillBetween or Scatter if unavailable
                                bool polyOk = TryInvokePolygon(EquityChart.Plot.Add, bx, by, fillBlue);
                                if (!polyOk)
                                {
                                    try
                                    {
                                        // Fall back to a simple scatter line if polygon isn't available
                                        var equityLine = EquityChart.Plot.Add.Scatter(xs, ys);
                                        equityLine.Color = ScottPlot.Colors.Green;
                                        equityLine.LineWidth = 2f;
                                        equityLine.MarkerSize = 0f;
                                    }
                                    catch (Exception exFill)
                                    {
                                        System.Diagnostics.Debug.WriteLine($"Equity polygon fallback failed entirely: {exFill}");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Equity polygon failed, falling back to line: {ex}");
                                var equityLine = EquityChart.Plot.Add.Scatter(xs, ys);
                                equityLine.Color = ScottPlot.Colors.Green;
                                equityLine.LineWidth = 2f;
                                equityLine.MarkerSize = 0f;
                            }

                            EquityChart.Plot.Title("Equity curve");
                            EquityChart.Plot.Axes.Bottom.Label.Text = "Bar / window";
                            EquityChart.Plot.Axes.Left.Label.Text = "Equity";
                        }
                        else
                        {
                            EquityChart.Plot.Title("Equity curve (no data)");
                        }
                    }
                    else
                    {
                        EquityChart.Plot.Title("Equity curve (no data)");
                    }

                    try { EquityChart.Refresh(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"EquityChart.Refresh error: {ex}"); }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating EquityChart in PlotDeepAnalysis: {ex}");
                MessageBox.Show($"Error updating EquityChart: {ex.Message}", "UI Render Error");
            }

            // WFA timeline
            try
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (WfaTimelinePlot == null) return;

                    WfaTimelinePlot.Plot.Clear();
                    if (analysis.WfaWindows != null && analysis.WfaWindows.Count > 0)
                    {
                        double halfHeight = 0.35;
                        foreach (var w in analysis.WfaWindows.OrderBy(w => w.TestWindowId))
                        {
                            try
                            {
                                double isStart = w.IsStartIndex;
                                double isEnd = w.IsEndIndex;
                                double oosStart = w.OosStartIndex;
                                double oosEnd = w.OosEndIndex;
                                double y = w.TestWindowId;

                                var bx = new double[] { isStart, isEnd, isEnd, isStart };
                                var by = new double[] { y - halfHeight, y - halfHeight, y + halfHeight, y + halfHeight };
                                dynamic add = WfaTimelinePlot.Plot.Add;
                                var baseBlue = ScottPlot.Colors.Blue;
                                var fillBlue = System.Drawing.Color.FromArgb(120, baseBlue.R, baseBlue.G, baseBlue.B);
                                bool polyOk = TryInvokePolygon(WfaTimelinePlot.Plot.Add, bx, by, fillBlue);
                                if (!polyOk)
                                {
                                    try
                                    {
                                        // Fall back to drawing the closed rectangle as a scatter polyline
                                        var winLine = WfaTimelinePlot.Plot.Add.Scatter(bx, by);
                                        winLine.Color = ScottPlot.Color.FromColor(System.Drawing.Color.FromArgb(fillBlue.A, fillBlue.R, fillBlue.G, fillBlue.B));
                                        winLine.LineWidth = 1f;
                                        winLine.MarkerSize = 0f;
                                    }
                                    catch (Exception exFill)
                                    {
                                        System.Diagnostics.Debug.WriteLine($"WFA blue window scatter fallback failed: {exFill}");
                                    }
                                }

                                var ox = new double[] { oosStart, oosEnd, oosEnd, oosStart };
                                var oy = new double[] { y - halfHeight, y - halfHeight, y + halfHeight, y + halfHeight };
                                var baseGreen = ScottPlot.Colors.Green;
                                var fillGreen = System.Drawing.Color.FromArgb(120, baseGreen.R, baseGreen.G, baseGreen.B);
                                bool polyOk2 = TryInvokePolygon(WfaTimelinePlot.Plot.Add, ox, oy, fillGreen);
                                if (!polyOk2)
                                {
                                    try
                                    {
                                        var winLine2 = WfaTimelinePlot.Plot.Add.Scatter(ox, oy);
                                        winLine2.Color = ScottPlot.Color.FromColor(System.Drawing.Color.FromArgb(fillGreen.A, fillGreen.R, fillGreen.G, fillGreen.B));
                                        winLine2.LineWidth = 1f;
                                        winLine2.MarkerSize = 0f;
                                    }
                                    catch (Exception exFill2)
                                    {
                                        System.Diagnostics.Debug.WriteLine($"WFA green window scatter fallback failed: {exFill2}");
                                    }
                                }
                            }
                            catch (Exception exWin)
                            {
                                System.Diagnostics.Debug.WriteLine($"Error plotting WFA window in PlotDeepAnalysis: {exWin}");
                                MessageBox.Show($"Error plotting WFA window: {exWin.Message}", "UI Render Error");
                            }
                        }

                        WfaTimelinePlot.Plot.Title("WFA windows timeline");
                        WfaTimelinePlot.Plot.Axes.Bottom.Label.Text = "Bar index";
                        WfaTimelinePlot.Plot.Axes.Left.Label.Text = "WFA window id";
                    }

                    try { WfaTimelinePlot.Refresh(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"WfaTimelinePlot.Refresh error: {ex}"); }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating WfaTimelinePlot in PlotDeepAnalysis: {ex}");
                MessageBox.Show($"Error updating WFA timeline: {ex.Message}", "UI Render Error");
            }

            // Monte Carlo
            try
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (MonteCarloPlot == null) return;

                    MonteCarloPlot.Plot.Clear();
                    if (analysis.SimulatedEquities != null && analysis.SimulatedEquities.Count > 0)
                    {
                        try
                        {
                            var traceColor = ScottPlot.Color.FromColor(System.Drawing.Color.FromArgb(30, 100, 100, 100));
                            foreach (var curve in analysis.SimulatedEquities)
                            {
                                if (curve == null || curve.Count == 0) continue;
                                var xs = Enumerable.Range(0, curve.Count).Select(i => (double)i).ToArray();
                                var ys = curve.ToArray();
                                if (xs.Length > 0 && ys.Length > 0)
                                {
                                    var line = MonteCarloPlot.Plot.Add.Scatter(xs, ys);
                                    line.LineWidth = 1f;
                                    line.MarkerSize = 0f;
                                    line.Color = traceColor;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Error plotting simulated equities: {ex}");
                            MessageBox.Show($"Error plotting simulated equities: {ex.Message}", "UI Render Error");
                        }
                    }

                    if (analysis.OriginalEquity != null && analysis.OriginalEquity.Count > 0)
                    {
                        try
                        {
                            var xs = Enumerable.Range(0, analysis.OriginalEquity.Count).Select(i => (double)i).ToArray();
                            var ys = analysis.OriginalEquity.ToArray();
                            if (xs.Length > 0 && ys.Length > 0)
                            {
                                var originalLine = MonteCarloPlot.Plot.Add.Scatter(xs, ys);
                                originalLine.Color = analysis.SurvivalProbability >= 50.0 ? ScottPlot.Colors.Green : ScottPlot.Colors.Red;
                                originalLine.LineWidth = 3f;
                                originalLine.MarkerSize = 0f;
                                originalLine.LegendText = "Original equity";
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Error plotting original equity: {ex}");
                            MessageBox.Show($"Error plotting original equity: {ex.Message}", "UI Render Error");
                        }
                    }

                    try
                    {
                        MonteCarloPlot.Plot.Title($"Monte Carlo robustness simulation — Survival probability: {analysis.SurvivalProbability:F1}%");
                        MonteCarloPlot.Plot.Axes.Bottom.Label.Text = "Step";
                        MonteCarloPlot.Plot.Axes.Left.Label.Text = "Equity";
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"MonteCarloPlot labels/title error: {ex}");
                    }

                    try { MonteCarloPlot.Refresh(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"MonteCarloPlot.Refresh error: {ex}"); }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating MonteCarloPlot in PlotDeepAnalysis: {ex}");
                MessageBox.Show($"Error updating MonteCarloPlot: {ex.Message}", "UI Render Error");
            }

            // Drawdown
            try
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (DrawdownChart == null) return;

                    DrawdownChart.Plot.Clear();
                    if (analysis.DrawdownCurve != null && analysis.DrawdownCurve.Count > 0)
                    {
                        var xs = analysis.DrawdownCurve.Select(p => (double)p.Index).ToArray();
                        var ys = analysis.DrawdownCurve.Select(p => p.Value).ToArray();
                        if (xs.Length > 0 && ys.Length > 0)
                        {
                            var ddLine = DrawdownChart.Plot.Add.Scatter(xs, ys);
                            ddLine.LegendText = "Drawdown %";
                            ddLine.Color = ScottPlot.Colors.IndianRed;
                            ddLine.LineWidth = 2f;
                            ddLine.MarkerSize = 0f;

                            DrawdownChart.Plot.Title("Drawdown");
                            DrawdownChart.Plot.Axes.Bottom.Label.Text = "Bar / window";
                            DrawdownChart.Plot.Axes.Left.Label.Text = "Drawdown %";
                        }
                    }
                    else
                    {
                        DrawdownChart.Plot.Title("Drawdown (no data)");
                    }

                    try { DrawdownChart.Refresh(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"DrawdownChart.Refresh error: {ex}"); }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating DrawdownChart in PlotDeepAnalysis: {ex}");
                MessageBox.Show($"Error updating Drawdown chart: {ex.Message}", "UI Render Error");
            }

            // Sensitivity
            try
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (SensitivityPlot == null) return;

                    SensitivityPlot.Plot.Clear();
                    SensitivitySummaryText.Text = string.Empty;

                    if (analysis.TestedParams != null && analysis.TestedParams.Count > 0 && analysis.TestedProfits != null && analysis.TestedProfits.Count > 0)
                    {
                        var first = analysis.TestedParams.FirstOrDefault();
                        if (first != null)
                        {
                            var keys = first.Keys.ToList();
                            if (keys.Count >= 2)
                            {
                                var xKey = keys[0];
                                var yKey = keys[1];
                                var xsList = new List<double>();
                                var ysList = new List<double>();
                                var zsList = new List<double>();

                                for (int i = 0; i < analysis.TestedParams.Count && i < analysis.TestedProfits.Count; i++)
                                {
                                    try
                                    {
                                        var dict = analysis.TestedParams[i];
                                        double x = 0, y = 0; double z = analysis.TestedProfits[i];

                                        if (dict != null)
                                        {
                                            if (dict.TryGetValue(xKey, out var xje))
                                            {
                                                if (xje.ValueKind == System.Text.Json.JsonValueKind.Number) x = xje.GetDouble();
                                                else if (xje.ValueKind == System.Text.Json.JsonValueKind.String)
                                                    double.TryParse(xje.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out x);
                                            }

                                            if (dict.TryGetValue(yKey, out var yje))
                                            {
                                                if (yje.ValueKind == System.Text.Json.JsonValueKind.Number) y = yje.GetDouble();
                                                else if (yje.ValueKind == System.Text.Json.JsonValueKind.String)
                                                    double.TryParse(yje.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out y);
                                            }
                                        }

                                        xsList.Add(x);
                                        ysList.Add(y);
                                        zsList.Add(z);
                                    }
                                    catch (Exception exDot)
                                    {
                                        System.Diagnostics.Debug.WriteLine($"Error processing sensitivity point: {exDot}");
                                    }
                                }

                                if (zsList.Count > 0)
                                {
                                    double zmin = zsList.Min();
                                    double zmax = zsList.Max();

                                    for (int i = 0; i < xsList.Count; i++)
                                    {
                                        var norm = (zmax - zmin) > 0 ? (zsList[i] - zmin) / (zmax - zmin) : 0.5;
                                        int r = (int)(255.0 * (1.0 - norm));
                                        int g = (int)(255.0 * norm);
                                        var sysColor = System.Drawing.Color.FromArgb(255, Math.Max(0, Math.Min(255, r)), Math.Max(0, Math.Min(255, g)), 0);

                                        var px = new double[] { xsList[i] };
                                        var py = new double[] { ysList[i] };
                                        if (px.Length > 0 && py.Length > 0)
                                        {
                                            var marker = SensitivityPlot.Plot.Add.Scatter(px, py);
                                            marker.LineWidth = 0;
                                            marker.MarkerShape = ScottPlot.MarkerShape.FilledCircle;
                                            marker.MarkerSize = (float)(6 + norm * 24);
                                            marker.Color = ScottPlot.Color.FromColor(sysColor);
                                        }
                                    }

                                    SensitivityPlot.Plot.Title("Sensitivity analysis");
                                    SensitivityPlot.Plot.Axes.Bottom.Label.Text = first.Keys.ElementAt(0);
                                    SensitivityPlot.Plot.Axes.Left.Label.Text = first.Keys.ElementAt(1);
                                    SensitivityPlot.Refresh();
                                    SensitivitySummaryText.Text = $"Plotted {xsList.Count} tested points. X: {first.Keys.ElementAt(0)}, Y: {first.Keys.ElementAt(1)}";
                                }
                                else
                                {
                                    SensitivitySummaryText.Text = "Not enough sensitivity data.";
                                }
                            }
                            else
                            {
                                SensitivitySummaryText.Text = "Not enough parameter dimensions to plot sensitivity (needs ≥2).";
                            }
                        }
                    }
                    else
                    {
                        SensitivitySummaryText.Text = "No sensitivity data available.";
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating SensitivityPlot in PlotDeepAnalysis: {ex}");
                MessageBox.Show($"Error updating SensitivityPlot: {ex.Message}", "UI Render Error");
            }

            // Trades table (safe)
            try
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (analysis.Trades == null)
                    {
                        TradesDataGrid.ItemsSource = null;
                        return;
                    }

                    // Build quick lookup tables from price/equity curves for UI-side fallbacks
                    var priceByIndex = new Dictionary<int, double>();
                    try
                    {
                        if (analysis.PriceCurve != null)
                        {
                            foreach (var p in analysis.PriceCurve)
                            {
                                priceByIndex[p.Index] = p.Value;
                            }
                        }
                    }
                    catch { }

                    var equityByIndex = new Dictionary<int, double>();
                    try
                    {
                        if (analysis.EquityCurve != null)
                        {
                            foreach (var e in analysis.EquityCurve)
                            {
                                equityByIndex[e.Index] = e.Value;
                            }
                        }
                        else if (analysis.BaselineEquityCurve != null)
                        {
                            foreach (var e in analysis.BaselineEquityCurve)
                                equityByIndex[e.Index] = e.Value;
                        }
                    }
                    catch { }

                    // UI-only fallback: derive missing trade prices/PnL/Return from curves when server omitted them
                    try
                    {
                        foreach (var t in analysis.Trades)
                        {
                            try
                            {
                                bool entryPriceMissing = !double.IsFinite(t.EntryPrice) || t.EntryPrice == 0.0;
                                bool exitPriceMissing = !double.IsFinite(t.ExitPrice) || t.ExitPrice == 0.0;
                                bool pnlMissing = !double.IsFinite(t.Pnl) || t.Pnl == 0.0;
                                bool retMissing = !double.IsFinite(t.ReturnPercent) || t.ReturnPercent == 0.0;

                                // Try to fill entry/exit prices from PriceCurve by index
                                if (entryPriceMissing && t.EntryIndex.HasValue && priceByIndex.TryGetValue(t.EntryIndex.Value, out var ip))
                                {
                                    t.EntryPrice = ip;
                                    entryPriceMissing = false;
                                }
                                if (exitPriceMissing && t.ExitIndex.HasValue && priceByIndex.TryGetValue(t.ExitIndex.Value, out var xp))
                                {
                                    t.ExitPrice = xp;
                                    exitPriceMissing = false;
                                }

                                // If still missing, try equity diff (exit equity - entry equity) as PnL proxy
                                if ((pnlMissing || (entryPriceMissing && exitPriceMissing)) && t.EntryIndex.HasValue && t.ExitIndex.HasValue && equityByIndex.Count > 0)
                                {
                                    if (equityByIndex.TryGetValue(t.ExitIndex.Value, out var eqExit) && equityByIndex.TryGetValue(t.EntryIndex.Value, out var eqEntry))
                                    {
                                        double inferredPnl = eqExit - eqEntry;
                                        t.Pnl = inferredPnl;
                                        pnlMissing = false;
                                    }
                                }

                                // If prices now available compute PnL and ReturnPercent when possible
                                if ((pnlMissing || retMissing) && double.IsFinite(t.EntryPrice) && double.IsFinite(t.ExitPrice))
                                {
                                    double inferred = t.ExitPrice - t.EntryPrice;
                                    t.Pnl = inferred;
                                    pnlMissing = false;

                                    if (Math.Abs(t.EntryPrice) > 1e-12)
                                    {
                                        t.ReturnPercent = (inferred / Math.Abs(t.EntryPrice)) * 100.0;
                                        retMissing = false;
                                    }
                                }
                            }
                            catch (Exception exFill)
                            {
                                System.Diagnostics.Debug.WriteLine($"Error deriving trade prices/PnL: {exFill}");
                            }
                        }
                    }
                    catch { }

                    var rows = new List<TradeRow>();
                    int nonzeroPnl = 0, nonzeroRet = 0;
                    foreach (var t in analysis.Trades)
                    {
                        try
                        {
                            string pnlStr = double.IsFinite(t.Pnl) ? t.Pnl.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) : "-";
                            if (!string.IsNullOrWhiteSpace(pnlStr) && !pnlStr.Contains("$")) pnlStr += " $";

                            string retStr = double.IsFinite(t.ReturnPercent) ? t.ReturnPercent.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) : "-";
                            if (!string.IsNullOrWhiteSpace(retStr) && !retStr.Contains("%")) retStr += " %";

                            if (double.IsFinite(t.Pnl) && Math.Abs(t.Pnl) > 1e-12) nonzeroPnl++;
                            if (double.IsFinite(t.ReturnPercent) && Math.Abs(t.ReturnPercent) > 1e-12) nonzeroRet++;

                            rows.Add(new TradeRow
                            {
                                TradeId = t.TradeId,
                                Direction = t.Direction ?? string.Empty,
                                EntryTime = t.EntryTime ?? t.EntryIndex?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-",
                                ExitTime = t.ExitTime ?? t.ExitIndex?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-",
                                Pnl = pnlStr,
                                ReturnPercent = retStr
                            });
                        }
                        catch (Exception exRow)
                        {
                            System.Diagnostics.Debug.WriteLine($"Error formatting trade row: {exRow}");
                        }
                    }

                    TradesDataGrid.ItemsSource = rows;

                    try
                    {
                        System.Diagnostics.Debug.WriteLine($"TradesDataGrid rows populated: {rows.Count}, nonzeroPnl={nonzeroPnl}, nonzeroRet={nonzeroRet}");
                        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wfa-ui-log.txt");
                        System.IO.File.AppendAllText(tmp, $"[{DateTime.Now:O}] Trades rows: {rows.Count}, nonzeroPnl={nonzeroPnl}, nonzeroRet={nonzeroRet}\n");
                        if (rows.Count > 0)
                        {
                            try { System.Diagnostics.Debug.WriteLine($"First trade row preview: {System.Text.Json.JsonSerializer.Serialize(rows[0])}"); } catch { }
                        }
                    }
                    catch { }

                    try
                    {
                        System.Diagnostics.Debug.WriteLine($"TradesDataGrid rows populated: {rows.Count}");
                        if (rows.Count > 0)
                        {
                            var first = rows[0];
                            System.Diagnostics.Debug.WriteLine($"First trade row preview: {System.Text.Json.JsonSerializer.Serialize(first)}");
                        }
                    }
                    catch { }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error populating TradesDataGrid in PlotDeepAnalysis: {ex}");
                MessageBox.Show($"Error populating Trades table: {ex.Message}", "UI Render Error");
            }

        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Unhandled error in PlotDeepAnalysis: {ex}");
            MessageBox.Show($"Unhandled error in PlotDeepAnalysis: {ex.Message}", "UI Render Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }


 
  

    // Reflection helper to invoke ScottPlot's Polygon method across versions when signature varies.
    private bool TryInvokePolygon(object addObj, double[] xs, double[] ys, System.Drawing.Color color)
    {
        if (addObj == null || xs == null || ys == null) return false;
        try
        {
            var mtype = addObj.GetType();
            var methods = mtype.GetMethods().Where(m => m.Name == "Polygon");
            // Prepare PointF array as a common representation
            var pts = new System.Drawing.PointF[xs.Length];
            for (int i = 0; i < xs.Length; i++) pts[i] = new System.Drawing.PointF((float)xs[i], (float)ys[i]);

            foreach (var m in methods)
            {
                var ps = m.GetParameters();
                object[] args = null;
                try
                {
                    if (ps.Length == 3)
                    {
                        // Try (double[], double[], Color)
                        if (ps[0].ParameterType.IsArray && ps[0].ParameterType.GetElementType() == typeof(double))
                        {
                            args = new object[] { xs, ys, color };
                        }
                        else if (ps[0].ParameterType.IsArray && ps[0].ParameterType.GetElementType() == typeof(float))
                        {
                            var fx = xs.Select(v => (float)v).ToArray();
                            var fy = ys.Select(v => (float)v).ToArray();
                            args = new object[] { fx, fy, color };
                        }
                    }
                    else if (ps.Length == 2)
                    {
                        // Try (PointF[], Color) or (IEnumerable<PointF>, Color)
                        if (ps[0].ParameterType.IsArray && ps[0].ParameterType.GetElementType() == typeof(System.Drawing.PointF))
                        {
                            args = new object[] { pts, color };
                        }
                        else if (typeof(System.Collections.IEnumerable).IsAssignableFrom(ps[0].ParameterType))
                        {
                            args = new object[] { pts, color };
                        }
                    }
                    else if (ps.Length == 1)
                    {
                        if (ps[0].ParameterType.IsArray && ps[0].ParameterType.GetElementType() == typeof(System.Drawing.PointF))
                            args = new object[] { pts };
                        else if (typeof(System.Collections.IEnumerable).IsAssignableFrom(ps[0].ParameterType))
                            args = new object[] { pts };
                    }

                    if (args != null)
                    {
                        m.Invoke(addObj, args);
                        return true;
                    }
                }
                catch { /* try next overload */ }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TryInvokePolygon reflection failure: {ex}");
        }
        return false;

    }

    private static string MapTimeframeToApi(string mt5Timeframe)
    {
        return mt5Timeframe.Trim().ToUpperInvariant() switch
        {
            "M1" => "1m",
            "M5" => "5m",
            "M15" => "15m",
            "M30" => "30m",
            "H1" => "1h",
            "H4" => "4h",
            "D1" => "1d",
            "W1" => "1wk",
            _ => mt5Timeframe.ToLowerInvariant(),
        };
    }

    private string ReadAssetClassTag()
    {
        if (AssetClassCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            return tag;
        }

        return "FX";
    }

    private static string ReadComboText(ComboBox combo)
    {
        if (combo.SelectedItem is ComboBoxItem item)
        {
            return item.Content?.ToString()?.Trim() ?? string.Empty;
        }

        return combo.Text?.Trim() ?? string.Empty;
    }

    private static string ReadComboTagOrContent(ComboBox combo)
    {
        if (combo.SelectedItem is ComboBoxItem item)
        {
            if (item.Tag is string tag && !string.IsNullOrWhiteSpace(tag))
                return tag;

            return item.Content?.ToString()?.Trim() ?? string.Empty;
        }

        return combo.Text?.Trim() ?? string.Empty;
    }

    private static string FormatApiError(string summary, ApiException ex)
    {
        return $"{summary}{Environment.NewLine}{Environment.NewLine}" +
               $"HTTP {ex.StatusCode}{Environment.NewLine}{Environment.NewLine}" +
               ex.ResponseBody;
    }

 

    private void ResultsDataGrid_AutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (e == null || e.Column == null) return;
        var header = (e.Column.Header as string) ?? string.Empty;
        var h = header.ToLowerInvariant();

        // Use System.Windows.Media types explicitly to avoid ambiguity with ScottPlot types
        var blueColorObj = System.Windows.Media.ColorConverter.ConvertFromString("#4A90E2");
        var greenColorObj = System.Windows.Media.ColorConverter.ConvertFromString("#32CD32");
        var redColorObj = System.Windows.Media.ColorConverter.ConvertFromString("#FF4040");
        var blue = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)blueColorObj);
        var green = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)greenColorObj);
        var red = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)redColorObj);

        if (h.Contains("profit") || h.Contains("payoff") || h.Contains("factor"))
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.ForegroundProperty, blue));
            if (e.Column is System.Windows.Controls.DataGridBoundColumn bound)
                bound.ElementStyle = style;
        }
        else if (h.Contains("drawdown"))
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.ForegroundProperty, green));
            if (e.Column is System.Windows.Controls.DataGridBoundColumn bound)
                bound.ElementStyle = style;
        }
        else if (h.Contains("score") || h.Contains("result"))
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.ForegroundProperty, red));
            if (e.Column is System.Windows.Controls.DataGridBoundColumn bound)
                bound.ElementStyle = style;
        }
    }

    private void ResultsDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_currentAnalysis == null || _currentAnalysis.WfaWindows == null || _currentAnalysis.WfaWindows.Count == 0)
            return;

        var selected = ResultsDataGrid.SelectedItem;
        if (selected == null) return;

        int? testWindowId = null;
        try
        {
            var prop = selected.GetType().GetProperty("TestWindowId");
            if (prop != null)
            {
                var val = prop.GetValue(selected);
                if (val is int i) testWindowId = i;
                else if (val is long l) testWindowId = (int)l;
                else if (val is string s && int.TryParse(s, out var pi)) testWindowId = pi;
            }
            else
            {
                // If ItemsSource is a DataRowView (optimization table), try to extract column "TestWindowId"
                if (selected is System.Data.DataRowView drv && drv.Row.Table.Columns.Contains("TestWindowId"))
                {
                    var o = drv["TestWindowId"];
                    if (o is int oi) testWindowId = oi;
                    else if (o is long ol) testWindowId = (int)ol;
                    else if (o is string os && int.TryParse(os, out var osi)) testWindowId = osi;
                }
            }
        }
        catch { }

        if (!testWindowId.HasValue) return;

        var win = _currentAnalysis.WfaWindows.FirstOrDefault(w => w.TestWindowId == testWindowId.Value);
        if (win == null) return;

        // Plot the specific window (extract series and trades from the overall analysis)
        Dispatcher.Invoke(() => PlotWindowOnDashboards(win));
    }

    private void PlotWindowOnDashboards(WfaResultModel window)
    {
        try
        {
            if (_currentAnalysis == null)
            {
                System.Diagnostics.Debug.WriteLine("PlotWindowOnDashboards: _currentAnalysis is null");
                return;
            }

            if (window == null)
            {
                System.Diagnostics.Debug.WriteLine("PlotWindowOnDashboards: window is null");
                return;
            }

            // Define window range: use IS start..OOS end to show full coverage
            var start = Math.Min(window.IsStartIndex, window.OosStartIndex);
            var end = Math.Max(window.IsEndIndex, window.OosEndIndex);

            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    // Clear safely
                    try { PriceChart?.Plot.Clear(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"PriceChart.Clear error: {ex}"); }
                    try { EquityChart?.Plot.Clear(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"EquityChart.Clear error: {ex}"); }
                    try { DrawdownChart?.Plot.Clear(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"DrawdownChart.Clear error: {ex}"); }

                    // Price series segment: plot as simple line
                    try
                    {
                        if (_currentAnalysis.PriceCurve != null && _currentAnalysis.PriceCurve.Count > 0 && PriceChart != null)
                        {
                            var seg = _currentAnalysis.PriceCurve.Where(p => p.Index >= start && p.Index <= end).ToList();
                            if (seg != null && seg.Count > 0)
                            {
                                var xs = seg.Select(p => (double)p.Index).ToArray();
                                var ys = seg.Select(p => p.Value).ToArray();
                                if (xs.Length > 0 && ys.Length > 0)
                                {
                                    var priceLine = PriceChart.Plot.Add.Scatter(xs, ys);
                                    priceLine.LineWidth = 1f;
                                    priceLine.MarkerSize = 0f;
                                    PriceChart.Plot.Title($"Price — Window {window.TestWindowId}");
                                    PriceChart.Plot.Axes.Bottom.Label.Text = "Bar / window";
                                    PriceChart.Plot.Axes.Left.Label.Text = "Price";
                                }
                            }
                        }
                    }
                    catch (Exception exPrice)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error plotting price segment in PlotWindowOnDashboards: {exPrice}");
                        MessageBox.Show($"Error plotting price segment: {exPrice.Message}", "UI Render Error");
                    }

                    // Compute trades within window range (EntryIndex inside window)
                    var tradesInWindow = new List<TradeRecordModel>();
                    try
                    {
                        if (_currentAnalysis.Trades != null)
                        {
                            tradesInWindow = _currentAnalysis.Trades
                                .Where(t => t.EntryIndex.HasValue && t.EntryIndex.Value >= start && t.EntryIndex.Value <= end)
                                .OrderBy(t => t.EntryIndex)
                                .ToList();
                        }
                    }
                    catch (Exception exTrades)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error filtering trades in PlotWindowOnDashboards: {exTrades}");
                        MessageBox.Show($"Error filtering trades: {exTrades.Message}", "UI Render Error");
                    }

                    // Equity chart: cumulative PnL of those trades (stepwise)
                    try
                    {
                        if (EquityChart != null)
                        {
                            if (tradesInWindow != null && tradesInWindow.Count > 0)
                            {
                                var cum = new List<double>();
                                double running = 0.0;
                                foreach (var t in tradesInWindow)
                                {
                                    if (double.IsFinite(t.Pnl)) running += t.Pnl;
                                    cum.Add(running);
                                }

                                var xsEq = Enumerable.Range(0, cum.Count).Select(i => (double)i).ToArray();
                                var ysEq = cum.ToArray();
                                if (xsEq.Length > 0 && ysEq.Length > 0)
                                {
                                    var eqPlot = EquityChart.Plot.Add.Scatter(xsEq, ysEq);
                                    eqPlot.LineWidth = 2f;
                                    eqPlot.MarkerSize = 0f;
                                    eqPlot.Color = ScottPlot.Colors.Green;
                                    EquityChart.Plot.Title($"Trade cumulative PnL — Window {window.TestWindowId}");
                                    EquityChart.Plot.Axes.Bottom.Label.Text = "Trade # (ordered)";
                                    EquityChart.Plot.Axes.Left.Label.Text = "Cumulative PnL";
                                }
                            }
                            else
                            {
                                EquityChart.Plot.Title($"Trade cumulative PnL — Window {window.TestWindowId} (no trades)");
                            }
                        }
                    }
                    catch (Exception exEq)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error plotting equity in PlotWindowOnDashboards: {exEq}");
                        MessageBox.Show($"Error plotting equity: {exEq.Message}", "UI Render Error");
                    }

                    // Drawdown segment
                    try
                    {
                        if (_currentAnalysis.DrawdownCurve != null && _currentAnalysis.DrawdownCurve.Count > 0 && DrawdownChart != null)
                        {
                            var seg = _currentAnalysis.DrawdownCurve.Where(p => p.Index >= start && p.Index <= end).ToList();
                            if (seg != null && seg.Count > 0)
                            {
                                var xs = seg.Select(p => (double)p.Index).ToArray();
                                var ys = seg.Select(p => p.Value).ToArray();
                                if (xs.Length > 0 && ys.Length > 0)
                                {
                                    var dd = DrawdownChart.Plot.Add.Scatter(xs, ys);
                                    dd.LineWidth = 2f;
                                    dd.MarkerSize = 0f;
                                    DrawdownChart.Plot.Title($"Drawdown — Window {window.TestWindowId}");
                                    DrawdownChart.Plot.Axes.Bottom.Label.Text = "Bar / window";
                                    DrawdownChart.Plot.Axes.Left.Label.Text = "Drawdown %";
                                }
                            }
                        }
                    }
                    catch (Exception exDd)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error plotting drawdown in PlotWindowOnDashboards: {exDd}");
                        MessageBox.Show($"Error plotting drawdown: {exDd.Message}", "UI Render Error");
                    }

                    // Trade markers: grouped into 4 plottables to avoid per-trade overhead
                    try
                    {
                        if (tradesInWindow != null && tradesInWindow.Count > 0 && PriceChart != null)
                        {
                            var longEntryXs = new List<double>();
                            var longEntryYs = new List<double>();
                            var shortEntryXs = new List<double>();
                            var shortEntryYs = new List<double>();
                            var longExitXs = new List<double>();
                            var longExitYs = new List<double>();
                            var shortExitXs = new List<double>();
                            var shortExitYs = new List<double>();

                            foreach (var t in tradesInWindow)
                            {
                                try
                                {
                                    if (t.EntryIndex.HasValue && double.IsFinite(t.EntryPrice))
                                    {
                                        if (string.Equals(t.Direction, "Long", StringComparison.OrdinalIgnoreCase))
                                        {
                                            longEntryXs.Add(t.EntryIndex.Value);
                                            longEntryYs.Add(t.EntryPrice);
                                        }
                                        else
                                        {
                                            shortEntryXs.Add(t.EntryIndex.Value);
                                            shortEntryYs.Add(t.EntryPrice);
                                        }
                                    }

                                    if (t.ExitIndex.HasValue && double.IsFinite(t.ExitPrice))
                                    {
                                        if (string.Equals(t.Direction, "Long", StringComparison.OrdinalIgnoreCase))
                                        {
                                            longExitXs.Add(t.ExitIndex.Value);
                                            longExitYs.Add(t.ExitPrice);
                                        }
                                        else
                                        {
                                            shortExitXs.Add(t.ExitIndex.Value);
                                            shortExitYs.Add(t.ExitPrice);
                                        }
                                    }
                                }
                                catch { }
                            }

                            if (longEntryXs.Count > 0)
                            {
                                var m = PriceChart.Plot.Add.Scatter(longEntryXs.ToArray(), longEntryYs.ToArray());
                                m.LineWidth = 0;
                                m.MarkerSize = 10;
                                m.MarkerShape = ScottPlot.MarkerShape.FilledTriangleUp;
                                m.Color = ScottPlot.Colors.Green;
                            }

                            if (shortEntryXs.Count > 0)
                            {
                                var m = PriceChart.Plot.Add.Scatter(shortEntryXs.ToArray(), shortEntryYs.ToArray());
                                m.LineWidth = 0;
                                m.MarkerSize = 10;
                                m.MarkerShape = ScottPlot.MarkerShape.FilledTriangleDown;
                                m.Color = ScottPlot.Colors.Red;
                            }

                            if (longExitXs.Count > 0)
                            {
                                var m = PriceChart.Plot.Add.Scatter(longExitXs.ToArray(), longExitYs.ToArray());
                                m.LineWidth = 0;
                                m.MarkerSize = 10;
                                m.MarkerShape = ScottPlot.MarkerShape.FilledTriangleDown;
                                m.Color = ScottPlot.Colors.Red;
                            }

                            if (shortExitXs.Count > 0)
                            {
                                var m = PriceChart.Plot.Add.Scatter(shortExitXs.ToArray(), shortExitYs.ToArray());
                                m.LineWidth = 0;
                                m.MarkerSize = 10;
                                m.MarkerShape = ScottPlot.MarkerShape.FilledTriangleUp;
                                m.Color = ScottPlot.Colors.Green;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error plotting trade markers in PlotWindowOnDashboards: {ex}");
                    }

                    // Final refresh of plots on UI thread
                    try { PriceChart?.Refresh(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"PriceChart.Refresh error: {ex}"); }
                    try { EquityChart?.Refresh(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"EquityChart.Refresh error: {ex}"); }
                    try { DrawdownChart?.Refresh(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"DrawdownChart.Refresh error: {ex}"); }
                }
                catch (Exception exInner)
                {
                    System.Diagnostics.Debug.WriteLine($"Error inside dispatcher for PlotWindowOnDashboards: {exInner}");
                    MessageBox.Show($"Error rendering window dashboards: {exInner.Message}", "UI Render Error");
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Unhandled error in PlotWindowOnDashboards: {ex}");
            MessageBox.Show($"Unhandled error in PlotWindowOnDashboards: {ex.Message}", "UI Render Error");
        }
    }

    private static string FormatParameters(Dictionary<string, JsonElement> parameters)
    {
        if (parameters == null || parameters.Count == 0)
            return "-";

        var parts = new List<string>();
        foreach (var kvp in parameters)
        {
            parts.Add($"{kvp.Key}={kvp.Value}");
        }

        return string.Join(", ", parts);
    }

    private async void StartStandardOptButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildPayload(out var payload, out var error))
        {
            MessageBox.Show(this, error, "Invalid settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Force standard optimization execution mode regardless of payload UI setting
        payload.ExecutionMode = "StandardOptimization";

        StartBacktestButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        LoadingProgressBar.Value = 0;
        LoadingProgressBar.Visibility = Visibility.Visible;
        StatusMessageText.Text = "Starting...";
        StatusMessageText.Visibility = Visibility.Visible;
        MainTabControl.IsEnabled = false;
        MainTabControl.SelectedItem = ResultsTab;

        _analysisCts?.Dispose();
        _analysisCts = new System.Threading.CancellationTokenSource();
        _isCancelled = false;

        var progressReporter = new Progress<(int Progress, string Status)>(update =>
        {
            LoadingProgressBar.Value = update.Progress;
            StatusMessageText.Text = update.Status;
        });

        try
        {
            var analysis = await ApiService.Instance
                .RunAnalysisAsync(payload, progressReporter, _analysisCts.Token)
                .ConfigureAwait(true);
            PlotAnalysisResults(analysis);
            MainTabControl.SelectedItem = analysis.WfaWindows.Count > 0 ? ResultsTab : DeepAnalysisTab;
        }
        catch (OperationCanceledException)
        {
            MessageBox.Show(this, "Backtest was cancelled.", "Cancelled", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (ApiException ex)
        {
            MessageBox.Show(
                this,
                FormatApiError("The backtest request was rejected by the API.", ex),
                "Backtest failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Backtest failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            StartBacktestButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            LoadingProgressBar.Value = 0;
            LoadingProgressBar.Visibility = Visibility.Collapsed;
            StatusMessageText.Text = string.Empty;
            StatusMessageText.Visibility = Visibility.Collapsed;
            MainTabControl.IsEnabled = true;
            _analysisCts?.Dispose();
            _analysisCts = null;
            _isCancelled = false;
        }
    }

private void PlotAnalysisResults(AnalysisResultModel analysis)
    {
        _currentAnalysis = analysis;
        PlotDeepAnalysis(analysis);

        try
        {
            if (analysis.ExecutionMode == "StandardOptimization" && analysis.OptimizationResults != null && analysis.OptimizationResults.Count > 0)
            {
                var dt = new System.Data.DataTable();
                var keys = analysis.OptimizationResults.SelectMany(d => d.Keys).Distinct().ToList();
                foreach (var k in keys) dt.Columns.Add(k, typeof(string));

                foreach (var dict in analysis.OptimizationResults)
                {
                    var dr = dt.NewRow();
                    foreach (var k in keys)
                    {
                        if (dict.TryGetValue(k, out var je))
                        {
                            try
                            {
                                if (je.ValueKind == JsonValueKind.Number) dr[k] = je.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture);
                                else if (je.ValueKind == JsonValueKind.String) dr[k] = je.GetString() ?? string.Empty;
                                else dr[k] = je.GetRawText();
                            }
                            catch { dr[k] = je.GetRawText(); }
                        }
                        else dr[k] = System.DBNull.Value;
                    }
                    dt.Rows.Add(dr);
                }

                Application.Current.Dispatcher.Invoke(() => {
                    ResultsDataGrid.ItemsSource = dt.DefaultView;
                });
                
                PlotOptimizationGraph(analysis.OptimizationResults);
            }
            else
            {
                PlotWfaResults(analysis.WfaWindows);
                if (analysis.OptimizationResults != null && analysis.OptimizationResults.Count > 0)
                {
                    PlotOptimizationGraph(analysis.OptimizationResults);
                }
            }
        }
        catch { }
    }

    private void PlotOptimizationGraph(List<Dictionary<string, System.Text.Json.JsonElement>> optResults)
    {
        try
        {
            if (optResults == null || optResults.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine("PlotOptimizationGraph: no results");
                return;
            }

            var xsPos = new List<double>();
            var ysPos = new List<double>();
            var xsNeg = new List<double>();
            var ysNeg = new List<double>();

            for (int i = 0; i < optResults.Count; i++)
            {
                try
                {
                    var dict = optResults[i] ?? new Dictionary<string, System.Text.Json.JsonElement>();
                    double y = double.NaN;
                    double profit = double.NaN;

                    void TryReadNumber(string key, out double outVal)
                    {
                        outVal = double.NaN;
                        if (dict.TryGetValue(key, out var je))
                        {
                            if (je.ValueKind == System.Text.Json.JsonValueKind.Number)
                                outVal = je.GetDouble();
                            else if (je.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                double.TryParse(je.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out outVal);
                            }
                        }
                    }

                    TryReadNumber("Score", out y);
                    if (double.IsNaN(y)) TryReadNumber("Result (Score)", out y);
                    if (double.IsNaN(y)) TryReadNumber("Result", out y);

                    TryReadNumber("Profit", out profit);
                    if (double.IsNaN(profit)) profit = y;

                    if (double.IsNaN(y)) continue;

                    if (!double.IsNaN(profit) && profit > 0)
                    {
                        xsPos.Add(i + 1);
                        ysPos.Add(y);
                    }
                    else
                    {
                        xsNeg.Add(i + 1);
                        ysNeg.Add(y);
                    }
                }
                catch (Exception exItem)
                {
                    System.Diagnostics.Debug.WriteLine($"Error parsing optimization result item {i}: {exItem}");
                }
            }

            Application.Current.Dispatcher.Invoke(() =>
            {
                try
                {
                    if (OptimizationScatterPlot == null) return;
                    OptimizationScatterPlot.Plot.Clear();

                    if (xsPos.Count > 0 && ysPos.Count == xsPos.Count)
                    {
                        try
                        {
                            var sp = OptimizationScatterPlot.Plot.Add.Scatter(xsPos.ToArray(), ysPos.ToArray());
                            sp.Color = ScottPlot.Color.FromColor(System.Drawing.Color.LimeGreen);
                            sp.LineWidth = 0f;
                            sp.MarkerSize = 6f;
                            sp.MarkerShape = ScottPlot.MarkerShape.FilledCircle;
                        }
                        catch (Exception exPlot)
                        {
                            System.Diagnostics.Debug.WriteLine($"Error plotting positive optimization points: {exPlot}");
                            MessageBox.Show($"Error plotting positive optimization points: {exPlot.Message}", "UI Render Error");
                        }
                    }

                    if (xsNeg.Count > 0 && ysNeg.Count == xsNeg.Count)
                    {
                        try
                        {
                            var sn = OptimizationScatterPlot.Plot.Add.Scatter(xsNeg.ToArray(), ysNeg.ToArray());
                            sn.Color = ScottPlot.Color.FromColor(System.Drawing.Color.Red);
                            sn.LineWidth = 0f;
                            sn.MarkerSize = 6f;
                            sn.MarkerShape = ScottPlot.MarkerShape.FilledCircle;
                        }
                        catch (Exception exPlot)
                        {
                            System.Diagnostics.Debug.WriteLine($"Error plotting negative optimization points: {exPlot}");
                            MessageBox.Show($"Error plotting negative optimization points: {exPlot.Message}", "UI Render Error");
                        }
                    }

                    try { OptimizationScatterPlot.Plot.Axes.Bottom.Label.Text = "Passes (Deneme Sayısı)"; } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"OptimizationScatterPlot bottom label error: {ex}"); }
                    try { OptimizationScatterPlot.Plot.Axes.Left.Label.Text = "Optimizasyon Skoru"; } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"OptimizationScatterPlot left label error: {ex}"); }
                    try { OptimizationScatterPlot.Plot.Title("Optimization Graph"); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"OptimizationScatterPlot title error: {ex}"); }

                    try { OptimizationScatterPlot.Refresh(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"OptimizationScatterPlot.Refresh error: {ex}"); }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Dispatcher error in PlotOptimizationGraph: {ex}");
                    MessageBox.Show($"Error updating Optimization graph: {ex.Message}", "UI Render Error");
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Unhandled error in PlotOptimizationGraph: {ex}");
            MessageBox.Show($"Unhandled error in PlotOptimizationGraph: {ex.Message}", "UI Render Error");
        }
    }

    private void PlotWfaResults(IReadOnlyList<WfaResultModel> results)
    {
        try
        {
            if (results == null || results.Count == 0)
            {
                Application.Current.Dispatcher.Invoke(() => { ResultsDataGrid.ItemsSource = null; });
                return;
            }

            var ordered = results.OrderBy(r => r.TestWindowId).ToList();

            var displayResults = new List<object>();
            foreach (var r in ordered)
            {
                try
                {
                    string isKari = double.IsFinite(r.IsProfit) ? r.IsProfit.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) : "-";
                    if (!isKari.Contains("$")) isKari += " $";

                    string oosKari = double.IsFinite(r.OosProfit) ? r.OosProfit.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) : "-";
                    if (!oosKari.Contains("$")) oosKari += " $";

                    string winRate = double.IsFinite(r.WinRate) ? (r.WinRate * 100.0).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) : "-";
                    if (!winRate.Contains("%")) winRate += " %";

                    string drawdown = double.IsFinite(r.DrawdownPercent) ? r.DrawdownPercent.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) : "-";
                    if (!drawdown.Contains("%")) drawdown += " %";

                    displayResults.Add(new
                    {
                        r.TestWindowId,
                        Pencere = "Window " + r.TestWindowId,
                        IS_Kari = isKari,
                        OOS_Kari = oosKari,
                        OOS_WinRate = winRate,
                        OOS_Drawdown = drawdown,
                        OOS_Trades = r.TotalTrades,
                        En_Iyi_Parametreler = FormatParameters(r.BestParameters)
                    });
                }
                catch (Exception exRow)
                {
                    System.Diagnostics.Debug.WriteLine($"Error formatting WFA result row for window {r?.TestWindowId}: {exRow}");
                }
            }

            Application.Current.Dispatcher.Invoke(() => { ResultsDataGrid.ItemsSource = displayResults; });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Unhandled error in PlotWfaResults: {ex}");
            MessageBox.Show($"Unhandled error in PlotWfaResults: {ex.Message}", "UI Render Error");
        }
    }

    
private void GenerateMt5ReportAndCharts(AnalysisResultModel analysis)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            try
            {
                // If trades are present, build the detailed MT5-like report from trades
                if (!_forceUseSummaryReport && analysis.Trades != null && analysis.Trades.Count > 0)
                {
                    int totalTrades = analysis.Trades.Count;
                    int profitTrades = 0, lossTrades = 0, longTrades = 0, shortTrades = 0;
                    int longWon = 0, shortWon = 0;
                    double grossProfit = 0, grossLoss = 0, largestProfit = 0, largestLoss = 0;
                    int currentWinStreak = 0, maxWinStreak = 0, currentLossStreak = 0, maxLossStreak = 0;

                    var hourCounts = new double[24];
                    var dayCounts = new double[7];
                    var monthCounts = new double[12];

                    foreach (var t in analysis.Trades)
                    {
                        bool isLong = string.Equals(t.Direction, "Long", StringComparison.OrdinalIgnoreCase);
                        if (isLong) longTrades++; else shortTrades++;

                        if (double.IsFinite(t.Pnl) && t.Pnl > 0)
                        {
                            profitTrades++; grossProfit += t.Pnl;
                            if (t.Pnl > largestProfit) largestProfit = t.Pnl;
                            if (isLong) longWon++; else shortWon++;

                            currentWinStreak++; currentLossStreak = 0;
                            if (currentWinStreak > maxWinStreak) maxWinStreak = currentWinStreak;
                        }
                        else if (double.IsFinite(t.Pnl))
                        {
                            lossTrades++; grossLoss += t.Pnl;
                            if (t.Pnl < largestLoss) largestLoss = t.Pnl;

                            currentLossStreak++; currentWinStreak = 0;
                            if (currentLossStreak > maxLossStreak) maxLossStreak = currentLossStreak;
                        }

                        // Saat, Gün, Ay grafikleri için verileri parse et (if entry time parseable)
                        string timeStr = t.EntryTime?.Replace("T", " ") ?? "";
                        if (DateTime.TryParse(timeStr, out DateTime dt))
                        {
                            if (dt.Hour >= 0 && dt.Hour < 24) hourCounts[dt.Hour]++;
                            dayCounts[(int)dt.DayOfWeek]++;
                            if (dt.Month >= 1 && dt.Month <= 12) monthCounts[dt.Month - 1]++;
                        }
                    }

                    double netProfit = grossProfit + grossLoss;
                    double profitFactor = grossLoss != 0 ? Math.Abs(grossProfit / grossLoss) : (grossProfit > 0 ? 999.99 : 0);
                    double expectedPayoff = totalTrades > 0 ? netProfit / totalTrades : 0;
                    double winRate = totalTrades > 0 ? (profitTrades / (double)totalTrades) * 100 : 0;

                    RepNetProfit.Text = netProfit.ToString("F2", CultureInfo.InvariantCulture) + " $";
                    RepGrossProfit.Text = grossProfit.ToString("F2", CultureInfo.InvariantCulture) + " $";
                    RepGrossLoss.Text = grossLoss.ToString("F2", CultureInfo.InvariantCulture) + " $";
                    RepProfitFactor.Text = profitFactor.ToString("F2", CultureInfo.InvariantCulture);
                    RepExpectedPayoff.Text = expectedPayoff.ToString("F2", CultureInfo.InvariantCulture) + " $";
                    RepWinRate.Text = winRate.ToString("F2", CultureInfo.InvariantCulture) + " %";
                    RepTotalTrades.Text = totalTrades.ToString();
                    RepLargestProfit.Text = largestProfit.ToString("F2", CultureInfo.InvariantCulture) + " $";
                    RepLargestLoss.Text = largestLoss.ToString("F2", CultureInfo.InvariantCulture) + " $";
                    RepMaxWinStreak.Text = maxWinStreak.ToString();
                    RepMaxLossStreak.Text = maxLossStreak.ToString();
                    RepProfitTradesPct.Text = $"{profitTrades} ({winRate.ToString("F1", CultureInfo.InvariantCulture)}%)";
                    RepLossTradesPct.Text = totalTrades > 0 ? $"{lossTrades} ({(lossTrades / (double)totalTrades * 100).ToString("F1", CultureInfo.InvariantCulture)}%)" : "-";
                    RepLongTradesPct.Text = totalTrades > 0 ? $"{longTrades} ({(longTrades > 0 ? (longWon / (double)longTrades * 100) : 0).ToString("F1", CultureInfo.InvariantCulture)}%)" : "-";
                    RepShortTradesPct.Text = totalTrades > 0 ? $"{shortTrades} ({(shortTrades > 0 ? (shortWon / (double)shortTrades * 100) : 0).ToString("F1", CultureInfo.InvariantCulture)}%)" : "-";

                    // Bar Grafiklerini Çiz (Saat, Gün, Ay)
                    try { RepEntriesByHourPlot.Plot.Clear(); } catch { }
                    try { var hBar = RepEntriesByHourPlot.Plot.Add.Bars(hourCounts); hBar.Color = ScottPlot.Colors.DodgerBlue; RepEntriesByHourPlot.Plot.Title("Entries by Hour"); RepEntriesByHourPlot.Refresh(); } catch { }

                    try { RepProfitByWeekdayPlot.Plot.Clear(); } catch { }
                    try { var dBar = RepProfitByWeekdayPlot.Plot.Add.Bars(dayCounts); dBar.Color = ScottPlot.Colors.MediumSeaGreen; RepProfitByWeekdayPlot.Plot.Title("Entries by Weekday"); RepProfitByWeekdayPlot.Refresh(); } catch { }

                    try { RepEntriesByMonthPlot.Plot.Clear(); } catch { }
                    try { var mBar = RepEntriesByMonthPlot.Plot.Add.Bars(monthCounts); mBar.Color = ScottPlot.Colors.Orange; RepEntriesByMonthPlot.Plot.Title("Entries by Month"); RepEntriesByMonthPlot.Refresh(); } catch { }

                    return; // done when trades present
                }

                // If no trades are available, try to populate report from SummaryModel or Summary dictionary
                if (analysis.SummaryModel != null)
                {
                    var s = analysis.SummaryModel;
                    try { RepNetProfit.Text = double.IsFinite(s.NetProfit) ? s.NetProfit.ToString("F2", CultureInfo.InvariantCulture) + " $" : "-"; } catch { RepNetProfit.Text = "-"; }
                    try { RepProfitFactor.Text = double.IsFinite(s.ProfitFactor) ? s.ProfitFactor.ToString("F2", CultureInfo.InvariantCulture) : "-"; } catch { RepProfitFactor.Text = "-"; }
                    try { RepWinRate.Text = double.IsFinite(s.WinRate) ? (s.WinRate * 100.0).ToString("F2", CultureInfo.InvariantCulture) + " %" : "-"; } catch { RepWinRate.Text = "-"; }
                    try { RepTotalTrades.Text = s.TotalTrades.ToString(); } catch { RepTotalTrades.Text = "-"; }
                    try { double expected = (s.TotalTrades > 0 && double.IsFinite(s.NetProfit)) ? (s.NetProfit / s.TotalTrades) : double.NaN; RepExpectedPayoff.Text = double.IsFinite(expected) ? expected.ToString("F2", CultureInfo.InvariantCulture) + " $" : "-"; } catch { RepExpectedPayoff.Text = "-"; }
                    // Clear charts because no trades available to build hour/day/month
                    try { RepEntriesByHourPlot.Plot.Clear(); RepEntriesByHourPlot.Plot.Title("Entries by Hour (no trade timestamps)"); RepEntriesByHourPlot.Refresh(); } catch { }
                    try { RepProfitByWeekdayPlot.Plot.Clear(); RepProfitByWeekdayPlot.Plot.Title("Entries by Weekday (no trade timestamps)"); RepProfitByWeekdayPlot.Refresh(); } catch { }
                    try { RepEntriesByMonthPlot.Plot.Clear(); RepEntriesByMonthPlot.Plot.Title("Entries by Month (no trade timestamps)"); RepEntriesByMonthPlot.Refresh(); } catch { }

                    return;
                }

                // Last resort: try to populate from the generic Summary dictionary (case-insensitive)
                if (analysis.Summary != null && analysis.Summary.Count > 0)
                {
                    var dict = new Dictionary<string, string>(analysis.Summary, StringComparer.OrdinalIgnoreCase);
                    dict.TryGetValue("Net Profit", out var netProfitStr);
                    dict.TryGetValue("Gross Profit", out var grossProfitStr);
                    dict.TryGetValue("Gross Loss", out var grossLossStr);
                    dict.TryGetValue("Profit Factor", out var pfStr);
                    dict.TryGetValue("Win Rate", out var winStr);
                    dict.TryGetValue("Total Trades", out var tradesStr);

                    RepNetProfit.Text = string.IsNullOrWhiteSpace(netProfitStr) ? "-" : netProfitStr + (netProfitStr.Contains("$") ? "" : " $");
                    RepGrossProfit.Text = string.IsNullOrWhiteSpace(grossProfitStr) ? "-" : grossProfitStr + (grossProfitStr.Contains("$") ? "" : " $");
                    RepGrossLoss.Text = string.IsNullOrWhiteSpace(grossLossStr) ? "-" : grossLossStr + (grossLossStr.Contains("$") ? "" : " $");
                    RepProfitFactor.Text = string.IsNullOrWhiteSpace(pfStr) ? "-" : pfStr;
                    RepWinRate.Text = string.IsNullOrWhiteSpace(winStr) ? "-" : winStr + (winStr.Contains("%") ? "" : " %");
                    RepTotalTrades.Text = string.IsNullOrWhiteSpace(tradesStr) ? "-" : tradesStr;

                    // Clear small charts
                    try { RepEntriesByHourPlot.Plot.Clear(); RepEntriesByHourPlot.Plot.Title("Entries by Hour (no trade timestamps)"); RepEntriesByHourPlot.Refresh(); } catch { }
                    try { RepProfitByWeekdayPlot.Plot.Clear(); RepProfitByWeekdayPlot.Plot.Title("Entries by Weekday (no trade timestamps)"); RepProfitByWeekdayPlot.Refresh(); } catch { }
                    try { RepEntriesByMonthPlot.Plot.Clear(); RepEntriesByMonthPlot.Plot.Title("Entries by Month (no trade timestamps)"); RepEntriesByMonthPlot.Refresh(); } catch { }

                    return;
                }

                // Nothing to show — clear report controls
                RepNetProfit.Text = "-";
                RepGrossProfit.Text = "-";
                RepGrossLoss.Text = "-";
                RepProfitFactor.Text = "-";
                RepExpectedPayoff.Text = "-";
                RepWinRate.Text = "-";
                RepTotalTrades.Text = "-";
                RepLargestProfit.Text = "-";
                RepLargestLoss.Text = "-";
                RepMaxWinStreak.Text = "-";
                RepMaxLossStreak.Text = "-";
                RepProfitTradesPct.Text = "-";
                RepLossTradesPct.Text = "-";
                RepLongTradesPct.Text = "-";
                RepShortTradesPct.Text = "-";
                try { RepEntriesByHourPlot.Plot.Clear(); RepEntriesByHourPlot.Refresh(); } catch { }
                try { RepProfitByWeekdayPlot.Plot.Clear(); RepProfitByWeekdayPlot.Refresh(); } catch { }
                try { RepEntriesByMonthPlot.Plot.Clear(); RepEntriesByMonthPlot.Refresh(); } catch { }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GenerateMt5ReportAndCharts error: {ex}");
            }
        });
    }

}
