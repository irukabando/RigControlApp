using System;
using System.Threading;

namespace RigControlApp
{
    /// <summary>
    /// Yaesu 5-Byte Binary CAT プロトコル向けドライバー (FT-1000, FT-1000MP, Mark-V 等)
    /// </summary>
    public class YaesuBinaryDriver : RigDriverBase
    {
        public override bool SupportsDualVfoRead => true;

        private long _cachedFreqA = 14074000;
        private long _cachedFreqB = 14074000;
        private string _cachedMode = "USB";

        public YaesuBinaryDriver(RigConfig config) : base(config) { }

        /// <summary>
        /// 5バイトパケット送信
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

        public override long GetFrequency(VfoType vfo)
        {
            lock (SyncLock)
            {
                if (!IsOpen) return vfo == VfoType.VfoA ? _cachedFreqA : _cachedFreqB;

                try
                {
                    Port!.DiscardInBuffer();
                    // ステータス更新要求: 00 00 00 03 10
                    byte[] request = { 0x00, 0x00, 0x00, 0x03, 0x10 };
                    Port.Write(request, 0, request.Length);

                    byte[] buf = new byte[32];
                    int read = 0;
                    int elapsed = 0;
                    while (read < 32 && elapsed < 200)
                    {
                        if (Port.BytesToRead > 0)
                            read += Port.Read(buf, read, 32 - read);
                        else
                        {
                            Thread.Sleep(10);
                            elapsed += 10;
                        }
                    }

                    if (read == 32)
                    {
                        // VFO-A: offset 1, VFO-B: offset 17
                        _cachedFreqA = DecodeFrequency(buf, 1);
                        _cachedFreqB = DecodeFrequency(buf, 17);
                    }
                }
                catch { }

                return vfo == VfoType.VfoA ? _cachedFreqA : _cachedFreqB;
            }
        }

        private long DecodeFrequency(byte[] buf, int offset)
        {
            Config.Commands.TryGetValue("YaesuModel", out var model);
            return model switch
            {
                "FT-1000" =>
                    (((long)buf[offset] << 16) | ((long)buf[offset + 1] << 8) | buf[offset + 2]) * 10L,
                "FT-1000MP" =>
                    (long)Math.Round((((long)buf[offset] << 24) | ((long)buf[offset + 1] << 16) | ((long)buf[offset + 2] << 8) | buf[offset + 3]) / 1.60),
                "MarkVField" =>
                    (((long)buf[offset] << 24) | ((long)buf[offset + 1] << 16) | ((long)buf[offset + 2] << 8) | buf[offset + 3]) * 10L,
                _ => DecodeMarkVBcd(buf, offset) // MarkV デフォルト
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

        public override void SetFrequency(VfoType vfo, long freqHz)
        {
            var (p1, p2, p3, p4) = EncodeBcdFrequency(freqHz);
            if (vfo == VfoType.VfoA)
            {
                _cachedFreqA = freqHz;
                SendCommand(p4, p3, p2, p1, 0x0A);
            }
            else
            {
                _cachedFreqB = freqHz;
                SendCommand(p4, p3, p2, p1, 0x8A);
            }
        }

        public override string GetMode(VfoType vfo) => _cachedMode;

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

            _cachedMode = modeName;
            string key = vfo == VfoType.VfoA ? "MD_SET_A" : "MD_SET_B";
            if (Config.Commands.TryGetValue(key, out var tmpl))
            {
                SendRawCommand(string.Format(tmpl, modeCode.ToString("X2")));
            }
            else
            {
                SendCommand(0x00, 0x00, 0x00, modeCode, 0x0C);
            }
        }

        public override void SelectVfo(VfoType vfo)
        {
            string key = vfo == VfoType.VfoA ? "VFO_A" : "VFO_B";
            string defaultHex = vfo == VfoType.VfoA ? "00 00 00 00 05" : "00 00 00 01 05";
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

        public override string GetAntenna(VfoType vfo) => string.Empty;

        public override void SetAntenna(VfoType vfo, string antennaIndex)
        {
            string antCode = Config.Antennas.GetValueOrDefault(antennaIndex, antennaIndex);
            string key = vfo == VfoType.VfoA ? "ANT_SET_A" : "ANT_SET_B";
            string tmpl = Config.Commands.GetValueOrDefault(key, Config.Commands.GetValueOrDefault("ANT_SET", ""));
            if (!string.IsNullOrEmpty(tmpl))
            {
                SendRawCommand(string.Format(tmpl, antCode));
            }
        }

        public override void SetPtt(bool txOn)
        {
            byte p1 = txOn ? (byte)0x01 : (byte)0x00;
            SendCommand(0x00, 0x00, 0x00, p1, 0x0F);
        }

        public override bool GetPtt() => false;

        public override bool GetTuner() => false;

        public override void SetTuner(bool tunerOn) { }

        public override string GetBandwidth(VfoType vfo) => string.Empty;

        public override void SetBandwidth(VfoType vfo, string bandwidthKey) { }

        public override string GetRigState() => $"Freq: {_cachedFreqA} Hz, Mode: {_cachedMode}";

        public override int GetSMeter()
        {
            lock (SyncLock)
            {
                if (!IsOpen) return 0;

                try
                {
                    Port!.DiscardInBuffer();
                    byte[] request = { 0x00, 0x00, 0x00, 0x00, 0xF7 };
                    Port.Write(request, 0, request.Length);

                    byte[] buf = new byte[5];
                    int read = 0;
                    int elapsed = 0;
                    while (read < 5 && elapsed < 100)
                    {
                        if (Port.BytesToRead > 0)
                            read += Port.Read(buf, read, 5 - read);
                        else
                        {
                            Thread.Sleep(5);
                            elapsed += 5;
                        }
                    }

                    if (read >= 1) return NormalizeMeterValue(buf[0], Config.SMeterMax);
                }
                catch { }

                return 0;
            }
        }

        public override int GetPowerMeter() => 0;

        public override int GetSwrMeter() => 0;

        public override int GetAlcMeter() => 0;

        public override int GetAfGain() => 0;

        public override void SetAfGain(int gainValue) { }

        public override string SendRawCommand(string raw)
        {
            lock (SyncLock)
            {
                EnsureOpen();
                string[] hexParts = raw.Split(new[] { ' ', ',', ';', '-' }, StringSplitOptions.RemoveEmptyEntries);
                if (hexParts.Length != 5) return "Invalid 5-Byte format";

                byte[] bytes = new byte[5];
                for (int i = 0; i < 5; i++)
                    bytes[i] = Convert.ToByte(hexParts[i], 16);

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