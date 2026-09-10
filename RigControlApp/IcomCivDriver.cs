using System;
using System.Collections.Generic;
using System.Threading;

namespace RigControlApp
{
    /// <summary>
    /// Icom CI-V ドライバ (IC-7100, IC-7300, IC-7610, IC-705 共通)
    /// ini ファイル ([COMMANDS], [METERS]) の設定値を最優先で解釈して動作します。
    /// ハードコードされたコマンド判定を廃止し、ini のキーから動的にバイト列を生成・照合します。
    /// </summary>
    public class IcomCivDriver : RigDriverBase
    {
        public override bool SupportsDualVfoRead => false;

        private const byte Preamble = 0xFE;
        private const byte EndByte = 0xFD;
        private const byte NakByte = 0xFA; // CI-V NG/エラー

        public IcomCivDriver(RigConfig config) : base(config) { }

        /// <summary>
        /// ini ファイルの 16進数文字列（例: "1C 01 02" や "15 02"）を byte 配列にパースします。
        /// </summary>
        public static byte[] ParseHexToBytes(string hexStr)
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
        /// CI-V フレームを送信し、要求したコマンド・サブコマンドに合致する応答フレームを厳密に受信します。
        /// 自身が送信したエコーバックや、無線機から自発的に送出されるトランシーブフレームは安全にスキップします。
        /// </summary>
        private List<byte> SendFrame(byte[] payload, bool expectReply = true, byte[]? expectedMatchPrefix = null)
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

