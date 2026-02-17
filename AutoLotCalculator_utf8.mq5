//+------------------------------------------------------------------+
//|                                              AutoLotCalculator.mq5 |
//|                                  Copyright 2025, MetaQuotes Software Corp. |
//|                                              https://www.mql5.com |
//+------------------------------------------------------------------+
#property copyright "Copyright 2025, MetaQuotes Software Corp."
#property link      "https://www.mql5.com"
#property version   "1.32" // ★変更点: RR比の自動調整機能、RR比率ラベルを追加

#include <Trade\Trade.mqh>

//--- input parameters
input double RiskPercentage = 1.0; 
input double slippage = 3.0;
input ulong MagicNumber = 123456;

//--- トレードモードを定義
enum ENUM_TRADE_MODE
{
   MODE_MARKET,
   MODE_PENDING
};

//--- global variables
double balance = 0.0;
double margin_free = 0.0;
double currentPrice = 0.0;
double g_tp_price = 0.0;
double g_sl_price = 0.0;
double g_entry_price = 0.0;
double g_entry_price_shallow = 0.0;
double g_entry_price_deep = 0.0;
double lot = 0.0;
double riskAmount = 0.0;
double winRate = 0.0;
double expectedValue_Money = 0.0;
double expectedValue_Pips = 0.0;
bool isUIVisible = true;
ENUM_TRADE_MODE tradeMode = MODE_MARKET;
bool isSplitEntryMode = false;

//--- Forward Declarations
void UpdateLinePrices();
void HandleObjectClickEvent(const string &sparam);
void CreateUIElements();
void DeleteAllObjects();
void UpdateLabels();
double AdjustLot(double raw_lot);
void ResetButtonState(const string &buttonName);
bool SendMarketOrderWithRetry(double volume, double stopLoss, double takeProfit, bool isBuy);
bool SendPendingOrderWithRetry(double volume, double entryPrice, double stopLoss, double takeProfit);
void CloseAllPositions();
void ResetLinePositions();
void CreateButton(string name, string text, int x, int y);
void CreateLabel(string name, int x, int y);
void CalculateTradingValues();
double CalculateWinRate();
void CalculateExpectedValues();
void SetUIVisibility(bool show);
void UpdateModeUI();
void UpdateAllCalculationsAndLabels();

//+------------------------------------------------------------------+
//| Expert initialization function
//+------------------------------------------------------------------+
int OnInit()
{
   Print("EA初期化開始: AutoLotCalculator v1.32");

   CreateUIElements();
   ResetLinePositions();
   UpdateModeUI();
   UpdateAllCalculationsAndLabels();
   SetUIVisibility(isUIVisible);
   ChartRedraw();

   Print("EA初期化完了。");
   return(INIT_SUCCEEDED);
}

//+------------------------------------------------------------------+
//| Expert deinitialization function
//+------------------------------------------------------------------+
void OnDeinit(const int reason)
{
   DeleteAllObjects();
}


//+------------------------------------------------------------------+
//| ラインの位置を初期化する専用関数
//+------------------------------------------------------------------+
void ResetLinePositions()
{
    currentPrice = (SymbolInfoDouble(_Symbol, SYMBOL_BID) + SymbolInfoDouble(_Symbol, SYMBOL_ASK)) / 2;
    double point = SymbolInfoDouble(_Symbol, SYMBOL_POINT);
    if(point > 0)
    {
       ObjectMove(0, "SLLine", 0, 0, currentPrice - 100 * point);
       ObjectMove(0, "TPLine", 0, 0, currentPrice + 200 * point);
    }
}


