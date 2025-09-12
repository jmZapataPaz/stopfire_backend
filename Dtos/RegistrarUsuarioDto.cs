using System.ComponentModel.DataAnnotations;

namespace StopFire.Api.Dtos;

public class RegistrarUsuarioDto
{
    [Required, MinLength(2)]
    public string Nombre { get; set; } = string.Empty;

    [Required, MinLength(2)]
    public string Apellido { get; set; } = string.Empty;

    [Required, MinLength(3)]
    public string Ci { get; set; } = string.Empty;

    [Required, EmailAddress]
    public string Correo { get; set; } = string.Empty;
    [Required, MinLength(3)]
    public string Celular { get; set; } = string.Empty;

    [Required, MinLength(8)]
    public string Contrasena { get; set; } = string.Empty;
}