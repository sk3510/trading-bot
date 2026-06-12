// ╔══════════════════════════════════════════════════════════════════╗
// ║              RejBlockVwapMNQ — LIVE STRATEGY                    ║
// ║                                                                  ║
// ║  Filters active:                                                 ║
// ║    1. Lunch filter: skip 12:00–13:30 ET                         ║
// ║    2. News blackout: hardcoded NFP/FOMC/CPI ±30 min             ║
// ║    3. Session window: entries only 09:30–15:00 ET               ║
// ║    4. One trade per day                                          ║
// ║    5. Consecutive loss halt                                      ║
// ║    6. Drawdown guard ($1,900)                                    ║
// ║    7. Session range filter: skip if prior RTH range < 150 or    ║
// ║       > 700 pts                                                  ║
// ║    8. Daily profit cap: stop trading if today profit >= $1,200   ║
// ║    9. Rejection expiry: discard blocks older than 30 bars        ║
// ║                                                                  ║
// ║  Confirmed baseline (1-min bars, slippage=2, commission on):    ║
// ║    PF 1.77 → Jun 2025–Apr 2026 (all filters active incl expiry) ║
// ║    PF 1.77 → Jan 2025–Apr 2026 (all filters active incl expiry) ║
// ║                                                                  ║
// ║  Optimal params: WickRatio=2, SlBuffer=3, TrailActivation=120,  ║
// ║                  TrailOffset=25, EmaPeriod=40, RejExpiryBars=30  ║
// ║  Session range: MinSessionRange=150, MaxSessionRange=700         ║
// ║                                                                  ║
// ║  AUDIT FIXES (2026-05):                                          ║
// ║    FIX 1 — ManagePosition() uses Position.AveragePrice for all  ║
// ║             post-fill logic (partial TP, trail, stop exit PnL)  ║
// ║    FIX 2 — tradedToday moved to OnExecutionUpdate on fill        ║
// ║    FIX 3 — partialPnl tracked separately; Discord shows total    ║
// ║             trade PnL (partial leg + remaining leg combined)     ║
// ║    FIX 4 — lowWaterMark initialized to entryPrice at short entry ║
// ╚══════════════════════════════════════════════════════════════════╝
//
// ════════════════════ V2 CHANGES (SIM101 FIRST) ════════════════════
//  This file is RejBlockVwapMNQ with three structural fixes applied.
//  Diff it against the live file; do NOT push to the eval until it has
//  soaked in Sim101 and the checklist below passes.
//
//  [FIX 1] Hybrid exchange stop (v3 — combines v1 loss control + runner room).
//      - PHASE 1 (pre-partial): a TIGHT real exchange stop sits at the
//        structural level and fires intrabar — caps initial losers tight,
//        which is what kept v1's drawdown low.
//      - PHASE 2 (post-partial): the exchange stop WIDENS to a catastrophe
//        floor (CatastropheStopMult x risk) and the normal exit becomes
//        BAR-CLOSE at BE/trail — so the proven runner breathes and the trail
//        can finally activate (the edge). v1 killed the trail; v2 widened the
//        loss; this keeps both good halves.
//      - TRADEOFF to test on the FULL window: tighter phase-1 stops trade loss
//        SIZE for loss FREQUENCY (more wicks stop you out before 1R). Net PF
//        must be confirmed vs the original on Jan 2025–Apr 2026.
//      - Exit tags: "Stop loss" = phase-1 tight stop or catastrophe (exchange);
//        "SL" = phase-2 bar-close exit (manual). PnL/consec/Discord finalize
//        in OnExecutionUpdate from real fills.
//
//  [FIX 2] Trailing drawdown (was measured from static startingBalance).
//      - trailingDdFloor = maxEodEquity - maxDrawdown, updated at 09:30,
//        locked at startingBalance. CheckPropFirmRules halts against it.
//      - Streak halt is now $-budget based (40% of DD room at streak start),
//        not the count of 10 (10 x $400 = $4,000 >> $1,900 floor).
//
//  [FIX 3] Skip-instead-of-compress.
//      - When structural risk > MaxLossDollars, the trade is SKIPPED.
//        The old "sl = c - cap/(qty*2)" stop-compression is REMOVED.
//
//  VERIFY IN SIM BEFORE LIVE:
//    1. Partial out 1/3 -> the resting exchange stop auto-reduces to 2.
//    2. Kill NT8 / pull network mid-trade -> stop is STILL resting at broker.
//    3. Repeated SetStopLoss modifies ONE order, doesn't stack duplicates.
//    4. OnExecutionUpdate PnL matches NT's own realized PnL per trade.
//    5. Confirm Lucid's exact trailing rule: EOD vs intraday, and lock point.
//    6. Assumes managed orders (EnterLong/ExitLong) — confirmed in live file.
// ════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript.Strategies;
using NinjaTrader.NinjaScript.Indicators;

namespace NinjaTrader.NinjaScript.Strategies
{
    public class RejBlockVwapMNQLab : Strategy
    {
        // ─── PUBLIC PROPERTIES FOR OPTIMIZER ──────────────
        [NinjaScriptProperty]
        public double WickRatio
        {
            get { return wickRatio; }
            set { wickRatio = Math.Max(1.0, value); }
        }

