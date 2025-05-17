
using Azure.Storage.Blobs;
using Hangfire;
using TelecomCdr.API.Middlewares;
using TelecomCdr.Core.Features.CdrProcessing.Commands;
using TelecomCdr.Core.Features.CdrProcessing.Validators;
using Hangfire.SqlServer;
using Serilog;
using Serilog.Sinks.ApplicationInsights.TelemetryConverters;
using Microsoft.EntityFrameworkCore;
using FluentValidation;
using StackExchange.Redis;
using TelecomCdr.Core.Filters;
using Hangfire.Dashboard;
using TelecomCdr.Infrastructure.Persistence.Repositories;
using TelecomCdr.Infrastructure.Services;
using TelecomCdr.Abstraction.Interfaces.Service;
using TelecomCdr.Abstraction.Interfaces.Repository;

namespace TelecomCdr.API
{
    public class Program
    {
        public static void Main(string[] args)
        {
            // Configure Serilog logger reading from appsettings.json FIRST
            // to capture bootstrap/host errors.
            var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json")
                .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json", optional: true)
                .AddEnvironmentVariables() // Allows overriding appsettings with env vars
                .Build();

            // Read AppInsights connection string early for logger setup
            string? appInsightsConnectionString = configuration["ApplicationInsights:ConnectionString"];

            try
            {
                // Configure the initial logger
                Log.Logger = new LoggerConfiguration()
                    .ReadFrom.Configuration(configuration)
                    // Conditionally add Application Insights sink here if connection string is valid
                    .WriteTo.Conditional(
                        logEvent => !string.IsNullOrWhiteSpace(appInsightsConnectionString), // Condition
                        sinkConfig => sinkConfig.ApplicationInsights( // Sink configuration
                            connectionString: appInsightsConnectionString,
                            telemetryConverter: new TraceTelemetryConverter() // Use Trace converter
                        )
                     )
                    .CreateBootstrapLogger(); // Use CreateBootstrapLogger for early logging

                Log.Information("Starting web host for TelecomCdr.API");

                var builder = WebApplication.CreateBuilder(args);

                // *** Use Serilog for logging throughout the application ***
                // This replaces the default logger providers.
                builder.Host.UseSerilog((context, services, loggerConfiguration) => loggerConfiguration
                    .ReadFrom.Configuration(context.Configuration) // Read config again for context-aware setup
                    .ReadFrom.Services(services) // Allow enrichment from services
                    .Enrich.FromLogContext()
                    .Enrich.WithProperty("Application", "TelecomCdr.API") // Ensure Application property
                                                                   // Conditionally add Application Insights sink again within the host context
                                                                   // Ensures it uses the fully configured TelemetryConfiguration if AddApplicationInsightsTelemetry is called.
                    .WriteTo.Conditional(
                         logEvent => !string.IsNullOrWhiteSpace(context.Configuration["ApplicationInsights:ConnectionString"]),
                         sinkConfig => sinkConfig.ApplicationInsights(
                            services.GetRequiredService<Microsoft.ApplicationInsights.Extensibility.TelemetryConfiguration>(), // Use TelemetryConfiguration from DI
                            new TraceTelemetryConverter()
                         )
                     )
                );

                // --- Add services to the container ---

                // *** Add Application Insights Telemetry ***
                // Conditionally add App Insights based on configuration.
                string? aiConnectionString = builder.Configuration["ApplicationInsights:ConnectionString"];
                if (!string.IsNullOrWhiteSpace(aiConnectionString))
                {
                    Log.Information("Application Insights configured with Connection String.");
                    builder.Services.AddApplicationInsightsTelemetry(options =>
                    {
                        options.ConnectionString = aiConnectionString;
                    });
                }
                else
                {
                    Log.Warning("Application Insights Connection String not found. Telemetry will not be sent to Application Insights.");
                }

                // *** Add AppDbContext ***
                var cdrConnectionString = builder.Configuration.GetConnectionString("CdrConnection");
                if (string.IsNullOrEmpty(cdrConnectionString))
                {
                    Log.Error("FATAL: CdrConnection string is not configured.");
                    throw new InvalidOperationException("CdrConnection string is not configured.");
                }
                builder.Services.AddDbContext<AppDbContext>(options =>
                    options.UseSqlServer(cdrConnectionString));

                // *** Add Azure Blob Storage Service ***
                var blobConnectionString = builder.Configuration.GetValue<string>("AzureBlobStorage:ConnectionString");
                if (string.IsNullOrEmpty(blobConnectionString))
                {
                    Log.Warning("Azure Blob Storage connection string 'AzureBlobStorage:ConnectionString' not found. Large file uploads may fail.");
                    builder.Services.AddSingleton<IBlobStorageService, NullBlobStorageService>();
                }
                else
                {
                    builder.Services.AddSingleton(x => new BlobServiceClient(blobConnectionString));
                    builder.Services.AddScoped<IBlobStorageService, AzureBlobStorageService>();
                }

                builder.Services.AddScoped<ICdrRepository, SqlCdrRepository>();
                builder.Services.AddScoped<IJobStatusRepository, SqlJobStatusRepository>();
                builder.Services.AddScoped<IFailedCdrRecordRepository, SqlFailedCdrRecordRepository>();
                builder.Services.AddScoped<IFileProcessingService, CsvFileProcessingService>();
                builder.Services.AddScoped<IQueueService, AzureStorageQueueService>();

                // *** Add MediatR for CQRS pattern ***
                builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(ProcessCdrFileCommand).Assembly));

