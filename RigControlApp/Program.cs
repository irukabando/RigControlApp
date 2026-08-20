using System;
using System.IO;
using System.IO.Ports;
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
            Console.WriteLine("     アマチュア無線機 汎用 CAT / CI-V 制御プログラム     ");
            Console.WriteLine("     対応: Yaesu / Kenwood (ASCII) / Icom (CI-V)        ");
            Console.WriteLine("==========================================================");

            string configFile = "config.txt";
            if (args.Length > 0 && File.Exists(args[0]))
            {
                configFile = args[0];
            }
            else if (!File.Exists(configFile))
            {
                // Check sample configs
                if (File.Exists("config_kenwood.txt")) configFile = "config_kenwood.txt";
                else if (File.Exists("config_yaesu.txt")) configFile = "config_yaesu.txt";
                else if (File.Exists("config_icom.txt")) configFile = "config_icom.txt";
            }

            LoadAndInitConfig(configFile);

            bool exit = false;
            while (!exit)
            {
                PrintMainMenu();
                Console.Write("操作番号を選択してください > ");
                string? choice = Console.ReadLine()?.Trim();

                try
                {
                    switch (choice)
                    {
                        case "1": // ポート再接続 / 設定再読込
                            SelectAndLoadConfig();
                            break;

                        case "2": // VFO-A 周波数 読出 / 設定
                            HandleFrequencyA();
                            break;

                        case "3": // VFO-B 周波数 読出 / 設定
                            HandleFrequencyB();
                            break;

                        case "4": // 動作モード (MD) 読出 / 設定
                            HandleMode();
                            break;

                        case "5": // 送受信切替 (PTT / TX / RX)
                            HandlePtt();
                            break;

                        case "6": // 無線機状態一括読出 (IF)
                            HandleRigState();
                            break;

                        case "7": // Sメーター読出 (SM)
                            HandleSMeter();
                            break;

                        case "8": // AFゲイン (音量 AG) 読出 / 設定
                            HandleAfGain();
                            break;

                        case "9": // リアルタイム・ポーリング監視 (周波数/モード/Sメーター)
                            HandlePollingMonitor();
                            break;

                        case "10": // カスタム生コマンド送信
                            HandleRawCommand();
                            break;

                        case "0": // 終了
                            exit = true;
                            break;

                        default:
                            Console.WriteLine("無効な選択です。もう一度入力してください。");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[エラー発生]: {ex.Message}");
                    Console.ResetColor();
                }

                if (!exit)
                {
                    Console.WriteLine("[Enter]キーを押してメニューに戻ります...");
                    Console.ReadLine();
                }
            }

            _driver?.Close();
            Console.WriteLine("プログラムを終了しました。");
        }

        private static void PrintMainMenu()
        {
            Console.Clear();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("==========================================================");
            Console.WriteLine($" 接続先: {_config.PortName} ({_config.BaudRate} bps) | プロトコル: {_config.Protocol}");
            Console.WriteLine($" 接続状態: {(_driver != null && _driver.IsOpen ? "● 接続中 (ONLINE)" : "○ 未接続 (OFFLINE)")}");
            Console.WriteLine("==========================================================");
            Console.ResetColor();

            Console.WriteLine(" [1] 設定ファイル再読込 / COMポート変更");
            Console.WriteLine(" [2] VFO-A 周波数の取得・変更 (FA)");
            Console.WriteLine(" [3] VFO-B 周波数の取得・変更 (FB)");
            Console.WriteLine(" [4] 動作モードの取得・変更   (MD: USB, LSB, CW, FM, AM...)");
            Console.WriteLine(" [5] 送受信制御 (PTT: TX / RX)");
            Console.WriteLine(" [6] 状態一括読出 (IF / State)");
            Console.WriteLine(" [7] Sメーター値の取得 (SM)");
            Console.WriteLine(" [8] AFゲイン (音量) 取得・変更 (AG)");
            Console.WriteLine(" [9] リアルタイム・ステータス監視 (ポーリング)");
            Console.WriteLine(" [10] 生コマンド直接送信 (CAT/CI-V Raw)");
            Console.WriteLine(" [0] 終了");
        }

        private static void LoadAndInitConfig(string path)
        {
            try
            {
                _driver?.Close();
                _config = RigConfig.LoadFromFile(path);
                Console.WriteLine($"設定ファイルを読み込みました: {Path.GetFullPath(path)}");

                if (_config.Protocol == ProtocolType.Civ)
                {
                    _driver = new IcomCivDriver(_config);
                }
                else
                {
                    _driver = new AsciiCatDriver(_config);
                }

                _driver.Open();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"COMポート {_config.PortName} を正常にオープンしました。");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[警告] ポート接続失敗: {ex.Message}");
                Console.WriteLine("メニュー [1] から設定を確認・変更してください。");
                Console.ResetColor();
            }
        }

        private static void SelectAndLoadConfig()
        {
            Console.WriteLine("利用可能な設定ファイル一覧:");
            var files = Directory.GetFiles(".", "*.txt");
            for (int i = 0; i < files.Length; i++)
            {
                Console.WriteLine($" [{i + 1}] {Path.GetFileName(files[i])}");
            }
            Console.Write("読み込む設定ファイルの番号またはパスを入力 > ");
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
                Console.WriteLine("変更をキャンセルしました。");
            }
        }

        private static void HandleFrequencyA()
        {
            EnsureOpen();
            long currentFreq = _driver!.GetFrequencyA();
            Console.WriteLine($"[現在の VFO-A 周波数]: {currentFreq:N0} Hz ({(currentFreq / 1_000_000.0):F6} MHz)");

            Console.Write("新しい周波数を入力 (Hz または MHz単位、例: 7074000 または 14.074、変更しない場合は空Enter) > ");
            string? input = Console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(input))
            {
                long newFreq = ParseFrequencyInput(input);
                if (newFreq > 0)
                {
                    _driver.SetFrequencyA(newFreq);
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"VFO-A を {newFreq:N0} Hz に設定しました。");
                    Console.ResetColor();
                }
            }
        }

        private static void HandleFrequencyB()
        {
            EnsureOpen();
            long currentFreq = _driver!.GetFrequencyB();
            Console.WriteLine($"[現在の VFO-B 周波数]: {currentFreq:N0} Hz ({(currentFreq / 1_000_000.0):F6} MHz)");

            Console.Write("新しい周波数を入力 (例: 7041000、変更しない場合は空Enter) > ");
            string? input = Console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(input))
            {
                long newFreq = ParseFrequencyInput(input);
                if (newFreq > 0)
                {
                    _driver.SetFrequencyB(newFreq);
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"VFO-B を {newFreq:N0} Hz に設定しました。");
                    Console.ResetColor();
                }
            }
        }

        private static void HandleMode()
        {
            EnsureOpen();
            string currentMode = _driver!.GetMode();
            Console.WriteLine($"[現在の動作モード]: {currentMode}");

            Console.WriteLine("設定可能なモード一覧:");
            foreach (var kvp in _config.ModeMap)
            {
                Console.Write($" [{kvp.Key}]");
            }
            Console.WriteLine();

            Console.Write("設定するモード名を入力 (変更しない場合は空Enter) > ");
            string? input = Console.ReadLine()?.Trim().ToUpperInvariant();
            if (!string.IsNullOrEmpty(input))
            {
                _driver.SetMode(input);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"モードを {input} に設定しました。");
                Console.ResetColor();
            }
        }

        private static void HandlePtt()
        {
            EnsureOpen();
            Console.WriteLine("[PTT 送受信切替]");
            Console.WriteLine(" 1: 送信開始 (PTT ON / TX)");
            Console.WriteLine(" 2: 受信に戻る (PTT OFF / RX)");
            Console.Write("選択 > ");
            string? input = Console.ReadLine()?.Trim();

            if (input == "1")
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write("本当に送信を開始しますか？ 無変調連続送信に注意してください (y/N) > ");
                Console.ResetColor();
                if (Console.ReadLine()?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) == true)
                {
                    _driver!.SetPtt(true);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[PTT ON] 送信状態に移行しました。");
                    Console.ResetColor();
                }
            }
            else if (input == "2")
            {
                _driver!.SetPtt(false);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("[PTT OFF] 受信状態に戻りました。");
                Console.ResetColor();
            }
        }

        private static void HandleRigState()
        {
            EnsureOpen();
            string state = _driver!.GetRigState();
            Console.WriteLine($"[無線機 状態取得 (IF / State)]:");
            Console.WriteLine(state);
        }

        private static void HandleSMeter()
        {
            EnsureOpen();
            int smeter = _driver!.GetSMeter();
            Console.WriteLine($"[現在の Sメーター値]: {smeter}");
        }

        private static void HandleAfGain()
        {
            EnsureOpen();
            int gain = _driver!.GetAfGain();
            Console.WriteLine($"[現在の AFゲイン (音量)]: {gain}");

            Console.Write("新しい音量値を入力 (0 - 255、変更しない場合は空Enter) > ");
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
            Console.WriteLine("=== リアルタイム・ステータス監視中 ([Q]キーで終了) ===");

            while (!Console.KeyAvailable || Console.ReadKey(true).Key != ConsoleKey.Q)
            {
                try
                {
                    long freq = _driver!.GetFrequencyA();
                    string mode = _driver.GetMode();
                    int smeter = _driver.GetSMeter();

                    Console.Write($"\r[VFO-A]: {(freq / 1_000_000.0):F6} MHz | [MODE]: {mode,-10} | [S-METER]: {smeter,3}  ");
                }
                catch
                {
                    Console.Write("\r[通信エラーまたはタイムアウト]                                ");
                }
                Thread.Sleep(250);
            }
            Console.WriteLine("\n監視を終了しました。");
        }

        private static void HandleRawCommand()
        {
            EnsureOpen();
            Console.WriteLine("[生コマンド直接送信]");
            Console.WriteLine("ASCIIプロトコルの場合: 例 'FA;' または 'MD02;'");
            Console.WriteLine("CI-Vプロトコルの場合: 例 '03' (周波数読出) または '15 02' (Sメーター)");
            Console.Write("コマンド文字列を入力 > ");
            string? raw = Console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(raw))
            {
                string reply = _driver!.SendRawCommand(raw);
                Console.WriteLine($"[受信応答]: {reply}");
            }
        }

        private static void EnsureOpen()
        {
            if (_driver == null || !_driver.IsOpen)
            {
                throw new InvalidOperationException("シリアルポートが接続されていません。設定を確認して再接続してください。");
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
            Console.WriteLine("周波数の形式が無効です。");
            return 0;
        }
    }
}
