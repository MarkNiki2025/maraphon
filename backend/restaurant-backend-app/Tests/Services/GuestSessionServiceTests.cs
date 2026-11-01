using Amazon.CognitoIdentityProvider.Model;
using APIHandler.Constants.Dishes;
using APIHandler.Constants.Orders;
using APIHandler.Entities;
using APIHandler.Exceptions;
using APIHandler.Models.GuestSessions;
using APIHandler.Repositories.Auth;
using APIHandler.Repositories.Dishes;
using APIHandler.Repositories.GroupReservations;
using APIHandler.Repositories.GuestSessions;
using APIHandler.Repositories.Orders;
using APIHandler.Repositories.Reservations;
using APIHandler.Repositories.Tables;
using APIHandler.Services.GuestSessions;
using AutoFixture.Xunit2;
using Moq;
using NPOI.POIFS.Storage;
using System.Security.Cryptography;
using System.Text;

namespace APIHandler.Tests.Services;

public class GuestSessionServiceTests
{
    private readonly Mock<IGuestSessionRepository> _mockGuestSessionRepository = new();
    private readonly Mock<IGroupReservationRepository> _mockGroupReservationRepository = new();
    private readonly Mock<IDishRepository> _mockDishRepository = new();
    private readonly Mock<IReservationRepository> _mockReservationRepository = new();
    private readonly Mock<IUserRepository> _mockUserRepository = new();
    private readonly Mock<ITableRepository> _mockTableRepository = new();
    private readonly Mock<IOrderRepository> _mockOrderRepository = new();
    private readonly GuestSessionService _guestSessionService;

    public GuestSessionServiceTests()
    {
        _guestSessionService = new GuestSessionService(
            _mockGroupReservationRepository.Object,
            _mockGuestSessionRepository.Object,
            _mockDishRepository.Object,
            _mockReservationRepository.Object,
            _mockUserRepository.Object,
            _mockTableRepository.Object,
            _mockOrderRepository.Object);
    }
    [Theory]
    [InlineAutoData(0)]
    [InlineAutoData(3)]
    public async Task CreateGuestSessionAsync_CorrectData_ReturnsSessionToken(
        int guestCount,
        CreateGuestSessionRequest request,
        GroupReservation groupReservation,
        Reservation parentReservation)
    {
        groupReservation.GroupReservationId = request.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;
        groupReservation.ReservationId = parentReservation.Id;

        parentReservation.GuestCount = 5;

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(parentReservation.Id))
            .ReturnsAsync(parentReservation);

        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync([]);

        GuestSession? session = null;
        _mockGuestSessionRepository
            .Setup(r => r.SaveAsync(It.IsAny<GuestSession>()))
            .Callback<GuestSession>(s => session = s);

        _mockGuestSessionRepository
            .Setup(r => r.CountByGroupReservationId(groupReservation.GroupReservationId))
            .ReturnsAsync(guestCount);

        var response = await _guestSessionService.CreateGuestSessionAsync(request);

