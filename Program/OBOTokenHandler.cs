using System.Net.Http.Headers;

namespace CopilotStudioFunction;

/// <summary>
/// Delegating handler that forwards the user Bearer token directly.
/// The incoming user Bearer token is stored per async context via AsyncLocal,
/// so concurrent function invocations never share tokens.
/// </summary>
public class OBOTokenHandler : DelegatingHandler
{
    // AsyncLocal: each async execution context (i.e. each function invocation) has its own copy
    private static readonly AsyncLocal<string?> _userToken = new();

    public OBOTokenHandler(AgentConnectionSettings settings)
    {
        // Settings kept for future use
    }

    /// <summary>
    /// Call this at the start of every function invocation before using CopilotClient.
    /// The value is scoped to the current async context only.
    /// </summary>
    public static void SetUserToken(string token) => _userToken.Value = token;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var userToken = _userToken.Value
            ?? throw new UnauthorizedAccessException(
                "No user token found. Call OBOTokenHandler.SetUserToken() before invoking the agent.");

        // Forward the user token directly
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", userToken);

        return await base.SendAsync(request, cancellationToken);
    }
}
