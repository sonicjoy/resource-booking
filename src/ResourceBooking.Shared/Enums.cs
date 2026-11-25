using System.Text.Json.Serialization;

namespace ResourceBooking.Shared;

// Every enum travels as its name in JSON ("Admin", not 1), on the server and in the client.

[JsonConverter(typeof(JsonStringEnumConverter<ResourceType>))]
public enum ResourceType { MeetingRoom, Car, Catering }

[JsonConverter(typeof(JsonStringEnumConverter<Role>))]
public enum Role { Employee, Admin }

[JsonConverter(typeof(JsonStringEnumConverter<BookingStatus>))]
public enum BookingStatus { Confirmed, Cancelled }

[JsonConverter(typeof(JsonStringEnumConverter<StepKind>))]
public enum StepKind { UserMessage, ModelCall, ToolCall, Reply }

[JsonConverter(typeof(JsonStringEnumConverter<ProposalKind>))]
public enum ProposalKind { Create, Change, Cancel }

[JsonConverter(typeof(JsonStringEnumConverter<ProposalStatus>))]
public enum ProposalStatus { Open, Confirmed, Superseded }
