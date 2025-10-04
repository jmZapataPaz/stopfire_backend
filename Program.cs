using Microsoft.EntityFrameworkCore;
using StopFire.Api.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using StopFire.Api.Services;
using StopFire.Api.Hubs;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(o =>
{
    o.ListenAnyIP(5190);
     
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddCors(o =>
{
    o.AddPolicy("CorsPolicy", p => p
        .SetIsOriginAllowed(_ => true)          // acepta cualquier origen (dev)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()                     // necesario con Authorization
    );
});

builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();

var cs = builder.Configuration.GetConnectionString("StopFireDb");
builder.Services.AddDbContext<StopFireDbContext>(opt =>
    opt.UseNpgsql(cs, o => o.UseNetTopologySuite()));

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var jwt = builder.Configuration.GetSection("Jwt");
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = false,
            RequireExpirationTime = false,
            ValidIssuer = jwt["Issuer"],
            ValidAudience = jwt["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt["Key"]!))
        };
        // NECESARIO PARA SIGNALR (token via query access_token)
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var accessToken = ctx.Request.Query["access_token"];
                var path = ctx.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs/notificaciones"))
                {
                    ctx.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", p => p.RequireClaim("role_id", "1")); 
    options.AddPolicy("BomberoOnly", p => p.RequireClaim("role_id", "2"));
});

builder.Services.AddSignalR();
builder.Services.AddSingleton(NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

//app.UseHttpsRedirection(); // Comentado para evitar redirección a https sin endpoint
app.UseStaticFiles();
app.UseCors("CorsPolicy");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.MapHub<NotificacionesHub>("/hubs/notificaciones");

// DEBUG: forzar evento
app.MapPost("/debug/send-reporte", async (IHubContext<NotificacionesHub> hub) =>
{
    var dto = new {
        Id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Descripcion = "REPORTE DEBUG",
        Latitud = -17.78,
        Longitud = -63.18,
        ImagenUrl = "",
        CreadoEn = DateTime.UtcNow
    };
    await hub.Clients.All.SendAsync("ReporteCreado", dto);
    return Results.Ok(dto);
});

app.Run();
