using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace RigControlApp
{
    /// <summary>
    /// Kenwood & Yaesu(ASCII) 向け CAT 制御ドライバー
    /// </summary>
    public class AsciiCatDriver : RigDriverBase
    {
        public override bool SupportsDualVfoRead => true;

        public AsciiCatDriver(RigConfig config) : base(config) { }

        private string ExecuteCommand(string cmd, bool expectResponse = true)
        {
            lock (SyncLock)
            {
                if (!IsOpen) throw new InvalidOperationException("シリアルポートが開いていません。");

                Port!.DiscardInBuffer();
                Port.Write(cmd);

                if (!expectResponse) return string.Empty;

                var sb = new StringBuilder();
                var startTime = DateTime.Now;

                while ((DateTime.Now - startTime).TotalMilliseconds < Config.ReadTimeoutMs)
                {
                    if (Port.BytesToRead > 0)
                    {
                        char c = (char)Port.ReadChar();
                        sb.Append(c);
                        if (c == Config.Terminator)
                        {
                            return sb.ToString();
                        }
                    }
                    else
                    {
                        Thread.Sleep(5);
                    }
                }

                return sb.ToString();
            }
        }

        public override long GetFrequency(VfoType vfo)
        {
            string key = vfo == VfoType.VfoA ? "FA_GET" : "FB_GET";
            string defaultCmd = vfo == VfoType.VfoA ? "FA;" : "FB;";
            string prefix = vfo == VfoType.VfoA ? "FA" : "FB";

            string cmd = Config.Commands.GetValueOrDefault(key, defaultCmd);
            string resp = ExecuteCommand(cmd);
            return ParseFrequencyResponse(resp, prefix);
        }

        public override void SetFrequency(VfoType vfo, long freqHz)
        {
            string key = vfo == VfoType.VfoA ? "FA_SET" : "FB_SET";
            string defaultTmpl = vfo == VfoType.VfoA
                ? $"FA{{0:D{Config.FreqDigits}}};"
                : $"FB{{0:D{Config.FreqDigits}}};";

            string tmpl = Config.Commands.GetValueOrDefault(key, defaultTmpl);
            string cmd = string.Format(tmpl, freqHz);
            ExecuteCommand(cmd, expectResponse: false);
        }

        public override string GetMode()
        {
            string cmd = Config.Commands.GetValueOrDefault("MD_GET", "MD;");
            string resp = ExecuteCommand(cmd).TrimEnd(Config.Terminator);

            string code = resp.StartsWith("MD", StringComparison.OrdinalIgnoreCase) ? resp[2..] : resp;

            foreach (var kvp in Config.ModeMap)
            {
                if (kvp.Value.Equals(code, StringComparison.OrdinalIgnoreCase) ||
                    kvp.Value.PadLeft(2, '0').Equals(code, StringComparison.OrdinalIgnoreCase))
                {
                    return kvp.Key;
                }
            }
            return string.IsNullOrEmpty(code) ? "Unknown" : $"Code: {code}";
        }

        public override void SetMode(string modeName)
        {
            if (!Config.ModeMap.TryGetValue(modeName, out var code))
            {
                throw new ArgumentException($"未定義のモード名です: {modeName}");
            }

            string tmpl = Config.Commands.GetValueOrDefault("MD_SET", "MD{0};");
            string cmd = string.Format(tmpl, code);
            ExecuteCommand(cmd, expectResponse: false);
        }

        public override void SelectVfo(VfoType vfo)
        {
            string key = vfo == VfoType.VfoA ? "VFO_A" : "VFO_B";
            string defaultCmd = vfo == VfoType.VfoA ? "VS0;" : "VS1;";
            string cmd = Config.Commands.GetValueOrDefault(key, defaultCmd);
            ExecuteCommand(cmd, expectResponse: false);
        }

        public override void SetPtt(bool txOn)
        {
            string cmd = txOn
                ? Config.Commands.GetValueOrDefault("TX_ON", "TX1;")
                : Config.Commands.GetValueOrDefault("TX_OFF", "TX0;");
            ExecuteCommand(cmd, expectResponse: false);
        }

        public override string GetRigState()
        {
            string cmd = Config.Commands.GetValueOrDefault("IF_GET", "IF;");
            return ExecuteCommand(cmd);
        }

        public override int GetSMeter()
        {
            string cmd = Config.Commands.GetValueOrDefault("SM_GET", "SM0;");
            string resp = ExecuteCommand(cmd).TrimEnd(Config.Terminator);
            if (resp.Length > 2 && int.TryParse(resp[2..], out int val))
            {
                return val;
            }
            return 0;
        }

        public override int GetAfGain()
        {
            string cmd = Config.Commands.GetValueOrDefault("AG_GET", "AG0;");
            string resp = ExecuteCommand(cmd).TrimEnd(Config.Terminator);
            if (resp.Length > 3 && int.TryParse(resp[3..], out int val))
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

        public override string SendRawCommand(string rawInput)
        {
            if (!rawInput.EndsWith(Config.Terminator.ToString()))
                rawInput += Config.Terminator;

            return ExecuteCommand(rawInput);
        }

        private long ParseFrequencyResponse(string resp, string prefix)
        {
            resp = resp.TrimEnd(Config.Terminator);
            if (resp.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string numStr = resp[prefix.Length..];
                if (long.TryParse(numStr, out long freq)) return freq;
            }
            return 0;
        }
    }
}