//+------------------------------------------------------------------+
//| Chart event function
//+------------------------------------------------------------------+
void OnChartEvent(const int id, const long &lparam, const double &dparam, const string &sparam)
{
   if (id == CHARTEVENT_OBJECT_CLICK)
   {
      if(sparam == "UIToggleButton")
      {
         isUIVisible = !isUIVisible;
         SetUIVisibility(isUIVisible);
         ResetButtonState("UIToggleButton");
         if(isUIVisible) UpdateAllCalculationsAndLabels();
         return;
      }
      
      if(sparam == "ModeToggleButton")
      {
         if(tradeMode == MODE_MARKET) tradeMode = MODE_PENDING;
         else tradeMode = MODE_MARKET;

         isSplitEntryMode = false;
         ResetLinePositions();
         UpdateModeUI();
         UpdateAllCalculationsAndLabels();
         ResetButtonState("ModeToggleButton");
         return;
      }
      
      if(sparam == "SplitEntryToggleButton")
      {
         if(tradeMode == MODE_PENDING)
         {
            bool intended_state = !isSplitEntryMode;
            UpdateLinePrices();
            CalculateTradingValues(); 
            bool can_split = AdjustLot(lot / 2.0) > 0;
            
            if(intended_state && !can_split)
            {
                isSplitEntryMode = false;
                // Alert("ロットサイズが小さすぎるため分割モードをONにできません。"); // アラート不要
            }
            else
            {
                isSplitEntryMode = intended_state;
            }
            
            UpdateModeUI();
            UpdateAllCalculationsAndLabels();
         }
         ResetButtonState("SplitEntryToggleButton");
         return;
      }
      
      if(!isUIVisible) return;
      
      HandleObjectClickEvent(sparam);
      UpdateAllCalculationsAndLabels();
   }
   else if (id == CHARTEVENT_OBJECT_DRAG && (sparam == "SLLine" || (tradeMode == MODE_PENDING && sparam == "TPLine")))
   {
      if(!isUIVisible) return;
      UpdateAllCalculationsAndLabels();
   }
}

//+------------------------------------------------------------------+
//| UIの表示・非表示を切り替える関数
//+------------------------------------------------------------------+
void SetUIVisibility(bool show)
{
    // ボタンとラベルの表示/非表示（画面外への移動）
    string buttons_to_toggle[] = {"ModeToggleButton", "SplitEntryToggleButton", "FullEntryButton", "HalfEntryButton", "ThirdEntryButton", "RedrawButton", "CloseAllButton"};
    string labels_to_toggle[]  = {"RiskAmountLabel", "CurrentPriceLabel", "SLPipsLabel", "WinRateLabel", "EV_MoneyLabel", "EV_PipsLabel", "RRRatioLabel"};
    
    int button_x = 20;
    int label_x = 150;

    for(int i = 0; i < ArraySize(buttons_to_toggle); i++)
    {
        ObjectSetInteger(0, buttons_to_toggle[i], OBJPROP_XDISTANCE, show ? button_x : -20000);
    }
    for(int i = 0; i < ArraySize(labels_to_toggle); i++)
    {
        ObjectSetInteger(0, labels_to_toggle[i], OBJPROP_XDISTANCE, show ? label_x : -20000);
    }
    
    // ★★★ ここからが新しいロジックです ★★★

    if(show) // 「UIを表示」を押した場合
    {
        // UI再表示は、ラインの完全な再初期化として機能する
        // 1. メインラインを（なければ）再生成
        CreateUIElements();
        // 2. ライン位置を初期化
        ResetLinePositions();
        // 3. 現在のモードに合わせて指値ライン等を再描画
        UpdateModeUI();
        // 4. 全ての数値を再計算して表示を更新
        UpdateAllCalculationsAndLabels();
    }
    else // 「UIを隠す」を押した場合
    {
        // UI非表示は、全ての取引ラインを削除する
        if(ObjectFind(0, "SLLine") >= 0)            ObjectDelete(0, "SLLine");
        if(ObjectFind(0, "TPLine") >= 0)            ObjectDelete(0, "TPLine");
        if(ObjectFind(0, "EntryLine") >= 0)         ObjectDelete(0, "EntryLine");
        if(ObjectFind(0, "EntryLine_Shallow") >= 0) ObjectDelete(0, "EntryLine_Shallow");
        if(ObjectFind(0, "EntryLine_Deep") >= 0)    ObjectDelete(0, "EntryLine_Deep");
    }
    
    // トグルボタン自体のテキストを更新
    ObjectSetString(0, "UIToggleButton", OBJPROP_TEXT, show ? "UIを隠す" : "UIを表示");
    ChartRedraw();
}

//+------------------------------------------------------------------+
//| Update all calculations and labels
//+------------------------------------------------------------------+
void UpdateAllCalculationsAndLabels()
{
   UpdateLinePrices();
   CalculateTradingValues();
   winRate = CalculateWinRate();
   CalculateExpectedValues();
   UpdateLabels();
   ChartRedraw();
}

