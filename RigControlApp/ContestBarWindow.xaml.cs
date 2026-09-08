using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace RigControlApp
{
    /// <summary>
    /// コンテスト運用向け横長タスクバー画面
    /// </summary>
    public partial class ContestBarWindow : Window
    {
        private readonly MainWindow _mainWindow;
        private long _currentFreq = 14074000;
        private bool _isUpdatingText = false;
        private bool _isUpdatingAntenna = false;
        private bool _isTxActive = false;

        public ContestBarWindow(MainWindow mainWindow)
        {
            InitializeComponent();
            _mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
            Loaded += ContestBarWindow_Loaded;
        }

        private void ContestBarWindow_Loaded(object sender, RoutedEventArgs e)
        {
            TxtFreqMain.CaretIndex = 6;
            Dispatcher.BeginInvoke(new Action(UpdateMarkerPosition), DispatcherPriority.Loaded);
        }

        /// <summary>
        /// 画面どこでもドラッグで移動可能
        /// </summary>
        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        /// <summary>
        /// 詳細（通常）画面から現在の無線機状態を同期
        /// </summary>
        public void SyncStateFromMain()
        {
            _currentFreq = _mainWindow.CurrentFreq;
            UpdateFrequencyDisplay(_currentFreq);
            TxtCurrentMode.Text = _mainWindow.CurrentMode;
            UpdateAntennaUi(_mainWindow.CurrentAntenna);
            UpdatePttUi(_mainWindow.IsTxActive);
            UpdateVfoUi(_mainWindow.ActiveVfo);
        }

        /// <summary>
        /// ポーリング周期ごとの表示更新
        /// </summary>
        public void UpdateStatus(long freq, string mode, string antenna, int smeter, int power, int swr, int alc, bool isTx, VfoType vfo)
        {
            if (freq > 0 && freq != _currentFreq)
            {
                _currentFreq = freq;
                UpdateFrequencyDisplay(_currentFreq);
            }

            if (!string.IsNullOrEmpty(mode) && TxtCurrentMode.Text != mode)
            {
                TxtCurrentMode.Text = mode;
            }

            if (!string.IsNullOrEmpty(antenna))
            {
                UpdateAntennaUi(antenna);
            }

            if (isTx != _isTxActive)
            {
                _isTxActive = isTx;
                UpdatePttUi(_isTxActive);
            }

            UpdateVfoUi(vfo);

            PbSMeter.Value = Math.Clamp(smeter, 0, 255);
            PbPowerMeter.Value = Math.Clamp(power, 0, 255);
            PbSwrMeter.Value = Math.Clamp(swr, 0, 255);
            PbAlcMeter.Value = Math.Clamp(alc, 0, 255);
        }

        private void UpdatePttUi(bool isTx)
        {
            _isTxActive = isTx;
            if (isTx)
            {
                BtnPtt.Content = "TX [送信]";
                BtnPtt.Background = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // 赤
                BtnPtt.Foreground = new SolidColorBrush(Color.FromRgb(255, 255, 255));
                BtnPtt.BorderBrush = new SolidColorBrush(Color.FromRgb(220, 38, 38));
            }
            else
            {
                BtnPtt.Content = "RX [受信]";
                BtnPtt.Background = new SolidColorBrush(Color.FromRgb(226, 232, 240));
                BtnPtt.Foreground = new SolidColorBrush(Color.FromRgb(30, 41, 59));
                BtnPtt.BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225));
            }
        }

        private void UpdateVfoUi(VfoType vfo)
        {
            var activeBg = new SolidColorBrush(Color.FromRgb(2, 132, 199));
            var activeFg = new SolidColorBrush(Color.FromRgb(255, 255, 255));
            var inactiveBg = Brushes.Transparent;
            var inactiveFg = new SolidColorBrush(Color.FromRgb(100, 116, 139));

            if (vfo == VfoType.VfoA)
            {
                BtnVfoA.Background = activeBg;
                BtnVfoA.Foreground = activeFg;
                BtnVfoB.Background = inactiveBg;
                BtnVfoB.Foreground = inactiveFg;
            }
            else
            {
                BtnVfoA.Background = inactiveBg;
                BtnVfoA.Foreground = inactiveFg;
                BtnVfoB.Background = activeBg;
                BtnVfoB.Foreground = activeFg;
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

        private async void BtnPtt_Click(object sender, RoutedEventArgs e)
        {
            await _mainWindow.TogglePttFromContestAsync();
        }

        private async void BtnVfoA_Click(object sender, RoutedEventArgs e)
        {
            await _mainWindow.SwitchVfoFromContestAsync(VfoType.VfoA);
        }

        private async void BtnVfoB_Click(object sender, RoutedEventArgs e)
        {
            await _mainWindow.SwitchVfoFromContestAsync(VfoType.VfoB);
        }

        private async void CmbAntenna_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingAntenna) return;
            if (CmbAntenna.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                await _mainWindow.SetAntennaFromContestAsync(item.Tag.ToString()!);
            }
        }

        private void BtnReturnDetail_Click(object sender, RoutedEventArgs e)
        {
            _mainWindow.ReturnFromContest();
        }

        private void BtnTopmost_Click(object sender, RoutedEventArgs e)
        {
            Topmost = BtnTopmost.IsChecked == true;
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            _mainWindow.Close();
        }

        // --- 周波数操作 & 桁マーカー処理 (MainWindow 準拠) ---
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

            double charWidth = 18.0;
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
                int caret = TxtFreqMain.CaretIndex;
                var (charIndex, weight, _) = GetDigitInfo(caret);

                _currentFreq += (e.Key == Key.Up) ? weight : -weight;
                if (_currentFreq < 0) _currentFreq = 0;

                UpdateFrequencyDisplay(_currentFreq);
                TxtFreqMain.CaretIndex = charIndex;
                UpdateMarkerPosition();

                await _mainWindow.ApplyFrequencyFromContestAsync(_currentFreq);
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
            int caret = TxtFreqMain.CaretIndex;
            var (charIndex, weight, _) = GetDigitInfo(caret);

            _currentFreq += (e.Delta > 0) ? weight : -weight;
            if (_currentFreq < 0) _currentFreq = 0;

            UpdateFrequencyDisplay(_currentFreq);
            TxtFreqMain.CaretIndex = charIndex;
            UpdateMarkerPosition();

            await _mainWindow.ApplyFrequencyFromContestAsync(_currentFreq);
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
            long mhz = freq / 1_000_000;
            long khz = (freq % 1_000_000) / 1_000;
            long hz = freq % 1_000;
            TxtFreqMain.Text = $"{mhz:D3}.{khz:D3}.{hz:D3}";
            _isUpdatingText = false;

            if (caret >= 0)
            {
                TxtFreqMain.CaretIndex = Math.Min(caret, TxtFreqMain.Text.Length);
            }
            UpdateMarkerPosition();
        }

        private async void Meter_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement elem && elem.Tag is string meterType)
            {
                await _mainWindow.SelectMeterAsync(meterType);
            }
        }
    }
}