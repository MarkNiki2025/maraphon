using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Exceptions;
using APIHandler.Helpers;
using APIHandler.Models.Feedbacks;
using APIHandler.Services.Auth;
using APIHandler.Services.Feedbacks;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace APIHandler.Handlers.Feedbacks;

public class GetFeedbackForLocationHandler : IApiRequestHandler
{
    private readonly IFeedbackService _feedbackService;
    private readonly IUserService _userService;

    public GetFeedbackForLocationHandler(IFeedbackService feedbackService, IUserService userService)
    {
        _feedbackService = feedbackService ?? throw new ArgumentNullException(nameof(feedbackService));
        _userService = userService ?? throw new ArgumentNullException(nameof(userService));
    }

    public async Task<APIGatewayProxyResponse> HandleRequest(APIGatewayProxyRequest request, ILambdaContext context)
    {
        if (!TryParseQueryParameters(request, out var queryParameters, out var errorMessage))
        {
            return ResponseBuilder.CreateErrorResponse(400, errorMessage!, context.Logger);
        }

        try
        {
            var responseBody = await _feedbackService.GetFeedbackForLocationAsync(
                queryParameters!.LocationId,
                queryParameters.Page,
                queryParameters.Type,
                queryParameters.SortOrder);

            if (responseBody?.Items != null)
            {
                foreach (var feedback in responseBody.Items)
                {
                    if (feedback == null) continue;

                    feedback.Username = feedback.UserId is not null
                        ? await _userService.GetUserName(feedback.UserId) ?? "Anonymous User"
                        : "Anonymous User";
                }
            }

            return ResponseBuilder.CreateSuccessResponse(responseBody ?? new FeedbackResponse());
        }
        catch (ValidationException ex)
        {
            return ResponseBuilder.CreateErrorResponse(400, ex.Message, context.Logger);
        }
    }

    private static bool TryParseQueryParameters(
        APIGatewayProxyRequest request,
        out FeedbackQueryParameters? queryParameters,
        out string? errorMessage)
    {
        try
        {
            queryParameters = ParseQueryParameters(request);
            errorMessage = null;
            return true;
        }
        catch (ValidationException ex)
        {
            queryParameters = null;
            errorMessage = ex.Message;
            return false;
        }
    }

    private static FeedbackQueryParameters ParseQueryParameters(APIGatewayProxyRequest request)
    {
        var queryString = request.QueryStringParameters ?? new Dictionary<string, string>();

        if (!queryString.TryGetValue("locationId", out var locationId) ||
            string.IsNullOrEmpty(locationId))
        {
            throw new ValidationException("Location ID is required.");
        }

        // Default values
        FeedbackType type = FeedbackType.Service;
        SortingOrder sortOrder = SortingOrder.NewestFirst;
        int page = 1;

        if (queryString.TryGetValue("type", out var typeString) && !string.IsNullOrEmpty(typeString))
        {
            if (!Enum.TryParse(typeString, true, out type))
            {
                throw new ValidationException("Invalid feedback type.");
            }
        }

        if (queryString.TryGetValue("sort", out var sortString) && !string.IsNullOrEmpty(sortString))
        {
            if (!Enum.TryParse(sortString, true, out sortOrder))
            {
                throw new ValidationException("Invalid sorting order.");
            }
        }

        if (queryString.TryGetValue("page", out var pageString) && !string.IsNullOrEmpty(pageString))
        {
            if (!int.TryParse(pageString, out page) || page < 1)
            {
                throw new ValidationException("Invalid page number.");
            }
        }

        return new FeedbackQueryParameters
        {
            LocationId = locationId,
            Type = type,
            SortOrder = sortOrder,
            Page = page
        };
    }
}