        [NinjaScriptProperty]
        public double SlBuffer
        {
            get { return slBuffer; }
            set { slBuffer = Math.Max(1, value); }
        }

        [NinjaScriptProperty]
        public double TrailActivation
        {
            get { return trailActivation; }
            set { trailActivation = Math.Max(10, value); }
        }

        [NinjaScriptProperty]
        public double TrailOffset
        {
            get { return trailOffset; }
            set { trailOffset = Math.Max(5, value); }
        }

        [NinjaScriptProperty]
        public double MaxLossDollars
        {
            get { return maxLossDollars; }
            set { maxLossDollars = Math.Max(100, value); }
        }

        [NinjaScriptProperty]
        public int EmaPeriod
        {
            get { return emaPeriod; }
            set { emaPeriod = Math.Max(5, value); }
        }

        [NinjaScriptProperty]
        public int MaxConsecLosses
        {
            get { return maxConsecLosses; }
            set { maxConsecLosses = Math.Max(1, value); }
        }

        [NinjaScriptProperty]
        public bool SkipToday
        {
            get { return skipToday; }
            set { skipToday = value; }
        }

        [NinjaScriptProperty]
        public int NewsBufferMinutes
        {
            get { return newsBufferMinutes; }
            set { newsBufferMinutes = Math.Max(0, value); }
        }

        // ─── SESSION RANGE FILTER ─────────────────────────
        [NinjaScriptProperty]
        public double MinSessionRange
        {
            get { return minSessionRange; }
            set { minSessionRange = Math.Max(0, value); }
        }

        [NinjaScriptProperty]
        public double MaxSessionRange
        {
            get { return maxSessionRange; }
            set { maxSessionRange = Math.Max(0, value); }
        }

        // ─── REJECTION EXPIRY ─────────────────────────────
        [NinjaScriptProperty]
        public int RejExpiryBars
        {
            get { return rejExpiryBars; }
            set { rejExpiryBars = Math.Max(1, value); }
        }

        // ─── FIX 1 — CATASTROPHE STOP WIDTH (x structural risk) ──
        [NinjaScriptProperty]
        public double CatastropheStopMult
        {
            get { return catastropheMult; }
            set { catastropheMult = Math.Max(1.0, value); }
        }

        // ─── ENTRY TOLERANCE — max points from the rejection block to enter ──
        // Replaces the old 0.1% (≈25pt @ 25k) tolerance with an absolute points
        // value. Wider = looser entries = wider stops = the wide-entry losses the
        // postmortem flagged. Sweep 2→10 to find where loss size drops without
        // starving trade count.
        [NinjaScriptProperty]
        public double RetraceTolerancePts
        {
            get { return retraceTolerancePts; }
            set { retraceTolerancePts = Math.Max(0.0, value); }
        }

        // ─── ACCOUNT SIZE — eval account size; equity source of truth ──
        // Hardcoded eval size (NOT read from the platform account) so backtest
        // and live compute identical equity. See AccountEquity().
        [NinjaScriptProperty]
        public double AccountSize
        {
            get { return accountSize; }
            set { accountSize = Math.Max(1, value); }
        }

        // ─── PARAMETERS ───────────────────────────────────
        private double wickRatio         = 2.0;
        private double slBuffer          = 3;
        private double trailActivation   = 120;
        private double trailOffset       = 25;
        private double maxLossDollars    = 400;
        private int    emaPeriod         = 40;
        private int    maxConsecLosses   = 10;
        private int    contracts         = 3;
        private double profitTarget      = 3000;   // Lucid eval target
        private double maxDrawdown       = 1900;   // Lucid drawdown limit
        private double dailyProfitCap    = 1200;   // stop trading once today's profit >= this
        private bool   skipToday         = false;
        private int    newsBufferMinutes = 30;
        private double minSessionRange   = 150;
        private double maxSessionRange   = 700;
        private int    rejExpiryBars     = 30;
        private double catastropheMult   = 3.0;   // catastrophe stop = this x structural risk
        private double retraceTolerancePts = 25;  // entry tolerance in POINTS (old 0.1% ≈ 25pt @ 25k)
        private double accountSize       = 50000; // eval account size — equity baseline (see AccountEquity)

        // ─── INDICATORS ───────────────────────────────────
        private EMA ema;

        // ─── SESSION STATE ────────────────────────────────
        private bool   tradedToday    = false;
        private bool   evalPassed     = false;
        private bool   dailyHalted    = false;
        private bool   lastBullishRej = false;
        private bool   lastBearishRej = false;
        private double lastRejHigh    = 0;
        private double lastRejLow     = 0;
        private int    lastRejBar     = 0;

        // ─── CONSECUTIVE LOSS TRACKING ────────────────────
        private int    consecLosses   = 0;
        private bool   consecHalted   = false;

