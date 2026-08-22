# RigControlApp 設定ファイル (`.ini`) 編集マニュアル

本アプリケーションは、設定ファイル（`.ini`）を編集することで Kenwood、Yaesu、Icom を含む多様なアマチュア無線機（トランシーバー）の通信規格やコマンド体系に対応できます。

---

## 1. 設定ファイルの基本構造と記述ルール

設定ファイルはテキスト形式であり、以下の要素で構成されます。

* **セクション（`[セクション名]`）**: 設定項目のグループ。
* **キーと値（`Key = Value`）**: 各機能の設定項目名と設定値。`=` の前後にスペースが入っていても自動で除去されて正しく認識されます。
* **コメントの記述ルール**:
* **行全体のコメント**: 行頭に `#` を記述します。その行は処理から除外されます。
* **行末のインラインコメント**: コマンドや値と同じ行の後ろにメモを書く場合も、**必ず** `#` を使用してください（例: `TX_GET = TX; # PTT取得`）。`;` はCATコマンドの終端文字として扱われるため、インラインコメントには使用できません。

---

## 2. 書式指定プレースホルダ（変数置換）の仕組み

`[COMMANDS]` セクションのコマンド内に記述する `{0}` や `{0:D9}` は、画面上のボタン操作や設定数値に応じて、プログラムが動的に値をはめ込む「変数枠（プレースホルダ）」です。

### (1) `{0}`（単純文字列置換）

画面で選択されたコードや文字列をそのまま差し込みます。

* **設定例**: `MD_SET_A = MD0{0};`
* **動作**: 画面で「CW（モードコード `3`）」が選ばれた場合、`{0}` の部分が `3` に置換され、リグには `MD03;` が送信されます。

### (2) `{0:D<桁数>}`（10進数ゼロ埋め置換）

数値を指定した桁数になるよう、左側を `0` で埋めて整形します。周波数コマンドなどで必須となります。

* **`{0:D11}`（11桁ゼロ埋め）**: 周波数 7.074 MHz (7,074,000 Hz) $\rightarrow$ `00007074000`（送信例: `FA00007074000;`）
* **`{0:D9}`（9桁ゼロ埋め）**: 周波数 14.074 MHz (14,074,000 Hz) $\rightarrow$ `014074000`（送信例: `FA014074000;`）
* **`{0:D8}`（8桁ゼロ埋め）**: 周波数 14.074 MHz (14,074,000 Hz) $\rightarrow$ `14074000`（送信例: `FA14074000;`）
* **`{0:D3}`（3桁ゼロ埋め）**: AFゲインやATU設定値など。数値 50 $\rightarrow$ `050`（送信例: `AG0050;`）

---

## 3. [SERIAL] セクション（シリアル通信設定）

PCとリグ（トランシーバー）を接続するUSBシリアルポートの物理通信パラメータを設定します。**リグ本体のメニュー設定と完全に一致させる必要があります。**

