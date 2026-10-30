using System.Net;
using System.Text;

namespace Fyrd.Core.Tests;

public sealed record RecordedRequest(HttpRequestMessage Request, string? Body);

public sealed class FakeHandler(HttpStatusCode status = HttpStatusCode.OK, string responseBody = "{}") : HttpMessageHandler
{
    public List<RecordedRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Add(new RecordedRequest(request, body));
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
        };
    }
}
