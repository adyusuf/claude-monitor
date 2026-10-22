using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Data;

/// <summary>Keys, indexes, CHECKs and defaults of the work tracker integrations (ADR-0015).</summary>
internal static class PodiumDbModelIntegrations
{
    public static void Configure(ModelBuilder b)
    {
        b.Entity<IntegrationConnection>(e =>
        {
            e.HasIndex(x => new { x.WorkspaceId, x.Provider, x.SiteHost }).IsUnique().HasFilter("status <> 'disconnected'")
                .HasDatabaseName("ux_integration_connections_site");
            e.HasIndex(x => x.WorkspaceId);
            e.HasIndex(x => x.ConnectorUserId);
            e.Property(x => x.WebhookIds).HasColumnType("jsonb");
            e.Property(x => x.Status).HasDefaultValue(ConnectionStatuses.Active);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_integration_connections_provider", PodiumDbModel.In("provider", "jira"));
                t.HasCheckConstraint("ck_integration_connections_status", PodiumDbModel.In("status", [.. ConnectionStatuses.All]));
            });
            e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.ConnectorUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<IntegrationAccount>(e =>
        {
            e.HasIndex(x => new { x.ConnectionId, x.UserId }).IsUnique();
            e.HasIndex(x => x.UserId);
            e.Property(x => x.Status).HasDefaultValue(AccountStatuses.Active);
            e.ToTable(t => t.HasCheckConstraint("ck_integration_accounts_status", PodiumDbModel.In("status", [.. AccountStatuses.All])));
            e.HasOne<IntegrationConnection>().WithMany().HasForeignKey(x => x.ConnectionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<IntegrationOAuthState>(e =>
        {
            e.HasKey(x => x.StateHash);
            e.HasIndex(x => x.ExpiresAt);
            e.ToTable(t => t.HasCheckConstraint("ck_integration_oauth_states_purpose", PodiumDbModel.In("purpose", [.. OAuthPurposes.All])));
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<IntegrationConnection>().WithMany().HasForeignKey(x => x.ConnectionId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<IntegrationLink>(e =>
        {
            e.HasIndex(x => new { x.ConnectionId, x.Kind, x.ExternalId }).IsUnique();
            e.HasIndex(x => x.LocalId);
            e.ToTable(t => t.HasCheckConstraint("ck_integration_links_kind", PodiumDbModel.In("kind", [.. LinkKinds.All])));
            e.HasOne<IntegrationConnection>().WithMany().HasForeignKey(x => x.ConnectionId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<IntegrationPerson>(e =>
        {
            e.HasIndex(x => new { x.ConnectionId, x.ExternalAccountId }).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.EmailNormalized);
            e.HasOne<IntegrationConnection>().WithMany().HasForeignKey(x => x.ConnectionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<IntegrationSyncRun>(e =>
        {
            e.HasIndex(x => new { x.ConnectionId, x.StartedAt });
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_integration_sync_runs_kind", PodiumDbModel.In("kind", [.. SyncKinds.All]));
                t.HasCheckConstraint("ck_integration_sync_runs_status", PodiumDbModel.In("status", [.. SyncStatuses.All]));
            });
            e.HasOne<IntegrationConnection>().WithMany().HasForeignKey(x => x.ConnectionId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<IntegrationWebhookReceipt>(e =>
        {
            e.HasKey(x => new { x.ConnectionId, x.DeliveryId });
            e.HasIndex(x => x.ReceivedAt);
            e.HasOne<IntegrationConnection>().WithMany().HasForeignKey(x => x.ConnectionId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ManagedProject>(e =>
        {
            e.HasIndex(x => x.SourceConnectionId);
            e.HasOne<IntegrationConnection>().WithMany().HasForeignKey(x => x.SourceConnectionId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Sprint>(e =>
        {
            e.HasIndex(x => x.SourceConnectionId);
            e.HasOne<IntegrationConnection>().WithMany().HasForeignKey(x => x.SourceConnectionId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Issue>(e =>
        {
            e.HasIndex(x => x.AssigneePersonId);
            e.HasIndex(x => x.ExternalKey);
            e.ToTable(t => t.HasCheckConstraint("ck_issues_external_url", "external_url IS NULL OR external_url LIKE 'https://%'"));
            e.HasOne<IntegrationPerson>().WithMany().HasForeignKey(x => x.AssigneePersonId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
