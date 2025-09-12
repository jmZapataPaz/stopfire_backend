using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StopFire.Api.Data;
using StopFire.Api.Dtos;
using StopFire.Api.Models;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using System.Text;

namespace StopFire.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class UsuariosController : ControllerBase
{
    private readonly StopFireDbContext _db;
    private readonly IConfiguration _config;

    public UsuariosController(StopFireDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    [HttpPost("registrar")]
    public async Task<IActionResult> Registrar([FromBody] RegistrarUsuarioDto dto, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var correo = dto.Correo.Trim().ToLowerInvariant();
        var ci = dto.Ci.Trim();
        var existe = await _db.Usuarios
            .AnyAsync(u => u.Correo.ToLower() == correo || u.Ci == ci, ct);

        if (existe)
            return Conflict(new { mensaje = "Ya existe un usuario con ese correo o CI." });

        var hash = BCrypt.Net.BCrypt.HashPassword(dto.Contrasena);
        var usuario = new Usuario
        {
            Nombre = dto.Nombre.Trim(),
            Apellido = dto.Apellido.Trim(),
            Ci = ci,
            Correo = correo,
            Celular = string.IsNullOrWhiteSpace(dto.Celular) ? null : dto.Celular.Trim(),
            Contrasena = hash,
            RolId = 3
        };

        _db.Usuarios.Add(usuario);
        await _db.SaveChangesAsync(ct);
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
}