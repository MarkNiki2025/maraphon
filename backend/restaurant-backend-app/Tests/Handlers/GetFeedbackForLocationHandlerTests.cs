using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Handlers.Feedbacks;
using APIHandler.Models.Feedbacks;
using APIHandler.Services.Auth;
using APIHandler.Services.Feedbacks;
using Moq;
using Xunit;

namespace APIHandler.Tests.Handlers;

public class GetFeedbackForLocationHandlerTests
{
    private readonly Mock<IFeedbackService> _mockFeedbackService = new();
    private readonly Mock<IUserService> _mockUserService = new();
    private readonly Mock<ILambdaContext> _mockLambdaContext = new();
    private readonly Mock<ILambdaLogger> _mockLogger = new();
    private readonly GetFeedbackForLocationHandler _handler;

    public GetFeedbackForLocationHandlerTests()
    {
        _mockLambdaContext.Setup(c => c.Logger).Returns(_mockLogger.Object);
        _handler = new GetFeedbackForLocationHandler(_mockFeedbackService.Object, _mockUserService.Object);
    }

    [Fact]
    public async Task HandleRequest_NullQueryParameters_Returns400()
    {
        // Arrange
        var request = new APIGatewayProxyRequest
        {
            QueryStringParameters = null
        };

        // Act
        var response = await _handler.HandleRequest(request, _mockLambdaContext.Object);

        // Assert
        Assert.Equal(400, response.StatusCode);
        Assert.Contains("Location ID is required", response.Body);
    }

    [Fact]
    public async Task HandleRequest_MissingLocationId_Returns400()
    {
        // Arrange
        var request = new APIGatewayProxyRequest
        {
            QueryStringParameters = new Dictionary<string, string>()
        };

        // Act
        var response = await _handler.HandleRequest(request, _mockLambdaContext.Object);

        // Assert
        Assert.Equal(400, response.StatusCode);
        Assert.Contains("Location ID is required", response.Body);
    }

    [Fact]
    public async Task HandleRequest_ValidLocationId_OptionalParamsMissing_UsesDefaults()
    {
        // Arrange
        var request = new APIGatewayProxyRequest
        {
            QueryStringParameters = new Dictionary<string, string>
            {
                { "locationId", "loc123" }
            }
        };

        _mockFeedbackService
            .Setup(s => s.GetFeedbackForLocationAsync("loc123", 1, FeedbackType.Service, SortingOrder.NewestFirst))
            .ReturnsAsync(new FeedbackResponse { Items = new List<FeedbackResponseItem>() });

        // Act
        var response = await _handler.HandleRequest(request, _mockLambdaContext.Object);

        // Assert
        Assert.Equal(200, response.StatusCode);
        _mockFeedbackService.Verify(s => s.GetFeedbackForLocationAsync("loc123", 1, FeedbackType.Service, SortingOrder.NewestFirst), Times.Once);
    }

    [Fact]
    public async Task HandleRequest_InvalidPage_Returns400()
    {
        // Arrange
        var request = new APIGatewayProxyRequest
        {
            QueryStringParameters = new Dictionary<string, string>
            {
                { "locationId", "loc123" },
                { "page", "0" }
            }
        };

        // Act
        var response = await _handler.HandleRequest(request, _mockLambdaContext.Object);

        // Assert
        Assert.Equal(400, response.StatusCode);
        Assert.Contains("Invalid page number", response.Body);
    }

    [Fact]
    public async Task HandleRequest_InvalidType_Returns400()
    {
        // Arrange
        var request = new APIGatewayProxyRequest
        {
            QueryStringParameters = new Dictionary<string, string>
            {
                { "locationId", "loc123" },
                { "type", "InvalidType" }
            }
        };

        // Act
        var response = await _handler.HandleRequest(request, _mockLambdaContext.Object);

        // Assert
        Assert.Equal(400, response.StatusCode);
        Assert.Contains("Invalid feedback type", response.Body);
    }

    [Fact]
    public async Task HandleRequest_InvalidSort_Returns400()
    {
        // Arrange
        var request = new APIGatewayProxyRequest
        {
            QueryStringParameters = new Dictionary<string, string>
            {
                { "locationId", "loc123" },
                { "sort", "InvalidSort" }
            }
        };

        // Act
        var response = await _handler.HandleRequest(request, _mockLambdaContext.Object);

        // Assert
        Assert.Equal(400, response.StatusCode);
        Assert.Contains("Invalid sorting order", response.Body);
    }

    [Fact]
    public async Task HandleRequest_EmptyOptionalParams_UsesDefaults()
    {
        // Arrange
        var request = new APIGatewayProxyRequest
        {
            QueryStringParameters = new Dictionary<string, string>
            {
                { "locationId", "loc123" },
                { "page", "" },
                { "type", "" },
                { "sort", "" }
            }
        };

        _mockFeedbackService
            .Setup(s => s.GetFeedbackForLocationAsync("loc123", 1, FeedbackType.Service, SortingOrder.NewestFirst))
            .ReturnsAsync(new FeedbackResponse { Items = new List<FeedbackResponseItem>() });

        // Act
        var response = await _handler.HandleRequest(request, _mockLambdaContext.Object);

        // Assert
        Assert.Equal(200, response.StatusCode);
        _mockFeedbackService.Verify(s => s.GetFeedbackForLocationAsync("loc123", 1, FeedbackType.Service, SortingOrder.NewestFirst), Times.Once);
    }
}
