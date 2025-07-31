using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SearchFrontend.Application.Services;
using SearchFrontend.Domain.Interfaces;
using SearchFrontend.Domain.Models;
using SearchFrontend.Infrastructure.Configuration;
using Xunit;

namespace SearchFrontend.Tests.Unit.Services
{
    public class GraphRagServiceTests : IDisposable
    {
        private readonly Mock<ICaseRepository> _mockCaseRepository;
        private readonly Mock<IAnalysisEngine> _mockAnalysisEngine;
        private readonly Mock<IProcessingDecisionEngine> _mockDecisionEngine;
        private readonly Mock<IReportGenerator> _mockReportGenerator;
        private readonly Mock<IContentUploader> _mockContentUploader;
        private readonly Mock<ILogger<GraphRagService>> _mockLogger;
        private readonly Mock<IOptions<GraphRagOptions>> _mockGraphRagOptions;
        private readonly Mock<IOptions<ProcessingOptions>> _mockProcessingOptions;
        private readonly GraphRagService _service;

        public GraphRagServiceTests()
        {
            _mockCaseRepository = new Mock<ICaseRepository>();
            _mockAnalysisEngine = new Mock<IAnalysisEngine>();
            _mockDecisionEngine = new Mock<IProcessingDecisionEngine>();
            _mockReportGenerator = new Mock<IReportGenerator>();
            _mockContentUploader = new Mock<IContentUploader>();
            _mockLogger = new Mock<ILogger<GraphRagService>>();
            _mockGraphRagOptions = new Mock<IOptions<GraphRagOptions>>();
            _mockProcessingOptions = new Mock<IOptions<ProcessingOptions>>();

            // Setup default options
            _mockGraphRagOptions.Setup(x => x.Value).Returns(new GraphRagOptions
            {
                PythonPath = "python",
                WorkspaceRoot = "/tmp/graphrag",
                AzureOpenAIKey = "test-key",
                AzureOpenAIEndpoint = "https://test.openai.azure.com/",
                MaxConcurrentIndexing = 2,
                BatchSize = 10,
                MaxRetryAttempts = 3
            });

            _mockProcessingOptions.Setup(x => x.Value).Returns(new ProcessingOptions
            {
                MaxConcurrentCaseProcessing = 5,
                MaxConcurrentBatches = 3,
                DefaultCacheExpirationMinutes = 30,
                EnableIncrementalProcessing = true,
                FullReindexThresholdDays = 7,
                MaxIncrementalUpdates = 10
            });

            _service = new GraphRagService(
                _mockCaseRepository.Object,
                _mockAnalysisEngine.Object,
                _mockDecisionEngine.Object,
                _mockReportGenerator.Object,
                _mockContentUploader.Object,
                _mockLogger.Object,
                _mockGraphRagOptions.Object,
                _mockProcessingOptions.Object);
        }

