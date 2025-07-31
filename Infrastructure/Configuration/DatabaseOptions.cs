using System.ComponentModel.DataAnnotations;

namespace SearchFrontend.Infrastructure.Configuration
{
    public class DatabaseOptions
    {
        public const string SectionName = "Database";

        [Required]
        public string UdpServer { get; set; } = "";

        [Required]
        public string UdpDatabase { get; set; } = "";

        [Required]
        public string SupportServer { get; set; } = "";

        [Required]
        public string SupportDatabase { get; set; } = "";
    }

    public class GraphRagOptions
    {
        public const string SectionName = "GraphRag";

        [Required]
        public string PythonPath { get; set; } = "";

        [Required]
        public string WorkspaceRoot { get; set; } = @"C:\New folder\GraphRAG";

        [Required]
        public string AzureOpenAIKey { get; set; } = "";

        [Required]
        public string AzureOpenAIEndpoint { get; set; } = "";

        public int MaxConcurrentIndexing { get; set; } = 3;
        public int BatchSize { get; set; } = 75;
        public int MaxRetryAttempts { get; set; } = 3;
    }

    public class ProcessingOptions
    {
        public const string SectionName = "Processing";

        public int MaxConcurrentCaseProcessing { get; set; } = 10;
        public int MaxConcurrentBatches { get; set; } = 5;
        public int DefaultCacheExpirationMinutes { get; set; } = 60;
        public bool EnableIncrementalProcessing { get; set; } = true;
        public int FullReindexThresholdDays { get; set; } = 7;
        public int MaxIncrementalUpdates { get; set; } = 10;
    }
}