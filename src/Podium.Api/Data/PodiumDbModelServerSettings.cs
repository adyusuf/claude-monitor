using Microsoft.EntityFrameworkCore;

namespace Podium.Api.Data;

/// <summary>Keys and limits of the server-wide settings (ADR-0017).</summary>
internal static class PodiumDbModelServerSettings
{
    public static void Configure(ModelBuilder b)
    {
        b.Entity<ServerSetting>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(100);
            e.Property(x => x.Value).HasMaxLength(4000);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UpdatedBy).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
