using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.Extensions.Logging;

public class Program
{
    public static void Main()
    {
        var host = new HostBuilder()
            .ConfigureFunctionsWebApplication()
            .ConfigureServices((hostContext, services) =>
            {
                var configuration = hostContext.Configuration;

                // Retrieve Hangfire connection string from environment variables
                // (set in local.settings.json for local dev, App Settings in Azure Portal)
                string hangfireConnectionString = Environment.GetEnvironmentVariable("HANGFIRE_SQL_CONNECTION_STRING");

                if (string.IsNullOrEmpty(hangfireConnectionString))
                {
                    // Log this issue. A logger isn't easily available here before the host is built,
                    // so Console.Error or a dedicated pre-host logger might be used for critical failures.
                    Console.Error.WriteLine(
                        "FATAL: HANGFIRE_SQL_CONNECTION_STRING environment variable is not set. " +
                        "The Azure Function will not be able to enqueue Hangfire jobs. " +
                        "Ensure it is configured in local.settings.json or Azure Function App settings.");

                    // Depending on how critical this is, you might throw an exception to prevent startup.
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

                services.AddLogging(loggingBuilder =>
                {
                    loggingBuilder.AddConsole();
                    // Add other logging providers like Application Insights
                });

            })
            .Build();

        host.Run();
    }
}