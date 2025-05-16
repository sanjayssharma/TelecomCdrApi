using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.Extensions.Logging;
using TelecomCdr.Abstraction.Interfaces.Service;
using TelecomCdr.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Azure.Storage.Blobs;
using TelecomCdr.Abstraction.Interfaces.Repository;
using TelecomCdr.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using TelecomCdr.AzureFunctions.Orchestrators;

public class Program
{
    public static void Main()
    {
        var host = new HostBuilder()
            .ConfigureFunctionsWebApplication()
            .ConfigureAppConfiguration(configBuilder => // Optional: Add if you need appsettings.json in Functions
            {
                configBuilder.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                             .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("AZURE_FUNCTIONS_ENVIRONMENT") ?? "Development"}.json", optional: true, reloadOnChange: true)
                             .AddEnvironmentVariables();
            })
            .ConfigureServices((hostContext, services) =>
            {
                var configuration = hostContext.Configuration;

                // Retrieve Hangfire connection string from environment variables
                // (set in local.settings.json for local dev, App Settings in Azure Portal)
                string hangfireConnectionString = configuration.GetConnectionString("HangfireConnectionString");

                if (string.IsNullOrEmpty(hangfireConnectionString))
                {
                    // Log this issue. A _logger isn't easily available here before the host is built,
                    // so Console.Error or a dedicated pre-host _logger might be used for critical failures.
                    Console.Error.WriteLine(
                        "FATAL: HANGFIRE_SQL_CONNECTION_STRING environment variable is not set. " +
                        "The Azure Function will not be able to enqueue Hangfire jobs. " +
                        "Ensure it is configured in local.settings.json or Azure Function App settings.");

                    // Depending on how critical this is, we might throw an exception to prevent startup.
                    throw new InvalidOperationException(
                        "HANGFIRE_SQL_CONNECTION_STRING is not configured. Cannot initialize Hangfire client.");
                }
                else
                {
                    Console.WriteLine($"Hangfire Connection String found: {hangfireConnectionString.Substring(0, Math.Min(hangfireConnectionString.Length, 30))}..."); // Log a snippet for verification
                }

                // Add Hangfire client services. This registers IBackgroundJobClient.
                // This configuration tells Hangfire HOW to connect to the database to enqueue jobs.
                // It DOES NOT start a Hangfire server within the Azure Function.
                services.AddHangfire(config => config
                    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                    .UseSimpleAssemblyNameTypeSerializer()
                    .UseRecommendedSerializerSettings()
                    // Configure SQL Server storage for Hangfire.
                    // The Azure Function acts as a CLIENT to this storage.
                    .UseSqlServerStorage(hangfireConnectionString, new SqlServerStorageOptions
                    {}));

                // 1. Register DbContext (Example for SqlJobStatusRepository)
                string sqlConnectionString = configuration.GetConnectionString("CdrConnectionString");
                services.AddDbContext<AppDbContext>(options =>
                    options.UseSqlServer(sqlConnectionString));

                // 2. Register repositories and services
                services.AddScoped<IJobStatusRepository, SqlJobStatusRepository>();
                services.AddScoped<ICdrRepository, SqlCdrRepository>();
                services.AddScoped<IFailedCdrRecordRepository, SqlFailedCdrRecordRepository>();

                // *** REGISTER IBlobStorageService ***
                string azureWebJobsStorage = configuration["AZURE_STORAGE_CONNECTION_STRING"];
                services.AddSingleton(x => new BlobServiceClient(azureWebJobsStorage));
                services.AddScoped<IBlobStorageService, AzureBlobStorageService>();
                services.AddScoped<IFileProcessingService, CsvFileProcessingService>();
                services.AddScoped<IBlobProcessingOrchestrator, BlobProcessingOrchestrator>();
                // Or services.AddScoped if its dependencies are scoped and it's appropriate.
                // Singleton is often fine for client services like this if they are thread-safe.

                // Configure logging
                services.AddLogging(loggingBuilder =>
                {
                    loggingBuilder.AddConsole();
                    // Add Application Insights for Azure Functions if not automatically configured
                    // string appInsightsKey = configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
                    // if (!string.IsNullOrEmpty(appInsightsKey))
                    // {
                    //     loggingBuilder.AddApplicationInsights(
                    //         configureTelemetryConfiguration: (config) => config.ConnectionString = appInsightsKey,
                    //         configureApplicationInsightsLoggerOptions: (options) => { }
                    //     );
                    // }
                });

            })
            .Build();

        host.Run();
    }
}