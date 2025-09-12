using System.ComponentModel.DataAnnotations;

namespace stopfire_backend.Dtos.admin;

public class AdminActualizarUsuarioDto
{
    [Required, MinLength(2)] public string Nombre { get; set; } = string.Empty;
    [Required, MinLength(2)] public string Apellido { get; set; } = string.Empty;
    [Required, MinLength(3)] public string Celular { get; set; } = string.Empty;
    public string? NuevaContrasena { get; set; }
    public int? RolId { get; set; }
}