        [Fact]
        public async Task ProcessAnalysisAsync_WithValidRequest_ReturnsAnalysisResult()
        {
            // Arrange
            var request = new IngestionRequest
            {
                ProductId = "TEST-PRODUCT",
                NumCasesToFetch = 5,
                Mode = "multi"
            };

            var mockCases = CreateMockCaseRecords(5);
            var mockDecision = new ProcessingDecision
            {
                ShouldProcess = true,
                Decision = "Process",
                Reason = "New analysis requested",
                Stats = new ProcessingStats { TotalCasesRequested = 5 }
            };

            _mockCaseRepository
                .Setup(x => x.GetClosedCasesAsync(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(mockCases);

            _mockCaseRepository
                .Setup(x => x.GetEmailThreadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<string> { "Test email content" }.AsReadOnly());

            _mockCaseRepository
                .Setup(x => x.GetNotesThreadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<string> { "Test note content" }.AsReadOnly());

            _mockDecisionEngine
                .Setup(x => x.MakeDecisionAsync(It.IsAny<IngestionRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(mockDecision);

            _mockAnalysisEngine
                .Setup(x => x.PerformSelfHelpGapAnalysisAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SelfHelpGapAnalysis());

            _mockAnalysisEngine
                .Setup(x => x.PerformFMEAAnalysisAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new FMEAAnalysisResult());

            // Act
            var result = await _service.ProcessAnalysisAsync(request);

            // Assert
            Assert.NotNull(result);
            Assert.Equal("TEST-PRODUCT", result.ProductId);
            Assert.Equal(5, result.TotalCasesAnalyzed);
            Assert.False(result.ProcessingSkipped);
            Assert.True(result.Metrics.ProcessingTime > TimeSpan.Zero);

            // Verify interactions
            _mockCaseRepository.Verify(x => x.GetClosedCasesAsync(
                "TEST-PRODUCT",
                It.IsAny<string>(),
                It.IsAny<string>(),
                5,
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()), Times.Once);

            _mockDecisionEngine.Verify(x => x.MakeDecisionAsync(request, It.IsAny<CancellationToken>()), Times.Once);
            _mockAnalysisEngine.Verify(x => x.PerformSelfHelpGapAnalysisAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
            _mockAnalysisEngine.Verify(x => x.PerformFMEAAnalysisAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task ProcessAnalysisAsync_WithSkipDecision_ReturnsSkippedResult()
        {
            // Arrange
            var request = new IngestionRequest
            {
                ProductId = "TEST-PRODUCT",
                NumCasesToFetch = 5
            };

            var mockCases = CreateMockCaseRecords(5);
            var skipDecision = new ProcessingDecision
            {
                ShouldProcess = false,
                Decision = "UseExisting",
                Reason = "No changes detected",
                Stats = new ProcessingStats { TotalCasesRequested = 5 },
                SkipReasons = new List<string> { "All cases unchanged" }
            };

            _mockCaseRepository
                .Setup(x => x.GetClosedCasesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(mockCases);

            _mockDecisionEngine
                .Setup(x => x.MakeDecisionAsync(It.IsAny<IngestionRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(skipDecision);

            // Act
            var result = await _service.ProcessAnalysisAsync(request);

            // Assert
            Assert.NotNull(result);
            Assert.True(result.ProcessingSkipped);
            Assert.Equal("No changes detected", result.SkipReason);
            Assert.Contains("All cases unchanged", result.SkipDetails);

            // Verify that analysis was not performed
            _mockAnalysisEngine.Verify(x => x.PerformSelfHelpGapAnalysisAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            _mockAnalysisEngine.Verify(x => x.PerformFMEAAnalysisAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task ProcessAnalysisAsync_WithNoCases_ReturnsEmptyResult()
        {
            // Arrange
            var request = new IngestionRequest
            {
                ProductId = "EMPTY-PRODUCT",
                NumCasesToFetch = 10
            };

            _mockCaseRepository
                .Setup(x => x.GetClosedCasesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<ClosedCaseRecord>().AsReadOnly());

            // Act
            var result = await _service.ProcessAnalysisAsync(request);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(0, result.TotalCasesAnalyzed);
            Assert.Equal("EMPTY-PRODUCT", result.ProductId);

            // Verify that processing decision was not made when no cases found
            _mockDecisionEngine.Verify(x => x.MakeDecisionAsync(It.IsAny<IngestionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task QueryAsync_WithInvalidRootDir_ThrowsArgumentException(string rootDir)
        {
            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(() =>
                _service.QueryAsync(rootDir, "test query"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task QueryAsync_WithInvalidQuery_ThrowsArgumentException(string query)
        {
            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(() =>
                _service.QueryAsync("/valid/root", query));
        }

        [Fact]
        public async Task ProcessAnalysisAsync_WithNullRequest_ThrowsArgumentNullException()
        {
            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                _service.ProcessAnalysisAsync(null));
        }

        [Fact]
        public async Task ProcessAnalysisAsync_WithCancellation_ThrowsOperationCanceledException()
        {
            // Arrange
            var request = new IngestionRequest
            {
                ProductId = "TEST-PRODUCT",
                NumCasesToFetch = 5
            };

            var cancellationTokenSource = new CancellationTokenSource();
            cancellationTokenSource.Cancel();

            _mockCaseRepository
                .Setup(x => x.GetClosedCasesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new OperationCanceledException());

            // Act & Assert
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                _service.ProcessAnalysisAsync(request, cancellationTokenSource.Token));
        }

        private static List<ClosedCaseRecord> CreateMockCaseRecords(int count)
        {
            var records = new List<ClosedCaseRecord>();
            for (int i = 1; i <= count; i++)
            {
                records.Add(new ClosedCaseRecord
                {
                    SRNumber = $"SR{i:D6}",
                    PESProductID = "TEST-PRODUCT",
                    CurrentProductName = "Test Product",
                    SRCreationDateTime = DateTime.UtcNow.AddDays(-i),
                    SRClosedDateTime = DateTime.UtcNow.AddDays(-i + 1),
                    SROwner = "Test Owner",
                    SRStatus = "Closed",
                    ServiceRequestStatus = "Resolved",
                    SRSeverity = "Medium",
                    SRMaxSeverity = "High",
                    InternalTitle = $"Test Case {i}",
                    FirstAssignmentDateTime = DateTime.UtcNow.AddDays(-i),
                    CaseIdleDays = 1,
                    CommunicationIdleDays = 0,
                    IsS500Program = false,
                    CollaborationTasks = 2,
                    PhoneInteractions = 1,
                    EmailInteractions = 3,
                    IRStatus = "None",
                    IsTransferred = false,
                    SAPPath = "Test/Path",
                    SupportTopicID = "Topic123",
                    SupportTopicIDL2 = "Topic",
                    SupportTopicIDL3 = "123",
                    CaseAge = i
                });
            }
            return records;
        }

        public void Dispose()
        {
            _service?.Dispose();
        }
    }

    // Additional test classes for other services
    public class CacheServiceTests
    {
        private readonly Mock<ILogger<HybridCachingService>> _mockLogger;
        private readonly Mock<IOptions<ProcessingOptions>> _mockOptions;
        private readonly MemoryCache _memoryCache;
        private readonly HybridCachingService _cachingService;

        public CacheServiceTests()
        {
            _mockLogger = new Mock<ILogger<HybridCachingService>>();
            _mockOptions = new Mock<IOptions<ProcessingOptions>>();
            _mockOptions.Setup(x => x.Value).Returns(new ProcessingOptions
            {
                DefaultCacheExpirationMinutes = 60
            });

            _memoryCache = new MemoryCache(new MemoryCacheOptions());
            _cachingService = new HybridCachingService(_memoryCache, _mockLogger.Object, _mockOptions.Object);
        }

        [Fact]
        public async Task GetOrSetAsync_WithCacheMiss_CallsFactory()
        {
            // Arrange
            const string key = "test-key";
            const string expectedValue = "test-value";
            var factoryCalled = false;

            // Act
            var result = await _cachingService.GetOrSetAsync(key, _ =>
            {
                factoryCalled = true;
                return Task.FromResult(expectedValue);
            });

            // Assert
            Assert.Equal(expectedValue, result);
            Assert.True(factoryCalled);
        }

        [Fact]
        public async Task GetOrSetAsync_WithCacheHit_DoesNotCallFactory()
        {
            // Arrange
            const string key = "test-key";
            const string cachedValue = "cached-value";
            const string factoryValue = "factory-value";

            await _cachingService.SetAsync(key, cachedValue);

            var factoryCalled = false;

            // Act
            var result = await _cachingService.GetOrSetAsync(key, _ =>
            {
                factoryCalled = true;
                return Task.FromResult(factoryValue);
            });

            // Assert
            Assert.Equal(cachedValue, result);
            Assert.False(factoryCalled);
        }
    }
}

// Required using statements for the tests
using Microsoft.Extensions.Caching.Memory;
using SearchFrontend.Infrastructure.Services;