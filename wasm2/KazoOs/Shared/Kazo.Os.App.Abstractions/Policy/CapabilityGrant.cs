using System.Globalization;
using System.Net;

namespace Kazo.Os.App.Abstractions.Policy;

/// <summary>Identifies one immutable capability grant.</summary>
public readonly record struct CapabilityGrantId
{
    private CapabilityGrantId(Guid value) => Value = value;

    /// <summary>Gets the opaque grant identifier.</summary>
    public Guid Value { get; }

    /// <summary>Creates an identifier from a non-empty value.</summary>
    public static CapabilityGrantId FromGuid(Guid value) => value == Guid.Empty
        ? throw new ArgumentException("Capability grant IDs cannot be empty.", nameof(value))
        : new CapabilityGrantId(value);

    /// <summary>Returns canonical lowercase identifier text.</summary>
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}

/// <summary>Records the trusted source that created a capability grant.</summary>
public enum CapabilityGrantSource
{
    /// <summary>
    /// Seeded at profile creation for a normal capability the app declares
    /// (<c>CleanProfileCapabilityGrantSeeder</c>). The name predates ADR 0098 §7, which removed the
    /// build profile itself: the value stays because grants stored in <c>/etc</c> already carry it.
    /// </summary>
    BuildProfile = 1,

    /// <summary>Approved by an authenticated user.</summary>
    UserApproval = 2,

    /// <summary>Approved by an authenticated Administrator.</summary>
    AdministratorApproval = 3,

    /// <summary>Created by an explicit audited System operation.</summary>
    SystemPolicy = 4
}

/// <summary>Identifies a closed structured resource-constraint kind.</summary>
public enum CapabilityConstraintKind
{
    /// <summary>A canonical virtual path subtree.</summary>
    VirtualPath = 1,

    /// <summary>An exact normalized network host.</summary>
    NetworkHost = 2,

    /// <summary>An inclusive network port range.</summary>
    NetworkPort = 3
}

/// <summary>Base type for structured capability constraints.</summary>
public abstract record CapabilityConstraint
{
    /// <summary>Gets the closed constraint kind.</summary>
    public abstract CapabilityConstraintKind Kind { get; }
}

/// <summary>Constrains a capability to one path or path subtree.</summary>
public sealed record VirtualPathCapabilityConstraint : CapabilityConstraint
{
    /// <summary>Initializes a canonical path constraint.</summary>
    public VirtualPathCapabilityConstraint(VirtualPath path, bool includeDescendants)
    {
        if (string.IsNullOrEmpty(path.Value))
        {
            throw new ArgumentException("Path constraints require a canonical virtual path.", nameof(path));
        }

        Path = path;
        IncludeDescendants = includeDescendants;
    }

    /// <inheritdoc />
    public override CapabilityConstraintKind Kind => CapabilityConstraintKind.VirtualPath;

    /// <summary>Gets the constrained canonical path.</summary>
    public VirtualPath Path { get; }

    /// <summary>Gets whether descendants of the path are included.</summary>
    public bool IncludeDescendants { get; }

    /// <summary>Determines whether a canonical path is inside this constraint.</summary>
    public bool Allows(VirtualPath candidate) => candidate == Path
        || (IncludeDescendants
            && (Path.Value == "/"
                || candidate.Value.StartsWith($"{Path.Value}/", StringComparison.Ordinal)));
}

/// <summary>Constrains a network capability to one exact normalized host.</summary>
public sealed record NetworkHostCapabilityConstraint : CapabilityConstraint
{
    private static readonly IdnMapping Idn = new();

    /// <summary>Initializes an exact host constraint.</summary>
    public NetworkHostCapabilityConstraint(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        string trimmed = host.Trim().TrimEnd('.');
        if (trimmed.Contains('*') || Uri.CheckHostName(trimmed) == UriHostNameType.Unknown)
        {
            throw new ArgumentException("Host constraints require one exact DNS name or IP address.", nameof(host));
        }

        Host = IPAddress.TryParse(trimmed, out IPAddress? address)
            ? address.ToString()
            : Idn.GetAscii(trimmed).ToLowerInvariant();
    }

    /// <inheritdoc />
    public override CapabilityConstraintKind Kind => CapabilityConstraintKind.NetworkHost;

    /// <summary>Gets the exact normalized ASCII DNS name or IP address.</summary>
    public string Host { get; }
}

/// <summary>Constrains a network capability to an inclusive port range.</summary>
public sealed record NetworkPortCapabilityConstraint : CapabilityConstraint
{
    /// <summary>Initializes an inclusive port range.</summary>
    public NetworkPortCapabilityConstraint(ushort minimumPort, ushort maximumPort)
    {
        if (minimumPort == 0 || maximumPort == 0 || maximumPort < minimumPort)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPort), "Ports must form an inclusive range from 1 through 65535.");
        }

        MinimumPort = minimumPort;
        MaximumPort = maximumPort;
    }

    /// <inheritdoc />
    public override CapabilityConstraintKind Kind => CapabilityConstraintKind.NetworkPort;

    /// <summary>Gets the inclusive minimum port.</summary>
    public ushort MinimumPort { get; }

    /// <summary>Gets the inclusive maximum port.</summary>
    public ushort MaximumPort { get; }
}

/// <summary>Represents one immutable exact capability grant.</summary>
public sealed record CapabilityGrant
{
    /// <summary>Initializes a validated immutable grant.</summary>
    public CapabilityGrant(
        CapabilityGrantId id,
        string appId,
        string userId,
        string capability,
        long policyRevision,
        CapabilityGrantSource source,
        IEnumerable<CapabilityConstraint>? constraints = null)
    {
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException("A grant requires a non-empty ID.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        if (!AppCapabilities.IsKnown(capability) && !TopicPermissions.IsWellFormed(capability))
        {
            throw new ArgumentException($"Unknown exact capability '{capability}'.", nameof(capability));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(policyRevision, 1);
        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown capability grant source.");
        }

        CapabilityConstraint[] copiedConstraints = constraints?.ToArray() ?? [];
        if (copiedConstraints.Any(static constraint => constraint is null))
        {
            throw new ArgumentException("Grant constraints cannot contain null values.", nameof(constraints));
        }

        CapabilityConstraintKind? duplicateKind = copiedConstraints
            .GroupBy(static constraint => constraint.Kind)
            .FirstOrDefault(static group => group.Count() > 1)
            ?.Key;
        if (duplicateKind is not null)
        {
            throw new ArgumentException($"Only one {duplicateKind} constraint is allowed per grant.", nameof(constraints));
        }

        Id = id;
        AppId = appId;
        UserId = userId;
        Capability = capability;
        PolicyRevision = policyRevision;
        Source = source;
        Constraints = Array.AsReadOnly(copiedConstraints);
    }

    /// <summary>Gets the immutable grant ID.</summary>
    public CapabilityGrantId Id { get; }

    /// <summary>Gets the exact app ID receiving the grant.</summary>
    public string AppId { get; }

    /// <summary>Gets the exact user ID receiving the grant.</summary>
    public string UserId { get; }

    /// <summary>Gets the exact known capability identifier.</summary>
    public string Capability { get; }

    /// <summary>Gets the policy revision that produced the grant.</summary>
    public long PolicyRevision { get; }

    /// <summary>Gets the trusted grant source.</summary>
    public CapabilityGrantSource Source { get; }

    /// <summary>Gets immutable structured resource constraints.</summary>
    public IReadOnlyList<CapabilityConstraint> Constraints { get; }
}