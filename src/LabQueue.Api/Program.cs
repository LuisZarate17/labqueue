using LabQueue.Core.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<LabQueueDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("LabQueue")));

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
