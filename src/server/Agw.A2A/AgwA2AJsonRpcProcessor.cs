using System.Diagnostics;
using System.Text.Json;
using A2A;
using A2A.AspNetCore;
using Agw.Shared.Exceptions;
using Microsoft.AspNetCore.Http;

namespace Agw.A2A;

internal class AgwA2AJsonRpcProcessor
{
    internal static async Task<IResult> ProcessRequestAsync(
        IAgwA2ARequestHandler requestHandler,
        HttpRequest request,
        string agentName,
        CancellationToken cancellationToken
    )
    {
        // Version negotiation: check A2A-Version header
        var version = request.Headers["A2A-Version"].FirstOrDefault();
        if (!string.IsNullOrEmpty(version) && version != "1.0" && version != "0.3")
        {
            return new JsonRpcResponseResult(
                JsonRpcResponse.CreateJsonRpcErrorResponse(
                    new JsonRpcId((string?)null),
                    new A2AException(
                        $"Protocol version '{version}' is not supported. Supported versions: 0.3, 1.0",
                        A2AErrorCode.VersionNotSupported
                    )
                )
            );
        }

        using var activity = AgwA2ADiagnostics.Source.StartActivity("HandleA2ARequest", ActivityKind.Server);

        JsonRpcRequest? rpcRequest = null;

        try
        {
            rpcRequest = (JsonRpcRequest?)
                await JsonSerializer
                    .DeserializeAsync(
                        request.Body,
                        A2AJsonUtilities.DefaultOptions.GetTypeInfo(typeof(JsonRpcRequest)),
                        cancellationToken
                    )
                    .ConfigureAwait(false);

            activity?.SetTag("request.id", rpcRequest!.Id.ToString());
            activity?.SetTag("request.method", rpcRequest!.Method);

            if (A2AMethods.IsStreamingMethod(rpcRequest!.Method))
            {
                return StreamResponse(
                    requestHandler,
                    agentName,
                    rpcRequest.Id,
                    rpcRequest.Method,
                    rpcRequest.Params,
                    cancellationToken
                );
            }

            return await SingleResponseAsync(
                    requestHandler,
                    agentName,
                    rpcRequest.Id,
                    rpcRequest.Method,
                    rpcRequest.Params,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (A2AException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            var errorId = rpcRequest?.Id ?? new JsonRpcId(ex.GetRequestId());
            return new JsonRpcResponseResult(JsonRpcResponse.CreateJsonRpcErrorResponse(errorId, ex));
        }
        catch (AgwException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            var errorId = rpcRequest?.Id ?? new JsonRpcId((string?)null);
            return new JsonRpcResponseResult(JsonRpcResponse.CreateJsonRpcErrorResponse(errorId, ToA2AException(ex)));
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            var errorId = rpcRequest?.Id ?? new JsonRpcId((string?)null);
            return new JsonRpcResponseResult(
                JsonRpcResponse.InternalErrorResponse(errorId, "An internal error occurred.")
            );
        }
    }

    internal static async Task<JsonRpcResponseResult> SingleResponseAsync(
        IAgwA2ARequestHandler requestHandler,
        string agentName,
        JsonRpcId requestId,
        string method,
        JsonElement? parameters,
        CancellationToken cancellationToken
    )
    {
        using var activity = AgwA2ADiagnostics.Source.StartActivity($"SingleResponse/{method}", ActivityKind.Server);
        activity?.SetTag("request.id", requestId.ToString());
        activity?.SetTag("request.method", method);

        JsonRpcResponse? response = null;

        if (parameters == null)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Invalid parameters");
            return new JsonRpcResponseResult(JsonRpcResponse.InvalidParamsResponse(requestId));
        }

        // For push notification methods, check if push notifications are supported
        // BEFORE deserializing params. DeserializeAndValidate would throw InvalidParams
        // for malformed requests, masking the PushNotificationNotSupported error.
        if (A2AMethods.IsPushNotificationMethod(method))
        {
            try
            {
                await requestHandler.GetTaskPushNotificationConfigAsync(null!, cancellationToken).ConfigureAwait(false);
            }
            catch (AgwException ex) when (ex.Code == ErrorCodes.A2APushNotificationNotSupported.Code)
            {
                throw;
            }
            catch (A2AException ex) when (ex.ErrorCode == A2AErrorCode.PushNotificationNotSupported)
            {
                throw;
            }
            catch
            {
                // Any other exception means push notifications are supported;
                // continue with normal deserialization and handling.
            }
        }

        switch (method)
        {
            case A2AMethods.SendMessage:
                var sendRequest = DeserializeAndValidate<SendMessageRequest>(parameters.Value);
                var sendResult = await requestHandler
                    .SendMessageAsync(agentName, sendRequest, cancellationToken)
                    .ConfigureAwait(false);
                response = JsonRpcResponse.CreateJsonRpcResponse(requestId, sendResult);
                break;

            case A2AMethods.GetTask:
                var getTaskRequest = DeserializeAndValidate<GetTaskRequest>(parameters.Value);
                var agentTask = await requestHandler
                    .GetTaskAsync(getTaskRequest, cancellationToken)
                    .ConfigureAwait(false);
                response = JsonRpcResponse.CreateJsonRpcResponse(requestId, agentTask);
                break;

            case A2AMethods.ListTasks:
                var listTasksRequest = DeserializeAndValidate<ListTasksRequest>(parameters.Value);

                // Validate pageSize: must be 1-100 if specified
                if (listTasksRequest.PageSize is { } ps && (ps <= 0 || ps > 100))
                {
                    throw new AgwException(
                        ErrorCodes.InvalidPageSize,
                        $"Invalid pageSize: {ps}. Must be between 1 and 100."
                    );
                }

                // Validate historyLength: must be >= 0 if specified
                if (listTasksRequest.HistoryLength is { } hl && hl < 0)
                {
                    throw new AgwException(
                        ErrorCodes.InvalidHistoryLength,
                        $"Invalid historyLength: {hl}. Must be non-negative."
                    );
                }

                var listResult = await requestHandler
                    .ListTasksAsync(agentName, listTasksRequest, cancellationToken)
                    .ConfigureAwait(false);
                response = JsonRpcResponse.CreateJsonRpcResponse(requestId, listResult);
                break;

            case A2AMethods.CancelTask:
                var cancelRequest = DeserializeAndValidate<CancelTaskRequest>(parameters.Value);
                var cancelledTask = await requestHandler
                    .CancelTaskAsync(agentName, cancelRequest, cancellationToken)
                    .ConfigureAwait(false);
                response = JsonRpcResponse.CreateJsonRpcResponse(requestId, cancelledTask);
                break;

            case A2AMethods.CreateTaskPushNotificationConfig:
                var createPnConfig = DeserializeAndValidate<CreateTaskPushNotificationConfigRequest>(parameters.Value);
                var createdConfig = await requestHandler
                    .CreateTaskPushNotificationConfigAsync(createPnConfig, cancellationToken)
                    .ConfigureAwait(false);
                response = JsonRpcResponse.CreateJsonRpcResponse(requestId, createdConfig);
                break;

            case A2AMethods.GetTaskPushNotificationConfig:
                var getPnConfig = DeserializeAndValidate<GetTaskPushNotificationConfigRequest>(parameters.Value);
                var gotConfig = await requestHandler
                    .GetTaskPushNotificationConfigAsync(getPnConfig, cancellationToken)
                    .ConfigureAwait(false);
                response = JsonRpcResponse.CreateJsonRpcResponse(requestId, gotConfig);
                break;

            case A2AMethods.ListTaskPushNotificationConfig:
                var listPnConfig = DeserializeAndValidate<ListTaskPushNotificationConfigRequest>(parameters.Value);
                var listPnResult = await requestHandler
                    .ListTaskPushNotificationConfigAsync(listPnConfig, cancellationToken)
                    .ConfigureAwait(false);
                response = JsonRpcResponse.CreateJsonRpcResponse(requestId, listPnResult);
                break;

            case A2AMethods.DeleteTaskPushNotificationConfig:
                var deletePnConfig = DeserializeAndValidate<DeleteTaskPushNotificationConfigRequest>(parameters.Value);
                await requestHandler
                    .DeleteTaskPushNotificationConfigAsync(deletePnConfig, cancellationToken)
                    .ConfigureAwait(false);
                response = JsonRpcResponse.CreateJsonRpcResponse(requestId, (object?)null);
                break;

            case A2AMethods.GetExtendedAgentCard:
                var getCardRequest = DeserializeAndValidate<GetExtendedAgentCardRequest>(parameters.Value);
                var extCard = await requestHandler
                    .GetExtendedAgentCardAsync(getCardRequest, cancellationToken)
                    .ConfigureAwait(false);
                response = JsonRpcResponse.CreateJsonRpcResponse(requestId, extCard);
                break;

            default:
                response = JsonRpcResponse.MethodNotFoundResponse(requestId);
                break;
        }

        return new JsonRpcResponseResult(response);
    }

    private static T DeserializeAndValidate<T>(JsonElement jsonParamValue)
        where T : class
    {
        T? parms;
        try
        {
            parms = jsonParamValue.Deserialize(A2AJsonUtilities.DefaultOptions.GetTypeInfo(typeof(T))) as T;
        }
        catch (JsonException ex)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"Invalid parameters: request body could not be deserialized as {typeof(T).Name}.",
                ex
            );
        }

