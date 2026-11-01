using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Exceptions;
using APIHandler.Helpers;
using APIHandler.Models.GroupReservation;
using APIHandler.Services.GuestSessions;
using System;
using System.Threading.Tasks;

namespace APIHandler.Handlers.GroupReservations;

public class SubmitGroupReservationHandler(IGuestSessionService guestSessionService) : IApiRequestHandler
{
    private readonly IGuestSessionService _guestSessionService = guestSessionService ?? throw new ArgumentNullException(nameof(guestSessionService));

    public async Task<APIGatewayProxyResponse> HandleRequest(APIGatewayProxyRequest request, ILambdaContext context)
    {
        if (!request.TryGetPathValue("groupReservationId", out var groupReservationId))
        {
            return ResponseBuilder.CreateErrorResponse(400, "Missing 'groupReservationId' in path parameters.", context.Logger);
        }

        var userId = UserContextHelper.GetAuthenticatedUserId(request);
        if (userId is null)
        {
            return ResponseBuilder.CreateErrorResponse(401, "Unauthorized.", context.Logger);
        }

        if (!RequestBodyHelper.TryParseAndValidate<SubmitGroupReservationRequest>(request, context, out var submitRequest, out var errorResponse))
        {
            return errorResponse;
        }

        try
        {
            var consolidatedDishes = await _guestSessionService.SubmitGroupReservationPreOrderAsync(
                userId,
                groupReservationId,
                submitRequest.ReservationId);

            return ResponseBuilder.CreateJsonResponse(200, consolidatedDishes);
        }
        catch (GroupReservationNotFoundException ex)
        {
            return ResponseBuilder.CreateErrorResponse(404, ex.Message, context.Logger);
        }
        catch (ReservationNotFoundException ex)
        {
            return ResponseBuilder.CreateErrorResponse(404, ex.Message, context.Logger);
        }
        catch (ActionForbiddenException ex)
        {
            return ResponseBuilder.CreateErrorResponse(403, ex.Message, context.Logger);
        }
        catch (ValidationException ex)
        {
            return ResponseBuilder.CreateErrorResponse(400, ex.Message, context.Logger);
        }
    }
}
