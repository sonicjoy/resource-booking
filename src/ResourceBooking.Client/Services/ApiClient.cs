using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using ResourceBooking.Shared;
using ResourceBooking.Shared.Dtos;

namespace ResourceBooking.Client.Services;

/// <summary>
/// A refusal from the API, already in words: Problem Details turned into one message (each rule's reason for
/// a rule refusal), the reasons, and any field errors.
/// </summary>
public sealed record ApiProblem(int Status, string? Code, string Message, IReadOnlyList<string> Reasons, IReadOnlyDictionary<string, string[]> FieldErrors)
{
    public static readonly ApiProblem Unreachable = new(0, null, "The server couldn't be reached. Try again.", [], new Dictionary<string, string[]>());

    public string? FieldError(string field) => FieldErrors.TryGetValue(field, out var messages) ? string.Join(" ", messages) : null;
}

public sealed record ApiResult<T>(T? Value, ApiProblem? Problem)
{
    public bool Ok => Problem is null;
}

/// <summary>The app's API as typed calls. Times go out as company-local wall-clock times with no offset.</summary>
public sealed class ApiClient(HttpClient http)
{
    private static string Local(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);

    public Task<ApiResult<AvailabilityDay>> AvailabilityAsync(DateOnly date, ResourceType type) =>
        GetAsync<AvailabilityDay>(string.Create(CultureInfo.InvariantCulture, $"api/availability?date={date:yyyy-MM-dd}&type={type}"));

    public Task<ApiResult<List<ResourceDto>>> ResourcesAsync() => GetAsync<List<ResourceDto>>("api/resources");

    public Task<ApiResult<List<ResourceDto>>> FreeAsync(ResourceType type, DateTime start, DateTime end, int attendees) =>
        GetAsync<List<ResourceDto>>(string.Create(CultureInfo.InvariantCulture,
            $"api/resources/free?type={type}&start={Local(start)}&end={Local(end)}&attendees={attendees}"));

    public Task<ApiResult<List<BookingDto>>> MineAsync() => GetAsync<List<BookingDto>>("api/bookings/mine");

    public Task<ApiResult<BookingDto>> CreateAsync(BookingRequest request) => SendAsync<BookingDto>(HttpMethod.Post, "api/bookings", request);

    public Task<ApiResult<BookingDto>> UpdateAsync(Guid id, BookingRequest request) => SendAsync<BookingDto>(HttpMethod.Put, $"api/bookings/{id}", request);

    public Task<ApiResult<bool>> CancelAsync(Guid id) => SendAsync<bool>(HttpMethod.Delete, $"api/bookings/{id}", null);

    public Task<ApiResult<List<NoticeDto>>> NoticesAsync() => GetAsync<List<NoticeDto>>("api/notices");

    public Task<ApiResult<bool>> MarkReadAsync(Guid id) => SendAsync<bool>(HttpMethod.Post, $"api/notices/{id}/read", null);

    public Task<ApiResult<bool>> SignOutAsync() => SendAsync<bool>(HttpMethod.Post, "api/auth/sign-out", null);

    private Task<ApiResult<T>> GetAsync<T>(string url) => SendAsync<T>(HttpMethod.Get, url, null);

    private async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string url, object? body)
    {
        try
        {
            using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                return new(default, await ProblemAsync(response));
            }
            if (typeof(T) == typeof(bool))
            {
                return new((T)(object)true, null);
            }
            return new(await response.Content.ReadFromJsonAsync<T>(), null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return new(default, ApiProblem.Unreachable);
        }
    }

    private static async Task<ApiProblem> ProblemAsync(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        var fallback = status switch
        {
            401 => "Your session has ended. Sign in again.",
            403 => "You don't have access to that.",
            404 => "That no longer exists.",
            429 => "Too many tries. Wait a moment and try again.",
            _ => "Something went wrong. Try again.",
        };
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var code = root.TryGetProperty("code", out var c) ? c.GetString() : null;
            var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
            List<string> reasons = root.TryGetProperty("violations", out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Select(x => x.GetProperty("message").GetString() ?? "").Where(s => s != "").ToList()
                : [];
            var fields = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Object)
            {
                foreach (var field in e.EnumerateObject())
                {
                    fields[field.Name] = field.Value.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
                }
            }
            var summary = reasons.Count > 0 ? string.Join(" ", reasons) : message ?? (fields.Count > 0 ? "Check the fields marked below." : fallback);
            return new ApiProblem(status, code, summary, reasons, fields);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            // A body that isn't the Problem Details we expect, or that couldn't be read: say what the status means.
            return new ApiProblem(status, null, fallback, [], new Dictionary<string, string[]>());
        }
    }
}
