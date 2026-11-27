using System.Globalization;
using Microsoft.AspNetCore.Components;
using ResourceBooking.Shared;

namespace ResourceBooking.Client.Services;

/// <summary>
/// What Book and Month share: the tab id for each resource type, reading a type from the address, the link from a
/// month day to Book, and telling a real tab change from one Fluent reports while a page is being removed.
/// </summary>
public static class TypeTabs
{
    public static string Of(ResourceType type) => type switch
    {
        ResourceType.Car => "cars",
        ResourceType.Catering => "catering",
        _ => "rooms",
    };

    public static ResourceType TypeOf(string? tab) => tab switch
    {
        "cars" => ResourceType.Car,
        "catering" => ResourceType.Catering,
        _ => ResourceType.MeetingRoom,
    };

    /// <summary>A type named in the address, or meeting rooms for anything else.</summary>
    public static ResourceType Parse(string? query) =>
        Enum.TryParse<ResourceType>(query, out var type) && Enum.IsDefined(type) ? type : ResourceType.MeetingRoom;

    public static string BookLink(DateOnly date, ResourceType type) =>
        string.Create(CultureInfo.InvariantCulture, $"book?date={date:yyyy-MM-dd}&type={type}");

    /// <summary>True while the address is still the given page ("book" for Book; "" or "month" for Month).</summary>
    public static bool IsOn(NavigationManager navigation, string page) =>
        navigation.ToBaseRelativePath(navigation.Uri).Split('?', '#')[0] == page;
}
