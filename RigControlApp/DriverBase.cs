using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Text;

namespace RigControlApp
{
    public enum VfoType
    {
        VfoA,
        VfoB
    }

    /// <summary>
    /// 各社無線機（Yaesu, Kenwood, Icom等）の制御ドライバー共通インターフェース
    /// </summary>
    public interface IRigDriver : IDisposable
    {
        void Open();
        void Close();
        bool IsOpen { get; }
        bool SupportsDualVfoRead { get; }

        long GetFrequency(VfoType vfo);
        void SetFrequency(VfoType vfo, long freqHz);

        string GetMode();
        void SetMode(string modeName);

        void SelectVfo(VfoType vfo);
        void SetPtt(bool txOn);

        string GetRigState();
        int GetSMeter();
        int GetAfGain();
        void SetAfGain(int gainValue);

        string SendRawCommand(string rawInput);
    }

    /// <summary>
    /// 各ドライバー共通のシリアルポート管理および排他制御基底クラス
    /// </summary>
    public abstract class RigDriverBase : IRigDriver
    {
        protected readonly RigConfig Config;
        protected SerialPort? Port;
        protected readonly object SyncLock = new();

        public virtual bool SupportsDualVfoRead => false;

        protected RigDriverBase(RigConfig config)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public bool IsOpen
        {
            get
            {
                lock (SyncLock)
                {
                    return Port != null && Port.IsOpen;
                }
            }
        }

        public virtual void Open()
        {
            lock (SyncLock)
            {
                if (IsOpen) return;

                Port = new SerialPort(
                    Config.PortName,
                    Config.BaudRate,
                    Config.Parity,
                    Config.DataBits,
                    Config.StopBits)
                {
                    DtrEnable = Config.DtrEnable,
                    RtsEnable = Config.RtsEnable,
                    ReadTimeout = Config.ReadTimeoutMs,
                    WriteTimeout = Config.WriteTimeoutMs,
                    Encoding = Encoding.ASCII
                };

                Port.Open();
                Port.DiscardInBuffer();
                Port.DiscardOutBuffer();
            }
        }

        public virtual void Close()
        {
            lock (SyncLock)
            {
                if (Port != null)
                {
                    if (Port.IsOpen)
                    {
                        try { Port.DiscardInBuffer(); } catch { }
                        try { Port.DiscardOutBuffer(); } catch { }
                        try { Port.Close(); } catch { }
                    }
                    Port.Dispose();
                    Port = null;
                }
            }
        }

        public void Dispose()
        {
            Close();
            GC.SuppressFinalize(this);
        }

        public abstract long GetFrequency(VfoType vfo);
        public abstract void SetFrequency(VfoType vfo, long freqHz);
        public abstract string GetMode();
        public abstract void SetMode(string modeName);
        public abstract void SelectVfo(VfoType vfo);
        public abstract void SetPtt(bool txOn);
        public abstract string GetRigState();
        public abstract int GetSMeter();
        public abstract int GetAfGain();
        public abstract void SetAfGain(int gainValue);
        public abstract string SendRawCommand(string rawInput);
    }

    /// <summary>
    /// プロトコル種別に応じたドライバー生成ファクトリ
    /// </summary>
    public static class RigDriverFactory
    {
        public static IRigDriver Create(RigConfig config)
        {
            return config.Protocol switch
            {
                ProtocolType.Civ => new IcomCivDriver(config),
                ProtocolType.YaesuBinary => new YaesuBinaryDriver(config),
                ProtocolType.Ascii => new AsciiCatDriver(config),
                _ => throw new NotSupportedException($"未対応のプロトコルです: {config.Protocol}")
            };
        }
    }
}