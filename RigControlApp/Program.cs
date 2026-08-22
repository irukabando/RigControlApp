using System;
using System.IO;
using System.Threading;

namespace RigControlApp
{
    internal class Program
    {
        private static IRigDriver? _driver;
        private static RigConfig _config = new();

        static void Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("==========================================================");
            Console.WriteLine("       Amateur Radio Universal Rig Controller (CLI)       ");
            Console.WriteLine("      対応プロトコル: Yaesu / Kenwood (ASCII) / Icom (CI-V) ");
            Console.WriteLine("==========================================================");

            string configFile = "config.ini";
            if (args.Length > 0 && File.Exists(args[0]))
            {
                configFile = args[0];
            }
            else if (!File.Exists(configFile))
            {
                if (File.Exists("config/kenwood.ini")) configFile = "config/kenwood.ini";
                else if (File.Exists("config/yaesu.ini")) configFile = "config/yaesu.ini";
                else if (File.Exists("config/icom.ini")) configFile = "config/icom.ini";
            }

            LoadAndInitConfig(configFile);

            bool exit = false;
            while (!exit)
            {
                PrintMainMenu();
                Console.Write("操作を選択 > ");
                string? choice = Console.ReadLine()?.Trim();

                try
                {
                    switch (choice)
                    {
                        case "1":
                            SelectAndLoadConfig();
                            break;
                        case "2":
                            HandleFrequencyA();
                            break;
                        case "3":
                            HandleFrequencyB();
                            break;
                        case "4":
                            HandleMode();
                            break;
                        case "5":
                            HandlePtt();
                            break;
                        case "6":
                            HandleRigState();
                            break;
                        case "7":
                            HandleSMeter();
                            break;
                        case "8":
                            HandleAfGain();
                            break;
                        case "9":
                            HandlePollingMonitor();
                            break;
                        case "10":
                            HandleRawCommand();
                            break;
                        case "0":
                            exit = true;
                            break;
                        default:
                            Console.WriteLine("無効な選択肢です。");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[エラー]: {ex.Message}");
                    Console.ResetColor();
                }

                if (!exit)
                {
                    Console.WriteLine("\n[Enter]キーを押してメニューに戻ります...");
                    Console.ReadLine();
                }
            }

            _driver?.Close();
            Console.WriteLine("終了しました。");
        }

        private static void PrintMainMenu()
        {
            Console.Clear();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("==========================================================");
            Console.WriteLine($"接続先ポート: {_config.PortName} ({_config.BaudRate} bps) | プロトコル: {_config.Protocol}");
            Console.WriteLine($"接続状態: {(_driver != null && _driver.IsOpen ? "オンライン (ONLINE)" : "オフライン (OFFLINE)")}");
            Console.WriteLine("==========================================================");
            Console.ResetColor();

            Console.WriteLine(" [1] 設定ファイル再選択 / COMポート再接続");
            Console.WriteLine(" [2] VFO-A 周波数設定 (FA)");
            Console.WriteLine(" [3] VFO-B 周波数設定 (FB)");
            Console.WriteLine(" [4] 動作モード切替 (MD: USB, LSB, CW, FM, AM...)");
            Console.WriteLine(" [5] 送受信切替 (PTT: TX / RX)");
            Console.WriteLine(" [6] トランシーバ状態取得 (IF / State)");
            Console.WriteLine(" [7] Sメーター値取得 (SM)");
            Console.WriteLine(" [8] AFゲイン設定 (AG)");
            Console.WriteLine(" [9] リアルタイム状態監視 (ポーリングモニタ)");
            Console.WriteLine(" [10] コマンド直接送信 (CAT/CI-V Raw)");
            Console.WriteLine(" [0] 終了");
        }

        private static void LoadAndInitConfig(string path)
        {
            try
            {
                _driver?.Close();
                _config = RigConfig.LoadFromFile(path);
                Console.WriteLine($"設定読込完了: {Path.GetFullPath(path)}");

                _driver = RigDriverFactory.Create(_config);
                _driver.Open();

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"COMポート {_config.PortName} に接続しました。");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[設定/接続警告]: {ex.Message}");
                Console.WriteLine("メニュー [1] から設定ファイルまたはCOMポートを再選択してください。");
                Console.ResetColor();
            }
        }

        private static void SelectAndLoadConfig()
        {
            Console.WriteLine("設定ファイル一覧:");
            var files = Directory.GetFiles(".", "*.ini", SearchOption.AllDirectories);
            for (int i = 0; i < files.Length; i++)
            {
                Console.WriteLine($" [{i + 1}] {files[i]}");
            }
            Console.Write("番号またはファイルパスを入力 > ");
            string? input = Console.ReadLine()?.Trim();

            if (int.TryParse(input, out int idx) && idx >= 1 && idx <= files.Length)
            {
                LoadAndInitConfig(files[idx - 1]);
            }
            else if (!string.IsNullOrEmpty(input) && File.Exists(input))
            {
                LoadAndInitConfig(input);
            }
            else
            {
                Console.WriteLine("指定されたファイルが見つかりません。");
            }
        }

