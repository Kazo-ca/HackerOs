using Microsoft.EntityFrameworkCore;

namespace Kazo.Os.Server.Data;

/// <summary>
/// Entity Framework Core database context for the optional HackerOs server.
/// Uses SQLite by default; connection string is injected through configuration.
/// </summary>
public sealed class HackerOsServerDbContext : DbContext
{
    /// <inheritdoc />
    public HackerOsServerDbContext(DbContextOptions<HackerOsServerDbContext> options)
        : base(options) { }

    /// <summary>Registered server accounts.</summary>
    public DbSet<AccountEntity> Accounts => Set<AccountEntity>();

    /// <summary>Registered devices per account.</summary>
    public DbSet<DeviceEntity> Devices => Set<DeviceEntity>();

    /// <summary>Active refresh tokens (one per device session; invalidated on rotation or logout).</summary>
    public DbSet<RefreshTokenEntity> RefreshTokens => Set<RefreshTokenEntity>();

    /// <summary>Sync record envelopes (payload stored as JSON text).</summary>
    public DbSet<SyncRecordEntity> SyncRecords => Set<SyncRecordEntity>();

    /// <summary>File content blobs — metadata only; actual bytes stored on filesystem or blob storage.</summary>
    public DbSet<ContentBlobEntity> ContentBlobs => Set<ContentBlobEntity>();

    /// <summary>In-progress upload sessions for chunked content transfer.</summary>
    public DbSet<UploadSessionEntity> UploadSessions => Set<UploadSessionEntity>();

    /// <summary>Audit log entries for security-sensitive operations.</summary>
    public DbSet<AuditEntryEntity> AuditEntries => Set<AuditEntryEntity>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ── Account ──────────────────────────────────────────────────────────
        modelBuilder.Entity<AccountEntity>(e =>
        {
            e.HasKey(a => a.AccountId);
            e.Property(a => a.AccountId).ValueGeneratedNever(); // client-stable UUID
            e.HasIndex(a => a.Username).IsUnique();
            e.Property(a => a.Username).HasMaxLength(128).IsRequired();
            e.Property(a => a.PasswordHash).HasMaxLength(128).IsRequired();
            e.Property(a => a.Salt).HasMaxLength(64).IsRequired();
        });

