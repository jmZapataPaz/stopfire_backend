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

namespace StopFire.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class UsuariosController : ControllerBase
{
    private readonly StopFireDbContext _db;
    private readonly IConfiguration _config;
    private readonly IMemoryCache _cache;
    private readonly IEmailSender _emailSender;

    public UsuariosController(StopFireDbContext db, IConfiguration config, IMemoryCache cache, IEmailSender emailSender)
    {
        _db = db;
        _config = config;
        _cache = cache;
        _emailSender = emailSender;
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
            return Unauthorized(new { mensaje = "Credenciales inválidas." });
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

        var asunto = "Código de verificación (OTP)";
        var cuerpo = $@"<p>Hola {dto.Nombre},</p>
                        <p>Tu código de verificación es: <b>{otp}</b></p>
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
}