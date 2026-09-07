namespace UseNotch.Domain;

public enum ProviderId { OpenAi, Anthropic }
public enum ProviderCapability { Quota, Activity }
public enum IdentityConfidence { Unknown, LocalPartition, ProviderConfirmed }
public enum AuthenticationState { Disabled, Discovering, Missing, PresentUnverified, Authenticated, Expired, Rejected, AccessDenied, Unsupported }
public enum DataFreshness { Fresh, Stale, Expired, Unknown }
public enum ReadingFidelity { ProviderReported, Derived, Unknown }
public enum ActivityState { Unknown, Working, Waiting, Idle }
public enum ErrorCategory { None, Network, RateLimited, Authentication, Forbidden, Unsupported, Schema, OperatingSystem, Unknown }

public sealed record ProviderDefinition(ProviderId Id, string DisplayName, IReadOnlySet<ProviderCapability> Capabilities);

public sealed record ProviderConnection
{
    public ProviderConnection(ProviderId provider, string sourceId, bool enabled, long generation)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) { throw new ArgumentException("A source identifier is required.", nameof(sourceId)); }
        if (generation < 0) { throw new ArgumentOutOfRangeException(nameof(generation)); }
        Provider = provider; SourceId = sourceId; Enabled = enabled; Generation = generation;
    }
    public ProviderId Provider { get; }
    public string SourceId { get; }
    public bool Enabled { get; }
    public long Generation { get; }
}

public sealed record AccountScope
{
    public AccountScope(string partition, IdentityConfidence confidence)
    {
        if (string.IsNullOrWhiteSpace(partition)) { throw new ArgumentException("An account partition is required.", nameof(partition)); }
        Partition = partition; Confidence = confidence;
    }
    public string Partition { get; }
    public IdentityConfidence Confidence { get; }
}

public sealed record UsageLimit
{
    public UsageLimit(decimal? used, decimal? remaining, decimal? capacity, decimal? usedFraction, string unit)
    {
        if (string.IsNullOrWhiteSpace(unit)) { throw new ArgumentException("A unit is required.", nameof(unit)); }
        if (used is < 0 || remaining is < 0 || capacity is < 0 || usedFraction is < 0) { throw new ArgumentOutOfRangeException(nameof(used)); }
        Used = used; Remaining = remaining; Capacity = capacity; UsedFraction = usedFraction; Unit = unit;
    }
    public decimal? Used { get; }
    public decimal? Remaining { get; }
    public decimal? Capacity { get; }
    public decimal? UsedFraction { get; }
    public string Unit { get; }
}

public sealed record QuotaWindow
{
    public QuotaWindow(string id, string scope, TimeSpan? duration, DateTimeOffset? startsAt, DateTimeOffset? resetsAt, UsageLimit limit)
    {
        if (string.IsNullOrWhiteSpace(id)) { throw new ArgumentException("A window identifier is required.", nameof(id)); }
        if (string.IsNullOrWhiteSpace(scope)) { throw new ArgumentException("A window scope is required.", nameof(scope)); }
        if (duration is not null && duration <= TimeSpan.Zero) { throw new ArgumentOutOfRangeException(nameof(duration)); }
        Id = id; Scope = scope; Duration = duration; StartsAt = startsAt; ResetsAt = resetsAt; Limit = limit;
    }
    public string Id { get; }
    public string Scope { get; }
    public TimeSpan? Duration { get; }
    public DateTimeOffset? StartsAt { get; }
    public DateTimeOffset? ResetsAt { get; }
    public UsageLimit Limit { get; }
}

public sealed record UsageBlock(string Category, string Capability, DateTimeOffset? EndsAt);
public sealed record SourceDescriptor(string Kind, string CompatibilityId, bool ContractDocumented);

public sealed record UsageSnapshot
{
    public UsageSnapshot(ProviderId provider, AccountScope account, DateTimeOffset retrievedAt, IReadOnlyList<QuotaWindow> windows, string? headlineWindowId, SourceDescriptor source, UsageBlock? block)
    {
        if (retrievedAt.Offset != TimeSpan.Zero) { throw new ArgumentException("Snapshot timestamps must be UTC.", nameof(retrievedAt)); }
        if (headlineWindowId is not null && windows.All(window => window.Id != headlineWindowId)) { throw new ArgumentException("The headline must identify an existing window.", nameof(headlineWindowId)); }
        Provider = provider; Account = account; RetrievedAt = retrievedAt; Windows = windows; HeadlineWindowId = headlineWindowId; Source = source; Block = block;
    }
    public ProviderId Provider { get; }
    public AccountScope Account { get; }
    public DateTimeOffset RetrievedAt { get; }
    public IReadOnlyList<QuotaWindow> Windows { get; }
    public string? HeadlineWindowId { get; }
    public SourceDescriptor Source { get; }
    public UsageBlock? Block { get; }
    public QuotaWindow? Headline => HeadlineWindowId is null ? null : Windows.First(window => window.Id == HeadlineWindowId);
}

public sealed record ActivitySession(string Id, ProviderId Provider, ActivityState State, DateTimeOffset StartedAt, DateTimeOffset LastObservedAt, ReadingFidelity Fidelity, string? SafeLabel);
public sealed record ErrorState(ErrorCategory Category, bool Retryable, string SafeMessage, int? Code, DateTimeOffset? RetryAt);
public sealed record ProviderStatus(AuthenticationState Authentication, DataFreshness Freshness, DateTimeOffset? LastAttempt, DateTimeOffset? LastSuccess, DateTimeOffset? NextAttempt, bool RefreshInProgress, ErrorState? Error);
