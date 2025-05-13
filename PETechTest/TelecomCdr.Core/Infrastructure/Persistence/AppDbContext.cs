using System.Reflection;
using Microsoft.EntityFrameworkCore;
using TelecomCdr.Core.Models.DomainModel;
using TelecomCdr.Core.Models.DomainModels;

namespace TelecomCdr.Core.Infrastructure.Persistence
{
    public class AppDbContext : DbContext
    {
        public DbSet<CallDetailRecord> CallDetailRecords { get; set; }

        public DbSet<JobStatus> JobStatuses { get; set; }

        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());

            modelBuilder.Entity<CallDetailRecord>(entity =>
            {
                entity.HasKey(e => e.Reference);
                entity.HasIndex(e => e.Reference).IsUnique();

                entity.Property(e => e.CallerId).IsRequired().HasMaxLength(20);
                entity.HasIndex(e => e.CallerId);

                entity.Property(e => e.Recipient).IsRequired().HasMaxLength(20);
                entity.HasIndex(e => e.Recipient);

                entity.Property(e => e.CallEndDateTime).IsRequired();
                entity.HasIndex(e => e.CallEndDateTime);

                entity.Property(e => e.Duration).IsRequired();
                entity.Property(e => e.Cost).HasColumnType("decimal(18,3)").IsRequired();
                entity.Property(e => e.Currency).HasMaxLength(3).IsRequired();
                
                entity.Property(e => e.UploadCorrelationId).HasMaxLength(36); 
                entity.HasIndex(e => e.UploadCorrelationId);
            });

            modelBuilder.Entity<JobStatus>(entity =>
            {
                entity.HasKey(e => e.CorrelationId);
                entity.Property(e => e.CorrelationId).IsRequired().HasMaxLength(36);
                entity.HasIndex(e => e.CorrelationId).IsUnique();

                entity.HasIndex(js => js.LastUpdatedAtUtc);

                entity.Property(e => e.Status).IsRequired().HasMaxLength(30);
                entity.Property(e => e.Message).HasMaxLength(300);
                entity.Property(e => e.OriginalFileName).HasMaxLength(200);
                entity.Property(e => e.BlobName).HasMaxLength(200);
                entity.Property(e => e.ContainerName).HasMaxLength(200);
                entity.Property(e => e.CreatedAtUtc).IsRequired();
                entity.Property(e => e.LastUpdatedAtUtc).IsRequired();
                entity.Property(e => e.ProcessedRecordsCount).IsRequired(false);
                entity.Property(e => e.FailedRecordsCount).IsRequired(false);
            });
            
        }
    }
}