//+------------------------------------------------------------------+
//| Update line prices based on mode
//+------------------------------------------------------------------+
void UpdateLinePrices()
{
    currentPrice = (SymbolInfoDouble(_Symbol, SYMBOL_BID) + SymbolInfoDouble(_Symbol, SYMBOL_ASK)) / 2;
    
    if(ObjectFind(0,"SLLine")<0 || ObjectFind(0,"TPLine")<0) return;
    g_sl_price = ObjectGetDouble(0, "SLLine", OBJPROP_PRICE);
    
    if(tradeMode == MODE_MARKET)
    {
        g_tp_price = currentPrice + (currentPrice - g_sl_price) * 2;
        g_entry_price = currentPrice;
        ObjectMove(0, "TPLine", 0, 0, g_tp_price);
    }
    else // MODE_PENDING
    {
        g_tp_price = ObjectGetDouble(0, "TPLine", OBJPROP_PRICE);

        double total_distance = MathAbs(g_tp_price - g_sl_price);
        bool is_buy = g_tp_price > g_sl_price;
        
        g_entry_price = is_buy ? (g_sl_price + total_distance / 3.0) : (g_sl_price - total_distance / 3.0);
        g_entry_price_shallow = is_buy ? (g_sl_price + total_distance / 2.0) : (g_sl_price - total_distance / 2.0);
        g_entry_price_deep = is_buy ? (g_sl_price + total_distance / 4.0) : (g_sl_price - total_distance / 4.0);

        // ラインの移動はCalculateTradingValuesで調整後に行うため、ここでは行わない
    }
}

//+------------------------------------------------------------------+
//| Handle object click event
//+------------------------------------------------------------------+
void HandleObjectClickEvent(const string &sparam)
{
   if (sparam == "FullEntryButton" || sparam == "HalfEntryButton" || sparam == "ThirdEntryButton")
   {
      double entry_lot_total = lot;
      if(sparam == "HalfEntryButton") entry_lot_total = AdjustLot(lot / 2.0);
      if(sparam == "ThirdEntryButton") entry_lot_total = AdjustLot(lot / 3.0);
      
      if(tradeMode == MODE_MARKET)
      {
         SendMarketOrderWithRetry(entry_lot_total, g_sl_price, g_tp_price, currentPrice > g_sl_price);
      }
      else
      {
         if(isSplitEntryMode)
         {
            double split_lot = AdjustLot(entry_lot_total / 2.0);
            if(split_lot > 0)
            {
               SendPendingOrderWithRetry(split_lot, g_entry_price_shallow, g_sl_price, g_tp_price);
               SendPendingOrderWithRetry(split_lot, g_entry_price_deep, g_sl_price, g_tp_price);
            }
         }
         else
         {
            SendPendingOrderWithRetry(entry_lot_total, g_entry_price, g_sl_price, g_tp_price);
         }
      }
      ResetButtonState(sparam);
   }
   else if (sparam == "RedrawButton")
   {
      CreateUIElements(); 
      ResetLinePositions();
      UpdateModeUI();
      UpdateAllCalculationsAndLabels();
      ResetButtonState("RedrawButton");
   }
   else if (sparam == "CloseAllButton")
   {
      CloseAllPositions();
      ResetButtonState("CloseAllButton");
   }
}

//+------------------------------------------------------------------+
//| Create UI elements
//+------------------------------------------------------------------+
void CreateUIElements()
{
   CreateButton("UIToggleButton", "UIを隠す", 20, 10);
   CreateButton("ModeToggleButton", "モード: 成行", 20, 50);
   CreateButton("SplitEntryToggleButton", "分割: OFF", 20, 90);
   CreateButton("FullEntryButton",  "Full Entry",   20, 130);
   CreateButton("HalfEntryButton",  "1/2 Entry",    20, 170);
   CreateButton("ThirdEntryButton", "1/3 Entry",    20, 210);
   CreateButton("RedrawButton",     "手動更新", 20, 250);
   CreateButton("CloseAllButton",   "全ポジション決済", 20, 290);

   CreateLabel("RiskAmountLabel",   150, 20);
   CreateLabel("CurrentPriceLabel", 150, 40);
   CreateLabel("SLPipsLabel",       150, 60);
   CreateLabel("WinRateLabel",      150, 80);
   CreateLabel("EV_MoneyLabel",     150, 100);
   CreateLabel("EV_PipsLabel",      150, 120);
   CreateLabel("RRRatioLabel",      150, 140); // ★追加: RR比率ラベル

   if(ObjectFind(0, "SLLine") < 0)
   {
      ObjectCreate(0, "SLLine", OBJ_HLINE, 0, 0, 0);
      ObjectSetInteger(0, "SLLine", OBJPROP_SELECTABLE, true);
   }
   if(ObjectFind(0, "TPLine") < 0)
   {
      ObjectCreate(0, "TPLine", OBJ_HLINE, 0, 0, 0);
   }
}

