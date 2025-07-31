using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SearchFrontend.Application.Services;
using SearchFrontend.Domain.Interfaces;
using SearchFrontend.Infrastructure.Configuration;
using SearchFrontend.Infrastructure.Repositories;
using ProvidersLibrary.DataProviders.Implementations;
using ProviderLibrary.AuthenticationProvider.Abstractions;
using ProviderLibrary.HTTPProviders.Abstractions;
using SearchFrontend.Providers.GPT;
using System;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using SearchFrontend.Domain.Models;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureAppConfiguration((context, config) =>
    {
        config
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables()
            .AddUserSecrets<Program>(optional: true);
    })
    .ConfigureServices((context, services) =>
    {
        var configuration = context.Configuration;

        // Configure options with validation
        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.SectionName))
            .AddSingleton<IValidateOptions<DatabaseOptions>, ValidateOptionsService<DatabaseOptions>>();

        services.Configure<GraphRagOptions>(configuration.GetSection(GraphRagOptions.SectionName))
            .AddSingleton<IValidateOptions<GraphRagOptions>, ValidateOptionsService<GraphRagOptions>>();

        services.Configure<ProcessingOptions>(configuration.GetSection(ProcessingOptions.SectionName))
            .AddSingleton<IValidateOptions<ProcessingOptions>, ValidateOptionsService<ProcessingOptions>>();

        // Register core infrastructure services
        services.AddSingleton<SQLQueryExecutor>();
        services.AddSingleton<KustoQueryExecutor>();

        // Register authentication and HTTP providers (these would need to be implemented)
        services.AddScoped<IAuthenticationProvider, AuthenticationProvider>();
        services.AddScoped<IGptChatProviderV2, GptChatProviderV2>();

        // Register repositories
        services.AddScoped<ICaseRepository, CaseRepository>();

        // Register application services
        services.AddScoped<IGraphRagService, GraphRagService>();
        services.AddScoped<IAnalysisEngine, AnalysisEngine>();
        services.AddScoped<IProcessingDecisionEngine, ProcessingDecisionEngine>();
        services.AddScoped<IReportGenerator, ReportGenerator>();
        services.AddScoped<IContentUploader, ContentUploader>();

        // Configure logging
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.AddApplicationInsights();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        // Add memory cache for performance optimization
        services.AddMemoryCache();

        // Add HTTP client factory
        services.AddHttpClient();

        // Health checks
        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database")
            .AddCheck<GraphRagHealthCheck>("graphrag");
    })
    .Build();

host.Run();

// Supporting classes for dependency injection
public class ValidateOptionsService<TOptions> : IValidateOptions<TOptions> where TOptions : class
{
    public ValidateOptionsResult Validate(string? name, TOptions options)
    {
        var validationContext = new ValidationContext(options);
        var validationResults = new List<ValidationResult>();

        if (Validator.TryValidateObject(options, validationContext, validationResults, true))
        {
            return ValidateOptionsResult.Success;
        }

        var errors = validationResults.Select(r => r.ErrorMessage).Where(e => e != null);
        return ValidateOptionsResult.Fail(errors!);
    }
}

// Placeholder implementations for missing services
public class AuthenticationProvider : IAuthenticationProvider
{
    // Implementation would go here
    public Task<string> GetTokenAsync() => Task.FromResult("mock-token");
}

public class AnalysisEngine : IAnalysisEngine
{
    private readonly ILogger<AnalysisEngine> _logger;

    public AnalysisEngine(ILogger<AnalysisEngine> logger)
    {
        _logger = logger;
    }

    public async Task<SelfHelpGapAnalysis> PerformSelfHelpGapAnalysisAsync(
        string rootDir, 
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Performing self-help gap analysis for: {RootDir}", rootDir);
        await Task.Delay(100, cancellationToken); // Placeholder
        return new SelfHelpGapAnalysis();
    }

    public async Task<FMEAAnalysisResult> PerformFMEAAnalysisAsync(
        string rootDir, 
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Performing FMEA analysis for: {RootDir}", rootDir);
        await Task.Delay(100, cancellationToken); // Placeholder
        return new FMEAAnalysisResult();
    }

    public async Task PerformEntityRelationshipAnalysisAsync(
        GraphRagAnalysisResult result, 
        string outputDir, 
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Performing entity relationship analysis for: {OutputDir}", outputDir);
        await Task.Delay(100, cancellationToken); // Placeholder
    }
}

public class ProcessingDecisionEngine : IProcessingDecisionEngine
{
    private readonly ILogger<ProcessingDecisionEngine> _logger;

    public ProcessingDecisionEngine(ILogger<ProcessingDecisionEngine> logger)
    {
        _logger = logger;
    }

    public async Task<ProcessingDecision> MakeDecisionAsync(
        IngestionRequest request, 
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Making processing decision for ProductId: {ProductId}", request.ProductId);
        await Task.Delay(100, cancellationToken); // Placeholder

        return new ProcessingDecision
        {
            ShouldProcess = true,
            Decision = "Process",
            Reason = "New analysis requested",
            Stats = new ProcessingStats
            {
                TotalCasesRequested = request.NumCasesToFetch
            }
        };
    }
}

public class ReportGenerator : IReportGenerator
{
    private readonly ILogger<ReportGenerator> _logger;

    public ReportGenerator(ILogger<ReportGenerator> logger)
    {
        _logger = logger;
    }

    public async Task GenerateReportsAsync(
        GraphRagAnalysisResult result, 
        IngestionRequest input, 
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating reports for AnalysisId: {AnalysisId}", result.AnalysisId);
        await Task.Delay(100, cancellationToken); // Placeholder
    }
}

public class ContentUploader : IContentUploader
{
    private readonly ILogger<ContentUploader> _logger;

    public ContentUploader(ILogger<ContentUploader> logger)
    {
        _logger = logger;
    }

    public async Task UploadAsync(string path, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Uploading content from: {Path}", path);
        await Task.Delay(100, cancellationToken); // Placeholder
    }
}

// Health check implementations
public class DatabaseHealthCheck : IHealthCheck
{
    private readonly ICaseRepository _caseRepository;
    private readonly ILogger<DatabaseHealthCheck> _logger;

    public DatabaseHealthCheck(ICaseRepository caseRepository, ILogger<DatabaseHealthCheck> logger)
    {
        _caseRepository = caseRepository;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, 
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Simple health check - try to get a small number of cases
            await _caseRepository.GetClosedCasesAsync("TEST", "", null, 1, 1, cancellationToken);
            return HealthCheckResult.Healthy("Database is accessible");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database health check failed");
            return HealthCheckResult.Unhealthy("Database is not accessible", ex);
        }
    }
}

public class GraphRagHealthCheck : IHealthCheck
{
    private readonly GraphRagOptions _options;
    private readonly ILogger<GraphRagHealthCheck> _logger;

    public GraphRagHealthCheck(IOptions<GraphRagOptions> options, ILogger<GraphRagHealthCheck> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, 
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Check if Python executable exists
            if (!File.Exists(_options.PythonPath))
            {
                return HealthCheckResult.Unhealthy($"Python executable not found at: {_options.PythonPath}");
            }

            // Check if workspace directory is accessible
            if (!Directory.Exists(_options.WorkspaceRoot))
            {
                Directory.CreateDirectory(_options.WorkspaceRoot);
            }

            await Task.Delay(10, cancellationToken); // Simulate async check
            return HealthCheckResult.Healthy("GraphRAG components are accessible");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GraphRAG health check failed");
            return HealthCheckResult.Unhealthy("GraphRAG components are not accessible", ex);
        }
    }
}
