# cTrader WebView Development: Auto Lot Calculator

## Overview
This project successfully reverse-engineered the internal cTrader WebView protocol to build a fully functional **Auto Lot Calculator (App v4.1)**. The app calculates optimal trade volume based on risk percentage and stop loss, fetches real-time prices, and places orders directly from the WebView.

## 1. Protocol Reverse Engineering
We discovered that the platform uses a "Tunneling" (Wrapper) architecture for API communication.

### 1.1 The Tunnel Structure
Standard request IDs (e.g., `2102` Auth) failed when sent directly.
We found that all API commands must be wrapped inside a specific **2100 (SendServerDataReq)** envelope.

- **Request ID:** `2100` (Wrapper)
- **Payload:**
  - `payloadType`: The actual command ID (e.g., `2102`, `601`, `143`)
  - `clientMsgId`: Unique ID for tracking response
  - `payload`: The actual command data (JSON object or string)

### 1.2 Key Command IDs Discovered
| ID | Name | Purpose | Notes |
|---|---|---|---|
| **2000** | Heartbeat | Initial Handshake | Reply with 2001 (ACK) |
| **2100** | Wrapper (Req) | Send Command | Wraps all other commands |
| **2101** | Wrapper (Res) | Receive Response | Contains `data` (string) or `payload` (obj) |
| **2102** | Wrapper (Event) | Receive Event | Push notifications (Quotes, Order updates) |
| **175** | GetAccount | Fetch Account Info | Returns Balance, Currency, ID |
| **841** | GetSymbolList | Fetch All Symbols | Returns lightweight list (Id, Name) |
| **163** | GetSymbol | Fetch Symbol Details | Returns StepVolume, MinVolume, Digits |
| **601** | SubscribeSpot | Stream Prices | `subscribeToSpotTimestamp: true` required |
| **143** | CreateOrder | Place Trade | Quantity must be normalized (Units) |

## 2. App Implementation Stages

### App v1.0 (Proof of Concept)
- Successfully established the Tunnel (`2100` -> `2001`).
- Fetched Account Balance and Symbol List.
- **Result:** Connection Verified.

### App v2.0 (Core Logic)
- Implemented Risk Calculation UI.
- Added Trading Button (`143`).
- **Issue:** Order rejected due to precision error ("Volume ...333.33 must be multiple of 1000").
- **Lesson:** Volume must be normalized to Steps.

### App v3.0 (Precision Fix)
- Added `Math.floor(vol / step) * step` logic.
- Improved JSON parsing (handled PascalCase keys).
- **Issue:** Order rejected due to "Not Enough Money".
- **Root Cause:** Sent `7,600,000` assuming cents, but API expected **Units**. The server interpreted this as 76 Lots!
- **Lesson:** `1 Lot = 100,000 Units`. Send raw units (e.g., `76000`).

### App v4.0 (Unit Fix)
- Corrected Volume Calculation to output Units.
- Polished UI to Dark Mode.
- **Issue:** Real-time Price ("---") stopped updating.
- **Root Cause:** Disabled `subscribeToSpotTimestamp` in cleanup.

### App v4.1 (Final Stable Version)
- Re-enabled `subscribeToSpotTimestamp`.
- Added loose ID matching for robust event handling.
- **Result:** Fully functional Calculator & Trader.

## 3. Usage Guide
1. **Select Symbol**: Choose a pair (e.g., USDJPY).
2. **Input Risk**: Set Risk Percentage (e.g., 1.0%) and Stop Loss (e.g., 20 Pips).
3. **Verify**: Check the calculated Volume (Lots).
4. **Trade**: Click "PLACE ORDER" to send a Market Buy command.
5. **Confirm**: A success modal will confirm the trade execution.

## 4. Future Enhancements
- **Short Selling**: Add logic for SELL orders.
- **Pending Orders**: Support Limit/Stop orders.
- **Dynamic Pip Value**: Fetch precise conversion rates for non-USD pairs.
