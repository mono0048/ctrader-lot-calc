# 実装計画書 - AutoLotCalculator cTrader移植

既存のMQL5ユーティリティEA「AutoLotCalculator」をcTrader (cBot) へ移植します。
このBotは、チャート上のライン（SL/TP/Entry）操作に基づいて、リスク％に応じた適切なロット数を自動計算し、発注を支援します。

## ユーザーレビュー事項
> [!NOTE]
> **UIの変更点**:
> MQL5版ではチャートオブジェクト（OBJ_BUTTON）を使用していましたが、cTrader版ではモダンな `Chart.Controls` API（WPF風のパネルやボタン）を使用します。これにより操作性が向上しますが、見た目は多少異なります。全てのラベルとメッセージは日本語化されます。

## 変更内容

### [新規] AutoLotCalculator.cs
新しいcBotのソースコードを作成します。

#### クラス構造
- `Robot` クラスを継承
- **パラメータ**: 
  - `RiskPercentage` (リスク許容率 %)
  - `Slippage` (スリッページ pips)
  - `MagicNumber` (マジックナンバー)
- **状態変数**: `TradeMode` (成行/指値), `IsSplitEntry` (分割エントリー) 等
- **UIコントロール**: MainPanel, Buttons, TextBlocks（GridやStackPanelを使用）

#### 主要メソッドのマッピング
| MQL5 関数 | cTrader 実装 / 近似機能 |
| :--- | :--- |
| `OnInit` | `OnStart`: UIの初期化、ラインの生成、イベント購読 |
| `OnDeinit` | `OnStop`: UIとラインの削除 |
| `OnChartEvent` (Click) | `Button.Click` イベントハンドラ |
| `OnChartEvent` (Drag) | `Chart.ObjectUpdated` イベント (ライン移動の検知) |
| `CalculateTradingValues` | `Account.Balance`, `Symbol.PipSize`, `Symbol.TickValue` を使用して再実装 |
| `SendMarketOrder...` | `ExecuteMarketOrder` |
| `SendPendingOrder...` | `PlaceLimitOrder` / `PlaceStopOrder` |
| `CalculateWinRate`/`EV` | `History` コレクションをループして計算 |

#### 視覚要素 (日本語UI)
- **インタラクティブ・ライン**:
  - `SL Line`: 水平線（赤/赤橙）、ドラッグ可能
  - `TP Line`: 水平線（青/水色）、ドラッグ可能
  - `Entry Line`: 水平線（金）、ドラッグ可能（指値モード時のみ）
- **ダッシュボード (チャート左上)**:
  - **トグルボタン**: UI表示/非表示、モード切替(成行/指値)、分割モード
  - **アクションボタン**: Full Entry, 1/2 Entry, 1/3 Entry, 全決済
  - **情報ラベル**: リスク額、現在価格、SL幅(pips)、勝率、期待値(円/pips)、RR比率

## 検証計画

### 手動検証手順
cTrader環境での動作確認はユーザー自身が行う必要があります。
1.  cTraderを開き、「AutoLotCalculator」という名前で新規cBotを作成する。
2.  提供するコードをコピペし、ビルドする。
3.  チャートに適用する。
4.  **UI動作確認**:
    - 「モード切替」でラインが出現・移動するか確認。
    - ラインをドラッグした際、「SL Pips」や「リスク額」が再計算されるか確認。
5.  **発注テスト**:
    - **成行モード**: SLラインを調整し、「Full Entry」をクリック。意図したSLとロット数でポジションを持つか確認。
    - **指値モード**: Entry/SL/TPラインを調整し、「Full Entry」をクリック。適切な指値注文が入るか確認。
6.  **分割・決済**:
    - 「1/2 Entry」等を試し、ロットが半分になっているか確認。
    - 「全ポジション決済」で正しく決済されるか確認。
