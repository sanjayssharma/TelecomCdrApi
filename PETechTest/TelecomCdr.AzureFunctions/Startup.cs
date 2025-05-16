using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore; // For DbContext
using Microsoft.Extensions.Configuration; // For IConfiguration
using Azure.Storage.Blobs; // For BlobServiceClient
using Hangfire; // For Hangfire
using Hangfire.SqlServer;
using TelecomCdr.Infrastructure.Persistence.Repositories;
using TelecomCdr.Abstraction.Interfaces.Repository;
using TelecomCdr.Infrastructure.Services;
using TelecomCdr.Abstraction.Interfaces.Service; // For Hangfire SQL Server storage, if used

[assembly: FunctionsStartup(typeof(Cdr.AzureFunctions.Startup))]
namespace Cdr.AzureFunctions
{
    public class Startup : FunctionsStartup
    {
        public override void Configure(IFunctionsHostBuilder builder)
        {
            // Access configuration (from local.settings.json or App Settings in Azure)
            var context = builder.GetContext();
            var configuration = context.Configuration;

            // 1. Register DbContext (Example for MsSqlJobStatusRepository)
            string sqlConnectionString = configuration.GetConnectionString("CdrConnectionString");
            builder.Services.AddDbContext<AppDbContext>(options =>
                options.UseSqlServer(sqlConnectionString));

            // 2. Register repositories and services
            builder.Services.AddScoped<IJobStatusRepository, SqlJobStatusRepository>();
            builder.Services.AddScoped<ICdrRepository, SqlCdrRepository>();
            builder.Services.AddScoped<IFailedCdrRecordRepository, SqlFailedCdrRecordRepository>();

            // ... other repositories

            // 3. Register Blob Storage Service
            string azureWebJobsStorage = configuration["AzureWebJobsStorage"];
            builder.Services.AddSingleton(x => new BlobServiceClient(azureWebJobsStorage));
            builder.Services.AddScoped<IBlobStorageService, AzureBlobStorageService>();
            builder.Services.AddScoped<IFileProcessingService, CsvFileProcessingService>();

            // 4. Register Hangfire Client (if your Azure Function enqueues jobs)
            // Ensure Hangfire is configured to use the same backend as your Hangfire server
            string hangfireConnectionString = configuration.GetConnectionString("HangfireConnectionString"); 
            builder.Services.AddHangfire(config => config
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_170)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UseSqlServerStorage(hangfireConnectionString, new SqlServerStorageOptions
                {
                    CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
                    SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
                    QueuePollInterval = TimeSpan.Zero,
                    UseRecommendedIsolationLevel = true,
                    DisableGlobalLocks = true // if using SQL Azure
                }));
        }

        public override void ConfigureAppConfiguration(IFunctionsConfigurationBuilder builder)
        {
            // This method will allow us to add additional configuration sources
            // For example, to load settings from Azure App Configuration or custom JSON files.
            // By default, it loads from local.settings.json (local) and App Settings (Azure).
            // builder.ConfigurationBuilder.AddJsonFile(Path.Combine(context.ApplicationRootPath, "appsettings.json"), optional: true, reloadOnChange: false);
        }
    }
}