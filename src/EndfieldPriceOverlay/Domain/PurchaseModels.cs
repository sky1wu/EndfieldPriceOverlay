namespace EndfieldPriceOverlay.Domain;

public sealed record RegionPurchaseSettings(
    string Region,
    int Current,
    int Limit,
    int DailyRecovery);

public sealed record DailyPurchaseOffer(
    string Region,
    string ItemName,
    DateOnly Date,
    int Weekday,
    int Price,
    int? MinimumPrice = null,
    int? MaximumPrice = null)
{
    public int Minimum => MinimumPrice ?? Price;

    public int Maximum => MaximumPrice ?? Price;

    public bool IsRange => Minimum != Maximum;
}

public sealed record PurchaseRecommendationLine(
    string Region,
    DateOnly Date,
    int Weekday,
    string ItemName,
    int Price,
    int Quantity,
    int AvailableBeforePurchase,
    int? MinimumPrice = null,
    int? MaximumPrice = null)
{
    public int Minimum => MinimumPrice ?? Price;

    public int Maximum => MaximumPrice ?? Price;

    public bool IsRange => Minimum != Maximum;
}

public sealed record RegionPurchaseRecommendation(
    string Region,
    int Current,
    int Limit,
    int DailyRecovery,
    int TotalQuantity,
    IReadOnlyList<PurchaseRecommendationLine> Lines,
    string Message,
    bool IsReady);
