namespace QuantConnect.China

open QuantConnect

/// <summary>
/// The fixed ratio fee model charges based on the total value of the buying asset.
/// </summary>
/// <param name="feeRatio">The fee ratio to apply.</param>
type FixedRatioFeeModel(?feeRatio) =
    let feeRatio = defaultArg feeRatio 0m

    interface Orders.Fees.IFeeModel with

        member _.GetOrderFee(parameters) =
            let security = parameters.Security
            let order = parameters.Order
            let orderValue = order.GetValue(security)
            let fee = abs orderValue * feeRatio
            Securities.CashAmount(fee, security.QuoteCurrency.Symbol)
            |> Orders.Fees.OrderFee
