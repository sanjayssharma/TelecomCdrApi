using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Hangfire;
using TelecomCdr.Infrastructure.Persistence.Repositories;
using TelecomCdr.Abstraction.Interfaces.Repository;

public class Program
{
    public static void Main()
    {
        var host = new HostBuilder()
            .ConfigureFunctionsWebApplication()
            .ConfigureServices(services =>
            {
                string hangfireConnectionString = Environment.GetEnvironmentVariable("HANGFIRE_SQL_CONNECTION_STRING")
                    ?? throw new InvalidOperationException("HANGFIRE_SQL_CONNECTION_STRING is not set.");

                // Add Hangfire client services - this registers IBackgroundJobClient
                services.AddHangfire(config => config
                    .SetDataCompatibilityLevel(CompatibilityLevel.Version_170) // Or your Hangfire version
                    .UseSimpleAssemblyNameTypeSerializer()
                    .UseRecommendedSerializerSettings()
                    .UseSqlServerStorage(hangfireConnectionString));

                // Explicitly add BackgroundJobClient if needed, but AddHangfire should cover it.
                // services.AddSingleton<IBackgroundJobClient>(sp => new BackgroundJobClient(JobStorage.Current));

                // Add other services if needed by the function constructor itself.
                services.AddTransient<IJobStatusRepository, SqlJobStatusRepository>();
                services.AddTransient<IFailedCdrRecordRepository, SqlFailedCdrRecordRepository>();
                services.AddTransient<ICdrRepository, SqlCdrRepository>();
            })
            .Build();
        host.Run();
    }
}