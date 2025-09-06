namespace QuantConnect.China

open System
open System.IO
open FSharpPlus
open QuantConnect
open QuantConnect.Data

[<AllowNullLiteral>]
type FutureDailyPlusBar() =
    inherit Market.TradeBar()

    static let shTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai")

    override _.GetSource(subscriptionDataConfig: SubscriptionDataConfig, time: DateTime, isLiveMode: bool) = 
        let symbol = subscriptionDataConfig.Symbol
        let market = symbol.ID.Market.ToUpperInvariant()
        let directory = Path.Combine(Globals.DataFolder, "future", market, "daily+")
        let year = symbol.ID.Date.Year
        let suffix = $"{year % 100}{symbol.ID.Date.Month |> string |> String.padLeftWith 2 '0'}"
        let name = $"{symbol.Underlying.ID.Symbol}{suffix}.csv"
        let path = Path.Combine(directory, string year, name)
        SubscriptionDataSource(path, SubscriptionTransportMedium.LocalFile, FileFormat.Csv)
        
    override me.Reader(config: SubscriptionDataConfig, line: string, date: DateTime, isLiveMode: bool) =
        match line.StartsWith("#") with
        | true -> null
        | false when line |> String.IsNullOrEmpty -> null
        | false ->
            let exchangeTimeZone = TimeZoneInfo.FindSystemTimeZoneById(config.ExchangeTimeZone.Id)
            let parts = line.Split(',', StringSplitOptions.TrimEntries)
            let day = DateOnly.ParseExact(parts[2], "yyyyMMdd")
            let endTime =
                let t = day.ToDateTime(TimeOnly(15, 0, 0))
                TimeZoneInfo.ConvertTime(t, shTimeZone, exchangeTimeZone)
            me.TradingDay <- day
            me.Open <- Decimal.Parse(parts[3])
            me.High <- Decimal.Parse(parts[4])
            me.Low <- Decimal.Parse(parts[5])
            me.Close <- Decimal.Parse(parts[6])
            me.SettlementPrice <- Decimal.Parse(parts[7])
            me.Volume <- Decimal.Parse(parts[8])
            me.Turnover <- Decimal.Parse(parts[9])
            me.OpenInterest <- Decimal.Parse(parts[10])
            me.PreSettlementPrice <- Decimal.Parse(parts[11])
            me.PreClosePrice <- Decimal.Parse(parts[12])
            me.LowerBound <- Decimal.Parse(parts[13])
            me.UpperBound <- Decimal.Parse(parts[14])
            me.EndTime <- endTime
            // Lean aligns daily bars by EndTime.
            // Setting Time same as EndTime makes the date the same otherwise Lean's SubscriptionDataReader
            // might make Time a day ahead the EndTime.
            me.Time <- endTime  
            me.Symbol <- config.Symbol
            me :> BaseData
            
    member val TradingDay = DateOnly.MinValue with get, set
    member val PreSettlementPrice = -1m with get, set
    member val PreClosePrice = -1m with get, set
    member val LowerBound = -1m with get, set
    member val UpperBound = -1m with get, set
    member val SettlementPrice = -1m with get, set
    override val Open = -1m with get, set
    override val High = -1m with get, set
    override val Low = -1m with get, set
    override val Close = -1m with get, set
    member val Turnover = -1m with get, set
    member val OpenInterest = -1m with get, set
    override val Volume = -1m with get, set
    override val Value = -1m with get, set
    override val EndTime = DateTime.MinValue with get, set