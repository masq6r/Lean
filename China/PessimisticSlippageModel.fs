namespace QuantConnect.China

open QuantConnect.Orders.Slippage

/// <summary>
/// Use the maximum adverse price of the last trade bar plus <paramref name="v"/> as the fill price.
/// Must use trade bar.
/// </summary>
/// <param name = "v">Price variation.</param>
type PessimisticSlippageModel(?v) =
    let v' = decimal <| defaultArg v 0
    interface ISlippageModel with
        member _.GetSlippageApproximation(asset, order) =
            let o = v' * asset.SymbolProperties.MinimumPriceVariation
            if order.Quantity > 0m
            then asset.High + o - asset.Close
            else asset.Close + o - asset.Low

