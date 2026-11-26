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
        var results = Validate(request);
        if (results.Count == 0)
        {
            return null;
        }
        var errors = results
            .SelectMany(r => (r.MemberNames.Any() ? r.MemberNames : [""]).Select(member => (member, r.ErrorMessage ?? "Invalid")))
            .GroupBy(e => e.member)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Item2).ToArray());
        return Results.ValidationProblem(errors);
    }

    /// <summary>
    /// Every validation error in the request. The framework's validator skips <see cref="IValidatableObject.Validate"/>
    /// when an attribute fails, so its rules run here as well and the client hears about every field at once.
    /// A request's own rules must therefore cope with fields its attributes would have refused (a null
    /// [Required] field, a value out of range): they run on whatever the client sent.
    /// </summary>
    public static List<ValidationResult> Validate(object request)
    {
        var context = new ValidationContext(request);
        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(request, context, results, validateAllProperties: true) && request is IValidatableObject rules)
        {
            static string Key(ValidationResult r) => $"{string.Join(",", r.MemberNames)}|{r.ErrorMessage}";
            var seen = results.Select(Key).ToHashSet();
            results.AddRange(rules.Validate(context).Where(r => seen.Add(Key(r))));
        }
        return results;
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
