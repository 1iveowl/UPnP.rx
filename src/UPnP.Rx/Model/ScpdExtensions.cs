using System.Collections.ObjectModel;
using System.Globalization;

namespace UPnP.Rx.Model;

/// <summary>
/// SCPD-driven argument marshalling (UDA 2.0 §3.2.1): validates a set of
/// in-arguments against an action's declaration and returns them in SCPD order,
/// ready for <see cref="UpnpService.InvokeAsync"/>. Pure and total — problems
/// are values, never exceptions.
/// </summary>
public static class ScpdExtensions
{
    /// <summary>Extension members for <see cref="Scpd"/>.</summary>
    extension(Scpd scpd)
    {
        /// <summary>
        /// Validates <paramref name="arguments"/> against the named action: every
        /// declared in-argument must be present (empty string for wildcards), no
        /// unknown arguments, and each value must satisfy its related state
        /// variable's data type, <c>allowedValueList</c> and <c>allowedValueRange</c>
        /// where declared. On success the returned dictionary enumerates in SCPD
        /// declaration order — pass it to <see cref="UpnpService.InvokeAsync"/> as-is.
        /// </summary>
        /// <param name="actionName">The action to marshal for.</param>
        /// <param name="arguments">The in-arguments by name; case-insensitive lookup.</param>
        /// <returns>The ordered, validated arguments, or a failure listing every violation.</returns>
        /// <exception cref="ArgumentNullException">The receiver is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="actionName"/> is null or whitespace.</exception>
        public ParseResult<IReadOnlyDictionary<string, string>> ValidateAndOrderArguments(
            string actionName,
            IReadOnlyDictionary<string, string>? arguments = null)
        {
            ArgumentNullException.ThrowIfNull(scpd);
            ArgumentException.ThrowIfNullOrWhiteSpace(actionName);

            var action = scpd.Actions.FirstOrDefault(a =>
                string.Equals(a.Name, actionName, StringComparison.OrdinalIgnoreCase));

            if (action is null)
            {
                return ParseResult<IReadOnlyDictionary<string, string>>.Failure(
                    $"The SCPD declares no action named '{actionName}'.");
            }

            var provided = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (name, value) in arguments ?? ReadOnlyDictionary<string, string>.Empty)
            {
                provided[name] = value;
            }

            // Unknown direction counts as "in" (leniency toward sloppy SCPDs); the
            // strictness here is toward what WE send, per the house policy.
            var inArguments = action.Arguments
                .Where(a => a.Direction != ArgumentDirection.Out && a.Name is not null)
                .ToList();

            var errors = new List<string>();
            var ordered = new Dictionary<string, string>();

            foreach (var argument in inArguments)
            {
                if (!provided.Remove(argument.Name!, out var value))
                {
                    errors.Add($"Missing in-argument '{argument.Name}' (UDA 2.0 requires every in-argument; use \"\" for wildcards).");
                    continue;
                }

                var stateVariable = scpd.StateVariables.FirstOrDefault(v =>
                    string.Equals(v.Name, argument.RelatedStateVariable, StringComparison.OrdinalIgnoreCase));

                if (stateVariable is not null && Violation(stateVariable, argument.Name!, value) is { } violation)
                {
                    errors.Add(violation);
                    continue;
                }

                ordered[argument.Name!] = value;
            }

            errors.AddRange(provided.Keys.Select(unknown =>
                $"'{unknown}' is not an in-argument of {action.Name}."));

            return errors.Count > 0
                ? ParseResult<IReadOnlyDictionary<string, string>>.Failure(string.Join(" ", errors))
                : ParseResult<IReadOnlyDictionary<string, string>>.Success(ordered);
        }
    }

    /// <summary>The violation message, or null when <paramref name="value"/> satisfies the variable's constraints.</summary>
    private static string? Violation(StateVariable variable, string argumentName, string value)
    {
        if (!SatisfiesDataType(variable.DataType, value))
        {
            return $"'{argumentName}' value '{value}' is not a valid {variable.DataType}.";
        }

        if (variable.AllowedValues.Count > 0
            && !variable.AllowedValues.Contains(value, StringComparer.Ordinal))
        {
            return $"'{argumentName}' value '{value}' is not in the allowed value list ({string.Join("|", variable.AllowedValues)}).";
        }

        if (variable.AllowedRange is { } range
            && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
        {
            if (decimal.TryParse(range.Minimum, NumberStyles.Number, CultureInfo.InvariantCulture, out var min)
                && number < min)
            {
                return $"'{argumentName}' value {number} is below the allowed minimum {min}.";
            }

            if (decimal.TryParse(range.Maximum, NumberStyles.Number, CultureInfo.InvariantCulture, out var max)
                && number > max)
            {
                return $"'{argumentName}' value {number} is above the allowed maximum {max}.";
            }
        }

        return null;
    }

    private static bool SatisfiesDataType(string? dataType, string value) =>
        dataType?.ToLowerInvariant() switch
        {
            "ui1" => byte.TryParse(value, out _),
            "ui2" => ushort.TryParse(value, out _),
            "ui4" => uint.TryParse(value, out _),
            "i1" => sbyte.TryParse(value, out _),
            "i2" => short.TryParse(value, out _),
            "i4" or "int" => int.TryParse(value, out _),
            "boolean" => value is "0" or "1"
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("false", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || value.Equals("no", StringComparison.OrdinalIgnoreCase),
            // string, uri, uuid, bin.*, dates, floats, unknown or absent types:
            // no structural check — leniency toward the document.
            _ => true
        };
}
