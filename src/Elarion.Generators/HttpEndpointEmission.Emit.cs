using System.Text;
using Microsoft.CodeAnalysis;

namespace Elarion.Generators;

internal static partial class HttpEndpointEmission {
    private const string RequestDelegateFqn = "global::Microsoft.AspNetCore.Http.RequestDelegate";
    private const string BinderFqn = "global::Elarion.AspNetCore.ElarionHttpEndpointBinder";
    private const string ProducesMetadataFqn = "global::Microsoft.AspNetCore.Http.ProducesResponseTypeMetadata";
    private const string TaskOfResultFqn =
        "global::System.Threading.Tasks.Task<global::Microsoft.AspNetCore.Http.IResult>";
    private const string IdempotentMarkerFqn =
        "global::Elarion.AspNetCore.ElarionIdempotentEndpointMetadata.Instance";
    private const string FileMarkerFqn =
        "global::Elarion.AspNetCore.ElarionFileEndpointMetadata.Instance";

    /// <summary>
    /// Emits one endpoint registration onto <paramref name="target"/>: an AOT-safe
    /// <c>Map*(string, RequestDelegate)</c> call whose delegate binds the request through
    /// <see cref="BinderFqn"/>, invokes the typed handler, and executes the translated result — plus the
    /// deterministic metadata chain (name, description, module tag, response metadata, error metadata, markers,
    /// and the OpenAPI shape <c>MethodInfo</c>). ASP.NET Core's reflection-based
    /// <c>RequestDelegateFactory</c> is never involved (ADR-0071).
    /// </summary>
    public static void AppendRegistration(StringBuilder sb, Model entry, string indent, string target,
        string? moduleTag, int index) {
        var inner = indent + "    ";
        var body = inner + "    ";

        // The shape MethodInfo is what makes ApiExplorer describe the endpoint at all; its parameters back the
        // per-member IParameterBindingMetadata below (since .NET 9, ApiExplorer reads parameters exclusively
        // from that metadata, never from the MethodInfo signature).
        sb.AppendLine(
            $"{indent}var __shape{index} = (({ShapeDelegateName(entry)}){ShapeMethodName(entry)}).Method;");
        if (entry.UseAsParameters && entry.BindingMembers.Count > 0)
            sb.AppendLine($"{indent}var __shapeParameters{index} = __shape{index}.GetParameters();");
        // The body JsonTypeInfo resolves once at mapping time and rides the delegate's closure — no
        // per-request options lookup (the closure allocates once per endpoint at startup).
        var bodyTypeFqn = BodyTypeFqn(entry);
        if (bodyTypeFqn is not null)
            sb.AppendLine(
                $"{indent}var __bodyTypeInfo{index} = {BinderFqn}.ResolveBodyTypeInfo<{bodyTypeFqn}>({target});");
        // A handler-declared CustomizeEndpoint hook receives the builder after the full metadata chain, so its
        // conventions land last; without a hook the registration stays a plain expression statement.
        var endpointLocal = entry.CustomizeEndpointTypeFqn is null ? string.Empty : $"var __endpoint{index} = ";
        sb.AppendLine($"{indent}{endpointLocal}{target}.Map{entry.Verb}({Literal(entry.Route)},");
        sb.AppendLine(
            $"{inner}({RequestDelegateFqn})({(bodyTypeFqn is null ? "static " : string.Empty)}async __context => {{");
        AppendDelegateBody(sb, entry, body, index);
        sb.AppendLine($"{inner}}}))");

        // Fluent metadata chain: order is deterministic so the emitted text stays a byte-identical contract.
        var chain = new List<string> { $".WithName({Literal(entry.EndpointName)})" };
        if (entry.Description is not null)
            chain.Add($".WithDescription({Literal(entry.Description)})");
        if (moduleTag is not null)
            chain.Add($".WithTags({Literal(moduleTag)})");
        // The RequestDelegate overload returns IEndpointConventionBuilder, where the Produces<T> sugar is
        // unavailable — the equivalent ProducesResponseTypeMetadata is attached directly. A file response
        // advertises the generic binary content type (the concrete type is per-payload at run time); the marker
        // lets the OpenAPI package upgrade the schema to type: string, format: binary.
        if (entry.ResponseIsFile)
            chain.Add($".WithMetadata(new {ProducesMetadataFqn}(200, null, new[] {{ \"application/octet-stream\" }}))");
        else if (entry.ResponseIsCreated)
            // The created envelope is peeled by the translation, so the advertised 201 body is the inner type.
            chain.Add(
                $".WithMetadata(new {ProducesMetadataFqn}(201, typeof({entry.CreatedInnerTypeFqn}), new[] {{ \"application/json\" }}))");
        else if (entry.ResponseIsEmpty)
            chain.Add($".WithMetadata(new {ProducesMetadataFqn}(204))");
        else
            chain.Add(
                $".WithMetadata(new {ProducesMetadataFqn}(200, typeof({entry.ResponseTypeFqn}), new[] {{ \"application/json\" }}))");
        chain.Add(".ProducesElarionErrors()");
        if (entry.ResponseIsFile)
            chain.Add($".WithMetadata({FileMarkerFqn})");
        if (entry.IsIdempotent)
            chain.Add($".WithMetadata({IdempotentMarkerFqn})");
        if (entry.DisableAntiforgery)
            chain.Add(".DisableAntiforgery()");
        // ApiExplorer parameter descriptions come exclusively from IParameterBindingMetadata; each entry points
        // at the shape method's matching parameter, whose [From*] attribute carries source and wire name.
        if (entry.UseAsParameters) {
            for (var i = 0; i < entry.BindingMembers.Count; i++) {
                var member = entry.BindingMembers[i];
                var hasTryParse = member.Parse
                    is BindingParse.Parsable or BindingParse.Enum
                    or BindingParse.ParsableArray or BindingParse.EnumArray;
                chain.Add(
                    $".WithMetadata(new global::Elarion.AspNetCore.ElarionHttpParameterBindingMetadata({Literal(member.LookupName)}, __shapeParameters{index}[{i}], hasTryParse: {(hasTryParse ? "true" : "false")}, isOptional: {(member.IsRequired ? "false" : "true")}))");
            }
        }
        else {
            // The JSON request body: ApiExplorer synthesizes the body parameter and request formats from
            // IAcceptsMetadata, which RequestDelegateFactory used to add.
            chain.Add(
                $".WithMetadata(new global::Microsoft.AspNetCore.Http.Metadata.AcceptsMetadata(new[] {{ \"application/json\" }}, typeof({entry.RequestTypeFqn}), false))");
        }

        chain.Add($".WithMetadata(__shape{index})");

        for (var i = 0; i < chain.Count; i++)
            sb.AppendLine($"{inner}{chain[i]}{(i == chain.Count - 1 ? ";" : string.Empty)}");

        if (entry.CustomizeEndpointTypeFqn is not null)
            sb.AppendLine($"{indent}{entry.CustomizeEndpointTypeFqn}.CustomizeEndpoint(__endpoint{index});");
    }

