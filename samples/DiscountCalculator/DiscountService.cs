namespace DiscountCalculator;

public enum CustomerType
{
    Regular,
    Premium,
}

/// <summary>
/// Sample code under test, mirroring the example from the TestForge presentation:
/// premium customers get a discount, negative prices are rejected, regular customers pay full price.
/// </summary>
public class DiscountService
{
    public const decimal PremiumDiscountRate = 0.20m;
    public const decimal BulkThreshold = 1000m;
    public const decimal BulkDiscount = 50m;

    public decimal CalculateFinalPrice(decimal unitPrice, CustomerType customerType, int quantity = 1)
    {
        if (unitPrice < 0)
            throw new ArgumentException("Price cannot be negative.", nameof(unitPrice));

        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be at least 1.");

        var total = unitPrice * quantity;

        if (customerType == CustomerType.Premium)
        {
            total -= total * PremiumDiscountRate;
        }

        if (total >= BulkThreshold)
        {
            total -= BulkDiscount;
        }

        return Math.Round(total, 2, MidpointRounding.AwayFromZero);
    }

    public string DescribeDiscount(CustomerType customerType) => customerType switch
    {
        CustomerType.Premium => $"Premium: %{PremiumDiscountRate * 100:0} indirim",
        CustomerType.Regular => "İndirim yok",
        _ => throw new ArgumentOutOfRangeException(nameof(customerType)),
    };
}
