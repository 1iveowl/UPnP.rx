namespace Sample.Dashboard.Client.Models;

/// <summary>
/// A service's SCPD content, fetched on demand when the user unfolds a service
/// in the UI (hub RPC <see cref="HubEvents.GetServiceDetail"/>).
/// </summary>
public sealed record ServiceDetailDto(
    string ServiceType,
    ActionDto[] Actions,
    StateVariableDto[] StateVariables,
    string? Error);

/// <summary>One SOAP action: name plus its in/out argument names.</summary>
public sealed record ActionDto(string Name, string[] InArguments, string[] OutArguments);

/// <summary>One state variable: name, UPnP data type and allowed values when declared.</summary>
public sealed record StateVariableDto(string Name, string? DataType, string[] AllowedValues);
