# WebView Plugin Implementation Plan for AutoLotCalculator (Mobile-First)

## Goal
Convert the existing cBot to a **WebView Plugin** that works on **cTrader Mobile**.
The core requirement is **"Dynamic SL Line Adjustment"** (dragging the line to set SL distance).

## Architecture: Hybrid (C# + HTML/JS)

### 1. Chart Interaction (The "Dynamic" Part)
- **Mechanism**: Use standard cAlgo `Chart.DrawHorizontalLine` with `IsInteractive = true`.
- **Mobile Behavior**: 
    - Users **CAN** drag these lines on cTrader Mobile (tap & hold -> drag).
    - **Crucial**: The WebView Plugin does *not* automatically know when a line is moved.
    - **Solution**: The C# backend handles the `Chart.ObjectsUpdated` event.
        - When proper line (SL/TP) is moved by user -> C# detects change -> C# calculates new Lot/Risk -> C# sends `UPDATE_STATE` message to WebView.
        - This ensures that dragging the line on the chart instantly updates the numbers in the WebView panel.

### 2. WebView Operations (The "Control" Part)
- **Display**: Shows calculated Lots, Risk Amount, Win Rate, EVs.
- **Controls**:
    - **Fine-Tune Buttons**: `[+]` `[-]` buttons to move the SL line by 1 pip (since dragging on mobile can be imprecise).
    - **Trade Buttons**: [Buy] [Sell] [Close All] [Split Limit].
    - **Risk Slider**: Adjust Risk %.

## Technical Implementation Steps

### Step 1: HTML/JS Frontend (Embedded in C#)
- Create a responsive HTML layout (dark mode by default).
- JS Logic:
    - Listen for `UPDATE_STATE` triggers from C# to update displayed numbers.
    - Send `ADJUST_SL` commands (+/- pips) to C#.
    - Send `EXECUTE_ORDER` commands.

### Step 2: C# Backend Logic
- **Startup**: 
    - specific initialization for WebView.
    - Draw initial lines (`IsInteractive = true`).
- **Event Handling**:
    - `OnChartObjectsUpdated`: The heartbeat of this plugin.
        - Checks if SL/TP line Y-coordinate changed.
        - Re-runs calculations.
        - Pushes new data to WebView via `WebView.PostMessage`.
    - `OnWebMessageReceived`:
        - Handles button clicks from the WebView (Buy/Sell/Adjust).

## Verification Strategy
1.  **Deploy to Demo Account**.
2.  **Mobile Test**: 
    - Open cTrader Mobile.
    - Add cBot/Plugin.
    - **Test 1**: Drag Red Line (SL) on chart. -> Check if WebView numbers update.
    - **Test 2**: Tap "Buy" in WebView. -> Check if order is placed correctly.