        // ─── FIX 2 — TRAILING DRAWDOWN (LucidFlex EOD trailing) ──
        private double maxEodEquity      = 0;   // highest end-of-day equity seen
        private double trailingDdFloor   = 0;   // = maxEodEquity - maxDrawdown (locked at startBalance)
        private double consecLossDollars = 0;   // running $ of the current losing streak
        private double streakStartRoom   = 0;   // DD room ($) when the current streak began
        private bool   terminalHalt      = false;   // DD-floor breach latch — PERSISTS across sessions (never reset)

        // ─── FIX 1 — TRADE ACCOUNTING (real-fill PnL) ──
        private double tradeEntryPrice   = 0;   // real blended entry fill
        private double tradeRealizedPnl  = 0;   // accumulates across partial + remainder exits

        // ─── NEWS FILTER ──────────────────────────────────
        private bool           newsHaltActive = false;
        private List<DateTime> newsEvents     = new List<DateTime>();

        // ─── ACCOUNT TRACKING ─────────────────────────────
        private double startingBalance = 0;
        private double dayStartEquity  = 0;
        private double totalProfit     = 0;
        private double todayProfit     = 0;

        // ─── VWAP ─────────────────────────────────────────
        private double vwapCumTPVol = 0;
        private double vwapCumVol   = 0;

        // ─── POSITION STATE ───────────────────────────────
        private double stopPrice     = 0;
        private double entryPrice    = 0;   // estimated at order submission — used for stop sizing only
        private double highWaterMark = 0;
        private double lowWaterMark  = 0;
        private bool   trailActive   = false;
        private bool   partialTaken  = false;
        private string tradeSide     = "";
        private double initialRisk   = 0;

        // FIX 3 — tracks realized PnL from partial TP leg
        // so final exit Discord alert can show total trade PnL
        private double partialPnl    = 0;

        // ─── SESSION RANGE TRACKING ───────────────────────
        private double sessionHigh     = 0;
        private double sessionLow      = 0;
        private double prevSessionHigh = 0;
        private double prevSessionLow  = 0;
        private bool   sessionRangeOk  = true;

        // ─── DISCORD ──────────────────────────────────────
        private string discordBotToken  = "";
        private string discordChannelId = "1490716489211449425";

        // ─── EQUITY SOURCE OF TRUTH ───────────────────────
        // Internal equity: identical in Strategy Analyzer and live. Replaces
        // Account.Get(AccountItem.CashValue), which in backtest returns the SA
        // account value frozen (the maxEod=$100,000 / floor-never-ratchets bug).
        // Realized-only is correct under LucidFlex EOD trailing: the strategy is
        // flat by 15:00, so realized cum profit IS the end-of-day balance Lucid
        // trails. CumProfit moves only when a trade CLOSES, so the intraday
        // CheckPropFirmRules halt automatically behaves as an EOD check (it can't
        // false-trip on open-trade drawdown Lucid doesn't count).
        private double AccountEquity()
        {
            double cumRealized = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
            return startingBalance + cumRealized;
        }

        private void LoadDiscordToken()
        {
            try
            {
                string tokenPath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "NinjaTrader 8", "discord_token.txt");
                if (System.IO.File.Exists(tokenPath))
                {
                    discordBotToken = System.IO.File.ReadAllText(tokenPath).Trim();
                    Print("Discord token loaded OK");
                }
                else
                    Print("discord_token.txt not found — alerts disabled");
            }
            catch (Exception ex) { Print("Discord token load failed: " + ex.Message); }
        }

        private void SendDiscordAlert(string message)
        {
            if (State != State.Realtime) return;  // NEVER remove — blocks optimizer if missing
            if (string.IsNullOrEmpty(discordBotToken)) return;
            try
            {
                using (System.Net.WebClient client = new System.Net.WebClient())
                {
                    client.Headers[System.Net.HttpRequestHeader.Authorization] = "Bot " + discordBotToken;
                    client.Headers[System.Net.HttpRequestHeader.ContentType]   = "application/json";
                    string json = "{\"content\": \"" + message.Replace("\"", "'") + "\"}";
                    string url  = "https://discord.com/api/v10/channels/" + discordChannelId + "/messages";
                    client.UploadString(url, "POST", json);
                }
            }
            catch (Exception ex) { Print("Discord alert failed: " + ex.Message); }
        }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                         = "RejBlockVwapMNQLab";
                Description                  = "Rejection Block + VWAP + EMA + Session Range Filter on MNQ";
                Calculate                    = Calculate.OnBarClose;
                EntriesPerDirection          = 1;
                EntryHandling                = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds    = 900;
                BarsRequiredToTrade          = 60;
                TraceOrders                  = true;

