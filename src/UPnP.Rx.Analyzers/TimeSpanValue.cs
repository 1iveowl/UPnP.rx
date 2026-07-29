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
    private static TimeSpan? FromFactory(IInvocationOperation invocation)
    {
        if (!IsTimeSpan(invocation.TargetMethod.ContainingType)
            || !invocation.TargetMethod.IsStatic
            || invocation.Arguments.Length != 1
            || invocation.Arguments[0].Value.ConstantValue is not { HasValue: true, Value: { } raw })
        {
            return null;
        }

        // A double is what every From* overload ultimately takes; an int literal arrives
        // as int and must be widened rather than rejected.
        if (!TryToDouble(raw, out var amount))
        {
            return null;
        }

        try
        {
            return invocation.TargetMethod.Name switch
            {
                "FromDays" => TimeSpan.FromDays(amount),
                "FromHours" => TimeSpan.FromHours(amount),
                "FromMinutes" => TimeSpan.FromMinutes(amount),
                "FromSeconds" => TimeSpan.FromSeconds(amount),
                "FromMilliseconds" => TimeSpan.FromMilliseconds(amount),
                "FromTicks" => TimeSpan.FromTicks((long)amount),
                _ => null
            };
        }
        catch (OverflowException)
        {
            // TimeSpan.FromDays(double.MaxValue) and similar. The code is wrong, but this
            // rule is not the one to report it, and an analyzer must never throw.
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

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