                // 期待する応答プレフィックス (指定がなければ payload 全体または主要部)
                byte[] matchPrefix = expectedMatchPrefix ?? payload;

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
                            // 受信バッファ内をスキャンして正規の応答フレームを探す
                            for (int i = 0; i <= received.Count - 6; i++)
                            {
                                if (received[i] == Preamble && received[i + 1] == Preamble &&
                                    received[i + 2] == Config.CivControllerAddress && // コントローラー宛て
                                    received[i + 3] == Config.CivRigAddress)         // 無線機発
                                {
                                    int endIdx = received.IndexOf(EndByte, i + 4);
                                    if (endIdx != -1)
                                    {
                                        var reply = received.GetRange(i, endIdx - i + 1);

                                        // NAK (0xFA) 応答の場合
                                        if (reply.Count >= 6 && reply[4] == NakByte)
                                        {
                                            Console.WriteLine($"[CI-V NAK Error] 送信={BitConverter.ToString(payload)}");
                                            return reply;
                                        }

                                        // 要求したコマンドプレフィックスと一致するか確認
                                        bool isMatch = true;
                                        for (int m = 0; m < matchPrefix.Length; m++)
                                        {
                                            if (4 + m >= reply.Count || reply[4 + m] != matchPrefix[m])
                                            {
                                                isMatch = false;
                                                break;
                                            }
                                        }

                                        if (isMatch)
                                        {
                                            return reply;
                                        }
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
            string key = vfo == VfoType.VfoA ? "FA_GET" : "FB_GET";
            string hexCmd = Config.Commands.GetValueOrDefault(key, Config.Commands.GetValueOrDefault("FREQ_GET", "03"));
            byte[] payload = ParseHexToBytes(hexCmd);

            var reply = SendFrame(payload, expectReply: true, expectedMatchPrefix: payload);
            return ParseFreqFromCivFrame(reply);
        }

        public override void SetFrequency(VfoType vfo, long freqHz)
        {
            string key = vfo == VfoType.VfoA ? "FA_SET" : "FB_SET";
            string hexCmd = Config.Commands.GetValueOrDefault(key, "05");
            byte[] cmdBytes = ParseHexToBytes(hexCmd);
            byte[] bcd = FreqToBcd5(freqHz);

            var payload = new byte[cmdBytes.Length + bcd.Length];
            Array.Copy(cmdBytes, 0, payload, 0, cmdBytes.Length);
            Array.Copy(bcd, 0, payload, cmdBytes.Length, bcd.Length);

            SendFrame(payload, expectReply: false);
        }

        public override string GetMode(VfoType vfo)
        {
            string key = vfo == VfoType.VfoA ? "MD_GET_A" : "MD_GET_B";
            string hexCmd = Config.Commands.GetValueOrDefault(key, Config.Commands.GetValueOrDefault("MD_GET", "04"));
            byte[] payload = ParseHexToBytes(hexCmd);

            var reply = SendFrame(payload, expectReply: true, expectedMatchPrefix: payload);
            if (reply.Count >= 6)
            {
                int cmdIdx = 4; // CMD (04)
                if (reply.Count > cmdIdx + 1)
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
                string defaultHex = "06 {0}";
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
            string key = vfo == VfoType.VfoA ? "ANT_GET_A" : "ANT_GET_B";
            string? cmd = Config.Commands.GetValueOrDefault(key, Config.Commands.GetValueOrDefault("ANT_GET", null));
            if (string.IsNullOrWhiteSpace(cmd) || cmd.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            {
                return "1"; // IC-7100 など端子切替コマンド非対応機は "1" を固定返却
            }

            byte[] payload = ParseHexToBytes(cmd);
            var reply = SendFrame(payload, expectReply: true, expectedMatchPrefix: payload);
            if (reply.Count >= 6)
            {
                int dataIdx = 4 + payload.Length;
                if (dataIdx < reply.Count)
                {
                    byte antByte = reply[dataIdx];
                    string codeHex = antByte.ToString("X2");
                    string codeDec = antByte.ToString();

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
                    return (antByte + 1).ToString();
                }
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
            SendRawCommand(string.Format(tmpl, antCode));
        }

        public override void SetPtt(bool txOn)
        {
            string key = txOn ? "TX_ON" : "TX_OFF";
            string defaultHex = txOn ? "1C 00 01" : "1C 00 00";
            string hex = Config.Commands.GetValueOrDefault(key, defaultHex);
            byte[] payload = ParseHexToBytes(hex);
            SendFrame(payload, expectReply: false);
        }

        public override bool GetPtt()
        {
            string hex = Config.Commands.GetValueOrDefault("TX_GET", "1C 00");
            byte[] payload = ParseHexToBytes(hex);

            var reply = SendFrame(payload, expectReply: true, expectedMatchPrefix: payload);
            int valIdx = 4 + payload.Length;
            if (reply.Count > valIdx)
            {
                return reply[valIdx] == 0x01;
            }
            return false;
        }

        public override bool GetTuner()
        {
            string hex = Config.Commands.GetValueOrDefault("TUNER_GET", "1C 01");
            byte[] payload = ParseHexToBytes(hex);

            var reply = SendFrame(payload, expectReply: true, expectedMatchPrefix: payload);
            int valIdx = 4 + payload.Length;
            if (reply.Count > valIdx)
            {
                return reply[valIdx] == 0x01 || reply[valIdx] == 0x02;
            }
            return false;
        }

        public override void SetTuner(bool tunerOn)
        {
            string key = tunerOn ? "TUNER_ON" : "TUNER_OFF";
            string defaultHex = tunerOn ? "1C 01 01" : "1C 01 00";
            string hex = Config.Commands.GetValueOrDefault(key, defaultHex);
            byte[] payload = ParseHexToBytes(hex);
            SendFrame(payload, expectReply: false);
        }

        public override void StartTuning()
        {
            string hex = Config.Commands.GetValueOrDefault("TUNER_START", "1C 01 02");
            byte[] payload = ParseHexToBytes(hex);
            SendFrame(payload, expectReply: false);
        }

        public override string GetBandwidth(VfoType vfo)
        {
            string key = vfo == VfoType.VfoA ? "BW_GET_A" : "BW_GET_B";
            string hex = Config.Commands.GetValueOrDefault(key, Config.Commands.GetValueOrDefault("BW_GET", "1A 03"));
            byte[] payload = ParseHexToBytes(hex);

            var reply = SendFrame(payload, expectReply: true, expectedMatchPrefix: payload);
            int valIdx = 4 + payload.Length;
            if (reply.Count > valIdx)
            {
                int bw = BcdByteToInt(reply[valIdx]) * 50;
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
            string key = vfo == VfoType.VfoA ? "BW_SET_A" : "BW_SET_B";
            string tmpl = Config.Commands.GetValueOrDefault(key, Config.Commands.GetValueOrDefault("BW_SET", "1A 03 {0}"));

            if (int.TryParse(bwVal, out int hz))
            {
                int val = hz / 50;
                byte b = IntToBcdByte(val);
                string hexByte = b.ToString("X2");
                SendRawCommand(string.Format(tmpl, hexByte));
            }
            else
            {
                SendRawCommand(string.Format(tmpl, bwVal));
            }
        }

        public override string GetRigState()
        {
            long freq = GetFrequency(VfoType.VfoA);
            string mode = GetMode(VfoType.VfoA);
            return $"[CI-V State] Freq: {freq:N0} Hz, Mode: {mode}";
        }

        private int ReadCivMeterFromConfig(string cmdKey, string defaultHex, string meterMaxConfigKey)
        {
            string hex = Config.Commands.GetValueOrDefault(cmdKey, defaultHex);
            byte[] payload = ParseHexToBytes(hex);

            var reply = SendFrame(payload, expectReply: true, expectedMatchPrefix: payload);
            int valIdx = 4 + payload.Length;
            if (reply.Count >= valIdx + 2)
            {
                int val1 = BcdByteToInt(reply[valIdx]);
                int val2 = BcdByteToInt(reply[valIdx + 1]);
                int raw = val1 * 100 + val2;
                int maxVal = Config.GetMeterMaxValue(meterMaxConfigKey, 255);
                return NormalizeMeterValue(raw, maxVal);
            }
            return 0;
        }

        public override int GetSMeter() => ReadCivMeterFromConfig("SM_GET", "15 02", "SMeter");
        public override int GetPowerMeter() => ReadCivMeterFromConfig("PO_GET", "15 11", "PowerMeter");
        public override int GetSwrMeter() => ReadCivMeterFromConfig("SWR_GET", "15 12", "SwrMeter");
        public override int GetAlcMeter() => ReadCivMeterFromConfig("ALC_GET", "15 13", "AlcMeter");

        public override int GetAfGain()
        {
            string hex = Config.Commands.GetValueOrDefault("AG_GET", "14 01");
            byte[] payload = ParseHexToBytes(hex);

            var reply = SendFrame(payload, expectReply: true, expectedMatchPrefix: payload);
            int valIdx = 4 + payload.Length;
            if (reply.Count >= valIdx + 2)
            {
                int val1 = BcdByteToInt(reply[valIdx]);
                int val2 = BcdByteToInt(reply[valIdx + 1]);
                return val1 * 100 + val2;
            }
            return 0;
        }

        public override void SetAfGain(int gainValue)
        {
            gainValue = Math.Clamp(gainValue, 0, 255);
            byte b1 = IntToBcdByte(gainValue / 100);
            byte b2 = IntToBcdByte(gainValue % 100);

            string hex = Config.Commands.GetValueOrDefault("AG_SET", "14 01");
            byte[] prefix = ParseHexToBytes(hex);

            var payload = new byte[prefix.Length + 2];
            Array.Copy(prefix, 0, payload, 0, prefix.Length);
            payload[^2] = b1;
            payload[^1] = b2;

            SendFrame(payload, expectReply: false);
        }

        public override string SendRawCommand(string rawHex)
        {
            byte[] bytes = ParseHexToBytes(rawHex);
            var reply = SendFrame(bytes);
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