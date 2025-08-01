namespace SearchFrontend.Endpoints.GraphRAG.Configuration
{
    /// <summary>
    /// Configuration settings for GraphRAG processing
    /// </summary>
    public class GraphRagConfiguration
    {
        public const string SectionName = "GraphRAG";

        public string UdpSqlServer { get; set; } = "";
        public string UdpSqlDatabase { get; set; } = "";
        public string SupportSqlServer { get; set; } = "";
        public string SupportSqlDatabase { get; set; } = "";
        public string PythonPath { get; set; } = "";
        public string AzureOpenAIKey { get; set; } = "";
        public string WorkspaceRoot { get; set; } = @"C:\New folder\GraphRAG";
        
        // Processing settings
        public int MaxConcurrentBatches { get; set; } = 4;
        public int BatchSize { get; set; } = 75;
        public int MaxRetryAttempts { get; set; } = 3;
        public int RetryDelaySeconds { get; set; } = 2;
        
        // GraphRAG specific settings
        public int ChunkSize { get; set; } = 800;
        public int ChunkOverlap { get; set; } = 100;
        public string EmbeddingModel { get; set; } = "text-embedding-ada-002";
        public string CompletionModel { get; set; } = "gpt-4";
        
        // Storage settings
        public bool EnableCosmosDB { get; set; } = false;
        public bool EnableAzureDataExplorer { get; set; } = false;
        public string CosmosConnectionString { get; set; } = "";
        public string KustoConnectionString { get; set; } = "";
    }
}