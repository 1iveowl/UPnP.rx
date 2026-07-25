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

/// <summary>One SOAP action: name, in-arguments with their input metadata, out-argument names.</summary>
public sealed record ActionDto(string Name, ArgumentDto[] InArguments, string[] OutArguments);

/// <summary>
/// One in-argument with what the SCPD knows about its related state variable -
/// enough to build a sensible input (dropdown for allowed values, hints for
/// ranges) and to prefill defaults.
/// </summary>
public sealed record ArgumentDto(
    string Name,
    string? DataType,
    string[] AllowedValues,
    string? Minimum,
    string? Maximum,
    string? DefaultValue);

/// <summary>One state variable: name, UPnP data type and allowed values when declared.</summary>
public sealed record StateVariableDto(string Name, string? DataType, string[] AllowedValues);

/// <summary>An action invocation's outcome: the out-arguments, or the failure in the device's own words.</summary>
public sealed record InvokeResultDto(OutValueDto[] Out, string? Error);

/// <summary>One out-argument of an invoked action.</summary>
public sealed record OutValueDto(string Name, string Value);
