using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class AutoLotCalculator : Robot
    {
        // -------------------------------------------------------------------------
        // パラメータ設定
        // -------------------------------------------------------------------------
        [Parameter("リスク許容率 (%)", DefaultValue = 1.0, MinValue = 0.1, Step = 0.1, Group = "基本設定")]
        public double RiskPercentage { get; set; }

        [Parameter("スリッページ (Pips)", DefaultValue = 3.0, Group = "基本設定")]
        public double SlippagePips { get; set; }

        [Parameter("マジックナンバー", DefaultValue = "AutoLotCalc", Group = "基本設定")]
        public string Label { get; set; }

        // --- ライン位置の自動調整設定 ---
        [Parameter("ATRで自動調整する", DefaultValue = true, Group = "ライン初期位置")]
        public bool UseATR { get; set; }

        [Parameter("ATR倍率 (SL距離)", DefaultValue = 2.0, MinValue = 0.1, Step = 0.1, Group = "ライン初期位置")]
        public double ATRMultiplier { get; set; }

        [Parameter("固定距離 (Pips)", DefaultValue = 20.0, MinValue = 1.0, Group = "ライン初期位置")]
        public double DefaultDistancePips { get; set; }

        // -------------------------------------------------------------------------
        // 定数・列挙型
        // -------------------------------------------------------------------------
        private enum TradeMode
        {
            Market, // 成行
            Pending // 指値/逆指値
        }

        private const string SLLineName = "ALC_SL_Line";
        private const string TPLineName = "ALC_TP_Line";
        private const string EntryLineName = "ALC_Entry_Line";
        private const string EntryLineShallowName = "ALC_Entry_Shallow";
        private const string EntryLineDeepName = "ALC_Entry_Deep";
        
        // -------------------------------------------------------------------------
        // メンバ変数
        // -------------------------------------------------------------------------
        private TradeMode _currentMode = TradeMode.Market;
        private bool _isSplitEntryMode = false;
        private bool _isUIVisible = true;

        // 計算値
        private double _calculatedLot = 0.0;
        private double _riskAmount = 0.0;
        private double _expectedValueMoney = 0.0;
        private double _winRate = 0.0;

        // インジケーター
        private AverageTrueRange _atr;

        // UI コントロール
        private StackPanel _mainPanel;
        private Button _toggleUiButton;
        private Button _modeButton;
        private Button _splitButton;
        private Button _fullEntryButton;
        private Button _halfEntryButton;
        private Button _thirdEntryButton;
        private Button _closeAllButton;
        private Button _redrawButton;

        private TextBlock _labelRisk;
        private TextBlock _labelPrice;
        private TextBlock _labelSL;
        private TextBlock _labelWinRate;
        private TextBlock _labelEV;
        private TextBlock _labelRR;

        // -------------------------------------------------------------------------
        // cBot イベントハンドラ
        // -------------------------------------------------------------------------
        protected override void OnStart()
        {
            Print("AutoLotCalculator: 初期化開始");
            
            // ATRインジケーターの初期化 (期間14, SMA)
            _atr = Indicators.AverageTrueRange(Bars, 14, MovingAverageType.Simple);

            CreateUI();
            
            // 初回のOnStart内で処理（過去データがあればATR値は取れる）
            ResetLinePositions();
            
            // イベント購読
            Chart.ObjectsUpdated += OnChartObjectsUpdated;
            Positions.Closed += OnPositionsClosed;
            
            UpdateAllCalculationsAndLabels();
        }

        protected override void OnStop()
        {
            if (_mainPanel != null)
                Chart.RemoveControl(_mainPanel);
            
            if (_toggleUiButton != null)
                Chart.RemoveControl(_toggleUiButton);

            RemoveLines();
        }
        
        protected override void OnTick()
        {
            // 成行モードの場合、価格変動に合わせてラインや数値をリアルタイム更新する
            if (_currentMode == TradeMode.Market && _isUIVisible)
            {
                UpdateAllCalculationsAndLabels();
            }
        }
        
        private void OnPositionsClosed(PositionClosedEventArgs obj)
        {
            CalculateStats();
        }

        // -------------------------------------------------------------------------
        // ロジック: ライン操作と計算
        // -------------------------------------------------------------------------
        
        private void OnChartObjectsUpdated(ChartObjectsUpdatedEventArgs args)
        {
            foreach (var obj in args.ChartObjects)
            {
                if (obj.Name == SLLineName || 
                    obj.Name == TPLineName || 
                    obj.Name == EntryLineName)
                {
                    UpdateAllCalculationsAndLabels();
                    break; 
                }
            }
        }
        
        private void ResetLinePositions()
        {
            double currentPrice = Symbol.Bid;
            double dist = 0.0;

            // 距離決定ロジック
            if (UseATR && _atr != null && _atr.Result.Count > 0 && !double.IsNaN(_atr.Result.LastValue))
            {
                // ATRベース: 直近のボラティリティ * 倍率
                dist = _atr.Result.LastValue * ATRMultiplier;
                
                // あまりにも小さすぎる場合の保護
                if (dist < 2.0 * Symbol.PipSize) dist = 2.0 * Symbol.PipSize;
            }
            else
            {
                // 固定Pipsベース
                dist = DefaultDistancePips * Symbol.PipSize;
            }

            DrawLine(SLLineName, currentPrice - dist, Color.Red, true);
            
            if (_currentMode == TradeMode.Market)
            {
                // 初期位置：リスクリワード 1:2
                // OnTick自動追従（interactive=false）
                double initialSL = currentPrice - dist;
                double initialTP = currentPrice + (currentPrice - initialSL) * 2.0;
                DrawLine(TPLineName, initialTP, Color.Blue, false); 
            }
            else
            {
               // 指値モード
               // 初期TP = 距離の2倍
               DrawLine(TPLineName, currentPrice + dist * 2.0, Color.Blue, true);
            }

            if (_currentMode == TradeMode.Pending)
            {
                DrawLine(EntryLineName, currentPrice, Color.Gold, true);
            }
        }

        private void UpdateAllCalculationsAndLabels()
        {
            double currentPrice = Symbol.Bid;
            
            var slLine = Chart.FindObject(SLLineName) as ChartHorizontalLine;
            var tpLine = Chart.FindObject(TPLineName) as ChartHorizontalLine;
            var entryLine = Chart.FindObject(EntryLineName) as ChartHorizontalLine;

            if (slLine == null) return; 

            double slPrice = slLine.Y;
            double tpPrice = (tpLine != null) ? tpLine.Y : 0;
            double entryPrice = currentPrice;

            if (_currentMode == TradeMode.Market)
            {
                entryPrice = currentPrice;
                
                // 成行モード: TPライン自動追従 (1:2)
                double dist = Math.Abs(entryPrice - slPrice);
                bool isBuyScenario = entryPrice > slPrice;
                
                if (isBuyScenario)
                {
                    tpPrice = entryPrice + dist * 2.0;
                }
                else
                {
                    tpPrice = entryPrice - dist * 2.0;
                }
                
                DrawLine(TPLineName, tpPrice, Color.Blue, false);
            }
            else // Pending Mode
            {
                if (entryLine != null) entryPrice = entryLine.Y;
                
                 if (_isSplitEntryMode)
                 {
                     double totalDist = Math.Abs(tpPrice - slPrice);
                     bool isBuy = tpPrice > slPrice;
                     
                     double shallowPrice = isBuy ? slPrice + totalDist / 2.0 : slPrice - totalDist / 2.0;
                     double deepPrice = isBuy ? slPrice + totalDist / 4.0 : slPrice - totalDist / 4.0;

                     DrawLine(EntryLineShallowName, shallowPrice, Color.LimeGreen, false, LineStyle.Dots);
                     DrawLine(EntryLineDeepName, deepPrice, Color.DarkGreen, false, LineStyle.Dots);
                 }
                 else
                 {
                     Chart.RemoveObject(EntryLineShallowName);
                     Chart.RemoveObject(EntryLineDeepName);
                 }
            }

            CalculateTradingValues(entryPrice, slPrice);
            UpdateLabels(entryPrice, slPrice, tpPrice);
        }

        private void CalculateTradingValues(double entryPrice, double slPrice)
        {
            _riskAmount = Account.Balance * (RiskPercentage / 100.0);
            
            double slDistance = Math.Abs(entryPrice - slPrice);
            if (slDistance <= Symbol.TickSize)
            {
                _calculatedLot = 0;
                return;
            }

            double ticksRisk = slDistance / Symbol.TickSize;
            if (ticksRisk <= 0) { _calculatedLot = 0; return; }

            double riskPerUnit = ticksRisk * Symbol.TickValue;
            if (riskPerUnit <= 0) { _calculatedLot = 0; return; }

            double volumeUnits = _riskAmount / riskPerUnit;
            
            double lotSize = Symbol.LotSize;
            if (lotSize <= 0) lotSize = 1; 

            double stepUnits = Symbol.VolumeInUnitsStep;
            double minUnits = Symbol.VolumeInUnitsMin;
            
            if (stepUnits > 0) volumeUnits = Math.Floor(volumeUnits / stepUnits) * stepUnits;
            if (volumeUnits < minUnits) volumeUnits = 0.0;
            
            _calculatedLot = volumeUnits / lotSize;
        }

        private void CalculateStats()
        {
            var history = History.Where(h => h.SymbolName == SymbolName && h.Label == Label);
            
            int wins = 0;
            int total = 0;
            double totalProfitMoney = 0;
            double totalLossMoney = 0;
            int loseCount = 0;

            foreach (var trade in history)
            {
                total++;
                if (trade.NetProfit > 0)
                {
                    wins++;
                    totalProfitMoney += trade.NetProfit;
                }
                else
                {
                    loseCount++;
                    totalLossMoney += trade.NetProfit;
                }
            }
            
            _winRate = (total > 0) ? ((double)wins / total) * 100.0 : 0.0;
            
            if (total > 0)
            {
                double winRateDec = (double)wins / total;
                double avgProfit = (wins > 0) ? totalProfitMoney / wins : 0;
                double avgLoss = (loseCount > 0) ? Math.Abs(totalLossMoney / loseCount) : 0;
                
                _expectedValueMoney = (winRateDec * avgProfit) - ((1.0 - winRateDec) * avgLoss);
            }
            else
            {
                _expectedValueMoney = 0;
            }
        }

        // -------------------------------------------------------------------------
        // 発注処理
        // -------------------------------------------------------------------------
        
        private void ExecuteEntry(double ratio = 1.0)
        {
            if (_calculatedLot <= 0)
            {
                Print("ロット数が計算されていないか、少なすぎます。");
                return;
            }

            double targetLot = _calculatedLot * ratio; 
            double volumeInUnits = targetLot * Symbol.LotSize;
            volumeInUnits = Symbol.NormalizeVolumeInUnits(volumeInUnits, RoundingMode.Down);
            
            if (volumeInUnits < Symbol.VolumeInUnitsMin)
            {
                Print($"注文量が最小取引単位({Symbol.VolumeInUnitsMin})を下回っています。");
                return;
            }

            var slLine = Chart.FindObject(SLLineName) as ChartHorizontalLine;
            var tpLine = Chart.FindObject(TPLineName) as ChartHorizontalLine;
            var entryLine = Chart.FindObject(EntryLineName) as ChartHorizontalLine;
            
            if (slLine == null || tpLine == null) return;

            double slPrice = slLine.Y;
            double tpPrice = tpLine.Y;
            double entryPrice = (_currentMode == TradeMode.Pending && entryLine != null) ? entryLine.Y : Symbol.Bid;
            
            if (_currentMode == TradeMode.Market)
            {
                bool isBuy = entryPrice > slPrice;
                var tradeType = isBuy ? TradeType.Buy : TradeType.Sell;
                
                 var result = ExecuteMarketOrder(tradeType, SymbolName, volumeInUnits, Label, null, null);
                if (result.IsSuccessful && result.Position != null)
                {
                    try 
                    {
                        if (slPrice > 0) result.Position.ModifyStopLossPrice(slPrice);
                        if (tpPrice > 0) result.Position.ModifyTakeProfitPrice(tpPrice);
                    }
                    catch
                    {
                        Print("SL/TPの設定に失敗しました。");
                    }
                    
                    Print($"成行注文完了: {tradeType} {volumeInUnits/Symbol.LotSize:F2} Lots");
                }
                else
                {
                    Print($"注文失敗: {result.Error}");
                }
            }
            else // Pending
            {
                bool isLong = entryPrice > slPrice;
                TradeType tradeType = isLong ? TradeType.Buy : TradeType.Sell;
                double currentPrice = (tradeType == TradeType.Buy) ? Symbol.Ask : Symbol.Bid;
                bool isLimit = (tradeType == TradeType.Buy && entryPrice < currentPrice) ||
                               (tradeType == TradeType.Sell && entryPrice > currentPrice);
                
                TradeResult res;
                if (isLimit)
                    res = PlaceLimitOrder(tradeType, SymbolName, volumeInUnits, entryPrice, Label);
                else
                    res = PlaceStopOrder(tradeType, SymbolName, volumeInUnits, entryPrice, Label);
                
                if (res.IsSuccessful && res.PendingOrder != null)
                {
                    try
                    {
                       if (slPrice > 0) res.PendingOrder.ModifyStopLossPrice(slPrice);
                       if (tpPrice > 0) res.PendingOrder.ModifyTakeProfitPrice(tpPrice);
                    }
                    catch
                    {
                        Print("PendingOrder SL/TPの設定に失敗");
                    }
                    
                    Print($"予約注文完了: {tradeType} {(isLimit?"Limit":"Stop")} {volumeInUnits/Symbol.LotSize:F2} Lots @ {entryPrice}");
                }
                else
                {
                    Print($"予約注文失敗: {res.Error}");
                }
            }
        }
        
        private void ExecuteSplitEntry(double baseRatio)
        {
            var shallowLine = Chart.FindObject(EntryLineShallowName) as ChartHorizontalLine;
            var deepLine = Chart.FindObject(EntryLineDeepName) as ChartHorizontalLine;
            var slLine = Chart.FindObject(SLLineName) as ChartHorizontalLine;
            var tpLine = Chart.FindObject(TPLineName) as ChartHorizontalLine;
            
            if (shallowLine == null || deepLine == null || slLine == null || tpLine == null) return;
            
            double targetLotTotal = _calculatedLot * baseRatio; 
            double splitLot = targetLotTotal / 2.0;
            
            double volUnits = splitLot * Symbol.LotSize;
            volUnits = Symbol.NormalizeVolumeInUnits(volUnits, RoundingMode.Down);
            
            if (volUnits < Symbol.VolumeInUnitsMin)
            {
                Print("分割後のロット数が最小単位未満のため発注できません。");
                return;
            }

            double slPrice = slLine.Y;
            double tpPrice = tpLine.Y;
            bool isLong = tpPrice > slPrice; 
            TradeType tradeType = isLong ? TradeType.Buy : TradeType.Sell;
            
            PlaceOrderGeneric(tradeType, volUnits, shallowLine.Y, slPrice, tpPrice);
            PlaceOrderGeneric(tradeType, volUnits, deepLine.Y, slPrice, tpPrice);
            
            Print("分割注文を実行しました。");
        }

        private void PlaceOrderGeneric(TradeType type, double volume, double price, double sl, double tp)
        {
            double current = (type == TradeType.Buy) ? Symbol.Ask : Symbol.Bid;
            bool isLimit = (type == TradeType.Buy && price < current) || (type == TradeType.Sell && price > current);
            
            TradeResult res;
            if (isLimit)
                res = PlaceLimitOrder(type, SymbolName, volume, price, Label);
            else
                res = PlaceStopOrder(type, SymbolName, volume, price, Label);

             if (res.IsSuccessful && res.PendingOrder != null)
             {
                 if (sl > 0) res.PendingOrder.ModifyStopLossPrice(sl);
                 if (tp > 0) res.PendingOrder.ModifyTakeProfitPrice(tp);
             }
        }

        private void CloseAllPositions()
        {
            var positions = Positions.FindAll(Label, SymbolName);
            foreach (var p in positions)
            {
                ClosePosition(p);
            }
            Print($"{positions.Length} 件のポジションを決済しました。");
        }

        // -------------------------------------------------------------------------
        // UI 作成と更新
        // -------------------------------------------------------------------------
        private void CreateUI()
        {
            _toggleUiButton = new Button
            {
                Text = "UIを隠す",
                BackgroundColor = Color.Gray,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(20, 20, 0, 0),
                Width = 100,
                Height = 30
            };
            _toggleUiButton.Click += args => ToggleUI();
            Chart.AddControl(_toggleUiButton);

            _mainPanel = new StackPanel
            {
                BackgroundColor = Color.FromArgb(200, 30, 30, 30), 
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(20, 60, 0, 0), 
                Width = 200,
            };
            
            _modeButton = CreateButton("モード: 成行", Color.RoyalBlue);
            _modeButton.Click += args => SwitchMode();
            _mainPanel.AddChild(_modeButton);
            
            _splitButton = CreateButton("分割: OFF", Color.DimGray);
            _splitButton.Click += args => ToggleSplitMode();
            _mainPanel.AddChild(_splitButton);
            
            _fullEntryButton = CreateButton("Full Entry", Color.ForestGreen);
            _fullEntryButton.Click += args => OnEntryClick(1.0);
            _mainPanel.AddChild(_fullEntryButton);
            
            _halfEntryButton = CreateButton("1/2 Entry", Color.SeaGreen);
            _halfEntryButton.Click += args => OnEntryClick(0.5);
            _mainPanel.AddChild(_halfEntryButton);
            
            _thirdEntryButton = CreateButton("1/3 Entry", Color.SeaGreen);
            _thirdEntryButton.Click += args => OnEntryClick(1.0/3.0);
            _mainPanel.AddChild(_thirdEntryButton);
            
            _redrawButton = CreateButton("手動更新", Color.Gray);
            _redrawButton.Click += args => { ResetLinePositions(); UpdateAllCalculationsAndLabels(); };
            _mainPanel.AddChild(_redrawButton);
            
            _closeAllButton = CreateButton("全ポジション決済", Color.Crimson);
            _closeAllButton.Click += args => CloseAllPositions();
            _mainPanel.AddChild(_closeAllButton);

            _mainPanel.AddChild(CreateSeparator());
            _labelRisk = CreateLabel("Risk: ---");
            _mainPanel.AddChild(_labelRisk);
            
            _labelPrice = CreateLabel("Price: ---");
            _mainPanel.AddChild(_labelPrice);
            
            _labelSL = CreateLabel("SL: --- pips");
            _mainPanel.AddChild(_labelSL);
            
            _labelWinRate = CreateLabel("WinRate: --- %");
            _mainPanel.AddChild(_labelWinRate);
            
            _labelEV = CreateLabel("EV: ---");
            _mainPanel.AddChild(_labelEV);
            
            _labelRR = CreateLabel("RR Ratio: ---");
            _mainPanel.AddChild(_labelRR);

            Chart.AddControl(_mainPanel);
        }

        private Button CreateButton(string text, Color color)
        {
            return new Button
            {
                Text = text,
                BackgroundColor = color,
                Height = 30,
                Margin = new Thickness(10, 5, 10, 0), 
                ForegroundColor = Color.White
            };
        }

        private TextBlock CreateLabel(string text)
        {
            return new TextBlock
            {
                Text = text,
                ForegroundColor = Color.WhiteSmoke,
                Margin = new Thickness(10, 2, 10, 0) 
            };
        }
        
        private Control CreateSeparator()
        {
            return new TextBlock
            {
                Height = 1,
                BackgroundColor = Color.Gray,
                Margin = new Thickness(10, 10, 10, 5)
            };
        }

        // -------------------------------------------------------------------------
        // UI イベントロジック
        // -------------------------------------------------------------------------
        
        private void ToggleUI()
        {
            _isUIVisible = !_isUIVisible;
            _mainPanel.IsVisible = _isUIVisible;
            _toggleUiButton.Text = _isUIVisible ? "UIを隠す" : "UIを表示";
            
            if (_isUIVisible)
            {
                ResetLinePositions();
                UpdateAllCalculationsAndLabels();
            }
            else
            {
                RemoveLines();
            }
        }

        private void SwitchMode()
        {
            _currentMode = (_currentMode == TradeMode.Market) ? TradeMode.Pending : TradeMode.Market;
            _modeButton.Text = (_currentMode == TradeMode.Market) ? "モード: 成行" : "モード: 指値";
            
            if (_currentMode == TradeMode.Pending)
            {
                if (Chart.FindObject(EntryLineName) == null)
                    DrawLine(EntryLineName, Symbol.Bid, Color.Gold, true);
                    
                 var tp = Chart.FindObject(TPLineName) as ChartHorizontalLine;
                 if(tp != null) tp.IsInteractive = true;
            }
            else
            {
                 // MarketモードになったらEntryラインは消す
                 Chart.RemoveObject(EntryLineName);
                 Chart.RemoveObject(EntryLineShallowName);
                 Chart.RemoveObject(EntryLineDeepName);
                
                 var tp = Chart.FindObject(TPLineName) as ChartHorizontalLine;
                 if(tp != null) tp.IsInteractive = false; 
            }
            UpdateAllCalculationsAndLabels();
        }

        private void ToggleSplitMode()
        {
            if (_currentMode == TradeMode.Market) return; 

            _isSplitEntryMode = !_isSplitEntryMode;
            _splitButton.Text = _isSplitEntryMode ? "分割: ON" : "分割: OFF";
            _splitButton.BackgroundColor = _isSplitEntryMode ? Color.Orange : Color.DimGray;
            
            UpdateAllCalculationsAndLabels();
        }

        private void OnEntryClick(double ratio)
        {
            if (_currentMode == TradeMode.Pending && _isSplitEntryMode)
            {
                ExecuteSplitEntry(ratio);
            }
            else
            {
                ExecuteEntry(ratio);
            }
        }

        private void UpdateLabels(double entryPrice, double slPrice, double tpPrice)
        {
            _labelRisk.Text = $"Risk: {_riskAmount:F2} {Account.Asset.Name}";
            _labelPrice.Text = $"Price: {Symbol.Bid}";
            
            double pips = Math.Abs(entryPrice - slPrice) / Symbol.PipSize;
            _labelSL.Text = $"SL: {pips:F1} pips";
            
            _labelWinRate.Text = $"勝率: {_winRate:F1} %";
            _labelEV.Text = $"期待値: {_expectedValueMoney:F0}";

            // RR比
            double risk = Math.Abs(entryPrice - slPrice);
            double reward = Math.Abs(tpPrice - entryPrice);
            if (risk > 0)
            {
                double rr = reward / risk;
                _labelRR.Text = $"RR比: 1 : {rr:F2}";
            }
            else
            {
                _labelRR.Text = "RR比: -";
            }
            
            // ボタンラベル更新
            string dir = (entryPrice > slPrice) ? "Buy" : "Sell";
            _fullEntryButton.Text = $"{dir} {_calculatedLot:F2} Lots";
            
            double halfUnits = Symbol.NormalizeVolumeInUnits(_calculatedLot * 0.5 * Symbol.LotSize, RoundingMode.Down);
            double halfDisplay = halfUnits / Symbol.LotSize;
            _halfEntryButton.Text = $"{dir} {halfDisplay:F2} (1/2)";
            
            double thirdUnits = Symbol.NormalizeVolumeInUnits((_calculatedLot / 3.0) * Symbol.LotSize, RoundingMode.Down);
            double thirdDisplay = thirdUnits / Symbol.LotSize;
            _thirdEntryButton.Text = $"{dir} {thirdDisplay:F2} (1/3)";
        }
        
        // -------------------------------------------------------------------------
        // ヘルパー
        // -------------------------------------------------------------------------

        private void DrawLine(string name, double price, Color color, bool interactive, LineStyle style = LineStyle.Solid)
        {
            var line = Chart.DrawHorizontalLine(name, price, color);
            line.IsInteractive = interactive;
            line.LineStyle = style;
        }

        private void RemoveLines()
        {
            Chart.RemoveObject(SLLineName);
            Chart.RemoveObject(TPLineName);
            Chart.RemoveObject(EntryLineName);
            Chart.RemoveObject(EntryLineShallowName);
            Chart.RemoveObject(EntryLineDeepName);
        }
    }
}
