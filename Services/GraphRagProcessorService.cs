using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SearchFrontend.Endpoints.GraphRAG.Configuration;
using SearchFrontend.Endpoints.GraphRAG.Interfaces;
using SearchFrontend.Endpoints.GraphRAG.Models.Analysis;
using SearchFrontend.Endpoints.GraphRAG.Models.Entities;
using SearchFrontend.Endpoints.GraphRAG.Models.Requests;
using SearchFrontend.Endpoints.GraphRAG.Services.Analysis;
using SearchFrontend.Endpoints.GraphRAG.Services.ContentProcessing;
using SearchFrontend.Endpoints.GraphRAG.Services.GraphRag;
using SearchFrontend.Endpoints.GraphRAG.Services.Reporting;
using SearchFrontend.Endpoints.GraphRAG.Services.Storage;

namespace SearchFrontend.Endpoints.GraphRAG.Services
{
    /// <summary>
    /// Main service orchestrating GraphRAG analysis with parallel processing
    /// </summary>
    public class GraphRagProcessorService
    {
        private readonly ICaseLoader _caseLoader;
        private readonly IContentProcessingService _contentProcessor;
        private readonly IGraphRagIndexingService _graphRagService;
        private readonly IAnalysisService _analysisService;
        private readonly IReportingService _reportingService;
        private readonly IStorageService _storageService;
        private readonly ICaseUploader _uploader;
        private readonly GraphRagConfiguration _config;
        private readonly ILogger<GraphRagProcessorService> _logger;

        public GraphRagProcessorService(
            ICaseLoader caseLoader,
            IContentProcessingService contentProcessor,
            IGraphRagIndexingService graphRagService,
            IAnalysisService analysisService,
            IReportingService reportingService,
            IStorageService storageService,
            ICaseUploader uploader,
            IOptions<GraphRagConfiguration> config,
            ILogger<GraphRagProcessorService> logger)
        {
            _caseLoader = caseLoader;
            _contentProcessor = contentProcessor;
            _graphRagService = graphRagService;
            _analysisService = analysisService;
            _reportingService = reportingService;
            _storageService = storageService;
            _uploader = uploader;
            _config = config.Value;
            _logger = logger;
        }

        /// <summary>
        /// Runs comprehensive GraphRAG analysis with parallel processing
        /// </summary>
        public async Task<GraphRagAnalysisResult> RunComprehensiveAnalysisAsync(
            IngestionRequest request,
            CancellationToken ct = default)
        {
            var analysisResult = new GraphRagAnalysisResult
            {
                ProductId = request.ProductId,
                AnalysisTimestamp = DateTime.UtcNow
            };

            var stopwatch = Stopwatch.StartNew();

            try
            {
                _logger.LogInformation("Starting comprehensive analysis for ProductId: {ProductId}", request.ProductId);

                // Phase 1: Load and process case data in parallel
                var (caseRecords, processingDecision) = await LoadAndAnalyzeCaseDataAsync(request, ct);
                analysisResult.TotalCasesAnalyzed = caseRecords.Count;

                if (!processingDecision.ShouldProcess)
                {
                    return HandleSkippedProcessing(analysisResult, processingDecision);
                }

                // Phase 2: Build case documents with parallel processing
                var caseDocuments = await BuildCaseDocumentsAsync(caseRecords, ct);

                // Phase 3: GraphRAG indexing
                var indexResult = await _graphRagService.PrepareAndIndexAsync(caseDocuments, request, ct);
                analysisResult.RootDirectory = indexResult.RootDirectory;
                analysisResult.OutputDirectory = indexResult.OutputDirectory;

                // Phase 4: Parallel comprehensive analysis
                await PerformParallelAnalysisAsync(analysisResult, indexResult.OutputDirectory, request, ct);

                // Phase 5: Generate reports and save results in parallel
                await FinalizeResultsAsync(analysisResult, request, ct);

                stopwatch.Stop();
                analysisResult.Metrics.ProcessingTime = stopwatch.Elapsed;

                _logger.LogInformation("Comprehensive analysis completed for ProductId: {ProductId} in {Duration}",
                    request.ProductId, stopwatch.Elapsed);

                return analysisResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Comprehensive analysis failed for ProductId: {ProductId}", request.ProductId);
                throw;
            }
        }

