# GEMINI.md

Положи цей файл у корінь репозиторію (`marathon2026/`, поруч із `homework-backend.md`).

## Завдання

Повний текст домашки: `homework-backend.md` (рівні 1, 2, 3). Працюємо **лише з бекендом**: `backend/restaurant-backend-app`. Frontend (`frontend/`), `infra/` і deploy-скрипти не чіпати.

## Стек

- C# / .NET 10, одна AWS Lambda `api-handler` за API Gateway (proxy-інтеграція)
- DynamoDB через `IDynamoDBContext`, Cognito для авторизації
- Деплой через AWS Syndicate (не запускай `syndicate`, `terraform`, `aws` команди)
- Тести: xUnit + Moq + AutoFixture (`Tests/Services/*Tests.cs`)

Корінь коду: `backend/restaurant-backend-app/dnapp/lambdas/api-handler/`

## Архітектура

`Handler → Service → Repository`. Маршрути вручну реєструються в `Routing/RouteRegistry.cs` (DI через `ServiceCollection`, handler створюється через `ActivatorUtilities`). Frameworks на кшталт ASP.NET controllers тут **немає**.

Папки: `Handlers/<Домен>`, `Services/<Домен>` (інтерфейс `I...Service` + реалізація), `Repositories/<Домен>`, `Models/<Домен>` (request/response DTO), `Entities`, `Exceptions`, `Helpers`, `Mappers`.

## Як додати endpoint (чекліст, виконуй усі кроки)

1. DTO/record у `Models/<Домен>`.
2. Метод в `I...Service` і реалізацію в сервісі. Якщо додаєш залежності в конструктор сервісу, онови конструктор у відповідному `Tests/Services/*Tests.cs`.
3. Handler у `Handlers/<Домен>/...Handler.cs`, що реалізує `IApiRequestHandler`. Бери за зразок `Handlers/GuestSessions/DeleteGuestSessionHandler.cs`.
4. Реєстрація в `RouteRegistry.cs` (для guest/group маршрутів це `GetGuestAndGroupRoutes()`, для reservations `GetReservationAndOrderRoutes()`).
5. **Додай шлях в `backend/restaurant-backend-app/deployment_resources.json`** (секція API Gateway). Скопіюй блок, наприклад, `"/guest/groups/{groupReservationId}/sessions/{guestSessionId}"` з `"authorization_type": "cognito_authorizer"`, `enable_cors` і `enable_proxy`. Без цього API Gateway не пропустить запит. Якщо шлях уже є, не дублюй.
6. Unit-тести в `Tests/Services` (мінімум: успішний сценарій і кожен кейс помилки з умов завдання).
7. Звір `docs/ApiRoutes.md` і `swagger_src/swagger-ui/swagger-endpoints.yaml`: там ці endpoint-и вже можуть бути описані, оновлюй лише розбіжності.

## Конвенції коду (дотримуйся існуючого стилю)

- Handler: primary constructor, `request.TryGetPathValue("name", out var x)`, `UserContextHelper.GetAuthenticatedUserId(request)` (якщо `null`, повертай 401).
- Тіло POST читай так, як це робить інший handler з body (подивись `RequestBodyHelper`).
- Перевірка Owner: `reservation.CustomerId == userId`, інакше `ActionForbiddenException` (403). Готовий приклад: приватний `ValidateReservationOwnerAsync(groupReservationId, userId)` в `GuestSessionService`.
- Мапінг помилок у handler:
  - `GroupReservationNotFoundException`, `ReservationNotFoundException`, `GuestSessionNotFoundException` → 404 через `ResponseBuilder.CreateErrorResponse`
  - `ActionForbiddenException` → 403
  - `ValidationException` → 400 (`ResponseBuilder.CreateErrorCodeResponse(ex)` у guest-handler-ах)
- Для умов «повернути 400» кидай `ValidationException`, для 404 відповідний `...NotFoundException`.
- **JSON-відповіді віддавай через `ResponseBuilder.CreateJsonResponse(200, body)`.** Він використовує camelCase. `CreateSuccessResponse(object)` серіалізує без camelCase, і поля records вийдуть як `Participants`, `Total`, а в завданні вимагається `participants`, `total`.
- Async усюди, `Nullable` увімкнено, без нових NuGet-пакетів.

## Що вже є в проєкті (спочатку прочитай, не дублюй)

