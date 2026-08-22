using System;
using System.Text;
using System.Threading;

namespace RigControlApp
{
    /// <summary>
    /// Kenwood および Yaesu (新世代 ASCII コマンド CAT) 向けドライバー
    /// </summary>
    public class AsciiCatDriver : RigDriverBase
    {
        public override bool SupportsDualVfoRead => true;

        public AsciiCatDriver(RigConfig config) : base(config) { }

        /// <summary>
        /// ASCII コマンドを送信し、レスポンスを受信
        /// </summary>
        private string ExecuteCommand(string cmd, bool expectResponse = true)
        {
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
                            return sb.ToString();
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

        public override long GetFrequency(VfoType vfo)
        {
            string key = vfo == VfoType.VfoA ? "FA_GET" : "FB_GET";
            string defaultCmd = vfo == VfoType.VfoA ? "FA;" : "FB;";
            string prefix = vfo == VfoType.VfoA ? "FA" : "FB";

            string cmd = Config.Commands.GetValueOrDefault(key, defaultCmd);
            string resp = ExecuteCommand(cmd);

            return ParseFrequencyResponse(resp, prefix);
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

        public override string GetMode(VfoType vfo)
        {
            string key = vfo == VfoType.VfoA ? "MD_GET_A" : "MD_GET_B";
            string defaultCmd = "MD;";
            string cmd = Config.Commands.GetValueOrDefault(key, Config.Commands.GetValueOrDefault("MD_GET", defaultCmd));
            string resp = ExecuteCommand(cmd).TrimEnd(Config.Terminator);

            // モード応答の解析 (MD02; や MD2; などの形式に対応)
            string code = resp;
            if (code.StartsWith("MD", StringComparison.OrdinalIgnoreCase))
            {
                code = code[2..];
                if (code.Length > 1 && (code[0] == '0' || code[0] == '1'))
                {
                    code = code[1..];
                }
            }

            foreach (var kvp in Config.ModeMap)
            {
                if (kvp.Value.Equals(code, StringComparison.OrdinalIgnoreCase) ||
                    kvp.Value.PadLeft(2, '0').Equals(code, StringComparison.OrdinalIgnoreCase))
                {
                    return kvp.Key;
                }
            }

            return string.IsNullOrEmpty(code) ? "Unknown" : $"Code: {code}";
        }

        public override void SetMode(VfoType vfo, string modeName)
        {
            if (Config.ModeMap.TryGetValue(modeName, out var code))
            {
                string key = vfo == VfoType.VfoA ? "MD_SET_A" : "MD_SET_B";
                string tmpl = Config.Commands.GetValueOrDefault(key, Config.Commands.GetValueOrDefault("MD_SET", "MD{0};"));
                string cmd = string.Format(tmpl, code);
                ExecuteCommand(cmd, expectResponse: false);
            }
        }

        public override void SelectVfo(VfoType vfo)
        {
            string key = vfo == VfoType.VfoA ? "VFO_A" : "VFO_B";
            string defaultCmd = vfo == VfoType.VfoA ? "VS0;" : "VS1;";
            string cmd = Config.Commands.GetValueOrDefault(key, defaultCmd);
            ExecuteCommand(cmd, expectResponse: false);
        }

        public override void SelectBand(VfoType vfo, string bandKey)
        {
            // [BANDS] からバンド設定値（バンド番号、固有コマンド、または周波数Hz）を取得
            if (Config.Bands.TryGetValue(bandKey, out var bandVal))
            {
                string key = vfo == VfoType.VfoA ? "BAND_SET_A" : "BAND_SET_B";
                string tmpl = Config.Commands.GetValueOrDefault(key, Config.Commands.GetValueOrDefault("BAND_SET", ""));

                if (!string.IsNullOrEmpty(tmpl))
                {
                    // テンプレートが周波数ゼロ埋め書式 {0:D...} を含む場合は周波数Hzを数値変換して代入
                    if (tmpl.Contains("{0:") && long.TryParse(bandVal, out long freqHz))
                    {
                        string cmd = string.Format(tmpl, freqHz);
                        ExecuteCommand(cmd, expectResponse: false);
                    }
                    else
                    {
                        // 独自バンドコードをテンプレートに代入（例: BS0{0}; に 03 を代入 -> BS003;）
                        string cmd = string.Format(tmpl, bandVal);
                        ExecuteCommand(cmd, expectResponse: false);
                    }
                }
                else if (long.TryParse(bandVal, out long freqHz))
                {
                    // テンプレート未定義で周波数(Hz)が直接記載されている場合は周波数設定を実行
                    SetFrequency(vfo, freqHz);
                }
                else
                {
                    // [BANDS] に完全なCATコマンド文字列が直接指定されている場合
                    ExecuteCommand(bandVal, expectResponse: false);
                }
            }
        }

        public override void SetAntenna(VfoType vfo, string antennaIndex)
        {
            // [ANTENNAS] からアンテナ番号に対応するパラメータを取得 (未定義なら antennaIndex をそのまま使用)
            string antCode = Config.Antennas.GetValueOrDefault(antennaIndex, antennaIndex);

            string key = vfo == VfoType.VfoA ? "ANT_SET_A" : "ANT_SET_B";
            string tmpl = Config.Commands.GetValueOrDefault(key, Config.Commands.GetValueOrDefault("ANT_SET", "AN{0};"));
            string cmd = string.Format(tmpl, antCode);
            ExecuteCommand(cmd, expectResponse: false);
        }

        public override void SetPtt(bool txOn)
        {
            string cmd = txOn
                ? Config.Commands.GetValueOrDefault("TX_ON", "TX1;")
                : Config.Commands.GetValueOrDefault("TX_OFF", "TX0;");

            ExecuteCommand(cmd, expectResponse: false);
        }

        public override string GetRigState()
        {
            string cmd = Config.Commands.GetValueOrDefault("IF_GET", "IF;");
            return ExecuteCommand(cmd);
        }

        public override int GetSMeter()
        {
            string cmd = Config.Commands.GetValueOrDefault("SM_GET", "SM0;");
            string resp = ExecuteCommand(cmd).TrimEnd(Config.Terminator);

            if (resp.Length > 2 && int.TryParse(resp[2..], out int val))
            {
                return val;
            }
            return 0;
        }

        public override int GetAfGain()
        {
            string cmd = Config.Commands.GetValueOrDefault("AG_GET", "AG0;");
            string resp = ExecuteCommand(cmd).TrimEnd(Config.Terminator);

            if (resp.Length > 3 && int.TryParse(resp[3..], out int val))
            {
                return val;
            }
            return 0;
        }

        public override void SetAfGain(int gainValue)
        {
            string tmpl = Config.Commands.GetValueOrDefault("AG_SET", "AG0{0:D3};");
            string cmd = string.Format(tmpl, Math.Clamp(gainValue, 0, 255));
            ExecuteCommand(cmd, expectResponse: false);
        }

        public override string SendRawCommand(string rawInput)
        {
            if (!rawInput.EndsWith(Config.Terminator.ToString()))
                rawInput += Config.Terminator;

            return ExecuteCommand(rawInput);
        }

        private long ParseFrequencyResponse(string resp, string prefix)
        {
            resp = resp.TrimEnd(Config.Terminator);
            if (resp.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string numStr = resp[prefix.Length..];
                if (long.TryParse(numStr, out long freq)) return freq;
            }
            return 0;
        }
    }
}