                // *** Add FluentValidation Validators ***
                // Automatically registers all validators inheriting from AbstractValidator
                // within the specified assembly (typically Cdr.Application).
                builder.Services.AddValidatorsFromAssemblyContaining<ProcessCdrFileCommandValidator>();

                // *** Hangfire Configuration ***
                var hangfireConnectionString = builder.Configuration.GetConnectionString("HangfireConnection");
                if (string.IsNullOrEmpty(hangfireConnectionString))
                {
                    Log.Error("FATAL: CdrConnection string is not configured.");
                    throw new InvalidOperationException("hangfireConnectionString string is not configured.");
                }

                builder.Services.AddHangfire(configuration => configuration
                    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                    .UseSimpleAssemblyNameTypeSerializer()
                    .UseRecommendedSerializerSettings()
                    .UseSqlServerStorage(hangfireConnectionString, new SqlServerStorageOptions
                    {
                        CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
                        SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
                        QueuePollInterval = TimeSpan.FromSeconds(15),
                        UseRecommendedIsolationLevel = true,
                        DisableGlobalLocks = true
                    }));
                builder.Services.AddHangfireServer(options => { options.WorkerCount = Environment.ProcessorCount * 3; });

                builder.Services.AddHttpContextAccessor();

                // *** Add support for Idempotency ***
                var redisConnectionString = builder.Configuration.GetValue<string>("RedisSettings:ConnectionString");
                if (string.IsNullOrEmpty(redisConnectionString))
                {
                    Console.WriteLine("Redis connection string is not configured. Idempotency service will not use Redis.");
                    // Fallback to a null object pattern
                    builder.Services.AddScoped<IIdempotencyService, NullIdempotencyService>();
                }
                else
                {
                    builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
                    {
                        var logger = sp.GetRequiredService<ILogger<Program>>();
                        try
                        {
                            logger.LogInformation("Attempting to connect to Redis with connection string: {RedisConnectionString}", redisConnectionString);
                            var connection = ConnectionMultiplexer.Connect(redisConnectionString);
                            logger.LogInformation("Successfully connected to Redis.");
                            return connection;
                        }
                        catch (RedisConnectionException ex)
                        {
                            logger.LogCritical(ex, "Failed to connect to Redis. ConnectionString: {RedisConnectionString}", redisConnectionString);
                            throw; // Fail fast if Redis is critical
                        }
                    });
                    builder.Services.AddScoped<IIdempotencyService, RedisIdempotencyService>();
                }

                builder.Services.AddScoped<IdempotencyAttribute>();

                builder.Services.AddControllers();
                builder.Services.AddCors(options =>
                {
                    options.AddPolicy("AllowAll",
                        builder =>
                        {
                            builder.AllowAnyOrigin()
                                   .AllowAnyMethod()
                                   .AllowAnyHeader();
                        });
                });
                // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
                builder.Services.AddEndpointsApiExplorer();
                builder.Services.AddSwaggerGen(options =>
                {
                    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
                    {
                        Version = "v1",
                        Title = "CDR API",
                        Description = "API for managing Call Detail Records"
                    });
                });

                var app = builder.Build();
                
                app.UseRouting();

                if (app.Environment.IsDevelopment())
                {
                    app.UseSwagger();
                    app.UseSwaggerUI(c => { c.SwaggerEndpoint("/swagger/v1/swagger.json", "CDR API V1"); c.RoutePrefix = "swagger"; });
                    app.UseDeveloperExceptionPage();
                }
                else
                {
                    app.UseMiddleware<GlobalExceptionHandlerMiddleware>();
                    app.UseHsts();
                }

                app.UseHttpsRedirection();
                app.UseSerilogRequestLogging();
                app.UseMiddleware<CorrelationIdMiddleware>();
                app.MapControllers();
                app.MapHangfireDashboard("/hangfire", new DashboardOptions
                {
                    // Allows write operations on the dashboard (e.g., re-queueing jobs)
                    // In production, you MUST secure this dashboard.
                    IsReadOnlyFunc = (DashboardContext context) => false
                });

                using (var scope = app.Services.CreateScope())
                {
                    var services = scope.ServiceProvider;
                    try
                    {
                        var context = services.GetRequiredService<AppDbContext>();
                        if (context.Database.GetPendingMigrations().Any())
                        {
                            context.Database.Migrate();
                            Log.Information("Database migrations applied successfully.");
                        }
                        else
                        {
                            Log.Information("Database is up-to-date. No migrations needed.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "An error occurred while migrating or initializing the database.");
                    }
                }

                Log.Information("Starting CDR API application...");
                app.Run();

            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "TelecomCdr.Api host terminated unexpectedly");
            }
            finally
            {
                Log.CloseAndFlush(); // Ensure all logs are written before exiting
            }
        }
    }
}
