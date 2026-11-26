using CsCheck;
using ResourceBooking.Endpoints;
using ResourceBooking.Shared.Dtos;

namespace ResourceBooking.Tests.Rules;

/// <summary>
/// The request shape the API refuses with 400 before any rule runs (design: "Booking request"). A booking
/// needs a resource, a start and an end with the end after the start, 1 to 1000 attendees and a title of at
/// most 100 characters; a catering order needs a delivery time instead of a start and an end. Each test
/// asserts exactly which fields are refused.
/// </summary>
public class BookingRequestValidationTests
{
    private static readonly DateTime Two = new(2026, 11, 26, 14, 0, 0);
    private static readonly Guid Thames = Guid.NewGuid();

    /// <summary>The fields the API's validation refuses, as it reports them in a 400.</summary>
    private static List<string> Errors(BookingRequest request) =>
        ApiResults.Validate(request).SelectMany(r => r.MemberNames).Order().ToList();

    private static BookingRequest Valid() => new() { ResourceId = Thames, Start = Two, End = Two.AddHours(1), Attendees = 6, Title = "Team sync" };

    // ---- start and end: every combination, and the catering exemption ----

    /// <summary>Start and end as offsets in hours from 14:00 (null = missing), with or without a delivery time.</summary>
    public static TheoryData<string, int?, int?, bool, string[]> Times => new()
    {
        { "both missing", null, null, false, ["End", "Start"] },
        { "start missing", null, 1, false, ["Start"] },
        { "end missing", 0, null, false, ["End"] },
        { "end before start", 1, 0, false, ["End"] },
        { "end equal to start", 0, 0, false, ["End"] },
        { "a valid pair", 0, 1, false, [] },
        { "catering, neither", null, null, true, [] },
        { "catering, start only", 0, null, true, [] },
        { "catering, end only", null, 1, true, [] },
        { "catering, end before start", 1, 0, true, [] },
        { "catering, a valid pair", 0, 1, true, [] },
    };

    [Theory]
    [MemberData(nameof(Times))]
    public void Start_and_end_are_checked_each_on_its_own_and_not_for_catering(string id, int? start, int? end, bool catering, string[] expected)
    {
        var request = Valid();
        request.Start = start is { } s ? Two.AddHours(s) : null;
        request.End = end is { } e ? Two.AddHours(e) : null;
        request.DeliveryAt = catering ? Two : null;

        Assert.True(expected.SequenceEqual(Errors(request)), $"{id}: expected [{string.Join(", ", expected)}], got [{string.Join(", ", Errors(request))}]");
    }

    // ---- the other fields, each failing while everything else holds ----

    [Fact]
    public void A_missing_resource_is_refused_alone() =>
        Assert.Equal(["ResourceId"], Errors(new BookingRequest { Start = Two, End = Two.AddHours(1), Attendees = 6 }));

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(1000, true)]
    [InlineData(1001, false)]
    public void Attendees_run_from_1_to_1000(int attendees, bool accepted)
    {
        var request = Valid();
        request.Attendees = attendees;

        Assert.Equal(accepted ? [] : ["Attendees"], Errors(request));
    }

    [Theory]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public void A_title_is_at_most_100_characters(int length, bool accepted)
    {
        var request = Valid();
        request.Title = new string('a', length);

        Assert.Equal(accepted ? [] : ["Title"], Errors(request));
    }

    // ---- level 3: the whole rule against the spec's statement of it ----

    /// <summary>The design's rule, written out field by field, independent of the validator.</summary>
    private static List<string> Expected(BookingRequest r)
    {
        var errors = new List<string>();
        if (r.ResourceId is null)
        {
            errors.Add("ResourceId");
        }
        if (r.Attendees is < 1 or > 1000)
        {
            errors.Add("Attendees");
        }
        if (r.Title is { Length: > 100 })
        {
            errors.Add("Title");
        }
        if (r.DeliveryAt is null)
        {
            if (r.Start is null)
            {
                errors.Add("Start");
            }
            if (r.End is null || (r.Start is not null && r.End <= r.Start))
            {
                errors.Add("End");
            }
        }
        return errors.Order().ToList();
    }

    private static Gen<T?> OrNull<T>(Gen<T> gen, double chance) where T : struct =>
        Gen.Select(Gen.Double, gen, (roll, value) => roll < chance ? (T?)null : value);

    [Fact]
    public void Every_request_is_refused_on_exactly_the_fields_the_design_names()
    {
        var time = OrNull(Gen.Int[-2, 2].Select(h => Two.AddHours(h)), 0.25);
        var request = Gen.Select(Gen.Bool, time, time, OrNull(Gen.Int[-2, 2].Select(h => Two.AddHours(h)), 0.5),
                Gen.OneOf(Gen.Int[-1, 2], Gen.Int[998, 1002], Gen.Int[2, 1000]), OrNull(Gen.Int[0, 102], 0.3))
            .Select((resource, start, end, delivery, attendees, title) => new BookingRequest
            {
                ResourceId = resource ? Thames : null,
                Start = start,
                End = end,
                DeliveryAt = delivery,
                Attendees = attendees,
                Title = title is { } n ? new string('a', n) : null,
            });

        request.Sample(r => Assert.Equal(Expected(r), Errors(r)), iter: 20_000);
    }
}