| キー名 | 設定例 | 設定の基準と詳細な役割 |
| --- | --- | --- |
| **`PortName`** | `COM3` | PCの「デバイス マネージャー」で認識されているポート番号（画面上のUIでも切替可能）。 |
| **`BaudRate`** | `38400` | 通信速度（bps）。リグ側のCAT/CI-Vレート設定と一致させます。高速なほど画面表示の追従が滑らかになります。 |
| **`DataBits`** | `8` | 1データ文字あたりのビット長。通常は `8`（旧型機や一部プロトコルで `7` の場合あり）。 |
| **`Parity`** | `None` | 誤り検出符号。通常は `None`（パリティなし）。 |
| **`StopBits`** | `One` | データの区切りを示すストップビット長。通常は `One`（FT-1000等の旧型機は `Two`）。 |
| **`DtrEnable`** | `True` | **DTR（Data Terminal Ready）端子の電圧制御**。<br><br>USBシリアル変換ICやCATインターフェース回路への電源供給を兼ねている場合が多く、通常は `True` にします。ここを `False` にするとインターフェースが給電停止し通信できなくなる場合があります。 |
| **`RtsEnable`** | `True` | **RTS（Request to Send）端子の電圧制御**。<br><br>ハードウェアフロー制御や、CATインターフェース回路の駆動電源として機能します。通常は `True` に設定します。 |
| **`ReadTimeoutMs`** | `1000` | **受信タイムアウト時間（ミリ秒）**。<br><br>PCがコマンドを送信した後、リグから返信が返ってくるのを待機する上限時間です。通常は `1000`（1秒）。リグの電源が入っていない場合やケーブル断線時に、アプリが応答なしで無限フリーズするのを防ぎます。 |
| **`WriteTimeoutMs`** | `1000` | **送信タイムアウト時間（ミリ秒）**。<br><br>PCからリグへコマンドを書き出す際の上限時間です。ポート詰まり等で送信が滞った場合にタイムアウトエラーを発生させ、安全に復帰させます。通常は `1000`。 |

---

## 4. [PROTOCOL] セクション（プロトコル・動作設定）

通信プロトコルの方式、周波数フォーマット、画面の更新速度を設定します。

| キー名 | 設定例 | 設定の基準と詳細な役割 |
| --- | --- | --- |
| **`Type`** | `ASCII` | 通信プロトコルの種別を指定します。<br><br>・**`ASCII`**: テキスト形式のCATコマンド体系。Kenwood全般、およびYaesuのASCII CAT対応機（近年の主要機種）で使用。<br><br>・**`CIV`**: Icom独自のバイナリパケットフレーム体系。<br><br>・**`YaesuBinary`**: Yaesuの旧型5バイト固定バイナリ体系（FT-1000, FT-1000MP, Mark-V 等）。 |
| **`Terminator`** | `;` | コマンドの終端文字。ASCII CATでは `;` が区切り記号として使用されます（CI-Vやバイナリでは不要）。 |
| **`FreqDigits`** | `11` | **周波数設定コマンドで送信する数値の桁数**。<br><br>リグの世代・機種によりフォーマットが異なります。<br><br>・`11`: Kenwood全般、Yaesu新型（FT-991A, FT-710, FTDX10, FTDX101 等）<br><br>・`9`: Yaesu一部機種（FT-991, FT-710, FTX-1, FTDX101, FTDX10 等）<br><br>・`8`: Yaesu旧型ASCII機（FT-2000, FTDX3000, FTDX5000, FTDX9000 等） |
| **`CivRigAddress`** | `94` | **Icom機固有のCI-Vアドレス（16進数）**。<br><br>リグ本体のメニュー「CI-V アドレス」と一致させます。<br><br>【代表機種のアドレス例】<br><br>・IC-7300: `94`<br><br>・IC-7610: `98`<br><br>・IC-705: `A4`<br><br>・IC-9700: `A2`<br><br>・IC-7851: `8E` |
| **`CivControllerAddress`** | `E0` | PC側（コントローラー）のCI-Vアドレス。通常は `E0` 固定です。 |
| **`PollIntervalMs`** | `300` | **画面の周波数・メーター・送受信状態を取得するポーリング周期（ミリ秒）**。<br><br>数値を小さくする（例: `200`〜`300`）とダイヤル追従が滑らかになります。低速ボーレート（4800bps等）では通信詰まりを防ぐため `500` 前後を推奨します。 |

---

## 5. [COMMANDS] セクション（送受信コマンド定義）

各UI操作時や定期ポーリング時に送受信するCAT/CI-Vコマンドを定義します。

### VFO-A / VFO-B 独立コマンドのルール

キー名の末尾に `_A` または `_B` を付けると、現在アクティブな VFO に応じて自動的にコマンドが切り替わります（共通の場合は `_A`/`_B` を省略したキー名で記述可能）。

### 主なコマンドキー一覧

