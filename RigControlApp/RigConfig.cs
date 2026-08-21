using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;

namespace RigControlApp
{
    public enum ProtocolType
    {
        Ascii,
        Civ,
        YaesuBinary
    }

    public class RigConfig
    {
        public string PortName { get; set; } = "COM3";
        public int BaudRate { get; set; } = 38400;
        public int DataBits { get; set; } = 8;
        public Parity Parity { get; set; } = Parity.None;
        public StopBits StopBits { get; set; } = StopBits.One;
        public bool DtrEnable { get; set; } = true;
        public bool RtsEnable { get; set; } = true;
        public int ReadTimeoutMs { get; set; } = 1000;
        public int WriteTimeoutMs { get; set; } = 1000;

        public ProtocolType Protocol { get; set; } = ProtocolType.Ascii;
        public char Terminator { get; set; } = ';';
        public int FreqDigits { get; set; } = 11;
        public byte CivRigAddress { get; set; } = 0x94;
        public byte CivControllerAddress { get; set; } = 0xE0;

        public Dictionary<string, string> Commands { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> ModeMap { get; } = new(StringComparer.OrdinalIgnoreCase);
        // 新設: バンド名とバンド切替コマンドの対応マップ
        public Dictionary<string, string> Bands { get; } = new(StringComparer.OrdinalIgnoreCase);

        public static RigConfig LoadFromFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"設定ファイルが見つかりません: {filePath}");
            }

            var config = new RigConfig();
            string currentSection = "";

            foreach (var rawLine in File.ReadAllLines(filePath))
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#") || line.StartsWith(";"))
                    continue;

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    currentSection = line[1..^1].Trim().ToUpperInvariant();
                    continue;
                }

                int eqIdx = line.IndexOf('=');
                if (eqIdx <= 0) continue;

                string key = line[..eqIdx].Trim();
                string val = line[(eqIdx + 1)..].Trim();

                switch (currentSection)
                {
                    case "SERIAL":
                        ParseSerialConfig(config, key, val);
                        break;
                    case "PROTOCOL":
                        ParseProtocolConfig(config, key, val);
                        break;
                    case "COMMANDS":
                        config.Commands[key] = val;
                        break;
                    case "MODES":
                        config.ModeMap[key] = val;
                        break;
                    case "BANDS": // 新規追加: BANDS セクションのパース
                        config.Bands[key] = val;
                        break;
                }
            }

            return config;
        }

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

        private static void ParseProtocolConfig(RigConfig config, string key, string val)
        {
            if (key.Equals("Type", StringComparison.OrdinalIgnoreCase))
            {
                if (Enum.TryParse<ProtocolType>(val, true, out var proto))
                {
                    config.Protocol = proto;
                }
                else if (val.Equals("CIV", StringComparison.OrdinalIgnoreCase))
                {
                    config.Protocol = ProtocolType.Civ;
                }
                else if (val.Contains("Yaesu", StringComparison.OrdinalIgnoreCase) || val.Contains("5Byte", StringComparison.OrdinalIgnoreCase))
                {
                    config.Protocol = ProtocolType.YaesuBinary;
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
        }
    }
}