        Assert.NotEmpty(response.SessionId);
        Assert.NotEmpty(response.SessionToken);
        Assert.NotNull(session);
        Assert.Equal(groupReservation.GroupReservationId, session.GroupReservationId);
        Assert.Equal(request.GuestName, session.GuestName);
        Assert.NotNull(session.SessionTokenHash);
        Assert.NotEmpty(session.SessionTokenHash);
    }

    [Theory]
    [AutoData]
    public async Task CreateGuestSessionAsync_InvalidGroupReservationId_ThrowsGroupReservationNotFoundException(CreateGuestSessionRequest request)
    {
        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(request.GroupReservationId))
            .ReturnsAsync((GroupReservation?)null);

        await Assert.ThrowsAsync<GroupReservationNotFoundException>(() => _guestSessionService.CreateGuestSessionAsync(request));
    }

    [Theory]
    [AutoData]
    public async Task CreateGuestSessionAsync_GroupReservationExpired_ThrowsGroupReservationExpiredException(CreateGuestSessionRequest request, GroupReservation groupReservation)
    {
        groupReservation.GroupReservationId = request.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(request.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync<GroupReservationExpiredException>(() => _guestSessionService.CreateGuestSessionAsync(request));
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldAddNewDish_WhenDishDoesNotExistInSession(GuestSession guestSession, GroupReservation groupReservation, Reservation parentReservation, Table table, Dish dish)
    {
        guestSession.Dishes = [];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        groupReservation.ReservationId = parentReservation.Id;
        parentReservation.TableId = table.Id;
        dish.State = DishState.Available;
        int quantityToAdd = 2;

        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(guestSession.Id)).ReturnsAsync(guestSession);
        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId)).ReturnsAsync(groupReservation);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(parentReservation.Id)).ReturnsAsync(parentReservation);
        _mockTableRepository.Setup(r => r.GetTableAsync(table.Id)).ReturnsAsync(table);
        _mockDishRepository.Setup(r => r.GetDishesByLocationIdAsync(table.LocationId)).ReturnsAsync([dish]);

        await _guestSessionService.AddDishAsync(guestSession.Id, dish.Id, quantityToAdd);

        Assert.Single(guestSession.Dishes);
        Assert.Equal(dish.Id, guestSession.Dishes[0].DishId);
        Assert.Equal(quantityToAdd, guestSession.Dishes[0].Quantity);
        Assert.Equal(OrderItemState.Pending, guestSession.Dishes[0].State);

        _mockGuestSessionRepository.Verify(r => r.SaveAsync(guestSession), Times.Once);
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldIncreaseQuantity_WhenDishAlreadyExists(GuestSession guestSession, GroupReservation groupReservation, Reservation parentReservation, Table table, Dish dish)
    {
        int initialQuantity = 3;
        int quantityToAdd = 2;
        guestSession.Dishes = [new OrderItem { DishId = dish.Id, Quantity = initialQuantity, State = OrderItemState.Pending }];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        groupReservation.ReservationId = parentReservation.Id;
        parentReservation.TableId = table.Id;
        dish.State = DishState.Available;

        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(guestSession.Id)).ReturnsAsync(guestSession);
        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId)).ReturnsAsync(groupReservation);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(parentReservation.Id)).ReturnsAsync(parentReservation);
        _mockTableRepository.Setup(r => r.GetTableAsync(table.Id)).ReturnsAsync(table);
        _mockDishRepository.Setup(r => r.GetDishesByLocationIdAsync(table.LocationId)).ReturnsAsync([dish]);

        await _guestSessionService.AddDishAsync(guestSession.Id, dish.Id, quantityToAdd);

        Assert.Single(guestSession.Dishes);
        Assert.Equal(initialQuantity + quantityToAdd, guestSession.Dishes[0].Quantity);

        _mockGuestSessionRepository.Verify(r => r.SaveAsync(guestSession), Times.Once);
    }

    [Theory]
    [InlineAutoData(0)]
    [InlineAutoData(-1)]
    public async Task AddDishAsync_ShouldThrowValidationException_WhenQuantityIsZeroOrLess(int quantity, string guestSessionId, Dish dish)
    {
        dish.State = DishState.Available;

        _mockDishRepository
            .Setup(r => r.GetDishByIdAsync(dish.Id))
            .ReturnsAsync(dish);

        await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.AddDishAsync(guestSessionId, dish.Id, quantity));
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldThrowGuestSessionNotFoundException_WhenSessionNotFound(string guestSessionId, Dish dish)
    {
        dish.State = DishState.Available;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSessionId))
            .ReturnsAsync((GuestSession?)null);

        _mockDishRepository
            .Setup(r => r.GetDishByIdAsync(dish.Id))
            .ReturnsAsync(dish);

        var exception = await Assert.ThrowsAsync<GuestSessionNotFoundException>(() =>
            _guestSessionService.AddDishAsync(guestSessionId, dish.Id, 2));
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldThrowDishNotFound_WhenDishNotFound(GuestSession guestSession, GroupReservation groupReservation, Reservation parentReservation, Table table, string dishId)
    {
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;
        groupReservation.ReservationId = parentReservation.Id;
        parentReservation.TableId = table.Id;

        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(guestSession.Id)).ReturnsAsync(guestSession);
        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId)).ReturnsAsync(groupReservation);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(parentReservation.Id)).ReturnsAsync(parentReservation);
        _mockTableRepository.Setup(r => r.GetTableAsync(table.Id)).ReturnsAsync(table);

        _mockDishRepository.Setup(r => r.GetDishesByLocationIdAsync(table.LocationId)).ReturnsAsync([]);

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.AddDishAsync(guestSession.Id, dishId, quantity: 2));
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldThrowDishNotAvailable_WhenDishIsNotAvailable(GuestSession guestSession, GroupReservation groupReservation, Reservation parentReservation, Table table, Dish dish)
    {
        dish.State = DishState.OutOfStock;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;
        groupReservation.ReservationId = parentReservation.Id;
        parentReservation.TableId = table.Id;

        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(guestSession.Id)).ReturnsAsync(guestSession);
        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId)).ReturnsAsync(groupReservation);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(parentReservation.Id)).ReturnsAsync(parentReservation);
        _mockTableRepository.Setup(r => r.GetTableAsync(table.Id)).ReturnsAsync(table);
        _mockDishRepository.Setup(r => r.GetDishesByLocationIdAsync(table.LocationId)).ReturnsAsync([dish]);

        var exception = await Assert.ThrowsAsync<DishIsNotAvailableException>(() =>
            _guestSessionService.AddDishAsync(guestSession.Id, dish.Id, quantity: 2));
    }

    [Theory]
    [InlineAutoData(GroupReservationStatus.Submitted, typeof(GroupReservationSubmittedException))]
    [InlineAutoData(GroupReservationStatus.Closed, typeof(GroupReservationExpiredException))]
    public async Task AddDishAsync_ShouldThrowGroupReservationExpired_WhenGroupStatusIsNotOpen(GroupReservationStatus status, Type exceptionType, GuestSession guestSession, GroupReservation groupReservation, Dish dish)
    {
        dish.State = DishState.Available;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = status;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockDishRepository
            .Setup(r => r.GetDishByIdAsync(dish.Id))
            .ReturnsAsync(dish);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        var exception = await Assert.ThrowsAsync(exceptionType, () =>
            _guestSessionService.AddDishAsync(guestSession.Id, dish.Id, quantity: 2));
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldDecreaseQuantity_WhenQuantityIsGreaterThanAmountToRemove(GuestSession guestSession, GroupReservation groupReservation, string dishId)
    {
        int initialQuantity = 5;
        int quantityToRemove = 2;
        guestSession.Dishes =
        [
            new OrderItem { DishId = dishId, Quantity = initialQuantity, State = OrderItemState.Pending }
        ];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);


        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await _guestSessionService.RemoveDishAsync(guestSession.Id, dishId, quantityToRemove);

        Assert.Single(guestSession.Dishes);
        Assert.Equal(initialQuantity - quantityToRemove, guestSession.Dishes[0].Quantity);

        _mockGuestSessionRepository.Verify(r => r.SaveAsync(guestSession), Times.Once);
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldRemoveDishFromList_WhenQuantityToRemoveIsEqualOrGreater(GuestSession guestSession, GroupReservation groupReservation, string dishId)
    {
        int initialQuantity = 2;
        int quantityToRemove = 3;
        guestSession.Dishes =
        [
            new OrderItem { DishId = dishId, Quantity = initialQuantity, State = OrderItemState.Pending }
        ];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await _guestSessionService.RemoveDishAsync(guestSession.Id, dishId, quantityToRemove);

        Assert.Empty(guestSession.Dishes);
        _mockGuestSessionRepository.Verify(r => r.SaveAsync(guestSession), Times.Once);
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldThrowDishNotFoundException_WhenDishDoesNotExistInSession(GuestSession guestSession, GroupReservation groupReservation, string dishId)
    {
        guestSession.Dishes = [];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
           .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
           .ReturnsAsync(groupReservation);

        var exception = await Assert.ThrowsAsync<DishNotFoundException>(() =>
            _guestSessionService.RemoveDishAsync(guestSession.Id, dishId, 2));

        Assert.Contains("not found in guest session", exception.Message);
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldThrowValidationException_WhenQuantityIsZeroOrLess(string guestSessionId, string dishId)
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.RemoveDishAsync(guestSessionId, dishId, 0));

        await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.RemoveDishAsync(guestSessionId, dishId, -5));
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldThrowGuestSessionNotFoundException_WhenSessionNotFound(string guestSessionId, string dishId)
    {
        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSessionId))
            .ReturnsAsync((GuestSession?)null);

        var exception = await Assert.ThrowsAsync<GuestSessionNotFoundException>(() =>
            _guestSessionService.RemoveDishAsync(guestSessionId, dishId, 2));
    }
    [Theory]
    [InlineAutoData(1, 0)]
    [InlineAutoData(1, 1)]
    [InlineAutoData(2, 1)]
    [InlineAutoData(2, 2)]
    public async Task CreateGuestSessionAsync_WhenCapacityReached_ThrowsGuestLimitExceededException(
        int reservationCapacity,
        int guestCount,
        CreateGuestSessionRequest request,
        GroupReservation groupReservation,
        Reservation parentReservation)
    {
        groupReservation.Status = GroupReservationStatus.Open;
        groupReservation.GroupReservationId = request.GroupReservationId;
        groupReservation.ReservationId = parentReservation.Id;

        parentReservation.GuestCount = reservationCapacity;

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(parentReservation.Id))
            .ReturnsAsync(parentReservation);

        _mockGuestSessionRepository
            .Setup(r => r.CountByGroupReservationId(groupReservation.GroupReservationId))
            .ReturnsAsync(guestCount);

        var exception = await Assert.ThrowsAsync<GuestLimitExceededException>(() => _guestSessionService.CreateGuestSessionAsync(request));

        _mockGuestSessionRepository.Verify(r => r.SaveAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Theory]
    [AutoData]
    public async Task CreateGuestSessionAsync_MissingParentReservation_ThrowsGroupReservationExpiredException(
        CreateGuestSessionRequest request,
        GroupReservation groupReservation)
    {
        groupReservation.GroupReservationId = request.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(groupReservation.ReservationId))
            .ReturnsAsync((Reservation?)null);

        var exception = await Assert.ThrowsAsync<GroupReservationExpiredException>(() => _guestSessionService.CreateGuestSessionAsync(request));
    }

    [Theory]
    [AutoData]
    public async Task IsAuthorizedAsync_ValidTokenAndId_ReturnsTrue(GuestSession guestSession, byte[] tokenBytes)
    {
        string tokenString = Convert.ToBase64String(tokenBytes);
        var hashString = Convert.ToBase64String(SHA256.HashData(tokenBytes));
        guestSession.SessionTokenHash = hashString;

        _mockGuestSessionRepository
            .Setup(r => r.GetByTokenHash(hashString))
            .ReturnsAsync(guestSession);

        bool result = await _guestSessionService.IsAuthorizedAsync(tokenString, guestSession.Id);

        Assert.True(result);
    }

    [Theory]
    [AutoData]
    public async Task IsAuthorizedAsync_InvalidToken_ReturnsFalse(byte[] tokenBytes, string guestSessionId)
    {
        string tokenString = Convert.ToBase64String(tokenBytes);
        var hashString = Convert.ToBase64String(SHA256.HashData(tokenBytes));

        _mockGuestSessionRepository
            .Setup(r => r.GetByTokenHash(hashString))
            .ReturnsAsync((GuestSession?)null);

        bool result = await _guestSessionService.IsAuthorizedAsync(tokenString, guestSessionId);

        Assert.False(result);
    }

    [Theory]
    [AutoData]
    public async Task IsAuthorizedAsync_InvalidGuestSessionId_ReturnsFalse(GuestSession guestSession, byte[] tokenBytes, string guestSessionId)
    {
        string tokenString = Convert.ToBase64String(tokenBytes);
        var hashString = Convert.ToBase64String(SHA256.HashData(tokenBytes));
        guestSession.SessionTokenHash = hashString;

        _mockGuestSessionRepository
            .Setup(r => r.GetByTokenHash(hashString))
            .ReturnsAsync(guestSession);

        bool result = await _guestSessionService.IsAuthorizedAsync(tokenString, guestSessionId);

        Assert.False(result);
    }
    [Theory]
    [AutoData]
    public async Task SubmitCartAsync_Valid_SubmitsSuccessfuilly(GuestSession guestSession, GroupReservation groupReservation, Dish[] dishes)
    {
        foreach (var dish in dishes)
            dish.State = DishState.Available;

        guestSession.IsSubmitted = false;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
           .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
           .ReturnsAsync(groupReservation);

        _mockDishRepository
            .Setup(d => d.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(dishes);

        GuestSession? session = null;
        _mockGuestSessionRepository
            .Setup(r => r.SaveAsync(It.IsAny<GuestSession>()))
            .Callback<GuestSession>(s => session = s);

        await _guestSessionService.SubmitCartAsync(guestSession.Id);

        Assert.NotNull(session);
        Assert.True(session.IsSubmitted);
    }

    [Theory]
    [AutoData]
    public async Task SubmitCartAsync_SessionNotFound_ThrowsGuestSessionNotFound(string guestSessionId)
    {
        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSessionId))
            .ReturnsAsync((GuestSession?)null);

        await Assert.ThrowsAsync<GuestSessionNotFoundException>(() => _guestSessionService.SubmitCartAsync(guestSessionId));
    }

    [Theory]
    [InlineAutoData(GroupReservationStatus.Submitted, typeof(GroupReservationSubmittedException))]
    [InlineAutoData(GroupReservationStatus.Closed, typeof(GroupReservationExpiredException))]
    public async Task SubmitCartAsync_GroupReservationNotOpen_ThrowsException(GroupReservationStatus status, Type exceptionType, GuestSession guestSession, GroupReservation groupReservation)
    {
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = status;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync(exceptionType, () => _guestSessionService.SubmitCartAsync(guestSession.Id));
    }

    [Theory]
    [AutoData]
    public async Task SubmitCartAsync_GuestSessionHasNoDishes_ThrowsCartIsEmptyException(GuestSession guestSession, GroupReservation groupReservation)
    {
        guestSession.Dishes = [];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync<CartIsEmptyException>(() => _guestSessionService.SubmitCartAsync(guestSession.Id));
    }

    [Theory]
    [AutoData]
    public async Task SubmitCartAsync_GuestSessionSubmitted_ThrowsAlreadySubmited(GuestSession guestSession, GroupReservation groupReservation)
    {
        guestSession.IsSubmitted = true;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync<GuestSessionSubmittedException>(() => _guestSessionService.SubmitCartAsync(guestSession.Id));
    }

    [Theory]
    [InlineAutoData(DishState.OutOfStock)]
    [InlineAutoData(DishState.OnStop)]
    public async Task SubmitCartAsync_DishNotAvailable_ThrowsDishNotAvailable(DishState state, Dish dish, GuestSession guestSession, GroupReservation groupReservation)
    {
        dish.State = state;
        guestSession.IsSubmitted = false;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        guestSession.Dishes = 
        [
            new OrderItem()
            {
                DishId = dish.Id,
                Quantity = 1,
            }
        ];

        _mockDishRepository
            .Setup(d => d.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync([dish]);

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
           .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
           .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync<DishIsNotAvailableException>(() => _guestSessionService.SubmitCartAsync(guestSession.Id));
    }
    [Theory]
    [MemberAutoData(nameof(PepareDishesForGuestSession))]
    public async Task GetGuestSessionInfoAsync_Guest_ReturnsAsync(
        IEnumerable<string> dishIds,
        IEnumerable<Dish> dishes,
        List<OrderItem> orderItems,
        decimal expectedTotal,
        GuestSession session)
    {
        session.Dishes = orderItems;
        session.IsOwnerSession = false;

        _mockDishRepository
            .Setup(r => r.GetDishesByIdsAsync(dishIds))
            .ReturnsAsync(dishes);

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(session.Id))
            .ReturnsAsync(session);

        GetGuestSessionResponse result = await _guestSessionService.GetGuestSessionInfoAsync(session.Id);

        Assert.Equal(session.GuestName, result.Name);
        Assert.Equal(dishes.Count(), result.Items.Count());
        Assert.Equal(expectedTotal, result.Total);
    }

    [Theory]
    [MemberAutoData(nameof(PepareDishesForGuestSession))]
    public async Task GetGuestSessionInfoAsync_GroupOwner_ReturnsAsync(
        IEnumerable<string> dishIds,
        IEnumerable<Dish> dishes,
        List<OrderItem> orderItems,
        decimal expectedTotal,
        GuestSession session,
        GroupReservation groupReservation,
        Reservation reservation,
        string customerId,
        string ownerName,
        UserType userType)
    {
        session.Dishes = orderItems;
        session.IsOwnerSession = true;
        session.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.ReservationId = reservation.Id;
        reservation.CustomerId = customerId;
        userType.Attributes.Add(new AttributeType()
        {
            Name = "given_name",
            Value = ownerName,
        });

        _mockDishRepository
            .Setup(r => r.GetDishesByIdsAsync(dishIds))
            .ReturnsAsync(dishes);

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(session.Id))
            .ReturnsAsync(session);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        _mockUserRepository
            .Setup(r => r.GetUserByIdAsync(customerId))
            .ReturnsAsync(userType);

        GetGuestSessionResponse result = await _guestSessionService.GetGuestSessionInfoAsync(session.Id);

        Assert.Equal(ownerName, result.Name);
        Assert.Equal(dishes.Count(), result.Items.Count());
        Assert.Equal(expectedTotal, result.Total);
    }

    public static TheoryData<IEnumerable<string>, IEnumerable<Dish>, List<OrderItem>, decimal> PepareDishesForGuestSession()
    {
        var dishMap = new Dictionary<string, Dish>()
        {
            ["1"] = new()
            {
                Id = "1",
                Price = 1
            },
            ["2"] = new()
            {
                Id = "2",
                Price = 2
            },
            ["3"] = new()
            {
                Id = "3",
                Price = 3
            }
        };

        var orderItems = new List<OrderItem>
        {
            new OrderItem()
                    {
                        DishId = "1",
                        Quantity = 2
                    },
                    new OrderItem()
                    {
                        DishId = "2",
                        Quantity = 3
                    },
                    new OrderItem()
                    {
                        DishId = "3",
                        Quantity = 1
                    }
        };

        var expectedTotal = orderItems.Sum(o => dishMap[o.DishId].Price * o.Quantity);

        return new()
        {
            { dishMap.Keys, dishMap.Values, orderItems, expectedTotal }
        };
    }

    [Fact]
    public async Task GetGroupSessionsAsync_ValidOwner_ReturnsAllSessionsWithTotals()
    {
        string userId = "owner-user-1";
        string groupReservationId = "group-1";
        string reservationId = "reservation-1";

        var groupReservation = new GroupReservation
        {
            GroupReservationId = groupReservationId,
            ReservationId = reservationId,
            ReservationToken = "token-1",
            Status = GroupReservationStatus.Open
        };

        var reservation = new Reservation
        {
            Id = reservationId,
            CustomerId = userId,
            TableId = "table-1",
            WaiterId = "waiter-1"
        };

        var dishes = new List<Dish>
        {
            new() { Id = "dish1", Name = "Caesar", Price = 180 },
            new() { Id = "dish2", Name = "Tom Yum", Price = 220 }
        };

        var sessions = new List<GuestSession>
        {
            new()
            {
                Id = "session1",
                GuestName = "Andrii",
                GroupReservationId = groupReservationId,
                IsSubmitted = true,
                IsOwnerSession = false,
                Dishes = [new OrderItem { DishId = "dish1", Quantity = 2 }]
            },
            new()
            {
                Id = "session2",
                GuestName = "Olena",
                GroupReservationId = groupReservationId,
                IsSubmitted = false,
                IsOwnerSession = false,
                Dishes = [new OrderItem { DishId = "dish2", Quantity = 1 }]
            }
        };

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservationId))
            .ReturnsAsync(groupReservation);

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(reservationId))
            .ReturnsAsync(reservation);

        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(groupReservationId))
            .ReturnsAsync(sessions);

        _mockDishRepository
            .Setup(r => r.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(dishes);

        var response = await _guestSessionService.GetGroupSessionsAsync(userId, groupReservationId);

        Assert.NotNull(response);
        Assert.Equal(2, response.Participants.Count());
        Assert.Equal(580, response.Total);

        var first = response.Participants.First(p => p.Id == "session1");
        Assert.Equal(ParticipantType.Guest, first.Type);
        Assert.Equal(360, first.Total);
        Assert.True(first.IsSubmitted);

        var second = response.Participants.First(p => p.Id == "session2");
        Assert.Equal(220, second.Total);
        Assert.False(second.IsSubmitted);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_NonOwner_ThrowsActionForbiddenException()
    {
        string userId = "not-the-owner";
        string groupReservationId = "group-1";
        string reservationId = "reservation-1";

        var groupReservation = new GroupReservation
        {
            GroupReservationId = groupReservationId,
            ReservationId = reservationId,
            ReservationToken = "token-1",
            Status = GroupReservationStatus.Open
        };

        var reservation = new Reservation
        {
            Id = reservationId,
            CustomerId = "actual-owner",
            TableId = "table-1",
            WaiterId = "waiter-1"
        };

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservationId))
            .ReturnsAsync(groupReservation);

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(reservationId))
            .ReturnsAsync(reservation);

        await Assert.ThrowsAsync<ActionForbiddenException>(() =>
            _guestSessionService.GetGroupSessionsAsync(userId, groupReservationId));
    }

    [Fact]
    public async Task GetOrCreateOwnerSessionIdAsync_ExistingOwnerSession_ReturnsExistingSessionId()
    {
        string userId = "owner-1";
        string reservationId = "res-1";
        string groupId = "group-1";
        string expectedSessionId = "existing-session-id";

        var reservation = new Reservation
        {
            Id = reservationId,
            CustomerId = userId,
            TableId = "table-1",
            WaiterId = "waiter-1"
        };

        var groupReservation = new GroupReservation
        {
            GroupReservationId = groupId,
            ReservationId = reservationId,
            ReservationToken = "token-1",
            Status = GroupReservationStatus.Open
        };

        var sessions = new List<GuestSession>
        {
            new()
            {
                Id = expectedSessionId,
                GuestName = "Owner",
                GroupReservationId = groupId,
                IsOwnerSession = true,
                OwnerUserId = userId,
                Dishes = []
            }
        };

        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(reservationId)).ReturnsAsync(reservation);
        _mockGroupReservationRepository.Setup(r => r.GetByReservationIdAsync(reservationId)).ReturnsAsync(groupReservation);
        _mockGuestSessionRepository.Setup(r => r.GetAllByGroupReservationIdAsync(groupId)).ReturnsAsync(sessions);

        var result = await _guestSessionService.GetOrCreateOwnerSessionIdAsync(userId, reservationId);

        Assert.Equal(expectedSessionId, result.GuestSessionId);
        _mockGuestSessionRepository.Verify(r => r.SaveAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Fact]
    public async Task GetOrCreateOwnerSessionIdAsync_NoOwnerSession_CreatesNewAndReturnsSessionId()
    {
        string userId = "owner-1";
        string reservationId = "res-1";
        string groupId = "group-1";

        var reservation = new Reservation
        {
            Id = reservationId,
            CustomerId = userId,
            TableId = "table-1",
            WaiterId = "waiter-1"
        };

        var groupReservation = new GroupReservation
        {
            GroupReservationId = groupId,
            ReservationId = reservationId,
            ReservationToken = "token-1",
            Status = GroupReservationStatus.Open
        };

        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(reservationId)).ReturnsAsync(reservation);
        _mockGroupReservationRepository.Setup(r => r.GetByReservationIdAsync(reservationId)).ReturnsAsync(groupReservation);
        _mockGuestSessionRepository.Setup(r => r.GetAllByGroupReservationIdAsync(groupId)).ReturnsAsync([]);

        GuestSession? savedSession = null;
        _mockGuestSessionRepository
            .Setup(r => r.SaveAsync(It.IsAny<GuestSession>()))
            .Callback<GuestSession>(s => savedSession = s)
            .Returns(Task.CompletedTask);

        var result = await _guestSessionService.GetOrCreateOwnerSessionIdAsync(userId, reservationId);

        Assert.NotNull(result);
        Assert.NotEmpty(result.GuestSessionId);
        Assert.NotNull(savedSession);
        Assert.True(savedSession.IsOwnerSession);
        Assert.Equal(userId, savedSession.OwnerUserId);
        Assert.Equal(groupId, savedSession.GroupReservationId);
    }

    [Fact]
    public async Task GetOrCreateOwnerSessionIdAsync_NonOwner_ThrowsActionForbiddenException()
    {
        string userId = "not-owner";
        string reservationId = "res-1";

        var reservation = new Reservation
        {
            Id = reservationId,
            CustomerId = "actual-owner",
            TableId = "table-1",
            WaiterId = "waiter-1"
        };

        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(reservationId)).ReturnsAsync(reservation);

        await Assert.ThrowsAsync<ActionForbiddenException>(() =>
            _guestSessionService.GetOrCreateOwnerSessionIdAsync(userId, reservationId));
    }

    [Fact]
    public async Task SubmitGroupReservationPreOrderAsync_ValidRequest_ConsolidatesDishesAndUpdatesStatuses()
    {
        string userId = "owner-1";
        string groupId = "group-1";
        string reservationId = "res-1";

        var group = new GroupReservation
        {
            GroupReservationId = groupId,
            ReservationId = reservationId,
            ReservationToken = "token-1",
            Status = GroupReservationStatus.Open
        };

        var reservation = new Reservation
        {
            Id = reservationId,
            CustomerId = userId,
            TableId = "table-1",
            WaiterId = "waiter-1"
        };

        var order = new Order
        {
            OrderId = "order-1",
            CustomerId = userId,
            ReservationId = reservationId,
            ReservationAddress = "Table 1",
            ReservationTime = "12:00 - 13:00",
            Dishes = [],
            Status = OrderStatus.Created
        };

        var sessions = new List<GuestSession>
        {
            new()
            {
                Id = "s1",
                GuestName = "A",
                GroupReservationId = groupId,
                IsSubmitted = true,
                Dishes =
                [
                    new OrderItem { DishId = "dish1", Quantity = 2 },
                    new OrderItem { DishId = "dish2", Quantity = 1 }
                ]
            },
            new()
            {
                Id = "s2",
                GuestName = "B",
                GroupReservationId = groupId,
                IsSubmitted = true,
                Dishes =
                [
                    new OrderItem { DishId = "dish1", Quantity = 1 },
                    new OrderItem { DishId = "dish2", Quantity = 1 }
                ]
            },
            new()
            {
                Id = "s3",
                GuestName = "C",
                GroupReservationId = groupId,
                IsSubmitted = false,
                Dishes =
                [
                    new OrderItem { DishId = "dish3", Quantity = 5 } // unsubmitted, should be ignored
                ]
            }
        };

        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupId)).ReturnsAsync(group);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(reservationId)).ReturnsAsync(reservation);
        _mockOrderRepository.Setup(r => r.GetOrderByReservationIdAsync(reservationId)).ReturnsAsync(order);
        _mockGuestSessionRepository.Setup(r => r.GetAllByGroupReservationIdAsync(groupId)).ReturnsAsync(sessions);

        var result = (await _guestSessionService.SubmitGroupReservationPreOrderAsync(userId, groupId, reservationId)).ToList();

        Assert.Equal(2, result.Count);
        var dish1 = result.First(d => d.DishId == "dish1");
        Assert.Equal(3, dish1.Quantity);
        var dish2 = result.First(d => d.DishId == "dish2");
        Assert.Equal(2, dish2.Quantity);

        Assert.Equal(OrderStatus.SubmittedByCustomer, order.Status);
        Assert.Equal(GroupReservationStatus.Submitted, group.Status);
        _mockOrderRepository.Verify(r => r.SaveOrderAsync(order), Times.Once);
        _mockGroupReservationRepository.Verify(r => r.SaveGroupReservationAsync(group), Times.Once);
    }

    [Fact]
    public async Task SubmitGroupReservationPreOrderAsync_OrderNotFound_ThrowsValidationException()
    {
        string userId = "owner-1";
        string groupId = "group-1";
        string reservationId = "res-1";

        var group = new GroupReservation
        {
            GroupReservationId = groupId,
            ReservationId = reservationId,
            ReservationToken = "token-1",
            Status = GroupReservationStatus.Open
        };

        var reservation = new Reservation
        {
            Id = reservationId,
            CustomerId = userId,
            TableId = "table-1",
            WaiterId = "waiter-1"
        };

        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupId)).ReturnsAsync(group);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(reservationId)).ReturnsAsync(reservation);
        _mockOrderRepository.Setup(r => r.GetOrderByReservationIdAsync(reservationId)).ReturnsAsync((Order?)null);

        await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.SubmitGroupReservationPreOrderAsync(userId, groupId, reservationId));
    }

    [Fact]
    public async Task SubmitGroupReservationPreOrderAsync_NoSubmittedSessions_ThrowsValidationException()
    {
        string userId = "owner-1";
        string groupId = "group-1";
        string reservationId = "res-1";

        var group = new GroupReservation
        {
            GroupReservationId = groupId,
            ReservationId = reservationId,
            ReservationToken = "token-1",
            Status = GroupReservationStatus.Open
        };

        var reservation = new Reservation
        {
            Id = reservationId,
            CustomerId = userId,
            TableId = "table-1",
            WaiterId = "waiter-1"
        };

        var order = new Order
        {
            OrderId = "order-1",
            CustomerId = userId,
            ReservationId = reservationId,
            ReservationAddress = "Table 1",
            ReservationTime = "12:00",
            Dishes = [],
            Status = OrderStatus.Created
        };

        var sessions = new List<GuestSession>
        {
            new()
            {
                Id = "s1",
                GuestName = "A",
                GroupReservationId = groupId,
                IsSubmitted = false,
                Dishes = [new OrderItem { DishId = "dish1", Quantity = 1 }]
            }
        };

        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupId)).ReturnsAsync(group);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(reservationId)).ReturnsAsync(reservation);
        _mockOrderRepository.Setup(r => r.GetOrderByReservationIdAsync(reservationId)).ReturnsAsync(order);
        _mockGuestSessionRepository.Setup(r => r.GetAllByGroupReservationIdAsync(groupId)).ReturnsAsync(sessions);

        await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.SubmitGroupReservationPreOrderAsync(userId, groupId, reservationId));
    }

    [Fact]
    public async Task SubmitGroupReservationPreOrderAsync_NoDishesInSubmittedSessions_ThrowsValidationException()
    {
        string userId = "owner-1";
        string groupId = "group-1";
        string reservationId = "res-1";

        var group = new GroupReservation
        {
            GroupReservationId = groupId,
            ReservationId = reservationId,
            ReservationToken = "token-1",
            Status = GroupReservationStatus.Open
        };

        var reservation = new Reservation
        {
            Id = reservationId,
            CustomerId = userId,
            TableId = "table-1",
            WaiterId = "waiter-1"
        };

        var order = new Order
        {
            OrderId = "order-1",
            CustomerId = userId,
            ReservationId = reservationId,
            ReservationAddress = "Table 1",
            ReservationTime = "12:00",
            Dishes = [],
            Status = OrderStatus.Created
        };

        var sessions = new List<GuestSession>
        {
            new()
            {
                Id = "s1",
                GuestName = "A",
                GroupReservationId = groupId,
                IsSubmitted = true,
                Dishes = []
            }
        };

        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupId)).ReturnsAsync(group);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(reservationId)).ReturnsAsync(reservation);
        _mockOrderRepository.Setup(r => r.GetOrderByReservationIdAsync(reservationId)).ReturnsAsync(order);
        _mockGuestSessionRepository.Setup(r => r.GetAllByGroupReservationIdAsync(groupId)).ReturnsAsync(sessions);

        await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.SubmitGroupReservationPreOrderAsync(userId, groupId, reservationId));
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_ValidOwner_DeletesAndReturnsUpdatedSnapshot()
    {
        string userId = "owner-1";
        string groupId = "group-1";
        string reservationId = "res-1";
        string sessionToDeleteId = "session-to-delete";
        string remainingSessionId = "remaining-session";

        var group = new GroupReservation
        {
            GroupReservationId = groupId,
            ReservationId = reservationId,
            ReservationToken = "token-1",
            Status = GroupReservationStatus.Open
        };

        var reservation = new Reservation
        {
            Id = reservationId,
            CustomerId = userId,
            TableId = "table-1",
            WaiterId = "waiter-1"
        };

        var sessionToDelete = new GuestSession
        {
            Id = sessionToDeleteId,
            GroupReservationId = groupId,
            GuestName = "Guest 1",
            Dishes = []
        };

        var remainingSession = new GuestSession
        {
            Id = remainingSessionId,
            GroupReservationId = groupId,
            GuestName = "Guest 2",
            IsSubmitted = true,
            Dishes = [new OrderItem { DishId = "dish1", Quantity = 2 }]
        };

        var dishes = new List<Dish>
        {
            new() { Id = "dish1", Name = "Soup", Price = 100 }
        };

        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupId)).ReturnsAsync(group);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(reservationId)).ReturnsAsync(reservation);
        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(sessionToDeleteId)).ReturnsAsync(sessionToDelete);
        _mockGuestSessionRepository.Setup(r => r.GetAllByGroupReservationIdAsync(groupId)).ReturnsAsync([remainingSession]);
        _mockDishRepository.Setup(r => r.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>())).ReturnsAsync(dishes);

        var result = await _guestSessionService.DeleteGuestSessionAsync(userId, groupId, sessionToDeleteId);

        _mockGuestSessionRepository.Verify(r => r.DeleteAsync(sessionToDelete), Times.Once);
        Assert.NotNull(result);
        Assert.Single(result.Participants);
        Assert.Equal(200, result.Total);
        Assert.Equal(remainingSessionId, result.Participants.First().Id);
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_SessionNotFound_ThrowsGuestSessionNotFoundException()
    {
        string userId = "owner-1";
        string groupId = "group-1";
        string reservationId = "res-1";
        string sessionToDeleteId = "non-existent";

        var group = new GroupReservation
        {
            GroupReservationId = groupId,
            ReservationId = reservationId,
            ReservationToken = "token-1",
            Status = GroupReservationStatus.Open
        };

        var reservation = new Reservation
        {
            Id = reservationId,
            CustomerId = userId,
            TableId = "table-1",
            WaiterId = "waiter-1"
        };

        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupId)).ReturnsAsync(group);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(reservationId)).ReturnsAsync(reservation);
        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(sessionToDeleteId)).ReturnsAsync((GuestSession?)null);

        await Assert.ThrowsAsync<GuestSessionNotFoundException>(() =>
            _guestSessionService.DeleteGuestSessionAsync(userId, groupId, sessionToDeleteId));
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_SessionDoesNotBelongToGroup_ThrowsValidationException()
    {
        string userId = "owner-1";
        string groupId = "group-1";
        string reservationId = "res-1";
        string sessionToDeleteId = "other-session";

        var group = new GroupReservation
        {
            GroupReservationId = groupId,
            ReservationId = reservationId,
            ReservationToken = "token-1",
            Status = GroupReservationStatus.Open
        };

        var reservation = new Reservation
        {
            Id = reservationId,
            CustomerId = userId,
            TableId = "table-1",
            WaiterId = "waiter-1"
        };

        var otherSession = new GuestSession
        {
            Id = sessionToDeleteId,
            GroupReservationId = "different-group",
            GuestName = "Guest",
            Dishes = []
        };

        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupId)).ReturnsAsync(group);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(reservationId)).ReturnsAsync(reservation);
        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(sessionToDeleteId)).ReturnsAsync(otherSession);

        await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.DeleteGuestSessionAsync(userId, groupId, sessionToDeleteId));
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_NonOwner_ThrowsActionForbiddenException()
    {
        string userId = "stranger";
        string groupId = "group-1";
        string reservationId = "res-1";
        string sessionToDeleteId = "session-1";

        var group = new GroupReservation
        {
            GroupReservationId = groupId,
            ReservationId = reservationId,
            ReservationToken = "token-1",
            Status = GroupReservationStatus.Open
        };

        var reservation = new Reservation
        {
            Id = reservationId,
            CustomerId = "real-owner",
            TableId = "table-1",
            WaiterId = "waiter-1"
        };

        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupId)).ReturnsAsync(group);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(reservationId)).ReturnsAsync(reservation);

        await Assert.ThrowsAsync<ActionForbiddenException>(() =>
            _guestSessionService.DeleteGuestSessionAsync(userId, groupId, sessionToDeleteId));
    }
}





