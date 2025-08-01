using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SearchFrontend.Endpoints.GraphRAG.Configuration;
using SearchFrontend.Endpoints.GraphRAG.Interfaces;
using SearchFrontend.Endpoints.GraphRAG.Services;
using SearchFrontend.Endpoints.GraphRAG.Services.Analysis;
using SearchFrontend.Endpoints.GraphRAG.Services.ContentProcessing;
using SearchFrontend.Endpoints.GraphRAG.Services.GraphRag;
using SearchFrontend.Endpoints.GraphRAG.Services.Reporting;
using SearchFrontend.Endpoints.GraphRAG.Services.Storage;

namespace SearchFrontend.Endpoints.GraphRAG.Extensions
{
    /// <summary>
    /// Extension methods for configuring GraphRAG services
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Adds all GraphRAG services to the dependency injection container
        /// </summary>
        public static IServiceCollection AddGraphRagServices(
            this IServiceCollection services, 
            IConfiguration configuration)
        {
            // Configuration
            services.Configure<GraphRagConfiguration>(
                configuration.GetSection(GraphRagConfiguration.SectionName));

            // Validate configuration
            services.AddSingleton<IValidateOptions<GraphRagConfiguration>, GraphRagConfigurationValidator>();

            // Core services
            services.AddScoped<ICaseLoader, CaseLoaderService>();
            services.AddScoped<ICaseUploader, CaseUploaderService>();
            services.AddScoped<GraphRagProcessorService>();

            // Processing services
            services.AddScoped<IContentProcessingService, ContentProcessingService>();
            services.AddScoped<IGraphRagIndexingService, GraphRagIndexingService>();

            // Analysis services
            services.AddScoped<IAnalysisService, AnalysisService>();
            services.AddScoped<ISelfHelpAnalysisService, SelfHelpAnalysisService>();
            services.AddScoped<IFMEAAnalysisService, FMEAAnalysisService>();
            services.AddScoped<IEntityAnalysisService, EntityAnalysisService>();
            services.AddScoped<ICommunityAnalysisService, CommunityAnalysisService>();

            // Reporting and storage
            services.AddScoped<IReportingService, ReportingService>();
            services.AddScoped<IStorageService, StorageService>();

            // HTTP clients with retry policies
            services.AddHttpClient<IStorageService, StorageService>()
                .AddPolicyHandler(GetRetryPolicy());

            return services;
        }

        /// <summary>
        /// Configures HTTP retry policy
        /// </summary>
        private static Polly.IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
        {
            return Polly.Extensions.Http.HttpPolicyExtensions
                .HandleTransientHttpError()
                .OrResult(msg => !msg.IsSuccessStatusCode)
                .WaitAndRetryAsync(
                    retryCount: 3,
                    sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                    onRetry: (outcome, timespan, retryCount, context) =>
                    {
                        Console.WriteLine($"HTTP retry {retryCount} in {timespan.TotalSeconds}s");
                    });
        }
    }

    /// <summary>
    /// Validates GraphRAG configuration
    /// </summary>
    public class GraphRagConfigurationValidator : IValidateOptions<GraphRagConfiguration>
    {
        public ValidateOptionsResult Validate(string name, GraphRagConfiguration options)
        {
            var failures = new List<string>();

            if (string.IsNullOrEmpty(options.UdpSqlServer))
                failures.Add("UdpSqlServer is required");

            if (string.IsNullOrEmpty(options.UdpSqlDatabase))
                failures.Add("UdpSqlDatabase is required");

            if (string.IsNullOrEmpty(options.SupportSqlServer))
                failures.Add("SupportSqlServer is required");

            if (string.IsNullOrEmpty(options.SupportSqlDatabase))
                failures.Add("SupportSqlDatabase is required");

            if (string.IsNullOrEmpty(options.PythonPath))
                failures.Add("PythonPath is required");

            if (string.IsNullOrEmpty(options.AzureOpenAIKey))
                failures.Add("AzureOpenAIKey is required");

            if (options.MaxConcurrentBatches <= 0)
                failures.Add("MaxConcurrentBatches must be greater than 0");

            if (options.BatchSize <= 0)
                failures.Add("BatchSize must be greater than 0");

            if (failures.Any())
            {
                return ValidateOptionsResult.Fail(failures);
            }

            return ValidateOptionsResult.Success;
        }
    }
}