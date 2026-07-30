using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Elarion.AspNetCore;

/// <summary>
/// Binding helpers invoked by the endpoint registrations that
/// <c>Elarion.Generators.AppModuleDiscoveryGenerator</c> emits for <c>[HttpEndpoint]</c> handlers. The generated
/// <see cref="RequestDelegate"/> binds route, query, header, form, and JSON-body values through these helpers,
/// accumulating failures in a by-<see langword="ref"/> <see cref="ElarionHttpBindingErrors"/> and
/// short-circuiting before the handler runs.
/// </summary>
/// <remarks>
/// <para>
/// The generator owns request binding (ADR-0071): the emitted registrations use the AOT-safe
/// <c>Map*(string, RequestDelegate)</c> overloads, so ASP.NET Core's reflection-based
/// <c>RequestDelegateFactory</c> is never involved. Parsing is <see cref="IParsable{TSelf}"/>-based with
/// <see cref="CultureInfo.InvariantCulture"/> — no reflection, no runtime code generation. The API is static
/// and struct-state-based so a successfully bound request allocates nothing beyond the request DTO itself:
/// the error dictionary only exists on the failure path, and the body's <see cref="JsonTypeInfo{T}"/> is
/// resolved once at mapping time (<see cref="ResolveBodyTypeInfo{T}"/>), not per request.
/// </para>
/// <para>
/// Binding failures produce the same RFC 7807 <c>ValidationProblem</c> shape as Elarion's handler-tier
/// validation (field-keyed <c>errors</c> map, status 400), keyed by the route/query/header/form name the client
/// sent. A request body with the wrong content type is rejected with 415. JSON bodies deserialize through the
/// minimal-API <see cref="HttpJsonOptions"/>, so <c>AddElarionHttpJson</c> keeps request binding on the canonical
/// source-generated contexts with reflection off, and a host override via <c>ConfigureHttpJsonOptions</c> applies.
/// </para>
/// </remarks>
public static class ElarionHttpEndpointBinder {
    /// <summary>How reading a JSON request body failed, when it did.</summary>
    public enum BodyFailure {
        None,
        UnsupportedMediaType,
        MissingBody,
        InvalidJson
    }

    /// <summary>The outcome of one JSON body read: a value, or the failure to report.</summary>
    public readonly struct BodyResult<T>(T? value, BodyFailure failure) where T : class {
        /// <summary>The deserialized body; <see langword="null"/> unless <see cref="Failure"/> is <see cref="BodyFailure.None"/>.</summary>
        public T? Value { get; } = value;

        /// <summary>The failure kind, <see cref="BodyFailure.None"/> on success.</summary>
        public BodyFailure Failure { get; } = failure;
    }

    /// <summary>
    /// Resolves the request type's <see cref="JsonTypeInfo{T}"/> from the minimal-API JSON options once, at
    /// mapping time — the generated registration captures it, so no per-request options lookup happens.
    /// </summary>
    public static JsonTypeInfo<T> ResolveBodyTypeInfo<T>(IEndpointRouteBuilder endpoints) {
        var options = endpoints.ServiceProvider.GetService<IOptions<HttpJsonOptions>>()?.Value.SerializerOptions
                      ?? JsonSerializerOptions.Default;
        if (options.TryGetTypeInfo(typeof(T), out var typeInfo) && typeInfo is JsonTypeInfo<T> typed)
            return typed;

        throw new InvalidOperationException(
            $"No JsonTypeInfo for request type '{typeof(T)}' in the minimal-API JSON options. Call "
            + "AddElarionHttpJson() (and keep the type in a source-generated JsonSerializerContext) so "
            + "[HttpEndpoint] bodies bind with reflection off.");
    }

    /// <summary>Binds a string route value. Route tokens always match, so absence only occurs for optional tokens.</summary>
    public static string? RouteString(HttpContext httpContext, string name, bool required,
        ref ElarionHttpBindingErrors errors) {
        var raw = RouteRaw(httpContext, name);
        if (raw is null && required) errors.AddMissing(name);
        return raw;
    }

    /// <summary>Binds and parses a route value via <see cref="IParsable{TSelf}"/> with the invariant culture.</summary>
    public static T? RouteValue<T>(HttpContext httpContext, string name, bool required,
        ref ElarionHttpBindingErrors errors) where T : struct, IParsable<T> {
        return Parse<T>(name, RouteRaw(httpContext, name), required, ref errors);
    }

    /// <summary>Binds and parses a route value as an enum member (case-insensitive).</summary>
    public static T? RouteEnum<T>(HttpContext httpContext, string name, bool required,
        ref ElarionHttpBindingErrors errors) where T : struct, Enum {
        return ParseEnum<T>(name, RouteRaw(httpContext, name), required, ref errors);
    }