| コマンドキー | 機能・役割 | ASCII CAT 設定例 | Icom CI-V 設定例 |
| --- | --- | --- | --- |
| **`FA_GET` / `FB_GET**` | 周波数の取得 | `FA;` / `FB;` | `03` |
| **`FA_SET` / `FB_SET**` | 周波数の設定（`{0}` にHzが入る） | `FA{0:D11};` | `05` |
| **`VFO_A` / `VFO_B**` | アクティブVFOの切替 | `VS0;` / `VS1;`（または `FR0;`） | `07 00` / `07 01` |
| **`MD_GET_A` / `MD_GET_B**` | 動作モードの取得 | `MD0;` / `MD1;`（または `MD;`） | `04` |
| **`MD_SET_A` / `MD_SET_B**` | 動作モードの設定（`{0}` にモード番号） | `MD0{0};`（または `MD{0};`） | `06 {0} 01` |
| **`ANT_GET_A` / `ANT_GET_B**` | 現在アンテナ番号の取得 | `AN0;` / `AN1;`（または `AN;`） | `12` |
| **`ANT_SET_A` / `ANT_SET_B**` | アンテナ端子の切替（`{0}` にアンテナ番号） | `AN0{0};`（または `AN{0};`） | `12 0{0}` |
| **`BAND_SET_A` / `BAND_SET_B**` | バンド切替テンプレート | `BS{0};`（または `BD{0};`） | `01 {0}` |
| **`TX_ON` / `TX_OFF**` | 送信開始（PTT ON）/ 受信復帰（PTT OFF） | `TX1;` / `TX0;`（または `TX;` / `RX;`） | `1C 00 01` / `1C 00 00` |
| **`TX_GET`** | 送受信状態（PTT）の問い合わせ | `TX;`（または `IF;`） | `1C 00` |
| **`IF_GET`** | リグ状態の一括取得 | `IF;` | ― |
| **`SM_GET`** | Sメーター（受信信号強度）の取得 | `SM0;` | `15 02` |
| **`PO_GET`** | Power（送信出力）メーターの取得 | `RM4;`（Kenwood: `RM;`） | `15 11` |
| **`SWR_GET`** | SWRメーターの取得 | `RM1;`（Kenwood: `RM;`） | `15 12` |
| **`ALC_GET`** | ALCメーターの取得 | `RM3;`（Kenwood: `RM;`） | `15 13` |
| **`AG_GET` / `AG_SET**` | AFゲイン（音量）の取得・設定 | `AG0;` / `AG0{0:D3};` | `14 01` |
| **`TUNER_GET`** | アンテナチューナー（ATU）状態取得 | `AC;` | `1C 01` |
| **`TUNER_ON` / `TUNER_OFF**` | アンテナチューナーのON / OFF | `AC001;` / `AC000;` | `1C 01 01` / `1C 01 00` |
| **`TUNER_SET`** | アンテナチューナー設定テンプレート | `AC{0:D3};` | `1C 01 0{0}` |
| **`BW_GET_A` / `BW_GET_B**` | フィルタ通過帯域幅の取得 | `SH0;` / `SH1;`（Kenwood: `FW;`） | `1A 03` |
| **`BW_SET_A` / `BW_SET_B**` | フィルタ通過帯域幅の設定テンプレート | `SH0{0};`（Kenwood: `FW{0:D4};`） | `1A 03 {0}` |
| **`YaesuModel`** | YaesuBinary専用モデル指定 | `FT-1000MP`, `MarkV` 等 | ― |

---

## 6. マッピング・リストセクション

画面のボタンや選択肢と、リグが解釈するパラメータコードを紐付けます。

### `[MODES]`（モードマッピング）

画面のボタン表示名と、リグに送信するモード番号・記号を紐付けます。

```ini
[MODES]
LSB = 1
USB = 2
CW = 3
FM = 4
AM = 5
RTTY-LSB = 6
DATA-USB = C

```