//+------------------------------------------------------------------+
//| Delete all objects
//+------------------------------------------------------------------+
void DeleteAllObjects()
{
   string objects[] = {
      "SLLine", "TPLine", "EntryLine", "EntryLine_Shallow", "EntryLine_Deep",
      "UIToggleButton", "ModeToggleButton", "SplitEntryToggleButton", "FullEntryButton", 
      "HalfEntryButton", "ThirdEntryButton", "RedrawButton", "CloseAllButton", 
      "RiskAmountLabel", "CurrentPriceLabel", "SLPipsLabel", "WinRateLabel", 
      "EV_MoneyLabel", "EV_PipsLabel", "RRRatioLabel" // ★追加
   };
   for (int i = 0; i < ArraySize(objects); i++)
   {
      ObjectDelete(0, objects[i]);
   }
}

//+------------------------------------------------------------------+
//| Label and Button update function
//+------------------------------------------------------------------+
void UpdateLabels()
{
   string currency = AccountInfoString(ACCOUNT_CURRENCY);
   string riskText = "Risk: " + currency + " " + DoubleToString(riskAmount, 2);
   string priceText = "Price: " + DoubleToString(currentPrice, _Digits);
   
   double pips = 0;
   double point = SymbolInfoDouble(_Symbol, SYMBOL_POINT);
   double price_for_pips = (tradeMode == MODE_MARKET) ? currentPrice : g_entry_price;
   if(point > 0 && price_for_pips > 0 && g_sl_price > 0)
   {
     int digits_adjust = (_Digits == 3 || _Digits == 5) ? 10 : 1;
     pips = MathAbs(price_for_pips - g_sl_price) / (point * digits_adjust);
   }
   string pipsText = "SL: " + DoubleToString(pips, 1) + " pips";
   
   string winRateText = "WinRate: " + DoubleToString(winRate, 2) + "%";
   string evMoneyText = "EV(Money): " + currency + " " + DoubleToString(expectedValue_Money, 2);
   string evPipsText = "EV(Pips): " + DoubleToString(expectedValue_Pips, 2) + " pips";

   // ★追加: RR比率の計算と表示
   string rrText = "RR Ratio: -";
   if(tradeMode == MODE_PENDING)
   {
      double risk_dist = MathAbs(g_entry_price - g_sl_price);
      double reward_dist = MathAbs(g_tp_price - g_entry_price);
      if(risk_dist > 0)
      {
         double rr_ratio = reward_dist / risk_dist;
         rrText = "RR Ratio: 1 : " + DoubleToString(rr_ratio, 1);
      }
   }
   ObjectSetString(0, "RRRatioLabel", OBJPROP_TEXT, rrText);

   ObjectSetString(0, "RiskAmountLabel", OBJPROP_TEXT, riskText);
   ObjectSetString(0, "CurrentPriceLabel", OBJPROP_TEXT, priceText);
   ObjectSetString(0, "SLPipsLabel", OBJPROP_TEXT, pipsText);
   ObjectSetString(0, "WinRateLabel", OBJPROP_TEXT, winRateText);
   ObjectSetString(0, "EV_MoneyLabel", OBJPROP_TEXT, evMoneyText);
   ObjectSetString(0, "EV_PipsLabel", OBJPROP_TEXT, evPipsText);

   if(tradeMode == MODE_PENDING)
   {
      bool can_split = AdjustLot(lot / 2.0) >= SymbolInfoDouble(_Symbol, SYMBOL_VOLUME_MIN);
      ObjectSetInteger(0, "SplitEntryToggleButton", OBJPROP_BGCOLOR, can_split ? clrNONE : clrGray);
      ObjectSetInteger(0, "SplitEntryToggleButton", OBJPROP_COLOR, can_split ? clrBlack : clrDarkGray);
      ObjectSetString(0, "SplitEntryToggleButton", OBJPROP_TEXT, isSplitEntryMode ? "分割: ON" : "分割: OFF");
   }

   double half_lot = AdjustLot(lot / 2.0);
   double third_lot = AdjustLot(lot / 3.0);
   double price_for_direction = (tradeMode == MODE_MARKET) ? currentPrice : g_entry_price;
   string direction = (price_for_direction > g_sl_price) ? "Buy" : "Sell";

   string fullButtonText, halfButtonText, thirdButtonText;
   if(isSplitEntryMode && tradeMode == MODE_PENDING)
   {
      fullButtonText = (half_lot > 0) ? StringFormat("分割 %s %.2f x2", direction, half_lot) : "No Lot";
      halfButtonText = "N/A";
      thirdButtonText = "N/A";
   }
   else
   {
      fullButtonText = (lot > 0) ? StringFormat("%s %.2f", direction, lot) : "No Lot";
      halfButtonText = (half_lot > 0) ? StringFormat("%s %.2f (1/2)", direction, half_lot) : "No Lot";
      thirdButtonText = (third_lot > 0) ? StringFormat("%s %.2f (1/3)", direction, third_lot) : "No Lot";
   }

   ObjectSetString(0, "FullEntryButton", OBJPROP_TEXT, fullButtonText);
   ObjectSetString(0, "HalfEntryButton", OBJPROP_TEXT, halfButtonText);
   ObjectSetString(0, "ThirdEntryButton", OBJPROP_TEXT, thirdButtonText);
}

