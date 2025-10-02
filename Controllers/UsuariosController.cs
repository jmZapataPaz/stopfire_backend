using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StopFire.Api.Data;
using StopFire.Api.Dtos;
using StopFire.Api.Models;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Text.Json.Nodes;
using NetTopologySuite.IO;
using Microsoft.Extensions.Caching.Memory;
using StopFire.Api.Services;
using System.Security.Cryptography;
using stopfire_backend.Models;
using StopFire.Api.Dtos.Reportes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using StopFire.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using NetTopologySuite.Geometries;
using System.Globalization;

namespace StopFire.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public partial class UsuariosController : ControllerBase
{
    private readonly StopFireDbContext _db;
    private readonly IConfiguration _config;
    private readonly IMemoryCache _cache;
    private readonly IEmailSender _emailSender;
    private readonly IWebHostEnvironment _env;
    private readonly IHubContext<NotificacionesHub> _hub;
    private readonly GeometryFactory _geometryFactory;

    public UsuariosController(
        StopFireDbContext db,
        IConfiguration config,
        IMemoryCache cache,
        IEmailSender emailSender,
        IWebHostEnvironment env,
        IHubContext<NotificacionesHub> hub,
        GeometryFactory geometryFactory)
    {
        _db = db;
        _config = config;
        _cache = cache;
        _emailSender = emailSender;
        _env = env;
        _hub = hub;
        _geometryFactory = geometryFactory;
    }

    private int? TryGetUserIdFromToken()
    {
        var idStr =
            User.FindFirstValue(ClaimTypes.NameIdentifier) ??
            User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        return int.TryParse(idStr, out var i) ? i : null;
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequestDto dto, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var correo = dto.Correo.Trim().ToLowerInvariant();
        var usuario = await _db.Usuarios
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Correo.ToLower() == correo, ct);

        if (usuario is null || !BCrypt.Net.BCrypt.Verify(dto.Contrasena, usuario.Contrasena))
            return Unauthorized(new { mensaje = "Credenciales inv�lidas." });
        var token = GenerarJwt(usuario);
        return Ok(new
        {
            token,
            usuario = new
            {
                usuario.Id,
                usuario.Nombre,
                usuario.Apellido,
                usuario.Correo,
                usuario.Celular,
                usuario.RolId
            }
        });
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> ObtenerPorId(int id, CancellationToken ct)
    {
        var usuario = await _db.Usuarios.FindAsync([id], ct);
        if (usuario is null) return NotFound();

        return Ok(new
        {
            usuario.Id,
            usuario.Nombre,
            usuario.Apellido,
            usuario.Ci,
            usuario.Correo,
            usuario.Celular,
            usuario.RolId
        });
    }
    [HttpGet]
    public async Task<IActionResult> ObtenerTodos(CancellationToken ct)
    {
        var usuarios = await _db.Usuarios
            .AsNoTracking()
            .OrderBy(u => u.Id)
            .Select(u => new
            {
                u.Id,
                u.Nombre,
                u.Apellido,
                u.Ci,
                u.Correo,
                u.Celular,
                u.RolId
            })
            .ToListAsync(ct);

        return Ok(usuarios);
    }

