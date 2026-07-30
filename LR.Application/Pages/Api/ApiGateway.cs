namespace LR.Application.Pages.Api;

/// <summary>
/// Routes incoming API requests to the appropriate protocol handler based on path prefix.
/// </summary>
public class ApiGateway
{
    private readonly Dictionary<string, IProtocolHandler> _handlers = new();

    public void Register(IProtocolHandler handler)
    {
        _handlers[handler.PathPrefix] = handler;
    }

    /// <summary>
    /// Find the handler for a given path.
    /// </summary>
    public IProtocolHandler? GetHandlerForPath(string path)
    {
        foreach (var kvp in _handlers)
        {
            if (path.StartsWith(kvp.Key, StringComparison.Ordinal))
                return kvp.Value;
        }
        return null;
    }
}
