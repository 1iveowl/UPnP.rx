using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace UPnP.Rx.Analyzers;

/// <summary>
/// Reads a <see cref="TimeSpan"/> out of source, when and only when source says exactly
/// what it is.
/// </summary>
/// <remarks>
/// The whole false-positive budget lives here. A <see cref="TimeSpan"/> is not a C#
/// constant - <c>TimeSpan.FromSeconds(90)</c> is a method call, so Roslyn folds nothing -
/// which means recognising the factory forms by hand is the only way to see a value at
/// build time. Anything not recognised returns <see langword="null"/> and is left alone:
/// a missed diagnostic costs nothing, a wrong one teaches people to suppress the rule.
/// </remarks>
internal static class TimeSpanValue
{
    /// <summary>The value this operation denotes, or null when source does not say.</summary>
    internal static TimeSpan? TryRead(IOperation? operation)
    {
        // Casts and parentheses are noise between us and the value.
        while (operation is IConversionOperation { IsImplicit: true } conversion)
        {
            operation = conversion.Operand;
        }

        return operation switch
        {
            null => null,
            IFieldReferenceOperation field => FromField(field),
            IInvocationOperation invocation => FromFactory(invocation),
            IObjectCreationOperation creation => FromConstructor(creation),
            _ => null
        };
    }

    /// <summary><c>TimeSpan.Zero</c> and friends, plus any user constant of TimeSpan type.</summary>
    private static TimeSpan? FromField(IFieldReferenceOperation field)
    {
        if (!IsTimeSpan(field.Field.ContainingType))
        {
            // A const/static readonly TimeSpan elsewhere: only readable when the compiler
            // folded it, which for TimeSpan it does not. Deliberately gives up.
            return null;
        }

        return field.Field.Name switch
        {
            "Zero" => TimeSpan.Zero,
            "MaxValue" => TimeSpan.MaxValue,
            "MinValue" => TimeSpan.MinValue,
            _ => null
        };
    }

    /// <summary><c>TimeSpan.FromSeconds(90)</c> and the rest of the factory family.</summary>
    /// <remarks>
    /// Summed by PARAMETER NAME rather than read from a single argument, because .NET 8 added
    /// integer overloads with defaulted trailing parameters and they change the shape without
    /// changing the source. Measured: <c>TimeSpan.FromMilliseconds(500)</c> binds to
    /// <c>FromMilliseconds(long milliseconds, long microseconds = 0)</c> and arrives as
    /// <em>two</em> arguments - so a rule that insisted on exactly one silently stopped seeing
    /// it. The <c>double</c> overloads name their parameter <c>value</c>, in which case the
    /// unit comes from the method name instead.
    /// </remarks>
    private static TimeSpan? FromFactory(IInvocationOperation invocation)
    {
        if (!IsTimeSpan(invocation.TargetMethod.ContainingType)
            || !invocation.TargetMethod.IsStatic
            || invocation.Arguments.Length == 0
            || TicksPerUnitOfMethod(invocation.TargetMethod.Name) is not { } methodUnit)
        {
            return null;
        }

        double ticks = 0;

        foreach (var argument in invocation.Arguments)
        {
            if (argument.Value.ConstantValue is not { HasValue: true, Value: { } raw }
                || !TryToDouble(raw, out var amount))
            {
                // One unreadable component makes the whole value unreadable. Partial
                // arithmetic here would be a guess, and guesses are what the budget forbids.
                return null;
            }

            if (TicksPerUnitOfParameter(argument.Parameter?.Name) is not { } unit)
            {
                // A parameter named "value" (the double overloads) takes the method's unit;
                // anything else unrecognised means this is not a shape we understand.
                if (argument.Parameter?.Name != "value")
                {
                    return null;
                }

                unit = methodUnit;
            }

            ticks += amount * unit;
        }

        // TimeSpan.MinValue/MaxValue in ticks. Outside this the constructor throws, and an
        // analyzer must never throw - the code is wrong, but not this rule's business.
        return ticks is >= -9_223_372_036_854_775_808.0 and <= 9_223_372_036_854_775_807.0
            ? TimeSpan.FromTicks((long)ticks)
            : null;
    }

    /// <summary>The unit a factory method measures in, as ticks.</summary>
    private static double? TicksPerUnitOfMethod(string name) => name switch
    {
        "FromDays" => TimeSpan.TicksPerDay,
        "FromHours" => TimeSpan.TicksPerHour,
        "FromMinutes" => TimeSpan.TicksPerMinute,
        "FromSeconds" => TimeSpan.TicksPerSecond,
        "FromMilliseconds" => TimeSpan.TicksPerMillisecond,
        "FromMicroseconds" => _ticksPerMicrosecond,
        "FromTicks" => 1,
        _ => null
    };

    /// <summary>The unit a factory parameter measures in, by its name, as ticks.</summary>
    private static double? TicksPerUnitOfParameter(string? name) => name switch
    {
        "days" => TimeSpan.TicksPerDay,
        "hours" => TimeSpan.TicksPerHour,
        "minutes" => TimeSpan.TicksPerMinute,
        "seconds" => TimeSpan.TicksPerSecond,
        "milliseconds" => TimeSpan.TicksPerMillisecond,
        "microseconds" => _ticksPerMicrosecond,
        "ticks" => 1,
        _ => null
    };

    /// <summary><c>TimeSpan.TicksPerMicrosecond</c> is newer than netstandard2.0.</summary>
    private const double _ticksPerMicrosecond = 10;

    /// <summary><c>new TimeSpan(0, 30, 0)</c> - the constructor forms, all-constant only.</summary>
    private static TimeSpan? FromConstructor(IObjectCreationOperation creation)
    {
        if (!IsTimeSpan(creation.Type))
        {
            return null;
        }

        var parts = new long[creation.Arguments.Length];

        for (var i = 0; i < creation.Arguments.Length; i++)
        {
            if (creation.Arguments[i].Value.ConstantValue is not { HasValue: true, Value: { } raw }
                || !TryToDouble(raw, out var value))
            {
                return null;
            }

            parts[i] = (long)value;
        }

        try
        {
            return parts.Length switch
            {
                1 => TimeSpan.FromTicks(parts[0]),
                3 => new TimeSpan((int)parts[0], (int)parts[1], (int)parts[2]),
                4 => new TimeSpan((int)parts[0], (int)parts[1], (int)parts[2], (int)parts[3]),
                5 => new TimeSpan((int)parts[0], (int)parts[1], (int)parts[2], (int)parts[3], (int)parts[4]),
                _ => null
            };
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool TryToDouble(object raw, out double value)
    {
        switch (raw)
        {
            case double d: value = d; return true;
            case float f: value = f; return true;
            case int i: value = i; return true;
            case long l: value = l; return true;
            case short s: value = s; return true;
            case byte b: value = b; return true;
            case decimal m: value = (double)m; return true;
            default: value = 0; return false;
        }
    }

    private static bool IsTimeSpan(ITypeSymbol? type) =>
        type is { Name: "TimeSpan", ContainingNamespace.Name: "System" }
        && type.ContainingNamespace.ContainingNamespace is { IsGlobalNamespace: true };
}
