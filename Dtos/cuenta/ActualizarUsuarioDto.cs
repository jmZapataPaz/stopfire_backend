using System.ComponentModel.DataAnnotations;

namespace stopfire_backend.Dtos.cuenta;

public class ActualizarUsuarioDto
{
    [Required, StringLength(100)]
    public string Nombre { get; set; } = string.Empty;

    [Required, StringLength(100)]
    public string Apellido { get; set; } = string.Empty;
    public string Celular { get; set; } = string.Empty; // CAMBIO: reemplaza correo
}