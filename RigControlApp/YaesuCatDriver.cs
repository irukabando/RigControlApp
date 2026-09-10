using System;
using System.Collections.Generic;

namespace RigControlApp
{
    /// <summary>
    /// Yaesu 新世代 ASCII CAT (FTDX9000, FTDX101, FT-991A, FTDX10, FT-710 等) 向けドライバ
    /// FTDX9000 (8桁周波数, RM08;等2桁メーター) と FTDX101系 (9桁周波数, RM5;等1桁+000付加) の差異を吸収します。
    /// </summary>
    public class YaesuCatDriver : AsciiCatDriverBase
    {
        public YaesuCatDriver(RigConfig config) : base(config) { }

        public override string GetMode(VfoType vfo)
        {
            string key = vfo == VfoType.VfoA ? "MD_GET_A" : "MD_GET_B";
            string defaultCmd = vfo == VfoType.VfoA ? "MD0;" : "MD1;";
            string cmd = Config.Commands.GetValueOrDefault(key, Config.Commands.GetValueOrDefault("MD_GET", defaultCmd));
            string cleanExpected = cmd.TrimEnd(Config.Terminator).Trim();

            string resp = ExecuteCommandWithExpectedPrefix(cmd, cleanExpected);
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
                string defaultTmpl = vfo == VfoType.VfoA ? "MD0{0};" : "MD1{0};";
                string tmpl = Config.Commands.GetValueOrDefault(key, Config.Commands.GetValueOrDefault("MD_SET", defaultTmpl));
                string cmd = string.Format(tmpl, code);
                ExecuteCommand(cmd, expectResponse: false);
            }
        }

        public override void SelectVfo(VfoType vfo)
        {
            // FTDX9000 / FTDX101 共通: VS0; (MAIN), VS1; (SUB)
            string key = vfo == VfoType.VfoA ? "VFO_A" : "VFO_B";
            string defaultCmd = vfo == VfoType.VfoA ? "VS0;" : "VS1;";
            string cmd = Config.Commands.GetValueOrDefault(key, defaultCmd);
            ExecuteCommand(cmd, expectResponse: false);
        }

