using System.Threading.Channels;
using Elarion.Abstractions.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Elarion.Features;

/// <summary>
/// Validates the seeded variant registry against the running host: at startup — and again on every
/// configuration reload — each configuration-selected switch's current value must be one the registry offers,
/// and at startup each <i>platform</i> variant contract (owned by no module, so wired by hand in the host)
/// must actually be registered in DI. Findings log as warnings by default, or fail startup with
/// <see cref="VariantValidationOptions.Strict"/>. Requires the catalog seeded first:
/// <c>services.AddElarionVariantCatalog(ElarionVariants.All)</c>.
/// </summary>
/// <remarks>
/// The value check closes the configuration axis' deliberate silent fallback (an unknown value resolves the
/// default implementation): the fallback keeps requests serving, and this validator makes the mismatch loud.
/// Module-owned variants are excluded from the registration check on purpose — the registry is compile-time,
/// so a deliberately disabled module's variants would otherwise report as "missing" on every start.
/// </remarks>
public sealed class VariantConfigurationValidator(
    IVariantCatalog catalog,
    IConfiguration configuration,
    IServiceProvider serviceProvider,
    VariantValidationOptions options,
    ILogger<VariantConfigurationValidator> logger) : BackgroundService {
    // Capacity 1 with DropWrite coalesces a burst of configuration reloads into one pending re-validation.
    private readonly Channel<byte> _signals = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private IDisposable? _subscription;

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken) {
        // Subscribe before the first validation so a reload during startup is not missed; the callback only
        // signals the loop (it runs on the reloading thread, so it must not block).
        _subscription = ChangeToken.OnChange(
            configuration.GetReloadToken,
            () => _signals.Writer.TryWrite(0));

        var findings = new List<string>();
        ValidateConfiguredValues(findings);
        ValidatePlatformRegistrations(findings);

        if (findings.Count > 0 && options.Strict)
            throw new InvalidOperationException(
                "Variant registry validation failed:" + Environment.NewLine
                                                      + string.Join(Environment.NewLine, findings));

        foreach (var finding in findings) logger.LogWarning("{VariantValidationFinding}", finding);

        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        try {
            await foreach (var _ in _signals.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false)) {
                // Reload-time re-validation only re-checks values (registrations cannot change after build)
                // and always logs — a runtime settings write must never take the host down.
                var findings = new List<string>();
                ValidateConfiguredValues(findings);
                foreach (var finding in findings) logger.LogWarning("{VariantValidationFinding}", finding);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            // Expected on shutdown.
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken) {
        _subscription?.Dispose();
        _signals.Writer.TryComplete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ValidateConfiguredValues(List<string> findings) {
        foreach (var descriptor in catalog.All) {
            if (descriptor.Axis != VariantAxis.Configuration) continue;

            var value = configuration[descriptor.Key];
            if (string.IsNullOrWhiteSpace(value)) continue;

            // Registry values are lower-cased, matching the runtime's case-insensitive keyed lookup.
            if (descriptor.Values.Contains(value.ToLowerInvariant(), StringComparer.Ordinal)) continue;

            findings.Add(descriptor.HasDefault
                ? $"Configuration key '{descriptor.Key}' is '{value}', which no variant of "
                  + $"'{descriptor.ContractName}' offers; resolution falls back to the default implementation."
                : $"Configuration key '{descriptor.Key}' is '{value}', which no variant of "
                  + $"'{descriptor.ContractName}' offers, and no default implementation is declared; "
                  + "resolution will fail.");
        }
    }

    private void ValidatePlatformRegistrations(List<string> findings) {
        // IServiceProviderIsService answers "is it registered?" without constructing anything; a non-default
        // container that does not implement it skips the check.
        if (serviceProvider.GetService<IServiceProviderIsService>() is not { } isService) return;

        foreach (var descriptor in catalog.All) {
            if (descriptor.Module is not null || descriptor.Contract is null) continue;

            if (isService.IsService(descriptor.Contract)) continue;

            findings.Add(
                $"Platform variant contract '{descriptor.ContractName}' (switch '{descriptor.Key}') has no DI "
                + "registration — did the host forget its Add…VariantService() call?");
        }
    }
}
