using System.ComponentModel.DataAnnotations;
using ResourceBooking.Bookings;

namespace ResourceBooking.Endpoints;

/// <summary>Shared request validation and error bodies for the API.</summary>
public static class ApiResults
{
    /// <summary>
    /// Runs the request's validation attributes and <see cref="IValidatableObject"/> rules; a failure is a
    /// 400 validation problem before the handler does anything else.
    /// </summary>
    public static IResult? Invalid(object request)
    {
        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true))
        {
            return null;
        }
        var errors = results
            .SelectMany(r => (r.MemberNames.Any() ? r.MemberNames : [""]).Select(member => (member, r.ErrorMessage ?? "Invalid")))
            .GroupBy(e => e.member)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Item2).ToArray());
        return Results.ValidationProblem(errors);
    }

    /// <summary>Problem Details carrying a machine-readable code, a message, and any rule violations.</summary>
    public static IResult Problem(ApiError error)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = error.Code, ["message"] = error.Message };
        if (error.Violations is { } violations)
        {
            extensions["violations"] = violations.Select(v => new { code = v.Code, message = v.Message }).ToList();
        }
        return Results.Problem(statusCode: error.Status, title: error.Message, extensions: extensions);
    }
}
