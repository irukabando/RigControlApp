using System;
using System.IO.Ports;
using System.Text;

namespace RigControlApp
{
    /// <summary>
    /// VFO の種別 (VFO-A / VFO-B)
    /// </summary>
    public enum VfoType
    {
        VfoA,
        VfoB
    }

    /// <summary>
    /// リグ制御ドライバーの共通インターフェース
    /// </summary>
    public interface IRigDriver : IDisposable
    {
        void Open();
        void Close();
        bool IsOpen { get; }
        bool SupportsDualVfoRead { get; }

        long GetFrequency(VfoType vfo);
        void SetFrequency(VfoType vfo, long freqHz);
        string GetMode(VfoType vfo);
        void SetMode(VfoType vfo, string modeName);
        void SelectVfo(VfoType vfo);
        void SelectBand(VfoType vfo, string bandKey);
        void SetAntenna(VfoType vfo, string antennaIndex);
        void SetPtt(bool txOn);
        string GetRigState();
        int GetSMeter();
        int GetAfGain();
        void SetAfGain(int gainValue);
        string SendRawCommand(string rawInput);
    }

    /// <summary>
    /// リグドライバーの共通基底クラス
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

        /// <summary>
        /// ポートが開いていることを確認
        /// </summary>
        protected void EnsureOpen()
        {
            if (!IsOpen)
            {
                throw new InvalidOperationException("シリアルポートが開いていません。先に接続を行ってください。");
            }
        }

        public abstract long GetFrequency(VfoType vfo);
        public abstract void SetFrequency(VfoType vfo, long freqHz);
        public abstract string GetMode(VfoType vfo);
        public abstract void SetMode(VfoType vfo, string modeName);
        public abstract void SelectVfo(VfoType vfo);
        public abstract void SelectBand(VfoType vfo, string bandKey);
        public abstract void SetAntenna(VfoType vfo, string antennaIndex);
        public abstract void SetPtt(bool txOn);
        public abstract string GetRigState();
        public abstract int GetSMeter();
        public abstract int GetAfGain();
        public abstract void SetAfGain(int gainValue);
        public abstract string SendRawCommand(string rawInput);
    }

    /// <summary>
    /// リグ設定に応じたドライバーを生成するファクトリクラス
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
                _ => throw new NotSupportedException($"サポートされていないプロトコルです: {config.Protocol}")
            };
        }
    }
}