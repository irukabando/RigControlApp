using System;
using System.IO.Ports;
using System.Text;

namespace RigControlApp
{
    /// <summary>
    /// VFO 種別 (VFO-A / VFO-B)
    /// </summary>
    public enum VfoType
    {
        VfoA,
        VfoB
    }

    /// <summary>
    /// リグ制御ドライバー共通インターフェース
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

        string GetAntenna(VfoType vfo); // アンテナ状態取得
        void SetAntenna(VfoType vfo, string antennaIndex);

        void SetPtt(bool txOn);
        bool GetPtt();                   // 追加: PTT状態取得

        bool GetTuner();                 // アンテナチューナー状態取得
        void SetTuner(bool tunerOn);     // チューナー ON/OFF

        string GetBandwidth(VfoType vfo); // フィルタ帯域幅取得
        void SetBandwidth(VfoType vfo, string bandwidthKey); // フィルタ帯域幅設定

        string GetRigState();

        int GetSMeter();
        int GetPowerMeter();             // 追加: Power メーター
        int GetSwrMeter();               // 追加: SWR メーター
        int GetAlcMeter();               // 追加: ALC メーター

        int GetAfGain();
        void SetAfGain(int gainValue);

        string SendRawCommand(string rawInput);
    }

    /// <summary>
    /// リグドライバー基底抽象クラス
    /// </summary>
    public abstract class RigDriverBase : IRigDriver
    {
        protected readonly RigConfig Config;
        protected SerialPort? Port;
        protected readonly object SyncLock = new();

        public virtual bool SupportsDualVfoRead => false;
        public abstract string GetAntenna(VfoType vfo);

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
                throw new InvalidOperationException("シリアルポートが開いていません。");
            }
        }

        /// <summary>
        /// 機種固有の最大値から UI用の 0〜255 スケールに正規化
        /// </summary>
        protected static int NormalizeMeterValue(int rawVal, int maxVal)
        {
            if (maxVal <= 0 || rawVal <= 0) return 0;
            if (maxVal == 255) return Math.Clamp(rawVal, 0, 255);

            int normalized = (int)Math.Round((double)rawVal * 255.0 / maxVal);
            return Math.Clamp(normalized, 0, 255);
        }

        public abstract long GetFrequency(VfoType vfo);
        public abstract void SetFrequency(VfoType vfo, long freqHz);
        public abstract string GetMode(VfoType vfo);
        public abstract void SetMode(VfoType vfo, string modeName);
        public abstract void SelectVfo(VfoType vfo);
        public abstract void SelectBand(VfoType vfo, string bandKey);
        public abstract void SetAntenna(VfoType vfo, string antennaIndex);
        public abstract void SetPtt(bool txOn);
        public abstract bool GetPtt();
        public abstract bool GetTuner();
        public abstract void SetTuner(bool tunerOn);
        public abstract string GetBandwidth(VfoType vfo);
        public abstract void SetBandwidth(VfoType vfo, string bandwidthKey);
        public abstract string GetRigState();
        public abstract int GetSMeter();
        public abstract int GetPowerMeter();
        public abstract int GetSwrMeter();
        public abstract int GetAlcMeter();
        public abstract int GetAfGain();
        public abstract void SetAfGain(int gainValue);
        public abstract string SendRawCommand(string rawInput);
    }

    /// <summary>
    /// ドライバー生成ファクトリ
    /// </summary>
    public static class RigDriverFactory
    {
        public static IRigDriver Create(RigConfig config)
        {
            return config.Protocol switch
            {
                ProtocolType.Kenwood => new KenwoodCatDriver(config),
                ProtocolType.Yaesu => new YaesuCatDriver(config),
                ProtocolType.Ascii => new YaesuCatDriver(config), // 汎用ASCIIはYaesu系CATで処理
                ProtocolType.Civ => new IcomCivDriver(config),
                ProtocolType.YaesuBinary => new YaesuBinaryDriver(config),
                _ => throw new NotSupportedException($"未対応のプロトコルです: {config.Protocol}")
            };
        }
    }
}