using System;
using System.Collections.Generic;

namespace RigControlApp
{
    /// <summary>
    /// Yaesu 新型 ASCII CAT (FTDX101, FT-991A, FTDX10, FT-710 など)
    /// </summary>
    public class YaesuCatDriver : AsciiCatDriverBase
    {
        public YaesuCatDriver(RigConfig config) : base(config) { }

        public override string GetMode(VfoType vfo)
        {
            // Yaesu: MD0; (VFO-A) / MD1; (VFO-B) -> MD0[P2];
            string key = vfo == VfoType.VfoA ? "MD_GET_A" : "MD_GET_B";
            string defaultCmd = vfo == VfoType.VfoA ? "MD0;" : "MD1;";
            string cmd = Config.Commands.GetValueOrDefault(key, Config.Commands.GetValueOrDefault("MD_GET", defaultCmd));
            string resp = ExecuteCommand(cmd);
            // 応答例: "MD02;" -> "2" または "C"
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
            // Yaesu VS コマンド: VS0; (MAIN), VS1; (SUB)
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
                        // Yaesu BS コマンド: BS05; で 14MHz
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
            // Yaesu AN コマンド: AN0; -> AN0[P2]0; (P2: 1=ANT1, 2=ANT2, 3=ANT3/RX)
            string key = vfo == VfoType.VfoA ? "ANT_GET_A" : "ANT_GET_B";
            string defaultCmd = vfo == VfoType.VfoA ? "AN0;" : "AN1;";
            string cmd = Config.Commands.GetValueOrDefault(key, Config.Commands.GetValueOrDefault("ANT_GET", defaultCmd));
            if (string.IsNullOrEmpty(cmd)) return string.Empty;

            string resp = ExecuteCommand(cmd);
            string data = StripCommandPrefix(resp, cmd);

            if (data.Length > 0)
            {
                string antCode = data[0].ToString(); // P2 (アンテナ番号)
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
            // Yaesu AN コマンド: AN[P1][P2]; (P1: 0=MAIN, 1=SUB / P2: 1~3)
            string antCode = Config.Antennas.GetValueOrDefault(antennaIndex, antennaIndex);
            string key = vfo == VfoType.VfoA ? "ANT_SET_A" : "ANT_SET_B";
            string defaultTmpl = vfo == VfoType.VfoA ? "AN0{0};" : "AN1{0};";
            string tmpl = Config.Commands.GetValueOrDefault(key, Config.Commands.GetValueOrDefault("ANT_SET", defaultTmpl));
            string cmd = string.Format(tmpl, antCode);
            ExecuteCommand(cmd, expectResponse: false);
        }

        public override void SetPtt(bool txOn)
        {
            // Yaesu TX コマンド: TX1; (CAT TX ON), TX0; (TX OFF)
            string cmd = txOn
                ? Config.Commands.GetValueOrDefault("TX_ON", "TX1;")
                : Config.Commands.GetValueOrDefault("TX_OFF", "TX0;");
            ExecuteCommand(cmd, expectResponse: false);
        }

        public override bool GetPtt()
        {
            // Yaesu TX コマンド: TX; -> TX[P1]; (0=OFF, 1=ON, 2=ON)
            string cmd = Config.Commands.GetValueOrDefault("TX_GET", "TX;");
            string resp = ExecuteCommand(cmd);
            string data = StripCommandPrefix(resp, cmd);
            return data == "1" || data == "2";
        }

        public override bool GetTuner()
        {
            if (!Config.Commands.ContainsKey("TUNER_GET") && !Config.Commands.ContainsKey("AC"))
            {
                return false;
            }

            // Yaesu AC コマンド: AC; -> AC[P1][P2][P3]; (P3: 0=OFF, 1=ON, 2=Tuning)
            string cmd = Config.Commands.GetValueOrDefault("TUNER_GET", "AC;");
            string resp = ExecuteCommand(cmd);
            string data = StripCommandPrefix(resp, cmd);

            if (data.Length >= 3)
            {
                return data[2] == '1' || data[2] == '2';
            }
            return false;
        }

        public override void SetTuner(bool tunerOn)
        {
            // Yaesu AC コマンド: AC001; (ON), AC000; (OFF), AC002; (Tune)
            string cmd;
            if (tunerOn)
            {
                cmd = Config.Commands.GetValueOrDefault("TUNER_ON", "AC001;");
            }
            else
            {
                cmd = Config.Commands.GetValueOrDefault("TUNER_OFF", "AC000;");
            }

            if (Config.Commands.TryGetValue("TUNER_SET", out var tmpl))
            {
                cmd = string.Format(tmpl, tunerOn ? "001" : "000");
            }
            ExecuteCommand(cmd, expectResponse: false);
        }

        public override string GetBandwidth(VfoType vfo)
        {
            string key = vfo == VfoType.VfoA ? "BW_GET_A" : "BW_GET_B";
            string cmd = Config.Commands.GetValueOrDefault(key, Config.Commands.GetValueOrDefault("BW_GET", "SH0;"));
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
            string tmpl = Config.Commands.GetValueOrDefault(key, Config.Commands.GetValueOrDefault("BW_SET", "SH00{0:D2};"));
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
            // Yaesu SM0; -> SM0[000~255];
            string cmd = Config.Commands.GetValueOrDefault("SM_GET", "SM0;");
            string resp = ExecuteCommand(cmd);
            string data = StripCommandPrefix(resp, cmd);
            if (int.TryParse(data, out int val))
            {
                int maxVal = int.TryParse(Config.Meters.GetValueOrDefault("SMeter", "255"), out int max) ? max : 255;
                return NormalizeMeterValue(val, maxVal);
            }
            return 0;
        }

        public override int GetPowerMeter()
        {
            // Yaesu RM5; (POW) -> RM5[3桁 P2][3桁 000];
            string cmd = Config.Commands.GetValueOrDefault("PO_GET", "RM5;");
            string resp = ExecuteCommand(cmd);
            int rawVal = ParseYaesuReadMeter(resp, cmd);
            int maxVal = int.TryParse(Config.Meters.GetValueOrDefault("PowerMeter", "255"), out int max) ? max : 255;
            return NormalizeMeterValue(rawVal, maxVal);
        }

        public override int GetSwrMeter()
        {
            // Yaesu RM6; (SWR) -> RM6[3桁 P2][3桁 000];
            string cmd = Config.Commands.GetValueOrDefault("SWR_GET", "RM6;");
            string resp = ExecuteCommand(cmd);
            int rawVal = ParseYaesuReadMeter(resp, cmd);
            int maxVal = int.TryParse(Config.Meters.GetValueOrDefault("SwrMeter", "255"), out int max) ? max : 255;
            return NormalizeMeterValue(rawVal, maxVal);
        }

        public override int GetAlcMeter()
        {
            // Yaesu RM4; (ALC) -> RM4[3桁 P2][3桁 000];
            string cmd = Config.Commands.GetValueOrDefault("ALC_GET", "RM4;");
            string resp = ExecuteCommand(cmd);
            int rawVal = ParseYaesuReadMeter(resp, cmd);
            int maxVal = int.TryParse(Config.Meters.GetValueOrDefault("AlcMeter", "255"), out int max) ? max : 255;
            return NormalizeMeterValue(rawVal, maxVal);
        }

        /// <summary>
        /// Yaesu RM 応答のパース (P2: 3桁 メータ値 + P3: 3桁 000)
        /// </summary>
        private int ParseYaesuReadMeter(string resp, string sentCmd)
        {
            string data = StripCommandPrefix(resp, sentCmd);
            // 6桁応答の場合 (P2: メータ値のみ取得)
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
            // Yaesu AG0; -> AG0[000~255];
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