        public override void SelectBand(VfoType vfo, string bandKey)
        {
            if (Config.Bands.TryGetValue(bandKey, out var bandVal))
            {
                string key = vfo == VfoType.VfoA ? "BAND_SET_A" : "BAND_SET_B";
                string tmpl = Config.Commands.GetValueOrDefault(key, Config.Commands.GetValueOrDefault("BAND_SET", "BS{0};"));
                if (!string.IsNullOrEmpty(tmpl))
                {
                    if (tmpl.Contains("{0:") && long.TryParse(bandVal, out long freqHz))
                    {
                        string cmd = string.Format(tmpl, freqHz);
                        ExecuteCommand(cmd, expectResponse: false);
                    }
                    else
                    {
                        // Yaesu BS コマンド (例: BS05;)
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
            string? cmd = Config.Commands.GetValueOrDefault(key, Config.Commands.GetValueOrDefault("ANT_GET", null));
            if (string.IsNullOrWhiteSpace(cmd) || cmd.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            {
                return "1"; // アンテナ非対応機種は "1" を固定返却
            }

            string cleanExpected = cmd.TrimEnd(Config.Terminator).Trim();
            string resp = ExecuteCommandWithExpectedPrefix(cmd, cleanExpected);
            if (resp.StartsWith("?;")) return "1";

            string data = StripCommandPrefix(resp, cmd);
            if (data.Length > 0)
            {
                string antCode = data[0].ToString();
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
            string? tmpl = Config.Commands.GetValueOrDefault(key, Config.Commands.GetValueOrDefault("ANT_SET", null));
            if (string.IsNullOrWhiteSpace(tmpl) || tmpl.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string antCode = Config.Antennas.GetValueOrDefault(antennaIndex, antennaIndex);
            string cmd = string.Format(tmpl, antCode);
            ExecuteCommand(cmd, expectResponse: false);
        }

        public override void SetPtt(bool txOn)
        {
            string key = txOn ? "TX_ON" : "TX_OFF";
            string defaultCmd = txOn ? "TX1;" : "TX0;";
            string cmd = Config.Commands.GetValueOrDefault(key, defaultCmd);
            ExecuteCommand(cmd, expectResponse: false);
        }

        public override bool GetPtt()
        {
            string cmd = Config.Commands.GetValueOrDefault("TX_GET", "TX;");
            string resp = ExecuteCommandWithExpectedPrefix(cmd, "TX");
            string data = StripCommandPrefix(resp, cmd);
            return data == "1" || data == "2";
        }

        public override bool GetTuner()
        {
            if (!Config.Commands.ContainsKey("TUNER_GET") && !Config.Commands.ContainsKey("AC"))
            {
                return false;
            }
            // Yaesu AC 応答: AC[P1][P2][P3]; (P3: 0=OFF, 1=ON, 2=Tuning)
            string cmd = Config.Commands.GetValueOrDefault("TUNER_GET", "AC;");
            string resp = ExecuteCommandWithExpectedPrefix(cmd, "AC");
            string data = StripCommandPrefix(resp, cmd);
            if (data.Length >= 3)
            {
                return data[2] == '1' || data[2] == '2';
            }
            return false;
        }

        public override void StartTuning()
        {
            string cmd = Config.Commands.GetValueOrDefault("TUNER_START", "AC002;");
            ExecuteCommand(cmd, expectResponse: false);
        }

        public override void SetTuner(bool tunerOn)
        {
            string key = tunerOn ? "TUNER_ON" : "TUNER_OFF";
            string defaultCmd = tunerOn ? "AC001;" : "AC000;";
            string cmd = Config.Commands.GetValueOrDefault(key, defaultCmd);

            if (Config.Commands.TryGetValue("TUNER_SET", out var tmpl))
            {
                cmd = string.Format(tmpl, tunerOn ? "001" : "000");
            }
            ExecuteCommand(cmd, expectResponse: false);
        }

        public override string GetBandwidth(VfoType vfo)
        {
            string key = vfo == VfoType.VfoA ? "BW_GET_A" : "BW_GET_B";
            string defaultCmd = vfo == VfoType.VfoA ? "SH0;" : "SH1;";
            string cmd = Config.Commands.GetValueOrDefault(key, Config.Commands.GetValueOrDefault("BW_GET", defaultCmd));
            if (string.IsNullOrEmpty(cmd)) return string.Empty;

            string cleanExpected = cmd.TrimEnd(Config.Terminator).Trim();
            string resp = ExecuteCommandWithExpectedPrefix(cmd, cleanExpected);
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
            string defaultTmpl = vfo == VfoType.VfoA ? "SH0{0};" : "SH1{0};";
            string tmpl = Config.Commands.GetValueOrDefault(key, Config.Commands.GetValueOrDefault("BW_SET", defaultTmpl));
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
            string cmd = Config.Commands.GetValueOrDefault("SM_GET", "SM0;");
            string cleanExpected = cmd.TrimEnd(Config.Terminator).Trim();
            string resp = ExecuteCommandWithExpectedPrefix(cmd, cleanExpected);
            string data = StripCommandPrefix(resp, cmd);
            if (int.TryParse(data, out int val))
            {
                int maxVal = Config.GetMeterMaxValue("SMeter", 255);
                return NormalizeMeterValue(val, maxVal);
            }
            return 0;
        }

        public override int GetPowerMeter()
        {
            string cmd = Config.Commands.GetValueOrDefault("PO_GET", "RM08;");
            string cleanExpected = cmd.TrimEnd(Config.Terminator).Trim();
            string resp = ExecuteCommandWithExpectedPrefix(cmd, cleanExpected);
            int rawVal = ParseYaesuReadMeter(resp, cmd);
            int maxVal = Config.GetMeterMaxValue("PowerMeter", 255);
            return NormalizeMeterValue(rawVal, maxVal);
        }

        public override int GetSwrMeter()
        {
            string cmd = Config.Commands.GetValueOrDefault("SWR_GET", "RM09;");
            string cleanExpected = cmd.TrimEnd(Config.Terminator).Trim();
            string resp = ExecuteCommandWithExpectedPrefix(cmd, cleanExpected);
            int rawVal = ParseYaesuReadMeter(resp, cmd);
            int maxVal = Config.GetMeterMaxValue("SwrMeter", 255);
            return NormalizeMeterValue(rawVal, maxVal);
        }

        public override int GetAlcMeter()
        {
            string cmd = Config.Commands.GetValueOrDefault("ALC_GET", "RM07;");
            string cleanExpected = cmd.TrimEnd(Config.Terminator).Trim();
            string resp = ExecuteCommandWithExpectedPrefix(cmd, cleanExpected);
            int rawVal = ParseYaesuReadMeter(resp, cmd);
            int maxVal = Config.GetMeterMaxValue("AlcMeter", 255);
            return NormalizeMeterValue(rawVal, maxVal);
        }

        /// <summary>
        /// Yaesu RM コマンド応答をパースします。
        /// FTDX9000（RM08123; -> 123）および FTDX101系（RM5123000; -> 123）の両フォーマットに対応します。
        /// </summary>
        private int ParseYaesuReadMeter(string resp, string sentCmd)
        {
            string data = StripCommandPrefix(resp, sentCmd);
            // FTDX101系で3桁の実効値の後ろに "000" パディングが付与されている場合
            if (data.Length >= 6)
            {
                data = data[..3];
            }
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