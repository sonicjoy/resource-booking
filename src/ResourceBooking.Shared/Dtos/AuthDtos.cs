namespace ResourceBooking.Shared.Dtos;

/// <summary>A user the mock sign-in page offers.</summary>
public sealed record SignInOption(Guid Id, string DisplayName, string Email, Role Role);

public sealed record SignInRequest(Guid UserId);

public sealed record CurrentUser(Guid Id, string DisplayName, string Email, Role Role);
