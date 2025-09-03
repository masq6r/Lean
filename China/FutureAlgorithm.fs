namespace QuantConnect.China

open System
open QuantConnect
open QuantConnect.Securities

type private FutureInitialiser(brokerageModel, securitySeeder, algo: Algorithm.QCAlgorithm, slippage, feeRatio, marginRatio) =
    inherit BrokerageModelSecurityInitializer(brokerageModel, securitySeeder)

    override _.Initialize(security) =
        base.Initialize(security)
        security.SetSlippageModel(PessimisticSlippageModel(slippage))
        security.SetFeeModel(FixedRatioFeeModel(decimal feeRatio))
        security.SetBuyingPowerModel(FixedRatioFutureMarginModel(decimal marginRatio, algo))
        security.SettlementModel <- FutureSettlementModel(algo)

/// <summary>
/// Tailored to fit China futures market:
/// TimeZone: set to Shanghai.
/// Currency: CNH.
/// Benchmark: constant annual return set by strategy parameter `annual-return-rate`.
/// Buying power model, fee model and slippage model are customised to fit China futures market.
/// </summary>
type FutureAlgorithm() as self =
    inherit Algorithm.QCAlgorithm()

    let slippage = self.GetParameter("slippage", 0)
    let feeRatio = self.GetParameter("fee-ratio", 0.00005)
    let marginRatio = self.GetParameter("margin-ratio", 0.15)
    let dailyReturn =
        let rate = self.GetParameter("annual-return-rate", 0.05)
        Math.Pow(1.0 + rate, 1.0 / 365.0)

    do
        self.SetTimeZone(TimeZones.Shanghai)
        self.SetAccountCurrency(Currencies.CNH)
        self.SetBenchmark(fun day ->
            let span = day - self.StartDate
            decimal <|  dailyReturn ** span.TotalDays)
        self.SetSecurityInitializer(FutureInitialiser(self.BrokerageModel, SecuritySeeder.Null, self, slippage, feeRatio, marginRatio))

    member val DataTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Wake")

    member me.AddDailyPlus(underlying: Symbol, ?fillForward) =
        let ff = defaultArg fillForward false
        me.AddData<FutureDailyPlusBar>(underlying, Resolution.Daily, fillForward = ff)
