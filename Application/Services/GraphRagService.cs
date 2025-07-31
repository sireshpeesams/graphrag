using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using SearchFrontend.Domain.Interfaces;
using SearchFrontend.Domain.Models;
using SearchFrontend.Infrastructure.Configuration;

namespace SearchFrontend.Application.Services
{
    public sealed class GraphRagService : IGraphRagService, IDisposable
    {
        private readonly ICaseRepository _caseRepository;
        private readonly IAnalysisEngine _analysisEngine;
        private readonly IProcessingDecisionEngine _decisionEngine;
        private readonly IReportGenerator _reportGenerator;
        private readonly IContentUploader _contentUploader;
        private readonly ILogger<GraphRagService> _logger;
        private readonly GraphRagOptions _graphRagOptions;
        private readonly ProcessingOptions _processingOptions;
        
        private readonly SemaphoreSlim _indexingSemaphore;
        private readonly SemaphoreSlim _processingTasksSemaphore;
        private readonly Channel<CaseProcessingTask> _processingChannel;

        public GraphRagService(
            ICaseRepository caseRepository,
            IAnalysisEngine analysisEngine,
            IProcessingDecisionEngine decisionEngine,
            IReportGenerator reportGenerator,
            IContentUploader contentUploader,
            ILogger<GraphRagService> logger,
            IOptions<GraphRagOptions> graphRagOptions,
            IOptions<ProcessingOptions> processingOptions)
        {
            _caseRepository = caseRepository ?? throw new ArgumentNullException(nameof(caseRepository));
            _analysisEngine = analysisEngine ?? throw new ArgumentNullException(nameof(analysisEngine));
            _decisionEngine = decisionEngine ?? throw new ArgumentNullException(nameof(decisionEngine));
            _reportGenerator = reportGenerator ?? throw new ArgumentNullException(nameof(reportGenerator));
            _contentUploader = contentUploader ?? throw new ArgumentNullException(nameof(contentUploader));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _graphRagOptions = graphRagOptions?.Value ?? throw new ArgumentNullException(nameof(graphRagOptions));
            _processingOptions = processingOptions?.Value ?? throw new ArgumentNullException(nameof(processingOptions));

            _indexingSemaphore = new SemaphoreSlim(_graphRagOptions.MaxConcurrentIndexing, _graphRagOptions.MaxConcurrentIndexing);
            _processingTasksSemaphore = new SemaphoreSlim(_processingOptions.MaxConcurrentCaseProcessing, _processingOptions.MaxConcurrentCaseProcessing);
            
            var channelOptions = new BoundedChannelOptions(_processingOptions.MaxConcurrentCaseProcessing * 2)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false
            };
            _processingChannel = Channel.CreateBounded<CaseProcessingTask>(channelOptions);
        }

