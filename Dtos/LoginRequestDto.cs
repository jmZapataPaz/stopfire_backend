using System.ComponentModel.DataAnnotations;

namespace StopFire.Api.Dtos;

public class LoginRequestDto
{
    [Required, EmailAddress]
    public string Correo { get; set; } = string.Empty;

    [Required, MinLength(8)]
    public string Contrasena { get; set; } = string.Empty;
}