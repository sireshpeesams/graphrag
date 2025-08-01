using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SearchFrontend.Endpoints.GraphRAG.Extensions;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureAppConfiguration((context, config) =>
    {
        config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
              .AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json", optional: true, reloadOnChange: true)
              .AddEnvironmentVariables()
              .AddUserSecrets<Program>(optional: true);
    })
    .ConfigureServices((context, services) =>
    {
        // Configure application insights
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // Add GraphRAG services
        services.AddGraphRagServices(context.Configuration);

        // Configure logging
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.AddDebug();
            if (context.HostingEnvironment.IsProduction())
            {
                builder.AddApplicationInsights();
            }
        });

        // Configure HTTP client factory
        services.AddHttpClient();

        // Add health checks
        services.AddHealthChecks();
    })
    .Build();

await host.RunAsync();
