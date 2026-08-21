using System;
using System.Threading;

namespace RigControlApp
{
    /// <summary>
    /// Yaesu 5-Byte Binary CAT ドライバ
    /// </summary>
    public class YaesuBinaryDriver : RigDriverBase
    {
        public override bool SupportsDualVfoRead => true;

        private long _cachedFreqA = 14074000;
        private long _cachedFreqB = 14074000;
        private string _cachedMode = "USB";

        public YaesuBinaryDriver(RigConfig config) : base(config) { }

        public void SendCommand(byte p4, byte p3, byte p2, byte p1, byte cmd)
        {
            lock (SyncLock)
            {
                if (!IsOpen) throw new InvalidOperationException("ポートが開いていません。");

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
                    // ステータス要求: 00 00 00 03 10
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
                // 3バイト (buf[1..3] / buf[17..19]) * 10
                "FT-1000" =>
                    (((long)buf[offset] << 16) | ((long)buf[offset + 1] << 8) | buf[offset + 2]) * 10L,

                // 4バイト / 1.60
                "FT-1000MP" =>
                    (long)Math.Round((((long)buf[offset] << 24) | ((long)buf[offset + 1] << 16) | ((long)buf[offset + 2] << 8) | buf[offset + 3]) / 1.60),

                // 4バイト BCD (下位桁から順にパック)
                "MarkV" =>
                    DecodeMarkVBcd(buf, offset),

                // 4バイト * 10
                "MarkVField" =>
                    (((long)buf[offset] << 24) | ((long)buf[offset + 1] << 16) | ((long)buf[offset + 2] << 8) | buf[offset + 3]) * 10L,

                _ => DecodeMarkVBcd(buf, offset)
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
                SendCommand(p4, p3, p2, p1, 0x0A); // VFO-A 設定
            }
            else
            {
                _cachedFreqB = freqHz;
                SendCommand(p4, p3, p2, p1, 0x8A); // VFO-B 設定
            }
        }

        public override string GetMode() => _cachedMode;

        public override void SetMode(string modeName)
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
                    "RTTY" or "RTTY-L" => 0x08,
                    "RTTY-U" => 0x09,
                    "PKT" or "PKT-L" or "DATA-USB" or "DATA-LSB" => 0x0A,
                    "PKT-FM" or "FM-N" => 0x0B,
                    _ => 0x01
                };
            }

            _cachedMode = modeName;
            SendCommand(0x00, 0x00, 0x00, modeCode, 0x0C);
        }

        public override void SelectVfo(VfoType vfo)
        {
            string key = vfo == VfoType.VfoA ? "VFO_A" : "VFO_B";
            string defaultHex = vfo == VfoType.VfoA ? "00 00 00 00 05" : "00 00 00 01 05";
            string hexCmd = Config.Commands.GetValueOrDefault(key, defaultHex);
            SendRawCommand(hexCmd);
        }

        // 新設: バンド選択コマンドの送信
        public override void SelectBand(string bandKey)
        {
            if (Config.Bands.TryGetValue(bandKey, out var cmd) && !string.IsNullOrEmpty(cmd))
            {
                SendRawCommand(cmd);
            }
        }

        public override void SetPtt(bool txOn)
        {
            byte p1 = txOn ? (byte)0x01 : (byte)0x00;
            SendCommand(0x00, 0x00, 0x00, p1, 0x0F);
        }

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

                    if (read >= 1) return buf[0];
                }
                catch { }

                return 0;
            }
        }

        public override int GetAfGain() => 0;

        public override void SetAfGain(int gainValue) { }

        public override string SendRawCommand(string raw)
        {
            lock (SyncLock)
            {
                if (!IsOpen) throw new InvalidOperationException("ポートが開いていません。");

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