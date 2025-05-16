using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TelecomCdr.Abstraction.Interfaces.Repository;
using TelecomCdr.Abstraction.Interfaces.Service;
using TelecomCdr.Infrastructure.Persistence.Repositories;
using TelecomCdr.Infrastructure.Services;

var host = new HostBuilder()
            .ConfigureFunctionsWebApplication() // Includes Durable Task extension by default if package is referenced
            .ConfigureAppConfiguration(configBuilder =>
            {
                configBuilder.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                             .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("AZURE_FUNCTIONS_ENVIRONMENT") ?? "Development"}.json", optional: true, reloadOnChange: true)
                             .AddEnvironmentVariables();
            })
            .ConfigureServices((hostContext, services) =>
            {
                var configuration = hostContext.Configuration;

                // Database Context
                string mainDbConnectionString = configuration.GetConnectionString("CdrConnectionString")
                    ?? Environment.GetEnvironmentVariable("MAIN_DB_CONNECTION_STRING");
                if (string.IsNullOrEmpty(mainDbConnectionString))
                    throw new InvalidOperationException("Main DB ConnectionString (CdrConnection or MAIN_DB_CONNECTION_STRING) is not configured.");
                services.AddDbContext<AppDbContext>(options =>
                    options.UseSqlServer(mainDbConnectionString));

                // Register Application Services & Repositories
                services.AddScoped<IJobStatusRepository, SqlJobStatusRepository>();
                services.AddScoped<ICdrRepository, SqlCdrRepository>();
                services.AddScoped<IFailedCdrRecordRepository, SqlFailedCdrRecordRepository>();

                services.AddSingleton<IBlobStorageService, AzureBlobStorageService>(); // Singleton if thread-safe
                services.AddScoped<IFileProcessingService, CsvFileProcessingService>();

                // Add other services needed by your activity functions
                // Logging is typically available via ILogger<T> injection
            })
            .Build();

host.Run();