        /// <summary>
        /// Loads case data and makes processing decision
        /// </summary>
        private async Task<(List<ClosedCaseRecord> records, ProcessingDecision decision)> LoadAndAnalyzeCaseDataAsync(
            IngestionRequest request, 
            CancellationToken ct)
        {
            var loadTask = _caseLoader.GetClosedCasesAsync(
                request.ProductId,
                request.SapPath ?? "",
                request.CaseFilter,
                request.NumCasesToFetch,
                request.MaxAgeDays,
                ct);

            var records = await loadTask;

            if (records.Count == 0)
            {
                _logger.LogWarning("No cases found for ProductId: {ProductId}", request.ProductId);
                return (records, new ProcessingDecision { ShouldProcess = false, Reason = "No cases found" });
            }

            // Analyze if processing is needed
            var decision = await _contentProcessor.MakeProcessingDecisionAsync(records, request, ct);
            
            return (records, decision);
        }

        /// <summary>
        /// Builds case documents with parallel processing
        /// </summary>
        private async Task<List<CaseDocument>> BuildCaseDocumentsAsync(
            List<ClosedCaseRecord> records, 
            CancellationToken ct)
        {
            _logger.LogInformation("Building case documents for {Count} records", records.Count);

            var semaphore = new SemaphoreSlim(_config.MaxConcurrentBatches, _config.MaxConcurrentBatches);
            var results = new ConcurrentBag<CaseDocument>();

            var tasks = records.Select(async record =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    var document = await _contentProcessor.BuildCaseDocumentAsync(record, ct);
                    results.Add(document);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);

            var documents = results.ToList();
            _logger.LogInformation("Built {Count} case documents", documents.Count);

            return documents;
        }

        /// <summary>
        /// Performs comprehensive analysis in parallel
        /// </summary>
        private async Task PerformParallelAnalysisAsync(
            GraphRagAnalysisResult result,
            string outputDirectory,
            IngestionRequest request,
            CancellationToken ct)
        {
            _logger.LogInformation("Starting parallel comprehensive analysis");

            var analysisOptions = new AnalysisOptions
            {
                RootDirectory = result.RootDirectory,
                OutputDirectory = outputDirectory,
                ProductId = request.ProductId,
                PerformSelfHelpAnalysis = true,
                PerformFMEAAnalysis = true,
                PerformEntityAnalysis = true,
                PerformCommunityAnalysis = true
            };

            // Run all analysis types in parallel
            var analysisTask = Task.WhenAll(
                _analysisService.PerformSelfHelpGapAnalysisAsync(analysisOptions, ct)
                    .ContinueWith(t => result.SelfHelpGaps = t.Result, ct),
                    
                _analysisService.PerformFMEAAnalysisAsync(analysisOptions, ct)
                    .ContinueWith(t => result.FMEAAnalysis = t.Result, ct),
                    
                _analysisService.PerformEntityRelationshipAnalysisAsync(analysisOptions, ct)
                    .ContinueWith(t => 
                    {
                        result.EntityInsights = t.Result.EntityInsights;
                        result.RelationshipInsights = t.Result.RelationshipInsights;
                        result.Metrics.TotalEntitiesExtracted = t.Result.EntityInsights.Count;
                        result.Metrics.TotalRelationshipsFound = t.Result.RelationshipInsights.Count;
                    }, ct),
                    
                _analysisService.PerformCommunityAnalysisAsync(analysisOptions, ct)
                    .ContinueWith(t => 
                    {
                        result.CommunityInsights = t.Result;
                        result.Metrics.TotalCommunitiesIdentified = t.Result.Count;
                    }, ct)
            );

            await analysisTask;

            // Generate recommendations based on analysis results
            result.Recommendations = _analysisService.GenerateActionableRecommendations(result);

            _logger.LogInformation("Parallel comprehensive analysis completed");
        }