        private static void HandleFrequencyA()
        {
            EnsureOpen();
            long currentFreq = _driver!.GetFrequency(VfoType.VfoA);
            Console.WriteLine($"[現在の VFO-A 周波数]: {currentFreq:N0} Hz ({(currentFreq / 1_000_000.0):F6} MHz)");
            Console.Write("設定する周波数 (Hz単位 または 14.074 等のMHz) > ");
            string? input = Console.ReadLine()?.Trim();

            if (!string.IsNullOrEmpty(input))
            {
                long newFreq = ParseFrequencyInput(input);
                if (newFreq > 0)
                {
                    _driver.SetFrequency(VfoType.VfoA, newFreq);
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"VFO-A を {newFreq:N0} Hz に設定しました。");
                    Console.ResetColor();
                }
            }
        }

        private static void HandleFrequencyB()
        {
            EnsureOpen();
            long currentFreq = _driver!.GetFrequency(VfoType.VfoB);
            Console.WriteLine($"[現在の VFO-B 周波数]: {currentFreq:N0} Hz ({(currentFreq / 1_000_000.0):F6} MHz)");
            Console.Write("設定する周波数 (Hz単位 または 7.041 等のMHz) > ");
            string? input = Console.ReadLine()?.Trim();

            if (!string.IsNullOrEmpty(input))
            {
                long newFreq = ParseFrequencyInput(input);
                if (newFreq > 0)
                {
                    _driver.SetFrequency(VfoType.VfoB, newFreq);
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"VFO-B を {newFreq:N0} Hz に設定しました。");
                    Console.ResetColor();
                }
            }
        }

        private static void HandleMode()
        {
            EnsureOpen();
            string currentMode = _driver!.GetMode(VfoType.VfoA);
            Console.WriteLine($"[現在のモード]: {currentMode}");
            Console.WriteLine("利用可能モード:");
            foreach (var kvp in _config.ModeMap)
            {
                Console.Write($" [{kvp.Key}]");
            }
            Console.WriteLine();
            Console.Write("設定するモード名を入力 > ");
            string? input = Console.ReadLine()?.Trim().ToUpperInvariant();

            if (!string.IsNullOrEmpty(input))
            {
                _driver.SetMode(VfoType.VfoA, input);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"モードを {input} に設定しました。");
                Console.ResetColor();
            }
        }

        private static void HandlePtt()
        {
            EnsureOpen();
            Console.WriteLine("[PTT 送受信制御]");
            Console.WriteLine(" 1: 送信開始 (PTT ON / TX)");
            Console.WriteLine(" 2: 送信停止 (PTT OFF / RX)");
            Console.Write("選択 > ");
            string? input = Console.ReadLine()?.Trim();

            if (input == "1")
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write("送信を開始しますか？ (y/N) > ");
                Console.ResetColor();
                if (Console.ReadLine()?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) == true)
                {
                    _driver!.SetPtt(true);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[PTT ON] 送信状態です。");
                    Console.ResetColor();
                }
            }
            else if (input == "2")
            {
                _driver!.SetPtt(false);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("[PTT OFF] 受信状態です。");
                Console.ResetColor();
            }
        }

        private static void HandleRigState()
        {
            EnsureOpen();
            string state = _driver!.GetRigState();
            Console.WriteLine("[トランシーバ状態 (IF / State)]:");
            Console.WriteLine(state);
        }

        private static void HandleSMeter()
        {
            EnsureOpen();
            int smeter = _driver!.GetSMeter();
            Console.WriteLine($"[Sメーター値]: {smeter}");
        }

        private static void HandleAfGain()
        {
            EnsureOpen();
            int gain = _driver!.GetAfGain();
            Console.WriteLine($"[現在のAFゲイン]: {gain}");
            Console.Write("設定値 (0 - 255) を入力 > ");
            string? input = Console.ReadLine()?.Trim();

            if (int.TryParse(input, out int newGain))
            {
                _driver.SetAfGain(newGain);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"AFゲインを {newGain} に設定しました。");
                Console.ResetColor();
            }
        }

        private static void HandlePollingMonitor()
        {
            EnsureOpen();
            Console.WriteLine("=== リアルタイム監視中 ([Q]キーで終了) ===");
            while (!Console.KeyAvailable || Console.ReadKey(true).Key != ConsoleKey.Q)
            {
                try
                {
                    long freq = _driver!.GetFrequency(VfoType.VfoA);
                    string mode = _driver.GetMode(VfoType.VfoA);
                    int smeter = _driver.GetSMeter();
                    Console.Write($"\r[VFO-A]: {(freq / 1_000_000.0):F6} MHz | [MODE]: {mode,-10} | [S-METER]: {smeter,3}  ");
                }
                catch
                {
                    Console.Write("\r[通信エラー]                                ");
                }
                Thread.Sleep(250);
            }
            Console.WriteLine("\n監視を終了しました。");
        }

        private static void HandleRawCommand()
        {
            EnsureOpen();
            Console.WriteLine("[Rawコマンド送信]");
            Console.WriteLine("ASCII形式例: 'FA;' または 'MD02;'");
            Console.WriteLine("CI-V 16進例: '03' または '15 02'");
            Console.Write("コマンドを入力 > ");
            string? raw = Console.ReadLine()?.Trim();

            if (!string.IsNullOrEmpty(raw))
            {
                string reply = _driver!.SendRawCommand(raw);
                Console.WriteLine($"[応答]: {reply}");
            }
        }

        private static void EnsureOpen()
        {
            if (_driver == null || !_driver.IsOpen)
            {
                throw new InvalidOperationException("シリアルポートが開いていません。");
            }
        }

        private static long ParseFrequencyInput(string input)
        {
            if (input.Contains('.'))
            {
                if (double.TryParse(input, out double mhz))
                {
                    return (long)Math.Round(mhz * 1_000_000.0);
                }
            }
            else
            {
                if (long.TryParse(input, out long hz))
                {
                    return hz;
                }
            }
            Console.WriteLine("数値の形式が正しくありません。");
            return 0;
        }
    }
}