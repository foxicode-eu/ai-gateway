namespace Core.Providers;

/// <summary>
/// Thin abstraction over an HTTP response used while streaming, so <c>Core</c> doesn't need to depend on
/// ASP.NET Core's <c>HttpResponse</c> type. Lets a provider client switch the outer response to a non-streaming
/// JSON error (status code + content type) if the upstream provider fails before any SSE data was sent — this
/// only works if it happens before the first write to <see cref="Body"/>, since headers can't change after that.
/// </summary>
public interface IStreamResponseWriter
{
    void SetStatusCode(int statusCode);

    void SetContentType(string contentType);

    Stream Body { get; }
}
