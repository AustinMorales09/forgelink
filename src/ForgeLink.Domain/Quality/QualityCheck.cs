namespace ForgeLink.Domain.Quality;

public enum QualityResult
{
    Pass,
    Fail,
}

/// <summary>A variable (measured) inspection against specification limits.</summary>
public class QualityCheck : IAudited
{
    private QualityCheck() { }

    public static QualityCheck Record(
        string workOrderNumber, string lotNumber, string characteristic,
        decimal nominal, decimal lowerLimit, decimal upperLimit, decimal measured,
        string inspector, DateTime checkedAtUtc)
    {
        if (lowerLimit > upperLimit)
            throw new DomainException("validation", "Lower limit cannot be greater than upper limit.");
        if (nominal < lowerLimit || nominal > upperLimit)
            throw new DomainException("validation", "Nominal must sit inside the specification limits.");

        return new QualityCheck
        {
            WorkOrderNumber = Guard.NotBlank(workOrderNumber, nameof(workOrderNumber)).ToUpperInvariant(),
            LotNumber = Guard.NotBlank(lotNumber, nameof(lotNumber)).ToUpperInvariant(),
            Characteristic = Guard.NotBlank(characteristic, nameof(characteristic)),
            Nominal = nominal,
            LowerLimit = lowerLimit,
            UpperLimit = upperLimit,
            Measured = measured,
            Result = measured >= lowerLimit && measured <= upperLimit ? QualityResult.Pass : QualityResult.Fail,
            Inspector = Guard.NotBlank(inspector, nameof(inspector)),
            CheckedAtUtc = checkedAtUtc.Kind == DateTimeKind.Utc ? checkedAtUtc : checkedAtUtc.ToUniversalTime(),
        };
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public string WorkOrderNumber { get; private set; } = "";
    public string LotNumber { get; private set; } = "";
    public string Characteristic { get; private set; } = "";
    public decimal Nominal { get; private set; }
    public decimal LowerLimit { get; private set; }
    public decimal UpperLimit { get; private set; }
    public decimal Measured { get; private set; }
    public QualityResult Result { get; private set; }
    public string Inspector { get; private set; } = "";
    public DateTime CheckedAtUtc { get; private set; }
}