        /// <summary>
        /// Finalizes results with parallel report generation and storage
        /// </summary>
        private async Task FinalizeResultsAsync(
            GraphRagAnalysisResult result,
            IngestionRequest request,
            CancellationToken ct)
        {
            var tasks = new List<Task>();

            // Generate reports if requested
            if (request.GenerateReports)
            {
                tasks.Add(_reportingService.GenerateAndSaveReportsAsync(result, ct));
            }

            // Save to storage systems in parallel
            if (request.SaveToCosmosDB && _config.EnableCosmosDB)
            {
                tasks.Add(_storageService.SaveToCosmosDBAsync(result, ct));
            }

            if (request.SaveToAzureDataExplorer && _config.EnableAzureDataExplorer)
            {
                tasks.Add(_storageService.SaveToAzureDataExplorerAsync(result, ct));
            }

            // Upload processed files
            tasks.Add(_uploader.UploadAsync(result.OutputDirectory, ct));

            // Send callback if specified
            if (!string.IsNullOrWhiteSpace(request.CallbackUri))
            {
                tasks.Add(SendCallbackAsync(request.CallbackUri, result, ct));
            }

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Handles cases where processing is skipped
        /// </summary>
        private GraphRagAnalysisResult HandleSkippedProcessing(
            GraphRagAnalysisResult result,
            ProcessingDecision decision)
        {
            _logger.LogInformation("Skipping processing: {Reason}", decision.Reason);

            result.ProcessingSkipped = true;
            result.SkipReason = decision.Reason;
            result.SkipDetails = decision.SkipReasons;

            // Try to load existing results if available
            if (decision.Decision == "UseExisting")
            {
                // Implementation would load existing analysis results
                _logger.LogInformation("Using existing analysis results");
            }

            return result;
        }

        /// <summary>
        /// Sends callback notification
        /// </summary>
        private async Task SendCallbackAsync(string callbackUri, GraphRagAnalysisResult result, CancellationToken ct)
        {
            try
            {
                using var client = new HttpClient();
                var payload = new
                {
                    Status = "Success",
                    AnalysisId = result.AnalysisId,
                    ProductId = result.ProductId,
                    TotalCasesAnalyzed = result.TotalCasesAnalyzed,
                    TopGaps = result.SelfHelpGaps.IdentifiedGaps.Take(5).Select(g => g.SuggestedTitle),
                    TopRisks = result.FMEAAnalysis.HighRiskItems.Take(5).Select(r => r.FailureMode),
                    RecommendationsCount = result.Recommendations.Count,
                    ProcessingTime = result.Metrics.ProcessingTime.TotalMinutes
                };

                var json = System.Text.Json.JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                await client.PostAsync(callbackUri, content, ct);

                _logger.LogInformation("Callback sent successfully to: {CallbackUri}", callbackUri);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send callback to: {CallbackUri}", callbackUri);
            }
        }
    }

    /// <summary>
    /// Options for analysis operations
    /// </summary>
    public class AnalysisOptions
    {
        public string RootDirectory { get; set; } = "";
        public string OutputDirectory { get; set; } = "";
        public string ProductId { get; set; } = "";
        public bool PerformSelfHelpAnalysis { get; set; } = true;
        public bool PerformFMEAAnalysis { get; set; } = true;
        public bool PerformEntityAnalysis { get; set; } = true;
        public bool PerformCommunityAnalysis { get; set; } = true;
    }

    /// <summary>
    /// Processing decision information
    /// </summary>
    public class ProcessingDecision
    {
        public bool ShouldProcess { get; set; }
        public string Decision { get; set; } = "";
        public string Reason { get; set; } = "";
        public List<string> SkipReasons { get; set; } = new();
        public ProcessingStats Stats { get; set; } = new();
    }

    /// <summary>
    /// Processing statistics
    /// </summary>
    public class ProcessingStats
    {
        public int TotalCasesRequested { get; set; }
        public int NewCases { get; set; }
        public int UpdatedCases { get; set; }
        public int UnchangedCases { get; set; }
        public int TotalExistingCases { get; set; }
        public bool SelfHelpContentChanged { get; set; }
        public DateTime LastProcessingTime { get; set; }
        public TimeSpan TimeSinceLastProcess { get; set; }
    }

    /// <summary>
    /// Case document model
    /// </summary>
    public class CaseDocument
    {
        public string CaseId { get; set; } = "";
        public string Content { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public int ContentLength => Content?.Length ?? 0;
    }

    /// <summary>
    /// GraphRAG indexing result
    /// </summary>
    public class GraphRagIndexResult
    {
        public string RootDirectory { get; set; } = "";
        public string OutputDirectory { get; set; } = "";
        public bool IndexingCompleted { get; set; }
        public TimeSpan IndexingTime { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
    }
}