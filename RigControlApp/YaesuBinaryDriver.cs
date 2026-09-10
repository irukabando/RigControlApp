using System;
using System.Collections.Generic;
using System.Threading;

namespace RigControlApp
{
    /// <summary>
    /// Yaesu 5-Byte Binary CAT 向けドライバ (FT-1000, FT-1000MP, Mark-V 等)
    /// ini ファイル ([COMMANDS], [METERS]) の設定値を最優先で解釈して動作します。
    /// 32バイト一括返却パケット (0x10) による VFO/モード/アンテナ同期、5バイト連続返却 F7 メーター取得に対応。
    /// </summary>
    public class YaesuBinaryDriver : RigDriverBase
    {
        public override bool SupportsDualVfoRead => true;

        private long _cachedFreqA = 14074000;
        private long _cachedFreqB = 14074000;
        private string _cachedModeA = "USB";
        private string _cachedModeB = "USB";
        private string _cachedAntennaA = "1";
        private string _cachedAntennaB = "1";
        private bool _cachedPtt = false;
        private DateTime _lastStatusFetchTime = DateTime.MinValue;

        public YaesuBinaryDriver(RigConfig config) : base(config) { }

        /// <summary>
        /// ini ファイルの 16進数文字列（5バイト分）をパースして byte 配列にします。
        /// </summary>
        public static byte[] ParseHexTo5Bytes(string hexStr)
        {
            if (string.IsNullOrWhiteSpace(hexStr)) return Array.Empty<byte>();
            var parts = hexStr.Split(new[] { ' ', ',', '-', ';' }, StringSplitOptions.RemoveEmptyEntries);
            var bytes = new List<byte>();
            foreach (var p in parts)
            {
                if (byte.TryParse(p, System.Globalization.NumberStyles.HexNumber, null, out byte b))
                {
                    bytes.Add(b);
                }
            }
            return bytes.ToArray();
        }

        /// <summary>
        /// 5バイトバイナリコマンドを送信します。
        /// FT-1000MP マニュアル仕様: 送信順は DATA 1 (P4), DATA 2 (P3), DATA 3 (P2), DATA 4 (P1), DATA 5 (CMD)
        /// </summary>
        public void SendCommand(byte p4, byte p3, byte p2, byte p1, byte cmd)
        {
            lock (SyncLock)
            {
                EnsureOpen();
                byte[] packet = { p4, p3, p2, p1, cmd };
                Port!.Write(packet, 0, packet.Length);
                Thread.Sleep(30);
            }
        }

        /// <summary>
        /// ini ファイルの STATUS_GET (デフォルト: 00 00 00 03 10 = VFO A, B 読出) を送信して
        /// 32バイトの一括ステータス情報を読み出し、周波数・モード・アンテナを最新化します。
        /// </summary>
        private void EnsureStatusUpdated()
        {
            lock (SyncLock)
            {
                if (!IsOpen) return;
                if ((DateTime.Now - _lastStatusFetchTime).TotalMilliseconds < 150)
                {
                    return; // 150 ms以内のキャッシュは再利用
                }

                try
                {
                    Port!.DiscardInBuffer();

                    string hexCmd = Config.Commands.GetValueOrDefault("STATUS_GET", "00 00 00 03 10");
                    byte[] request = ParseHexTo5Bytes(hexCmd);
                    if (request.Length != 5)
                    {
                        request = new byte[] { 0x00, 0x00, 0x00, 0x03, 0x10 };
                    }

                    Port.Write(request, 0, request.Length);

                    byte[] buf = new byte[32];
                    int read = 0;
                    int elapsed = 0;

                    while (read < 32 && elapsed < 350)
                    {
                        if (Port.BytesToRead > 0)
                        {
                            read += Port.Read(buf, read, 32 - read);
                        }
                        else
                        {
                            Thread.Sleep(10);
                            elapsed += 10;
                        }
                    }

                    if (read == 32)
                    {
                        // VFO-A: offset 0~15, VFO-B: offset 16~31
                        _cachedFreqA = DecodeFrequency(buf, 1);
                        _cachedFreqB = DecodeFrequency(buf, 17);

                        _cachedModeA = DecodeModeByte(buf[7]);
                        _cachedModeB = DecodeModeByte(buf[23]);

                        _cachedAntennaA = DecodeAntennaByte(buf[9]);
                        _cachedAntennaB = DecodeAntennaByte(buf[25]);

                        _lastStatusFetchTime = DateTime.Now;
                    }
                }
                catch { }
            }
        }

        public override long GetFrequency(VfoType vfo)
        {
            EnsureStatusUpdated();
            return vfo == VfoType.VfoA ? _cachedFreqA : _cachedFreqB;
        }

