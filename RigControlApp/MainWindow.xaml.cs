using System;
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

        // VFO-A / VFO-B ごとの周波数・モードの個別管理
        private long _currentFreq = 14074000;
        private long _vfoAFreq = 14074000;
        private long _vfoBFreq = 7074000;
        private string _vfoAMode = "USB";
        private string _vfoBMode = "LSB";

        private VfoType _activeVfo = VfoType.VfoA;
        private bool _isBusy = false;
        private bool _isUpdatingText = false;
        private bool _isUpdatingBand = false;
        private string _lastReadMode = "";

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

            // .ini ファイルの探索
            var configs = Directory.GetFiles(".", "config*.ini");
            foreach (var f in configs) CmbConfig.Items.Add(Path.GetFileName(f));
            if (CmbConfig.Items.Count > 0) CmbConfig.SelectedIndex = 0;

            _pollTimer.Interval = TimeSpan.FromMilliseconds(500);
            _pollTimer.Tick += PollTimer_Tick;

            UpdateVfoUi();
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            TxtFreqMain.CaretIndex = 6;
            Dispatcher.BeginInvoke(new Action(UpdateMarkerPosition), DispatcherPriority.Loaded);
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
                TxtStatus.Text = "未接続";
                AppendLog("切断しました。");
                return;
            }

            try
            {
                string configFile = CmbConfig.SelectedItem?.ToString() ?? "config.ini";
                _config = RigConfig.LoadFromFile(configFile);

                if (CmbPort.SelectedItem != null)
                {
                    _config.PortName = CmbPort.SelectedItem.ToString()!;
                }

                // ドライバの生成とオープン
                _driver = RigDriverFactory.Create(_config);
                _driver.Open();

                BtnConnect.Content = "切断";
                BtnConnect.Background = new SolidColorBrush(Color.FromRgb(180, 40, 40));
                LedStatus.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94));
                TxtStatus.Text = $"{_config.PortName} 接続中";

                await FetchCurrentFrequencyAsync();
                _pollTimer.Start();

                AppendLog($"接続成功: {_config.PortName} ({_config.Protocol})");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"接続に失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                AppendLog($"[エラー] 接続失敗: {ex.Message}");
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
                int smeter = 0;
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
                    mode = _driver.GetMode();
                    smeter = _driver.GetSMeter();
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

                PbSMeter.Value = Math.Clamp(smeter, 0, 255);
            }
            catch
            {
                // 通信タイムアウト等は無視して次回ポーリングに委ねる
            }
            finally
            {
                _isBusy = false;
            }
        }

        private async Task FetchCurrentFrequencyAsync()
        {
            if (_driver == null || !_driver.IsOpen) return;

            try
            {
                long freq = 0;
                string mode = "";
                var vfo = _activeVfo;

                await Task.Run(() =>
                {
                    if (_driver != null && _driver.IsOpen)
                    {
                        freq = _driver.GetFrequency(vfo);
                        mode = _driver.GetMode();
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

                long subFreq = vfo == VfoType.VfoA ? _vfoBFreq : _vfoAFreq;
                TxtFreqSub.Text = subFreq > 0 ? $"{FormatFrequency(subFreq)} Hz" : "---.---.--- Hz";
            }
            catch (Exception ex)
            {
                AppendLog($"[エラー] 状態取得失敗: {ex.Message}");
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

            double charWidth = 22.0;
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
                AppendLog($"[エラー] 周波数設定失敗: {ex.Message}");
            }
        }

        // バンド切替コマンドを用いたバンド変更処理
        private async void CmbBand_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_driver == null || !_driver.IsOpen || _isUpdatingBand) return;

            if (CmbBand.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                string bandKey = item.Tag.ToString()!;
                try
                {
                    _isBusy = true;
                    await Task.Run(() =>
                    {
                        _driver.SelectBand(bandKey);
                        Task.Delay(100).Wait(); // リグ側のバンド切り替え完了を待機
                    });

                    AppendLog($"バンド切替: {item.Content} (コマンド送信)");
                    await FetchCurrentFrequencyAsync();
                }
                catch (Exception ex)
                {
                    AppendLog($"[エラー] バンド切替失敗: {ex.Message}");
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
                    await Task.Run(() => _driver.SetMode(mode));
                    _lastReadMode = mode;
                    if (_activeVfo == VfoType.VfoA) _vfoAMode = mode;
                    else _vfoBMode = mode;

                    UpdateModeUi(mode);
                    AppendLog($"モード設定: {mode}");
                }
                catch (Exception ex)
                {
                    AppendLog($"[エラー] モード '{mode}' 設定失敗: {ex.Message}");
                }
            }
        }

        // VFO-A 切り替え処理
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
                    await Task.Run(() =>
                    {
                        _driver.SelectVfo(VfoType.VfoA);
                        Task.Delay(60).Wait(); // リグ側のVFO切替処理を確実に待機
                    });

                    AppendLog("VFO-A に切り替えました");
                    await FetchCurrentFrequencyAsync();
                }
                catch (Exception ex)
                {
                    AppendLog($"[エラー] VFO-A 切り替え失敗: {ex.Message}");
                }
                finally
                {
                    _isBusy = false;
                }
            }
        }

        // VFO-B 切り替え処理
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
                    await Task.Run(() =>
                    {
                        _driver.SelectVfo(VfoType.VfoB);
                        Task.Delay(60).Wait(); // リグ側のVFO切替処理を確実に待機
                    });

                    AppendLog("VFO-B に切り替えました");
                    await FetchCurrentFrequencyAsync();
                }
                catch (Exception ex)
                {
                    AppendLog($"[エラー] VFO-B 切り替え失敗: {ex.Message}");
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
            }
        }

        private async void CmbAntenna_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_driver == null || !_driver.IsOpen) return;

            if (CmbAntenna.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                string tag = item.Tag.ToString()!;
                if (_config.Commands.TryGetValue(tag, out string? cmd) && !string.IsNullOrEmpty(cmd))
                {
                    try
                    {
                        await Task.Run(() => _driver.SendRawCommand(cmd));
                        AppendLog($"アンテナ設定: {item.Content} (コマンド: {cmd})");
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"[エラー] アンテナ設定失敗: {ex.Message}");
                    }
                }
                else
                {
                    AppendLog($"[警告] アンテナ設定 {tag} が定義されていません。");
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