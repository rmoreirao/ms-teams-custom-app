using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Teams.Core;

namespace Teams.Host;

public static class GraphRequests
{
    public static TimeSpan? RetryAfter(HttpResponseMessage response, TimeProvider clock) =>
        response.Headers.RetryAfter?.Delta ?? response.Headers.RetryAfter?.Date - clock.GetUtcNow();

    public static async Task<JsonElement> SendAsync(Func<HttpClient> client, Func<CancellationToken, Task<string>> token,
        HttpMethod method, Uri uri, object? body, string mode, GraphRetry retry, CancellationToken ct)
    {
        var expires = retry.Deadline();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(retry.Options.OperationTimeoutSeconds), retry.Clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        for (var attempt = 1; ; attempt++)
        {
            int? status = null;
            TimeSpan? retryAfter = null;
            try
            {
                using var attemptTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(retry.Options.AttemptTimeoutSeconds), retry.Clock);
                using var attemptToken = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, attemptTimeout.Token);
                string access;
                try { access = await token(attemptToken.Token).WaitAsync(attemptToken.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                { throw new AppError(503, "graph_not_dispatched", "Token preparation ended before Graph dispatch; work remains pending."); }
                catch (Azure.Identity.AuthenticationFailedException)
                { throw new AppError(503, "graph_not_dispatched", "Workload authentication was unavailable before Graph dispatch; work remains pending."); }
                attemptToken.Token.ThrowIfCancellationRequested();
                using var request = new HttpRequestMessage(method, uri);
                request.Headers.Authorization = new("Bearer", access);
                request.Headers.Add("client-request-id", Guid.NewGuid().ToString());
                if (body is not null) request.Content = JsonContent.Create(body);
                var http = client();
                var kind = attempt == 1 ? "initial" : "retry";
                LiveTelemetry.GraphAttempts.Add(1, new("mode", mode), new KeyValuePair<string, object?>("attempt", kind));
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptToken.Token);
                status = (int)response.StatusCode;
                retryAfter = RetryAfter(response, retry.Clock);
                LiveTelemetry.GraphResponses.Add(1, new("mode", mode), new("attempt", kind),
                    new KeyValuePair<string, object?>("status", status));
                if (response.IsSuccessStatusCode)
                    return response.StatusCode == HttpStatusCode.NoContent ? JsonSerializer.SerializeToElement(new { }) :
                        await BoundedJson.ReadAsync(response.Content, 16 * 1024 * 1024, attemptToken.Token,
                            allowEmpty: GraphUrls.IsReauthorization(method, uri));
            }
            catch (HttpRequestException) when (method == HttpMethod.Get && !linked.IsCancellationRequested)
            {
                status = null;
                LiveTelemetry.Failure("graph-read-transport");
            }
            catch (OperationCanceledException) when (method == HttpMethod.Get && !linked.IsCancellationRequested)
            {
                status = null;
                LiveTelemetry.Failure("graph-read-timeout");
            }
            var retryable = status == 429 || method == HttpMethod.Get && status is null or 408 or 500 or 502 or 503 or 504;
            var delay = retry.Delay(attempt, retryAfter);
            if (!retryable || linked.IsCancellationRequested || !retry.CanRetry(attempt, delay, expires))
            {
                if (retryable) LiveTelemetry.GraphRetryExhausted.Add(1, new KeyValuePair<string, object?>("mode", mode));
                ct.ThrowIfCancellationRequested();
                throw new AppError(status is 401 or 403 or 404 or 429 ? status.Value : 503,
                    status is null ? "graph_read_unavailable" : $"graph_{status}",
                    "Graph did not complete the request within its retry budget; work remains pending.");
            }
            LiveTelemetry.GraphRetries.Add(1, new KeyValuePair<string, object?>("mode", mode));
            LiveTelemetry.GraphRetryWait.Record(delay.TotalMilliseconds, new KeyValuePair<string, object?>("mode", mode));
            await retry.WaitAsync(delay, linked.Token);
        }
    }
}
