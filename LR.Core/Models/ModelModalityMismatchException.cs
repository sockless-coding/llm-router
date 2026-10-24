namespace LR.Core.Models;

/// <summary>
/// A request named a preset whose engine generates something else — a chat request for an image
/// model, or an image request for an LLM. Thrown by the routing engine before anything is started.
/// </summary>
public sealed class ModelModalityMismatchException(string modelName, EngineModality modelModality, EngineModality requestModality)
    : InvalidOperationException(modelModality == EngineModality.Image
        ? $"Model '{modelName}' is an image model — send it to /v1/images/generations instead."
        : $"Model '{modelName}' is a {modelModality.ToString().ToLowerInvariant()} model and can't serve {requestModality.ToString().ToLowerInvariant()} requests.")
{
    public string ModelName { get; } = modelName;
}
