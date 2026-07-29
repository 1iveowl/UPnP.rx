using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;

namespace UPnP.Rx.Analyzers.Tests;

/// <summary>
/// The analyzer suite's shared vocabulary: the verifier wiring, and the source stub of
/// the API under test.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a stub rather than the real library.</b> Test compilations are built against
/// <see cref="ReferenceAssemblies"/>, which tops out at .NET 9 - so a net10.0 library
/// cannot be referenced from one at all. The stub is the only way to give the analyzer
/// something to bind against.
/// </para>
/// <para>
/// A stub can drift from the thing it stands for, silently, and then the tests prove
/// nothing about the shipped API. <see cref="StubGuardTests"/> is the answer: it asserts
/// the stub's shapes against the real types by reflection, so drift is a red build.
/// </para>
/// </remarks>
internal static class TestKit
{
    /// <summary>
    /// The subset of <c>UPnP.Rx.PortMapping</c> the rules bind against. Deliberately
    /// minimal - every member here is one <see cref="StubGuardTests"/> has to keep honest.
    /// </summary>
    public const string PortMappingStub = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;

        namespace UPnP.Rx.PortMapping
        {
            public enum Protocol { Tcp, Udp }

            public static class LeaseDurations
            {
                public static TimeSpan Maximum { get; } = TimeSpan.FromSeconds(604800);
                public static TimeSpan Indefinite => TimeSpan.Zero;
            }

            public sealed record PortMappingEntry
            {
                public required ushort ExternalPort { get; init; }
                public required ushort? InternalPort { get; init; }
                public required Protocol Protocol { get; init; }
                public TimeSpan? LeaseDuration { get; init; }
            }

            public interface IPortMappingLease : IAsyncDisposable, IDisposable
            {
                PortMappingEntry Mapping { get; }
            }

            public interface IInternetGateway
            {
                Task<IPortMappingLease> AddPortMappingAsync(
                    ushort externalPort, ushort internalPort, Protocol protocol,
                    string description, TimeSpan lease, System.Net.IPAddress? internalClient = null,
                    CancellationToken ct = default);

                Task<IPortMappingLease> AddAnyPortMappingAsync(
                    ushort externalPort, ushort internalPort, Protocol protocol,
                    string description, TimeSpan lease, System.Net.IPAddress? internalClient = null,
                    CancellationToken ct = default);
            }

            public static class PortMapper
            {
                public static Task<IPortMappingLease> AddPortMappingAsync(
                    ushort externalPort, ushort internalPort, Protocol protocol,
                    string description, TimeSpan lease, CancellationToken ct = default) => null!;
            }
        }
        """;

    /// <summary>
    /// A type in a namespace that merely looks like ours, for asserting the rules bind on
    /// the real symbol rather than on a matching name.
    /// </summary>
    public const string LookalikeStub = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;

        namespace SomeoneElse.PortMapping
        {
            public enum Protocol { Tcp, Udp }

            public sealed record PortMappingEntry
            {
                public TimeSpan? LeaseDuration { get; init; }
            }

            public interface IInternetGateway
            {
                Task AddPortMappingAsync(
                    ushort externalPort, ushort internalPort, Protocol protocol,
                    string description, TimeSpan lease, CancellationToken ct = default);
            }
        }
        """;

    /// <summary>Runs an analyzer over sources, expecting exactly the diagnostics the markup declares.</summary>
    /// <remarks>
    /// Expected locations come from <c>{|RULEID:code|}</c> markup in the source rather than
    /// hand-computed line/column spans, which rot the moment a test's preamble changes.
    /// </remarks>
    public static Task VerifyAsync<TAnalyzer>(string source, params string[] additionalSources)
        where TAnalyzer : DiagnosticAnalyzer, new()
    {
        var test = new Test<TAnalyzer> { TestCode = source };

        foreach (var additional in additionalSources)
        {
            test.TestState.Sources.Add(additional);
        }

        return test.RunAsync(Xunit.TestContext.Current.CancellationToken);
    }

    /// <summary>Runs an analyzer and its code fix, expecting <paramref name="fixedSource"/> afterwards.</summary>
    public static Task VerifyFixAsync<TAnalyzer, TCodeFix>(
        string source,
        string fixedSource,
        int? codeActionIndex = null,
        params string[] additionalSources)
        where TAnalyzer : DiagnosticAnalyzer, new()
        where TCodeFix : CodeFixProvider, new()
    {
        var test = new FixTest<TAnalyzer, TCodeFix>
        {
            TestCode = source,
            FixedCode = fixedSource,
            CodeActionIndex = codeActionIndex
        };

        foreach (var additional in additionalSources)
        {
            test.TestState.Sources.Add(additional);
            test.FixedState.Sources.Add(additional);
        }

        return test.RunAsync(Xunit.TestContext.Current.CancellationToken);
    }

    private sealed class Test<TAnalyzer> : CSharpAnalyzerTest<TAnalyzer, DefaultVerifier>
        where TAnalyzer : DiagnosticAnalyzer, new()
    {
        public Test() => ReferenceAssemblies = Net90;
    }

    private sealed class FixTest<TAnalyzer, TCodeFix> : CSharpCodeFixTest<TAnalyzer, TCodeFix, DefaultVerifier>
        where TAnalyzer : DiagnosticAnalyzer, new()
        where TCodeFix : CodeFixProvider, new()
    {
        public FixTest() => ReferenceAssemblies = Net90;
    }

    /// <summary>
    /// The newest reference assemblies the testing framework offers. A net10.0 library
    /// cannot be referenced from a test compilation at all, which is what forces the stub.
    /// </summary>
    private static ReferenceAssemblies Net90 => ReferenceAssemblies.Net.Net90;

    /// <summary>Wraps a snippet in a method body so tests can be one expression long.</summary>
    public static string InMethod(string body) => $$"""
        using System;
        using System.Threading.Tasks;
        using UPnP.Rx.PortMapping;

        class Consumer
        {
            async Task Run(IInternetGateway gateway)
            {
        {{body}}
            }
        }
        """;

    /// <summary>Suppresses the compiler diagnostics a snippet-shaped test provokes but does not care about.</summary>
    public static ImmutableArray<string> IrrelevantCompilerDiagnostics { get; } =
        ImmutableArray.Create("CS1998", "CS0219", "CS0168");
}
