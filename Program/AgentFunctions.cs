using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.CopilotStudio.Client;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using CopilotStudioFunction.Models;

namespace CopilotStudioFunction;

public class AgentFunctions
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly CopilotClient _copilotClient;
    private readonly ILogger<AgentFunctions> _logger;

    public AgentFunctions(CopilotClient copilotClient, ILogger<AgentFunctions> logger)
    {
        _copilotClient = copilotClient;
        _logger = logger;
    }

 
    [Function("GetA2AAgentCard")]
    public async Task<HttpResponseData> GetA2AAgentCard(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = ".well-known/agent-card.json")]
        HttpRequestData req)
    {
        var card = new AgentCard
        {
            Url = ResolveA2AUrlFromCardRoute(req)
        };

        return await WriteResponse(req, HttpStatusCode.OK, card);
    }

   
    [Function("A2AJsonRpc")]
    public async Task<HttpResponseData> A2AJsonRpc(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "a2a")]
        HttpRequestData req,
        CancellationToken cancellationToken)
    {
        A2AJsonRpcRequest? rpc;
        try
        {
            rpc = await JsonSerializer.DeserializeAsync<A2AJsonRpcRequest>(req.Body, JsonOptions, cancellationToken);
        }
        catch
        {
            return await WriteResponse(req, HttpStatusCode.BadRequest, new A2AJsonRpcResponse
            {
                Error = new A2AJsonRpcError { Code = -32700, Message = "Parse error" }
            });
        }

        if (rpc == null || string.IsNullOrWhiteSpace(rpc.Method))
        {
            return await WriteResponse(req, HttpStatusCode.BadRequest, new A2AJsonRpcResponse
            {
                Id = rpc?.Id?.ToString(),
                Error = new A2AJsonRpcError { Code = -32600, Message = "Invalid Request" }
            });
        }

        try
        {
            return rpc.Method switch
            {
                "agent.getCard" => await WriteResponse(req, HttpStatusCode.OK, new A2AJsonRpcResponse
                {
                    Id = ConvertJsonRpcId(rpc.Id),
                    Result = new AgentCard { Url = ResolveAbsoluteRequestUrl(req) }
                }),
                "chat.start" => await HandleChatStart(req, rpc, cancellationToken),
                "chat.send" => await HandleChatSend(req, rpc, cancellationToken),
                _ => await WriteResponse(req, HttpStatusCode.OK, new A2AJsonRpcResponse
                {
                    Id = ConvertJsonRpcId(rpc.Id),
                    Error = new A2AJsonRpcError { Code = -32601, Message = $"Method not found: {rpc.Method}" }
                })
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            return await WriteResponse(req, HttpStatusCode.OK, new A2AJsonRpcResponse
            {
                Id = ConvertJsonRpcId(rpc.Id),
                Error = new A2AJsonRpcError { Code = 401, Message = ex.Message }
            });
        }
        catch (MsalUiRequiredException)
        {
            return await WriteResponse(req, HttpStatusCode.OK, new A2AJsonRpcResponse
            {
                Id = ConvertJsonRpcId(rpc.Id),
                Error = new A2AJsonRpcError { Code = 401, Message = "User consent required." }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A2AJsonRpc: Unexpected error");
            return await WriteResponse(req, HttpStatusCode.OK, new A2AJsonRpcResponse
            {
                Id = ConvertJsonRpcId(rpc.Id),
                Error = new A2AJsonRpcError { Code = -32000, Message = ex.Message }
            });
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool IsConsentCard(IActivity activity)
    {
        if (activity.Type != "message" || activity.Attachments == null)
            return false;

        foreach (var attachment in activity.Attachments)
        {
            if (attachment.ContentType != "application/vnd.microsoft.card.adaptive")
                continue;
 if (attachment.Content is JsonElement cardElement)
            {
                var cardJson = cardElement.GetRawText();
                if (cardJson.Contains("Connect to continue") ||
                    cardJson.Contains("actionSubmitId") && cardJson.Contains("Allow"))
                    return true;
            }
        }
        return false;
    }

    private static bool TryExtractBearerToken(HttpRequestData req, out string? token)
    {
        token = null;
        if (!req.Headers.TryGetValues("Authorization", out var values))
            return false;

        var header = values.FirstOrDefault();
        if (string.IsNullOrEmpty(header) ||
            !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return false;

        token = header["Bearer ".Length..].Trim();
        return !string.IsNullOrEmpty(token);
    }

    private async Task<ChatResponse> StartConversationInternal(HttpRequestData req, CancellationToken cancellationToken)
    {
        _logger.LogInformation("StartConversationInternal: Request received");

        EnsureBearerToken(req);
        _logger.LogInformation("StartConversationInternal: Token set, starting conversation");

        var messages = new List<string>();
        int activityCount = 0;
        string conversationId = string.Empty;

        await foreach (var activity in _copilotClient.StartConversationAsync(
            emitStartConversationEvent: true,
            cancellationToken: cancellationToken))
        {
            activityCount++;
            _logger.LogInformation("StartConversationInternal: Activity {ActivityCount} - Type: {ActivityType}, TextLength: {TextLength}",
                activityCount, activity.Type, activity.Text?.Length ?? 0);

            if (string.IsNullOrEmpty(conversationId) && !string.IsNullOrEmpty(activity.Conversation?.Id))
                conversationId = activity.Conversation.Id;

            CollectMessages(activity, messages);
        }

        if (string.IsNullOrEmpty(conversationId))
            conversationId = $"conv-{Guid.NewGuid()}";

        _logger.LogInformation("StartConversationInternal: Completed with {ActivityCount} activities, {MessageCount} messages, ConversationId: {ConversationId}",
            activityCount, messages.Count, conversationId);

        return new ChatResponse(conversationId, messages);
    }

    private async Task<ChatResponse> SendMessageInternal(
        HttpRequestData req,
        string conversationId,
        string message,
        CancellationToken cancellationToken)
    {
        EnsureBearerToken(req);

        var messages = new List<string>();
        int activityCount = 0;

        await foreach (var activity in _copilotClient.AskQuestionAsync(
            message,
            conversationId,
            cancellationToken))
        {
            activityCount++;
            _logger.LogInformation("SendMessageInternal: Activity {ActivityCount} - Type: {ActivityType}, TextLength: {TextLength}",
                activityCount, activity.Type, activity.Text?.Length ?? 0);

            if (IsConsentCard(activity))
            {
                _logger.LogInformation("SendMessageInternal: Detected consent card, auto-approving with Allow");
                var allowActivity = new Activity
                {
                    Type = "message",
                    Value = new { action = "Allow", actionSubmitId = "Allow", id = "submit" },
                    Conversation = new ConversationAccount { Id = conversationId }
                };

                await foreach (var consentResponse in _copilotClient.AskQuestionAsync(allowActivity, cancellationToken))
                {
                    CollectMessages(consentResponse, messages);
                }

                continue;
            }

            CollectMessages(activity, messages);
        }

        _logger.LogInformation("SendMessageInternal: Completed with {ActivityCount} activities, {MessageCount} messages",
            activityCount, messages.Count);

        return new ChatResponse(conversationId, messages);
    }

    private void EnsureBearerToken(HttpRequestData req)
    {
        if (!TryExtractBearerToken(req, out var userToken))
            throw new UnauthorizedAccessException("Missing or invalid Authorization: Bearer header.");

        OBOTokenHandler.SetUserToken(userToken!);
    }

    private static string ResolveA2AUrlFromCardRoute(HttpRequestData req)
    {
        var path = req.Url.AbsolutePath;
        const string cardSuffix = "/.well-known/agent-card.json";

        if (path.EndsWith(cardSuffix, StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^cardSuffix.Length] + "/a2a";
        }

        return $"{req.Url.Scheme}://{req.Url.Authority}{path}";
    }

    private static string ResolveAbsoluteRequestUrl(HttpRequestData req)
    {
        return $"{req.Url.Scheme}://{req.Url.Authority}{req.Url.AbsolutePath}";
    }

    private static object? ConvertJsonRpcId(JsonElement? id)
    {
        if (id is null)
            return null;

         var value = id.Value;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var i64) => i64,
            JsonValueKind.Number when value.TryGetDecimal(out var dec) => dec,
            JsonValueKind.Null => null,
            _ => value.GetRawText()
        };
    }

    private async Task<HttpResponseData> HandleChatStart(
        HttpRequestData req,
        A2AJsonRpcRequest rpc,
        CancellationToken cancellationToken)
    {
        var result = await StartConversationInternal(req, cancellationToken);

        return await WriteResponse(req, HttpStatusCode.OK, new A2AJsonRpcResponse
        {
            Id = ConvertJsonRpcId(rpc.Id),
            Result = new
            {
                conversationId = result.ConversationId,
                messages = result.Messages
            }
        });
    }

    private async Task<HttpResponseData> HandleChatSend(
        HttpRequestData req,
        A2AJsonRpcRequest rpc,
        CancellationToken cancellationToken)
    {
        if (rpc.Params is null)
        {
            return await WriteResponse(req, HttpStatusCode.OK, new A2AJsonRpcResponse
            {
                Id = ConvertJsonRpcId(rpc.Id),
                Error = new A2AJsonRpcError { Code = -32602, Message = "Invalid params" }
            });
        }

        var payload = JsonNode.Parse(rpc.Params.Value.GetRawText())?.AsObject();
        var conversationId = payload?["conversationId"]?.GetValue<string>();
        var message = payload?["message"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(conversationId) || string.IsNullOrWhiteSpace(message))
        {
            return await WriteResponse(req, HttpStatusCode.OK, new A2AJsonRpcResponse
            {
                Id = ConvertJsonRpcId(rpc.Id),
                Error = new A2AJsonRpcError { Code = -32602, Message = "conversationId and message are required." }
            });
        }

        var result = await SendMessageInternal(req, conversationId, message, cancellationToken);

        return await WriteResponse(req, HttpStatusCode.OK, new A2AJsonRpcResponse
        {
            Id = ConvertJsonRpcId(rpc.Id),
            Result = new
            {
                conversationId = result.ConversationId,
                messages = result.Messages
            }
        });
    }

    private void CollectMessages(IActivity activity, List<string> messages)
    {
        if (activity.Type == "message")
        {
            // First, try to use the text property
            if (!string.IsNullOrWhiteSpace(activity.Text))
            {
                _logger.LogDebug("CollectMessages: Adding text message");
                messages.Add(activity.Text);
                return;
            }

            // If no text, check for attachments (Adaptive Cards, etc.)
            if (activity.Attachments != null && activity.Attachments.Count > 0)
            {
                _logger.LogDebug("CollectMessages: Found {AttachmentCount} attachments", activity.Attachments.Count);
                
                foreach (var attachment in activity.Attachments)
                {
                    try
                    {
                        // Handle Adaptive Cards
                        if (attachment.ContentType == "application/vnd.microsoft.card.adaptive" &&
                            attachment.Content is JsonElement cardElement)
                        {
                            var cardJson = cardElement.GetRawText();
                            _logger.LogDebug("CollectMessages: Found Adaptive Card");
                            messages.Add($"[Adaptive Card Response]\n{cardJson}");
                            return;
                        }
                        // Handle other JSON content
                        else if (attachment.Content is JsonElement jsonContent)
                        {
                            var contentJson = jsonContent.GetRawText();
                            _logger.LogDebug("CollectMessages: Found JSON attachment - ContentType: {ContentType}", attachment.ContentType);
                            messages.Add(contentJson);
                            return;
                        }
                        // Handle string content
                        else if (attachment.Content is string stringContent && !string.IsNullOrEmpty(stringContent))
                        {
                            _logger.LogDebug("CollectMessages: Found string content attachment");
                            messages.Add(stringContent);
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "CollectMessages: Error processing attachment");
                    }
                }
            }

            // If no text and no attachments
            _logger.LogDebug("CollectMessages: Message activity has no text or attachments");
        }
        else
        {
            _logger.LogDebug("CollectMessages: Skipping non-message activity - Type: {ActivityType}", activity.Type);
        }
    }

    private static async Task<HttpResponseData> WriteResponse<T>(
        HttpRequestData req, HttpStatusCode status, T body)
    {
        var response = req.CreateResponse(status);
        await response.WriteAsJsonAsync(body);
        return response;
    }
}