    /// <summary>Binds a string query value. Multiple occurrences bind comma-joined, matching minimal-API scalars.</summary>
    public static string? QueryString(HttpContext httpContext, string name, bool required,
        ref ElarionHttpBindingErrors errors) {
        var raw = QueryRaw(httpContext, name);
        if (raw is null && required) errors.AddMissing(name);
        return raw;
    }

    /// <summary>Binds and parses a query value via <see cref="IParsable{TSelf}"/> with the invariant culture.</summary>
    public static T? QueryValue<T>(HttpContext httpContext, string name, bool required,
        ref ElarionHttpBindingErrors errors) where T : struct, IParsable<T> {
        return Parse<T>(name, QueryRaw(httpContext, name), required, ref errors);
    }

    /// <summary>Binds and parses a query value as an enum member (case-insensitive).</summary>
    public static T? QueryEnum<T>(HttpContext httpContext, string name, bool required,
        ref ElarionHttpBindingErrors errors) where T : struct, Enum {
        return ParseEnum<T>(name, QueryRaw(httpContext, name), required, ref errors);
    }

    /// <summary>Binds every occurrence of a repeated query key as a string array.</summary>
    public static string[]? QueryStrings(HttpContext httpContext, string name, bool required,
        ref ElarionHttpBindingErrors errors) {
        var values = httpContext.Request.Query[name];
        if (values.Count == 0) {
            if (required) errors.AddMissing(name);
            return null;
        }

        var result = new string[values.Count];
        for (var i = 0; i < values.Count; i++) result[i] = values[i] ?? string.Empty;
        return result;
    }

    /// <summary>Binds every occurrence of a repeated query key, parsing each via <see cref="IParsable{TSelf}"/>.</summary>
    public static T[]? QueryValues<T>(HttpContext httpContext, string name, bool required,
        ref ElarionHttpBindingErrors errors) where T : struct, IParsable<T> {
        var values = httpContext.Request.Query[name];
        if (values.Count == 0) {
            if (required) errors.AddMissing(name);
            return null;
        }

        var result = new T[values.Count];
        for (var i = 0; i < values.Count; i++) {
            if (T.TryParse(values[i], CultureInfo.InvariantCulture, out var parsed)) {
                result[i] = parsed;
                continue;
            }

            errors.AddInvalid(name, values[i]);
            return null;
        }

        return result;
    }

    /// <summary>Binds every occurrence of a repeated query key, parsing each as an enum member (case-insensitive).</summary>
    public static T[]? QueryEnums<T>(HttpContext httpContext, string name, bool required,
        ref ElarionHttpBindingErrors errors) where T : struct, Enum {
        var values = httpContext.Request.Query[name];
        if (values.Count == 0) {
            if (required) errors.AddMissing(name);
            return null;
        }

        var result = new T[values.Count];
        for (var i = 0; i < values.Count; i++) {
            if (Enum.TryParse(values[i], ignoreCase: true, out T parsed)) {
                result[i] = parsed;
                continue;
            }

            errors.AddInvalid(name, values[i]);
            return null;
        }

        return result;
    }

    /// <summary>Binds a string header value.</summary>
    public static string? HeaderString(HttpContext httpContext, string name, bool required,
        ref ElarionHttpBindingErrors errors) {
        var raw = HeaderRaw(httpContext, name);
        if (raw is null && required) errors.AddMissing(name);
        return raw;
    }

    /// <summary>Binds and parses a header value via <see cref="IParsable{TSelf}"/> with the invariant culture.</summary>
    public static T? HeaderValue<T>(HttpContext httpContext, string name, bool required,
        ref ElarionHttpBindingErrors errors) where T : struct, IParsable<T> {
        return Parse<T>(name, HeaderRaw(httpContext, name), required, ref errors);
    }

    /// <summary>Binds and parses a header value as an enum member (case-insensitive).</summary>
    public static T? HeaderEnum<T>(HttpContext httpContext, string name, bool required,
        ref ElarionHttpBindingErrors errors) where T : struct, Enum {
        return ParseEnum<T>(name, HeaderRaw(httpContext, name), required, ref errors);
    }