                WickRatio         = 2.0;
                SlBuffer          = 3;
                TrailActivation   = 120;
                TrailOffset       = 25;
                MaxLossDollars    = 400;
                EmaPeriod         = 40;
                MaxConsecLosses   = 10;
                SkipToday         = false;
                NewsBufferMinutes = 30;
                MinSessionRange   = 150;
                MaxSessionRange   = 700;
                RejExpiryBars     = 30;
                CatastropheStopMult = 3.0;
                RetraceTolerancePts = 25;   // matches the old 0.1% behavior at ~25k; sweep down 2-10
                AccountSize       = 50000;  // Lucid eval size — equity baseline
            }
            else if (State == State.DataLoaded)
            {
                ema             = EMA(Close, emaPeriod);
                startingBalance = 0;
                dayStartEquity  = 0;
                LoadDiscordToken();
                LoadNewsEvents();
            }
        }

        // ─── NEWS EVENTS ──────────────────────────────────
        private void LoadNewsEvents()
        {
            newsEvents.Clear();
            LoadHardcodedNews();
            Print("Total news events loaded: " + newsEvents.Count);
        }

        private void LoadHardcodedNews()
        {
            // NFP — first Friday of month, 8:30 AM ET
            int[] nfpYears   = { 2025, 2025, 2025, 2025, 2025, 2025, 2025, 2025, 2025, 2025, 2025, 2025,
                                  2026, 2026, 2026, 2026, 2026, 2026 };
            int[] nfpMonths  = { 1,  2,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 1,  2,  3,  4,  5,  6 };
            int[] nfpDays    = { 10,  7,  7,  4,  2,  6,  4,  1,  5,  3,  7,  5, 9,  6,  6,  3,  2,  5 };
            for (int i = 0; i < nfpYears.Length; i++)
                newsEvents.Add(new DateTime(nfpYears[i], nfpMonths[i], nfpDays[i], 8, 30, 0));

            // FOMC — 2:00 PM ET
            int[] fomcYears  = { 2025, 2025, 2025, 2025, 2025, 2025, 2025, 2025,
                                  2026, 2026, 2026, 2026, 2026, 2026, 2026, 2026 };
            int[] fomcMonths = { 1,  3,  5,  6,  7,  9, 11, 12, 1,  3,  4,  6,  7,  9, 10, 12 };
            int[] fomcDays   = { 29, 19,  7, 18, 30, 17,  7, 10, 28, 18, 29, 17, 29, 16, 28,  9 };
            for (int i = 0; i < fomcYears.Length; i++)
                newsEvents.Add(new DateTime(fomcYears[i], fomcMonths[i], fomcDays[i], 14, 0, 0));

            // CPI — 8:30 AM ET
            int[] cpiYears   = { 2025, 2025, 2025, 2025, 2025, 2025, 2025, 2025, 2025, 2025, 2025, 2025,
                                  2026, 2026, 2026, 2026, 2026, 2026 };
            int[] cpiMonths  = { 1,  2,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 1,  2,  3,  4,  5,  6 };
            int[] cpiDays    = { 15, 12, 12, 10, 13, 11, 15, 12, 10, 15, 13, 10, 15, 11, 11, 14, 13, 11 };
            for (int i = 0; i < cpiYears.Length; i++)
                newsEvents.Add(new DateTime(cpiYears[i], cpiMonths[i], cpiDays[i], 8, 30, 0));

            Print("Hardcoded news loaded: " + newsEvents.Count + " events");
        }

        private bool IsNewsBlackout(DateTime barTime)
        {
            foreach (var newsTime in newsEvents)
            {
                if (newsTime.Date != barTime.Date) continue;
                double minutesDiff = Math.Abs((barTime - newsTime).TotalMinutes);
                if (minutesDiff <= newsBufferMinutes)
                {
                    if (!newsHaltActive)
                    {
                        newsHaltActive = true;
                        Print(barTime + " NEWS BLACKOUT — within " + newsBufferMinutes
                            + " min of event at " + newsTime.ToString("HH:mm"));
                    }
                    return true;
                }
            }
            if (newsHaltActive)
            {
                newsHaltActive = false;
                Print(barTime + " NEWS BLACKOUT lifted");
            }
            return false;
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < BarsRequiredToTrade) return;
            if (CurrentBar < emaPeriod) return;
            if (evalPassed) return;

            // ── Capture starting balance on first bar ──────
            if (startingBalance == 0)
            {
                startingBalance = accountSize;                  // hardcoded eval size — backtest/live identical
                dayStartEquity  = AccountEquity();
                Print(Time[0] + " Starting balance: $" + startingBalance.ToString("F2"));
                SendDiscordAlert("STARTED | Balance: $" + startingBalance.ToString("F2"));
            }

            int hour   = Time[0].Hour;
            int minute = Time[0].Minute;

            // ── Track running session high/low (RTH only) ─
            if (hour >= 9 && hour < 16)
            {
                if (sessionHigh == 0 || High[0] > sessionHigh) sessionHigh = High[0];
                if (sessionLow  == 0 || Low[0]  < sessionLow)  sessionLow  = Low[0];
            }

            // ── Reset at session open ──────────────────────
            if (hour == 9 && minute == 30)
            {
                // Evaluate prior session range before resetting
                if (sessionHigh > 0)
                {
                    prevSessionHigh = sessionHigh;
                    prevSessionLow  = sessionLow;
                    double priorRange = prevSessionHigh - prevSessionLow;
                    bool tooQuiet    = minSessionRange > 0    && priorRange < minSessionRange;
                    bool tooChaotic  = maxSessionRange < 9999 && priorRange > maxSessionRange;
                    sessionRangeOk   = !tooQuiet && !tooChaotic;
                    string rangeStatus = sessionRangeOk ? "TRADE TODAY ✅" : "SKIP TODAY ⛔";
                    Print(Time[0] + " PRIOR RANGE: " + priorRange.ToString("F1") + " pts | " + rangeStatus
                        + (tooQuiet   ? " (too quiet <"   + minSessionRange + ")" : "")
                        + (tooChaotic ? " (too chaotic >" + maxSessionRange + ")" : ""));
                    SendDiscordAlert("SESSION | Range: " + priorRange.ToString("F0") + "pts | " + rangeStatus);
                }

                // Reset session tracking
                sessionHigh = 0;
                sessionLow  = 0;

                // FIX 2 — tradedToday is now set in OnExecutionUpdate on actual fill,
                // so we only reset it here at session open (no change needed here)
                tradedToday    = false;
                dailyHalted    = false;
                lastBullishRej = false;
                lastBearishRej = false;
                lastRejHigh    = 0;
                lastRejLow     = 0;
                lastRejBar     = 0;
                vwapCumTPVol   = 0;
                vwapCumVol     = 0;
                trailActive    = false;
                partialTaken   = false;
                partialPnl     = 0;     // FIX 3 — reset partial PnL tracker at session open
                highWaterMark  = 0;
                lowWaterMark   = 0;
                tradeSide      = "";
                initialRisk    = 0;
                dayStartEquity = AccountEquity();
                todayProfit    = 0;
                consecLosses   = 0;
                consecLossDollars = 0;   // FIX 2 — reset streak dollars
                consecHalted   = false;
                newsHaltActive = false;

                // [FIX 2] Update the EOD trailing-drawdown floor. At 09:30 dayStartEquity
                // = AccountEquity() = prior end-of-day balance (the bot is flat overnight),
                // and LucidFlex trails on the EOD balance — CONFIRMED EOD, not intraday.
                if (dayStartEquity > maxEodEquity) maxEodEquity = dayStartEquity;
                trailingDdFloor = maxEodEquity - maxDrawdown;
                // NO lock at startingBalance. Lucid trails maxEod-1900 right up until you
                // reach initial+target, which for this eval coincides with evalPassed
                // (+$3,000) — so during the eval the floor just trails. The old
                // lock-at-startingBalance was MORE LENIENT than Lucid's rule
                // (under-protective in the tail). Removed.
                Print(Time[0] + " TRAILING DD FLOOR = $" + trailingDdFloor.ToString("F2")
                    + " (maxEod=$" + maxEodEquity.ToString("F2") + ")");

                if (skipToday)
                    Print(Time[0] + " MANUAL SKIP TODAY enabled");
            }

            // ── VWAP accumulation ─────────────────────────
            if (hour >= 9 && hour < 16)
            {
                double hlc3   = (High[0] + Low[0] + Close[0]) / 3.0;
                vwapCumTPVol += hlc3 * Volume[0];
                vwapCumVol   += Volume[0];
            }

            // ── Prop firm checks ──────────────────────────
            CheckPropFirmRules();

            // ── Manage open position ──────────────────────
            if (Position.MarketPosition != MarketPosition.Flat)
            {
                ManagePosition();
                return;
            }

            // ── All entry gates ───────────────────────────
            if (terminalHalt)    return;   // DD-floor breached — account terminal, never re-enter
            if (skipToday)       return;
            if (!sessionRangeOk) return;
            if (dailyHalted)     return;
            if (evalPassed)      return;
            if (consecHalted)    return;
            if (tradedToday)     return;

            // ── Lunch hour filter 12:00–13:30 ET ──────────
            if ((hour == 12) || (hour == 13 && minute < 30)) return;

            // ── News blackout ─────────────────────────────
            if (IsNewsBlackout(Time[0])) return;

            // ── Trading hours ─────────────────────────────
            if (hour < 9 || hour >= 15) return;

            // ── VWAP / EMA ready ──────────────────────────
            if (vwapCumVol == 0 || ema == null) return;

            DetectRejections();
            TryEntry();
        }

        // ─── FIX 2 — tradedToday set on confirmed fill ────
        // Prevents silent day lockout if order is rejected by broker.
        // tradedToday = true is no longer set in TryEntry().
        protected override void OnExecutionUpdate(
            Execution execution,
            string executionId,
            double price,
            int quantity,
            MarketPosition marketPosition,
            string orderId,
            DateTime time)
        {
            if (execution.Order == null) return;
            if (execution.Order.OrderState != OrderState.Filled
                && execution.Order.OrderState != OrderState.PartFilled) return;

            OrderAction action = execution.Order.OrderAction;
            double pv = Instrument.MasterInstrument.PointValue;   // MNQ = 2

            // ── ENTRY fills (Buy opens long, SellShort opens short) ──
            if (action == OrderAction.Buy || action == OrderAction.SellShort)
            {
                tradedToday      = true;                  // set on confirmed fill (no silent lockout)
                tradeEntryPrice  = Position.AveragePrice; // real blended entry
                tradeRealizedPnl = 0;
                Print(time + " ENTRY FILL — " + execution.Order.Name
                    + " avg=" + tradeEntryPrice.ToString("F2") + " x" + quantity);
                return;
            }

            // ── EXIT fills (Sell closes long, BuyToCover closes short) ──
            // [FIX 1] Accounting moved here because the protective stop now lives at the
            // exchange and can fill intrabar — ManagePosition() may never see the exit.
            if (action == OrderAction.Sell || action == OrderAction.BuyToCover)
            {
                double legPnl = (action == OrderAction.Sell)
                    ? (price - tradeEntryPrice) * quantity * pv    // long exit leg
                    : (tradeEntryPrice - price) * quantity * pv;   // short exit leg
                tradeRealizedPnl += legPnl;

                // Finalize once fully flat (covers partial + remainder at real fill prices)
                if (Position.MarketPosition == MarketPosition.Flat && tradeSide != "")
                {
                    UpdateConsecLosses(tradeRealizedPnl);
                    string side = tradeSide.ToUpper();
                    string tag  = execution.Order.Name;
                    Print(time + " TRADE CLOSED (" + tag + ") PnL="
                        + tradeRealizedPnl.ToString("F2"));
                    SendDiscordAlert((tradeRealizedPnl >= 0 ? "✅" : "❌") + " " + side
                        + " CLOSED (" + tag + ") | PnL: "
                        + (tradeRealizedPnl >= 0 ? "+$" : "-$")
                        + Math.Abs(tradeRealizedPnl).ToString("F2"));

                    // reset trade state
                    tradeSide    = "";
                    partialTaken = false;
                    partialPnl   = 0;
                    trailActive  = false;
                }
            }
        }

        private void DetectRejections()
        {
            double o         = Open[0];
            double h         = High[0];
            double l         = Low[0];
            double c         = Close[0];
            double body      = Math.Abs(c - o);
            bool   validBody = body > 0;
            double upperWick = h - Math.Max(o, c);
            double lowerWick = Math.Min(o, c) - l;

            bool bearishRej = validBody
                && upperWick > body * wickRatio
                && c < o
                && lowerWick < body * 0.5;

            bool bullishRej = validBody
                && lowerWick > body * wickRatio
                && c > o
                && upperWick < body * 0.5;

            if (bearishRej)
            {
                lastRejHigh    = h;
                lastRejLow     = l;
                lastBearishRej = true;
                lastBullishRej = false;
                lastRejBar     = CurrentBar;
                Print(Time[0] + " BEARISH REJ h=" + h + " l=" + l);
            }
            if (bullishRej)
            {
                lastRejHigh    = h;
                lastRejLow     = l;
                lastBullishRej = true;
                lastBearishRej = false;
                lastRejBar     = CurrentBar;
                Print(Time[0] + " BULLISH REJ h=" + h + " l=" + l);
            }
        }

        private void TryEntry()
        {
            // ── Reject stale blocks ───────────────────────
            if (lastRejBar > 0 && (CurrentBar - lastRejBar) > rejExpiryBars)
            {
                lastBullishRej = false;
                lastBearishRej = false;
                lastRejHigh    = 0;
                lastRejLow     = 0;
                lastRejBar     = 0;
                Print(Time[0] + " REJ EXPIRED after " + rejExpiryBars + " bars");
                return;
            }

            double vwap   = vwapCumTPVol / vwapCumVol;
            double emaVal = ema[0];
            double c      = Close[0];

            // ── Long entry ────────────────────────────────
            if (lastBullishRej
                && lastRejLow > 0
                && c <= lastRejLow + retraceTolerancePts
                && c > vwap
                && c > emaVal)
            {
                double sl          = lastRejLow - slBuffer;
                double riskDollars = (c - sl) * contracts * 2;

                // [FIX 3] SKIP-instead-of-COMPRESS. The old code did:
                //     if (riskDollars > maxLossDollars) sl = c - (maxLossDollars/(contracts*2));
                // which yanks the stop to a structureless price that gets run instantly.
                // Now: skip the trade and wait for a setup whose real stop fits the budget.
                if (riskDollars > maxLossDollars)
                {
                    Print(Time[0] + " SKIP long — risk $" + riskDollars.ToString("F0")
                        + " > cap $" + maxLossDollars + " (stop NOT compressed)");
                    return;
                }

                // entryPrice is a bar-close estimate used only for the initial stop size.
                // All post-fill logic uses Position.AveragePrice (fillPrice) in ManagePosition().
                stopPrice     = sl;
                entryPrice    = c;
                initialRisk   = c - sl;
                highWaterMark = c;
                trailActive   = false;
                partialTaken  = false;
                partialPnl    = 0;
                tradeSide     = "long";

                // [HYBRID] Phase-1 stop = TIGHT intrabar exchange stop at the structural
                // level (v1 loss control). It widens to a catastrophe floor at the partial.
                SetStopLoss("RB Long", CalculationMode.Price, sl, false);
                EnterLong(contracts, "RB Long");

                Print(Time[0] + " LONG entry=" + c.ToString("F2")
                    + " sl=" + sl.ToString("F2")
                    + " 1R=" + (c + initialRisk).ToString("F2")
                    + " vwap=" + vwap.ToString("F2")
                    + " ema=" + emaVal.ToString("F2"));

                SendDiscordAlert("📈 LONG | " + c.ToString("F2")
                    + " | SL: " + sl.ToString("F2")
                    + " | 1R: " + (c + initialRisk).ToString("F2")
                    + " | " + Time[0].ToString("HH:mm ET"));
            }

            // ── Short entry ───────────────────────────────
            else if (lastBearishRej
                && lastRejHigh > 0
                && c >= lastRejHigh - retraceTolerancePts
                && c < vwap
                && c < emaVal)
            {
                double sl          = lastRejHigh + slBuffer;
                double riskDollars = (sl - c) * contracts * 2;

                // [FIX 3] SKIP-instead-of-COMPRESS (short side)
                if (riskDollars > maxLossDollars)
                {
                    Print(Time[0] + " SKIP short — risk $" + riskDollars.ToString("F0")
                        + " > cap $" + maxLossDollars + " (stop NOT compressed)");
                    return;
                }

                stopPrice    = sl;
                entryPrice   = c;
                initialRisk  = sl - c;
                lowWaterMark = c;     // FIX 4 — init to entry, not 0
                trailActive  = false;
                partialTaken = false;
                partialPnl   = 0;
                tradeSide    = "short";

                // [HYBRID] Phase-1 tight intrabar exchange stop at structural; widens at partial
                SetStopLoss("RB Short", CalculationMode.Price, sl, false);
                EnterShort(contracts, "RB Short");

                Print(Time[0] + " SHORT entry=" + c.ToString("F2")
                    + " sl=" + sl.ToString("F2")
                    + " 1R=" + (c - initialRisk).ToString("F2")
                    + " vwap=" + vwap.ToString("F2")
                    + " ema=" + emaVal.ToString("F2"));

                SendDiscordAlert("📉 SHORT | " + c.ToString("F2")
                    + " | SL: " + sl.ToString("F2")
                    + " | 1R: " + (c - initialRisk).ToString("F2")
                    + " | " + Time[0].ToString("HH:mm ET"));
            }
        }

        private void ManagePosition()
        {
            double c = Close[0];

            // ── FIX 1 — use actual fill price for all post-fill logic ──
            // Position.AveragePrice reflects the real fill, not bar close estimate.
            // entryPrice (set in TryEntry) is only used for initial stop sizing.
            double fillPrice = Position.AveragePrice;

            if (tradeSide == "long")
            {
                if (High[0] > highWaterMark)
                    highWaterMark = High[0];

                if (!partialTaken)
                {
                    // ── PHASE 1 (pre-partial) ── the TIGHT intrabar exchange stop set at
                    // entry IS the loss control (v1 behavior — caps initial losers tight,
                    // keeps DD down). The only transition here is the 1R partial.
                    if (initialRisk > 0 && c >= fillPrice + initialRisk && Position.Quantity > 1)
                    {
                        ExitLong(1, "Partial TP", "RB Long");
                        partialTaken = true;
                        stopPrice    = fillPrice;   // BE for the phase-2 bar-close exit
                        // widen the exchange stop to a catastrophe floor so the runner breathes
                        double catStop = fillPrice - catastropheMult * initialRisk;
                        SetStopLoss("RB Long", CalculationMode.Price, catStop, false);
                        Print(Time[0] + " PARTIAL TP long @ " + c.ToString("F2")
                            + " stop→BE fill=" + fillPrice.ToString("F2") + ", exch→catastrophe");
                        SendDiscordAlert("✂️ PARTIAL TP LONG | " + c.ToString("F2")
                            + " | Stop → BE: " + fillPrice.ToString("F2"));
                    }
                }
                else
                {
                    // ── PHASE 2 (post-partial) ── runner. Bar-close exit at BE/trail gives
                    // room to reach the trail (the edge). Exchange stop is now the wide
                    // catastrophe floor (gaps/disconnects only).
                    double profitPts = highWaterMark - fillPrice;
                    if (profitPts >= trailActivation && !trailActive)
                    {
                        trailActive = true;
                        Print(Time[0] + " TRAIL ACTIVATED long");
                    }
                    if (trailActive)
                    {
                        double newStop = highWaterMark - trailOffset;
                        if (newStop > stopPrice) stopPrice = newStop;
                    }
                    if (c <= stopPrice)
                        ExitLong("SL", "RB Long");
                }
            }
            else if (tradeSide == "short")
            {
                if (Low[0] < lowWaterMark || lowWaterMark == 0)
                    lowWaterMark = Low[0];

                if (!partialTaken)
                {
                    // ── PHASE 1 (pre-partial) ── tight intrabar exchange stop is loss control
                    if (initialRisk > 0 && c <= fillPrice - initialRisk && Position.Quantity > 1)
                    {
                        ExitShort(1, "Partial TP", "RB Short");
                        partialTaken = true;
                        stopPrice    = fillPrice;
                        double catStop = fillPrice + catastropheMult * initialRisk;
                        SetStopLoss("RB Short", CalculationMode.Price, catStop, false);
                        Print(Time[0] + " PARTIAL TP short @ " + c.ToString("F2")
                            + " stop→BE fill=" + fillPrice.ToString("F2") + ", exch→catastrophe");
                        SendDiscordAlert("✂️ PARTIAL TP SHORT | " + c.ToString("F2")
                            + " | Stop → BE: " + fillPrice.ToString("F2"));
                    }
                }
                else
                {
                    // ── PHASE 2 (post-partial) ── runner; bar-close BE/trail, wide catastrophe exch
                    double profitPts = fillPrice - lowWaterMark;
                    if (profitPts >= trailActivation && !trailActive)
                    {
                        trailActive = true;
                        Print(Time[0] + " TRAIL ACTIVATED short");
                    }
                    if (trailActive)
                    {
                        double newStop = lowWaterMark + trailOffset;
                        if (newStop < stopPrice) stopPrice = newStop;
                    }
                    if (c >= stopPrice)
                        ExitShort("SL", "RB Short");
                }
            }
        }

        private void UpdateConsecLosses(double pnl)
        {
            double equity = AccountEquity();

            if (pnl > 0)
            {
                consecLosses      = 0;
                consecLossDollars = 0;        // FIX 2
                consecHalted      = false;
                Print(Time[0] + " WIN — streak reset");
                return;
            }

            // ── loss ──
            if (consecLosses == 0)            // FIX 2 — streak begins: anchor the budget
                streakStartRoom = (trailingDdFloor > 0) ? (equity - trailingDdFloor) : 0;

            consecLosses++;
            consecLossDollars += Math.Abs(pnl);
            Print(Time[0] + " LOSS — streak=" + consecLosses
                + " streak$=" + consecLossDollars.ToString("F0"));

            // [FIX 2] Halt when the streak has burned 40% of the DD room it started with.
            // This is the REAL backstop: 10 x $400 = $4,000 >> the $1,900 floor, so the
            // old count-of-10 never fired in time. Dollars, not a round count.
            if (streakStartRoom > 0 && consecLossDollars >= 0.40 * streakStartRoom)
            {
                consecHalted = true;
                Print(Time[0] + " STREAK $-BUDGET HALT — $" + consecLossDollars.ToString("F0")
                    + " of room $" + streakStartRoom.ToString("F0"));
                SendDiscordAlert("⛔ STREAK HALT | -$" + consecLossDollars.ToString("F0")
                    + " in " + consecLosses + " losses (DD budget)");
            }

            // count-based backstop retained (optimizer property)
            if (consecLosses >= maxConsecLosses)
            {
                consecHalted = true;
                Print(Time[0] + " CONSEC COUNT HALT");
                SendDiscordAlert("⛔ CONSEC HALT | " + consecLosses + " consecutive losses");
            }
        }

        private void CheckPropFirmRules()
        {
            if (startingBalance == 0) return;

            double equity = AccountEquity();
            todayProfit   = equity - dayStartEquity;
            totalProfit   = equity - startingBalance;   // == cumulative realized PnL

            // ── Eval passed ───────────────────────────────
            if (totalProfit >= profitTarget && !evalPassed)
            {
                evalPassed = true;
                ExitLong("Eval Passed", "RB Long");
                ExitShort("Eval Passed", "RB Short");
                Print(Time[0] + " EVAL PASSED $" + totalProfit.ToString("F2"));
                SendDiscordAlert("🏆 EVAL PASSED! Total: +$" + totalProfit.ToString("F2")
                    + " | CHECK LUCID ACCOUNT NOW!");
                return;
            }

            // ── [FIX 2] TRAILING drawdown halt (LucidFlex trails on EOD balance) ──
            // The old code measured drawdown from the STATIC startingBalance, which
            // under-protects: once equity rises, Lucid's floor rises with it, so the
            // static guard reports room that Lucid no longer gives. Halt vs trailingDdFloor.
            // terminalHalt (not dailyHalted) latches the breach so a blown account stays
            // halted across sessions and does NOT re-fire the alert every morning.
            if (trailingDdFloor > 0 && equity <= trailingDdFloor && !terminalHalt)
            {
                terminalHalt = true;
                dailyHalted  = true;
                ExitLong("DD Halt", "RB Long");
                ExitShort("DD Halt", "RB Short");
                double usedDd = maxEodEquity - equity;
                Print(Time[0] + " TRAILING DD HALT — equity $" + equity.ToString("F2")
                    + " <= floor $" + trailingDdFloor.ToString("F2"));
                SendDiscordAlert("🚨 TRAILING DD HALT | equity $" + equity.ToString("F2")
                    + " | floor $" + trailingDdFloor.ToString("F2")
                    + " | used -$" + usedDd.ToString("F2"));
                return;
            }

            // ── Daily profit cap ──────────────────────────
            if (todayProfit >= dailyProfitCap && !dailyHalted)
            {
                dailyHalted = true;
                ExitLong("Daily Cap", "RB Long");
                ExitShort("Daily Cap", "RB Short");
                Print(Time[0] + " DAILY PROFIT CAP HIT $" + todayProfit.ToString("F2"));
                SendDiscordAlert("💰 DAILY CAP HIT | Today: +$" + todayProfit.ToString("F2")
                    + " | Done for the day ✅");
                return;
            }
        }
    }
}
