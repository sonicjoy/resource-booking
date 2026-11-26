namespace ResourceBooking.Shared;

/// <summary>Problem Details codes the client acts on, other than the rule codes in <see cref="RuleCodes"/>.</summary>
public static class ApiCodes
{
    /// <summary>409: the database refused an overlap; the client offers free alternatives.</summary>
    public const string Overlap = "OVERLAP";

    /// <summary>The IANA id of the company's time zone, the default for the server's Booking:TimeZone and the client's labels.</summary>
    public const string CompanyZone = "Europe/London";
}
