using System.Net;
using System.Text;

namespace Fyrd.Core.Tests;

public sealed record RecordedRequest(HttpRequestMessage Request, string? Body);

public sealed class FakeHandler(HttpStatusCode status = HttpStatusCode.OK, Func<string>? nextBody = null) : HttpMessageHandler
{
    public FakeHandler(HttpStatusCode status, string responseBody) : this(status, () => responseBody) { }

    public List<RecordedRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Add(new RecordedRequest(request, body));
        return new HttpResponseMessage(status)
        {
            Content = new StringContent((nextBody ?? (() => "{}"))(), Encoding.UTF8, "application/json"),
        };
    }
}
