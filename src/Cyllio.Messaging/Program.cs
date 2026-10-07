var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();
var app = builder.Build();
app.MapGet("/", () => Results.Ok(new { service = "Cyllio.Messaging", status = "running" }));
app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");
app.Run();