    /// <summary>
    /// Reads the request form ahead of the form/file accessors. Returns <see langword="null"/> when the request
    /// carries no form content type or the form is unreadable; the failure is already recorded when it does.
    /// </summary>
    public static async ValueTask<IFormCollection?> ReadFormAsync(HttpContext httpContext) {
        if (!httpContext.Request.HasFormContentType)
            return null;

        try {
            return await httpContext.Request.ReadFormAsync(httpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (InvalidDataException) {
            return null;
        }
    }

    /// <summary>Records the failure for a form that <see cref="ReadFormAsync"/> could not produce, then writes it.</summary>
    public static Task WriteFormProblemAsync(HttpContext httpContext) {
        var errors = default(ElarionHttpBindingErrors);
        if (!httpContext.Request.HasFormContentType)
            errors.SetStatus(StatusCodes.Status415UnsupportedMediaType);
        else
            errors.Add(string.Empty, "The request form could not be read.");
        return errors.WriteAsync(httpContext);
    }

    /// <summary>Binds a string form value.</summary>
    public static string? FormString(IFormCollection form, string name, bool required,
        ref ElarionHttpBindingErrors errors) {
        var raw = FormRaw(form, name);
        if (raw is null && required) errors.AddMissing(name);
        return raw;
    }

    /// <summary>Binds and parses a form value via <see cref="IParsable{TSelf}"/> with the invariant culture.</summary>
    public static T? FormValue<T>(IFormCollection form, string name, bool required,
        ref ElarionHttpBindingErrors errors) where T : struct, IParsable<T> {
        return Parse<T>(name, FormRaw(form, name), required, ref errors);
    }

    /// <summary>Binds and parses a form value as an enum member (case-insensitive).</summary>
    public static T? FormEnum<T>(IFormCollection form, string name, bool required,
        ref ElarionHttpBindingErrors errors) where T : struct, Enum {
        return ParseEnum<T>(name, FormRaw(form, name), required, ref errors);
    }

    /// <summary>Binds one uploaded file by form field name.</summary>
    public static IFormFile? FormFile(IFormCollection form, string name, bool required,
        ref ElarionHttpBindingErrors errors) {
        var file = form.Files.GetFile(name);
        if (file is null && required) errors.AddMissing(name);
        return file;
    }

    /// <summary>Binds the full uploaded-file collection.</summary>
    public static IFormFileCollection? FormFiles(IFormCollection form, bool required,
        ref ElarionHttpBindingErrors errors) {
        var files = form.Files;
        if (files.Count == 0 && required) errors.AddMissing("files");
        return files;
    }

    /// <summary>
    /// Reads the JSON request body as <typeparamref name="T"/> through the mapping-time-resolved
    /// <paramref name="typeInfo"/>. Failures come back as a <see cref="BodyResult{T}"/> (no
    /// <see langword="ref"/> state across the await).
    /// </summary>
    public static async ValueTask<BodyResult<T>> ReadJsonBodyAsync<T>(
        HttpContext httpContext, JsonTypeInfo<T> typeInfo) where T : class {
        var request = httpContext.Request;
        if (!request.HasJsonContentType())
            return new BodyResult<T>(null, BodyFailure.UnsupportedMediaType);

        try {
            var value = await request.ReadFromJsonAsync(typeInfo, httpContext.RequestAborted).ConfigureAwait(false);
            return value is null
                ? new BodyResult<T>(null, BodyFailure.MissingBody)
                : new BodyResult<T>(value, BodyFailure.None);
        }
        catch (JsonException) {
            return new BodyResult<T>(null, BodyFailure.InvalidJson);
        }
        catch (InvalidDataException) {
            return new BodyResult<T>(null, BodyFailure.InvalidJson);
        }
    }

    /// <summary>Writes the RFC 7807 response for a failed <see cref="ReadJsonBodyAsync{T}"/>.</summary>
    public static Task WriteBodyProblemAsync(HttpContext httpContext, BodyFailure failure) {
        var errors = default(ElarionHttpBindingErrors);
        switch (failure) {
            case BodyFailure.UnsupportedMediaType:
                errors.SetStatus(StatusCodes.Status415UnsupportedMediaType);
                break;
            case BodyFailure.MissingBody:
                errors.Add(string.Empty, "A non-empty request body is required.");
                break;
            default:
                errors.Add(string.Empty, "The request body could not be read as valid JSON.");
                break;
        }

        return errors.WriteAsync(httpContext);
    }

    private static string? RouteRaw(HttpContext httpContext, string name) {
        return httpContext.Request.RouteValues[name]?.ToString();
    }

    private static string? QueryRaw(HttpContext httpContext, string name) {
        var values = httpContext.Request.Query[name];
        return values.Count == 0 ? null : values.ToString();
    }

    private static string? HeaderRaw(HttpContext httpContext, string name) {
        var values = httpContext.Request.Headers[name];
        return StringValues.IsNullOrEmpty(values) ? null : values.ToString();
    }

    private static string? FormRaw(IFormCollection form, string name) {
        var values = form[name];
        return values.Count == 0 ? null : values.ToString();
    }

    private static T? Parse<T>(string name, string? raw, bool required, ref ElarionHttpBindingErrors errors)
        where T : struct, IParsable<T> {
        if (raw is null) {
            if (required) errors.AddMissing(name);
            return null;
        }

        if (T.TryParse(raw, CultureInfo.InvariantCulture, out var value))
            return value;

        errors.AddInvalid(name, raw);
        return null;
    }

    private static T? ParseEnum<T>(string name, string? raw, bool required, ref ElarionHttpBindingErrors errors)
        where T : struct, Enum {
        if (raw is null) {
            if (required) errors.AddMissing(name);
            return null;
        }

        if (Enum.TryParse(raw, ignoreCase: true, out T value))
            return value;

        errors.AddInvalid(name, raw);
        return null;
    }
}
