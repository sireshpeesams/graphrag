using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using SearchFrontend.Endpoints.GraphRAG.Models.Requests;
using SearchFrontend.Endpoints.GraphRAG.Services;

namespace SearchFrontend.Endpoints.GraphRAG.Functions
{
    /// <summary>
    /// Azure Function for GraphRAG processing with optimized architecture
    /// </summary>
    public class GraphRagFunction
    {
        private readonly GraphRagProcessorService _processorService;
        private readonly ILogger<GraphRagFunction> _logger;

        public GraphRagFunction(
            GraphRagProcessorService processorService,
            ILogger<GraphRagFunction> logger)
        {
            _processorService = processorService;
            _logger = logger;
        }

        /// <summary>
        /// Main endpoint for GraphRAG ingestion and analysis
        /// </summary>
        [Function("ProcessGraphRagIngestion")]
        public async Task<HttpResponseData> ProcessIngestionAsync(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req,
            CancellationToken ct = default)
        {
            var correlationId = Guid.NewGuid().ToString();
            
            using var scope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["CorrelationId"] = correlationId,
                ["FunctionName"] = "ProcessGraphRagIngestion"
            });

            _logger.LogInformation("GraphRAG ingestion started with correlation ID: {CorrelationId}", correlationId);

            try
            {
                // Parse and validate request
                var request = await ParseAndValidateRequestAsync(req, ct);
                if (request == null)
                {
                    return await CreateErrorResponseAsync(req, HttpStatusCode.BadRequest, 
                        "Invalid request format or missing required fields");
                }

                _logger.LogInformation("Processing request for ProductId: {ProductId}", request.ProductId);

                // Start background processing
                _ = ProcessInBackgroundAsync(request, correlationId, ct);

                // Return immediate response
                var response = new
                {
                    Status = "Accepted",
                    CorrelationId = correlationId,
                    Message = "GraphRAG analysis started successfully",
                    ProductId = request.ProductId,
                    EstimatedProcessingTime = "5-15 minutes"
                };

                return await CreateSuccessResponseAsync(req, HttpStatusCode.Accepted, response);
            }
            catch (ValidationException ex)
            {
                _logger.LogWarning("Validation failed: {Message}", ex.Message);
                return await CreateErrorResponseAsync(req, HttpStatusCode.BadRequest, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GraphRAG function failed with correlation ID: {CorrelationId}", correlationId);
                return await CreateErrorResponseAsync(req, HttpStatusCode.InternalServerError, 
                    "An internal error occurred. Please try again later.");
            }
        }

        /// <summary>
        /// Health check endpoint
        /// </summary>
        [Function("GraphRagHealthCheck")]
        public async Task<HttpResponseData> HealthCheckAsync(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req)
        {
            try
            {
                var healthStatus = new
                {
                    Status = "Healthy",
                    Timestamp = DateTime.UtcNow,
                    Version = "2.0.0",
                    Service = "GraphRAG Processor"
                };

                return await CreateSuccessResponseAsync(req, HttpStatusCode.OK, healthStatus);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Health check failed");
                return await CreateErrorResponseAsync(req, HttpStatusCode.ServiceUnavailable, 
                    "Service is temporarily unavailable");
            }
        }

        /// <summary>
        /// Parses and validates the incoming request
        /// </summary>
        private async Task<IngestionRequest?> ParseAndValidateRequestAsync(HttpRequestData req, CancellationToken ct)
        {
            try
            {
                var body = await new StreamReader(req.Body).ReadToEndAsync();
                
                if (string.IsNullOrWhiteSpace(body))
                {
                    throw new ValidationException("Request body cannot be empty");
                }

                var request = JsonSerializer.Deserialize<IngestionRequest>(body, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (request == null)
                {
                    throw new ValidationException("Failed to parse request body");
                }

                // Validate the request
                var validationResults = new List<ValidationResult>();
                var validationContext = new ValidationContext(request);
                
                if (!Validator.TryValidateObject(request, validationContext, validationResults, true))
                {
                    var errors = string.Join("; ", validationResults.Select(r => r.ErrorMessage));
                    throw new ValidationException($"Validation failed: {errors}");
                }

                return request;
            }
            catch (JsonException ex)
            {
                throw new ValidationException($"Invalid JSON format: {ex.Message}");
            }
        }

        /// <summary>
        /// Processes the request in the background
        /// </summary>
        private async Task ProcessInBackgroundAsync(
            IngestionRequest request, 
            string correlationId, 
            CancellationToken ct)
        {
            try
            {
                using var scope = _logger.BeginScope(new Dictionary<string, object>
                {
                    ["CorrelationId"] = correlationId,
                    ["ProductId"] = request.ProductId,
                    ["BackgroundProcessing"] = true
                });

                _logger.LogInformation("Starting background GraphRAG processing");

                var result = await _processorService.RunComprehensiveAnalysisAsync(request, ct);

                _logger.LogInformation("Background GraphRAG processing completed successfully. " +
                    "AnalysisId: {AnalysisId}, Cases: {CasesAnalyzed}, Duration: {Duration}",
                    result.AnalysisId, result.TotalCasesAnalyzed, result.Metrics.ProcessingTime);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Background processing was cancelled for correlation ID: {CorrelationId}", correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background GraphRAG processing failed for correlation ID: {CorrelationId}", correlationId);
                
                // Optionally send failure notification
                await SendFailureNotificationAsync(request, correlationId, ex);
            }
        }

        /// <summary>
        /// Sends failure notification if callback URI is provided
        /// </summary>
        private async Task SendFailureNotificationAsync(
            IngestionRequest request, 
            string correlationId, 
            Exception exception)
        {
            if (string.IsNullOrWhiteSpace(request.CallbackUri))
                return;

            try
            {
                using var client = new HttpClient();
                var payload = new
                {
                    Status = "Failed",
                    CorrelationId = correlationId,
                    ProductId = request.ProductId,
                    Error = exception.Message,
                    Timestamp = DateTime.UtcNow
                };

                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

                await client.PostAsync(request.CallbackUri, content);
                
                _logger.LogInformation("Failure notification sent to callback URI");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send failure notification");
            }
        }

        /// <summary>
        /// Creates a success HTTP response
        /// </summary>
        private async Task<HttpResponseData> CreateSuccessResponseAsync<T>(
            HttpRequestData req, 
            HttpStatusCode statusCode, 
            T data)
        {
            var response = req.CreateResponse(statusCode);
            response.Headers.Add("Content-Type", "application/json");
            response.Headers.Add("X-Content-Type-Options", "nosniff");
            response.Headers.Add("X-Frame-Options", "DENY");

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            await response.WriteStringAsync(json);
            return response;
        }

        /// <summary>
        /// Creates an error HTTP response
        /// </summary>
        private async Task<HttpResponseData> CreateErrorResponseAsync(
            HttpRequestData req, 
            HttpStatusCode statusCode, 
            string message)
        {
            var response = req.CreateResponse(statusCode);
            response.Headers.Add("Content-Type", "application/json");
            response.Headers.Add("X-Content-Type-Options", "nosniff");
            response.Headers.Add("X-Frame-Options", "DENY");

            var errorResponse = new
            {
                Error = message,
                Timestamp = DateTime.UtcNow,
                StatusCode = (int)statusCode
            };

            var json = JsonSerializer.Serialize(errorResponse, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            await response.WriteStringAsync(json);
            return response;
        }
    }
}