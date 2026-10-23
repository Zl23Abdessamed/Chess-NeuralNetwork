// Endpoints, modes, invalid requests, engine failures
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ChessModel.Tests;

public class ApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ApiTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task LiveEndpoint_ReturnsOk()
    {
        var response = await _client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ReadyEndpoint_ReturnsOkWithModelDetails()
    {
        var response = await _client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PostMove_ValidFen_ReturnsLegalMove()
    {
        var payload = new { fen = "rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq - 0 1", mode = "imitate" };
        var response = await _client.PostAsJsonAsync("/api/v1/move", payload);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MoveResponse>();

        Assert.NotNull(body);
        Assert.False(string.IsNullOrEmpty(body.Move));
        Assert.Equal("imitate", body.Mode);
        Assert.NotEmpty(body.Candidates);
    }

    [Fact]
    public async Task PostMove_InvalidFen_ReturnsBadRequest()
    {
        var payload = new { fen = "invalid-chess-board-fen", mode = "imitate" };
        var response = await _client.PostAsJsonAsync("/api/v1/move", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostMove_UnknownMode_ReturnsBadRequest()
    {
        var payload = new { fen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", mode = "cheat_mode" };
        var response = await _client.PostAsJsonAsync("/api/v1/move", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostMove_CheckmatedPosition_ReturnsConflict()
    {
        // Scholar's Mate final position (checkmate)
        string checkmateFen = "r1bqkb1r/pppp1Qpp/2n5/4p3/2B1n3/8/PPPP1PPP/RNB1K1NR b KQkq - 0 4";
        var payload = new { fen = checkmateFen, mode = "imitate" };
        var response = await _client.PostAsJsonAsync("/api/v1/move", payload);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    private sealed record MoveResponse(string Move, string Mode, List<object> Candidates);
}