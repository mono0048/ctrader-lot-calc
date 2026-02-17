using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.API.Indicators;
using System.Text.Json;
using System.IO;

namespace cAlgo.Robots
{
    // -------------------------------------------------------------------------
    // AutoLotCalculatorWeb (Hybrid WebView cBot)
    // -------------------------------------------------------------------------
    // Note: This is implemented as a "Robot" to allow Chart Drawing (SL Lines)
    // and Trading Actions, which are limited in pure "WebView Plugin" types.
    // It uses the same WebView technology as the official plugins.
    // -------------------------------------------------------------------------
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.FullAccess)] // FullAccess required for file I/O (temp html)
    public class AutoLotCalculatorWeb : Robot
    {
        // -------------------------------------------------------------------------
        // Parameters
        // -------------------------------------------------------------------------
        [Parameter("基本: リスク許容率 (%)", DefaultValue = 1.0, MinValue = 0.1, Step = 0.1, Group = "Basic Settings")]
        public double RiskPercentage { get; set; }
        
        [Parameter("基本: ATR自動調整", DefaultValue = true, Group = "Basic Settings")]
        public bool UseATR { get; set; }

        [Parameter("注文: スリッページ (Pips)", DefaultValue = 2.0, Group = "Order Settings")]
        public double SlippagePips { get; set; }

        [Parameter("注文: マジックナンバー", DefaultValue = "Active_ALC_Web")]
        public string Label { get; set; }

        // -------------------------------------------------------------------------
        // Internal Members
        // -------------------------------------------------------------------------
        private WebView _webView;
        private const string SLLineName = "ALC_Web_SL";
        private const string TPLineName = "ALC_Web_TP";
        
        private AverageTrueRange _atr;
        private double _calculatedLot = 0;
        private double _riskAmount = 0;
        private string _tempHtmlPath;

        protected override void OnStart()
        {
            Print("AutoLotCalculatorWeb: Starting...");

            // 1. Initialize Indicator
            _atr = Indicators.AverageTrueRange(Bars, 14, MovingAverageType.Simple);

            // 2. Setup WebView
            _webView = new WebView();
            _webView.HorizontalAlignment = HorizontalAlignment.Stretch; 
            _webView.VerticalAlignment = VerticalAlignment.Stretch;
            // cTrader Mobile might ignore exact size, but good for desktop testing
            _webView.Height = 400; 
            _webView.Width = 300;
            
            // Listen to messages from JS (Official WebView API)
            _webView.WebMessageReceived += OnWebMessageReceived;
            
            // 3. Load HTML Content
            // We save HTML to a local temp file to ensure maximum compatibility 
            // with different cTrader versions and API 'Navigate' methods.
            string htmlContent = GetHtmlContent();
            _tempHtmlPath = Path.Combine(Path.GetTempPath(), $"alc_web_{Guid.NewGuid()}.html");
            File.WriteAllText(_tempHtmlPath, htmlContent);
            
            // Navigate to the local file
            // Note: We use AbsoluteUri to get "file:///..." format which is robust
            _webView.NavigateAsync(new Uri(_tempHtmlPath).AbsoluteUri);

            // 4. Add to Chart
            // Note: On Mobile, this typically opens as a separate view or bottom sheet
            Chart.AddControl(_webView);

            // 5. Draw Helper Lines (The "Core" Feature)
            DrawInitialLines();

            // 6. Listen for Line Drags
            Chart.ObjectsUpdated += OnChartObjectsUpdated;
        }

        protected override void OnStop()
        {
            // Cleanup
            if (_webView != null)
            {
                _webView.WebMessageReceived -= OnWebMessageReceived;
                Chart.RemoveControl(_webView);
            }
            
            Chart.ObjectsUpdated -= OnChartObjectsUpdated;
            Chart.RemoveObject(SLLineName);
            Chart.RemoveObject(TPLineName);

            // Try to delete temp file
            try { 
                if (File.Exists(_tempHtmlPath)) File.Delete(_tempHtmlPath); 
            } catch { /* ignore */ }
        }

        protected override void OnTick()
        {
            // Update Price on UI
            SendToWeb("PRICE", Symbol.Bid.ToString("F" + Symbol.Digits));
            
            // Re-sync logic (in case of price movement affecting RR)
            CalculateAndSync();
        }

        // -------------------------------------------------------------------------
        // Core Logic: Sync Chart Lines <-> Web UI
        // -------------------------------------------------------------------------
        private void OnChartObjectsUpdated(ChartObjectsUpdatedEventArgs args)
        {
            // If user drags the red/blue lines, we detect it here
            bool relevantChange = args.ChartObjects.Any(o => o.Name == SLLineName || o.Name == TPLineName);
            if (relevantChange)
            {
                CalculateAndSync();
            }
        }

        private void CalculateAndSync()
        {
            var slLine = Chart.FindObject(SLLineName) as ChartHorizontalLine;
            var tpLine = Chart.FindObject(TPLineName) as ChartHorizontalLine;
            if (slLine == null || tpLine == null) return;

            double currentPrice = Symbol.Bid;
            double slPrice = slLine.Y;
            double tpPrice = tpLine.Y;

            // 1. Calculate Risk Amount
            _riskAmount = Account.Balance * (RiskPercentage / 100.0);

            // 2. Calculate Distance
            double slDist = Math.Abs(currentPrice - slPrice); // Price difference
            double slPips = slDist / Symbol.PipSize;

            if (slDist > Symbol.TickSize)
            {
                // 3. Calculate Lot Size
                // Risk / (Distance * TickValue) calculation
                double riskPerUnit = (slDist / Symbol.TickSize) * Symbol.TickValue;
                double rawVolume = _riskAmount / riskPerUnit;
                
                // Normalize Step
                double step = Symbol.VolumeInUnitsStep;
                if (step > 0) rawVolume = Math.Floor(rawVolume / step) * step;
                if (rawVolume < Symbol.VolumeInUnitsMin) rawVolume = 0;
                
                // Convert to Lots for display
                _calculatedLot = rawVolume / Symbol.LotSize;
                
                // 4. Prepare JSON Data for UI
                var data = new
                {
                    lot = _calculatedLot.ToString("F2"),
                    risk = _riskAmount.ToString("F0"),
                    sl_pips = slPips.ToString("F1"),
                    sl_price = slPrice.ToString("F" + Symbol.Digits),
                    tp_price = tpPrice.ToString("F" + Symbol.Digits),
                    rr = (Math.Abs(tpPrice - currentPrice) / slDist).ToString("F2")
                };
                
                // 5. Send to JS
                SendToWeb("DATA", JsonSerializer.Serialize(data));
            }
        }

        private void OnWebMessageReceived(WebViewWebMessageReceivedEventArgs args)
        {
            // Handle Commands from JS
            // Format: "CMD:VALUE"
            string message = args.Message;
            if (string.IsNullOrEmpty(message)) return;

            string[] parts = message.Split(':');
            string cmd = parts[0];
            string val = parts.Length > 1 ? parts[1] : "";

            switch (cmd)
            {
                case "BUY":
                    ExecuteTrade(TradeType.Buy);
                    break;
                case "SELL":
                    ExecuteTrade(TradeType.Sell);
                    break;
                case "CLOSE":
                    CloseAllPositions();
                    break;
                case "RISK":
                    if (double.TryParse(val, out double newRisk))
                    {
                        RiskPercentage = newRisk;
                        CalculateAndSync();
                    }
                    break;
                case "ADJUST_SL": // From +/- buttons
                    AdjustSLLine(val);
                    break;
            }
        }

        private void AdjustSLLine(string direction)
        {
            var slLine = Chart.FindObject(SLLineName) as ChartHorizontalLine;
            if (slLine == null) return;

            if (slLine == null) return;
            
            // Move SL away or closer? 
            // Logic depends on Buy/Sell direction, but here we just move 'away' from price for safety
            // or just absolute Y adjustment. 
            // Simple logic: Move UP or DOWN
            
            // Better logic: Increase/Decrease distance
            double currentPrice = Symbol.Bid;
            if (slLine.Y < currentPrice) // Long scenario (SL below)
            {
                if (direction == "PLUS") slLine.Y -= Symbol.PipSize; // Widen
                else slLine.Y += Symbol.PipSize; // Narrow
            }
            else // Short scenario (SL above)
            {
                if (direction == "PLUS") slLine.Y += Symbol.PipSize; // Widen
                else slLine.Y -= Symbol.PipSize; // Narrow
            }
            // Trigger update triggers ObjectUpdated
        }

        private void ExecuteTrade(TradeType type)
        {
            if (_calculatedLot <= 0) 
            {
                SendToWeb("ALERT", "ロット数が不正です (0 Lot)");
                return;
            }
            
            double volume = Symbol.NormalizeVolumeInUnits(_calculatedLot * Symbol.LotSize, RoundingMode.Down);
            if(volume < Symbol.VolumeInUnitsMin) 
            {
                SendToWeb("ALERT", "最小取引数量未満です");
                return;
            }

            var slLine = Chart.FindObject(SLLineName) as ChartHorizontalLine;
            var tpLine = Chart.FindObject(TPLineName) as ChartHorizontalLine;
            double sl = slLine != null ? slLine.Y : 0;
            double tp = tpLine != null ? tpLine.Y : 0;

            var result = ExecuteMarketOrder(type, SymbolName, volume, Label, sl, tp);
            if (result.IsSuccessful)
            {
                SendToWeb("ALERT", $"注文成功 #{result.Position.Id}");
            }
            else
            {
                SendToWeb("ALERT", $"エラー: {result.Error}");
            }
        }

        private void CloseAllPositions()
        {
            var positions = Positions.FindAll(Label, SymbolName);
            foreach(var p in positions) ClosePosition(p);
            SendToWeb("ALERT", $"{positions.Length} のポジションを決済しました");
        }

        private void SendToWeb(string type, string payload)
        {
            // Call JS function: receiveFromCAlgo(type, payload)
            // Use ExecuteScript as it is most reliable
            string safePayload = payload.Replace("'", "\\'").Replace("\n", ""); 
            string script = $"if(window.receiveFromCAlgo) window.receiveFromCAlgo('{type}', '{safePayload}');";
            _webView.ExecuteScript(script);
        }

        private void DrawInitialLines()
        {
            double price = Symbol.Bid;
            double dist = 20 * Symbol.PipSize; 
            
            if (UseATR && _atr != null && _atr.Result.Count > 0)
            {
                dist = _atr.Result.LastValue * 2.0;
                if (dist < 5 * Symbol.PipSize) dist = 5 * Symbol.PipSize;
                if (dist > 200 * Symbol.PipSize) dist = 200 * Symbol.PipSize; // Cap
            }

            // Simple Logic: Default to Long setup (SL below)
            double sl = price - dist;
            double tp = price + dist * 2;

            var lineSL = Chart.DrawHorizontalLine(SLLineName, sl, Color.Red);
            lineSL.IsInteractive = true;
            lineSL.Thickness = 2;
            lineSL.Comment = "Stop Loss";

            var lineTP = Chart.DrawHorizontalLine(TPLineName, tp, Color.Blue);
            lineTP.IsInteractive = true;
            lineTP.Thickness = 2;
            lineTP.Comment = "Take Profit";
        }

        // -------------------------------------------------------------------------
        // UI Assets (HTML/JS/CSS)
        // -------------------------------------------------------------------------
        private string GetHtmlContent()
        {
            return @"
<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8' />
    <meta name='viewport' content='width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no' />
    <style>
        :root {
            --bg-color: #121212;
            --card-bg: #1e1e1e;
            --text-main: #ffffff;
            --text-sub: #b0b0b0;
            --accent-buy: #2196F3;
            --accent-sell: #F44336;
            --accent-sl: #e91e63;
        }
        body {
            background-color: var(--bg-color);
            color: var(--text-main);
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
            margin: 0; padding: 12px;
            font-size: 14px; touch-action: manipulation;
        }
        /* Mobile-First Grid Layout */
        .grid-container {
            display: grid;
            gap: 12px;
        }
        .card {
            background: var(--card-bg);
            border-radius: 8px;
            padding: 12px;
            box-shadow: 0 2px 4px rgba(0,0,0,0.3);
        }
        /* Metrics Display */
        .metrics {
            display: grid;
            grid-template-columns: 1fr 1fr;
            gap: 8px;
        }
        .metric-item { display: flex; flex-direction: column; }
        .label { font-size: 11px; color: var(--text-sub); margin-bottom: 2px; }
        .value { font-size: 16px; font-weight: 600; }
        .value.highlight { color: #4CAF50; font-size: 20px; }
        
        /* Interactive Controls */
        .slider-group {
            display: flex; flex-direction: column; gap: 8px;
        }
        input[type=range] {
            width: 100%; height: 6px;
            background: #444; border-radius: 3px;
            outline: none;
        }

        /* Buttons */
        .btn-row { display: flex; gap: 10px; }
        .btn {
            flex: 1; padding: 14px;
            border: none; border-radius: 6px;
            font-size: 16px; font-weight: 700;
            color: #fff; cursor: pointer;
            transition: opacity 0.2s;
        }
        .btn:active { opacity: 0.7; transform: scale(0.98); }
        .btn-buy { background: var(--accent-buy); }
        .btn-sell { background: var(--accent-sell); }
        
        .btn-close {
            background: #424242; font-size: 13px; padding: 10px; width: 100%;
            margin-top: 8px;
        }

        /* Fine Tuning */
        .fine-tune {
            display: flex; align-items: center; justify-content: space-between;
            background: #2a2a2a; border-radius: 6px; padding: 4px;
        }
        .tune-btn {
            background: #333; color: #fff;
            border: none; width: 40px; height: 32px;
            border-radius: 4px; font-weight: bold;
        }

        /* Toast */
        .toast {
            position: fixed; top: 20px; left: 50%; transform: translateX(-50%);
            background: rgba(33, 33, 33, 0.95); color: #fff;
            padding: 8px 16px; border-radius: 20px;
            font-size: 13px; pointer-events: none;
            opacity: 0; transition: opacity 0.3s; z-index: 100;
        }
    </style>
</head>
<body>
    <div class='grid-container'>
        <!-- Top: Price & Pips -->
        <div class='card' style='display:flex; justify-content:space-between; align-items:center;'>
            <div>
                <div class='label'>BID PRICE</div>
                <div id='price' class='value'>Loading...</div>
            </div>
            <div style='text-align:right;'>
                <div class='label'>SL DISTANCE</div>
                <div class='value'><span id='sl-pips'>0</span> <span style='font-size:12px'>pips</span></div>
            </div>
        </div>

        <!-- Middle: Lot Size -->
        <div class='card metrics'>
            <div class='metric-item'>
                <span class='label'>CALCULATED LOTS</span>
                <span id='lot' class='value highlight'>0.00</span>
            </div>
            <div class='metric-item'>
                <span class='label'>RISK AMOUNT</span>
                <span id='risk-val' class='value'>$0</span>
            </div>
        </div>

        <!-- Controls: Risk & SL Adjust -->
        <div class='card slider-group'>
            <div style='display:flex; justify-content:space-between;'>
                <span class='label'>RISK: <span id='risk-disp' style='color:#fff'>1.0</span>%</span>
                <span class='label'>RR: <span id='rr-val'>---</span></span>
            </div>
            <input type='range' id='risk-slider' min='0.1' max='5.0' step='0.1' value='1.0'>
            
            <!-- Fine Tune SL -->
            <div style='margin-top:8px;'>
                <div class='label' style='margin-bottom:4px;'>SL FINE TUNE</div>
                <div class='fine-tune'>
                    <button class='tune-btn' onclick='send(""ADJUST_SL"", ""MINUS"")'>-</button>
                    <span style='font-size:11px; color:#888;'>WIDER / NARROWER</span>
                    <button class='tune-btn' onclick='send(""ADJUST_SL"", ""PLUS"")'>+</button>
                </div>
            </div>
        </div>

        <!-- Bottom: Actions -->
        <div>
            <div class='btn-row'>
                <button class='btn btn-sell' onclick='send(""SELL"")'>SELL</button>
                <button class='btn btn-buy' onclick='send(""BUY"")'>BUY</button>
            </div>
            <button class='btn btn-close' onclick='send(""CLOSE"")'>CLOSE ALL POSITIONS</button>
        </div>
    </div>

    <div id='toast' class='toast'>Hello cTrader!</div>

    <script>
        // --- Communication Bridge ---
        window.receiveFromCAlgo = function(type, payload) {
            try {
                if (type === 'PRICE') {
                    document.getElementById('price').innerText = payload;
                } else if (type === 'DATA') {
                    updateUI(JSON.parse(payload));
                } else if (type === 'ALERT') {
                    showToast(payload);
                }
            } catch(e) { console.error('JS Error:', e); }
        };

        function send(cmd, val = '') {
            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage(cmd + ':' + val);
            } else {
                console.log('Mock Send:', cmd, val);
            }
        }

        // --- UI Logic ---
        function updateUI(data) {
            document.getElementById('lot').innerText = data.lot;
            document.getElementById('risk-val').innerText = data.risk;
            document.getElementById('sl-pips').innerText = data.sl_pips;
            document.getElementById('rr-val').innerText = data.rr;
        }

        // Slider
        const slider = document.getElementById('risk-slider');
        const riskDisp = document.getElementById('risk-disp');
        slider.addEventListener('input', (e) => {
            riskDisp.innerText = e.target.value;
            send('RISK', e.target.value);
        });

        // Toast
        let toastTimeout;
        function showToast(msg) {
            const t = document.getElementById('toast');
            t.innerText = msg;
            t.style.opacity = '1';
            clearTimeout(toastTimeout);
            toastTimeout = setTimeout(() => { t.style.opacity = '0'; }, 3000);
        }
    </script>
</body>
</html>
";
        }
    }
}
