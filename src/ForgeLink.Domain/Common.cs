namespace ForgeLink.Domain;

/// <summary>A business rule was violated. <see cref="Code"/> is a stable, machine-readable identifier.</summary>
public class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Optimistic concurrency token. Incremented on every update by the persistence layer.</summary>
public interface IVersioned
{
    int Version { get; set; }
}

/// <summary>Marker: changes to this entity are written to the audit trail (who/what/when/before/after).</summary>
public interface IAudited;

internal static class Guard
{
    public static string NotBlank(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new DomainException("validation", $"{name} is required.")
            : value.Trim();

    public static decimal Positive(decimal value, string name) =>
        value <= 0 ? throw new DomainException("validation", $"{name} must be greater than zero.") : value;

    public static decimal NotNegative(decimal value, string name) =>
        value < 0 ? throw new DomainException("validation", $"{name} cannot be negative.") : value;

    public static DateTime Utc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
}
