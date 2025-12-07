using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StopFire.Api.Data;
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
using System.ComponentModel.DataAnnotations;
using stopfire_backend.Dtos.autenticacion;
using stopfire_backend.Dtos.cuenta;
using System.Text.RegularExpressions;

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
            .FirstOrDefaultAsync(u => u.Correo.ToLower() == correo, ct);

        if (usuario is null || !BCrypt.Net.BCrypt.Verify(dto.Contrasena, usuario.Contrasena))
            return Unauthorized(new { mensaje = "Credenciales inválidas." });

        if (usuario.Estado == false)
            return StatusCode(StatusCodes.Status403Forbidden, new { mensaje = "Su cuenta ha sido dada de baja. Contactese con soporte por favor" });

        usuario.UltimoIngreso = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var estacionId = await _db.Estaciones
            .AsNoTracking()
            .Where(e => e.IdUsuario == usuario.Id)
            .Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(ct);

        var token = GenerarJwt(usuario, estacionId); 
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
                usuario.RolId,
                UltimoIngreso = usuario.UltimoIngreso
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
    private string GenerarJwt(Usuario usuario, int? estacionId = null)
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

        if (estacionId.HasValue)
            claims.Add(new Claim("estacion_id", estacionId.Value.ToString()));

        var token = new JwtSecurityToken(
            issuer: jwt["Issuer"],
            audience: jwt["Audience"],
            claims: claims,
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    [HttpGet("estaciones")]
    public async Task<IActionResult> ListarEstacionesPublicas(CancellationToken ct)
    {
        var estaciones = await _db.Estaciones
            .AsNoTracking()
            .Where(e => e.Estado == true) 
            .OrderBy(e => e.Id)
            .ToListAsync(ct);

        var writer = new NetTopologySuite.IO.GeoJsonWriter();
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
            cobertura = e.Cobertura is null ? null : System.Text.Json.Nodes.JsonNode.Parse(writer.Write(e.Cobertura))
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
            return BadRequest(new { mensaje = "OTP inválido." });

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
            Direccion = string.IsNullOrWhiteSpace(dto.Direccion) ? null : dto.Direccion!.Trim(), 
            Estado = "PENDIENTE",
            FechaCreacion = DateTime.UtcNow 
        };

        reporte.Confirmaciones = 1;
        _db.Reportes.Add(reporte);
        await _db.SaveChangesAsync(ct);

        var u = await _db.Usuarios
            .AsNoTracking()
            .Where(x => x.Id == userId.Value)
            .Select(x => new { x.Nombre, x.Apellido, x.Ci, x.Correo, x.Celular })
            .FirstAsync(ct);

        var point = _geometryFactory.CreatePoint(new Coordinate(longitud, latitud));
        var contenedoras = await _db.Estaciones
            .AsNoTracking()
            .Where(e => e.Estado && e.Cobertura != null && e.Cobertura.Contains(point)) // SOLO activas
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
                .Where(e => e.Estado && e.Cobertura != null) // SOLO activas
                .OrderBy(e => e.Cobertura!.Distance(point))
                .FirstOrDefaultAsync(ct);
            primeraCandidataId = fallback?.Id;
        }

        int riesgoPercent = Math.Min(100, (reporte.Confirmaciones ?? 0) * 20); 

        await _hub.Clients.All.SendAsync("ReporteCreado", new
        {
            Id = reporte.Id,
            Descripcion = reporte.Descripcion,
            Latitud = reporte.Latitud,
            Longitud = reporte.Longitud,
            ImagenUrl = reporte.FotoUrl,
            Estado = reporte.Estado,
            PrimeraCandidata = primeraCandidataId,
            Confirmaciones = reporte.Confirmaciones ?? 0,
            RiesgoPercent = riesgoPercent,
            Direccion = reporte.Direccion, 
            usuarioNombre = $"{u.Nombre} {u.Apellido}".Trim(),
            usuarioCi = u.Ci,
            usuarioCelular = u.Celular,
            usuarioEmail = u.Correo,
        }, ct);

        if (primeraCandidataId.HasValue)
        {
            // Verificar que sigue activa antes de notificar
            var activa = await _db.Estaciones.AsNoTracking()
                .AnyAsync(e => e.Id == primeraCandidataId.Value && e.Estado, ct);
            if (activa)
            {
                await _hub.Clients.Group($"estacion_{primeraCandidataId.Value}")
                    .SendAsync("ReportePendiente", new
                    {
                        ReporteId = reporte.Id,
                        Candidata = primeraCandidataId.Value,
                        reporte.Descripcion,
                        reporte.Latitud,
                        reporte.Longitud,
                        Direccion = reporte.Direccion,
                        usuarioNombre = $"{u.Nombre} {u.Apellido}".Trim(),
                        usuarioCi = u.Ci,
                        usuarioCelular = u.Celular,
                        usuarioEmail = u.Correo,
                    }, ct);
            }
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
            primeraCandidata = primeraCandidataId,
            Direccion = reporte.Direccion, 
            usuarioNombre = $"{u.Nombre} {u.Apellido}".Trim(),
            usuarioCi = u.Ci,
            usuarioCelular = u.Celular,
            usuarioEmail = u.Correo,
        });
    }

    [HttpGet("reportes/{id:int}")]
    public async Task<IActionResult> ObtenerReportePorId(int id, CancellationToken ct)
    {
        var dto = await _db.Reportes
            .AsNoTracking()
            .Where(r => r.Id == id)
            .Select(r => new
            {
                r.Id,
                r.IdUsuario,
                r.Descripcion,
                r.FotoUrl,
                r.Latitud,
                r.Longitud,
                r.Estado,
                r.FechaCreacion,
                r.Direccion, 
                Confirmaciones = r.Confirmaciones ?? 0,
                RiesgoPercent = Math.Min(100, (r.Confirmaciones ?? 0) * 20),
                UsuarioNombre = _db.Usuarios
                    .AsNoTracking()
                    .Where(u => u.Id == r.IdUsuario)
                    .Select(u => (u.Nombre + " " + u.Apellido).Trim())
                    .FirstOrDefault(),
                UsuarioCi = _db.Usuarios
                    .AsNoTracking()
                    .Where(u => u.Id == r.IdUsuario)
                    .Select(u => u.Ci)
                    .FirstOrDefault(),
                UsuarioCelular = _db.Usuarios
                    .AsNoTracking()
                    .Where(u => u.Id == r.IdUsuario)
                    .Select(u => u.Celular)
                    .FirstOrDefault(),
                EstacionId = _db.Asignaciones
                    .AsNoTracking()
                    .Where(a => a.IdReporte == r.Id)
                    .OrderByDescending(a => a.Id)
                    .Select(a => (int?)a.IdEstacion)
                    .FirstOrDefault()
            })
            .FirstOrDefaultAsync(ct);

        if (dto is null) return NotFound();
        return Ok(dto);
    }

    [HttpPost("reportes/{id:int}/confirm")]
    public async Task<IActionResult> ConfirmarReporte(int id, CancellationToken ct)
    {
        var userId = TryGetUserIdFromToken();
        if (userId is null) return Unauthorized(new { mensaje = "Token inválido." });

        var r = await _db.Reportes.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return NotFound(new { mensaje = "Reporte no encontrado." });

        var yaConfirmo = await _db.ConfirmacionesReporte
            .AsNoTracking()
            .AnyAsync(c => c.IdReporte == id && c.IdUsuario == userId.Value, ct);
        
        if (yaConfirmo)
        {
            return Conflict(new { mensaje = "Ya confirmaste este incidente. Aguarda la llegada de los bomberos." });
        }
        var confirmacion = new ConfirmacionReporte
        {
            IdReporte = id,
            IdUsuario = userId.Value,
            FechaConfirmacion = DateTime.UtcNow
        };
        _db.ConfirmacionesReporte.Add(confirmacion);
        r.Confirmaciones = (r.Confirmaciones ?? 0) + 1;
        await _db.SaveChangesAsync(ct);

        await _hub.Clients.All.SendAsync("ReporteConfirmado", new
        {
            Id = r.Id,
            Confirmaciones = r.Confirmaciones,
            ConfirmadoPor = userId.Value
        }, ct);

        return Ok(new
        {
            mensaje = "Confirmación registrada.",
            reporteId = r.Id,
            confirmaciones = r.Confirmaciones
        });
    }

    [HttpGet("reportes")]
    public async Task<IActionResult> ListarReportes(
        [FromQuery] string? estado,
        [FromQuery] int? usuarioId,
        [FromQuery] double? lat,
        [FromQuery] double? lon,
        [FromQuery] double? radiusMeters,
        CancellationToken ct = default)
    {
        var q = _db.Reportes.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(estado))
        {
            var estadoNorm = estado.Trim().ToUpperInvariant();
            q = q.Where(r => r.Estado.ToUpper() == estadoNorm);
        }

        if (usuarioId.HasValue)
            q = q.Where(r => r.IdUsuario == usuarioId.Value);

        if (lat.HasValue && lon.HasValue && radiusMeters.HasValue)
        {
            var degLat = radiusMeters.Value / 111320.0;
            var degLon = radiusMeters.Value / (111320.0 * Math.Cos(Math.PI * lat.Value / 180.0));
            var minLat = lat.Value - degLat;
            var maxLat = lat.Value + degLat;
            var minLon = lon.Value - degLon;
            var maxLon = lon.Value + degLon;

            q = q.Where(r =>
                r.Latitud >= minLat && r.Latitud <= maxLat &&
                r.Longitud >= minLon && r.Longitud <= maxLon);
        }

        var prelim = await q
            .OrderByDescending(r => r.FechaCreacion)
            .Select(r => new
            {
                r.Id,
                r.IdUsuario,
                r.Descripcion,
                r.FotoUrl,
                r.Latitud,
                r.Longitud,
                r.Estado,
                r.FechaCreacion,
                r.Direccion, 
                Confirmaciones = r.Confirmaciones ?? 0,
                RiesgoPercent = Math.Min(100, (r.Confirmaciones ?? 0) * 20),
                UsuarioNombre = _db.Usuarios
                    .AsNoTracking()
                    .Where(u => u.Id == r.IdUsuario)
                    .Select(u => (u.Nombre + " " + u.Apellido).Trim())
                    .FirstOrDefault(),
                UsuarioCi = _db.Usuarios
                    .AsNoTracking()
                    .Where(u => u.Id == r.IdUsuario)
                    .Select(u => u.Ci)
                    .FirstOrDefault(),
                UsuarioCelular = _db.Usuarios
                    .AsNoTracking()
                    .Where(u => u.Id == r.IdUsuario)
                    .Select(u => u.Celular)
                    .FirstOrDefault(),

                EstacionId = _db.Asignaciones
                    .AsNoTracking()
                    .Where(a => a.IdReporte == r.Id)
                    .OrderByDescending(a => a.Id)
                    .Select(a => (int?)a.IdEstacion)
                    .FirstOrDefault()
            })
            .ToListAsync(ct);

        if (lat.HasValue && lon.HasValue && radiusMeters.HasValue)
        {
            var center = _geometryFactory.CreatePoint(new Coordinate(lon.Value, lat.Value));
            var buffer = center.Buffer(radiusMeters.Value / 111320.0);

            prelim = prelim
                .Where(x => x.Latitud != null && x.Longitud != null &&
                            buffer.Contains(_geometryFactory.CreatePoint(new Coordinate(x.Longitud!.Value, x.Latitud!.Value))))
                .ToList();
        }

        return Ok(prelim);
    }

    [Authorize]
    [HttpPut("{id:int}")]
    public async Task<IActionResult> ActualizarUsuario(int id, [FromBody] ActualizarUsuarioDto dto, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var userId = TryGetUserIdFromToken();
        if (userId is null) return Unauthorized(new { mensaje = "Token inválido." });
        if (userId.Value != id) return Forbid("No tienes permisos para actualizar este usuario.");

        var usuario = await _db.Usuarios.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (usuario is null) return NotFound(new { mensaje = "Usuario no encontrado." });

        var celularNorm = dto.Celular.Trim();
        if (string.IsNullOrWhiteSpace(celularNorm))
            return BadRequest(new { mensaje = "Celular es requerido." });
        if (!System.Text.RegularExpressions.Regex.IsMatch(celularNorm, @"^[0-9]{8,15}$"))
            return BadRequest(new { mensaje = "Celular inválido (solo dígitos 8-15)." });

        usuario.Nombre = dto.Nombre.Trim();
        usuario.Apellido = dto.Apellido.Trim();
        usuario.Celular = celularNorm;

        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            usuario.Id,
            usuario.Nombre,
            usuario.Apellido,
            usuario.Celular
        });
    }

    [HttpPost("ultimo-ingreso")]
    [Authorize]
    public async Task<IActionResult> RegistrarUltimoIngreso(CancellationToken ct)
    {
        var userIdStr =
            User.FindFirstValue(ClaimTypes.NameIdentifier) ??
            User.FindFirstValue(JwtRegisteredClaimNames.Sub) ??
            User.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(userIdStr) || !int.TryParse(userIdStr, out var userId))
            return Unauthorized(new { mensaje = "No se pudo determinar el usuario desde el token." });

        var u = await _db.Usuarios.FirstOrDefaultAsync(x => x.Id == userId, ct);
        if (u is null) return NotFound(new { mensaje = "Usuario no encontrado." });

        u.UltimoIngreso = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }

    [HttpPost("contrasena/recuperar/iniciar")]
    [AllowAnonymous]
    public async Task<IActionResult> IniciarRecuperacionContrasena([FromBody] RecuperarContrasenaIniciarDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Correo))
            return BadRequest(new { mensaje = "Correo es requerido." });

        var correo = dto.Correo.Trim().ToLowerInvariant();
        var existe = await _db.Usuarios.AsNoTracking().AnyAsync(u => u.Correo.ToLower() == correo, ct);
        if (!existe)
        {
            return NotFound(new { mensaje = "No existe la cuenta con ese correo." });
        }

        var otp = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        var key = $"pwd:{correo}";
        _cache.Set(key, new { Otp = otp }, TimeSpan.FromMinutes(10));

        var asunto = "Código de recuperación de contraseña";
        var cuerpo = $@"<p>Tu código de recuperación es: <b>{otp}</b></p>
                        <p>Vence en 10 minutos.</p>";
        await _emailSender.SendAsync(correo, asunto, cuerpo, ct);

        return Accepted(new { mensaje = "Se envió un código al correo." });
    }

    [HttpPost("contrasena/recuperar/verificar")]
    [AllowAnonymous]
    public async Task<IActionResult> VerificarRecuperacionContrasena([FromBody] RecuperarContrasenaVerificarDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Correo) || string.IsNullOrWhiteSpace(dto.Codigo) || string.IsNullOrWhiteSpace(dto.NuevaContrasena))
            return BadRequest(new { mensaje = "Datos incompletos." });

        var correo = dto.Correo.Trim().ToLowerInvariant();
        var key = $"pwd:{correo}";
        if (!_cache.TryGetValue<object>(key, out var data))
            return BadRequest(new { mensaje = "codigo vencido" });

        var otpGuardado = (string?)data?.GetType().GetProperty("Otp")?.GetValue(data) ?? string.Empty;
        if (!string.Equals(dto.Codigo.Trim(), otpGuardado, StringComparison.Ordinal))
            return BadRequest(new { mensaje = "codigo incorrecto" });

        // Política: mínimo 8, 1 mayúscula, 1 número
        var strong = Regex.IsMatch(dto.NuevaContrasena, @"^(?=.*[A-Z])(?=.*\d).{8,}$");
        if (!strong)
            return BadRequest(new { mensaje = "La contraseña debe tener mínimo 8 caracteres, 1 mayúscula y 1 número." });

        var user = await _db.Usuarios.FirstOrDefaultAsync(u => u.Correo.ToLower() == correo, ct);
        if (user is null) return BadRequest(new { mensaje = "Usuario no encontrado." });

        user.Contrasena = BCrypt.Net.BCrypt.HashPassword(dto.NuevaContrasena);
        await _db.SaveChangesAsync(ct);
        _cache.Remove(key);

        return Ok(new { mensaje = "Contraseña actualizada." });
    }

    [HttpGet("reportes/{id:int}/verificar-confirmacion")]
    public async Task<IActionResult> VerificarConfirmacion(int id, CancellationToken ct)
    {
        var userId = TryGetUserIdFromToken();
        if (userId is null) return Unauthorized(new { mensaje = "Token inválido." });

        var r = await _db.Reportes
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return NotFound(new { mensaje = "Reporte no encontrado." });

        var yaConfirmo = await _db.ConfirmacionesReporte
            .AsNoTracking()
            .AnyAsync(c => c.IdReporte == id && c.IdUsuario == userId.Value, ct);

        return Ok(new { yaConfirmo });
    }
}