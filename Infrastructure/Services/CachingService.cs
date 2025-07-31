using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SearchFrontend.Infrastructure.Configuration;

namespace SearchFrontend.Infrastructure.Services
{
    public interface ICachingService
    {
        Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class;
        Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, CancellationToken cancellationToken = default) where T : class;
        Task RemoveAsync(string key, CancellationToken cancellationToken = default);
        Task RemoveByPatternAsync(string pattern, CancellationToken cancellationToken = default);
        Task<T> GetOrSetAsync<T>(string key, Func<CancellationToken, Task<T>> factory, TimeSpan? expiration = null, CancellationToken cancellationToken = default) where T : class;
    }

    public sealed class HybridCachingService : ICachingService, IDisposable
    {
        private readonly IMemoryCache _memoryCache;
        private readonly IDistributedCache? _distributedCache;
        private readonly ILogger<HybridCachingService> _logger;
        private readonly ProcessingOptions _options;
        private readonly SemaphoreSlim _semaphore;

        // Cache key prefixes for different data types
        private const string CaseDataPrefix = "case_data:";
        private const string AnalysisResultPrefix = "analysis_result:";
        private const string ProcessingDecisionPrefix = "processing_decision:";
        private const string GraphRagQueryPrefix = "graphrag_query:";

        public HybridCachingService(
            IMemoryCache memoryCache,
            ILogger<HybridCachingService> logger,
            IOptions<ProcessingOptions> options,
            IDistributedCache? distributedCache = null)
        {
            _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
            _distributedCache = distributedCache;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _semaphore = new SemaphoreSlim(1, 1);
        }

        public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Cache key cannot be null or empty", nameof(key));

            var normalizedKey = NormalizeKey(key);

            try
            {
                // Try memory cache first (fastest)
                if (_memoryCache.TryGetValue(normalizedKey, out var memoryValue) && memoryValue is T typedMemoryValue)
                {
                    _logger.LogDebug("Cache hit (memory): {Key}", normalizedKey);
                    return typedMemoryValue;
                }

                // Try distributed cache if available
                if (_distributedCache != null)
                {
                    var distributedValue = await _distributedCache.GetStringAsync(normalizedKey, cancellationToken);
                    if (!string.IsNullOrEmpty(distributedValue))
                    {
                        var deserializedValue = JsonSerializer.Deserialize<T>(distributedValue);
                        if (deserializedValue != null)
                        {
                            // Store in memory cache for faster subsequent access
                            var memoryExpiration = TimeSpan.FromMinutes(_options.DefaultCacheExpirationMinutes / 2);
                            _memoryCache.Set(normalizedKey, deserializedValue, memoryExpiration);

                            _logger.LogDebug("Cache hit (distributed): {Key}", normalizedKey);
                            return deserializedValue;
                        }
                    }
                }

                _logger.LogDebug("Cache miss: {Key}", normalizedKey);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error retrieving from cache: {Key}", normalizedKey);
                return null;
            }
        }

        public async Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, CancellationToken cancellationToken = default) where T : class
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Cache key cannot be null or empty", nameof(key));

            if (value == null)
                throw new ArgumentNullException(nameof(value));

            var normalizedKey = NormalizeKey(key);
            var effectiveExpiration = expiration ?? TimeSpan.FromMinutes(_options.DefaultCacheExpirationMinutes);

            try
            {
                // Store in memory cache
                var memoryOptions = new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = effectiveExpiration,
                    Priority = GetCachePriority(key),
                    Size = EstimateSize(value)
                };

                _memoryCache.Set(normalizedKey, value, memoryOptions);

                // Store in distributed cache if available
                if (_distributedCache != null)
                {
                    var serializedValue = JsonSerializer.Serialize(value);
                    var distributedOptions = new DistributedCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = effectiveExpiration
                    };

                    await _distributedCache.SetStringAsync(normalizedKey, serializedValue, distributedOptions, cancellationToken);
                }

                _logger.LogDebug("Cache set: {Key}, Expiration: {Expiration}", normalizedKey, effectiveExpiration);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error setting cache: {Key}", normalizedKey);
                throw;
            }
        }

        public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Cache key cannot be null or empty", nameof(key));

            var normalizedKey = NormalizeKey(key);

            try
            {
                // Remove from memory cache
                _memoryCache.Remove(normalizedKey);

                // Remove from distributed cache if available
                if (_distributedCache != null)
                {
                    await _distributedCache.RemoveAsync(normalizedKey, cancellationToken);
                }

                _logger.LogDebug("Cache removed: {Key}", normalizedKey);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error removing from cache: {Key}", normalizedKey);
                throw;
            }
        }

        public async Task RemoveByPatternAsync(string pattern, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                throw new ArgumentException("Pattern cannot be null or empty", nameof(pattern));

            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                // For memory cache, we can't easily remove by pattern, so we'll clear specific known patterns
                if (pattern.StartsWith(CaseDataPrefix) || pattern.StartsWith(AnalysisResultPrefix) ||
                    pattern.StartsWith(ProcessingDecisionPrefix) || pattern.StartsWith(GraphRagQueryPrefix))
                {
                    // In a real implementation, you might maintain a registry of cache keys
                    // For now, we'll log that pattern-based removal was requested
                    _logger.LogInformation("Pattern-based cache removal requested: {Pattern}", pattern);
                }

                // Note: Distributed cache pattern removal would require a cache implementation
                // that supports pattern-based operations (like Redis with SCAN commands)
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task<T> GetOrSetAsync<T>(
            string key,
            Func<CancellationToken, Task<T>> factory,
            TimeSpan? expiration = null,
            CancellationToken cancellationToken = default) where T : class
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Cache key cannot be null or empty", nameof(key));

            if (factory == null)
                throw new ArgumentNullException(nameof(factory));

            // Try to get from cache first
            var cachedValue = await GetAsync<T>(key, cancellationToken);
            if (cachedValue != null)
            {
                return cachedValue;
            }

            // Use semaphore to prevent cache stampede
            await _semaphore.WaitAsync(cancellationToken);
            try
            {
                // Double-check pattern: check cache again after acquiring lock
                cachedValue = await GetAsync<T>(key, cancellationToken);
                if (cachedValue != null)
                {
                    return cachedValue;
                }

                // Generate the value
                _logger.LogDebug("Cache miss, generating value for key: {Key}", key);
                var value = await factory(cancellationToken);

                // Store in cache
                await SetAsync(key, value, expiration, cancellationToken);

                return value;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        // Helper methods for cache key management
        public static string CreateCaseDataKey(string productId, string sapPath, int numCases, int? maxAgeDays)
        {
            var keyBuilder = new StringBuilder(CaseDataPrefix);
            keyBuilder.Append(productId);
            keyBuilder.Append(':');
            keyBuilder.Append(sapPath?.Replace('/', '_').Replace('\\', '_') ?? "all");
            keyBuilder.Append(':');
            keyBuilder.Append(numCases);
            keyBuilder.Append(':');
            keyBuilder.Append(maxAgeDays?.ToString() ?? "all");

            return keyBuilder.ToString();
        }

        public static string CreateAnalysisResultKey(string productId, string analysisId)
        {
            return $"{AnalysisResultPrefix}{productId}:{analysisId}";
        }

        public static string CreateProcessingDecisionKey(string productId, string requestHash)
        {
            return $"{ProcessingDecisionPrefix}{productId}:{requestHash}";
        }

        public static string CreateGraphRagQueryKey(string rootDir, string query, string method)
        {
            var queryHash = ComputeHash($"{rootDir}:{query}:{method}");
            return $"{GraphRagQueryPrefix}{queryHash}";
        }

        private static string NormalizeKey(string key)
        {
            // Ensure key is valid for all cache providers
            return key.Replace(' ', '_').Replace(':', '_').ToLowerInvariant();
        }

        private static CacheItemPriority GetCachePriority(string key)
        {
            return key switch
            {
                var k when k.StartsWith(AnalysisResultPrefix) => CacheItemPriority.High,
                var k when k.StartsWith(CaseDataPrefix) => CacheItemPriority.Normal,
                var k when k.StartsWith(ProcessingDecisionPrefix) => CacheItemPriority.High,
                var k when k.StartsWith(GraphRagQueryPrefix) => CacheItemPriority.Low,
                _ => CacheItemPriority.Normal
            };
        }

        private static long EstimateSize<T>(T value) where T : class
        {
            try
            {
                // Simple size estimation based on JSON serialization
                var json = JsonSerializer.Serialize(value);
                return Encoding.UTF8.GetByteCount(json);
            }
            catch
            {
                // Fallback to a reasonable default
                return 1024; // 1KB
            }
        }

        private static string ComputeHash(string input)
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(input);
            var hash = sha256.ComputeHash(bytes);
            return Convert.ToBase64String(hash)[..16]; // Take first 16 characters
        }

        public void Dispose()
        {
            _semaphore?.Dispose();
        }
    }

    // Extension methods for easier cache usage
    public static class CachingServiceExtensions
    {
        public static async Task<T> GetCaseDataAsync<T>(
            this ICachingService cache,
            string productId,
            string sapPath,
            int numCases,
            int? maxAgeDays,
            Func<CancellationToken, Task<T>> factory,
            CancellationToken cancellationToken = default) where T : class
        {
            var key = HybridCachingService.CreateCaseDataKey(productId, sapPath, numCases, maxAgeDays);
            var expiration = TimeSpan.FromMinutes(30); // Case data changes less frequently
            return await cache.GetOrSetAsync(key, factory, expiration, cancellationToken);
        }

        public static async Task<T> GetAnalysisResultAsync<T>(
            this ICachingService cache,
            string productId,
            string analysisId,
            Func<CancellationToken, Task<T>> factory,
            CancellationToken cancellationToken = default) where T : class
        {
            var key = HybridCachingService.CreateAnalysisResultKey(productId, analysisId);
            var expiration = TimeSpan.FromHours(24); // Analysis results are more stable
            return await cache.GetOrSetAsync(key, factory, expiration, cancellationToken);
        }

        public static async Task<T> GetGraphRagQueryResultAsync<T>(
            this ICachingService cache,
            string rootDir,
            string query,
            string method,
            Func<CancellationToken, Task<T>> factory,
            CancellationToken cancellationToken = default) where T : class
        {
            var key = HybridCachingService.CreateGraphRagQueryKey(rootDir, query, method);
            var expiration = TimeSpan.FromMinutes(15); // Query results can be cached for a shorter time
            return await cache.GetOrSetAsync(key, factory, expiration, cancellationToken);
        }

        public static async Task InvalidateCaseDataAsync(
            this ICachingService cache,
            string productId,
            CancellationToken cancellationToken = default)
        {
            var pattern = $"case_data:{productId}:*";
            await cache.RemoveByPatternAsync(pattern, cancellationToken);
        }

        public static async Task InvalidateAnalysisResultsAsync(
            this ICachingService cache,
            string productId,
            CancellationToken cancellationToken = default)
        {
            var pattern = $"analysis_result:{productId}:*";
            await cache.RemoveByPatternAsync(pattern, cancellationToken);
        }
    }
}