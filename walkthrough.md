# AutoLotCalculator Walkthrough

## 1. 概要
このプロジェクトは、cTrader用の自動ロット計算ツールです。
リスク％（資金の何％を失う覚悟か）とSL（損切り幅）を指定するだけで、最適なロット数を自動計算し、ワンクリックで注文できます。

## 2. バージョン情報

### A. デスクトップ版 (cBot)
- **ファイル:** `AutoLotCalculator.cs` (または `_utf8.mq5`)
- **特徴:** チャート上にSL/TPラインが表示され、ドラッグ＆ドロップで調整可能。
- **対象:** PC (Windows/Web)

### B. モバイル対応版 (WebView Plugin)
- **ファイル:** `alc_sdk.html`
- **特徴:** スマホアプリ (cTrader Mobile) 内で動作するWebアプリ。公式SDKを使用。
- **機能:**
    - 残高・現在価格の自動取得
    - シンボル検索 (例: "USDJPY")
    - ロット自動計算
    - 注文実行 (Buy/Sell)

## 3. モバイル版 (SDK Plugin) の導入手順

**cTrader Mobileでは、C#製のBotではなく、このWeb Pluginを使用します。**

### ステップ 1: ファイルの準備
1. アーティファクトフォルダ内の `alc_sdk.html` を見つけます。
2. ファイル名を `index.html` に変更することを推奨します。

### ステップ 2: アップロード (ホスティング)
このHTMLファイルをインターネット上で公開し、URLを取得します。

**方法 A: Netlify Drop (推奨)**
1. [Netlify Drop](https://app.netlify.com/drop) にアクセス。
2. `index.html` が入っている **フォルダごと** ドラッグ＆ドロップ。
3. 発行されたURL (例: `https://...netlify.app`) をコピー。

**方法 B: GitHub Pages**
1. GitHubリポジトリを作成し、`index.html` をアップロード。
2. Settings > Pages で公開し、URLを取得。

### ステップ 3: cTraderでの設定
1. PC版 cTrader (Web/Desktop) を開く。
2. **"Automate"** (または "Plugins") タブへ移動。
3. **"New WebView Plugin"** を作成。
4. 設定画面の **"URL"** 欄に、ステップ2で取得したURLを貼り付け。
5. **"Save" (保存)** する。

### ステップ 4: スマホでの使用
1. スマホの cTrader アプリを開く。
2. チャート画面またはメニューから、作成したプラグインを選択して起動。
3. "Connected!" と表示されれば成功です。

## 4. 注意事項
- **デモ口座でテスト** してください。
- リアル口座で使用する際は、リスク設定 (%) を慎重に確認してください。
