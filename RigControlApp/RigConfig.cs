using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;

namespace RigControlApp
{
    /// <summary>
    /// 通信プロトコル種別
    /// </summary>
    public enum ProtocolType
    {
        Kenwood,        // Kenwood ASCII CAT (TS-590, TS-890, TS-990 など)
        Yaesu,          // Yaesu 新型 ASCII CAT (FTDX9000, FTDX101, FT-991A, FTDX10, FT-710 など)
        Ascii,          // 汎用 ASCII CAT
        Civ,            // Icom CI-V バイナリ (0xFE 0xFE ...)
        YaesuBinary     // Yaesu 5バイト Binary CAT (FT-1000, FT-1000MP, Mark-V など)
    }

    /// <summary>
    /// リグ設定保持クラス
    /// </summary>
    public class RigConfig
    {
        // [SERIAL] シリアル通信設定
        public string PortName { get; set; } = "COM1";
        public int BaudRate { get; set; } = 9600;
        public int DataBits { get; set; } = 8;
        public Parity Parity { get; set; } = Parity.None;
        public StopBits StopBits { get; set; } = StopBits.One;
        public bool DtrEnable { get; set; } = true;
        public bool RtsEnable { get; set; } = true;
        public int ReadTimeoutMs { get; set; } = 1000;
        public int WriteTimeoutMs { get; set; } = 1000;

        // [PROTOCOL] プロトコル共通設定
        public ProtocolType Protocol { get; set; } = ProtocolType.Kenwood;
        public char Terminator { get; set; } = ';';
        public int FreqDigits { get; set; } = 11;
        public int PollIntervalMs { get; set; } = 500;
        public byte CivRigAddress { get; set; } = 0x88; // IC-7100 デフォルト
        public byte CivControllerAddress { get; set; } = 0xE0;

        // [COMMANDS]
        public Dictionary<string, string> Commands { get; } = new(StringComparer.OrdinalIgnoreCase);

        // [MODES]
        public Dictionary<string, string> ModeMap { get; } = new(StringComparer.OrdinalIgnoreCase);

        // [ANTENNAS]
        public Dictionary<string, string> Antennas { get; } = new(StringComparer.OrdinalIgnoreCase);

        // [BANDS]
        public Dictionary<string, string> Bands { get; } = new(StringComparer.OrdinalIgnoreCase);

        // [FILTERS]
        public Dictionary<string, string> Filters { get; } = new(StringComparer.OrdinalIgnoreCase);

        // [METERS]
        public Dictionary<string, int> MeterMaxValues { get; } = new(StringComparer.OrdinalIgnoreCase);

        // [RIG]
        /// <summary>
        /// リグごとの送信出力パラメータ最大値 (Kenwood: 100/200, Yaesu/Icom: 255 など)。0 の場合は出力制御非対応。
        /// </summary>
        public int PowerMax { get; set; } = 100;

        public int GetMeterMaxValue(string key, int defaultValue)
        {
            if (MeterMaxValues.TryGetValue(key, out int val)) return val;
            if (MeterMaxValues.TryGetValue(key + "Max", out int valMax)) return valMax;
            return defaultValue;
        }

        /// <summary>
        /// .ini ファイルから設定をロード
        /// </summary>
        public static RigConfig LoadFromFile(string filePath)
        {
            var config = new RigConfig();
            if (!File.Exists(filePath))
            {
                return config;
            }

            string currentSection = "";
            var lines = File.ReadAllLines(filePath);

            foreach (var rawLine in lines)
            {
                string line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#") || line.StartsWith(";"))
                {
                    continue;
                }

                // セクションヘッダ
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    currentSection = line[1..^1].Trim().ToUpperInvariant();
                    continue;
                }

                // キー = 値 のパース
                int eqIdx = line.IndexOf('=');
                if (eqIdx < 0) continue;

                string key = line[..eqIdx].Trim();
                string val = line[(eqIdx + 1)..].Trim();

                // 行末コメント除去 (#)
                int commentIdx = val.IndexOf('#');
                if (commentIdx >= 0)
                {
                    val = val[..commentIdx].Trim();
                }

                switch (currentSection)
                {
                    case "SERIAL":
                        switch (key.ToLowerInvariant())
                        {
                            case "portname": config.PortName = val; break;
                            case "baudrate": if (int.TryParse(val, out int br)) config.BaudRate = br; break;
                            case "databits": if (int.TryParse(val, out int db)) config.DataBits = db; break;
                            case "parity": if (Enum.TryParse<Parity>(val, true, out var p)) config.Parity = p; break;
                            case "stopbits": if (Enum.TryParse<StopBits>(val, true, out var sb)) config.StopBits = sb; break;
                            case "dtrenable": if (bool.TryParse(val, out bool dtr)) config.DtrEnable = dtr; break;
                            case "rtsenable": if (bool.TryParse(val, out bool rts)) config.RtsEnable = rts; break;
                            case "readtimeoutms": if (int.TryParse(val, out int rt)) config.ReadTimeoutMs = rt; break;
                            case "writetimeoutms": if (int.TryParse(val, out int wt)) config.WriteTimeoutMs = wt; break;
                        }
                        break;

                    case "PROTOCOL":
                        switch (key.ToLowerInvariant())
                        {
                            case "type": if (Enum.TryParse<ProtocolType>(val, true, out var proto)) config.Protocol = proto; break;
                            case "terminator": if (!string.IsNullOrEmpty(val)) config.Terminator = val[0]; break;
                            case "freqdigits": if (int.TryParse(val, out int fd)) config.FreqDigits = fd; break;
                            case "pollintervalms": if (int.TryParse(val, out int pi)) config.PollIntervalMs = pi; break;
                            case "civrigaddress":
                                if (byte.TryParse(val.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out byte ra))
                                    config.CivRigAddress = ra;
                                break;
                            case "civcontrolleraddress":
                                if (byte.TryParse(val.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out byte ca))
                                    config.CivControllerAddress = ca;
                                break;
                        }
                        break;

                    case "COMMANDS":
                        config.Commands[key] = val;
                        break;

                    case "MODES":
                        config.ModeMap[key] = val;
                        break;

                    case "ANTENNAS":
                        config.Antennas[key] = val;
                        break;

                    case "BANDS":
                        config.Bands[key] = val;
                        break;

                    case "FILTERS":
                        config.Filters[key] = val;
                        break;

                    case "METERS":
                        if (int.TryParse(val, out int mVal))
                        {
                            config.MeterMaxValues[key] = mVal;
                        }
                        break;

                    case "RIG":
                        if (key.Equals("PowerMax", StringComparison.OrdinalIgnoreCase))
                        {
                            if (int.TryParse(val, out int pMax))
                            {
                                config.PowerMax = pMax;
                            }
                        }
                        break;
                }
            }

            return config;
        }
    }
}