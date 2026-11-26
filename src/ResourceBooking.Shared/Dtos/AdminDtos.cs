using System.ComponentModel.DataAnnotations;

namespace ResourceBooking.Shared.Dtos;

public sealed record UserDto(Guid Id, string Email, string DisplayName, Role Role, bool IsActive);

/// <summary>Adds or edits a user. Activity is not here: only deactivation changes it.</summary>
public sealed class UserRequest
{
    [Required, EmailAddress, StringLength(200)]
    public string Email { get; set; } = "";

    [Required, StringLength(100, MinimumLength = 1)]
    public string DisplayName { get; set; } = "";

    public Role Role { get; set; } = Role.Employee;
}

/// <summary>
/// Adds or edits a resource. Only the rule fields its type uses (<see cref="RuleFields.For"/>) are kept;
/// the others are cleared. Type can't change after creation, and the last day is set only by retirement.
/// </summary>
public sealed class ResourceRequest : IValidatableObject
{
    private static readonly TimeSpan Year = TimeSpan.FromDays(366);

    [Required, StringLength(100, MinimumLength = 1)]
    public string Name { get; set; } = "";

    [Required]
    public ResourceType? Type { get; set; }

    /// <summary>Seats for a room or car; portions per order for a kitchen.</summary>
    [Range(1, 1000)]
    public int Capacity { get; set; } = 1;

    /// <summary>Orders a kitchen takes per day; always 1 for rooms and cars.</summary>
    [Range(1, 50)]
    public int Units { get; set; } = 1;

    public TimeOnly? OpensAt { get; set; }
    public TimeOnly? ClosesAt { get; set; }
    public TimeSpan? MinDuration { get; set; }
    public TimeSpan? MaxDuration { get; set; }
    public TimeSpan? MinNotice { get; set; }

    [Range(1, 365)]
    public int? MaxDaysAhead { get; set; }

    public TimeSpan? Turnaround { get; set; }

    [Range(1, 100)]
    public int? MaxFuturePerUser { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (OpensAt is { } opens && ClosesAt is { } closes && closes <= opens)
        {
            yield return new ValidationResult("Closing must be after opening.", [nameof(ClosesAt)]);
        }
        foreach (var (name, value, max) in new[]
        {
            (nameof(MinDuration), MinDuration, Year),
            (nameof(MaxDuration), MaxDuration, Year),
            (nameof(MinNotice), MinNotice, Year),
            (nameof(Turnaround), Turnaround, TimeSpan.FromDays(1)),
        })
        {
            if (value is { } span && (span <= TimeSpan.Zero || span > max))
            {
                yield return new ValidationResult($"{name} must be more than zero and at most {max.TotalDays:0} days.", [name]);
            }
        }
        if (MinDuration is { } min && MaxDuration is { } most && most < min)
        {
            yield return new ValidationResult("The longest booking can't be shorter than the shortest.", [nameof(MaxDuration)]);
        }
    }
}

public sealed class RetirementRequest
{
    [Required]
    public DateOnly? LastDay { get; set; }
}

/// <summary>The result of a retirement or deactivation: how many future bookings it cancelled.</summary>
public sealed record CascadeResult(int Cancelled);

/// <summary>
/// One resource's use over a date range. Rooms and cars count hours booked over hours open; kitchens count
/// orders (unit-days) over units times days. Ratio is null when the resource had no open time in the range.
/// </summary>
public sealed record UtilisationRow(Guid ResourceId, string Name, ResourceType Type, DateOnly? LastDay, double Used, double Available, string Unit, double? Ratio);

public sealed record FieldChange(string Field, string? Old, string? New);

/// <summary>An audit event in words: who did what to which user, resource or booking, and when (London time).</summary>
public sealed record HistoryItem(DateTime At, string Actor, string EntityType, Guid EntityId, string Entity, string Verb, IReadOnlyList<FieldChange> Changes);

/// <summary>History, newest first, up to a limit; More is true when older matching events were left out.</summary>
public sealed record HistoryPage(IReadOnlyList<HistoryItem> Items, bool More);
