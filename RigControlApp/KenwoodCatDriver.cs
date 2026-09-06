using System;
using System.Collections.Generic;

namespace RigControlApp
{
    /// <summary>
    /// Kenwood TS-590 / TS-890 / TS-990 向け CAT ドライバ
    /// </summary>
    public class KenwoodCatDriver : AsciiCatDriverBase
    {
        public KenwoodCatDriver(RigConfig config) : base(config) { }

        public override string GetMode(VfoType vfo)
        {
            string key = vfo == VfoType.VfoA ? "MD_GET_A" : "MD_GET_B";
            string defaultCmd = "MD;";
            string cmd = Config.Commands.GetValueOrDefault(key, Config.Commands.GetValueOrDefault("MD_GET", defaultCmd));
            string resp = ExecuteCommand(cmd);
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
            // Kenwood は送受信 VFO を FR/FT で切替 (0=VFO-A, 1=VFO-B)
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
                        // Kenwood 直接 BD/BU コマンド等 (例: BD04;)
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
            // Kenwood AN コマンド: AN[P1][P2][P3]; (P1: 1=ANT1, 2=ANT2)
            string key = vfo == VfoType.VfoA ? "ANT_GET_A" : "ANT_GET_B";
            string defaultCmd = "AN;";
            string cmd = Config.Commands.GetValueOrDefault(key, Config.Commands.GetValueOrDefault("ANT_GET", defaultCmd));
            if (string.IsNullOrEmpty(cmd)) return string.Empty;

            string resp = ExecuteCommand(cmd);
            string data = StripCommandPrefix(resp, cmd);

            if (data.Length > 0)
            {
                string antCode = data[0].ToString(); // 1文字目が ANT 番号 (1 または 2)
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
            return string.Empty;
        }

        public override void SetAntenna(VfoType vfo, string antennaIndex)
        {
            // Kenwood AN 設定: AN[P1]99; (P1: 1=ANT1, 2=ANT2, 9=受信専用ANT等)
            string antCode = Config.Antennas.GetValueOrDefault(antennaIndex, antennaIndex);
            string key = vfo == VfoType.VfoA ? "ANT_SET_A" : "ANT_SET_B";
            string tmpl = Config.Commands.GetValueOrDefault(key, Config.Commands.GetValueOrDefault("ANT_SET", "AN{0}99;"));
            string cmd = string.Format(tmpl, antCode);
            ExecuteCommand(cmd, expectResponse: false);
        }

        public override void SetPtt(bool txOn)
        {
            // Kenwood コマンド: TX; または RX;
            string cmd = txOn
                ? Config.Commands.GetValueOrDefault("TX_ON", "TX;")
                : Config.Commands.GetValueOrDefault("TX_OFF", "RX;");
            ExecuteCommand(cmd, expectResponse: false);
        }

        public override bool GetPtt()
        {
            // Kenwood 状態取得 IF; の 29文字目 (0-indexed: 28) が TX (1) / RX (0)
            string cmd = Config.Commands.GetValueOrDefault("TX_GET", "IF;");
            string resp = ExecuteCommand(cmd).TrimEnd(Config.Terminator);

            if (resp.StartsWith("IF", StringComparison.OrdinalIgnoreCase) && resp.Length >= 29)
            {
                return resp[28] == '1';
            }
            if (resp.StartsWith("TX", StringComparison.OrdinalIgnoreCase))
            {
                string val = resp[2..];
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

            // Kenwood AC コマンド: AC[P1][P2][P3]; (P2: 0=THRU, 1=IN)
            string cmd = Config.Commands.GetValueOrDefault("TUNER_GET", "AC;");
            string resp = ExecuteCommand(cmd);
            string data = StripCommandPrefix(resp, cmd);

            if (data.Length >= 2)
            {
                return data[1] == '1'; // TX-AT が IN
            }
            return false;
        }

        public override void SetTuner(bool tunerOn)
        {
            string cmd;
            if (tunerOn)
            {
                cmd = Config.Commands.GetValueOrDefault("TUNER_ON", "AC111;"); // チューナー稼働
            }
            else
            {
                cmd = Config.Commands.GetValueOrDefault("TUNER_OFF", "AC000;"); // スルー / OFF
            }

            if (Config.Commands.TryGetValue("TUNER_SET", out var tmpl))
            {
                cmd = string.Format(tmpl, tunerOn ? "111" : "000");
            }
            ExecuteCommand(cmd, expectResponse: false);
        }

        public override string GetBandwidth(VfoType vfo)
        {
            string key = vfo == VfoType.VfoA ? "BW_GET_A" : "BW_GET_B";
            string cmd = Config.Commands.GetValueOrDefault(key, Config.Commands.GetValueOrDefault("BW_GET", "FW;"));
            if (string.IsNullOrEmpty(cmd)) return string.Empty;

            string resp = ExecuteCommand(cmd);
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
            // Kenwood SM0; -> SM0[0000~0030];
            string cmd = Config.Commands.GetValueOrDefault("SM_GET", "SM0;");
            string resp = ExecuteCommand(cmd);
            int rawVal = ParseKenwoodMeter(resp, cmd);
            int maxVal = int.TryParse(Config.Meters.GetValueOrDefault("SMeter", "30"), out int max) ? max : 30;
            return NormalizeMeterValue(rawVal, maxVal);
        }

        public override int GetPowerMeter()
        {
            // Kenwood 送信時 SM0; は PO 表示 (0000~0030)
            string cmd = Config.Commands.GetValueOrDefault("PO_GET", "SM0;");
            string resp = ExecuteCommand(cmd);
            int rawVal = ParseKenwoodMeter(resp, cmd);
            int maxVal = int.TryParse(Config.Meters.GetValueOrDefault("PowerMeter", "30"), out int max) ? max : 30;
            return NormalizeMeterValue(rawVal, maxVal);
        }

        public override int GetSwrMeter()
        {
            // Kenwood RM1; -> RM1[0000~0030]; (1=SWR)
            string cmd = Config.Commands.GetValueOrDefault("SWR_GET", "RM1;");
            string resp = ExecuteCommand(cmd);
            int rawVal = ParseKenwoodMeter(resp, cmd);
            int maxVal = int.TryParse(Config.Meters.GetValueOrDefault("SwrMeter", "30"), out int max) ? max : 30;
            return NormalizeMeterValue(rawVal, maxVal);
        }

        public override int GetAlcMeter()
        {
            // Kenwood RM3; -> RM3[0000~0030]; (3=ALC)
            string cmd = Config.Commands.GetValueOrDefault("ALC_GET", "RM3;");
            string resp = ExecuteCommand(cmd);
            int rawVal = ParseKenwoodMeter(resp, cmd);
            int maxVal = int.TryParse(Config.Meters.GetValueOrDefault("AlcMeter", "30"), out int max) ? max : 30;
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
            // Kenwood AG0; -> AG0[000~255];
            string cmd = Config.Commands.GetValueOrDefault("AG_GET", "AG0;");
            string resp = ExecuteCommand(cmd);
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