using System;

namespace SearchFrontend.Infrastructure.Exceptions
{
    /// <summary>
    /// Exception thrown when business logic validation fails
    /// </summary>
    public class BusinessLogicException : Exception
    {
        public string ErrorCode { get; }

        public BusinessLogicException(string message) : base(message)
        {
            ErrorCode = "BUSINESS_LOGIC_ERROR";
        }

        public BusinessLogicException(string message, string errorCode) : base(message)
        {
            ErrorCode = errorCode;
        }

        public BusinessLogicException(string message, Exception innerException) : base(message, innerException)
        {
            ErrorCode = "BUSINESS_LOGIC_ERROR";
        }

        public BusinessLogicException(string message, string errorCode, Exception innerException) : base(message, innerException)
        {
            ErrorCode = errorCode;
        }
    }

    /// <summary>
    /// Exception thrown when GraphRAG processing fails
    /// </summary>
    public class GraphRagProcessingException : Exception
    {
        public string ProductId { get; }
        public string ProcessingStage { get; }

        public GraphRagProcessingException(string message, string productId, string processingStage) : base(message)
        {
            ProductId = productId;
            ProcessingStage = processingStage;
        }

        public GraphRagProcessingException(string message, string productId, string processingStage, Exception innerException) 
            : base(message, innerException)
        {
            ProductId = productId;
            ProcessingStage = processingStage;
        }
    }

    /// <summary>
    /// Exception thrown when data access operations fail
    /// </summary>
    public class DataAccessException : Exception
    {
        public string Operation { get; }
        public string? EntityType { get; }

        public DataAccessException(string message, string operation) : base(message)
        {
            Operation = operation;
        }

        public DataAccessException(string message, string operation, string entityType) : base(message)
        {
            Operation = operation;
            EntityType = entityType;
        }

        public DataAccessException(string message, string operation, Exception innerException) : base(message, innerException)
        {
            Operation = operation;
        }

        public DataAccessException(string message, string operation, string entityType, Exception innerException) 
            : base(message, innerException)
        {
            Operation = operation;
            EntityType = entityType;
        }
    }

    /// <summary>
    /// Exception thrown when external service calls fail
    /// </summary>
    public class ExternalServiceException : Exception
    {
        public string ServiceName { get; }
        public int? StatusCode { get; }

        public ExternalServiceException(string message, string serviceName) : base(message)
        {
            ServiceName = serviceName;
        }

        public ExternalServiceException(string message, string serviceName, int statusCode) : base(message)
        {
            ServiceName = serviceName;
            StatusCode = statusCode;
        }

        public ExternalServiceException(string message, string serviceName, Exception innerException) 
            : base(message, innerException)
        {
            ServiceName = serviceName;
        }

        public ExternalServiceException(string message, string serviceName, int statusCode, Exception innerException) 
            : base(message, innerException)
        {
            ServiceName = serviceName;
            StatusCode = statusCode;
        }
    }

    /// <summary>
    /// Exception thrown when configuration is invalid or missing
    /// </summary>
    public class ConfigurationException : Exception
    {
        public string ConfigurationKey { get; }

        public ConfigurationException(string message, string configurationKey) : base(message)
        {
            ConfigurationKey = configurationKey;
        }

        public ConfigurationException(string message, string configurationKey, Exception innerException) 
            : base(message, innerException)
        {
            ConfigurationKey = configurationKey;
        }
    }
}