    /// <summary>
    /// Emits the never-invoked OpenAPI shape method (plus its exact-signature delegate type, so the
    /// <c>MethodInfo</c> is obtained by delegate creation — no <c>GetMethod</c> reflection) for one endpoint.
    /// The method gates ApiExplorer's description and supplies the return type; for member-wise endpoints its
    /// parameters are the flattened bound members — one per <see cref="BindingMember"/>, attributed with the
    /// classified <c>[From*]</c> source and wire name plus the member's copied DataAnnotations attributes —
    /// which the registration's <c>ElarionHttpParameterBindingMetadata</c> entries point at. Body endpoints take
    /// no parameters; their request body rides <c>AcceptsMetadata</c>.
    /// </summary>
    public static void AppendApiShapeMethod(StringBuilder sb, Model entry) {
        var parameters = entry.UseAsParameters
            ? string.Join(", ", entry.BindingMembers.Select(ShapeParameter))
            : string.Empty;

        sb.AppendLine();
        sb.AppendLine(
            $"    /// <summary>OpenAPI shape for '{entry.EndpointName}' — never invoked; ApiExplorer reads the endpoint description, return type, and the registration's parameter metadata from this method.</summary>");
        sb.AppendLine($"    private delegate {TaskOfResultFqn} {ShapeDelegateName(entry)}({parameters});");
        sb.AppendLine($"    private static {TaskOfResultFqn} {ShapeMethodName(entry)}({parameters})");
        sb.AppendLine("    {");
        sb.AppendLine(
            "        throw new global::System.NotSupportedException(\"OpenAPI metadata shape only; requests execute through the generated RequestDelegate.\");");
        sb.AppendLine("    }");
    }