//+------------------------------------------------------------------+
//| ★変更点: RR比自動調整ロジックを組み込んだ最重要関数
//+------------------------------------------------------------------+
void CalculateTradingValues()
{
    balance = AccountInfoDouble(ACCOUNT_BALANCE);
    margin_free = AccountInfoDouble(ACCOUNT_MARGIN_FREE);
    riskAmount = balance * (RiskPercentage / 100.0);
    
    // --- Step 1: 初期ロットを計算 ---
    double sl_width_price = MathAbs(g_entry_price - g_sl_price);
    if(sl_width_price <= 0) { lot = 0.0; return; }

    double contract_size = SymbolInfoDouble(_Symbol, SYMBOL_TRADE_CONTRACT_SIZE);
    double loss_per_lot_in_quote_currency = sl_width_price * contract_size;
    
    double loss_per_lot = loss_per_lot_in_quote_currency; 
    string quote_currency = SymbolInfoString(_Symbol, SYMBOL_CURRENCY_PROFIT); 
    string account_currency = AccountInfoString(ACCOUNT_CURRENCY);    
    double conversion_rate = 1.0;

    if(quote_currency != account_currency)
    {
        string conversion_pair = quote_currency + account_currency; 
        double rate = SymbolInfoDouble(conversion_pair, SYMBOL_BID);
        if(rate <= 0) 
        {
            conversion_pair = account_currency + quote_currency;
            double inverse_rate = SymbolInfoDouble(conversion_pair, SYMBOL_ASK);
            if(inverse_rate > 0) rate = 1.0 / inverse_rate;
        }
        if(rate > 0) { loss_per_lot *= rate; conversion_rate = rate; }
        else { Print("警告: 換算レートペアが見つかりません: ", conversion_pair); lot = 0.0; return; }
    }

    double risk_based_lot = 0.0;
    if(loss_per_lot > 0) risk_based_lot = riskAmount / loss_per_lot;

    // --- Step 2: 自動調整の要否を判定 ---
    double min_lot = SymbolInfoDouble(_Symbol, SYMBOL_VOLUME_MIN);
    if(tradeMode == MODE_PENDING && risk_based_lot > 0 && risk_based_lot < min_lot)
    {
        // --- Step 3: 自動調整を実行 ---
        double target_lot = min_lot;
        double value_per_lot_per_price_unit = contract_size * conversion_rate;
        
        if(value_per_lot_per_price_unit > 0)
        {
            double required_sl_width = riskAmount / (value_per_lot_per_price_unit * target_lot);
            bool is_buy = g_tp_price > g_sl_price;
            g_entry_price = is_buy ? (g_sl_price + required_sl_width) : (g_sl_price - required_sl_width);
        }
        lot = target_lot;
    }
    else
    {
        // --- Step 4: 通常のロット計算と証拠金チェック ---
        double final_lot = AdjustLot(risk_based_lot);
        
        double margin_required_per_lot = 0;
        if(!OrderCalcMargin(ORDER_TYPE_BUY, _Symbol, 1.0, SymbolInfoDouble(_Symbol, SYMBOL_ASK), margin_required_per_lot))
        {
           margin_required_per_lot = (contract_size * SymbolInfoDouble(_Symbol, SYMBOL_ASK)) / (double)AccountInfoInteger(ACCOUNT_LEVERAGE);
        }
    
        double margin_based_lot = 0.0;
        if(margin_required_per_lot > 0) margin_based_lot = AdjustLot((margin_free * 0.98) / margin_required_per_lot);

        lot = (margin_based_lot > 0) ? MathMin(final_lot, margin_based_lot) : final_lot;
    }

    // --- Step 5: 調整後の価格をラインに反映 ---
    if(tradeMode == MODE_PENDING)
    {
        if(ObjectFind(0, "EntryLine") >= 0) ObjectMove(0, "EntryLine", 0, 0, g_entry_price);
        if(ObjectFind(0, "EntryLine_Shallow") >= 0) ObjectMove(0, "EntryLine_Shallow", 0, 0, g_entry_price_shallow);
        if(ObjectFind(0, "EntryLine_Deep") >= 0) ObjectMove(0, "EntryLine_Deep", 0, 0, g_entry_price_deep);
    }
}


