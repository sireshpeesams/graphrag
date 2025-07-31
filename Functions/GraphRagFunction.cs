using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SearchFrontend.Domain.Interfaces;
using SearchFrontend.Domain.Models;
using SearchFrontend.Infrastructure.Exceptions;
using System.Linq; // Added for .Take()

namespace SearchFrontend.Functions
{
    public sealed class GraphRagFunction
    {
        private readonly IGraphRagService _graphRagService;
        private readonly ILogger<GraphRagFunction> _logger;

        public GraphRagFunction(
            IGraphRagService graphRagService,
            ILogger<GraphRagFunction> logger)
        {
            _graphRagService = graphRagService ?? throw new ArgumentNullException(nameof(graphRagService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        [Function("ProcessGraphRagIngestion")]
        public async Task<HttpResponseData> ProcessIngestionAsync(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData request)
        {
            var correlationId = Guid.NewGuid().ToString();
            using var scope = _logger.BeginScope(new { CorrelationId = correlationId });

            _logger.LogInformation(
                "GraphRAG ingestion request received at {Timestamp}",
                DateTime.UtcNow);

            try
            {
                // Step 1: Validate and parse request
                var ingestionRequest = await ValidateAndParseRequestAsync(request);

                _logger.LogInformation(
                    "Processing GraphRAG analysis for ProductId: {ProductId}, Mode: {Mode}, Cases: {NumCases}",
                    ingestionRequest.ProductId,
                    ingestionRequest.Mode,
                    ingestionRequest.NumCasesToFetch);

                // Step 2: Start background processing
                var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromHours(6)); // 6-hour timeout
                _ = ProcessAnalysisInBackgroundAsync(ingestionRequest, correlationId, cancellationTokenSource.Token);

                // Step 3: Return immediate response
                var acceptedResponse = new
                {
                    Status = "Accepted",
                    CorrelationId = correlationId,
                    ProductId = ingestionRequest.ProductId,
                    EstimatedProcessingTimeMinutes = EstimateProcessingTime(ingestionRequest),
                    Message = "Analysis has been queued for processing. Check the callback URL or logs for completion status."
                };

                return await CreateJsonResponseAsync(
                    request,
                    HttpStatusCode.Accepted,
                    acceptedResponse);
            }
            catch (ValidationException ex)
            {
                _logger.LogWarning(ex,
                    "Validation failed for GraphRAG ingestion request");

                var errorResponse = new
                {
                    Error = "ValidationFailed",
                    Message = ex.Message,
                    CorrelationId = correlationId
                };

                return await CreateJsonResponseAsync(
                    request,
                    HttpStatusCode.BadRequest,
                    errorResponse);
            }
            catch (BusinessLogicException ex)
            {
                _logger.LogWarning(ex,
                    "Business logic error in GraphRAG ingestion request");

                var errorResponse = new
                {
                    Error = "BusinessLogicError",
                    Message = ex.Message,
                    CorrelationId = correlationId
                };

                return await CreateJsonResponseAsync(
                    request,
                    HttpStatusCode.UnprocessableEntity,
                    errorResponse);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Unexpected error processing GraphRAG ingestion request");

                var errorResponse = new
                {
                    Error = "InternalServerError",
                    Message = "An unexpected error occurred while processing your request.",
                    CorrelationId = correlationId
                };

                return await CreateJsonResponseAsync(
                    request,
                    HttpStatusCode.InternalServerError,
                    errorResponse);
            }
        }

        [Function("QueryGraphRag")]
        public async Task<HttpResponseData> QueryAsync(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData request)
        {
            var correlationId = Guid.NewGuid().ToString();
            using var scope = _logger.BeginScope(new { CorrelationId = correlationId });

            _logger.LogInformation("GraphRAG query request received at {Timestamp}", DateTime.UtcNow);

            try
            {
                var queryRequest = await ValidateAndParseQueryRequestAsync(request);

                _logger.LogInformation(
                    "Executing GraphRAG query: {Query}, Method: {Method}",
                    queryRequest.Query,
                    queryRequest.Method);

                var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                var result = await _graphRagService.QueryAsync(
                    queryRequest.RootDir,
                    queryRequest.Query,
                    queryRequest.Method,
                    cancellationTokenSource.Token);

                var successResponse = new
                {
                    Status = "Success",
                    CorrelationId = correlationId,
                    Query = queryRequest.Query,
                    Method = queryRequest.Method,
                    Result = result,
                    ProcessingTimeMs = DateTime.UtcNow.Millisecond // This would be calculated properly
                };

                return await CreateJsonResponseAsync(
                    request,
                    HttpStatusCode.OK,
                    successResponse);
            }
            catch (ValidationException ex)
            {
                _logger.LogWarning(ex, "Validation failed for GraphRAG query request");

                var errorResponse = new
                {
                    Error = "ValidationFailed",
                    Message = ex.Message,
                    CorrelationId = correlationId
                };

                return await CreateJsonResponseAsync(
                    request,
                    HttpStatusCode.BadRequest,
                    errorResponse);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing GraphRAG query");

                var errorResponse = new
                {
                    Error = "QueryExecutionFailed",
                    Message = "Failed to execute the GraphRAG query.",
                    CorrelationId = correlationId
                };

                return await CreateJsonResponseAsync(
                    request,
                    HttpStatusCode.InternalServerError,
                    errorResponse);
            }
        }

        private async Task<IngestionRequest> ValidateAndParseRequestAsync(HttpRequestData request)
        {
            if (request.Body == null)
                throw new ValidationException("Request body cannot be empty.");

            string requestBody;
            using (var reader = new StreamReader(request.Body))
            {
                requestBody = await reader.ReadToEndAsync();
            }

            if (string.IsNullOrWhiteSpace(requestBody))
                throw new ValidationException("Request body cannot be empty.");

            IngestionRequest? ingestionRequest;
            try
            {
                ingestionRequest = JsonConvert.DeserializeObject<IngestionRequest>(requestBody);
            }
            catch (JsonException ex)
            {
                throw new ValidationException($"Invalid JSON format: {ex.Message}");
            }

            if (ingestionRequest == null)
                throw new ValidationException("Failed to parse request body.");

            // Validate the request using data annotations
            var validationContext = new ValidationContext(ingestionRequest);
            var validationResults = new System.Collections.Generic.List<ValidationResult>();

            if (!Validator.TryValidateObject(ingestionRequest, validationContext, validationResults, true))
            {
                var errors = string.Join("; ", validationResults.Select(vr => vr.ErrorMessage));
                throw new ValidationException($"Validation errors: {errors}");
            }

            // Additional business logic validation
            if (ingestionRequest.NumCasesToFetch <= 0)
                throw new ValidationException("NumCasesToFetch must be greater than 0.");

            if (ingestionRequest.MaxAgeDays.HasValue && ingestionRequest.MaxAgeDays.Value <= 0)
                throw new ValidationException("MaxAgeDays must be greater than 0 when specified.");

            return ingestionRequest;
        }

        private async Task<QueryRequest> ValidateAndParseQueryRequestAsync(HttpRequestData request)
        {
            if (request.Body == null)
                throw new ValidationException("Request body cannot be empty.");

            string requestBody;
            using (var reader = new StreamReader(request.Body))
            {
                requestBody = await reader.ReadToEndAsync();
            }

            if (string.IsNullOrWhiteSpace(requestBody))
                throw new ValidationException("Request body cannot be empty.");

            QueryRequest? queryRequest;
            try
            {
                queryRequest = JsonConvert.DeserializeObject<QueryRequest>(requestBody);
            }
            catch (JsonException ex)
            {
                throw new ValidationException($"Invalid JSON format: {ex.Message}");
            }

            if (queryRequest == null)
                throw new ValidationException("Failed to parse request body.");

            if (string.IsNullOrWhiteSpace(queryRequest.RootDir))
                throw new ValidationException("RootDir is required.");

            if (string.IsNullOrWhiteSpace(queryRequest.Query))
                throw new ValidationException("Query is required.");

            if (!System.IO.Directory.Exists(queryRequest.RootDir))
                throw new ValidationException($"RootDir does not exist: {queryRequest.RootDir}");

            return queryRequest;
        }

        private async Task ProcessAnalysisInBackgroundAsync(
            IngestionRequest request,
            string correlationId,
            CancellationToken cancellationToken)
        {
            using var scope = _logger.BeginScope(new { CorrelationId = correlationId, BackgroundTask = true });

            try
            {
                _logger.LogInformation(
                    "Starting background GraphRAG analysis for ProductId: {ProductId}",
                    request.ProductId);

                var result = await _graphRagService.ProcessAnalysisAsync(request, cancellationToken);

                _logger.LogInformation(
                    "Background GraphRAG analysis completed successfully for ProductId: {ProductId}. " +
                    "AnalysisId: {AnalysisId}, TotalCases: {TotalCases}, ProcessingTime: {ProcessingTime}",
                    request.ProductId,
                    result.AnalysisId,
                    result.TotalCasesAnalyzed,
                    result.Metrics.ProcessingTime);

                // Send success notification if callback URL is provided
                if (!string.IsNullOrWhiteSpace(request.CallbackUri))
                {
                    await SendCompletionCallbackAsync(request.CallbackUri, result, correlationId, true);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(
                    "Background GraphRAG analysis was cancelled for ProductId: {ProductId}",
                    request.ProductId);

                if (!string.IsNullOrWhiteSpace(request.CallbackUri))
                {
                    await SendCompletionCallbackAsync(request.CallbackUri, null, correlationId, false, "Analysis was cancelled due to timeout or system shutdown.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Background GraphRAG analysis failed for ProductId: {ProductId}",
                    request.ProductId);

                if (!string.IsNullOrWhiteSpace(request.CallbackUri))
                {
                    await SendCompletionCallbackAsync(request.CallbackUri, null, correlationId, false, ex.Message);
                }
            }
        }

        private async Task SendCompletionCallbackAsync(
            string callbackUri,
            GraphRagAnalysisResult? result,
            string correlationId,
            bool success,
            string? errorMessage = null)
        {
            try
            {
                using var httpClient = new System.Net.Http.HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(30);

                var callbackPayload = success && result != null
                    ? new
                    {
                        Status = "Completed",
                        Success = true,
                        CorrelationId = correlationId,
                        AnalysisId = result.AnalysisId,
                        ProductId = result.ProductId,
                        TotalCasesAnalyzed = result.TotalCasesAnalyzed,
                        ProcessingTime = result.Metrics.ProcessingTime,
                        TopRecommendations = result.Recommendations.Take(3).Select(r => new { r.Title, r.Priority, r.EstimatedImpact }),
                        CompletedAt = DateTime.UtcNow
                    }
                    : new
                    {
                        Status = "Failed",
                        Success = false,
                        CorrelationId = correlationId,
                        ErrorMessage = errorMessage ?? "Unknown error occurred",
                        CompletedAt = DateTime.UtcNow
                    };

                var json = JsonConvert.SerializeObject(callbackPayload);
                var content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");

                var response = await httpClient.PostAsync(callbackUri, content);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation(
                        "Completion callback sent successfully to {CallbackUri}",
                        callbackUri);
                }
                else
                {
                    _logger.LogWarning(
                        "Completion callback failed with status {StatusCode} for {CallbackUri}",
                        response.StatusCode,
                        callbackUri);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to send completion callback to {CallbackUri}",
                    callbackUri);
            }
        }

        private static int EstimateProcessingTime(IngestionRequest request)
        {
            // Simple estimation based on number of cases and mode
            var baseTimePerCase = request.Mode switch
            {
                "single" => 0.5,
                "multi" => 1.0,
                "full" => 2.0,
                _ => 1.0
            };

            var estimatedMinutes = (int)Math.Ceiling(request.NumCasesToFetch * baseTimePerCase);
            
            // Add overhead for indexing and analysis
            estimatedMinutes += request.GenerateReports ? 10 : 5;
            
            return Math.Max(estimatedMinutes, 5); // Minimum 5 minutes
        }

        private static async Task<HttpResponseData> CreateJsonResponseAsync(
            HttpRequestData request,
            HttpStatusCode statusCode,
            object responseObject)
        {
            var response = request.CreateResponse(statusCode);
            response.Headers.Add("Content-Type", "application/json; charset=utf-8");

            var json = JsonConvert.SerializeObject(responseObject, Formatting.Indented);
            await response.WriteStringAsync(json);

            return response;
        }
    }

    // Supporting models for the function
    public class QueryRequest
    {
        [Required]
        public string RootDir { get; set; } = "";

        [Required]
        public string Query { get; set; } = "";

        public string Method { get; set; } = "global";
    }
}