        private long DecodeFrequency(byte[] buf, int offset)
        {
            string model = Config.Commands.GetValueOrDefault("YaesuModel", "FT-1000MP");
            return model switch
            {
                "FT-1000" =>
                    (((long)buf[offset] << 16) | ((long)buf[offset + 1] << 8) | buf[offset + 2]) * 10L,
                "FT-1000MP" =>
                    (long)Math.Round((((long)buf[offset] << 24) | ((long)buf[offset + 1] << 16) | ((long)buf[offset + 2] << 8) | buf[offset + 3]) * 0.625),
                "MarkVField" =>
                    (((long)buf[offset] << 24) | ((long)buf[offset + 1] << 16) | ((long)buf[offset + 2] << 8) | buf[offset + 3]) * 10L,
                _ => DecodeMarkVBcd(buf, offset) // Mark-V BCD
            };
        }

        private static long DecodeMarkVBcd(byte[] buf, int offset)
        {
            long bcd = 0;
            for (int i = 3; i >= 0; i--)
            {
                byte b = buf[offset + i];
                bcd = bcd * 100 + (((b >> 4) & 0x0F) * 10 + (b & 0x0F));
            }
            return bcd * 10;
        }

        private static string DecodeModeByte(byte mode)
        {
            // FT-1000MP 16バイトステータス +7 モード定義:
            // 0:LSB, 1:USB, 2:CW, 3:AM, 4:FM, 5:RTTY, 6:PKT
            return (mode & 0x7F) switch
            {
                0 => "LSB",
                1 => "USB",
                2 => "CW",
                3 => "AM",
                4 => "FM",
                5 => "RTTY",
                6 => "DATA-USB",
                _ => "USB"
            };
        }

        private static string DecodeAntennaByte(byte flag)
        {
            // +9 FLAG: bit 2..3 ANT SELECT (00:ANT A -> "1", 01:ANT B -> "2", 10:RX ANT -> "3")
            int ant = (flag >> 2) & 0x03;
            return ant switch
            {
                0 => "1",
                1 => "2",
                2 => "3",
                _ => "1"
            };
        }

        public override void SetFrequency(VfoType vfo, long freqHz)
        {
            var (p1, p2, p3, p4) = EncodeBcdFrequency(freqHz);
            if (vfo == VfoType.VfoA)
            {
                _cachedFreqA = freqHz;
                SendCommand(p4, p3, p2, p1, 0x0A); // VFO-A 周波数設定 (0x0A)
            }
            else
            {
                _cachedFreqB = freqHz;
                SendCommand(p4, p3, p2, p1, 0x8A); // VFO-B 周波数設定 (0x8A)
            }
        }

        public override string GetMode(VfoType vfo)
        {
            EnsureStatusUpdated();
            return vfo == VfoType.VfoA ? _cachedModeA : _cachedModeB;
        }

        public override void SetMode(VfoType vfo, string modeName)
        {
            byte modeCode;
            if (Config.ModeMap.TryGetValue(modeName, out var hexStr))
            {
                modeCode = Convert.ToByte(hexStr, 16);
            }
            else
            {
                modeCode = modeName.ToUpperInvariant() switch
                {
                    "LSB" => 0x00,
                    "USB" => 0x01,
                    "CW" or "CW-U" => 0x02,
                    "CW-R" or "CW-L" => 0x03,
                    "AM" => 0x04,
                    "FM" => 0x06,
                    "RTTY" => 0x08,
                    _ => 0x01
                };
            }

            if (vfo == VfoType.VfoA) _cachedModeA = modeName;
            else _cachedModeB = modeName;

            string key = vfo == VfoType.VfoA ? "MD_SET_A" : "MD_SET_B";
            if (Config.Commands.TryGetValue(key, out var tmpl) && !string.IsNullOrWhiteSpace(tmpl))
            {
                SendRawCommand(string.Format(tmpl, modeCode.ToString("X2")));
            }
            else
            {
                byte cmd = vfo == VfoType.VfoA ? (byte)0x0C : (byte)0x8C;
                SendCommand(0x00, 0x00, 0x00, modeCode, cmd);
            }
        }

        public override void SelectVfo(VfoType vfo)
        {
            string key = vfo == VfoType.VfoA ? "VFO_A" : "VFO_B";
            string defaultHex = vfo == VfoType.VfoA ? "00 00 00 00 05" : "00 00 00 02 05";
            string hexCmd = Config.Commands.GetValueOrDefault(key, defaultHex);
            SendRawCommand(hexCmd);
        }

        public override void SelectBand(VfoType vfo, string bandKey)
        {
            if (Config.Bands.TryGetValue(bandKey, out var bandVal))
            {
                if (long.TryParse(bandVal, out long freqHz))
                {
                    SetFrequency(vfo, freqHz);
                }
                else
                {
                    SendRawCommand(bandVal);
                }
            }
        }

        public override string GetAntenna(VfoType vfo)
        {
            EnsureStatusUpdated();
            return vfo == VfoType.VfoA ? _cachedAntennaA : _cachedAntennaB;
        }

