using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests.Helpers;

/// <summary>
/// Test-only <see cref="ITenantIdentifier"/> for unit tests that don't care
/// about real tenant resolution.
/// </summary>
internal sealed class TestTenant : ITenantIdentifier
{
    public static readonly ITenantIdentifier Instance = new TestTenant();

    public string Value => "stub:test";
    public string Provider => "stub";
    public string ShortHash => "deadbeef";
}
