using System.Text.Json.Serialization;
using Starfall.Grove.Moba.Api.Hubs;
using Starfall.Grove.Moba.Api.Rooms;
using Starfall.Grove.Moba.Api.Services;

var builder = WebApplication.CreateBuilder(args);

var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["https://metur100.github.io", "http://localhost:5173"];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

builder.Services.AddSignalR(o =>
{
    o.KeepAliveInterval = TimeSpan.FromSeconds(10);
    o.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
    o.MaximumReceiveMessageSize = 32 * 1024;
    o.EnableDetailedErrors = builder.Environment.IsDevelopment();
}).AddJsonProtocol(o =>
{
    o.PayloadSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
});
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));

builder.Services.AddSingleton<RoomManager>();
builder.Services.AddSingleton<Outbox>();
builder.Services.AddSingleton<MatchStore>();
builder.Services.AddHostedService<GameLoop>();

var app = builder.Build();

app.UseCors();
app.UseWebSockets();

app.MapGet("/", () => Results.Text("Mini Rift server for Starfall Grove is running. Connect a client to /hub.", "text/plain"));
app.MapGet("/api/health", (RoomManager rooms, MatchStore store) => new
{
    ok = true,
    rooms = rooms.Rooms.Count,
    players = rooms.Seats.Count,
    database = store.Ready ? "ready" : store.Connecting ? "connecting" : store.Enabled ? "unavailable" : "off",
    databaseError = store.Ready ? null : store.LastError,
    time = DateTime.UtcNow,
});
app.MapGet("/api/catalog", () => MobaHub.BuildCatalog());
app.MapGet("/api/matches/recent", async (MatchStore store) => Results.Ok(await store.RecentAsync()));
app.MapGet("/api/stats/heroes", async (MatchStore store) => Results.Ok(await store.HeroStatsAsync()));
app.MapHub<MobaHub>("/hub");

_ = app.Services.GetRequiredService<MatchStore>().InitAsync();
app.Run();
