using ScreenSub.Core.Enums;
using ScreenSub.Core.Models;

namespace ScreenSub.Core.Interfaces;

public interface IModelManager
{
    IReadOnlyList<ModelMetadata> GetModels(ModelCategory? category = null);
    ModelMetadata? GetModel(string modelId);
    string GetModelStorageDirectory();
    void SetModelStorageDirectory(string path);
    Task<bool> InstallModelAsync(string modelId, IProgress<double>? progress = null, CancellationToken ct = default);
    Task<bool> DeleteModelAsync(string modelId);
    Task<bool> VerifyModelIntegrityAsync(string modelId);
    Task<bool> ActivateModelAsync(string modelId);
}

