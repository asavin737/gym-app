using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DataAccessLayer.Entities;
using DataAccessLayer.UnitOfWork;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace WebApi.Tests;

[TestClass]
public class Phase10Tests
{
    private const string Password = "Password123!";

    private static ApiApplicationFactory _factory = null!;
    private static HttpClient _client = null!;

    [ClassInitialize]
    public static void Initialize(TestContext context)
    {
        _factory = new ApiApplicationFactory();
        _client = _factory.CreateClient();
    }

    [ClassCleanup]
    public static void Cleanup()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [TestMethod]
    public async Task SixtyFirstRequestIsTooManyRequestsAndDoesNotApplyToAnotherUser()
    {
        var world = await Scenario.CreateWorldAsync(_factory);
        var other = await Scenario.AddUserAsync(_factory);
        try
        {
            var token = await Scenario.TokenAsync(_factory, world.UserId);

            for (var attempt = 0; attempt < 60; attempt++)
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, (await ScheduleAsync(token)).StatusCode);
            }

            var blocked = await ScheduleAsync(token);
            var json = await blocked.Content.ReadAsStringAsync();

            Assert.AreEqual(HttpStatusCode.TooManyRequests, blocked.StatusCode);
            Assert.AreEqual("application/problem+json", blocked.Content.Headers.ContentType!.MediaType);
            using var document = JsonDocument.Parse(json);
            Assert.AreEqual(429, document.RootElement.GetProperty("status").GetInt32());
            Assert.AreEqual("Too Many Requests", document.RootElement.GetProperty("title").GetString());
            Assert.IsFalse(document.RootElement.TryGetProperty("reason", out _));

            var otherResponse = await ScheduleAsync(await Scenario.TokenAsync(_factory, other));
            Assert.AreEqual(HttpStatusCode.BadRequest, otherResponse.StatusCode);
        }
        finally
        {
            await Scenario.DeleteUserAsync(_factory, other);
            await Scenario.DeleteUserAsync(_factory, world.UserId);
            await Scenario.DeleteRoomAsync(_factory, world.RoomId);
        }
    }

    [TestMethod]
    public async Task LoginDoesNotSpendTheLimit()
    {
        var user = await CreateLoginUserAsync();
        try
        {
            string? token = null;

            for (var attempt = 0; attempt < 60; attempt++)
            {
                var response = await LoginAsync(user.Email);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                token = await ReadTokenAsync(response);
            }

            Assert.AreEqual(HttpStatusCode.BadRequest, (await ScheduleAsync(token!)).StatusCode);
        }
        finally
        {
            await Scenario.DeleteUserAsync(_factory, user.Id);
        }
    }

    [TestMethod]
    public async Task LoginStillWorksAfterTheLimitIsSpent()
    {
        var user = await CreateLoginUserAsync();
        try
        {
            var token = await Scenario.TokenAsync(_factory, user.Id);

            for (var attempt = 0; attempt < 60; attempt++)
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, (await ScheduleAsync(token)).StatusCode);
            }

            var login = await LoginAsync(user.Email);
            Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
            Assert.IsFalse(string.IsNullOrWhiteSpace(await ReadTokenAsync(login)));
            Assert.AreEqual(HttpStatusCode.TooManyRequests, (await ScheduleAsync(token)).StatusCode);
        }
        finally
        {
            await Scenario.DeleteUserAsync(_factory, user.Id);
        }
    }

    private static async Task<(long Id, string Email)> CreateLoginUserAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().DbContext;
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        var user = new User
        {
            Email = $"phase10-{Guid.NewGuid():N}@club.test",
            PasswordHash = "pending",
            Role = "user",
            CreatedAt = DateTime.UtcNow
        };
        user.PasswordHash = hasher.HashPassword(user, Password);
        db.Set<User>().Add(user);
        await db.SaveChangesAsync();
        return (user.Id, user.Email);
    }

    private static Task<HttpResponseMessage> LoginAsync(string email)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/auth/login")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { email, password = Password }),
                Encoding.UTF8,
                "application/json")
        };

        return _client.SendAsync(message);
    }

    private static async Task<string> ReadTokenAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("accessToken").GetString()!;
    }

    private static Task<HttpResponseMessage> ScheduleAsync(string token)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, "/schedule");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(message);
    }
}
