using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace RigControlApp
{
    public partial class MainWindow : Window
    {
        private IRigDriver? _driver;
        private RigConfig _config = new();
        private readonly DispatcherTimer _pollTimer = new();

        // VFO-A / VFO-B 内部状態キャッシュ
        private long _currentFreq = 14074000;
        private long _vfoAFreq = 14074000;
        private long _vfoBFreq = 7074000;
        private string _vfoAMode = "USB";
        private string _vfoBMode = "LSB";
        private string _vfoAAntenna = "1";
        private string _vfoBAntenna = "1";
        private string _vfoABandwidth = "";
        private string _vfoBBandwidth = "";
        private VfoType _activeVfo = VfoType.VfoA;

        // PTT・チューナー状態キャッシュ
        private bool _isTxActive = false;
        private bool _isTunerActive = false;

        private bool _isBusy = false;
        private bool _isUpdatingText = false;
        private bool _isUpdatingBand = false;
        private bool _isUpdatingAntenna = false;
        private bool _isUpdatingFilter = false;
        private string _lastReadMode = "";
        private string _lastReadAntenna = "";
        private string _lastReadBandwidth = "";

        public MainWindow()
        {
            InitializeComponent();
            InitControls();
            Loaded += MainWindow_Loaded;
        }

        private void InitControls()
        {
            CmbPort.ItemsSource = SerialPort.GetPortNames();
            if (CmbPort.Items.Count > 0) CmbPort.SelectedIndex = 0;

            // 実行ベースディレクトリおよびカレントディレクトリから .ini ファイルを網羅的に列挙
            CmbConfig.Items.Clear();
            var searchPaths = new List<string> { AppDomain.CurrentDomain.BaseDirectory, "." };
            var foundFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in searchPaths)
            {
                if (Directory.Exists(path))
                {
                    try
                    {
                        var files = Directory.GetFiles(path, "*.ini", SearchOption.AllDirectories);
                        foreach (var f in files)
                        {
                            string fullPath = Path.GetFullPath(f);
                            if (foundFiles.Add(fullPath))
                            {
                                string fileName = Path.GetFileNameWithoutExtension(f);
                                // 表示名は拡張子なし、Tagに実体フルパスを保持
                                CmbConfig.Items.Add(new ComboBoxItem { Content = fileName, Tag = fullPath });
                            }
                        }
                    }
                    catch { }
                }
            }

            if (CmbConfig.Items.Count > 0) CmbConfig.SelectedIndex = 0;

            _pollTimer.Interval = TimeSpan.FromMilliseconds(_config.PollIntervalMs);
            _pollTimer.Tick += PollTimer_Tick;

            UpdateVfoUi();
            PopulateFilterList();
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            TxtFreqMain.CaretIndex = 6;
            Dispatcher.BeginInvoke(new Action(UpdateMarkerPosition), DispatcherPriority.Loaded);
        }

        private void PopulateFilterList()
        {
            _isUpdatingFilter = true;
            CmbFilter.Items.Clear();

            if (_config.Filters.Count > 0)
            {
                foreach (var kvp in _config.Filters)
                {
                    CmbFilter.Items.Add(new ComboBoxItem { Content = kvp.Key, Tag = kvp.Value });
                }
            }
            else
            {
                // デフォルトのフィルタ帯域段階
                CmbFilter.Items.Add(new ComboBoxItem { Content = "250 Hz", Tag = "250" });
                CmbFilter.Items.Add(new ComboBoxItem { Content = "500 Hz", Tag = "500" });
                CmbFilter.Items.Add(new ComboBoxItem { Content = "1.8 kHz", Tag = "1800" });
                CmbFilter.Items.Add(new ComboBoxItem { Content = "2.4 kHz", Tag = "2400" });
                CmbFilter.Items.Add(new ComboBoxItem { Content = "2.8 kHz", Tag = "2800" });
                CmbFilter.Items.Add(new ComboBoxItem { Content = "3.0 kHz", Tag = "3000" });
            }
            _isUpdatingFilter = false;
        }

        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (_driver != null && _driver.IsOpen)
            {
                _pollTimer.Stop();
                _driver.Close();
                _driver = null;

                BtnConnect.Content = "接続";
                BtnConnect.Background = new SolidColorBrush(Color.FromRgb(2, 132, 199));
                LedStatus.Fill = new SolidColorBrush(Color.FromRgb(148, 163, 184));
                TxtStatus.Text = "切断";
                AppendLog("リグから切断しました。");
                return;
            }

            try
            {
                // ComboBoxItem の Tag からフルパスを確実に取得
                string configFile = "config.ini";
                if (CmbConfig.SelectedItem is ComboBoxItem item && item.Tag != null)
                {
                    configFile = item.Tag.ToString()!;
                }
                else if (CmbConfig.SelectedItem != null)
                {
                    configFile = CmbConfig.SelectedItem.ToString()!;
                }

                _config = RigConfig.LoadFromFile(configFile);

                if (CmbPort.SelectedItem != null)
                {
                    _config.PortName = CmbPort.SelectedItem.ToString()!;
                }

                // 設定ファイルから取得したポーリング間隔をタイマーに適用
                _pollTimer.Interval = TimeSpan.FromMilliseconds(_config.PollIntervalMs);

                PopulateFilterList();

                _driver = RigDriverFactory.Create(_config);
                _driver.Open();

                BtnConnect.Content = "切断";
                BtnConnect.Background = new SolidColorBrush(Color.FromRgb(180, 40, 40));
                LedStatus.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94));
                TxtStatus.Text = $"{_config.PortName} 接続中";

                await FetchCurrentInfoAsync();
                _pollTimer.Start();

                AppendLog($"接続成功: {_config.PortName} ({_config.Protocol}), 周期: {_config.PollIntervalMs}ms");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"接続エラー: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                AppendLog($"[接続エラー]: {ex.Message}");
            }
        }

        private async void PollTimer_Tick(object? sender, EventArgs e)
        {
            if (_driver == null || !_driver.IsOpen || _isBusy) return;

            _isBusy = true;
            try
            {
                long freqMain = 0;
                long freqSub = 0;
                string mode = "";
                string antenna = "";
                string bandwidth = "";
                int smeter = 0;
                int power = 0;
                int swr = 0;
                int alc = 0;
                bool isTx = false;
                bool isTuner = false;

                bool supportsDual = _driver.SupportsDualVfoRead;
                var currentVfo = _activeVfo;

                await Task.Run(() =>
                {
                    if (_driver == null || !_driver.IsOpen) return;

                    freqMain = _driver.GetFrequency(currentVfo);
                    if (supportsDual)
                    {
                        var otherVfo = currentVfo == VfoType.VfoA ? VfoType.VfoB : VfoType.VfoA;
                        freqSub = _driver.GetFrequency(otherVfo);
                    }
                    mode = _driver.GetMode(currentVfo);
                    antenna = _driver.GetAntenna(currentVfo);
                    bandwidth = _driver.GetBandwidth(currentVfo);

                    isTx = _driver.GetPtt();
                    isTuner = _driver.GetTuner();

                    // 通信負荷軽減とリアルタイム性の両立: RX時はSメーター、TX時はPO/SWR/ALCを取得
                    if (isTx)
                    {
                        power = _driver.GetPowerMeter();
                        swr = _driver.GetSwrMeter();
                        alc = _driver.GetAlcMeter();
                    }
                    else
                    {
                        smeter = _driver.GetSMeter();
                    }
                });

                if (freqMain > 0)
                {
                    if (currentVfo == VfoType.VfoA) _vfoAFreq = freqMain;
                    else _vfoBFreq = freqMain;

                    if (freqMain != _currentFreq)
                    {
                        _currentFreq = freqMain;
                        UpdateFrequencyDisplay(_currentFreq);
                        UpdateBandSelection(_currentFreq);
                    }
                }

                if (freqSub > 0)
                {
                    if (currentVfo == VfoType.VfoA) _vfoBFreq = freqSub;
                    else _vfoAFreq = freqSub;
                    TxtFreqSub.Text = $"{FormatFrequency(freqSub)} Hz";
                }
                else
                {
                    long subCache = currentVfo == VfoType.VfoA ? _vfoBFreq : _vfoAFreq;
                    if (subCache > 0)
                    {
                        TxtFreqSub.Text = $"{FormatFrequency(subCache)} Hz";
                    }
                }

                if (!string.IsNullOrEmpty(mode) && mode != _lastReadMode)
                {
                    _lastReadMode = mode;
                    if (currentVfo == VfoType.VfoA) _vfoAMode = mode;
                    else _vfoBMode = mode;

                    UpdateModeUi(mode);
                }

                if (!string.IsNullOrEmpty(antenna) && antenna != _lastReadAntenna)
                {
                    _lastReadAntenna = antenna;
                    if (currentVfo == VfoType.VfoA) _vfoAAntenna = antenna;
                    else _vfoBAntenna = antenna;

                    UpdateAntennaUi(antenna);
                }

                if (!string.IsNullOrEmpty(bandwidth) && bandwidth != _lastReadBandwidth)
                {
                    _lastReadBandwidth = bandwidth;
                    if (currentVfo == VfoType.VfoA) _vfoABandwidth = bandwidth;
                    else _vfoBBandwidth = bandwidth;

                    UpdateFilterUi(bandwidth);
                }

                // 4連メーターの同時更新
                PbSMeter.Value = Math.Clamp(smeter, 0, 255);
                PbPowerMeter.Value = Math.Clamp(power, 0, 255);
                PbSwrMeter.Value = Math.Clamp(swr, 0, 255);
                PbAlcMeter.Value = Math.Clamp(alc, 0, 255);

                // PTT & ATU スイッチ状態の反映
                if (isTx != _isTxActive)
                {
                    _isTxActive = isTx;
                    UpdatePttUi(_isTxActive);
                }

                if (isTuner != _isTunerActive)
                {
                    _isTunerActive = isTuner;
                    UpdateTunerUi(_isTunerActive);
                }
            }
            catch
            {
                // 通信エラー時は次の周期まで無視
            }
            finally
            {
                _isBusy = false;
            }
        }

        private async Task FetchCurrentInfoAsync()
        {
            if (_driver == null || !_driver.IsOpen) return;

            try
            {
                long freq = 0;
                string mode = "";
                string antenna = "";
                string bandwidth = "";
                bool isTx = false;
                bool isTuner = false;
                var vfo = _activeVfo;

                await Task.Run(() =>
                {
                    if (_driver != null && _driver.IsOpen)
                    {
                        freq = _driver.GetFrequency(vfo);
                        mode = _driver.GetMode(vfo);
                        antenna = _driver.GetAntenna(vfo);
                        bandwidth = _driver.GetBandwidth(vfo);
                        isTx = _driver.GetPtt();
                        isTuner = _driver.GetTuner();
                    }
                });

                if (freq > 0)
                {
                    _currentFreq = freq;
                    if (vfo == VfoType.VfoA) _vfoAFreq = freq;
                    else _vfoBFreq = freq;

                    UpdateFrequencyDisplay(_currentFreq);
                    UpdateBandSelection(_currentFreq);
                }

                if (!string.IsNullOrEmpty(mode))
                {
                    _lastReadMode = mode;
                    if (vfo == VfoType.VfoA) _vfoAMode = mode;
                    else _vfoBMode = mode;

                    UpdateModeUi(mode);
                }

                if (!string.IsNullOrEmpty(antenna))
                {
                    _lastReadAntenna = antenna;
                    if (vfo == VfoType.VfoA) _vfoAAntenna = antenna;
                    else _vfoBAntenna = antenna;

                    UpdateAntennaUi(antenna);
                }

                if (!string.IsNullOrEmpty(bandwidth))
                {
                    _lastReadBandwidth = bandwidth;
                    if (vfo == VfoType.VfoA) _vfoABandwidth = bandwidth;
                    else _vfoBBandwidth = bandwidth;

                    UpdateFilterUi(bandwidth);
                }

                _isTxActive = isTx;
                UpdatePttUi(_isTxActive);

                _isTunerActive = isTuner;
                UpdateTunerUi(_isTunerActive);

                long subFreq = vfo == VfoType.VfoA ? _vfoBFreq : _vfoAFreq;
                TxtFreqSub.Text = subFreq > 0 ? $"{FormatFrequency(subFreq)} Hz" : "---.---.--- Hz";
            }
            catch (Exception ex)
            {
                AppendLog($"[読込エラー]: {ex.Message}");
            }
        }

        private void UpdateModeUi(string activeMode)
        {
            var activeBg = new SolidColorBrush(Color.FromRgb(2, 132, 199));
            var activeFg = new SolidColorBrush(Color.FromRgb(255, 255, 255));
            var activeBorder = new SolidColorBrush(Color.FromRgb(2, 132, 199));

            var inactiveBg = new SolidColorBrush(Color.FromRgb(255, 255, 255));
            var inactiveFg = new SolidColorBrush(Color.FromRgb(51, 65, 85));
            var inactiveBorder = new SolidColorBrush(Color.FromRgb(203, 213, 225));

            foreach (var child in PanelModes.Children)
            {
                if (child is Button btn)
                {
                    string btnMode = btn.Content.ToString()!;
                    bool isMatch = btnMode.Equals(activeMode, StringComparison.OrdinalIgnoreCase) ||
                                   activeMode.StartsWith(btnMode + " ", StringComparison.OrdinalIgnoreCase) ||
                                   activeMode.StartsWith(btnMode + "(", StringComparison.OrdinalIgnoreCase) ||
                                   (activeMode.StartsWith("DATA-", StringComparison.OrdinalIgnoreCase) && btnMode.Equals(activeMode, StringComparison.OrdinalIgnoreCase));

                    btn.Background = isMatch ? activeBg : inactiveBg;
                    btn.Foreground = isMatch ? activeFg : inactiveFg;
                    btn.BorderBrush = isMatch ? activeBorder : inactiveBorder;
                }
            }
        }

        private void UpdateAntennaUi(string activeAntenna)
        {
            for (int i = 0; i < CmbAntenna.Items.Count; i++)
            {
                if (CmbAntenna.Items[i] is ComboBoxItem item && item.Tag != null)
                {
                    if (item.Tag.ToString()!.Equals(activeAntenna, StringComparison.OrdinalIgnoreCase))
                    {
                        if (CmbAntenna.SelectedIndex != i)
                        {
                            _isUpdatingAntenna = true;
                            CmbAntenna.SelectedIndex = i;
                            _isUpdatingAntenna = false;
                        }
                        break;
                    }
                }
            }
        }

        private void UpdateFilterUi(string activeBandwidth)
        {
            _isUpdatingFilter = true;
            bool matched = false;

            for (int i = 0; i < CmbFilter.Items.Count; i++)
            {
                if (CmbFilter.Items[i] is ComboBoxItem item)
                {
                    string tagStr = item.Tag?.ToString() ?? "";
                    string contentStr = item.Content?.ToString() ?? "";

                    if (tagStr.Equals(activeBandwidth, StringComparison.OrdinalIgnoreCase) ||
                        contentStr.Equals(activeBandwidth, StringComparison.OrdinalIgnoreCase) ||
                        contentStr.StartsWith(activeBandwidth, StringComparison.OrdinalIgnoreCase))
                    {
                        CmbFilter.SelectedIndex = i;
                        matched = true;
                        break;
                    }
                }
            }

            // 段階の選択肢に完全一致しない場合は直接テキストに反映
            if (!matched && !string.IsNullOrEmpty(activeBandwidth))
            {
                CmbFilter.Text = $"{activeBandwidth} Hz";
            }
            _isUpdatingFilter = false;
        }

        private void UpdatePttUi(bool isTx)
        {
            if (isTx)
            {
                BtnPtt.Content = "TX [送信中]";
                BtnPtt.Background = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // 赤
                BtnPtt.Foreground = new SolidColorBrush(Color.FromRgb(255, 255, 255));
                BtnPtt.BorderBrush = new SolidColorBrush(Color.FromRgb(220, 38, 38));
            }
            else
            {
                BtnPtt.Content = "RX [受信]";
                BtnPtt.Background = new SolidColorBrush(Color.FromRgb(226, 232, 240)); // ライトグレー
                BtnPtt.Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85));
                BtnPtt.BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225));
            }
        }

        private void UpdateTunerUi(bool isTuner)
        {
            if (isTuner)
            {
                BtnTuner.Content = "ATU: ON";
                BtnTuner.Background = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // 緑
                BtnTuner.Foreground = new SolidColorBrush(Color.FromRgb(255, 255, 255));
                BtnTuner.BorderBrush = new SolidColorBrush(Color.FromRgb(5, 150, 105));
            }
            else
            {
                BtnTuner.Content = "ATU: OFF";
                BtnTuner.Background = new SolidColorBrush(Color.FromRgb(226, 232, 240));
                BtnTuner.Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85));
                BtnTuner.BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225));
            }
        }

        private async void BtnPtt_Click(object sender, RoutedEventArgs e)
        {
            if (_driver == null || !_driver.IsOpen) return;

            bool targetTx = !_isTxActive;
            try
            {
                await Task.Run(() => _driver.SetPtt(targetTx));
                _isTxActive = targetTx;
                UpdatePttUi(_isTxActive);
                AppendLog(targetTx ? "PTT ON (送信開始)" : "PTT OFF (受信開始)");
            }
            catch (Exception ex)
            {
                AppendLog($"[PTT制御エラー]: {ex.Message}");
            }
        }

        private async void BtnTuner_Click(object sender, RoutedEventArgs e)
        {
            if (_driver == null || !_driver.IsOpen) return;

            bool targetTuner = !_isTunerActive;
            try
            {
                await Task.Run(() => _driver.SetTuner(targetTuner));
                _isTunerActive = targetTuner;
                UpdateTunerUi(_isTunerActive);
                AppendLog(targetTuner ? "アンテナチューナ ON" : "アンテナチューナ OFF");
            }
            catch (Exception ex)
            {
                AppendLog($"[チューナ制御エラー]: {ex.Message}");
            }
        }

        private async void CmbFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_driver == null || !_driver.IsOpen || _isUpdatingFilter) return;

            string selectedValue = "";
            if (CmbFilter.SelectedItem is ComboBoxItem item)
            {
                selectedValue = item.Tag?.ToString() ?? item.Content?.ToString() ?? "";
            }
            else if (!string.IsNullOrEmpty(CmbFilter.Text))
            {
                selectedValue = CmbFilter.Text.Replace("Hz", "").Trim();
            }

            if (!string.IsNullOrEmpty(selectedValue))
            {
                try
                {
                    _isBusy = true;
                    await Task.Run(() => _driver.SetBandwidth(_activeVfo, selectedValue));
                    _lastReadBandwidth = selectedValue;
                    AppendLog($"フィルタ帯域設定: {selectedValue} ({_activeVfo})");
                }
                catch (Exception ex)
                {
                    AppendLog($"[帯域設定エラー]: {ex.Message}");
                }
                finally
                {
                    _isBusy = false;
                }
            }
        }

        private void UpdateBandSelection(long freq)
        {
            int targetIndex = freq switch
            {
                >= 1800000 and <= 1999999 => 0,
                >= 3500000 and <= 3800000 => 1,
                >= 7000000 and <= 7200000 => 2,
                >= 10100000 and <= 10150000 => 3,
                >= 14000000 and <= 14350000 => 4,
                >= 18068000 and <= 18168000 => 5,
                >= 21000000 and <= 21450000 => 6,
                >= 24890000 and <= 24990000 => 7,
                >= 28000000 and <= 29700000 => 8,
                >= 50000000 and <= 54000000 => 9,
                >= 144000000 and <= 146000000 => 10,
                >= 430000000 and <= 440000000 => 11,
                _ => -1
            };

            if (targetIndex >= 0 && CmbBand.SelectedIndex != targetIndex)
            {
                _isUpdatingBand = true;
                CmbBand.SelectedIndex = targetIndex;
                _isUpdatingBand = false;
            }
        }

        private void TxtFreqMain_SelectionChanged(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingText) return;
            UpdateMarkerPosition();
        }

        private void TxtFreqMain_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            TxtFreqMain.Focus();
            Point pt = e.GetPosition(TxtFreqMain);
            int charIndex = TxtFreqMain.GetCharacterIndexFromPoint(pt, true);
            if (charIndex >= 0)
            {
                var (targetIndex, _, _) = GetDigitInfo(charIndex);
                TxtFreqMain.CaretIndex = targetIndex;
                UpdateMarkerPosition();
            }
        }

        private void UpdateMarkerPosition()
        {
            int caret = TxtFreqMain.CaretIndex;
            var (charIndex, _, _) = GetDigitInfo(caret);
            Rect rect = TxtFreqMain.GetRectFromCharacterIndex(charIndex);
            if (rect == Rect.Empty) return;

            double charWidth = 26.0;
            if (charIndex + 1 <= TxtFreqMain.Text.Length)
            {
                Rect rectNext = TxtFreqMain.GetRectFromCharacterIndex(charIndex + 1);
                if (rectNext != Rect.Empty && rectNext.Left > rect.Left)
                {
                    charWidth = rectNext.Left - rect.Left;
                }
            }

            TxtMarker.Visibility = Visibility.Visible;
            TxtMarker.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double markerWidth = TxtMarker.DesiredSize.Width;
            double left = rect.Left + (charWidth / 2.0) - (markerWidth / 2.0);
            Canvas.SetLeft(TxtMarker, Math.Max(0, left));
        }

        private async void TxtFreqMain_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Up || e.Key == Key.Down)
            {
                e.Handled = true;
                if (_driver == null || !_driver.IsOpen) return;

                int caret = TxtFreqMain.CaretIndex;
                var (charIndex, weight, _) = GetDigitInfo(caret);

                _currentFreq += (e.Key == Key.Up) ? weight : -weight;
                if (_currentFreq < 0) _currentFreq = 0;

                UpdateFrequencyDisplay(_currentFreq);
                UpdateBandSelection(_currentFreq);
                TxtFreqMain.CaretIndex = charIndex;
                UpdateMarkerPosition();

                await ApplyFrequencyAsync(_currentFreq);
            }
            else if (e.Key == Key.Left)
            {
                e.Handled = true;
                int cur = TxtFreqMain.CaretIndex;
                int next = cur - 1;
                if (next == 3 || next == 7) next--;
                if (next < 0) next = 0;
                TxtFreqMain.CaretIndex = next;
                UpdateMarkerPosition();
            }
            else if (e.Key == Key.Right)
            {
                e.Handled = true;
                int cur = TxtFreqMain.CaretIndex;
                int next = cur + 1;
                if (next == 3 || next == 7) next++;
                if (next > 10) next = 10;
                TxtFreqMain.CaretIndex = next;
                UpdateMarkerPosition();
            }
        }

        private async void TxtFreqMain_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_driver == null || !_driver.IsOpen) return;

            int caret = TxtFreqMain.CaretIndex;
            var (charIndex, weight, _) = GetDigitInfo(caret);

            _currentFreq += (e.Delta > 0) ? weight : -weight;
            if (_currentFreq < 0) _currentFreq = 0;

            UpdateFrequencyDisplay(_currentFreq);
            UpdateBandSelection(_currentFreq);
            TxtFreqMain.CaretIndex = charIndex;
            UpdateMarkerPosition();

            await ApplyFrequencyAsync(_currentFreq);
        }

        private static (int charIndex, long weight, string name) GetDigitInfo(int caret)
        {
            return caret switch
            {
                0 => (0, 100_000_000, "100 MHz"),
                1 => (1, 10_000_000, "10 MHz"),
                2 => (2, 1_000_000, "1 MHz"),
                3 => (2, 1_000_000, "1 MHz"),
                4 => (4, 100_000, "100 kHz"),
                5 => (5, 10_000, "10 kHz"),
                6 => (6, 1_000, "1 kHz"),
                7 => (6, 1_000, "1 kHz"),
                8 => (8, 100, "100 Hz"),
                9 => (9, 10, "10 Hz"),
                _ => (10, 1, "1 Hz")
            };
        }

        private void UpdateFrequencyDisplay(long freq)
        {
            int caret = TxtFreqMain.CaretIndex;
            _isUpdatingText = true;
            TxtFreqMain.Text = FormatFrequency(freq);
            _isUpdatingText = false;

            if (caret >= 0)
            {
                TxtFreqMain.CaretIndex = Math.Min(caret, TxtFreqMain.Text.Length);
            }
            UpdateMarkerPosition();
        }

        private static string FormatFrequency(long freq)
        {
            long mhz = freq / 1_000_000;
            long khz = (freq % 1_000_000) / 1_000;
            long hz = freq % 1_000;
            return $"{mhz:D3}.{khz:D3}.{hz:D3}";
        }

        private async Task ApplyFrequencyAsync(long freq)
        {
            if (_driver == null || !_driver.IsOpen) return;
            var targetVfo = _activeVfo;

            try
            {
                await Task.Run(() =>
                {
                    if (_driver != null && _driver.IsOpen)
                    {
                        _driver.SetFrequency(targetVfo, freq);
                    }
                });

                if (targetVfo == VfoType.VfoA) _vfoAFreq = freq;
                else _vfoBFreq = freq;

                AppendLog($"周波数設定: {freq:N0} Hz ({(freq / 1_000_000.0):F6} MHz)");
            }
            catch (Exception ex)
            {
                AppendLog($"[周波数設定エラー]: {ex.Message}");
            }
        }

        private async void CmbBand_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_driver == null || !_driver.IsOpen || _isUpdatingBand) return;

            if (CmbBand.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                string bandKey = item.Tag.ToString()!;
                try
                {
                    _isBusy = true;
                    await Task.Run(() => _driver.SelectBand(_activeVfo, bandKey));
                    await Task.Delay(100);
                    AppendLog($"バンド切替: {item.Content} ({_activeVfo})");
                    await FetchCurrentInfoAsync();
                }
                catch (Exception ex)
                {
                    AppendLog($"[バンド切替エラー]: {ex.Message}");
                }
                finally
                {
                    _isBusy = false;
                }
            }
        }

        private async void ModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_driver == null || !_driver.IsOpen) return;

            if (sender is Button btn)
            {
                string mode = btn.Content.ToString()!;
                try
                {
                    await Task.Run(() => _driver.SetMode(_activeVfo, mode));
                    _lastReadMode = mode;

                    if (_activeVfo == VfoType.VfoA) _vfoAMode = mode;
                    else _vfoBMode = mode;

                    UpdateModeUi(mode);
                    AppendLog($"モード変更: {mode} ({_activeVfo})");
                }
                catch (Exception ex)
                {
                    AppendLog($"[モード変更エラー]: {ex.Message}");
                }
            }
        }

        private async void BtnVfoA_Click(object sender, RoutedEventArgs e)
        {
            if (_activeVfo == VfoType.VfoA) return;

            _activeVfo = VfoType.VfoA;
            UpdateVfoUi();

            if (_driver != null && _driver.IsOpen)
            {
                _isBusy = true;
                try
                {
                    await Task.Run(() => _driver.SelectVfo(VfoType.VfoA));
                    await Task.Delay(60);
                    AppendLog("VFO-A を選択");
                    await FetchCurrentInfoAsync();
                }
                catch (Exception ex)
                {
                    AppendLog($"[VFO-A 選択エラー]: {ex.Message}");
                }
                finally
                {
                    _isBusy = false;
                }
            }
        }

        private async void BtnVfoB_Click(object sender, RoutedEventArgs e)
        {
            if (_activeVfo == VfoType.VfoB) return;

            _activeVfo = VfoType.VfoB;
            UpdateVfoUi();

            if (_driver != null && _driver.IsOpen)
            {
                _isBusy = true;
                try
                {
                    await Task.Run(() => _driver.SelectVfo(VfoType.VfoB));
                    await Task.Delay(60);
                    AppendLog("VFO-B を選択");
                    await FetchCurrentInfoAsync();
                }
                catch (Exception ex)
                {
                    AppendLog($"[VFO-B 選択エラー]: {ex.Message}");
                }
                finally
                {
                    _isBusy = false;
                }
            }
        }

        private void UpdateVfoUi()
        {
            var activeBg = new SolidColorBrush(Color.FromRgb(2, 132, 199));
            var activeFg = new SolidColorBrush(Color.FromRgb(255, 255, 255));
            var activeBorder = new SolidColorBrush(Color.FromRgb(2, 132, 199));

            var inactiveBg = new SolidColorBrush(Color.FromRgb(255, 255, 255));
            var inactiveFg = new SolidColorBrush(Color.FromRgb(51, 65, 85));
            var inactiveBorder = new SolidColorBrush(Color.FromRgb(203, 213, 225));

            if (_activeVfo == VfoType.VfoB)
            {
                BtnVfoA.Background = inactiveBg;
                BtnVfoA.Foreground = inactiveFg;
                BtnVfoA.BorderBrush = inactiveBorder;

                BtnVfoB.Background = activeBg;
                BtnVfoB.Foreground = activeFg;
                BtnVfoB.BorderBrush = activeBorder;

                TxtVfoTitle.Text = "VFO-B (Hz)";
                TxtSubVfoLabel.Text = "VFO-A: ";
                UpdateAntennaUi(_vfoBAntenna);
                UpdateFilterUi(_vfoBBandwidth);
            }
            else
            {
                BtnVfoA.Background = activeBg;
                BtnVfoA.Foreground = activeFg;
                BtnVfoA.BorderBrush = activeBorder;

                BtnVfoB.Background = inactiveBg;
                BtnVfoB.Foreground = inactiveFg;
                BtnVfoB.BorderBrush = inactiveBorder;

                TxtVfoTitle.Text = "VFO-A (Hz)";
                TxtSubVfoLabel.Text = "VFO-B: ";
                UpdateAntennaUi(_vfoAAntenna);
                UpdateFilterUi(_vfoABandwidth);
            }
        }

        private async void CmbAntenna_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_driver == null || !_driver.IsOpen || _isUpdatingAntenna) return;

            if (CmbAntenna.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                string antIndex = item.Tag.ToString()!;
                try
                {
                    _isBusy = true;
                    await Task.Run(() => _driver.SetAntenna(_activeVfo, antIndex));
                    _lastReadAntenna = antIndex;
                    if (_activeVfo == VfoType.VfoA) _vfoAAntenna = antIndex;
                    else _vfoBAntenna = antIndex;

                    AppendLog($"アンテナ切替: {item.Content} ({_activeVfo})");
                }
                catch (Exception ex)
                {
                    AppendLog($"[アンテナ切替エラー]: {ex.Message}");
                }
                finally
                {
                    _isBusy = false;
                }
            }
        }

        private void AppendLog(string message)
        {
            TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
            TxtLog.ScrollToEnd();
        }

        protected override void OnClosed(EventArgs e)
        {
            _pollTimer.Stop();
            _driver?.Dispose();
            base.OnClosed(e);
        }
    }
}