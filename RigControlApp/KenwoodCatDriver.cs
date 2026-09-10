using System;
using System.Collections.Generic;

namespace RigControlApp
{
    /// <summary>
    /// Kenwood TS-590 / TS-890 / TS-990 向け CAT ドライバ
    /// ini の設定値 ([COMMANDS], [METERS]) を優先し、
    /// RM 一括返却仕様 (TS-590) と個別問い合わせ仕様 (TS-990) を汎用ロジックで切り替えて処理します。
    /// </summary>
    public class KenwoodCatDriver : AsciiCatDriverBase
    {
        // RM 一括返却コマンドのレスポンスキャッシュ
        private readonly Dictionary<string, int> _cachedMeterValues = new(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastMeterBatchTime = DateTime.MinValue;

        public KenwoodCatDriver(RigConfig config) : base(config) { }

        public override string GetMode(VfoType vfo)
        {
            string key = vfo == VfoType.VfoA ? "MD_GET_A" : "MD_GET_B";
            string defaultCmd = "MD;";
            string cmd = Config.Commands.GetValueOrDefault(key, Config.Commands.GetValueOrDefault("MD_GET", defaultCmd));
            string cleanExpected = cmd.TrimEnd(Config.Terminator).Trim();
            string resp = ExecuteCommandWithExpectedPrefix(cmd, cleanExpected);

            // 応答例: "MD1;" -> "1"
            string code = StripCommandPrefix(resp, cmd);
            foreach (var kvp in Config.ModeMap)
            {
                if (kvp.Value.Equals(code, StringComparison.OrdinalIgnoreCase) ||
                    kvp.Value.PadLeft(2, '0').Equals(code, StringComparison.OrdinalIgnoreCase))
                {
                    return kvp.Key;
                }
            }
            return string.IsNullOrEmpty(code) ? "Unknown" : $"Mode {code}";
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
            // ini に定義された VFO_A / VFO_B コマンドを送信 (TS-590: FR0;FT0;, TS-990: FR0; 等)
            string key = vfo == VfoType.VfoA ? "VFO_A" : "VFO_B";
            string defaultCmd = vfo == VfoType.VfoA ? "FR0;FT0;" : "FR1;FT1;";
            string cmd = Config.Commands.GetValueOrDefault(key, defaultCmd);
            ExecuteCommand(cmd, expectResponse: false);
        }

        public override void SelectBand(VfoType vfo, string bandKey)
        {
            if (Config.Bands.TryGetValue(bandKey, out var bandVal))
            {
                string key = vfo == VfoType.VfoA ? "BAND_SET_A" : "BAND_SET_B";
                string tmpl = Config.Commands.GetValueOrDefault(key, Config.Commands.GetValueOrDefault("BAND_SET", ""));
                if (!string.IsNullOrEmpty(tmpl))
                {
                    if (tmpl.Contains("{0:") && long.TryParse(bandVal, out long freqHz))
                    {
                        string cmd = string.Format(tmpl, freqHz);
                        ExecuteCommand(cmd, expectResponse: false);
                    }
                    else
                    {
                        // Kenwood バンド選択コマンド (例: BD04;)
                        string cmd = string.Format(tmpl, bandVal);
                        ExecuteCommand(cmd, expectResponse: false);
                    }
                }
                else if (long.TryParse(bandVal, out long freqHz))
                {
                    SetFrequency(vfo, freqHz);
                }
                else
                {
                    ExecuteCommand(bandVal, expectResponse: false);
                }
            }
        }

        public override string GetAntenna(VfoType vfo)
        {
            string key = vfo == VfoType.VfoA ? "ANT_GET_A" : "ANT_GET_B";
            string defaultCmd = "AN;";
            string cmd = Config.Commands.GetValueOrDefault(key, Config.Commands.GetValueOrDefault("ANT_GET", defaultCmd));
            if (string.IsNullOrEmpty(cmd) || cmd.Equals("NONE", StringComparison.OrdinalIgnoreCase)) return "1";

            string cleanExpected = cmd.TrimEnd(Config.Terminator).Trim();
            string resp = ExecuteCommandWithExpectedPrefix(cmd, cleanExpected);
            string data = StripCommandPrefix(resp, cmd);

            if (data.Length > 0)
            {
                // ini に AntennaDigitIndex が定義されていれば優先 (0=1文字目, 1=2文字目)
                // 定義がない場合、応答プレフィックスの長さや形式から動的に判定
                int digitIndex = 0;
                if (Config.Commands.TryGetValue("AntennaDigitIndex", out var idxStr) && int.TryParse(idxStr, out int customIdx))
                {
                    digitIndex = Math.Clamp(customIdx, 0, data.Length - 1);
                }
                else if (cmd.StartsWith("AN0", StringComparison.OrdinalIgnoreCase) && data.Length >= 2)
                {
                    // TS-990 系の AN0; クエリ応答 AN0[P1][P2]... (P1=Band, P2=Ant)
                    digitIndex = 1;
                }

                string antCode = data[digitIndex].ToString();

                foreach (var kvp in Config.Antennas)
                {
                    if (kvp.Value.Equals(antCode, StringComparison.OrdinalIgnoreCase))
                    {
                        return kvp.Key.StartsWith("ANT_", StringComparison.OrdinalIgnoreCase)
                            ? kvp.Key[4..]
                            : kvp.Key;
                    }
                }
                return antCode;
            }
            return "1";
        }

        public override void SetAntenna(VfoType vfo, string antennaIndex)
        {
            string key = vfo == VfoType.VfoA ? "ANT_SET_A" : "ANT_SET_B";
            string? tmpl = Config.Commands.GetValueOrDefault(key, Config.Commands.GetValueOrDefault("ANT_SET", "AN{0}99;"));
            if (string.IsNullOrWhiteSpace(tmpl) || tmpl.Equals("NONE", StringComparison.OrdinalIgnoreCase)) return;

            string antCode = Config.Antennas.GetValueOrDefault(antennaIndex, antennaIndex);
            string cmd = string.Format(tmpl, antCode);
            ExecuteCommand(cmd, expectResponse: false);
        }

        public override void SetPtt(bool txOn)
        {
            string key = txOn ? "TX_ON" : "TX_OFF";
            string defaultCmd = txOn ? "TX;" : "RX;";
            string cmd = Config.Commands.GetValueOrDefault(key, defaultCmd);
            ExecuteCommand(cmd, expectResponse: false);
        }

        public override bool GetPtt()
        {
            string cmd = Config.Commands.GetValueOrDefault("TX_GET", "IF;");
            string cleanExpected = cmd.TrimEnd(Config.Terminator).Trim();
            string resp = ExecuteCommandWithExpectedPrefix(cmd, cleanExpected).TrimEnd(Config.Terminator);

            // IF コマンド応答の場合: 29文字目(インデックス28) が '1' なら送信中 (TS-590 マニュアル仕様)
            if (resp.StartsWith("IF", StringComparison.OrdinalIgnoreCase) && resp.Length >= 29)
            {
                return resp[28] == '1';
            }
            if (resp.StartsWith("TX", StringComparison.OrdinalIgnoreCase))
            {
                string val = StripCommandPrefix(resp, cmd);
                return val == "1" || val == "2";
            }
            return false;
        }

        public override bool GetTuner()
        {
            if (!Config.Commands.ContainsKey("TUNER_GET") && !Config.Commands.ContainsKey("AC"))
            {
                return false;
            }
            // Kenwood AC 応答: AC[P1][P2][P3]; (P2: 0=THRU, 1=IN)
            string cmd = Config.Commands.GetValueOrDefault("TUNER_GET", "AC;");
            string resp = ExecuteCommandWithExpectedPrefix(cmd, "AC");
            string data = StripCommandPrefix(resp, cmd);
            if (data.Length >= 2)
            {
                return data[1] == '1'; // TX-AT イン
            }
            return false;
        }

        public override void StartTuning()
        {
            // Kenwood: AC111; (P3=1 でチューニング動作開始)
            string cmd = Config.Commands.GetValueOrDefault("TUNER_START", "AC111;");
            ExecuteCommand(cmd, expectResponse: false);
        }

        public override void SetTuner(bool tunerOn)
        {
            string key = tunerOn ? "TUNER_ON" : "TUNER_OFF";
            string defaultCmd = tunerOn ? "AC110;" : "AC000;";
            string cmd = Config.Commands.GetValueOrDefault(key, defaultCmd);

            if (Config.Commands.TryGetValue("TUNER_SET", out var tmpl))
            {
                cmd = string.Format(tmpl, tunerOn ? "110" : "000");
            }
            ExecuteCommand(cmd, expectResponse: false);
        }

        public override string GetBandwidth(VfoType vfo)
        {
            string key = vfo == VfoType.VfoA ? "BW_GET_A" : "BW_GET_B";
            string cmd = Config.Commands.GetValueOrDefault(key, Config.Commands.GetValueOrDefault("BW_GET", "FW;"));
            if (string.IsNullOrEmpty(cmd)) return string.Empty;

            string resp = ExecuteCommandWithExpectedPrefix(cmd, "FW");
            string code = StripCommandPrefix(resp, cmd);
            foreach (var kvp in Config.Filters)
            {
                if (kvp.Value.Equals(code, StringComparison.OrdinalIgnoreCase) ||
                    kvp.Key.Equals(code, StringComparison.OrdinalIgnoreCase))
                {
                    return kvp.Key;
                }
            }
            return code;
        }

        public override void SetBandwidth(VfoType vfo, string bandwidthKey)
        {
            string bwCode = Config.Filters.GetValueOrDefault(bandwidthKey, bandwidthKey);
            string key = vfo == VfoType.VfoA ? "BW_SET_A" : "BW_SET_B";
            string tmpl = Config.Commands.GetValueOrDefault(key, Config.Commands.GetValueOrDefault("BW_SET", "FW{0:D4};"));
            if (string.IsNullOrEmpty(tmpl)) return;

            string cmd;
            if (int.TryParse(bwCode, out int val) && tmpl.Contains("{0:D"))
            {
                cmd = string.Format(tmpl, val);
            }
            else
            {
                cmd = string.Format(tmpl, bwCode);
            }
            ExecuteCommand(cmd, expectResponse: false);
        }

        public override int GetSMeter()
        {
            // TS-590 はパラメータ無しの "SM;"、TS-990 は "SM0;" を ini から取得
            string cmd = Config.Commands.GetValueOrDefault("SM_GET", "SM;");
            string resp = ExecuteCommandWithExpectedPrefix(cmd, "SM");
            int rawVal = ParseKenwoodMeter(resp, cmd);
            int maxVal = Config.GetMeterMaxValue("SMeter", 30);
            return NormalizeMeterValue(rawVal, maxVal);
        }

        public override int GetPowerMeter()
        {
            string cmd = Config.Commands.GetValueOrDefault("PO_GET", "SM;");
            string resp = ExecuteCommandWithExpectedPrefix(cmd, "SM");
            int rawVal = ParseKenwoodMeter(resp, cmd);
            int maxVal = Config.GetMeterMaxValue("PowerMeter", 30);
            return NormalizeMeterValue(rawVal, maxVal);
        }

        /// <summary>
        /// 汎用マルチメーター一括更新ロジック。
        /// 機種名ベタ書き判定ではなく、ini に [RM_GET] が定義されている場合に一括クエリを発行してキャッシュします。
        /// </summary>
        private void EnsureMeterBatchUpdated()
        {
            if (!Config.Commands.TryGetValue("RM_GET", out var rmCmd) || string.IsNullOrWhiteSpace(rmCmd))
            {
                return; // 個別クエリ方式の機種ではバッチ取得を行わない
            }

            if ((DateTime.Now - _lastMeterBatchTime).TotalMilliseconds < 100)
            {
                return; // 100ms以内のキャッシュは再利用
            }

            int expectedCount = int.TryParse(Config.Commands.GetValueOrDefault("MultiResponseCount", "3"), out int cnt) ? cnt : 3;
            var responses = ExecuteMultiResponseCommand(rmCmd, expectedResponses: expectedCount);

            foreach (var resp in responses)
            {
                string trimmed = resp.TrimEnd(Config.Terminator).Trim();
                // 応答の先頭英数字キー（例: "RM1", "RM2", "RM3"）を抽出して値をキャッシュ
                int numStart = 0;
                while (numStart < trimmed.Length && !char.IsDigit(trimmed[numStart])) numStart++;
                if (numStart < trimmed.Length)
                {
                    // プレフィックス部 (例: "RM1") と 値部 (例: "0015")
                    int valStart = numStart + 1;
                    string prefix = trimmed[..valStart];
                    string data = trimmed[valStart..].Trim();
                    if (int.TryParse(data, out int val))
                    {
                        _cachedMeterValues[prefix] = val;
                    }
                }
            }
            _lastMeterBatchTime = DateTime.Now;
        }

        public override int GetSwrMeter()
        {
            int rawVal;
            // 1. ini に RM_GET が定義されている場合は一括取得キャッシュから引く
            if (Config.Commands.ContainsKey("RM_GET"))
            {
                EnsureMeterBatchUpdated();
                // ini の SWR_GET に指定されたプレフィックス (例: "RM1" または "RM2") のキャッシュを参照
                string targetKey = Config.Commands.GetValueOrDefault("SWR_GET", "RM1").TrimEnd(Config.Terminator).Trim();
                rawVal = _cachedMeterValues.GetValueOrDefault(targetKey, 0);
            }
            else
            {
                // 2. 個別クエリ型式の場合
                string cmd = Config.Commands.GetValueOrDefault("SWR_GET", "RM2;");
                string resp = ExecuteCommandWithExpectedPrefix(cmd, "RM");
                rawVal = ParseKenwoodMeter(resp, cmd);
            }

            int maxVal = Config.GetMeterMaxValue("SwrMeter", 30);
            return NormalizeMeterValue(rawVal, maxVal);
        }

        public override int GetAlcMeter()
        {
            int rawVal;
            if (Config.Commands.ContainsKey("RM_GET"))
            {
                EnsureMeterBatchUpdated();
                string targetKey = Config.Commands.GetValueOrDefault("ALC_GET", "RM3").TrimEnd(Config.Terminator).Trim();
                rawVal = _cachedMeterValues.GetValueOrDefault(targetKey, 0);
            }
            else
            {
                string cmd = Config.Commands.GetValueOrDefault("ALC_GET", "RM1;");
                string resp = ExecuteCommandWithExpectedPrefix(cmd, "RM");
                rawVal = ParseKenwoodMeter(resp, cmd);
            }

            int maxVal = Config.GetMeterMaxValue("AlcMeter", 30);
            return NormalizeMeterValue(rawVal, maxVal);
        }

        private int ParseKenwoodMeter(string resp, string sentCmd)
        {
            string data = StripCommandPrefix(resp, sentCmd);
            if (int.TryParse(data, out int val))
            {
                return val;
            }
            return 0;
        }

        public override int GetAfGain()
        {
            string cmd = Config.Commands.GetValueOrDefault("AG_GET", "AG0;");
            string cleanExpected = cmd.TrimEnd(Config.Terminator).Trim();
            string resp = ExecuteCommandWithExpectedPrefix(cmd, cleanExpected);
            string data = StripCommandPrefix(resp, cmd);
            if (int.TryParse(data, out int val))
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
    }
}