    private static string ShapeParameter(BindingMember member) {
        var source = ShapeParameterSourceAttribute(member);
        var sb = new StringBuilder(source);
        // The member's copied DataAnnotations attributes ride the shape parameter so ApiExplorer/OpenAPI keep
        // surfacing route/query/header/form constraints (the JSON body's flow through the schema transformer).
        foreach (var validation in member.ValidationAttributes)
            sb.Append('[').Append(validation).Append("] ");

        sb.Append(ShapeParameterType(member)).Append(' ').Append(LocalName(member));
        return sb.ToString();
    }

    private static string ShapeParameterSourceAttribute(BindingMember member) {
        return member.Source switch {
            BindingSource.Route =>
                $"[global::Microsoft.AspNetCore.Mvc.FromRoute(Name = {Literal(member.LookupName)})] ",
            BindingSource.Query =>
                $"[global::Microsoft.AspNetCore.Mvc.FromQuery(Name = {Literal(member.LookupName)})] ",
            BindingSource.Header =>
                $"[global::Microsoft.AspNetCore.Mvc.FromHeader(Name = {Literal(member.LookupName)})] ",
            BindingSource.Form =>
                $"[global::Microsoft.AspNetCore.Mvc.FromForm(Name = {Literal(member.LookupName)})] ",
            BindingSource.Body => "[global::Microsoft.AspNetCore.Mvc.FromBody] ",
            // IFormFile/IFormFileCollection parameters are classified by type.
            _ => string.Empty
        };
    }

    private static string ShapeParameterType(BindingMember member) {
        var nullable = member.DeclaredNullable ? "?" : string.Empty;
        return member.Parse switch {
            BindingParse.String => $"string{nullable}",
            BindingParse.StringArray => $"string[]{nullable}",
            BindingParse.ParsableArray or BindingParse.EnumArray => $"{member.ValueTypeFqn}[]{nullable}",
            BindingParse.File => $"global::Microsoft.AspNetCore.Http.IFormFile{nullable}",
            BindingParse.Files => $"global::Microsoft.AspNetCore.Http.IFormFileCollection{nullable}",
            // Body members carry the full declared type (nullability included) in ValueTypeFqn.
            BindingParse.Body => member.ValueTypeFqn,
            _ => $"{member.ValueTypeFqn}{nullable}"
        };
    }

    private static string ShapeDelegateName(Model entry) {
        return ShapeMethodName(entry) + "_Signature";
    }

    private static void AppendDelegateBody(StringBuilder sb, Model entry, string indent, int index) {
        var resultCall = entry.ResponseIsFile ? "ToFileResult"
            : entry.ResponseIsCreated ? "ToCreatedResult"
            : entry.ResponseIsEmpty ? "ToNoContentResult"
            : "ToResult";

        if (entry.UseAsParameters)
            AppendMemberBinding(sb, entry, indent, index);
        else
            AppendBodyBinding(sb, indent, index);

        sb.AppendLine(
            $"{indent}var __handler = global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions");
        sb.AppendLine($"{indent}    .GetRequiredService<{HandlerIfaceFqn(entry)}>(__context.RequestServices);");
        sb.AppendLine($"{indent}var __result = global::Elarion.AspNetCore.ElarionHttpResults.{resultCall}(");
        sb.AppendLine($"{indent}    await __handler.HandleAsync(__request, __context.RequestAborted));");
        sb.AppendLine($"{indent}await __result.ExecuteAsync(__context);");
    }

    private static void AppendBodyRead(StringBuilder sb, string indent, int index) {
        sb.AppendLine(
            $"{indent}var __bodyResult = await {BinderFqn}.ReadJsonBodyAsync(__context, __bodyTypeInfo{index});");
        sb.AppendLine($"{indent}if (__bodyResult.Failure != {BinderFqn}.BodyFailure.None) {{");
        sb.AppendLine($"{indent}    await {BinderFqn}.WriteBodyProblemAsync(__context, __bodyResult.Failure);");
        sb.AppendLine($"{indent}    return;");
        sb.AppendLine($"{indent}}}");
    }