//+------------------------------------------------------------------+
//| ラインの「削除」と「再描画」を行う最重要関数
//+------------------------------------------------------------------+
void UpdateModeUI()
{
    bool isMarket = (tradeMode == MODE_MARKET);
    ObjectSetString(0, "ModeToggleButton", OBJPROP_TEXT, isMarket ? "モード: 成行" : "モード: 指値");
    
    if(ObjectFind(0, "EntryLine") >= 0)         ObjectDelete(0, "EntryLine");
    if(ObjectFind(0, "EntryLine_Shallow") >= 0) ObjectDelete(0, "EntryLine_Shallow");
    if(ObjectFind(0, "EntryLine_Deep") >= 0)    ObjectDelete(0, "EntryLine_Deep");

    ObjectSetInteger(0, "SLLine", OBJPROP_HIDDEN, !isUIVisible);
    ObjectSetInteger(0, "TPLine", OBJPROP_HIDDEN, !isUIVisible);
    ObjectSetInteger(0, "SplitEntryToggleButton", OBJPROP_HIDDEN, isMarket || !isUIVisible);

    if(!isMarket && isUIVisible)
    {
        if(isSplitEntryMode)
        {
            ObjectCreate(0, "EntryLine_Shallow", OBJ_HLINE, 0, 0, 0);
            ObjectSetInteger(0, "EntryLine_Shallow", OBJPROP_COLOR, clrLimeGreen);
            ObjectSetInteger(0, "EntryLine_Shallow", OBJPROP_STYLE, STYLE_DOT);
            ObjectSetInteger(0, "EntryLine_Shallow", OBJPROP_SELECTABLE, false);

            ObjectCreate(0, "EntryLine_Deep", OBJ_HLINE, 0, 0, 0);
            ObjectSetInteger(0, "EntryLine_Deep", OBJPROP_COLOR, clrDarkGreen);
            ObjectSetInteger(0, "EntryLine_Deep", OBJPROP_STYLE, STYLE_DOT);
            ObjectSetInteger(0, "EntryLine_Deep", OBJPROP_SELECTABLE, false);
        }
        else
        {
            ObjectCreate(0, "EntryLine", OBJ_HLINE, 0, 0, 0);
            ObjectSetInteger(0, "EntryLine", OBJPROP_COLOR, clrGold);
            ObjectSetInteger(0, "EntryLine", OBJPROP_STYLE, STYLE_DOT);
            ObjectSetInteger(0, "EntryLine", OBJPROP_SELECTABLE, false);
        }
    }

    ObjectSetInteger(0, "SLLine", OBJPROP_COLOR, isMarket ? clrRed : clrOrangeRed);
    ObjectSetInteger(0, "TPLine", OBJPROP_COLOR, isMarket ? clrBlue : clrDeepSkyBlue);
    ObjectSetInteger(0, "TPLine", OBJPROP_SELECTABLE, !isMarket);
    ObjectSetInteger(0, "TPLine", OBJPROP_SELECTED, !isMarket);
    ObjectSetInteger(0, "SLLine", OBJPROP_SELECTED, true);
}


// （...以降の関数は変更なし...）


//+------------------------------------------------------------------+
//| Adjusts lot size based on broker's rules
//+------------------------------------------------------------------+
double AdjustLot(double raw_lot)
{
   double volume_step = SymbolInfoDouble(_Symbol, SYMBOL_VOLUME_STEP);
   double min_volume = SymbolInfoDouble(_Symbol, SYMBOL_VOLUME_MIN);
   double adjusted_lot = raw_lot;
   
   if(volume_step > 0) adjusted_lot = floor(raw_lot / volume_step) * volume_step;
   if(adjusted_lot < min_volume) adjusted_lot = 0.0;
   
   return adjusted_lot;
}

