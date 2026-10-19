namespace SmartX.Gateway.Shared.DataStructures;

public record ManualCommand(Guid Id, string DeviceMac, string CommandType, string? Payload, DateTime IssuedAt);
