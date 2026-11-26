using Microsoft.EntityFrameworkCore;
using Npgsql;
using ResourceBooking.Bookings;
using ResourceBooking.Data;
using ResourceBooking.Data.Entities;
using ResourceBooking.Shared;
using ResourceBooking.Shared.Dtos;

namespace ResourceBooking.Admin;

/// <summary>Either a value or the reason there isn't one.</summary>
public sealed record AdminResult<T>(T? Value, ApiError? Error)
{
    public static implicit operator AdminResult<T>(T value) => new(value, null);
    public static implicit operator AdminResult<T>(ApiError error) => new(default, error);
}

/// <summary>
/// Adds, edits and deactivates users. Anything that could remove the last active admin locks the target and
/// every active admin row (FOR NO KEY UPDATE, in id order) first, so two admins removing each other run one
/// after the other and the second sees the first's change.
/// </summary>
public sealed class UserAdmin(AppDb db, BookingService bookings)
{
    private const string UniqueViolation = "23505";

    public static readonly ApiError LastAdmin = new(StatusCodes.Status422UnprocessableEntity, "LAST_ADMIN",
        "There must be at least one active admin");

    public async Task<AdminResult<User>> AddAsync(UserRequest request, CancellationToken ct)
    {
        var user = new User
        {
            Id = Guid.CreateVersion7(),
            Email = Normalise(request.Email),
            DisplayName = request.DisplayName.Trim(),
            Role = request.Role ?? Role.Employee,
            IsActive = true,
        };
        db.Users.Add(user);
        return await SaveAsync(user, ct);
    }

    public async Task<AdminResult<User>> UpdateAsync(Guid id, UserRequest request, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var locked = await LockWithAdminsAsync(id, ct);
        if (locked.SingleOrDefault(u => u.Id == id) is not { } user)
        {
            return ApiError.NotFound("No such user");
        }
        var role = request.Role ?? user.Role;
        if (user is { Role: Role.Admin, IsActive: true } && role != Role.Admin && ActiveAdmins(locked) <= 1)
        {
            return LastAdmin;
        }
        user.Email = Normalise(request.Email);
        user.DisplayName = request.DisplayName.Trim();
        user.Role = role;
        var result = await SaveAsync(user, ct);
        if (result.Error is null)
        {
            await transaction.CommitAsync(ct);
        }
        return result;
    }

    /// <summary>Clears is_active and cancels the user's future bookings; the owner gets no notice, being unable to sign in.</summary>
    public async Task<AdminResult<CascadeResult>> DeactivateAsync(Guid id, Guid actorId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var locked = await LockWithAdminsAsync(id, ct);
        if (locked.SingleOrDefault(u => u.Id == id) is not { } user)
        {
            return ApiError.NotFound("No such user");
        }
        if (!user.IsActive)
        {
            return new CascadeResult(0);
        }
        if (user.Role == Role.Admin && ActiveAdmins(locked) <= 1)
        {
            return LastAdmin;
        }
        user.IsActive = false;
        await db.SaveChangesAsync(ct);
        var cancelled = await bookings.CancelFutureBookingsAsync(b => b.UserId == id, $"{user.DisplayName} no longer has access", actorId, ct);
        await transaction.CommitAsync(ct);
        return new CascadeResult(cancelled);
    }

    private async Task<List<User>> LockWithAdminsAsync(Guid id, CancellationToken ct) =>
        await db.Users.FromSql($"SELECT * FROM users WHERE id = {id} OR (role = 'Admin' AND is_active) ORDER BY id FOR NO KEY UPDATE")
            .ToListAsync(ct);

    private static int ActiveAdmins(IEnumerable<User> users) => users.Count(u => u is { Role: Role.Admin, IsActive: true });

    private static string Normalise(string email) => email.Trim().ToLowerInvariant();

    private async Task<AdminResult<User>> SaveAsync(User user, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return user;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            return new ApiError(StatusCodes.Status409Conflict, "DUPLICATE", $"{user.Email} is already in use");
        }
    }
}
