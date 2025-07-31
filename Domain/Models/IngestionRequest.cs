using System.ComponentModel.DataAnnotations;

namespace SearchFrontend.Domain.Models
{
    public class IngestionRequest
    {
        [Required]
        public string ProductId { get; set; } = "";
        
        public string SapPath { get; set; } = "";
        
        [RegularExpression("^(single|multi|full)$", ErrorMessage = "Mode must be 'single', 'multi', or 'full'")]
        public string Mode { get; set; } = "multi";
        
        [Range(1, 10000, ErrorMessage = "NumCasesToFetch must be between 1 and 10000")]
        public int NumCasesToFetch { get; set; } = 1;
        
        public bool SkipExisting { get; set; } = true;
        public bool SkipSearch { get; set; } = true;
        
        [Range(1, 365, ErrorMessage = "MaxAgeDays must be between 1 and 365")]
        public int? MaxAgeDays { get; set; } = 14;
        
        [Url(ErrorMessage = "CallbackUri must be a valid URL")]
        public string CallbackUri { get; set; } = "";
        
        public string CaseFilter { get; set; } = null;
        public string RootDir { get; set; } = "";
        public bool GenerateReports { get; set; } = true;
        public bool SaveToCosmosDB { get; set; } = false;
        public bool SaveToAzureDataExplorer { get; set; } = false;
        public bool ForceReindex { get; set; } = false;
        
        [RegularExpression("^(track|merge|replace)$", ErrorMessage = "UpdateStrategy must be 'track', 'merge', or 'replace'")]
        public string UpdateStrategy { get; set; } = "track";
    }
}