using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResourceBooking.Shared;

// Every enum travels as its name in JSON ("Admin", not 1), on the server and in the client.

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

/// <summary>
/// Writes an enum as its name, and reads only the exact name of a defined member:
/// numbers, unknown names and comma-joined flag values are a JSON error (400).
/// </summary>
public sealed class NameOnlyEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var name = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        if (name is not null && Enum.GetNames<T>().Contains(name))
        {
            return Enum.Parse<T>(name);
        }
        throw new JsonException($"'{name ?? reader.TokenType.ToString()}' is not a {typeof(T).Name}.");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