        // ── Device ───────────────────────────────────────────────────────────
        modelBuilder.Entity<DeviceEntity>(e =>
        {
            e.HasKey(d => d.DeviceId);
            e.Property(d => d.DeviceId).ValueGeneratedNever();
            e.HasIndex(d => new { d.AccountId, d.DeviceFingerprint }).IsUnique();
            e.Property(d => d.DeviceName).HasMaxLength(128).IsRequired();
            e.Property(d => d.DeviceFingerprint).HasMaxLength(256).IsRequired();
            e.HasOne<AccountEntity>().WithMany().HasForeignKey(d => d.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── RefreshToken ──────────────────────────────────────────────────────
        modelBuilder.Entity<RefreshTokenEntity>(e =>
        {
            e.HasKey(t => t.TokenId);
            e.HasIndex(t => t.TokenHash).IsUnique(); // fast lookup by opaque value
            e.Property(t => t.TokenHash).HasMaxLength(128).IsRequired();
            e.HasOne<DeviceEntity>().WithMany().HasForeignKey(t => t.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ── SyncRecord ────────────────────────────────────────────────────────
        modelBuilder.Entity<SyncRecordEntity>(e =>
        {
            e.HasKey(s => s.SyncRecordPk);
            e.HasIndex(s => s.RecordId); // lookup by stable domain record UUID
            e.HasIndex(s => new { s.OwnerAccountId, s.Domain, s.ServerSequence }); // pull cursor
            e.HasIndex(s => new { s.RecordId, s.Revision }).IsUnique();
            e.Property(s => s.Domain).HasMaxLength(64).IsRequired();
            e.Property(s => s.ContentHash).HasMaxLength(64).IsRequired();
            e.Property(s => s.PayloadJson).IsRequired(false); // null for tombstones
        });

        // ── ContentBlob ───────────────────────────────────────────────────────
        modelBuilder.Entity<ContentBlobEntity>(e =>
        {
            e.HasKey(b => b.ContentHash);
            e.Property(b => b.ContentHash).HasMaxLength(64); // SHA-256 hex
            e.Property(b => b.StoragePath).HasMaxLength(512).IsRequired();
        });

        // ── UploadSession ─────────────────────────────────────────────────────
        modelBuilder.Entity<UploadSessionEntity>(e =>
        {
            e.HasKey(u => u.SessionId);
            e.Property(u => u.ContentHash).HasMaxLength(64).IsRequired();
            e.Property(u => u.ReceivedChunksJson).IsRequired();
        });

        // ── AuditEntry ────────────────────────────────────────────────────────
        modelBuilder.Entity<AuditEntryEntity>(e =>
        {
            e.HasKey(a => a.AuditId);
            e.HasIndex(a => new { a.AccountId, a.OccurredUtc }); // per-account timeline
            e.HasIndex(a => a.EventCode); // filter by security event type
            e.Property(a => a.EventCode).HasMaxLength(64).IsRequired();
            e.Property(a => a.ActorDeviceId).IsRequired(false);
        });
    }
}

// =============================================================================
// Entity definitions
// =============================================================================

/// <summary>Server account record.</summary>
public sealed class AccountEntity
{
    public Guid AccountId { get; init; }
    public required string Username { get; set; }
    public required string PasswordHash { get; set; }
    public required string Salt { get; set; }
    public DateTimeOffset CreatedUtc { get; init; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedUtc { get; set; }
}

/// <summary>Registered device record.</summary>
public sealed class DeviceEntity
{
    public Guid DeviceId { get; init; }
    public Guid AccountId { get; init; }
    public required string DeviceName { get; set; }
    public required string DeviceFingerprint { get; set; }
    public DateTimeOffset RegisteredUtc { get; init; }
    public DateTimeOffset? LastSeenUtc { get; set; }
    public bool IsRevoked { get; set; }
}

/// <summary>Hashed refresh token bound to a device session.</summary>
public sealed class RefreshTokenEntity
{
    public int TokenId { get; init; }
    public Guid DeviceId { get; init; }
    public required string TokenHash { get; set; } // SHA-256(opaqueToken)
    public DateTimeOffset IssuedUtc { get; init; }
    public DateTimeOffset ExpiresUtc { get; init; }
    public bool IsInvalidated { get; set; }
}

/// <summary>
/// Sync record envelope row. Each row represents one revision of a domain record.
/// The latest revision for a (RecordId, Domain) pair is the authoritative state.
/// </summary>
public sealed class SyncRecordEntity
{
    public long SyncRecordPk { get; init; } // auto-increment — used as pull cursor
    public Guid RecordId { get; init; }
    public required string Domain { get; init; }
    public int SchemaVersion { get; init; }
    public Guid OwnerAccountId { get; init; }
    public Guid OriginDeviceId { get; init; }
    public long Revision { get; init; }
    public DateTimeOffset ModifiedUtc { get; init; }
    public DateTimeOffset ServerReceivedUtc { get; init; }
    public long ServerSequence { get; init; } // monotonic server-side ordering for cursors
    public required string ContentHash { get; init; }
    public bool IsTombstone { get; init; }
    public string? PayloadJson { get; init; }
}

/// <summary>Content-addressed file blob metadata.</summary>
public sealed class ContentBlobEntity
{
    public required string ContentHash { get; init; } // SHA-256 hex — natural PK
    public long TotalBytes { get; init; }
    public required string StoragePath { get; init; } // filesystem path relative to configured blob root
    public DateTimeOffset StoredUtc { get; init; }
    public int ReferenceCount { get; set; } // for deduplication tracking
}

/// <summary>Chunked content upload session.</summary>
public sealed class UploadSessionEntity
{
    public Guid SessionId { get; init; }
    public Guid AccountId { get; init; }
    public required string ContentHash { get; init; }
    public long TotalBytes { get; init; }
    public int TotalChunks { get; init; }
    public int ChunkSizeBytes { get; init; }
    public required string ReceivedChunksJson { get; set; } // JSON array of received chunk indices
    public bool IsComplete { get; set; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset ExpiresUtc { get; init; }
}

/// <summary>Audit log entry for a security-sensitive server operation.</summary>
public sealed class AuditEntryEntity
{
    public Guid AuditId { get; init; }
    public Guid AccountId { get; init; }
    public Guid? ActorDeviceId { get; init; }
    public required string EventCode { get; init; } // e.g., "PROXY_REQUEST", "SYNC_CONFLICT_RESOLVED"
    public DateTimeOffset OccurredUtc { get; init; }
    public string? Details { get; init; } // redacted JSON details
}
