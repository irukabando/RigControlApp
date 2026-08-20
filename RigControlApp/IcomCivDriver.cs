using System;
using System.Collections.Generic;
using System.Threading;

namespace RigControlApp
{
    /// <summary>
    /// Icom 向け CI-V 通信ドライバー
    /// </summary>
    public class IcomCivDriver : RigDriverBase
    {
        public override bool SupportsDualVfoRead => true;
        private const byte Preamble = 0xFE;
        private const byte EndByte = 0xFD;

        public IcomCivDriver(RigConfig config) : base(config) { }

        private List<byte> SendFrame(byte[] payload, bool expectReply = true)
        {
            lock (SyncLock)
            {
                if (!IsOpen) throw new InvalidOperationException("シリアルポートが開いていません。");

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

        public override string GetMode()
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

        public override void SetMode(string modeName)
        {
            if (!Config.ModeMap.TryGetValue(modeName, out var codeHex))
            {
                throw new ArgumentException($"未定義のモード名です: {modeName}");
            }

            byte modeByte = Convert.ToByte(codeHex, 16);
            SendFrame(new byte[] { 0x06, modeByte, 0x01 }, expectReply: false);
        }

        public override void SelectVfo(VfoType vfo)
        {
            string key = vfo == VfoType.VfoA ? "VFO_A" : "VFO_B";
            string defaultHex = vfo == VfoType.VfoA ? "07 00" : "07 01";
            string hexCmd = Config.Commands.GetValueOrDefault(key, defaultHex);
            SendRawCommand(hexCmd);
        }

        public override void SetPtt(bool txOn)
        {
            byte state = (byte)(txOn ? 0x01 : 0x00);
            SendFrame(new byte[] { 0x1C, 0x00, state }, expectReply: false);
        }

        public override string GetRigState()
        {
            long freq = GetFrequency(VfoType.VfoA);
            string mode = GetMode();
            return $"[CI-V State] Freq: {freq:N0} Hz, Mode: {mode}";
        }

        public override int GetSMeter()
        {
            var reply = SendFrame(new byte[] { 0x15, 0x02 });
            if (reply.Count >= 8)
            {
                int val1 = BcdByteToInt(reply[6]);
                int val2 = BcdByteToInt(reply[7]);
                return val1 * 100 + val2;
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