//+------------------------------------------------------------------+
//| Reset button state
//+------------------------------------------------------------------+
void ResetButtonState(const string &buttonName)
{
   ObjectSetInteger(0, buttonName, OBJPROP_STATE, false);
}

//+------------------------------------------------------------------+
//| Order sending function (Market)
//+------------------------------------------------------------------+
bool SendMarketOrderWithRetry(double volume, double stopLoss, double takeProfit, bool isBuy)
{
   if(volume <= 0) { Alert("ロットサイズが0です。注文はキャンセルされました。"); return false; }
   CTrade trade;
   trade.SetExpertMagicNumber(MagicNumber);
   trade.SetDeviationInPoints((ulong)slippage);
   trade.SetTypeFillingBySymbol(_Symbol);

   bool res=false;
   if(isBuy) res=trade.Buy(volume, _Symbol, 0, stopLoss, takeProfit, "Buy");
   else res=trade.Sell(volume, _Symbol, 0, stopLoss, takeProfit, "Sell");
   
   if(!res) Print("Market OrderSend failed. Error: ", trade.ResultRetcode(), " - ", trade.ResultRetcodeDescription());
   return res;
}

//+------------------------------------------------------------------+
//| Order sending function (Pending)
//+------------------------------------------------------------------+
bool SendPendingOrderWithRetry(double volume, double entryPrice, double stopLoss, double takeProfit)
{
   if(volume <= 0) { Alert("ロットサイズが0です。注文はキャンセルされました。"); return false; }
   
   bool is_long = (entryPrice > stopLoss);
   ENUM_ORDER_TYPE order_type;
   double current_ask = SymbolInfoDouble(_Symbol, SYMBOL_ASK);
   double current_bid = SymbolInfoDouble(_Symbol, SYMBOL_BID);
   
   if(is_long) order_type = (entryPrice < current_ask) ? ORDER_TYPE_BUY_LIMIT : ORDER_TYPE_BUY_STOP;
   else order_type = (entryPrice > current_bid) ? ORDER_TYPE_SELL_LIMIT : ORDER_TYPE_SELL_STOP;
   
   CTrade trade;
   trade.SetExpertMagicNumber(MagicNumber);
   
   bool res = trade.OrderOpen(_Symbol, order_type, volume, 0, entryPrice, stopLoss, takeProfit);
   
   if(!res) Print("Pending OrderSend failed. Error: ", trade.ResultRetcode(), " - ", trade.ResultRetcodeDescription());
   return res;
}

//+------------------------------------------------------------------+
//| Close all positions function
//+------------------------------------------------------------------+
void CloseAllPositions()
{
   CTrade trade;
   trade.SetExpertMagicNumber(MagicNumber);
   for(int i = PositionsTotal() - 1; i >= 0; i--)
     {
      ulong ticket = PositionGetTicket(i);
      if(PositionSelectByTicket(ticket))
      {
         if(PositionGetInteger(POSITION_MAGIC) == MagicNumber && PositionGetString(POSITION_SYMBOL) == _Symbol)
         {
            trade.PositionClose(ticket, (ulong)slippage);
         }
      }
     }
}

//+------------------------------------------------------------------+
//| Button creation function
//+------------------------------------------------------------------+
void CreateButton(string name, string text, int xDistance, int yDistance)
{
   if(ObjectFind(0, name) < 0) ObjectCreate(0, name, OBJ_BUTTON, 0, 0, 0);
   ObjectSetString(0, name, OBJPROP_TEXT, text);
   ObjectSetInteger(0, name, OBJPROP_XSIZE, 120);
   ObjectSetInteger(0, name, OBJPROP_YSIZE, 30);
   ObjectSetInteger(0, name, OBJPROP_CORNER, ANCHOR_LEFT_UPPER);
   ObjectSetInteger(0, name, OBJPROP_XDISTANCE, xDistance);
   ObjectSetInteger(0, name, OBJPROP_YDISTANCE, yDistance);
}

