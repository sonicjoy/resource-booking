namespace ResourceBooking.Shared;

public enum ResourceType { MeetingRoom, Car, Catering }

public enum Role { Employee, Admin }

public enum BookingStatus { Confirmed, Cancelled }

public enum StepKind { UserMessage, ModelCall, ToolCall, Reply }

public enum ProposalKind { Create, Change, Cancel }

public enum ProposalStatus { Open, Confirmed, Superseded }