### `[ANTENNAS]`（アンテナマッピング）

画面上のアンテナ番号（`1`〜`4`）と、リグに送信するパラメータを紐付けます。

```ini
# 1始まりの機種（Kenwood, Yaesu ASCII等）
[ANTENNAS]
1 = 1
2 = 2
3 = 3
4 = 4

# 0始まりの機種（Icom CI-V, Yaesu Binary等）
[ANTENNAS]
1 = 00
2 = 01
3 = 02
4 = 03

```

### `[BANDS]`（バンドマッピング）

バンド選択ドロップダウンと連動します。リグ固有のバンド番号、または代表周波数（Hz）を記述します。

```ini
# パターンA: 独自バンドコマンド（Yaesu BSコマンド等）
[BANDS]
1.9MHz = 00
3.5MHz = 01
7MHz = 03
14MHz = 05
21MHz = 07
28MHz = 09
50MHz = 10

# パターンB: 周波数ダイレクト指定（Kenwood等でBAND_SET未定義時）
[BANDS]
7MHz = 7074000
14MHz = 14074000

```

### `[FILTERS]`（フィルタ帯域幅の段階設定）

フィルタ選択ドロップダウンに表示する帯域幅（Hz/kHz）と、リグに送るステップ値やHz値を設定します。

```ini
# Yaesu ASCII CAT機（ステップコード指定の例）
[FILTERS]
300Hz = 00
500Hz = 01
1.2kHz = 04
1.8kHz = 07
2.4kHz = 09
3.0kHz = 12

# Kenwood機（周波数Hzダイレクト指定の例）
[FILTERS]
250Hz = 0250
500Hz = 0500
1.8kHz = 1800
2.4kHz = 2400
2.7kHz = 2700
3.0kHz = 3000

# Icom CI-V機（Hz指定の例）
[FILTERS]
250Hz = 250
500Hz = 500
1.2kHz = 1200
1.8kHz = 1800
2.4kHz = 2400
3.0kHz = 3000

```

---

## 7. 機種別 `.ini` 設定例（コピー用完全テンプレート）

### ① Kenwood（TS-590, TS-890, TS-990 等）

```ini
# ==============================================================================
# Kenwood / ASCII CAT Configuration
# ==============================================================================

[SERIAL]
PortName = COM3
BaudRate = 115200
DataBits = 8
Parity = None
StopBits = One
DtrEnable = True
RtsEnable = True
ReadTimeoutMs = 1000
WriteTimeoutMs = 1000

[PROTOCOL]
Type = ASCII
Terminator = ;
FreqDigits = 11
PollIntervalMs = 500

[COMMANDS]
FA_GET = FA;
FB_GET = FB;
FA_SET = FA{0:D11};
FB_SET = FB{0:D11};
VFO_A = FR0;
VFO_B = FR1;
MD_GET_A = MD;
MD_GET_B = MD;
MD_SET_A = MD{0};
MD_SET_B = MD{0};
ANT_GET_A = AN;
ANT_GET_B = AN;
ANT_SET_A = AN{0};
ANT_SET_B = AN{0};
BAND_SET_A = BD{0};
BAND_SET_B = BD{0};
TX_ON = TX;
TX_OFF = RX;
TX_GET = TX;
IF_GET = IF;
SM_GET = SM0;
PO_GET = RM;
SWR_GET = RM;
ALC_GET = RM;
AG_GET = AG0;
AG_SET = AG0{0:D3};
TUNER_GET = AC;
TUNER_ON = AC001;
TUNER_OFF = AC000;
TUNER_SET = AC{0:D3};
BW_GET_A = FW;
BW_GET_B = FW;
BW_GET = FW;
BW_SET_A = FW{0:D4};
BW_SET_B = FW{0:D4};
BW_SET = FW{0:D4};

[MODES]
LSB = 1
USB = 2
CW = 3
FM = 4
AM = 5
FSK = 6
CW-R = 7
DATA-USB = 2
DATA-LSB = 1

[ANTENNAS]
1 = 1
2 = 2
3 = 3
4 = 4

[BANDS]
1.9MHz = 00
3.5MHz = 01
7MHz = 03
10MHz = 04
14MHz = 05
18MHz = 06
21MHz = 07
24MHz = 08
28MHz = 09
50MHz = 10
144MHz = 14
430MHz = 15

[FILTERS]
250Hz = 0250
500Hz = 0500
1.0kHz = 1000
1.8kHz = 1800
2.4kHz = 2400
2.7kHz = 2700
3.0kHz = 3000

```

