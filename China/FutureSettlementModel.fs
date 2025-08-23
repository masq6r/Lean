namespace QuantConnect.China

open System
open QuantConnect.Data
open QuantConnect.Logging
open QuantConnect.Algorithm
open QuantConnect.Securities

/// <summary>
/// This models MTM settlement for Chinese futures market.
/// </summary>
type FutureSettlementModel(algo: QCAlgorithm) =
    inherit ImmediateSettlementModel()

    let mutable lastSettlementDate = DateOnly.MinValue
    let mutable settledFutureQuantity = 0m
    // This price updates every time `Scan` is called (when a trading day ends)
    let mutable settlementPrice = 0m

    /// <summary>
    /// When a close fill occurs, this method is called to calculate PnL.
    /// </summary>
    override _.ApplyFunds(applyFundsParameters) =
        if settledFutureQuantity <> 0m then
            let fill = applyFundsParameters.Fill
            let security = applyFundsParameters.Security
            let futureHolding = security.Holdings :?> Future.FutureHolding

            let absoluteQuantityClosed = Math.Min(fill.AbsoluteFillQuantity, security.Holdings.AbsoluteQuantity)
            let quantityClosed = decimal (Math.Sign(-fill.FillQuantity)) * absoluteQuantityClosed

            let absoluteQuantityClosedSettled = Math.Min(absoluteQuantityClosed, Math.Abs(settledFutureQuantity))
            let quantityClosedSettled = decimal (Math.Sign(-fill.FillQuantity)) * absoluteQuantityClosedSettled

            // here we use the last settlement price we've used to calculate the trade unsettled funds (daily P&L we should apply)
            let settledContractsTodaysProfit = 
                futureHolding.TotalCloseProfit(
                    includeFees = false, 
                    exitPrice = fill.FillPrice, 
                    entryPrice = settlementPrice, 
                    quantity = quantityClosedSettled)
            let unsettledContractsTodaysProfit =
                if quantityClosedSettled <> quantityClosed then
                    // if we fall into any of these cases, it means the position closed was increased today before closing which means the
                    // profit of the increased quantity is not related to the settlement price because it happens after the last settlement
                    applyFundsParameters.CashAmount.Amount - futureHolding.SettledProfit - settledContractsTodaysProfit
                else 0m

            applyFundsParameters.CashAmount <- 
                CashAmount(settledContractsTodaysProfit + unsettledContractsTodaysProfit, applyFundsParameters.CashAmount.Currency)

            Log.Debug(
                $"FutureSettlementModel.ApplyFunds({security.Symbol}): {security.LocalTime}, " +
                $"QuantityClosed: {quantityClosed}, Settled: {settledFutureQuantity}, " +
                $"Applying: {applyFundsParameters.CashAmount.Amount}")

            // reduce our settled future quantity proportionally too
            let factor = quantityClosedSettled / settledFutureQuantity
            settledFutureQuantity <- settledFutureQuantity - quantityClosedSettled

            futureHolding.SettledProfit <- futureHolding.SettledProfit - factor * futureHolding.SettledProfit

        base.ApplyFunds(applyFundsParameters)

    /// <summary>
    /// This is called periodically to scan for any necessary settlements for futures.
    /// </summary>
    override _.Scan(settlementParameters) =
        let security = settlementParameters.Security
        let symbol = security.Symbol
        let day = DateOnly.FromDateTime(security.LocalTime.Date)
        match lastSettlementDate < day with
        // Fist time call of this trading day, let's settle - losers pay winners.
        | true when security.Invested && lastSettlementDate > DateOnly.MinValue -> 
            let futureHolding = security.Holdings :?> Future.FutureHolding
            let futureCache = security.Cache :?> Future.FutureCache
            let isFallback, sPrice =
                algo.ActiveSecurities.Values
                |> Seq.tryPick (fun sec ->
                    match sec.GetLastData() with
                    | :? FutureDailyPlusBar as dBar when dBar.Symbol.Underlying = symbol ->
                        if dBar.SettlementPrice > -1m then Some(false, dBar.SettlementPrice)
                        else None
                    | _ -> None)
                |> Option.defaultValue (true, futureCache.SettlementPrice)
            settlementPrice <- sPrice
            settledFutureQuantity <- security.Holdings.Quantity

            let dailyProfitLoss =
                futureHolding.TotalCloseProfit(includeFees = false, exitPrice = settlementPrice) - futureHolding.SettledProfit
            if dailyProfitLoss <> 0m then
                futureHolding.SettledProfit <- futureHolding.SettledProfit + dailyProfitLoss
                settlementParameters.Portfolio.CashBook[security.QuoteCurrency.Symbol].AddAmount(dailyProfitLoss) |> ignore
                Log.Debug(
                    $"FutureSettlementModel.Scan({security.Symbol}): {security.LocalTime} Daily P&L: {dailyProfitLoss}, " +
                    $"UnrealizedProfit: {futureHolding.UnrealizedProfit}, " +
                    $"Quantity: {settledFutureQuantity}, Settlement: {settlementPrice}" +
                    $"""{if isFallback then " (fallback to close price)" else ""}, Close: {futureCache.Close}""")
            lastSettlementDate <- day
        // First time call but no holdings to settle.
        | true -> lastSettlementDate <- day
        | false -> ()

    member _.SetLocalDateTimeFrontier(newLocalTime: DateTime) =
        lastSettlementDate <- DateOnly.FromDateTime(newLocalTime.Date)
