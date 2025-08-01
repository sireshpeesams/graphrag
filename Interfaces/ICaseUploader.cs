namespace SearchFrontend.Endpoints.GraphRAG.Interfaces
{
    /// <summary>
    /// Interface for uploading processed case data
    /// </summary>
    public interface ICaseUploader
    {
        Task UploadAsync(string path, CancellationToken ct = default);
    }
}