//+------------------------------------------------------------------+
//| Label creation function
//+------------------------------------------------------------------+
void CreateLabel(string name, int xDistance, int yDistance)
{
   if(ObjectFind(0, name) < 0) ObjectCreate(0, name, OBJ_LABEL, 0, 0, 0);
   ObjectSetInteger(0, name, OBJPROP_CORNER, ANCHOR_LEFT_UPPER);
   ObjectSetInteger(0, name, OBJPROP_XDISTANCE, xDistance);
   ObjectSetInteger(0, name, OBJPROP_YDISTANCE, yDistance);
   ObjectSetInteger(0, name, OBJPROP_FONTSIZE, 10);
   ObjectSetInteger(0, name, OBJPROP_SELECTABLE, false);
}


//+------------------------------------------------------------------+
//| 勝率を計算する関数
//+------------------------------------------------------------------+
double CalculateWinRate()
{
   int totalTrades = 0;
   int winningTrades = 0;
   
   HistorySelect(0, TimeCurrent());
   uint totalDeals = HistoryDealsTotal();

   for(uint i=0; i<totalDeals; i++)
   {
      ulong ticket=HistoryDealGetTicket(i);
      if(ticket>0 && HistoryDealGetInteger(ticket,DEAL_MAGIC)==MagicNumber && 
         HistoryDealGetString(ticket,DEAL_SYMBOL)==_Symbol &&
         (ENUM_DEAL_ENTRY)HistoryDealGetInteger(ticket, DEAL_ENTRY) == DEAL_ENTRY_OUT)
      {
         totalTrades++;
         if(HistoryDealGetDouble(ticket,DEAL_PROFIT) > 0) winningTrades++;
      }
   }

   if(totalTrades > 0) return((double)winningTrades / totalTrades) * 100.0;
   return 0.0;
}

//+------------------------------------------------------------------+
//| 期待値を計算する関数
//+------------------------------------------------------------------+
void CalculateExpectedValues()
{
   int winningTrades = 0, losingTrades = 0;
   double totalProfit_Money = 0, totalLoss_Money = 0;
   double totalProfit_Pips = 0, totalLoss_Pips = 0;

   HistorySelect(0, TimeCurrent());
   uint totalDeals = HistoryDealsTotal();

   double point = SymbolInfoDouble(_Symbol, SYMBOL_POINT);
   double pip_adjuster = (_Digits == 3 || _Digits == 5) ? 10.0 : 1.0;

   for(uint i = 0; i < totalDeals; i++)
   {
      ulong ticket = HistoryDealGetTicket(i);
      if(ticket > 0 && HistoryDealGetInteger(ticket, DEAL_MAGIC) == MagicNumber && 
         HistoryDealGetString(ticket, DEAL_SYMBOL) == _Symbol &&
         (ENUM_DEAL_ENTRY)HistoryDealGetInteger(ticket, DEAL_ENTRY) == DEAL_ENTRY_OUT)
      {
         double profit = HistoryDealGetDouble(ticket, DEAL_PROFIT);
         
         if(profit > 0) { winningTrades++; totalProfit_Money += profit; }
         else if(profit < 0) { losingTrades++; totalLoss_Money += profit; }
         
         double tick_value = SymbolInfoDouble(_Symbol, SYMBOL_TRADE_TICK_VALUE);
         double volume = HistoryDealGetDouble(ticket, DEAL_VOLUME);
         
         if(tick_value > 0 && volume > 0 && point > 0)
         {
            double points_value = profit / (tick_value * volume);
            double pips_value = points_value / pip_adjuster;
            if(pips_value > 0) totalProfit_Pips += pips_value;
            else totalLoss_Pips += pips_value;
         }
      }
   }
   
   int totalTrades = winningTrades + losingTrades;
   if(totalTrades > 0)
   {
      double winRate_decimal = (double)winningTrades / totalTrades;
      double avgProfit_Money = (winningTrades > 0) ? totalProfit_Money / winningTrades : 0;
      double avgLoss_Money = (losingTrades > 0) ? MathAbs(totalLoss_Money / losingTrades) : 0;
      double avgProfit_Pips = (winningTrades > 0) ? totalProfit_Pips / winningTrades : 0;
      double avgLoss_Pips = (losingTrades > 0) ? MathAbs(totalLoss_Pips / losingTrades) : 0;
      
      expectedValue_Money = (winRate_decimal * avgProfit_Money) - ((1 - winRate_decimal) * avgLoss_Money);
      expectedValue_Pips = (winRate_decimal * avgProfit_Pips) - ((1 - winRate_decimal) * avgLoss_Pips);
   }
   else
   {
      expectedValue_Money = 0;
      expectedValue_Pips = 0;
   }
}
