using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace RigControlApp
{
    /// <summary>
    /// ASCII 文字列ベースの CAT 通信を行うドライバ基底クラス
    /// </summary>
    public abstract class AsciiCatDriverBase : RigDriverBase
    {
        public override bool SupportsDualVfoRead => true;

        protected AsciiCatDriverBase(RigConfig config) : base(config) { }

        /// <summary>
        /// ASCII コマンドを送信し、単一の応答（終端記号まで）を受信します。
        /// エラー応答（?; や E; など）の検知ログ出力を含みます。
        /// </summary>
        protected string ExecuteCommand(string cmd, bool expectResponse = true)
        {
            if (string.IsNullOrWhiteSpace(cmd)) return string.Empty;

            lock (SyncLock)
            {
                EnsureOpen();
                Port!.DiscardInBuffer();
                Port.Write(cmd);

                if (!expectResponse) return string.Empty;

                var sb = new StringBuilder();
                var startTime = DateTime.Now;

                while ((DateTime.Now - startTime).TotalMilliseconds < Config.ReadTimeoutMs)
                {
                    if (Port.BytesToRead > 0)
                    {
                        char c = (char)Port.ReadChar();
                        sb.Append(c);
                        if (c == Config.Terminator)
                        {
                            string result = sb.ToString();
                            // エラー応答の検出 (?; や E; または O;)
                            if (result.StartsWith("?;") || result.StartsWith("E;") || result.StartsWith("O;"))
                            {
                                Console.WriteLine($"[CAT Error] 送信='{cmd}', 応答='{result}'");
                            }
                            return result;
                        }
                    }
                    else
                    {
                        Thread.Sleep(5);
                    }
                }

                string timeoutResp = sb.ToString();
                if (timeoutResp.StartsWith("?;") || timeoutResp.StartsWith("E;") || timeoutResp.StartsWith("O;"))
                {
                    Console.WriteLine($"[CAT Error] 送信='{cmd}', 応答='{timeoutResp}'");
                }
                return timeoutResp;
            }
        }

        /// <summary>
        /// 期待する応答プレフィックス（例: "FA", "MD0" など）を指定してコマンドを送受信します。
        /// 無線機のAI機能による非同期通知や直前のゴミデータがバッファに混入した場合でも、
        /// 指定されたプレフィックスで始まる応答が届くまで読み進めて正しく抽出します。
        /// </summary>
        protected string ExecuteCommandWithExpectedPrefix(string cmd, string expectedPrefix, bool expectResponse = true)
        {
            if (string.IsNullOrWhiteSpace(cmd)) return string.Empty;

            lock (SyncLock)
            {
                EnsureOpen();
                Port!.DiscardInBuffer();
                Port.Write(cmd);

                if (!expectResponse) return string.Empty;

                var sb = new StringBuilder();
                var startTime = DateTime.Now;
                string cleanExpected = expectedPrefix.TrimEnd(Config.Terminator).Trim();

                while ((DateTime.Now - startTime).TotalMilliseconds < Config.ReadTimeoutMs)
                {
                    if (Port.BytesToRead > 0)
                    {
                        char c = (char)Port.ReadChar();
                        sb.Append(c);
                        if (c == Config.Terminator)
                        {
                            string frame = sb.ToString();
                            sb.Clear();

                            // エラー応答の検出
                            if (frame.StartsWith("?;") || frame.StartsWith("E;") || frame.StartsWith("O;"))
                            {
                                Console.WriteLine($"[CAT Error] 送信='{cmd}', 応答='{frame}'");
                                return frame;
                            }

                            // 目的のプレフィックスに一致した場合は即座に返却
                            if (frame.StartsWith(cleanExpected, StringComparison.OrdinalIgnoreCase))
                            {
                                return frame;
                            }
                            // 目的外のフレーム（AI通知など）の場合は読み飛ばして継続
                        }
                    }
                    else
                    {
                        Thread.Sleep(5);
                    }
                }

                return sb.ToString();
            }
        }

        /// <summary>
        /// 1回のコマンド送信に対して複数のレスポンスが連続して返却される場合（例: Kenwood TS-590の RM; に対する RM1..; RM2..; RM3..;）
        /// 指定個数またはタイムアウトまで連続するコマンド応答フレームをすべて受信してリストで返却します。
        /// </summary>
        protected List<string> ExecuteMultiResponseCommand(string cmd, int expectedResponses = 1)
        {
            var responses = new List<string>();
            if (string.IsNullOrWhiteSpace(cmd)) return responses;

            lock (SyncLock)
            {
                EnsureOpen();
                Port!.DiscardInBuffer();
                Port.Write(cmd);

                var sb = new StringBuilder();
                var startTime = DateTime.Now;

                while ((DateTime.Now - startTime).TotalMilliseconds < Config.ReadTimeoutMs)
                {
                    if (Port.BytesToRead > 0)
                    {
                        char c = (char)Port.ReadChar();
                        sb.Append(c);
                        if (c == Config.Terminator)
                        {
                            string frame = sb.ToString();
                            sb.Clear();
                            if (!string.IsNullOrWhiteSpace(frame))
                            {
                                responses.Add(frame);
                                if (responses.Count >= expectedResponses)
                                {
                                    break;
                                }
                            }
                        }
                    }
                    else
                    {
                        Thread.Sleep(5);
                    }
                }
            }

            return responses;
        }

        /// <summary>
        /// 応答からコマンドプレフィックスと終端記号を除去し、パラメータ実データ部を抽出します。
        /// </summary>
        protected string StripCommandPrefix(string resp, string sentCmd)
        {
            if (string.IsNullOrEmpty(resp)) return string.Empty;
            resp = resp.TrimEnd(Config.Terminator).Trim();
            string prefix = sentCmd.TrimEnd(Config.Terminator).Trim();

            if (resp.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return resp[prefix.Length..].Trim();
            }

            // プレフィックスと完全に一致しない場合、先頭の英字部分を読み飛ばす
            int idx = 0;
            while (idx < resp.Length && !char.IsDigit(resp[idx]) && resp[idx] != '+' && resp[idx] != '-') idx++;
            if (idx < resp.Length)
            {
                return resp[idx..].Trim();
            }

            return resp.Trim();
        }

        public override long GetFrequency(VfoType vfo)
        {
            string key = vfo == VfoType.VfoA ? "FA_GET" : "FB_GET";
            string defaultCmd = vfo == VfoType.VfoA ? "FA;" : "FB;";
            string cmd = Config.Commands.GetValueOrDefault(key, defaultCmd);
            string expectedPrefix = vfo == VfoType.VfoA ? "FA" : "FB";

            string resp = ExecuteCommandWithExpectedPrefix(cmd, expectedPrefix);
            string data = StripCommandPrefix(resp, cmd);

            if (long.TryParse(data, out long freq))
            {
                return freq;
            }
            return 0;
        }

        public override void SetFrequency(VfoType vfo, long freqHz)
        {
            string key = vfo == VfoType.VfoA ? "FA_SET" : "FB_SET";
            string defaultTmpl = vfo == VfoType.VfoA
                ? $"FA{{0:D{Config.FreqDigits}}};"
                : $"FB{{0:D{Config.FreqDigits}}};";
            string tmpl = Config.Commands.GetValueOrDefault(key, defaultTmpl);
            string cmd = string.Format(tmpl, freqHz);
            ExecuteCommand(cmd, expectResponse: false);
        }

        public override string GetRigState()
        {
            string cmd = Config.Commands.GetValueOrDefault("IF_GET", "IF;");
            return ExecuteCommandWithExpectedPrefix(cmd, "IF");
        }

        public override string SendRawCommand(string rawInput)
        {
            if (!rawInput.EndsWith(Config.Terminator.ToString()))
                rawInput += Config.Terminator;
            return ExecuteCommand(rawInput);
        }

        /// <summary>
        /// メーター表示切り替え (SM_SET / PO_SET / SWR_SET / ALC_SET)
        /// </summary>
        public override void SelectMeter(string meterType)
        {
            string key = $"{meterType.ToUpperInvariant()}_SET";
            // Sメーター用の SM_SET が未定義なら PO_SET をフォールバック利用
            if (!Config.Commands.ContainsKey(key) && key == "SM_SET")
            {
                key = "PO_SET";
            }
            if (Config.Commands.TryGetValue(key, out var cmd) && !string.IsNullOrWhiteSpace(cmd))
            {
                ExecuteCommand(cmd, expectResponse: false);
            }
        }
    }
}