        public override void SetAntenna(VfoType vfo, string antennaIndex)
        {
            // FT-1000MP は CAT コマンドによるアンテナ切り替えに対応していません (マニュアル仕様)
            string key = vfo == VfoType.VfoA ? "ANT_SET_A" : "ANT_SET_B";
            if (!Config.Commands.TryGetValue(key, out var tmpl) || string.IsNullOrWhiteSpace(tmpl) || tmpl.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string antCode = Config.Antennas.GetValueOrDefault(antennaIndex, antennaIndex);
            SendRawCommand(string.Format(tmpl, antCode));
        }

        public override void SetPtt(bool txOn)
        {
            _cachedPtt = txOn;
            string key = txOn ? "TX_ON" : "TX_OFF";
            if (Config.Commands.TryGetValue(key, out var hexCmd) && !string.IsNullOrWhiteSpace(hexCmd))
            {
                SendRawCommand(hexCmd);
            }
            else
            {
                byte p1 = txOn ? (byte)0x01 : (byte)0x00;
                SendCommand(0x00, 0x00, 0x00, p1, 0x0F);
            }
        }

        public override bool GetPtt() => _cachedPtt;

        public override bool GetTuner() => false;

        public override void StartTuning()
        {
            string hexCmd = Config.Commands.GetValueOrDefault("TUNER_START", "00 00 00 00 82");
            SendRawCommand(hexCmd);
        }

        public override void SetTuner(bool tunerOn)
        {
            string key = tunerOn ? "TUNER_ON" : "TUNER_OFF";
            string defaultCmd = tunerOn ? "00 00 00 01 81" : "00 00 00 00 81";
            string hexCmd = Config.Commands.GetValueOrDefault(key, defaultCmd);
            SendRawCommand(hexCmd);
        }

        public override string GetBandwidth(VfoType vfo) => string.Empty;
        public override void SetBandwidth(VfoType vfo, string bandwidthKey) { }

        public override string GetRigState() => $"Freq: {_cachedFreqA} Hz, Mode: {_cachedModeA}";

        private int ReadBinaryMeterFromConfig(string cmdKey, byte defaultParam, string meterMaxConfigKey)
        {
            byte param = defaultParam;
            if (Config.Commands.TryGetValue(cmdKey, out var hex) && !string.IsNullOrWhiteSpace(hex))
            {
                if (byte.TryParse(hex.Trim(), System.Globalization.NumberStyles.HexNumber, null, out byte b))
                {
                    param = b;
                }
            }
            return ReadBinaryMeter(param, meterMaxConfigKey);
        }

        /// <summary>
        /// FT-1000MP のメーター読み出しコマンド F7:
        /// マニュアル仕様: パラメータは P4 に配置するため、送信順序は { P4, 0x00, 0x00, 0x00, 0xF7 }。
        /// 返却: 4バイト値 + 0xF7 の計5バイト。
        /// </summary>
        private int ReadBinaryMeter(byte meterTypeParam, string meterMaxConfigKey)
        {
            lock (SyncLock)
            {
                if (!IsOpen) return 0;
                try
                {
                    Port!.DiscardInBuffer();
                    // パラメータ P4 が送信先頭バイト (DATA 1)
                    byte[] request = { meterTypeParam, 0x00, 0x00, 0x00, 0xF7 };
                    Port.Write(request, 0, request.Length);

                    byte[] buf = new byte[5];
                    int read = 0;
                    int elapsed = 0;

                    while (read < 5 && elapsed < 150)
                    {
                        if (Port.BytesToRead > 0)
                        {
                            read += Port.Read(buf, read, 5 - read);
                        }
                        else
                        {
                            Thread.Sleep(5);
                            elapsed += 5;
                        }
                    }

                    if (read >= 1)
                    {
                        int raw = buf[0];
                        int maxVal = Config.GetMeterMaxValue(meterMaxConfigKey, 255);
                        return NormalizeMeterValue(raw, maxVal);
                    }
                }
                catch { }
                return 0;
            }
        }

        public override int GetSMeter() => ReadBinaryMeterFromConfig("SM_GET", 0x00, "SMeter");
        public override int GetPowerMeter() => ReadBinaryMeterFromConfig("PO_GET", 0x80, "PowerMeter");
        public override int GetSwrMeter() => ReadBinaryMeterFromConfig("SWR_GET", 0x85, "SwrMeter");
        public override int GetAlcMeter() => ReadBinaryMeterFromConfig("ALC_GET", 0x81, "AlcMeter");

        public override int GetAfGain() => 0;
        public override void SetAfGain(int gainValue) { }

        public override string SendRawCommand(string raw)
        {
            lock (SyncLock)
            {
                EnsureOpen();
                byte[] bytes = ParseHexTo5Bytes(raw);
                if (bytes.Length != 5) return "Invalid 5-Byte format";

                Port!.DiscardInBuffer();
                Port.Write(bytes, 0, 5);
                Thread.Sleep(30);
                return "OK";
            }
        }

        private static (byte p1, byte p2, byte p3, byte p4) EncodeBcdFrequency(long freqHz)
        {
            long val = freqHz / 10;
            string s = val.ToString("D8");
            byte p1 = (byte)(((s[0] - '0') << 4) | (s[1] - '0'));
            byte p2 = (byte)(((s[2] - '0') << 4) | (s[3] - '0'));
            byte p3 = (byte)(((s[4] - '0') << 4) | (s[5] - '0'));
            byte p4 = (byte)(((s[6] - '0') << 4) | (s[7] - '0'));
            return (p1, p2, p3, p4);
        }
    }
}