    private string GenerarJwt(Usuario usuario)
    {
        var jwt = _config.GetSection("Jwt");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt["Key"]!));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, usuario.Id.ToString()),
            new(ClaimTypes.NameIdentifier, usuario.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, usuario.Correo),
            new(ClaimTypes.Name, $"{usuario.Nombre} {usuario.Apellido}"),
            new("role_id", usuario.RolId.ToString())
        };

        var token = new JwtSecurityToken(
            issuer: jwt["Issuer"],
            audience: jwt["Audience"],
            claims: claims,
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    [HttpGet("estaciones")]
    public async Task<IActionResult> ListarEstacionesPublic(CancellationToken ct)
    {
        var estaciones = await _db.Estaciones
            .AsNoTracking()
            .OrderBy(e => e.Id)
            .ToListAsync(ct);

        var writer = new GeoJsonWriter();
        var datos = estaciones.Select(e => new
        {
            e.Id,
            e.IdUsuario,
            e.Nombre,
            e.Latitud,
            e.Longitud,
            e.DescripcionDireccion,
            e.Celular,
            e.Estado,
            cobertura = e.Cobertura is null ? null : JsonNode.Parse(writer.Write(e.Cobertura))
        });

        return Ok(datos);
    }

    [HttpGet("estaciones/{id:int}")]
    public async Task<IActionResult> ObtenerEstacionPublicaPorId(int id, CancellationToken ct)
    {
        var e = await _db.Estaciones.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return NotFound();

        var writer = new GeoJsonWriter();
        var dto = new
        {
            e.Id,
            e.IdUsuario,
            e.Nombre,
            e.Latitud,
            e.Longitud,
            e.DescripcionDireccion,
            e.Celular,
            e.Estado,
            cobertura = e.Cobertura is null ? null : JsonNode.Parse(writer.Write(e.Cobertura))
        };

        return Ok(dto);
    }


    [HttpPost("registrar/iniciar")]
    public async Task<IActionResult> RegistrarIniciar([FromBody] RegistrarUsuarioDto dto, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var correo = dto.Correo.Trim().ToLowerInvariant();
        var ci = dto.Ci.Trim();

        var existe = await _db.Usuarios.AsNoTracking()
            .AnyAsync(u => u.Correo.ToLower() == correo || u.Ci == ci, ct);
        if (existe) return Conflict(new { mensaje = "Ya existe un usuario con ese correo o CI." });

        var otp = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        var hash = BCrypt.Net.BCrypt.HashPassword(dto.Contrasena);

        var key = $"reg:{correo}";
        var payload = new PendingRegistration
        {
            Nombre = dto.Nombre.Trim(),
            Apellido = dto.Apellido.Trim(),
            Ci = ci,
            Correo = correo,
            Celular = string.IsNullOrWhiteSpace(dto.Celular) ? null : dto.Celular.Trim(),
            PasswordHash = hash,
            RolId = 3,
            Otp = otp
        };
        _cache.Set(key, payload, TimeSpan.FromMinutes(10));

        var asunto = "C�digo de verificaci�n (OTP)";
        var cuerpo = $@"<p>Hola {dto.Nombre},</p>
                        <p>Tu c�digo de verificaci�n es: <b>{otp}</b></p>
                        <p>Vence en 10 minutos.</p>";
        await _emailSender.SendAsync(correo, asunto, cuerpo, ct);

        return Accepted(new { mensaje = "OTP enviado al correo. Verifica para completar el registro." });
    }

    [HttpPost("registrar/verificar")]
    public async Task<IActionResult> RegistrarVerificar([FromBody] VerificarOtpDto dto, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var correo = dto.Correo.Trim().ToLowerInvariant();
        var key = $"reg:{correo}";
        if (!_cache.TryGetValue<PendingRegistration>(key, out var data))
            return BadRequest(new { mensaje = "Solicitud no encontrada o OTP expirado." });

        if (!string.Equals(dto.Codigo?.Trim(), data.Otp, StringComparison.Ordinal))
            return BadRequest(new { mensaje = "OTP inv�lido." });

        var existe = await _db.Usuarios.AsNoTracking()
            .AnyAsync(u => u.Correo.ToLower() == correo || u.Ci == data.Ci, ct);
        if (existe) return Conflict(new { mensaje = "Ya existe un usuario con ese correo o CI." });

        var usuario = new Usuario
        {
            Nombre = data.Nombre,
            Apellido = data.Apellido,
            Ci = data.Ci,
            Correo = data.Correo,
            Celular = data.Celular,
            Contrasena = data.PasswordHash,
            RolId = data.RolId
        };

        _db.Usuarios.Add(usuario);
        await _db.SaveChangesAsync(ct);
        _cache.Remove(key);

        var result = new
        {
            usuario.Id,
            usuario.Nombre,
            usuario.Apellido,
            usuario.Ci,
            usuario.Correo,
            usuario.Celular,
            usuario.RolId
        };
        return CreatedAtAction(nameof(ObtenerPorId), new { id = usuario.Id }, result);
    }

    [Authorize]
    [HttpPost("reportes")]
    [RequestSizeLimit(10_000_000)]
    public async Task<IActionResult> CrearReporte([FromForm] CrearReporteDto dto, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var userId = TryGetUserIdFromToken();
        if (userId is null) return Unauthorized(new { mensaje = "Token inválido." });

        string? fotoUrl = null;
        if (dto.Foto is not null && dto.Foto.Length > 0)
        {
            var validTypes = new[] { "image/jpeg", "image/png", "image/webp" };
            if (!validTypes.Contains(dto.Foto.ContentType))
                return BadRequest(new { mensaje = "Formato de imagen no soportado." });

            var folder = Path.Combine(_env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"), "reportes");
            Directory.CreateDirectory(folder);
            var fileName = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}{Path.GetExtension(dto.Foto.FileName)}";
            var fullPath = Path.Combine(folder, fileName);
            await using (var fs = new FileStream(fullPath, FileMode.Create))
            {
                await dto.Foto.CopyToAsync(fs, ct);
            }
            var baseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
            fotoUrl = $"{baseUrl}/reportes/{fileName}";
        }

        var latitudValida = double.TryParse(dto.Latitud.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var latitud);
        var longitudValida = double.TryParse(dto.Longitud.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var longitud);
        if (!latitudValida || !longitudValida)
            return BadRequest(new { mensaje = "Latitud/Longitud inválidas." });

        var reporte = new Reporte
        {
            IdUsuario = userId.Value,
            Descripcion = string.IsNullOrWhiteSpace(dto.Descripcion) ? string.Empty : dto.Descripcion.Trim(),
            FotoUrl = fotoUrl,
            Latitud = latitud,
            Longitud = longitud,
            Estado = "PENDIENTE"
        };

        _db.Reportes.Add(reporte);
        await _db.SaveChangesAsync(ct);
        var point = _geometryFactory.CreatePoint(new Coordinate(longitud, latitud));
        var contenedoras = await _db.Estaciones
            .AsNoTracking()
            .Where(e => e.Estado && e.Cobertura != null && e.Cobertura.Contains(point))
            .ToListAsync(ct);

        int? primeraCandidataId = null;
        if (contenedoras.Count > 0)
        {
            primeraCandidataId = contenedoras
                .OrderBy(e => e.Cobertura!.Centroid.Distance(point))
                .First().Id;
        }
        else
        {
            var fallback = await _db.Estaciones
                .AsNoTracking()
                .Where(e => e.Estado && e.Cobertura != null)
                .OrderBy(e => e.Cobertura!.Distance(point))
                .FirstOrDefaultAsync(ct);
            primeraCandidataId = fallback?.Id;
        }
        await _hub.Clients.All.SendAsync("ReporteCreado", new
        {
            Id = reporte.Id,
            Descripcion = reporte.Descripcion,
            Latitud = reporte.Latitud,
            Longitud = reporte.Longitud,
            ImagenUrl = reporte.FotoUrl,
            Estado = reporte.Estado,
            PrimeraCandidata = primeraCandidataId
        }, ct);
        if (primeraCandidataId.HasValue)
        {
            await _hub.Clients.Group($"estacion_{primeraCandidataId.Value}")
                .SendAsync("ReportePendiente", new
                {
                    ReporteId = reporte.Id,
                    Candidata = primeraCandidataId.Value,
                    reporte.Descripcion,
                    reporte.Latitud,
                    reporte.Longitud
                }, ct);
        }

        return CreatedAtAction(nameof(ObtenerReportePorId), new { id = reporte.Id }, new
        {
            reporte.Id,
            reporte.IdUsuario,
            reporte.Descripcion,
            reporte.FotoUrl,
            reporte.Latitud,
            reporte.Longitud,
            reporte.Estado,
            primeraCandidata = primeraCandidataId
        });
    }

    [HttpGet("reportes/{id:int}")]
    public async Task<IActionResult> ObtenerReportePorId(int id, CancellationToken ct)
    {
        var r = await _db.Reportes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return NotFound();
        return Ok(new
        {
            r.Id,
            r.IdUsuario,
            r.Descripcion,
            r.FotoUrl,
            r.Latitud,
            r.Longitud,
            r.Estado
        });
    }
}