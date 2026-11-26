using System.Text.Json.Serialization;

namespace ResourceBooking.Shared;

// Every enum travels as its name in JSON ("Admin", not 1), on the server and in the client,
// and a number is refused on the way in.

[JsonConverter(typeof(NameOnlyEnumConverter<ResourceType>))]
public enum ResourceType { MeetingRoom, Car, Catering }

[JsonConverter(typeof(NameOnlyEnumConverter<Role>))]
public enum Role { Employee, Admin }

[JsonConverter(typeof(NameOnlyEnumConverter<BookingStatus>))]
public enum BookingStatus { Confirmed, Cancelled }

[JsonConverter(typeof(NameOnlyEnumConverter<StepKind>))]
public enum StepKind { UserMessage, ModelCall, ToolCall, Reply }

[JsonConverter(typeof(NameOnlyEnumConverter<ProposalKind>))]
public enum ProposalKind { Create, Change, Cancel }

[JsonConverter(typeof(NameOnlyEnumConverter<ProposalStatus>))]
public enum ProposalStatus { Open, Confirmed, Superseded }

/// <summary>Reads and writes enum names only; a number in a request is a JSON error (400).</summary>
public sealed class NameOnlyEnumConverter<T>() : JsonStringEnumConverter<T>(namingPolicy: null, allowIntegerValues: false)
    where T : struct, Enum;