    private static void AppendBodyBinding(StringBuilder sb, string indent, int index) {
        AppendBodyRead(sb, indent, index);
        sb.AppendLine($"{indent}var __request = __bodyResult.Value!;");
    }

    private static void AppendMemberBinding(StringBuilder sb, Model entry, string indent, int index) {
        var members = entry.BindingMembers;
        if (members.Count == 0) {
            sb.AppendLine($"{indent}var __request = new {entry.RequestTypeFqn}();");
            return;
        }

        // Awaited sources (form, [FromBody] member) read first and fail fast; the by-ref error state then only
        // flows through synchronous binds, so nothing crosses an await and the happy path allocates nothing.
        var needsForm = false;
        var hasBodyMember = false;
        foreach (var member in members) {
            needsForm |= member.Source is BindingSource.Form or BindingSource.FormFile or BindingSource.FormFiles;
            hasBodyMember |= member.Source == BindingSource.Body;
        }

        if (needsForm) {
            sb.AppendLine($"{indent}var __form = await {BinderFqn}.ReadFormAsync(__context);");
            sb.AppendLine($"{indent}if (__form is null) {{");
            sb.AppendLine($"{indent}    await {BinderFqn}.WriteFormProblemAsync(__context);");
            sb.AppendLine($"{indent}    return;");
            sb.AppendLine($"{indent}}}");
        }

        if (hasBodyMember)
            AppendBodyRead(sb, indent, index);

        sb.AppendLine($"{indent}var __errors = default(global::Elarion.AspNetCore.ElarionHttpBindingErrors);");
        foreach (var member in members)
            sb.AppendLine($"{indent}var {LocalName(member)} = {BindExpression(member)};");

        sb.AppendLine($"{indent}if (__errors.HasErrors) {{");
        sb.AppendLine($"{indent}    await __errors.WriteAsync(__context);");
        sb.AppendLine($"{indent}    return;");
        sb.AppendLine($"{indent}}}");

        AppendConstruction(sb, entry, indent, members);
    }

    private static void AppendConstruction(
        StringBuilder sb, Model entry, string indent, EquatableArray<BindingMember> members) {
        var ctorArgs = new List<string>();
        var initMembers = new List<BindingMember>();
        var needsDefaults = false;
        foreach (var member in members) {
            if (member.IsCtorParameter) {
                ctorArgs.Add(ValueExpression(member));
                continue;
            }

            initMembers.Add(member);
            needsDefaults |= member.UseDefaultsFallback;
        }

        var ctorArgList = string.Join(", ", ctorArgs);
        if (needsDefaults) {
            // Members the wire did not set keep the DTO's own defaults: a probe instance constructed from the
            // bound required members supplies them, so property initializers survive without the generator
            // needing their (potentially non-constant) values.
            sb.Append($"{indent}var __defaults = new {entry.RequestTypeFqn}({ctorArgList})");
            var requiredInits = initMembers.Where(m => m.IsRequired).ToList();
            AppendInitializer(sb, indent, requiredInits, _ => string.Empty);
            sb.AppendLine(";");
        }

        sb.Append($"{indent}var __request = new {entry.RequestTypeFqn}({ctorArgList})");
        AppendInitializer(sb, indent, initMembers, member =>
            member.UseDefaultsFallback ? $" ?? __defaults.{member.MemberName}" : string.Empty);
        sb.AppendLine(";");
    }

    private static void AppendInitializer(
        StringBuilder sb, string indent, IReadOnlyList<BindingMember> members,
        Func<BindingMember, string> fallbackSuffix) {
        if (members.Count == 0)
            return;

        sb.AppendLine(" {");
        for (var i = 0; i < members.Count; i++) {
            var member = members[i];
            var expression = member.UseDefaultsFallback
                ? $"{LocalName(member)}{fallbackSuffix(member)}"
                : ValueExpression(member);
            sb.AppendLine($"{indent}    {member.MemberName} = {expression}{(i == members.Count - 1 ? string.Empty : ",")}");
        }

        sb.Append($"{indent}}}");
    }

    private static string LocalName(BindingMember member) {
        var name = member.MemberName;
        var camel = char.ToLowerInvariant(name[0]) + name.Substring(1);
        return "@" + camel;
    }