---

### ② Yaesu ASCII CAT機（FT-991A, FT-710, FTDX10, FTDX101 等）

```ini
# ==============================================================================
# Yaesu CAT Configuration (ASCII Protocol: FT-991A, FT-710, FTDX10 等)
# ==============================================================================

[SERIAL]
PortName = COM4
BaudRate = 38400
DataBits = 8
Parity = None
StopBits = One
DtrEnable = True
RtsEnable = True
ReadTimeoutMs = 1000
WriteTimeoutMs = 1000

[PROTOCOL]
Type = ASCII
Terminator = ;
FreqDigits = 8
PollIntervalMs = 500

[COMMANDS]
FA_GET = FA;
FB_GET = FB;
FA_SET = FA{0:D8};
FB_SET = FB{0:D8};
VFO_A = VS0;
VFO_B = VS1;
MD_GET_A = MD0;
MD_GET_B = MD1;
MD_SET_A = MD0{0};
MD_SET_B = MD1{0};
ANT_SET_A = AN0{0};
ANT_SET_B = AN1{0};
ANT_GET_A = AN0;
ANT_GET_B = AN1;
BAND_SET_A = BS{0};
BAND_SET_B = BS{0};
TX_ON = TX1;
TX_OFF = TX0;
TX_GET = TX;
IF_GET = IF;
SM_GET = SM0;
PO_GET = RM4;
SWR_GET = RM1;
ALC_GET = RM3;
AG_GET = AG0;
AG_SET = AG0{0:D3};
TUNER_GET = AC;
TUNER_ON = AC001;
TUNER_OFF = AC000;
TUNER_SET = AC{0:D3};
BW_GET_A = SH0;
BW_GET_B = SH1;
BW_GET = SH;
BW_SET_A = SH0{0};
BW_SET_B = SH1{0};
BW_SET = SH{0};

[MODES]
LSB = 1
USB = 2
CW = 3
FM = 4
AM = 5
RTTY-LSB = 6
CW-R = 7
DATA-LSB = 8
DATA-USB = 9
DATA-FM = A
FM-N = B
RTTY-USB = C

[ANTENNAS]
1 = 1
2 = 2
3 = 3
4 = 4

[BANDS]
1.9MHz = 00
3.5MHz = 01
7MHz = 03
10MHz = 04
14MHz = 05
18MHz = 06
21MHz = 07
24MHz = 08
28MHz = 09
50MHz = 10
144MHz = 14
430MHz = 15

[FILTERS]
300Hz = 00
500Hz = 01
1.2kHz = 04
1.8kHz = 07
2.4kHz = 09
3.0kHz = 12

```

---

### ③ Icom（IC-7300, IC-705, IC-7610 等 CI-V機）

