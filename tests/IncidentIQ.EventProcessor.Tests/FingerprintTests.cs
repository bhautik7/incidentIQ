using IncidentIQ.EventProcessor.Normalization;

namespace IncidentIQ.EventProcessor.Tests;

public class LogFingerprintTests
{
    private static readonly Guid Tenant = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherTenant = new("22222222-2222-2222-2222-222222222222");

    private static string Compute(
        Guid? tenant = null,
        string environment = "production",
        string service = "payments-api",
        string? exceptionType = "Npgsql.NpgsqlException",
        string message = "The connection pool has been exhausted",
        string? stackTrace = null) =>
        LogFingerprint.Compute(tenant ?? Tenant, environment, service, exceptionType, message, stackTrace);

    [Fact]
    public void Is_deterministic_across_calls()
    {
        // The contract the entire pipeline rests on: same failure, same
        // fingerprint, every time and everywhere.
        Assert.Equal(Compute(), Compute());
    }

    [Fact]
    public void Is_sixty_four_lowercase_hex_characters()
    {
        var fingerprint = Compute();

        Assert.Equal(64, fingerprint.Length);
        Assert.Matches("^[0-9a-f]{64}$", fingerprint);
    }

    [Fact]
    public void Two_occurrences_differing_only_by_an_id_share_a_fingerprint()
    {
        var first = Compute(message: LogMessageNormalizer.Normalize("Connection timeout for user 18273"));
        var second = Compute(message: LogMessageNormalizer.Normalize("Connection timeout for user 94822"));

        Assert.Equal(first, second);
    }

    [Fact]
    public void Different_organizations_never_share_a_pattern()
    {
        Assert.NotEqual(Compute(tenant: Tenant), Compute(tenant: OtherTenant));
    }

    [Fact]
    public void Staging_does_not_merge_into_production()
    {
        // A staging failure must never join the production incident that woke
        // someone up.
        Assert.NotEqual(Compute(environment: "production"), Compute(environment: "staging"));
    }

    [Fact]
    public void Different_services_do_not_merge()
    {
        Assert.NotEqual(Compute(service: "payments-api"), Compute(service: "orders-api"));
    }

    [Fact]
    public void Different_exception_types_do_not_merge()
    {
        Assert.NotEqual(
            Compute(exceptionType: "Npgsql.NpgsqlException"),
            Compute(exceptionType: "System.TimeoutException"));
    }

    [Fact]
    public void Is_case_and_whitespace_insensitive_for_service_and_environment()
    {
        // Agents disagree about casing; that disagreement is not information.
        Assert.Equal(
            Compute(service: "payments-api", environment: "production"),
            Compute(service: " Payments-API ", environment: "PRODUCTION"));
    }

    [Fact]
    public void Line_numbers_do_not_change_the_fingerprint()
    {
        // The trap: with line numbers in, a one-line edit above the throw site
        // forks the pattern and loses the incident's history.
        var before = Compute(stackTrace: "at Payments.Charge(Order o) in /src/Payments.cs:line 42");
        var after = Compute(stackTrace: "at Payments.Charge(Order o) in /src/Payments.cs:line 87");

        Assert.Equal(before, after);
    }

    [Fact]
    public void Different_call_sites_do_produce_different_fingerprints()
    {
        var fromCharge = Compute(stackTrace: "at Payments.Charge(Order o)\nat Api.Post()");
        var fromRefund = Compute(stackTrace: "at Payments.Refund(Order o)\nat Api.Delete()");

        Assert.NotEqual(fromCharge, fromRefund);
    }

    [Fact]
    public void Only_the_top_frames_participate()
    {
        // Deep enough to separate callers, shallow enough that an unrelated
        // change further down the stack does not fork the pattern.
        var shallow = Compute(stackTrace: "at A()\nat B()\nat C()");
        var deeper = Compute(stackTrace: "at A()\nat B()\nat C()\nat D()\nat E()");

        Assert.Equal(shallow, deeper);
    }

    [Fact]
    public void A_missing_stack_trace_is_handled()
    {
        Assert.NotEqual(Compute(stackTrace: null), Compute(stackTrace: "at A()"));
        Assert.Equal(Compute(stackTrace: null), Compute(stackTrace: "   "));
    }

    [Fact]
    public void Field_boundaries_cannot_be_forged()
    {
        // Without a separator that cannot appear in the values, a service named
        // "a" with environment "bc" would hash identically to "ab" with "c".
        Assert.NotEqual(
            Compute(service: "a", environment: "bc"),
            Compute(service: "ab", environment: "c"));
    }
}
