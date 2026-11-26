using CsCheck;
using ResourceBooking.Bookings;
using ResourceBooking.Data.Entities;

namespace ResourceBooking.Tests.Rules;

/// <summary>
/// The visibility table (design: month-view §1): a title goes only to its owner, and a holder's name goes only
/// to an admin looking at someone else's booking.
/// </summary>
public class BookingVisibilityTests
{
    private static readonly Guid Holder = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Other = Guid.Parse("00000000-0000-0000-0000-0000000000b2");

    private static Booking Held(string? title = "Board prep") =>
        new() { UserId = Holder, Title = title, User = new User { Id = Holder, Email = "ana@example.test", DisplayName = "Ana" } };

    [Fact]
    public void The_owner_sees_the_title_and_no_owner_name() =>
        Assert.Equal((true, "Board prep", (string?)null), BookingVisibility.Project(Held(), Holder, isAdmin: false));

    [Fact]
    public void An_admin_owner_sees_the_title_and_no_owner_name() =>
        Assert.Equal((true, "Board prep", (string?)null), BookingVisibility.Project(Held(), Holder, isAdmin: true));

    [Fact]
    public void An_admin_sees_someone_elses_name_and_no_title() =>
        Assert.Equal((false, (string?)null, "Ana"), BookingVisibility.Project(Held(), Other, isAdmin: true));

    [Fact]
    public void An_employee_sees_neither_name_nor_title_of_someone_elses_booking() =>
        Assert.Equal((false, (string?)null, (string?)null), BookingVisibility.Project(Held(), Other, isAdmin: false));

    [Fact]
    public void An_untitled_own_booking_has_no_title() =>
        Assert.Equal((true, (string?)null, (string?)null), BookingVisibility.Project(Held(title: null), Holder, isAdmin: false));

    [Fact]
    public void The_rule_holds_for_any_caller_owner_and_role()
    {
        var ids = Gen.Int[0, 2].Select(i => new Guid(i, 0, 0, new byte[8]));
        var title = Gen.OneOf(Gen.Const((string?)null), Gen.String[1, 20].Select(s => (string?)s));
        Gen.Select(ids, ids, Gen.Bool, title).Sample((owner, caller, isAdmin, t) =>
        {
            var booking = new Booking { UserId = owner, Title = t, User = new User { Id = owner, Email = "holder@example.test", DisplayName = "Holder" } };
            var (mine, shownTitle, shownOwner) = BookingVisibility.Project(booking, caller, isAdmin);

            var isOwner = owner == caller;
            Assert.Equal(isOwner, mine);
            Assert.Equal(isOwner ? t : null, shownTitle);
            Assert.Equal(isAdmin && !isOwner ? "Holder" : null, shownOwner);
        }, iter: 2_000);
    }
}