```ini
# ==============================================================================
# Icom CI-V Configuration (IC-7300, IC-7610, IC-705 等)
# ==============================================================================

[SERIAL]
PortName = COM5
BaudRate = 19200
DataBits = 8
Parity = None
StopBits = One
DtrEnable = True
RtsEnable = True
ReadTimeoutMs = 1000
WriteTimeoutMs = 1000

[PROTOCOL]
Type = CIV
CivRigAddress = 94
CivControllerAddress = E0
PollIntervalMs = 500

[COMMANDS]
FA_GET = 03
FB_GET = 03
FA_SET = 05
FB_SET = 05
VFO_A = 07 00
VFO_B = 07 01
MD_GET_A = 04
MD_GET_B = 04
MD_SET_A = 06 {0}
MD_SET_B = 06 {0}
ANT_GET_A = 12
ANT_GET_B = 12
ANT_SET_A = 12 {0} 00
ANT_SET_B = 12 {0} 00
BAND_SET_A = 01 {0}
BAND_SET_B = 01 {0}
TX_ON = 1C 00 01
TX_OFF = 1C 00 00
TX_GET = 1C 00
SM_GET = 15 02
PO_GET = 15 11
SWR_GET = 15 12
ALC_GET = 15 13
AG_GET = 14 01
AG_SET = 14 01
TUNER_GET = 1C 01
TUNER_ON = 1C 01 01
TUNER_OFF = 1C 01 00
TUNER_SET = 1C 01 0{0}
BW_GET_A = 1A 03
BW_GET_B = 1A 03
BW_GET = 1A 03
BW_SET_A = 1A 03 {0}
BW_SET_B = 1A 03 {0}
BW_SET = 1A 03 {0}

[MODES]
LSB = 00
USB = 01
AM = 02
CW = 03
RTTY = 04
FM = 05
CW-R = 07
RTTY-R = 08
DATA-USB = 01

[ANTENNAS]
1 = 00
2 = 01
3 = 02
4 = 03

[BANDS]
1.9MHz = 00
3.5MHz = 01
7MHz = 02
10MHz = 03
14MHz = 04
18MHz = 05
21MHz = 06
24MHz = 07
28MHz = 08
50MHz = 09
144MHz = 10
430MHz = 11

[FILTERS]
250Hz = 250
500Hz = 500
1.2kHz = 1200
1.8kHz = 1800
2.4kHz = 2400
2.8kHz = 2800
3.0kHz = 3000

```

---

### ④ Yaesu 旧型バイナリCAT機（FT-1000, FT-1000MP, Mark-V 等）

```ini
# ==============================================================================
# Yaesu 5-Byte Binary CAT Configuration (FT-1000 / FT-1000MP / Mark-V)
# ==============================================================================

[SERIAL]
PortName = COM6
BaudRate = 4800
DataBits = 8
Parity = None
StopBits = Two
DtrEnable = True
RtsEnable = True
ReadTimeoutMs = 1000
WriteTimeoutMs = 1000

[PROTOCOL]
Type = YaesuBinary
Terminator = 
PollIntervalMs = 500

[COMMANDS]
# YaesuModel は機種名を正確に指定: FT-1000, FT-1000MP, MarkV, MarkVField
YaesuModel = FT-1000MP
VFO_A = 00 00 00 00 05
VFO_B = 00 00 00 02 05
MD_SET_A = 00 00 00 {0} 0C
MD_SET_B = 00 00 00 {0} 0C
PO_GET = 
SWR_GET = 
ALC_GET = 
TX_GET = 
TUNER_GET = 
TUNER_ON = 
TUNER_OFF = 
TUNER_SET = 
BW_GET_A = 
BW_GET_B = 
BW_GET = 
BW_SET_A = 
BW_SET_B = 
BW_SET = 

[MODES]
LSB = 00
USB = 01
CW = 02
CW-R = 03
AM = 04
FM = 06
RTTY = 08

[ANTENNAS]
1 = 00
2 = 01
3 = 02
4 = 03

[BANDS]
1.9MHz = 1910000
3.5MHz = 3535000
7MHz = 7074000
10MHz = 10136000
14MHz = 14074000
18MHz = 18100000
21MHz = 21074000
24MHz = 24915000
28MHz = 28074000
50MHz = 50313000
144MHz = 144100000
430MHz = 430100000

[FILTERS]
250Hz = 250
500Hz = 500
1.8kHz = 1800
2.4kHz = 2400
2.8kHz = 2800
3.0kHz = 3000

```
