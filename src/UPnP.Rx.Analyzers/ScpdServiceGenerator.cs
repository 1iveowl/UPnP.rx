using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace UPnP.Rx.Analyzers;

/// <summary>
/// Turns a checked-in SCPD document into a typed wrapper over
/// <c>IUpnpService.InvokeAsync</c> - one method per action, one record per result.
/// </summary>
/// <remarks>
/// <para>
/// It emits a façade, not a second protocol implementation: every generated method composes
/// the same argument dictionary and calls the same <c>InvokeAsync</c> a hand-written wrapper
/// would. What it removes is the hand-maintained mapping between wire names and code, which
/// is the part that can be silently wrong.
/// </para>
/// <para>
/// The pipeline carries only <see cref="ScpdDocument"/> and small strings - no
/// <c>ISymbol</c>, no <c>Compilation</c>, no syntax nodes - so incremental caching works and
/// compilations are not kept alive. <c>CacheabilityTests</c> asserts that rather than
/// trusting it.
/// </para>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class ScpdServiceGenerator : IIncrementalGenerator
{
    private const string _attribute = "UPnP.Rx.Model.ScpdServiceAttribute";

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // The SCPD documents, keyed by file name and projected to the equatable model
        // immediately - AdditionalText itself is not a good cache key.
        var documents = context.AdditionalTextsProvider
            .Where(text => text.Path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .Select((text, ct) =>
            {
                var name = System.IO.Path.GetFileName(text.Path);
                var content = text.GetText(ct)?.ToString();

                return content is null
                    ? default
                    : new DocumentEntry(name, ScpdReader.Read(content, ServiceNameFrom(name)));
            })
            .Where(entry => entry.Document is not null)
            .Collect();

        // The partial classes asking for one.
        var targets = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                _attribute,
                predicate: static (node, _) => node is Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax,
                transform: static (ctx, _) =>
                {
                    var symbol = (INamedTypeSymbol)ctx.TargetSymbol;

                    var fileName = ctx.Attributes
                        .SelectMany(a => a.ConstructorArguments)
                        .Select(a => a.Value as string)
                        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

                    return new Target(
                        Namespace: symbol.ContainingNamespace.IsGlobalNamespace
                            ? null
                            : symbol.ContainingNamespace.ToDisplayString(),
                        TypeName: symbol.Name,
                        Accessibility: symbol.DeclaredAccessibility == Microsoft.CodeAnalysis.Accessibility.Public
                            ? "public"
                            : "internal",
                        ScpdFileName: fileName ?? string.Empty);
                })
            .Collect();

        context.RegisterSourceOutput(targets.Combine(documents), static (production, pair) =>
        {
            var (allTargets, allDocuments) = pair;

            foreach (var target in allTargets)
            {
                var match = allDocuments.FirstOrDefault(d =>
                    string.Equals(d.FileName, target.ScpdFileName, StringComparison.OrdinalIgnoreCase));

                if (match.Document is not { } document)
                {
                    production.ReportDiagnostic(Diagnostic.Create(
                        _missingDocument,
                        Location.None,
                        target.TypeName,
                        target.ScpdFileName));

                    continue;
                }

                production.AddSource(
                    $"{target.TypeName}.Scpd.g.cs",
                    SourceText.From(Emit(target, document), Encoding.UTF8));
            }
        });
    }

    private static readonly DiagnosticDescriptor _missingDocument = new(
        DiagnosticIds.ScpdDocumentNotFound,
        title: "No SCPD document matches the one this wrapper names",
        messageFormat:
            "'{0}' asks to be generated from '{1}', which is not among the AdditionalFiles - add it with <AdditionalFiles Include=\"...\" />",
        category: "Usage",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "A wrapper marked with ScpdServiceAttribute names a document the compiler was not given, "
            + "so no members are generated for it and every call site fails to compile with a confusing "
            + "'no such method'. This says what actually went wrong.",
        helpLinkUri: DiagnosticIds.HelpLink(DiagnosticIds.ScpdDocumentNotFound));

    /// <summary>The one place the generated shape is decided.</summary>
    private static string Emit(Target target, ScpdDocument document)
    {
        var code = new StringBuilder();

        code.AppendLine("// <auto-generated/>");
        code.AppendLine("#nullable enable");
        code.AppendLine();
        code.AppendLine("using System;");
        code.AppendLine("using System.Collections.Generic;");
        code.AppendLine("using System.Threading;");
        code.AppendLine("using System.Threading.Tasks;");
        code.AppendLine();

        if (target.Namespace is not null)
        {
            code.AppendLine($"namespace {target.Namespace};");
            code.AppendLine();
        }

        code.AppendLine($"{target.Accessibility} partial class {target.TypeName}");
        code.AppendLine("{");
        code.AppendLine("    private readonly global::UPnP.Rx.IUpnpService _service;");
        code.AppendLine();
        code.AppendLine($"    /// <summary>Wraps <paramref name=\"service\"/> with the actions <c>{Escape(document.ServiceName)}</c> declares.</summary>");
        code.AppendLine("    /// <param name=\"service\">The service to invoke actions on.</param>");
        code.AppendLine($"    {target.Accessibility} {target.TypeName}(global::UPnP.Rx.IUpnpService service) =>");
        code.AppendLine("        _service = service ?? throw new ArgumentNullException(nameof(service));");
        code.AppendLine();
        code.AppendLine("    /// <summary>The service these actions are invoked on.</summary>");
        code.AppendLine("    public global::UPnP.Rx.IUpnpService Service => _service;");

        // Nested, so a result reads as WanIpConnection.GetStatusInfoResult rather than as a
        // top-level type with the wrapper's name glued on the front.
        foreach (var action in document.Actions)
        {
            EmitResultRecord(code, action);
        }

        foreach (var action in document.Actions)
        {
            EmitAction(code, target, action);
        }

        code.AppendLine("}");

        return code.ToString();
    }

    private static void EmitResultRecord(StringBuilder code, ScpdAction action)
    {
        var outputs = action.Out.ToList();

        if (outputs.Count == 0)
        {
            return;
        }

        code.AppendLine();
        code.AppendLine($"    /// <summary>The out-arguments of <c>{Escape(action.Name)}</c>.</summary>");

        foreach (var output in outputs)
        {
            code.AppendLine($"    /// <param name=\"{Property(output.Name)}\">The <c>{Escape(output.Name)}</c> out-argument"
                + $"{(output.DataType.Length > 0 ? $" (<c>{Escape(output.DataType)}</c>)" : string.Empty)}.</param>");
        }

        var parameters = string.Join(", ", outputs.Select(o => $"{ClrType(o.DataType)} {Property(o.Name)}"));
        code.AppendLine($"    public sealed record {ResultName(action)}({parameters});");
    }

    private static void EmitAction(StringBuilder code, Target target, ScpdAction action)
    {
        var inputs = action.In.ToList();
        var outputs = action.Out.ToList();
        var resultType = outputs.Count == 0 ? "Task" : $"Task<{ResultName(action)}>";

        code.AppendLine();
        code.AppendLine($"    /// <summary>Invokes the <c>{Escape(action.Name)}</c> action.</summary>");

        foreach (var input in inputs)
        {
            // The doc name is the identifier WITHOUT the verbatim '@': an escaped keyword is
            // spelled `@class` in code and `class` in a <param> element, and getting that
            // wrong is a CS1572 in the consumer's build, from code they did not write.
            code.Append($"    /// <param name=\"{Parameter(input.Name).TrimStart('@')}\">The <c>{Escape(input.Name)}</c> in-argument");

            if (input.Minimum is { } min && input.Maximum is { } max)
            {
                code.Append($"; the document allows {Number(min)} to {Number(max)}");
            }

            code.AppendLine(".</param>");
        }

        code.AppendLine("    /// <param name=\"ct\">Cancels the call.</param>");
        code.AppendLine("    /// <exception cref=\"global::UPnP.Rx.UpnpActionException\">The device answered with a SOAP fault.</exception>");
        code.AppendLine("    /// <exception cref=\"global::UPnP.Rx.UpnpException\">The exchange failed or the response was unparsable.</exception>");

        var signature = string.Join(", ", inputs.Select(i => $"{ClrType(i.DataType)} {Parameter(i.Name)}"));
        var separator = inputs.Count > 0 ? ", " : string.Empty;

        code.AppendLine($"    public async {resultType} {action.Name}Async({signature}{separator}CancellationToken ct = default)");
        code.AppendLine("    {");

        foreach (var input in inputs)
        {
            EmitRangeGuard(code, input);
            EmitAllowedValueGuard(code, input);
        }

        code.AppendLine("        var arguments = new Dictionary<string, string>");
        code.AppendLine("        {");

        foreach (var input in inputs)
        {
            code.AppendLine($"            [\"{Escape(input.Name)}\"] = {ToWire(input)},");
        }

        code.AppendLine("        };");
        code.AppendLine();
        // Only bind the result when something reads it; an unused local in generated code
        // is noise in a consumer's build output.
        var capture = outputs.Count > 0 ? "var result = " : string.Empty;
        code.AppendLine($"        {capture}await _service.InvokeAsync(\"{Escape(action.Name)}\", arguments, ct).ConfigureAwait(false);");

        if (outputs.Count > 0)
        {
            code.AppendLine();
            code.AppendLine($"        return new {ResultName(action)}(");
            code.AppendLine(string.Join(",\n", outputs.Select(o => $"            {FromWire(o)}")));
            code.AppendLine("        );");
        }

        code.AppendLine("    }");
    }

    /// <summary>
    /// The document's own <c>allowedValueRange</c>, checked before anything reaches the
    /// network - the compile-time half of what <c>ValidateAndOrderArguments</c> does at run
    /// time, minus its SCPD fetch.
    /// </summary>
    private static void EmitRangeGuard(StringBuilder code, ScpdArgument argument)
    {
        if (argument.Minimum is not { } min || argument.Maximum is not { } max || !IsNumeric(argument.DataType))
        {
            return;
        }

        var name = Parameter(argument.Name);

        // An unsigned parameter cannot be below zero, and emitting the comparison anyway
        // produces dead code in someone else's project - generated code has to be as clean
        // as hand-written code, including under this library's own rules.
        var lower = IsUnsigned(argument.DataType) && min <= 0 ? null : $"{name} < {Number(min)}";
        var test = lower is null ? $"{name} > {Number(max)}" : $"{lower} || {name} > {Number(max)}";

        code.AppendLine($"        if ({test})");
        code.AppendLine("        {");
        code.AppendLine($"            throw new ArgumentOutOfRangeException(nameof({name}), {name},");
        code.AppendLine($"                \"{Escape(argument.Name)} must be between {Number(min)} and {Number(max)}, per the service description.\");");
        code.AppendLine("        }");
        code.AppendLine();
    }

    /// <summary>
    /// The document's <c>allowedValueList</c>, for the string arguments that carry an
    /// enumeration on the wire (IGD's <c>NewProtocol</c> being the obvious one).
    /// </summary>
    private static void EmitAllowedValueGuard(StringBuilder code, ScpdArgument argument)
    {
        if (argument.AllowedValues.Count == 0 || ClrType(argument.DataType) != "string")
        {
            return;
        }

        var name = Parameter(argument.Name);
        var allowed = string.Join(" and ", argument.AllowedValues.Select(v => $"\"{Escape(v)}\""));
        var test = string.Join(" && ", argument.AllowedValues.Select(v =>
            $"{name} != \"{Escape(v)}\""));

        code.AppendLine($"        if ({test})");
        code.AppendLine("        {");
        code.AppendLine($"            throw new ArgumentOutOfRangeException(nameof({name}), {name},");
        code.AppendLine($"                \"{Escape(argument.Name)} must be one of {allowed.Replace('\"', '\'')}, per the service description.\");");
        code.AppendLine("        }");
        code.AppendLine();
    }

    private static string ToWire(ScpdArgument argument) => argument.DataType switch
    {
        "string" or "uri" or "uuid" or "" => Parameter(argument.Name) + " ?? string.Empty",
        "boolean" => Parameter(argument.Name) + " ? \"1\" : \"0\"",
        _ => Parameter(argument.Name)
            + ".ToString(global::System.Globalization.CultureInfo.InvariantCulture)"
    };

    /// <summary>
    /// Reading back is lenient: a device that answers with something unparsable yields the
    /// type's default rather than throwing, matching how the rest of the library treats a
    /// device's output. Strict in what we send, lenient in what we accept.
    /// </summary>
    private static string FromWire(ScpdArgument argument)
    {
        var access = $"result[\"{Escape(argument.Name)}\"]";

        return argument.DataType switch
        {
            "string" or "uri" or "uuid" or "" => $"{access} ?? string.Empty",
            "boolean" => $"{access} is \"1\" or \"true\" or \"yes\"",
            _ => $"{ClrType(argument.DataType)}.TryParse({access}, "
                + $"global::System.Globalization.NumberStyles.Integer, "
                + $"global::System.Globalization.CultureInfo.InvariantCulture, out var {Local(argument.Name)}) "
                + $"? {Local(argument.Name)} : default"
        };
    }

    private static bool IsUnsigned(string dataType) =>
        ClrType(dataType) is "byte" or "ushort" or "uint";

    private static bool IsNumeric(string dataType) =>
        ClrType(dataType) is not ("string" or "bool");

    /// <summary>UDA 2.0 clause 2.5 data types, mapped to the CLR types that carry them.</summary>
    private static string ClrType(string dataType) => dataType switch
    {
        "ui1" => "byte",
        "ui2" => "ushort",
        "ui4" => "uint",
        "i1" => "sbyte",
        "i2" => "short",
        "i4" or "int" => "int",
        "boolean" => "bool",
        // string, uri, uuid, bin.*, dates and floats all travel as strings: the library's
        // own leniency policy declines to impose structure the document does not guarantee.
        _ => "string"
    };

    private static string ResultName(ScpdAction action) => $"{action.Name}Result";

    /// <summary>Wire names are <c>NewExternalPort</c>; the parameter is <c>newExternalPort</c>.</summary>
    private static string Parameter(string wireName)
    {
        var identifier = Sanitize(wireName);
        var camel = char.ToLowerInvariant(identifier[0]) + identifier.Substring(1);

        return SyntaxFacts.IsValidIdentifier(camel) && !IsKeyword(camel) ? camel : "@" + camel;
    }

    private static string Property(string wireName) => Sanitize(wireName);

    private static string Local(string wireName) => "parsed" + Sanitize(wireName);

    private static string Sanitize(string wireName)
    {
        var builder = new StringBuilder(wireName.Length);

        foreach (var character in wireName)
        {
            builder.Append(char.IsLetterOrDigit(character) || character == '_' ? character : '_');
        }

        var text = builder.ToString();

        if (text.Length == 0 || char.IsDigit(text[0]))
        {
            text = "_" + text;
        }

        return char.ToUpperInvariant(text[0]) + text.Substring(1);
    }

    private static bool IsKeyword(string identifier) =>
        Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(identifier)
            != Microsoft.CodeAnalysis.CSharp.SyntaxKind.None;

    private static string Number(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string ServiceNameFrom(string fileName)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
        var dot = stem.IndexOf('.');

        return dot > 0 ? stem.Substring(0, dot) : stem;
    }

    /// <summary>A partial class asking to be generated, reduced to strings.</summary>
    private readonly record struct Target(string? Namespace, string TypeName, string Accessibility, string ScpdFileName);

    /// <summary>One parsed SCPD document, keyed by the file name the attribute names.</summary>
    private readonly record struct DocumentEntry(string FileName, ScpdDocument? Document);

    private static class SyntaxFacts
    {
        public static bool IsValidIdentifier(string text) =>
            Microsoft.CodeAnalysis.CSharp.SyntaxFacts.IsValidIdentifier(text);
    }
}