- `Models/GuestSessions/GetAllResponse.cs`: `GetAllSessionsResponse(Participants, Total)`, `Participant(Id, Type, Name, Items, Total, IsSubmitted)`, `ParticipantType { Owner, Guest }`. Використай для рівня 1.
- `Entities/GuestSession.cs`: `Dishes` (`List<OrderItem>`), `IsSubmitted`, `IsOwnerSession`, `OwnerUserId`, `GroupReservationId`.
- `IGuestSessionRepository.GetAllByGroupReservationIdAsync(groupReservationId)`.
- `IDishRepository.GetDishesByIdsAsync(ids)` і `DishMapper.ToDishResponse`.
- `Order` / `OrderItem` (`DishId`, `Quantity`, `State`), `OrderStatus.SubmittedByCustomer`, `GroupReservationStatus.Submitted`.
- `IOrderRepository.GetOrderByReservationIdAsync` / `SaveOrderAsync`, `IGroupReservationRepository.SaveGroupReservationAsync`.
- `OrderService.SubmitPreOrderAsync`: приклад того, як order переводиться в `SubmittedByCustomer`.

## Стан рівнів на зараз (перевір сам, не вір на слово)

- **Рівень 3 (DELETE session) вже реалізований**: `DeleteGuestSessionHandler`, `GuestSessionService.DeleteGuestSessionAsync`, маршрут і запис у `deployment_resources.json`. Не переписуй з нуля, а звір з умовами завдання. Відомі місця для перевірки: відповідь іде через `CreateSuccessResponse` (див. camelCase вище); у цьому методі всі учасники маплються як `ParticipantType.Guest`, навіть owner session; `dishesById[d.DishId]` падає з `KeyNotFoundException`, якщо страву видалили з меню.
- **Рівень 1 (`GET /guest/groups/{groupReservationId}/sessions`)**: не зареєстрований у `RouteRegistry`. Логіку побудови snapshot (список учасників і сума) краще винести в один приватний метод сервісу і використати і тут, і в DELETE.
- **Рівень 2 (`GET /reservations/{reservationId}/guest-session-id`, `POST /group-reservations/{groupReservationId}/submit`)**: не зареєстровані.
- `GuestSessionService.CreateGroupOwnerSessionAsync` існує, але **не входить в інтерфейс і ніде не викликається**. Тобто owner session може не існувати в БД. Для `guest-session-id`: знайди сесію з `IsOwnerSession == true` і `OwnerUserId == userId` для групи цього reservation. Якщо її немає, створи ліниво (додай метод в `IGuestSessionService`) і поверни її id. Перед реалізацією скажи мені, яке рішення обрав.

## Правила з завдання, які легко пропустити

- Обидва endpoint-и рівня 2 та DELETE: **тільки Owner** (власник reservation), інакше 403.
- Submit (рівень 2): брати лише сесії з `IsSubmitted == true`; зібрати всі страви в один список; згрупувати по `DishId` із сумою `Quantity`; додати в `order.Dishes`; `order.Status = SubmittedByCustomer`; `group.Status = Submitted`; зберегти обидві сутності; повернути `[{ "dishId", "quantity" }]`.
- Submit: reservation / group reservation не знайдено → 404; немає жодної submitted сесії → 400; у submitted сесіях немає жодної страви → 400; order не знайдено → 400.
- Узгодженість: спочатку всі перевірки і підрахунки, потім збереження, щоб при помилці валідації нічого не змінилося.
- Рівень 1: типи учасників `Owner` / `Guest`, `total` учасника = сума `price * quantity`, загальний `total` = сума по учасниках.

## Як працювати

1. Спочатку **тільки дослідження, без змін у коді**: коротко опиши, як зараз влаштований потік reservation → group reservation → guest sessions → order, і що ти плануєш змінити по файлах.
2. Реалізуй **один рівень за раз** (1, потім 2, потім перевірка 3). Після кожного рівня запускай:
   ```
   dotnet build backend/restaurant-backend-app/restaurant-backend-app.sln
   dotnet test backend/restaurant-backend-app/Tests/APIHandler.Tests.csproj
   ```
3. Не змінюй код, що не стосується завдання. Не форматуй чужі файли.
4. Якщо щось у завданні неоднозначне, став питання замість вгадування.
5. У кінці дай список змінених файлів і які команди я маю виконати для перевірки.
