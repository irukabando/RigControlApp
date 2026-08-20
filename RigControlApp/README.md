# Amateur Radio Universal Rig Controller (.NET 8 C#)

アマチュア無線機用のCAT/CI-V制御プログラムです。
Yaesu、Kenwood（ASCII CATコマンド体系）および Icom（CI-V バイナリフレーム体系）の双方に対応し、外部設定ファイル（TXT形式）により機種ごとのコマンドやCOMポート設定を柔軟にカスタマイズできます。

## 対応コマンド・機能
- **FA / FB**: VFO-A / VFO-B 周波数 読出・設定
- **MD**: 動作モード (LSB, USB, CW, FM, AM, RTTY, DATA等) 読出・設定
- **TX / RX**: 送信（PTT ON）/ 受信（PTT OFF）切り替え
- **IF**: 無線機状態の一括読出
- **SM**: Sメーター値の取得
- **AG**: AFゲイン（音量）の取得・設定
- **リアルタイム監視**: 周波数・モード・Sメーターの連続ポーリング表示
- **生コマンド直接送信**: CAT/CI-V Raw コマンドのテスト送信

## フォルダ構成
- `RigControlApp.csproj`: プロジェクトファイル (.NET 8)
- `Program.cs`: メニュー操作・リアルタイム監視CLI
- `RigConfig.cs`: TXT設定ファイルパーサー
- `RigDrivers.cs`: ASCII CAT（Kenwood/Yaesu）および CI-V（Icom）のプロトコルエンジン
- `config_kenwood.txt`: Kenwood用設定サンプル (TS-590SG, TS-890等)
- `config_yaesu.txt`: Yaesu用設定サンプル (FT-991A, FT-710, FTDX10等)
- `config_icom.txt`: Icom用設定サンプル (IC-7300, IC-705, IC-7610等)
- `config.txt`: デフォルト設定ファイル

## ビルド＆実行方法
```bash
dotnet build
dotnet run
```
