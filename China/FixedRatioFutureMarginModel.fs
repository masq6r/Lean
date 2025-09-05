namespace QuantConnect.China

open System
open FSharpPlus
open QuantConnect
open QuantConnect.Logging
open QuantConnect.Securities
open QuantConnect.Orders.Fees

/// <summary>
/// The fixed ratio fee model charges based on the total value of the buying asset.
/// </summary>
type FixedRatioFutureMarginModel =
    inherit Future.FutureMarginModel

    val private dataTz: TimeZoneInfo
    val private marginRatio: decimal
    val private algo: Algorithm.QCAlgorithm

    new(ratio, algorithm: Algorithm.QCAlgorithm, requiredFreeBuyingPowerPercent, security) = {
        inherit Future.FutureMarginModel(requiredFreeBuyingPowerPercent, security)
        marginRatio = ratio
        dataTz = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Wake")
        algo = algorithm
    }

    new(ratio, algorithm: Algorithm.QCAlgorithm, requiredFreeBuyingPowerPercent) = {
        inherit Future.FutureMarginModel(requiredFreeBuyingPowerPercent)
        marginRatio = ratio
        dataTz = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Wake")
        algo = algorithm
    }

    new(ratio, algorithm: Algorithm.QCAlgorithm) = {
        inherit Future.FutureMarginModel()
        marginRatio = ratio
        dataTz = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Wake")
        algo = algorithm
    }

    override me.GetInitialMarginRequirement(parameters) =
        match parameters.Quantity with
        | 0m -> InitialMargin.Zero
        | qty ->
            let sec = parameters.Security
            sec.SymbolProperties.ContractMultiplier
            * qty * sec.Price * me.marginRatio
            * sec.QuoteCurrency.ConversionRate
            |> InitialMargin

    override me.GetInitialMarginRequiredForOrder(parameters) =
        let fees = parameters.Security.FeeModel.GetOrderFee(
            OrderFeeParameters(parameters.Security, parameters.Order)).Value
        let feesInAccountCurrency = parameters.CurrencyConverter.ConvertToAccountCurrency(fees).Amount
        let orderMargin = me.GetInitialMarginRequirement(parameters.Security, parameters.Order.Quantity)
        InitialMargin(orderMargin + decimal (Math.Sign(orderMargin)) * feesInAccountCurrency)

    override me.GetMaintenanceMargin(parameters) =
        let security = parameters.Security
        let symbol = security.Symbol
        let futureCache = security.Cache :?> Future.FutureCache
        let multiplier = security.SymbolProperties.ContractMultiplier
        let isFallback, sPrice =
            me.algo.Securities.Values
            |> Seq.tryPick (fun sec ->
                match sec.GetLastData() with
                | :? FutureDailyPlusBar as dBar when dBar.Symbol.Underlying = symbol ->
                    if dBar.SettlementPrice > -1m then Some(false, dBar.SettlementPrice)
                    else None
                | _ -> None)
            |> Option.defaultValue (true, futureCache.SettlementPrice)
        
        let v =
            parameters.AbsoluteQuantity * sPrice * me.marginRatio
            * security.QuoteCurrency.ConversionRate * multiplier
        if isFallback then
            Log.Debug(
                $"{nameof FixedRatioFutureMarginModel}.GetMaintenanceMargin(): {security.Symbol}: {security.LocalTime}" +
                $"""Settlement: {sPrice}{if isFallback then " (fallback to close price)" else ""},""" +
                $"Qty: {parameters.AbsoluteQuantity}, Multiplier: {multiplier}, Margin ratio: {me.marginRatio}," +
                $" Maintenance margin: {v}")
        Securities.MaintenanceMargin v