        public async Task<GraphRagAnalysisResult> ProcessAnalysisAsync(
            IngestionRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            var analysisResult = new GraphRagAnalysisResult
            {
                ProductId = request.ProductId,
                AnalysisTimestamp = DateTime.UtcNow
            };

            var stopwatch = Stopwatch.StartNew();

            try
            {
                _logger.LogInformation(
                    "Starting comprehensive analysis for ProductId: {ProductId}",
                    request.ProductId);

                // Step 1: Load case data with parallel processing
                var caseRecords = await LoadCaseDataAsync(request, cancellationToken);
                analysisResult.TotalCasesAnalyzed = caseRecords.Count;

                if (caseRecords.Count == 0)
                {
                    _logger.LogWarning("No cases found for ProductId: {ProductId}", request.ProductId);
                    return analysisResult;
                }

                // Step 2: Build case documents with parallel processing
                var caseDocuments = await BuildCaseDocumentsAsync(caseRecords, cancellationToken);

                // Step 3: Make processing decision
                var processingDecision = await _decisionEngine.MakeDecisionAsync(request, cancellationToken);
                LogProcessingDecision(processingDecision);

                if (!processingDecision.ShouldProcess)
                {
                    _logger.LogInformation("Skipping processing: {Reason}", processingDecision.Reason);
                    
                    var existingResult = await LoadExistingAnalysisResultAsync(request.ProductId, request, cancellationToken);
                    if (existingResult != null)
                    {
                        existingResult.SkipReason = processingDecision.Reason;
                        existingResult.ProcessingSkipped = true;
                        existingResult.SkipDetails = processingDecision.SkipReasons.ToList();
                        return existingResult;
                    }

                    analysisResult.ProcessingSkipped = true;
                    analysisResult.SkipReason = processingDecision.Reason;
                    analysisResult.SkipDetails = processingDecision.SkipReasons.ToList();
                    return analysisResult;
                }

                _logger.LogInformation("Processing required: {Reason}", processingDecision.Reason);

                // Step 4: Prepare and index with GraphRAG
                var graphRagResult = await PrepareAndIndexWithGraphRAGAsync(
                    caseDocuments, 
                    request, 
                    processingDecision, 
                    cancellationToken);
                
                analysisResult.RootDirectory = graphRagResult.RootDir;
                analysisResult.OutputDirectory = graphRagResult.OutputDir;

                // Step 5: Perform comprehensive analysis with parallel processing
                await PerformComprehensiveAnalysisAsync(analysisResult, graphRagResult.OutputDir, request, cancellationToken);

                // Step 6: Generate reports (if requested)
                if (request.GenerateReports)
                {
                    await _reportGenerator.GenerateReportsAsync(analysisResult, request, cancellationToken);
                }

                // Step 7: Save to external systems (parallel execution)
                var saveTasks = new List<Task>();

                if (request.SaveToCosmosDB)
                {
                    saveTasks.Add(SaveToCosmosDBAsync(analysisResult, cancellationToken));
                }

                if (request.SaveToAzureDataExplorer)
                {
                    saveTasks.Add(SaveToAzureDataExplorerAsync(analysisResult, cancellationToken));
                }

                if (saveTasks.Any())
                {
                    await Task.WhenAll(saveTasks);
                }

                // Step 8: Upload results
                await _contentUploader.UploadAsync(graphRagResult.OutputDir, cancellationToken);

                // Step 9: Send callback (if configured)
                if (!string.IsNullOrWhiteSpace(request.CallbackUri))
                {
                    await SendCallbackAsync(request.CallbackUri, analysisResult, cancellationToken);
                }

                stopwatch.Stop();
                analysisResult.Metrics.ProcessingTime = stopwatch.Elapsed;

                _logger.LogInformation(
                    "Comprehensive analysis completed for ProductId: {ProductId} in {Duration}",
                    request.ProductId,
                    stopwatch.Elapsed);

                return analysisResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Comprehensive analysis failed for ProductId: {ProductId}",
                    request.ProductId);
                throw;
            }
        }

        public async Task<string> QueryAsync(
            string rootDir,
            string query,
            string method = "global",
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(rootDir))
                throw new ArgumentException("Root directory cannot be null or empty", nameof(rootDir));

            if (string.IsNullOrWhiteSpace(query))
                throw new ArgumentException("Query cannot be null or empty", nameof(query));

            try
            {
                _logger.LogInformation("Executing GraphRAG query: {Query}", query);

                var queryArgs = $"-m graphrag query --root \"{rootDir}\" --method {method} --query \"{query}\"";
                var result = await ExecutePythonCommandAsync(_graphRagOptions.PythonPath, queryArgs, cancellationToken);

                _logger.LogInformation("Query completed, result length: {Length}", result.Length);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GraphRAG query failed for query: {Query}", query);
                throw;
            }
        }

        private async Task<IReadOnlyList<ClosedCaseRecord>> LoadCaseDataAsync(
            IngestionRequest request,
            CancellationToken cancellationToken = default)
        {
            return await _caseRepository.GetClosedCasesAsync(
                request.ProductId,
                request.SapPath ?? "",
                request.CaseFilter,
                request.NumCasesToFetch,
                request.MaxAgeDays,
                cancellationToken);
        }

        private async Task<IReadOnlyList<CaseDocument>> BuildCaseDocumentsAsync(
            IReadOnlyList<ClosedCaseRecord> records,
            CancellationToken cancellationToken = default)
        {
            var results = new ConcurrentBag<CaseDocument>();
            var processingTasks = new List<Task>();

            // Create processing tasks for case documents
            foreach (var record in records)
            {
                var task = ProcessCaseRecordAsync(record, results, cancellationToken);
                processingTasks.Add(task);
            }

            await Task.WhenAll(processingTasks);

            return results.ToList().AsReadOnly();
        }

        private async Task ProcessCaseRecordAsync(
            ClosedCaseRecord record,
            ConcurrentBag<CaseDocument> results,
            CancellationToken cancellationToken = default)
        {
            await _processingTasksSemaphore.WaitAsync(cancellationToken);
            try
            {
                var caseDocument = await FetchCaseDocumentAsync(record, cancellationToken);
                results.Add(caseDocument);
            }
            finally
            {
                _processingTasksSemaphore.Release();
            }
        }

        private async Task<CaseDocument> FetchCaseDocumentAsync(
            ClosedCaseRecord record,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var metaText = BuildCaseMetadata(record);

                // Fetch emails and notes in parallel
                var emailTask = _caseRepository.GetEmailThreadAsync(record.SRNumber, cancellationToken);
                var noteTask = _caseRepository.GetNotesThreadAsync(record.SRNumber, cancellationToken);

                await Task.WhenAll(emailTask, noteTask);

                var emails = emailTask.Result;
                var notes = noteTask.Result;

                var customerProblems = BuildCustomerProblemsSection(emails);
                var resolutionSteps = BuildResolutionStepsSection(notes);

                var combinedContent = metaText + customerProblems + resolutionSteps;
                var contentHash = ComputeContentHash(combinedContent);

                return new CaseDocument
                {
                    CaseId = record.SRNumber,
                    Content = combinedContent,
                    ContentHash = contentHash,
                    CreatedAt = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to build document for case: {CaseId}", record.SRNumber);
                return new CaseDocument
                {
                    CaseId = record.SRNumber,
                    Content = string.Empty,
                    ContentHash = string.Empty,
                    CreatedAt = DateTime.UtcNow
                };
            }
        }

        private static string BuildCaseMetadata(ClosedCaseRecord record)
        {
            return $@"
=== SUPPORT CASE: {record.SRNumber} ===
Product: {record.CurrentProductName}
Area: {record.SAPPath}
Title: {record.InternalTitle}
Severity: {record.SRSeverity} (Max: {record.SRMaxSeverity})
Resolution Time: {record.CaseAge} days
Customer Interactions: {record.EmailInteractions} emails, {record.PhoneInteractions} calls
Case Age: {record.CaseAge} days
Idle Days: {record.CaseIdleDays} case, {record.CommunicationIdleDays} communication

=== CUSTOMER PROBLEM ===
";
        }

        private static string BuildCustomerProblemsSection(IReadOnlyList<string> emails)
        {
            if (!emails.Any()) return "";

            return "CUSTOMER COMMUNICATIONS:\n" +
                   string.Join("\n---\n", emails.Take(3)) + "\n\n";
        }

        private static string BuildResolutionStepsSection(IReadOnlyList<string> notes)
        {
            if (!notes.Any()) return "";

            return "=== RESOLUTION STEPS ===\n" +
                   string.Join("\n---\n", notes) + "\n";
        }

        private async Task<GraphRAGIndexResult> PrepareAndIndexWithGraphRAGAsync(
            IReadOnlyList<CaseDocument> documents,
            IngestionRequest request,
            ProcessingDecision decision,
            CancellationToken cancellationToken = default)
        {
            await _indexingSemaphore.WaitAsync(cancellationToken);
            try
            {
                var workRoot = GetGraphRagWorkRoot(request);
                Directory.CreateDirectory(workRoot);

                var productCumulativeDir = Path.Combine(workRoot, $"Product_{request.ProductId}_cumulative");
                var inputDir = Path.Combine(productCumulativeDir, "input");

                _logger.LogInformation(
                    "Using cumulative analysis for ProductId: {ProductId}",
                    request.ProductId);

                Directory.CreateDirectory(productCumulativeDir);
                Directory.CreateDirectory(inputDir);

                // Write case documents to files
                var writeTasks = documents.Select(async doc =>
                {
                    var caseFilePath = Path.Combine(inputDir, $"case_{doc.CaseId}.txt");
                    await File.WriteAllTextAsync(caseFilePath, doc.Content ?? "", cancellationToken);
                });

                await Task.WhenAll(writeTasks);

                // Add self-help content
                await AddSelfHelpContentToInputAsync(inputDir, request.ProductId, workRoot, cancellationToken);

                var outputDir = Path.Combine(productCumulativeDir, "output");

                // Run GraphRAG indexing if needed
                if (documents.Count > 0 || !Directory.Exists(outputDir))
                {
                    await RunGraphRagIndexingAsync(productCumulativeDir, request, cancellationToken);
                }

                outputDir = Directory.GetDirectories(Path.Combine(productCumulativeDir, "output"))
                    .OrderByDescending(d => d)
                    .FirstOrDefault() ?? "";

                if (string.IsNullOrEmpty(outputDir))
                    throw new DirectoryNotFoundException("No output directory found after indexing.");

                _logger.LogInformation(
                    "GraphRAG processing completed for ProductId: {ProductId}",
                    request.ProductId);

                return new GraphRAGIndexResult
                {
                    RootDir = productCumulativeDir,
                    OutputDir = outputDir,
                    Success = true
                };
            }
            finally
            {
                _indexingSemaphore.Release();
            }
        }

        private async Task PerformComprehensiveAnalysisAsync(
            GraphRagAnalysisResult result,
            string outputDir,
            IngestionRequest input,
            CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Starting comprehensive analysis...");

                // Run analysis tasks in parallel
                var analysisTasks = new List<Task>
                {
                    Task.Run(async () =>
                    {
                        result.SelfHelpGaps = await _analysisEngine.PerformSelfHelpGapAnalysisAsync(
                            result.RootDirectory, cancellationToken);
                    }, cancellationToken),

                    Task.Run(async () =>
                    {
                        result.FMEAAnalysis = await _analysisEngine.PerformFMEAAnalysisAsync(
                            result.RootDirectory, cancellationToken);
                    }, cancellationToken),

                    Task.Run(async () =>
                    {
                        await _analysisEngine.PerformEntityRelationshipAnalysisAsync(
                            result, outputDir, cancellationToken);
                    }, cancellationToken)
                };

                await Task.WhenAll(analysisTasks);

                // Generate actionable recommendations
                result.Recommendations = GenerateActionableRecommendations(result);

                _logger.LogInformation("Comprehensive analysis completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Comprehensive analysis failed");
                throw;
            }
        }

        private void LogProcessingDecision(ProcessingDecision decision)
        {
            _logger.LogInformation("Processing Decision: {Decision}", decision.Decision);
            _logger.LogInformation("Reason: {Reason}", decision.Reason);
            _logger.LogInformation("Stats: New={New}, Updated={Updated}, Unchanged={Unchanged}",
                decision.Stats.NewCases,
                decision.Stats.UpdatedCases,
                decision.Stats.UnchangedCases);

            if (decision.SkipReasons.Any())
            {
                _logger.LogInformation("Skip reasons: {SkipReasons}",
                    string.Join("; ", decision.SkipReasons));
            }
        }

        private async Task<GraphRagAnalysisResult?> LoadExistingAnalysisResultAsync(
            string productId,
            IngestionRequest input,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var workRoot = GetGraphRagWorkRoot(input);
                var productDir = Path.Combine(workRoot, $"Product_{productId}_cumulative");
                var reportsDir = Path.Combine(productDir, "reports");

                if (!Directory.Exists(reportsDir))
                    return null;

                var jsonFiles = Directory.GetFiles(reportsDir, "analysis_result_*.json")
                    .OrderByDescending(f => f)
                    .ToArray();

                if (jsonFiles.Length == 0)
                    return null;

                var json = await File.ReadAllTextAsync(jsonFiles[0], cancellationToken);
                return JsonConvert.DeserializeObject<GraphRagAnalysisResult>(json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load existing analysis result");
                return null;
            }
        }

        private string GetGraphRagWorkRoot(IngestionRequest? input = null)
        {
            return !string.IsNullOrEmpty(input?.RootDir) 
                ? input.RootDir 
                : _graphRagOptions.WorkspaceRoot;
        }

        private static string ComputeContentHash(string content)
        {
            using var sha256 = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(content ?? "");
            var hash = sha256.ComputeHash(bytes);
            return Convert.ToBase64String(hash);
        }

        private async Task RunGraphRagIndexingAsync(
            string rootDir,
            IngestionRequest request,
            CancellationToken cancellationToken = default)
        {
            var settingsPath = Path.Combine(rootDir, "settings.yaml");
            var graphragDirPath = Path.Combine(rootDir, ".graphrag");
            var graphragSettingsPath = Path.Combine(graphragDirPath, "settings.yaml");

            // Initialize GraphRAG if needed
            if (!File.Exists(settingsPath) && !File.Exists(graphragSettingsPath))
            {
                _logger.LogInformation("Initializing GraphRAG...");
                Environment.SetEnvironmentVariable("GRAPHRAG_API_KEY", _graphRagOptions.AzureOpenAIKey);
                
                var initArgs = $"-m graphrag init --root \"{rootDir}\"";
                await ExecutePythonCommandAsync(_graphRagOptions.PythonPath, initArgs, cancellationToken);
            }

            // Write settings
            var settingsContent = BuildGraphRagSettings();
            await File.WriteAllTextAsync(settingsPath, settingsContent, cancellationToken);

            _logger.LogInformation("Running GraphRAG indexing...");
            var indexArgs = $"-m graphrag index --root \"{rootDir}\" --verbose";
            await ExecutePythonCommandAsync(_graphRagOptions.PythonPath, indexArgs, cancellationToken);

            _logger.LogInformation("GraphRAG indexing completed for: {RootDir}", rootDir);
        }

        private async Task<string> ExecutePythonCommandAsync(
            string pythonExe,
            string arguments,
            CancellationToken cancellationToken = default)
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = processStartInfo };
            var output = new StringBuilder();
            var error = new StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    output.AppendLine(e.Data);
                    _logger.LogDebug("[Python] {Output}", e.Data);
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    error.AppendLine(e.Data);
                    _logger.LogWarning("[Python Error] {Error}", e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                var errorMessage = $"Python process exited with code {process.ExitCode}. Error: {error}";
                throw new InvalidOperationException(errorMessage);
            }

            return output.ToString();
        }

        private string BuildGraphRagSettings()
        {
            return $@"
encoding_model: cl100k_base
skip_workflows: []
llm:
  api_key: {_graphRagOptions.AzureOpenAIKey}
  type: azure_openai_chat
  model: gpt-4o
  model_supports_json: true
  max_tokens: 4000
  temperature: 0
  top_p: 1
  n: 1
  api_base: {_graphRagOptions.AzureOpenAIEndpoint}
  api_version: 2024-02-15-preview
  organization: 
  deployment_name: gpt-4o
  tokens_per_minute: 150_000
  requests_per_minute: 10_000
  max_retries: 10
  max_retry_wait: 10.0
  sleep_on_rate_limit_recommendation: true
  concurrent_requests: 25

parallelization:
  stagger: 0.3
  num_threads: 50

async_mode: threaded

embeddings:
  async_mode: threaded
  llm:
    api_key: {_graphRagOptions.AzureOpenAIKey}
    type: azure_openai_embedding
    model: text-embedding-3-large
    api_base: {_graphRagOptions.AzureOpenAIEndpoint}
    api_version: 2024-02-15-preview
    organization: 
    deployment_name: text-embedding-3-large
    tokens_per_minute: 150_000
    requests_per_minute: 10_000
    max_retries: 10
    max_retry_wait: 10.0
    sleep_on_rate_limit_recommendation: true
    concurrent_requests: 25
  parallelization:
    stagger: 0.3
    num_threads: 50
  batch_size: 16
  batch_max_tokens: 8191
  target: required

chunks:
  size: 300
  overlap: 100
  group_by_columns: [id]

input:
  type: file
  file_type: text
  base_dir: ""input""
  file_encoding: utf-8
  file_pattern: "".*\\.txt$""

cache:
  type: file
  base_dir: ""cache""

storage:
  type: file
  base_dir: ""output""

reporting:
  type: file
  base_dir: ""reports""

entity_extraction:
  prompt: ""prompts/entity_extraction.txt""
  entity_types: [organization,person,geo,event]
  max_gleanings: 0

summarize_descriptions:
  prompt: ""prompts/summarize_descriptions.txt""
  max_length: 500

claim_extraction:
  prompt: ""prompts/claim_extraction.txt""
  description: ""Any claims or facts that could be relevant to information discovery.""
  max_gleanings: 0

community_reports:
  prompt: ""prompts/community_report.txt""
  max_length: 2000
  max_input_length: 8000

cluster_graph:
  max_cluster_size: 10

embed_graph:
  enabled: false

umap:
  enabled: false

snapshots:
  graphml: false
  raw_entities: false
  top_level_nodes: false

local_search:
  text_unit_prop: 0.5
  community_prop: 0.1
  conversation_history_max_turns: 5
  top_k_mapped_entities: 10
  top_k_relationships: 10
  max_tokens: 12000

global_search:
  max_tokens: 12000
  data_max_tokens: 12000
  map_max_tokens: 1000
  reduce_max_tokens: 2000
  concurrency: 32
";
        }

        private async Task AddSelfHelpContentToInputAsync(
            string inputDir,
            string productId,
            string workRoot,
            CancellationToken cancellationToken = default)
        {
            // This would be implemented based on your self-help content logic
            // For now, creating a placeholder
            var selfHelpPath = Path.Combine(inputDir, "selfhelp_placeholder.txt");
            var placeholderContent = $"Self-help content placeholder for product {productId}";
            await File.WriteAllTextAsync(selfHelpPath, placeholderContent, cancellationToken);
        }

        private List<ActionableRecommendation> GenerateActionableRecommendations(GraphRagAnalysisResult result)
        {
            var recommendations = new List<ActionableRecommendation>();

            // Generate recommendations based on gaps and FMEA analysis
            foreach (var gap in result.SelfHelpGaps.IdentifiedGaps.Where(g => g.CustomerSolvable).Take(5))
            {
                recommendations.Add(new ActionableRecommendation
                {
                    Category = "Self-Help Content Creation",
                    Title = $"Create {gap.SuggestedTitle}",
                    Description = $"Address {gap.ProblemArea} issues that appear in {gap.FrequencyInCases} cases",
                    Priority = gap.EstimatedImpact > 7 ? "High" : gap.EstimatedImpact > 4 ? "Medium" : "Low",
                    EstimatedImpact = gap.EstimatedImpact,
                    ActionOwner = "Content Team",
                    ExpectedOutcome = $"Reduce support cases for {gap.ProblemArea} by enabling customer self-service"
                });
            }

            return recommendations;
        }

        private async Task SaveToCosmosDBAsync(GraphRagAnalysisResult result, CancellationToken cancellationToken = default)
        {
            // Implementation would go here
            _logger.LogInformation("Saving analysis result to Cosmos DB: {AnalysisId}", result.AnalysisId);
            await Task.Delay(100, cancellationToken); // Placeholder
        }

        private async Task SaveToAzureDataExplorerAsync(GraphRagAnalysisResult result, CancellationToken cancellationToken = default)
        {
            // Implementation would go here
            _logger.LogInformation("Saving analysis result to Azure Data Explorer: {AnalysisId}", result.AnalysisId);
            await Task.Delay(100, cancellationToken); // Placeholder
        }

        private async Task SendCallbackAsync(string callbackUri, GraphRagAnalysisResult result, CancellationToken cancellationToken = default)
        {
            // Implementation would go here
            _logger.LogInformation("Sending callback to: {CallbackUri}", callbackUri);
            await Task.Delay(100, cancellationToken); // Placeholder
        }

        public void Dispose()
        {
            _indexingSemaphore?.Dispose();
            _processingTasksSemaphore?.Dispose();
        }
    }

    // Supporting classes for the service
    public record CaseProcessingTask
    {
        public ClosedCaseRecord Record { get; init; } = new();
        public TaskCompletionSource<CaseDocument> CompletionSource { get; init; } = new();
        public CancellationToken CancellationToken { get; init; }
    }
}