using System.ComponentModel.DataAnnotations;

namespace StopFire.Api.Dtos;

public class VerificarOtpDto
{
    [Required, EmailAddress]
    public string Correo { get; set; } = string.Empty;

    [Required, MinLength(4), MaxLength(8)]
    public string Codigo { get; set; } = string.Empty;
}