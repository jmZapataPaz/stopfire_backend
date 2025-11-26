using System.ComponentModel.DataAnnotations;

namespace stopfire_backend.Dtos.autenticacion;

public class LoginRequestDto
{
    [Required, EmailAddress]
    public string Correo { get; set; } = string.Empty;
    [Required, MinLength(8)]
    public string Contrasena { get; set; } = string.Empty;

}