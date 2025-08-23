namespace QuantConnect.China

open System
open QuantConnect
open QuantConnect.Securities
open QuantConnect.Configuration

type private FutureInitialiser(brokerageModel, securitySeeder, algo: Algorithm.QCAlgorithm) =
    inherit BrokerageModelSecurityInitializer(brokerageModel, securitySeeder)

    override _.Initialize(security) =
        base.Initialize(security)
        let slippage = Configuration.Config.GetInt("slippage", 0)
        security.SetSlippageModel(PessimisticSlippageModel(slippage))
        let feeRatio = Config.GetDouble("fee-ratio", 0.00005)
        security.SetFeeModel(FixedRatioFeeModel(decimal feeRatio))
        let marginRatio = Config.GetDouble("margin-ratio", 0.15)
        security.SetBuyingPowerModel(FixedRatioFutureMarginModel(decimal marginRatio, algo))
        security.SettlementModel <- FutureSettlementModel(algo)

/// <summary>
/// Tailored to fit China futures market:
/// TimeZone: set to Shanghai.
/// Currency: CNH.
/// Benchmark: constant annual return set by `annual-return-rate` of `config.json`.
/// Buying power model, fee model and slippage model are customised to fit China futures market.
/// </summary>
type FutureAlgorithm() as self =
    inherit Algorithm.QCAlgorithm()

    let dailyReturn =
        let rate = Configuration.Config.GetDouble("annual-return-rate", 0.05)
        Math.Pow(1.0 + rate, 1.0 / 365.0)

    do
        self.SetTimeZone(TimeZones.Shanghai)
        self.SetAccountCurrency(Currencies.CNH)
        self.SetBenchmark(fun day ->
            let span = day - self.StartDate
            decimal <|  dailyReturn ** span.TotalDays)
        self.SetSecurityInitializer(FutureInitialiser(self.BrokerageModel, SecuritySeeder.Null, self))

    member val DataTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Wake")
