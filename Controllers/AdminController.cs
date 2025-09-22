using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StopFire.Api.Data;
using StopFire.Api.Models;
using stopfire_backend.Dtos.admin;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt; 
using NetTopologySuite.Geometries;
using System.Text.Json;
using NetTopologySuite.IO;
using System.Text.Json.Nodes;

namespace StopFire.Api.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Policy = "AdminOnly")]
public class AdminController : ControllerBase
{
    private readonly StopFireDbContext _db;

    public AdminController(StopFireDbContext db)
    {
        _db = db;
    }

    [HttpPost("usuarios/bombero")]
    public async Task<IActionResult> CrearBombero([FromBody] AdminCrearBomberoDto dto, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var correo = dto.Correo.Trim().ToLowerInvariant();
        var ci = dto.Ci.Trim();

        var existe = await _db.Usuarios.AnyAsync(u => u.Correo.ToLower() == correo || u.Ci == ci, ct);
        if (existe) return Conflict(new { mensaje = "Ya existe un usuario con ese correo o CI." });

        var hash = BCrypt.Net.BCrypt.HashPassword(dto.Contrasena);

        var usuario = new Usuario
        {
            Nombre = dto.Nombre.Trim(),
            Apellido = dto.Apellido.Trim(),
            Ci = ci,
            Correo = correo,
            Celular = dto.Celular.Trim(),
            Contrasena = hash,
            RolId = 2
        };

        _db.Usuarios.Add(usuario);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(ObtenerUsuarioPorId), new { id = usuario.Id }, new
        {
            usuario.Id, usuario.Nombre, usuario.Apellido, usuario.Ci, usuario.Correo, usuario.Celular, usuario.RolId
        });
    }

    [HttpGet("usuarios")]
    public async Task<IActionResult> ListarUsuarios([FromQuery] int? rolId, CancellationToken ct)
    {
        var q = _db.Usuarios.AsNoTracking();
        if (rolId.HasValue) q = q.Where(u => u.RolId == rolId.Value);

        var datos = await q.OrderBy(u => u.Id)
            .Select(u => new { u.Id, u.Nombre, u.Apellido, u.Ci, u.Correo, u.Celular, u.RolId })
            .ToListAsync(ct);

        return Ok(datos);
    }

    [HttpGet("usuarios/bomberos")]
    public async Task<IActionResult> ListarBomberos(CancellationToken ct)
    {
        var datos = await _db.Usuarios
            .AsNoTracking()
            .Where(u => u.RolId == 2)
            .OrderBy(u => u.Id)
            .Select(u => new { u.Id, u.Nombre, u.Apellido, u.Ci, u.Correo, u.Celular, u.RolId })
            .ToListAsync(ct);

        return Ok(datos);
    }

    [HttpGet("usuarios/{id:int}")]
    public async Task<IActionResult> ObtenerUsuarioPorId(int id, CancellationToken ct)
    {
        var u = await _db.Usuarios.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (u is null) return NotFound();
        return Ok(new { u.Id, u.Nombre, u.Apellido, u.Ci, u.Correo, u.Celular, u.RolId });
    }

    [HttpPut("usuarios/bomberos/{id:int}")]
    public async Task<IActionResult> ActualizarBombero(int id, [FromBody] AdminActualizarUsuarioDto dto, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var u = await _db.Usuarios.FirstOrDefaultAsync(x => x.Id == id && x.RolId == 2, ct);
        if (u is null) return NotFound(new { mensaje = "Bombero no encontrado." });
        if (dto.RolId.HasValue && dto.RolId.Value != 2)
            return BadRequest(new { mensaje = "Solo se permite rol_id = 2 (bombero) en este endpoint." });

        u.Nombre = dto.Nombre.Trim();
        u.Apellido = dto.Apellido.Trim();
        u.Celular = dto.Celular.Trim();
        u.RolId = 2; 

        if (!string.IsNullOrWhiteSpace(dto.NuevaContrasena))
            u.Contrasena = BCrypt.Net.BCrypt.HashPassword(dto.NuevaContrasena);

        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("usuarios/bomberos/{id:int}")]
    public async Task<IActionResult> EliminarBombero(int id, CancellationToken ct)
    {
        var u = await _db.Usuarios.FirstOrDefaultAsync(x => x.Id == id && x.RolId == 2, ct);
        if (u is null) return NotFound(new { mensaje = "Bombero no encontrado." });

        _db.Usuarios.Remove(u);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("estaciones")]
    public async Task<IActionResult> CrearEstacion([FromBody] AdminCrearEstacionDto dto, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        var tokenUserIdStr =
            User.FindFirstValue(ClaimTypes.NameIdentifier) ??
            User.FindFirstValue(JwtRegisteredClaimNames.Sub) ??
            User.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(tokenUserIdStr) || !int.TryParse(tokenUserIdStr, out var tokenUserId))
            return Unauthorized(new { mensaje = "No se pudo determinar el usuario desde el token." });
        var userId = dto.IdUsuario ?? tokenUserId;
        var existeUsuario = await _db.Usuarios.AnyAsync(u => u.Id == userId, ct);
        if (!existeUsuario) return BadRequest(new { mensaje = "IdUsuario no existe." });
            var geoJsonText = dto.CoberturaGeoJson.GetRawText();
            var reader = new GeoJsonReader();
            var geom = reader.Read<Geometry>(geoJsonText);
        if (geom is not Polygon poly)
            return BadRequest(new { mensaje = "CoberturaGeoJson debe ser un GeoJSON de tipo Polygon." });
        poly.SRID = 4326;

        var estacion = new Estacion
        {
            IdUsuario = userId,
            Nombre = dto.Nombre.Trim(),
            Latitud = dto.Latitud.Trim(),
            Longitud = dto.Longitud.Trim(),
            DescripcionDireccion = dto.DescripcionDireccion.Trim(),
            Celular = string.IsNullOrWhiteSpace(dto.Celular) ? null : dto.Celular.Trim(),
            Estado = dto.Estado ?? true,
            Cobertura = poly
        };

        _db.Estaciones.Add(estacion);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(ObtenerEstacionPorId), new { id = estacion.Id }, estacion);
    }

    [HttpGet("estaciones")]
    public async Task<IActionResult> ListarEstaciones(CancellationToken ct)
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
    public async Task<IActionResult> ObtenerEstacionPorId(int id, CancellationToken ct)
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

    [HttpPut("estaciones/{id:int}")]
    public async Task<IActionResult> ActualizarEstacion(int id, [FromBody] AdminActualizarEstacionDto dto, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var e = await _db.Estaciones.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return NotFound();

        if (dto.IdUsuario.HasValue)
        {
            var existeUsuario = await _db.Usuarios.AnyAsync(u => u.Id == dto.IdUsuario.Value, ct);
            if (!existeUsuario) return BadRequest(new { mensaje = "IdUsuario no existe." });
            e.IdUsuario = dto.IdUsuario.Value;
        }

        e.Nombre = dto.Nombre.Trim();
        e.Latitud = dto.Latitud.Trim();
        e.Longitud = dto.Longitud.Trim();
        e.DescripcionDireccion = dto.DescripcionDireccion.Trim();
        e.Celular = string.IsNullOrWhiteSpace(dto.Celular) ? null : dto.Celular.Trim();
        e.Estado = dto.Estado;

        if (dto.CoberturaGeoJson.HasValue)
        {
            if (dto.CoberturaGeoJson.Value.ValueKind == JsonValueKind.Null)
            {
                e.Cobertura = null; 
            }
            else if (dto.CoberturaGeoJson.Value.ValueKind != JsonValueKind.Undefined)
            {
                var geoJsonText = dto.CoberturaGeoJson.Value.GetRawText();
                var reader = new GeoJsonReader();
                var geom = reader.Read<Geometry>(geoJsonText);
                if (geom is not Polygon poly)
                    return BadRequest(new { mensaje = "CoberturaGeoJson debe ser un GeoJSON de tipo Polygon." });
                poly.SRID = 4326;
                e.Cobertura = poly;
            }
        }

        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("estaciones/{id:int}")]
    public async Task<IActionResult> EliminarEstacion(int id, CancellationToken ct)
    {
        var e = await _db.Estaciones.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return NotFound();

        _db.Estaciones.Remove(e);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}