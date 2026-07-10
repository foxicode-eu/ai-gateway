using Core.Providers;

namespace Core.Tests.Providers;

internal sealed class FakeStreamResponseWriter : IStreamResponseWriter
{
    public int StatusCode { get; private set; } = 200;
    public string? ContentType { get; private set; }
    public MemoryStream Body { get; } = new();

    Stream IStreamResponseWriter.Body => Body;

    public void SetStatusCode(int statusCode) => StatusCode = statusCode;

    public void SetContentType(string contentType) => ContentType = contentType;

    public string BodyAsString() => System.Text.Encoding.UTF8.GetString(Body.ToArray());
}
