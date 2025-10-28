using System.ComponentModel.DataAnnotations;

namespace StopFire.Api.Dtos;

public class ActualizarUsuarioDto
{
    [Required, StringLength(100)]
    public string Nombre { get; set; } = string.Empty;

    [Required, StringLength(100)]
    public string Apellido { get; set; } = string.Empty;

    [Required, EmailAddress, StringLength(200)]
    public string Correo { get; set; } = string.Empty;
}