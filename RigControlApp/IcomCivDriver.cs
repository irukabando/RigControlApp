using System;
using System.Collections.Generic;
using System.Threading;

namespace RigControlApp
{
    /// <summary>
    /// Icom CI-V バイナリプロトコル向けドライバー
    /// </summary>
    public class IcomCivDriver : RigDriverBase
    {
        public override bool SupportsDualVfoRead => false;

        private const byte Preamble = 0xFE;
        private const byte EndByte = 0xFD;

        public IcomCivDriver(RigConfig config) : base(config) { }

        /// <summary>
        /// CI-V フレームを送信 (FE FE [RigAddr] [CtrlAddr] [Payload...] FD)
        /// </summary>
        private List<byte> SendFrame(byte[] payload, bool expectReply = true)
        {
            lock (SyncLock)
            {
                EnsureOpen();
                Port!.DiscardInBuffer();

                var frame = new List<byte> { Preamble, Preamble, Config.CivRigAddress, Config.CivControllerAddress };
                frame.AddRange(payload);
                frame.Add(EndByte);

                Port.Write(frame.ToArray(), 0, frame.Count);

                if (!expectReply) return new List<byte>();

                var received = new List<byte>();
                var startTime = DateTime.Now;

                while ((DateTime.Now - startTime).TotalMilliseconds < Config.ReadTimeoutMs)
                {
                    if (Port.BytesToRead > 0)
                    {
                        byte b = (byte)Port.ReadByte();
                        received.Add(b);

                        if (b == EndByte && received.Count >= 6)
                        {
                            for (int i = 0; i <= received.Count - 6; i++)
                            {
                                if (received[i] == Preamble && received[i + 1] == Preamble &&
                                    received[i + 2] == Config.CivControllerAddress &&
                                    received[i + 3] == Config.CivRigAddress)
                                {
                                    int endIdx = received.IndexOf(EndByte, i + 4);
                                    if (endIdx != -1)
                                    {
                                        return received.GetRange(i, endIdx - i + 1);
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        Thread.Sleep(5);
                    }
                }

                return received;
            }
        }

        public override long GetFrequency(VfoType vfo)
        {
            var reply = SendFrame(new byte[] { 0x03 });
            return ParseFreqFromCivFrame(reply);
        }

        public override void SetFrequency(VfoType vfo, long freqHz)
        {
            byte[] bcd = FreqToBcd5(freqHz);
            var payload = new byte[1 + bcd.Length];
            payload[0] = 0x05;
            Array.Copy(bcd, 0, payload, 1, bcd.Length);
            SendFrame(payload, expectReply: false);
        }

        public override string GetMode(VfoType vfo)
        {
            var reply = SendFrame(new byte[] { 0x04 });
            if (reply.Count >= 6)
            {
                int cmdIdx = 4;
                if (reply[cmdIdx] == 0x04 && reply.Count > cmdIdx + 1)
                {
                    byte modeByte = reply[cmdIdx + 1];
                    string hex = modeByte.ToString("X2");

                    foreach (var kvp in Config.ModeMap)
                    {
                        if (kvp.Value.Equals(hex, StringComparison.OrdinalIgnoreCase))
                        {
                            return kvp.Key;
                        }
                    }
                    return $"Mode 0x{hex}";
                }
            }
            return "Unknown";
        }

        public override void SetMode(VfoType vfo, string modeName)
        {
            if (Config.ModeMap.TryGetValue(modeName, out var codeHex))
            {
                string key = vfo == VfoType.VfoA ? "MD_SET_A" : "MD_SET_B";
                string defaultHex = "06 {0} 01";
                string tmpl = Config.Commands.GetValueOrDefault(key, Config.Commands.GetValueOrDefault("MD_SET", defaultHex));
                string formatted = string.Format(tmpl, codeHex);
                SendRawCommand(formatted);
            }
        }

        public override void SelectVfo(VfoType vfo)
        {
            string key = vfo == VfoType.VfoA ? "VFO_A" : "VFO_B";
            string defaultHex = vfo == VfoType.VfoA ? "07 00" : "07 01";
            string hexCmd = Config.Commands.GetValueOrDefault(key, defaultHex);
            SendRawCommand(hexCmd);
        }

        public override void SelectBand(VfoType vfo, string bandKey)
        {
            if (Config.Bands.TryGetValue(bandKey, out var bandVal))
            {
                string key = vfo == VfoType.VfoA ? "BAND_SET_A" : "BAND_SET_B";
                string tmpl = Config.Commands.GetValueOrDefault(key, Config.Commands.GetValueOrDefault("BAND_SET", "01 {0}"));

                if (!string.IsNullOrEmpty(tmpl))
                {
                    // CI-V の独自バンド選択コマンド (01 [BandCode]) を送信
                    SendRawCommand(string.Format(tmpl, bandVal));
                }
                else if (long.TryParse(bandVal, out long freqHz))
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
            // CI-V アンテナ状態問い合わせコマンド (0x12) を送信
            var reply = SendFrame(new byte[] { 0x12 });

            // 受信フレーム検証: [FE FE TO FROM 12 (ANT) ...]
            if (reply.Count >= 6)
            {
                int cmdIdx = 4;
                if (reply[cmdIdx] == 0x12 && reply.Count > cmdIdx + 1)
                {
                    byte antByte = reply[cmdIdx + 1];
                    string codeHex = antByte.ToString("X2"); // "00", "01", ...
                    string codeDec = antByte.ToString();     // "0", "1", ...

                    // [ANTENNAS] マッピングがある場合は逆引き (例: 1=0 や ANT_1=00)
                    foreach (var kvp in Config.Antennas)
                    {
                        if (kvp.Value.Equals(codeHex, StringComparison.OrdinalIgnoreCase) ||
                            kvp.Value.Equals(codeDec, StringComparison.OrdinalIgnoreCase) ||
                            kvp.Value.PadLeft(2, '0').Equals(codeHex, StringComparison.OrdinalIgnoreCase))
                        {
                            return kvp.Key.StartsWith("ANT_", StringComparison.OrdinalIgnoreCase)
                                ? kvp.Key[4..]
                                : kvp.Key;
                        }
                    }

                    // マッピング未定義時は 0-based (0x00) を 1-based ("1", "2"...) に変換して返却
                    return (antByte + 1).ToString();
                }
            }

            return string.Empty;
        }

        public override void SetAntenna(VfoType vfo, string antennaIndex)
        {
            string antCode = Config.Antennas.GetValueOrDefault(antennaIndex, antennaIndex);
            string key = vfo == VfoType.VfoA ? "ANT_SET_A" : "ANT_SET_B";
            string tmpl = Config.Commands.GetValueOrDefault(key, Config.Commands.GetValueOrDefault("ANT_SET", "12 0{0}"));
            SendRawCommand(string.Format(tmpl, antCode));
        }

        public override void SetPtt(bool txOn)
        {
            byte state = (byte)(txOn ? 0x01 : 0x00);
            SendFrame(new byte[] { 0x1C, 0x00, state }, expectReply: false);
        }

        public override bool GetPtt()
        {
            var reply = SendFrame(new byte[] { 0x1C, 0x00 });
            if (reply.Count >= 7 && reply[4] == 0x1C && reply[5] == 0x00)
            {
                return reply[6] == 0x01;
            }
            return false;
        }

        public override bool GetTuner()
        {
            var reply = SendFrame(new byte[] { 0x1C, 0x01 });
            if (reply.Count >= 7 && reply[4] == 0x1C && reply[5] == 0x01)
            {
                return reply[6] == 0x01 || reply[6] == 0x02;
            }
            return false;
        }

        public override void SetTuner(bool tunerOn)
        {
            byte state = (byte)(tunerOn ? 0x01 : 0x00);
            SendFrame(new byte[] { 0x1C, 0x01, state }, expectReply: false);
        }

        public override string GetBandwidth(VfoType vfo)
        {
            var reply = SendFrame(new byte[] { 0x1A, 0x03 });
            if (reply.Count >= 7 && reply[4] == 0x1A && reply[5] == 0x03)
            {
                int bw = BcdByteToInt(reply[6]) * 50;
                string code = bw.ToString();

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
            return string.Empty;
        }

        public override void SetBandwidth(VfoType vfo, string bandwidthKey)
        {
            string bwVal = Config.Filters.GetValueOrDefault(bandwidthKey, bandwidthKey);
            if (int.TryParse(bwVal, out int hz))
            {
                int val = hz / 50;
                byte b = IntToBcdByte(val);
                SendFrame(new byte[] { 0x1A, 0x03, b }, expectReply: false);
            }
            else
            {
                SendRawCommand(bwVal);
            }
        }

        public override string GetRigState()
        {
            long freq = GetFrequency(VfoType.VfoA);
            string mode = GetMode(VfoType.VfoA);
            return $"[CI-V State] Freq: {freq:N0} Hz, Mode: {mode}";
        }

        public override int GetSMeter()
        {
            var reply = SendFrame(new byte[] { 0x15, 0x02 });
            if (reply.Count >= 8)
            {
                int val1 = BcdByteToInt(reply[6]);
                int val2 = BcdByteToInt(reply[7]);
                int raw = val1 * 100 + val2;
                return NormalizeMeterValue(raw, Config.SMeterMax);
            }
            return 0;
        }

        public override int GetPowerMeter()
        {
            var reply = SendFrame(new byte[] { 0x15, 0x11 });
            if (reply.Count >= 8)
            {
                int val1 = BcdByteToInt(reply[6]);
                int val2 = BcdByteToInt(reply[7]);
                int raw = val1 * 100 + val2;
                return NormalizeMeterValue(raw, Config.PowerMeterMax);
            }
            return 0;
        }

        public override int GetSwrMeter()
        {
            var reply = SendFrame(new byte[] { 0x15, 0x12 });
            if (reply.Count >= 8)
            {
                int val1 = BcdByteToInt(reply[6]);
                int val2 = BcdByteToInt(reply[7]);
                int raw = val1 * 100 + val2;
                return NormalizeMeterValue(raw, Config.SwrMeterMax);
            }
            return 0;
        }

        public override int GetAlcMeter()
        {
            var reply = SendFrame(new byte[] { 0x15, 0x13 });
            if (reply.Count >= 8)
            {
                int val1 = BcdByteToInt(reply[6]);
                int val2 = BcdByteToInt(reply[7]);
                int raw = val1 * 100 + val2;
                return NormalizeMeterValue(raw, Config.AlcMeterMax);
            }
            return 0;
        }

        public override int GetAfGain()
        {
            var reply = SendFrame(new byte[] { 0x14, 0x01 });
            if (reply.Count >= 8)
            {
                int val1 = BcdByteToInt(reply[6]);
                int val2 = BcdByteToInt(reply[7]);
                return val1 * 100 + val2;
            }
            return 0;
        }

        public override void SetAfGain(int gainValue)
        {
            gainValue = Math.Clamp(gainValue, 0, 255);
            byte b1 = IntToBcdByte(gainValue / 100);
            byte b2 = IntToBcdByte(gainValue % 100);
            SendFrame(new byte[] { 0x14, 0x01, b1, b2 }, expectReply: false);
        }

        public override string SendRawCommand(string rawHex)
        {
            var parts = rawHex.Split(new[] { ' ', ',', '-', ';' }, StringSplitOptions.RemoveEmptyEntries);
            var bytes = new List<byte>();
            foreach (var p in parts)
            {
                bytes.Add(Convert.ToByte(p, 16));
            }
            var reply = SendFrame(bytes.ToArray());
            return BitConverter.ToString(reply.ToArray());
        }

        private static byte[] FreqToBcd5(long freqHz)
        {
            var bytes = new byte[5];
            long current = freqHz;
            for (int i = 0; i < 5; i++)
            {
                int tensAndOnes = (int)(current % 100);
                bytes[i] = IntToBcdByte(tensAndOnes);
                current /= 100;
            }
            return bytes;
        }

        private static long ParseFreqFromCivFrame(List<byte> reply)
        {
            if (reply.Count >= 10)
            {
                long freq = 0;
                long multiplier = 1;
                for (int i = 5; i <= 9; i++)
                {
                    int val = BcdByteToInt(reply[i]);
                    freq += val * multiplier;
                    multiplier *= 100;
                }
                return freq;
            }
            return 0;
        }

        private static byte IntToBcdByte(int val)
        {
            int tens = (val / 10) % 10;
            int ones = val % 10;
            return (byte)((tens << 4) | ones);
        }

        private static int BcdByteToInt(byte b)
        {
            int high = (b >> 4) & 0x0F;
            int low = b & 0x0F;
            return (high * 10) + low;
        }
    }
}