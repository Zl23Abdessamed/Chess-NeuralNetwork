using System.Text.Json;
using ChessModel.Core;
using Microsoft.AspNetCore.Mvc;



var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});



static string ResolvePath(string relativePath)
{
    if (Path.IsPathRooted(relativePath) && (File.Exists(relativePath) || Directory.Exists(relativePath)))
        return relativePath;

    var searchRoots = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
    foreach (var root in searchRoots)
    {
        var dir = new DirectoryInfo(root);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate) || Directory.Exists(candidate))
                return Path.GetFullPath(candidate);
            dir = dir.Parent;
        }
    }

    return relativePath;
}

// 1. Locate config and model paths dynamically
string configPath = ResolvePath(builder.Configuration["ConfigPath"] ?? "configs/config.json");
string modelDir = ResolvePath(builder.Configuration["ModelDir"] ?? "models/v1");

if (!File.Exists(configPath))
    throw new FileNotFoundException($"Configuration file not found: {configPath}");

var configDoc = JsonDocument.Parse(File.ReadAllText(configPath));
var carefulJson = configDoc.RootElement.GetProperty("careful");

string rawStockfishPath = carefulJson.GetProperty("stockfishPath").GetString() ?? "stockfish";
string resolvedStockfishPath = ResolvePath(rawStockfishPath);

var carefulConfig = new CarefulConfig(
    StockfishPath: resolvedStockfishPath,
    Depth: carefulJson.GetProperty("depth").GetInt32(),
    Threads: carefulJson.GetProperty("threads").GetInt32(),
    HashMb: carefulJson.GetProperty("hashMb").GetInt32(),
    MaxNeuralCandidates: carefulJson.GetProperty("maxNeuralCandidates").GetInt32(),
    MaxCentipawnLoss: carefulJson.GetProperty("maxCentipawnLoss").GetInt32(),
    FallbackToStockfishBestMove: carefulJson.GetProperty("fallbackToStockfishBestMove").GetBoolean(),
    AnalysisTimeoutMilliseconds: carefulJson.GetProperty("analysisTimeoutMilliseconds").GetInt32());

// 2. Initialize Singletons
var rules = new ChessLibraryAdapter();
var (model, modelConfig) = ModelArtifacts.Load(modelDir);
var predictor = new PolicyPredictor(rules, model);
var engine = new StockfishService(carefulConfig);
var moveService = new MoveService(rules, predictor, engine, carefulConfig);

builder.Services.AddSingleton<IChessRulesAdapter>(rules);
builder.Services.AddSingleton<IEngineAnalyzer>(engine);
builder.Services.AddSingleton(predictor);
builder.Services.AddSingleton(moveService);
builder.Services.AddSingleton(modelConfig);

var app = builder.Build();

app.UseCors();

// 3. Health Endpoints
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

app.MapGet("/health/ready", ([FromServices] ModelConfig cfg) => Results.Ok(new
{
    status = "ready",
    model = new
    {
        schemaVersion = cfg.SchemaVersion,
        chosenEpoch = cfg.ChosenEpoch,
        valTop1 = cfg.ValidationTop1,
        valTop5 = cfg.ValidationTop5
    }
}));

// 4. POST /api/v1/move
app.MapPost("/api/v1/move", async (
    [FromBody] MoveApiRequest? request,
    [FromServices] MoveService service,
    CancellationToken ct) =>
{
    if (request == null || string.IsNullOrWhiteSpace(request.Fen))
        return Results.BadRequest(new { error = "Request body must include a valid 'fen'." });

    string mode = (request.Mode ?? "imitate").ToLowerInvariant();
    if (mode != "imitate" && mode != "careful")
        return Results.BadRequest(new { error = $"Unknown mode '{request.Mode}'. Allowed: 'imitate', 'careful'." });

    try
    {
        var result = await service.GetMoveAsync(request.Fen, mode, ct);
        return Results.Ok(result);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("terminal"))
    {
        return Results.Conflict(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Engine Error]: {ex.Message}");
        return Results.Json(new { error = "Stockfish engine error", details = ex.Message }, statusCode: 503);
    }
});

app.Run();

// DTOs
public sealed record MoveApiRequest(string Fen, string? Mode);

// Robust recursive path resolver that walks upward from execution directories


public partial class Program { }