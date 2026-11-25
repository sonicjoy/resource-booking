using ResourceBooking.Shared;

namespace ResourceBooking.Data.Entities;

public class User
{
    public Guid Id { get; set; }
    public required string Email { get; set; }
    public required string DisplayName { get; set; }
    public Role Role { get; set; }
    public bool IsActive { get; set; }
}
