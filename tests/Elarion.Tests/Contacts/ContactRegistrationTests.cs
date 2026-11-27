using System.Diagnostics;
using System.Text;
using AwesomeAssertions;
using Elarion.Contacts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Elarion.Tests.Contacts.VCardFiles;

namespace Elarion.Tests.Contacts;

public sealed class ContactRegistrationTests {
    private const string IsolatedVariable = "ELARION_CONTACTS_ISOLATED";

    [Fact]
    public async Task AddElarionContacts_RegistersASingletonReaderWithBoundDefaults() {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["Contacts:ReadDefaults:MaxContacts"] = "1",
                ["Contacts:ReadDefaults:FallbackEncoding"] = "windows-1250",
            })
            .Build();
        var services = new ServiceCollection()
            .AddElarionContacts(configuration.GetSection(ContactOptions.SectionName), options => options.ReadDefaults.MaxLineBytes = 4096)
            .AddElarionContacts();
        await using var provider = services.BuildServiceProvider();

        var reader = provider.GetRequiredService<ContactReader>();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Card("FN:A") + Card("FN:B")));
        var result = await reader.ReadAsync(stream, cancellationToken: TestCancellation);

        provider.GetRequiredService<ContactReader>().Should().BeSameAs(reader);
        services.Count(static d => d.ServiceType == typeof(ContactReader)).Should().Be(1);
        result.Entries.Should().ContainSingle();
        Assert.Single(result.FileErrors).Code.Should().Be(ContactErrorCodes.TooManyContacts);
    }

    [Fact]
    public async Task AnUnknownDefaultFallbackEncoding_ThrowsWhenTheReaderIsResolved() {
        await using var provider = new ServiceCollection()
            .AddElarionContacts(configure: options => options.ReadDefaults.FallbackEncoding = "x-nothing")
            .BuildServiceProvider();

        var resolve = () => provider.GetRequiredService<ContactReader>();

        resolve.Should().Throw<InvalidOperationException>();
    }

    // FR-016: the test process may already hold a provider another package registered, so the check runs this test
    // class's isolated case in a fresh process of the test executable.
    [Fact]
    public async Task Reading_RegistersNoEncodingProviderProcessWide() {
        var processPath = Environment.ProcessPath!;
        var start = new ProcessStartInfo(processPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) {
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add(typeof(ContactRegistrationTests).Assembly.Location);
        }

        start.ArgumentList.Add("-method");
        start.ArgumentList.Add($"{typeof(ContactRegistrationTests).FullName}.{nameof(IsolatedRead_LeavesTheCodePagesUnregistered)}");
        start.Environment[IsolatedVariable] = "1";

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestCancellation);
        var error = process.StandardError.ReadToEndAsync(TestCancellation);
        await process.WaitForExitAsync(TestCancellation);

        var log = await output + await error;
        process.ExitCode.Should().Be(0, because: log);
        log.Should().Contain("Total: 1,", "the isolated test must have run");
        log.Should().Contain("Failed: 0");
    }

    [Fact]
    public async Task IsolatedRead_LeavesTheCodePagesUnregistered() {
        if (Environment.GetEnvironmentVariable(IsolatedVariable) != "1") return;
        var before = () => Encoding.GetEncoding(1252);
        before.Should().Throw<NotSupportedException>("nothing registered the code pages in this fresh process");

        var bytes = CodePagesEncodingProvider.Instance.GetEncoding(1252)!.GetBytes(Card("FN:Müller", "NOTE;CHARSET=windows-1250:x"));
        var result = await ReadAsync(bytes);

        result.UsedFallbackEncoding.Should().BeTrue();
        result.Contacts.Single().FormattedName.Should().Be("Müller");
        var after = () => Encoding.GetEncoding(1252);
        after.Should().Throw<NotSupportedException>();
    }
}