        if (parms is null)
        {
            throw new AgwException(ErrorCodes.InvalidParam, $"Failed to deserialize parameters as {typeof(T).Name}");
        }

        if (parms is SendMessageRequest sendMsgRequest && sendMsgRequest.Message.Parts.Count == 0)
        {
            throw new AgwException(ErrorCodes.MessagePartsCannotBeEmpty, "Message parts cannot be empty");
        }

        return parms;
    }

    internal static IResult StreamResponse(
        IAgwA2ARequestHandler requestHandler,
        string agentName,
        JsonRpcId requestId,
        string method,
        JsonElement? parameters,
        CancellationToken cancellationToken
    )
    {
        using var activity = AgwA2ADiagnostics.Source.StartActivity("StreamResponse", ActivityKind.Server);
        activity?.SetTag("request.id", requestId.ToString());

        if (parameters == null)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Invalid parameters");
            return new JsonRpcResponseResult(JsonRpcResponse.InvalidParamsResponse(requestId));
        }

        switch (method)
        {
            case A2AMethods.SubscribeToTask:
                var subscribeRequest = DeserializeAndValidate<SubscribeToTaskRequest>(parameters.Value);
                var taskEvents = requestHandler.SubscribeToTaskAsync(subscribeRequest, cancellationToken);
                return new JsonRpcStreamedResult(taskEvents, requestId);

            case A2AMethods.SendStreamingMessage:
                var sendRequest = DeserializeAndValidate<SendMessageRequest>(parameters.Value);
                var sendEvents = requestHandler.SendStreamingMessageAsync(agentName, sendRequest, cancellationToken);
                return new JsonRpcStreamedResult(sendEvents, requestId);

            default:
                activity?.SetStatus(ActivityStatusCode.Error, "Invalid method");
                return new JsonRpcResponseResult(JsonRpcResponse.MethodNotFoundResponse(requestId));
        }
    }

    private static A2AException ToA2AException(AgwException exception)
    {
        var a2aErrorCode = A2AErrorCode.InvalidParams;

        if (exception.Code == ErrorCodes.A2APushNotificationNotSupported.Code)
        {
            a2aErrorCode = A2AErrorCode.PushNotificationNotSupported;
        }
        else if (exception.Code == ErrorCodes.A2ATaskNotFound.Code)
        {
            a2aErrorCode = A2AErrorCode.TaskNotFound;
        }
        else if (exception.Code == ErrorCodes.A2ATaskNotCancelable.Code)
        {
            a2aErrorCode = A2AErrorCode.TaskNotCancelable;
        }
        else if (
            exception.Code == ErrorCodes.A2AUnsupportedOperation.Code
            || exception.Code == ErrorCodes.A2ATerminalTaskCannotAcceptMessages.Code
            || exception.Code == ErrorCodes.A2ATerminalTaskCannotBeSubscribed.Code
        )
        {
            a2aErrorCode = A2AErrorCode.UnsupportedOperation;
        }
        else if (exception.Code == ErrorCodes.A2AExtendedAgentCardNotConfigured.Code)
        {
            a2aErrorCode = A2AErrorCode.ExtendedAgentCardNotConfigured;
        }
        else if (
            exception.Code == ErrorCodes.A2AInvalidAgentResponse.Code
            || exception.Code == ErrorCodes.AgentNotFound.Code
            || exception.Code == ErrorCodes.AgentReturnedNoResult.Code
            || exception.Code == ErrorCodes.UnableToCreateAgentSession.Code
        )
        {
            a2aErrorCode = A2AErrorCode.InvalidAgentResponse;
        }

        return new A2AException(exception.Message, a2aErrorCode);
    }
}
