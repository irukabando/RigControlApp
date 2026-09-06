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
        Yaesu,          // Yaesu 新型 ASCII CAT (FTDX101, FT-991A, FTDX10, FT-710 など)
        Ascii,          // 汎用 ASCII CAT
        Civ,            // Icom CI-V バイナリ (0xFE 0xFE ...)
        YaesuBinary     // Yaesu 5バイト Binary CAT (FT-1000, FT-1000MP など)
    }

    /// <summary>
    /// リグ設定保持クラス
    /// </summary>
    public class RigConfig
    {
        // --- シリアル通信設定 ---
        public string PortName { get; set; } = "COM3";
        public int BaudRate { get; set; } = 38400;
        public int DataBits { get; set; } = 8;
        public Parity Parity { get; set; } = Parity.None;
        public StopBits StopBits { get; set; } = StopBits.One;
        public bool DtrEnable { get; set; } = true;
        public bool RtsEnable { get; set; } = true;
        public int ReadTimeoutMs { get; set; } = 1000;
        public int WriteTimeoutMs { get; set; } = 1000;

        // --- プロトコル共通設定 ---
        public ProtocolType Protocol { get; set; } = ProtocolType.Kenwood;
        public char Terminator { get; set; } = ';';
        public int FreqDigits { get; set; } = 11;
        public byte CivRigAddress { get; set; } = 0x94;
        public byte CivControllerAddress { get; set; } = 0xE0;
        public int PollIntervalMs { get; set; } = 500;

        // --- マップ設定 (.ini から動的読み込み) ---
        public Dictionary<string, string> Meters { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Commands { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> ModeMap { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Bands { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Antennas { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Filters { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// .ini ファイルから設定をロード
        /// </summary>
        public static RigConfig LoadFromFile(string filePath)
        {
            // 設定ファイルの検索
            if (!File.Exists(filePath))
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string fileNameOnly = Path.GetFileName(filePath);
                string fileNameWithoutExt = Path.GetFileNameWithoutExtension(filePath);
                string withExt = filePath.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) ? filePath : filePath + ".ini";

                string[] candidates = new[]
                {
                    withExt,
                    Path.Combine(baseDir, filePath),
                    Path.Combine(baseDir, withExt),
                    Path.Combine(baseDir, "config", fileNameOnly),
                    Path.Combine(baseDir, "config", fileNameWithoutExt + ".ini"),
                    Path.Combine("config", fileNameOnly),
                    Path.Combine("config", fileNameWithoutExt + ".ini"),
                    Path.Combine(baseDir, "config.ini"),
                    Path.Combine(baseDir, "config", "config.ini")
                };

                bool found = false;
                foreach (var cand in candidates)
                {
                    if (File.Exists(cand))
                    {
                        filePath = cand;
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    throw new FileNotFoundException($"設定ファイルが見つかりません: {filePath}\n探索先: {baseDir}");
                }
            }

            var config = new RigConfig();
            string currentSection = "";

            foreach (var rawLine in File.ReadAllLines(filePath))
            {
                string line = rawLine.Trim();

                // コメント・空行スキップ (#, ;)
                if (string.IsNullOrEmpty(line) || line.StartsWith("#") || line.StartsWith(";"))
                    continue;

                // セクションヘッダ
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    currentSection = line[1..^1].Trim().ToUpperInvariant();
                    continue;
                }

                // キー = 値 のパース
                int eqIdx = line.IndexOf('=');
                if (eqIdx <= 0) continue;

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
                        ParseSerialConfig(config, key, val);
                        break;
                    case "PROTOCOL":
                        ParseProtocolConfig(config, key, val);
                        break;
                    case "METERS":
                    case "METER":
                        config.Meters[key] = val;
                        break;
                    case "COMMANDS":
                        config.Commands[key] = val;
                        break;
                    case "MODES":
                        config.ModeMap[key] = val;
                        break;
                    case "BANDS":
                        config.Bands[key] = val;
                        break;
                    case "ANTENNAS":
                        config.Antennas[key] = val;
                        break;
                    case "FILTERS":
                    case "FILTER":
                    case "BANDWIDTHS":
                        config.Filters[key] = val;
                        break;
                }
            }

            return config;
        }

        /// <summary>
        /// [SERIAL] セクションのパース
        /// </summary>
        private static void ParseSerialConfig(RigConfig config, string key, string val)
        {
            if (key.Equals("PortName", StringComparison.OrdinalIgnoreCase)) config.PortName = val;
            else if (key.Equals("BaudRate", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out int br)) config.BaudRate = br;
            else if (key.Equals("DataBits", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out int db)) config.DataBits = db;
            else if (key.Equals("Parity", StringComparison.OrdinalIgnoreCase) && Enum.TryParse<Parity>(val, true, out var par)) config.Parity = par;
            else if (key.Equals("StopBits", StringComparison.OrdinalIgnoreCase) && Enum.TryParse<StopBits>(val, true, out var sb)) config.StopBits = sb;
            else if (key.Equals("DtrEnable", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out bool dtr)) config.DtrEnable = dtr;
            else if (key.Equals("RtsEnable", StringComparison.OrdinalIgnoreCase) && bool.TryParse(val, out bool rts)) config.RtsEnable = rts;
            else if (key.Equals("ReadTimeoutMs", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out int rt)) config.ReadTimeoutMs = rt;
            else if (key.Equals("WriteTimeoutMs", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out int wt)) config.WriteTimeoutMs = wt;
        }

        /// <summary>
        /// [PROTOCOL] セクションのパース
        /// </summary>
        private static void ParseProtocolConfig(RigConfig config, string key, string val)
        {
            if (key.Equals("Type", StringComparison.OrdinalIgnoreCase))
            {
                if (val.Equals("Kenwood", StringComparison.OrdinalIgnoreCase))
                {
                    config.Protocol = ProtocolType.Kenwood;
                }
                else if (val.Equals("Yaesu", StringComparison.OrdinalIgnoreCase))
                {
                    config.Protocol = ProtocolType.Yaesu;
                }
                else if (val.Equals("CIV", StringComparison.OrdinalIgnoreCase) || val.Equals("Icom", StringComparison.OrdinalIgnoreCase))
                {
                    config.Protocol = ProtocolType.Civ;
                }
                else if (val.Contains("YaesuBinary", StringComparison.OrdinalIgnoreCase) || val.Contains("Binary", StringComparison.OrdinalIgnoreCase))
                {
                    config.Protocol = ProtocolType.YaesuBinary;
                }
                else if (Enum.TryParse<ProtocolType>(val, true, out var proto))
                {
                    config.Protocol = proto;
                }
                else
                {
                    config.Protocol = ProtocolType.Ascii;
                }
            }
            else if (key.Equals("Terminator", StringComparison.OrdinalIgnoreCase))
            {
                config.Terminator = val.Length > 0 ? val[0] : ';';
            }
            else if (key.Equals("FreqDigits", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out int fd))
            {
                config.FreqDigits = fd;
            }
            else if (key.Equals("CivRigAddress", StringComparison.OrdinalIgnoreCase))
            {
                config.CivRigAddress = Convert.ToByte(val, 16);
            }
            else if (key.Equals("CivControllerAddress", StringComparison.OrdinalIgnoreCase))
            {
                config.CivControllerAddress = Convert.ToByte(val, 16);
            }
            else if (key.Equals("PollIntervalMs", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out int pi))
            {
                config.PollIntervalMs = Math.Max(50, pi);
            }
        }
    }
}