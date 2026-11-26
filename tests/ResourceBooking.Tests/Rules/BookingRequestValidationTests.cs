using System.ComponentModel.DataAnnotations;
using ResourceBooking.Shared.Dtos;

namespace ResourceBooking.Tests.Rules;

/// <summary>The request shape the API refuses with 400 before any rule runs.</summary>
public class BookingRequestValidationTests
{
    private static readonly DateTime Two = new(2026, 11, 26, 14, 0, 0);

    private static List<string> Errors(BookingRequest request)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
        return results.SelectMany(r => r.MemberNames).ToList();
    }

    [Fact]
    public void A_room_booking_needs_a_start_and_an_end() =>
        Assert.Equal(["End", "Start"], Errors(new BookingRequest { ResourceId = Guid.NewGuid() }).Order());

    [Fact]
    public void A_catering_order_needs_only_a_delivery_time() =>
        Assert.Empty(Errors(new BookingRequest { ResourceId = Guid.NewGuid(), DeliveryAt = Two }));

    [Fact]
    public void The_end_must_be_after_the_start() =>
        Assert.Equal(["End"], Errors(new BookingRequest { ResourceId = Guid.NewGuid(), Start = Two, End = Two }));

    [Fact]
    public void Attendees_must_be_at_least_one() =>
        Assert.Equal(["Attendees"], Errors(new BookingRequest { ResourceId = Guid.NewGuid(), Start = Two, End = Two.AddHours(1), Attendees = 0 }));
}
