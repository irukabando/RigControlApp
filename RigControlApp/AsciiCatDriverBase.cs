using System;
using System.Text;
using System.Threading;

namespace RigControlApp
{
    /// <summary>
    /// ASCII コマンド形式 CAT ドライバーの共通基底クラス
    /// </summary>
    public abstract class AsciiCatDriverBase : RigDriverBase
    {
        public override bool SupportsDualVfoRead => true;

        protected AsciiCatDriverBase(RigConfig config) : base(config) { }

        /// <summary>
        /// ASCII コマンドを送信し、レスポンスを受信
        /// リグから ?; や E; などのエラーを受信した場合はコンソールに出力
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

                            // リグからのエラー応答 (?; や E; など) を検出してコンソールに出力
                            if (result.StartsWith("?;") || result.StartsWith("E;") || result.StartsWith("O;"))
                            {
                                Console.WriteLine($"[CAT Error] リグからエラー応答を受信しました: 送信コマンド='{cmd}', 応答='{result}'");
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
                    Console.WriteLine($"[CAT Error] リグからエラー応答を受信しました: 送信コマンド='{cmd}', 応答='{timeoutResp}'");
                }
                return timeoutResp;
            }
        }

        /// <summary>
        /// 問い合わせコマンドからプレフィックス（終端記号を除く）を動的に判定し、
        /// 受信レスポンスの先頭からその文字数分を確実に除去する共通ヘルパー
        /// </summary>
        protected string StripCommandPrefix(string resp, string sentCmd)
        {
            if (string.IsNullOrEmpty(resp)) return string.Empty;

            // 終端記号を除去
            resp = resp.TrimEnd(Config.Terminator);
            string prefix = sentCmd.TrimEnd(Config.Terminator);

            // 送信したプレフィックスと完全に一致する場合はその長さ分をスライス
            if (resp.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return resp[prefix.Length..];
            }

            // プレフィックスの長さが異なる場合（例: sent='MD;' で resp='MD02;' など）は先頭の英字部分をスキップ
            int idx = 0;
            while (idx < resp.Length && !char.IsDigit(resp[idx])) idx++;
            if (idx < resp.Length)
            {
                return resp[idx..];
            }

            return resp;
        }

        public override long GetFrequency(VfoType vfo)
        {
            string key = vfo == VfoType.VfoA ? "FA_GET" : "FB_GET";
            string defaultCmd = vfo == VfoType.VfoA ? "FA;" : "FB;";

            string cmd = Config.Commands.GetValueOrDefault(key, defaultCmd);
            string resp = ExecuteCommand(cmd);

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
            return ExecuteCommand(cmd);
        }

        public override string SendRawCommand(string rawInput)
        {
            if (!rawInput.EndsWith(Config.Terminator.ToString()))
                rawInput += Config.Terminator;

            return ExecuteCommand(rawInput);
        }
    }
}