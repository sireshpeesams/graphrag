using System.ComponentModel.DataAnnotations;

namespace SearchFrontend.Endpoints.GraphRAG.Models.Requests
{
    /// <summary>
    /// Request model for GraphRAG ingestion operations
    /// </summary>
    public class IngestionRequest
    {
        [Required]
        public string ProductId { get; set; } = "";
        
        public string SapPath { get; set; } = "";
        
        public string Mode { get; set; } = "multi";
        
        [Range(1, 10000)]
        public int NumCasesToFetch { get; set; } = 1;
        
        public bool SkipExisting { get; set; } = true;
        
        public bool SkipSearch { get; set; } = true;
        
        [Range(1, 365)]
        public int? MaxAgeDays { get; set; } = 14;
        
        public string CallbackUri { get; set; } = "";
        
        public string CaseFilter { get; set; } = null;
        
        public string RootDir { get; set; } = "";
        
        public bool GenerateReports { get; set; } = true;
        
        public bool SaveToCosmosDB { get; set; } = false;
        
        public bool SaveToAzureDataExplorer { get; set; } = false;
        
        public bool ForceReindex { get; set; } = false;
        
        public string UpdateStrategy { get; set; } = "track";
    }
}