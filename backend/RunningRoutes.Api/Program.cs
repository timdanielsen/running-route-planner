using System.Text.Json.Serialization;
using RunningRoutes.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddHttpClient<IOpenRouteServiceClient, OpenRouteServiceClient>();
builder.Services.AddHttpClient<IOverpassClient, OverpassClient>(client =>
{
    // Public Overpass instance; these lookups are best-effort, so keep this timeout short
    // rather than letting a slow Overpass response stall route generation.
    client.Timeout = TimeSpan.FromSeconds(10);
    // Overpass rejects requests with no/generic User-Agent with 406 - .NET's HttpClient sends
    // none by default, so this is required, not just polite.
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "RunningRoutePlanner/1.0 (+https://github.com/timdanielsen/running-route-planner)");
});
builder.Services.AddScoped<IGraveyardLookupService, GraveyardLookupService>();
builder.Services.AddScoped<IAmenityLookupService, AmenityLookupService>();

const string CorsPolicy = "AllowFrontendDev";
builder.Services.AddCors(options =>
{
    options.AddPolicy(CorsPolicy, policy =>
    {
        policy.WithOrigins("http://localhost:5173")
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors(CorsPolicy);
app.UseAuthorization();
app.MapControllers();

app.Run();
