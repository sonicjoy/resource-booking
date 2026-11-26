namespace ResourceBooking.Shared;

/// <summary>The rule settings an admin can set on a resource.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(NameOnlyEnumConverter<RuleField>))]
public enum RuleField
{
    OpeningHours,
    MinDuration,
    MaxDuration,
    MinNotice,
    MaxDaysAhead,
    Turnaround,
    MaxFuturePerUser,
    Units,
}

/// <summary>
/// Which rule settings apply to each resource type. The admin form shows only these,
/// and the rule checker ignores any other setting a resource happens to carry.
/// </summary>
public static class RuleFields
{
    private static readonly IReadOnlySet<RuleField> Room = new HashSet<RuleField>
    {
        RuleField.OpeningHours, RuleField.MinDuration, RuleField.MaxDuration,
        RuleField.MinNotice, RuleField.MaxDaysAhead, RuleField.MaxFuturePerUser,
    };

    private static readonly IReadOnlySet<RuleField> Car = new HashSet<RuleField>(Room) { RuleField.Turnaround };

    private static readonly IReadOnlySet<RuleField> Kitchen = new HashSet<RuleField>
    {
        RuleField.OpeningHours, RuleField.MinNotice, RuleField.MaxDaysAhead,
        RuleField.MaxFuturePerUser, RuleField.Units,
    };

    public static IReadOnlySet<RuleField> For(ResourceType type) => type switch
    {
        ResourceType.MeetingRoom => Room,
        ResourceType.Car => Car,
        ResourceType.Catering => Kitchen,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };
}