    private static string BindExpression(BindingMember member) {
        var name = Literal(member.LookupName);
        var required = member.IsRequired ? "required: true" : "required: false";
        var ctx = $"__context, {name}, {required}, ref __errors";
        var form = $"__form, {name}, {required}, ref __errors";
        return (member.Source, member.Parse) switch {
            (_, BindingParse.Body) => "__bodyResult.Value",
            (BindingSource.Route, BindingParse.String) => $"{BinderFqn}.RouteString({ctx})",
            (BindingSource.Route, BindingParse.Parsable) =>
                $"{BinderFqn}.RouteValue<{member.ValueTypeFqn}>({ctx})",
            (BindingSource.Route, BindingParse.Enum) =>
                $"{BinderFqn}.RouteEnum<{member.ValueTypeFqn}>({ctx})",
            (BindingSource.Header, BindingParse.String) => $"{BinderFqn}.HeaderString({ctx})",
            (BindingSource.Header, BindingParse.Parsable) =>
                $"{BinderFqn}.HeaderValue<{member.ValueTypeFqn}>({ctx})",
            (BindingSource.Header, BindingParse.Enum) =>
                $"{BinderFqn}.HeaderEnum<{member.ValueTypeFqn}>({ctx})",
            (BindingSource.Form, BindingParse.String) => $"{BinderFqn}.FormString({form})",
            (BindingSource.Form, BindingParse.Parsable) =>
                $"{BinderFqn}.FormValue<{member.ValueTypeFqn}>({form})",
            (BindingSource.Form, BindingParse.Enum) =>
                $"{BinderFqn}.FormEnum<{member.ValueTypeFqn}>({form})",
            (BindingSource.FormFile, _) => $"{BinderFqn}.FormFile({form})",
            (BindingSource.FormFiles, _) =>
                $"{BinderFqn}.FormFiles(__form, {required}, ref __errors)",
            (_, BindingParse.StringArray) => $"{BinderFqn}.QueryStrings({ctx})",
            (_, BindingParse.ParsableArray) => $"{BinderFqn}.QueryValues<{member.ValueTypeFqn}>({ctx})",
            (_, BindingParse.EnumArray) => $"{BinderFqn}.QueryEnums<{member.ValueTypeFqn}>({ctx})",
            (_, BindingParse.Parsable) => $"{BinderFqn}.QueryValue<{member.ValueTypeFqn}>({ctx})",
            (_, BindingParse.Enum) => $"{BinderFqn}.QueryEnum<{member.ValueTypeFqn}>({ctx})",
            _ => $"{BinderFqn}.QueryString({ctx})"
        };
    }

    /// <summary>
    /// The JSON body type this endpoint reads, when it reads one: the whole request for body-mode endpoints,
    /// or the single <c>[FromBody]</c> member's type for member-wise shapes; <see langword="null"/> otherwise.
    /// </summary>
    private static string? BodyTypeFqn(Model entry) {
        if (!entry.UseAsParameters)
            return entry.RequestTypeFqn;

        foreach (var member in entry.BindingMembers)
            if (member.Source == BindingSource.Body)
                return member.ValueTypeFqn;

        return null;
    }

    private static string ValueExpression(BindingMember member) {
        var local = LocalName(member);
        var isValueParse = member.Parse is BindingParse.Parsable or BindingParse.Enum;

        if (member.DeclaredNullable)
            return member.UseDefaultsFallback ? $"{local} ?? __defaults.{member.MemberName}" : local;
        if (member.DefaultLiteral is not null)
            return $"{local} ?? {member.DefaultLiteral}";
        if (member.UseDefaultsFallback)
            return $"{local} ?? __defaults.{member.MemberName}";
        if (isValueParse)
            return $"{local}.GetValueOrDefault()";

        return $"{local}!";
    }

    private static string HandlerIfaceFqn(Model entry) {
        return $"global::Elarion.Abstractions.IHandler<{entry.RequestTypeFqn}, "
               + $"global::Elarion.Abstractions.Result<{entry.ResponseTypeFqn}>>";
    }

    private static string ShapeMethodName(Model entry) {
        var sb = new StringBuilder("ApiShape_", entry.EndpointName.Length + 9);
        foreach (var c in entry.EndpointName)
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }
}
