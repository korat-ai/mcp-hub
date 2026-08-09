using Korat.Domain;
using Korat.Domain.Auth;
using Korat.Persistence;

namespace Korat.Persistence.Tests;

/// <summary>
/// Unit tests for tolerant UserId parsing added to fix FormatException on empty/legacy stored values.
/// Covers <see cref="UserId.TryParse"/> and the three EntityMapping call sites.
/// </summary>
public sealed class EntityMappingUserIdTests
{
    // ── UserId.TryParse ────────────────────────────────────────────────────────

    [Fact]
    public void TryParse_Null_ReturnsNull()
    {
        Assert.Null(UserId.TryParse(null));
    }

    [Fact]
    public void TryParse_Empty_ReturnsNull()
    {
        Assert.Null(UserId.TryParse(""));
    }

    [Fact]
    public void TryParse_NotAGuid_ReturnsNull()
    {
        Assert.Null(UserId.TryParse("not-a-guid"));
    }

    [Fact]
    public void TryParse_ValidNFormat_ReturnsParsedUserId()
    {
        var guid = Guid.NewGuid();
        var result = UserId.TryParse(guid.ToString("N"));
        Assert.NotNull(result);
        Assert.Equal(guid, result!.Value.Value);
    }

    // ── EntityMapping.ToDomain(AccessRequestRecord) ───────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    public void ToDomain_AccessRequest_BadResolvedByUserId_ReturnsNull(string? value)
    {
        var record = MinimalAccessRequestRecord();
        record.ResolvedByUserId = value;

        var domain = EntityMapping.ToDomain(record);

        Assert.Null(domain.ResolvedByUserId);
    }

    [Fact]
    public void ToDomain_AccessRequest_ValidResolvedByUserId_ReturnsParsed()
    {
        var guid = Guid.NewGuid();
        var record = MinimalAccessRequestRecord();
        record.ResolvedByUserId = guid.ToString("N");

        var domain = EntityMapping.ToDomain(record);

        Assert.NotNull(domain.ResolvedByUserId);
        Assert.Equal(guid, domain.ResolvedByUserId!.Value.Value);
    }

    // ── EntityMapping.ToDomain(GrantRecord) ───────────────────────────────────

    [Fact]
    public void ToDomain_Grant_EmptyApprovedByUserId_ReturnsEmptyGuidSentinel()
    {
        var record = MinimalGrantRecord();
        record.ApprovedByUserId = "";

        var domain = EntityMapping.ToDomain(record);

        Assert.Equal(new UserId(Guid.Empty), domain.ApprovedByUserId);
    }

    [Fact]
    public void ToDomain_Grant_EmptyRevokedByUserId_ReturnsNull()
    {
        var record = MinimalGrantRecord();
        record.RevokedByUserId = "";

        var domain = EntityMapping.ToDomain(record);

        Assert.Null(domain.RevokedByUserId);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static AccessRequestRecord MinimalAccessRequestRecord() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        SpaceId = Guid.NewGuid().ToString("N"),
        ConsumerId = Guid.NewGuid().ToString("N"),
        McpServerId = Guid.NewGuid().ToString("N"),
        RequestedByNodeId = Guid.NewGuid().ToString("N"),
        PublisherNodeId = Guid.NewGuid().ToString("N"),
        Status = AccessRequestStatus.Pending,
        RequestedAt = DateTimeOffset.UtcNow
    };

    private static GrantRecord MinimalGrantRecord() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        SpaceId = Guid.NewGuid().ToString("N"),
        ConsumerId = Guid.NewGuid().ToString("N"),
        McpServerId = Guid.NewGuid().ToString("N"),
        Status = GrantStatus.Active,
        ApprovedByUserId = Guid.NewGuid().ToString("N"),
        ApprovedAt = DateTimeOffset.UtcNow
    };
}
