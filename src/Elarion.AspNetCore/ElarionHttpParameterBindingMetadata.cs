using System.Reflection;
using Microsoft.AspNetCore.Http.Metadata;

namespace Elarion.AspNetCore;

/// <summary>
/// Endpoint metadata describing one bound request member of a generated <c>[HttpEndpoint]</c> registration, for
/// ApiExplorer/OpenAPI. Since .NET 9, <c>EndpointMetadataApiDescriptionProvider</c> builds parameter
/// descriptions exclusively from <see cref="IParameterBindingMetadata"/> endpoint metadata (which only
/// <c>RequestDelegateFactory</c>/RDG used to add), so the generated registrations — which bind through
/// <c>ElarionHttpEndpointBinder</c>, never <c>RequestDelegateFactory</c> — attach one of these per member
/// (ADR-0071). <see cref="ParameterInfo"/> points at the matching parameter of the endpoint's never-invoked
/// API shape method, whose <c>[From*]</c> attributes carry the binding source and wire name.
/// </summary>
public sealed class ElarionHttpParameterBindingMetadata(
    string name,
    ParameterInfo parameterInfo,
    bool hasTryParse,
    bool isOptional) : IParameterBindingMetadata {
    /// <inheritdoc />
    public string Name => name;

    /// <inheritdoc />
    public ParameterInfo ParameterInfo => parameterInfo;

    /// <inheritdoc />
    public bool HasTryParse => hasTryParse;

    /// <inheritdoc />
    public bool HasBindAsync => false;

    /// <inheritdoc />
    public bool IsOptional => isOptional;
}
