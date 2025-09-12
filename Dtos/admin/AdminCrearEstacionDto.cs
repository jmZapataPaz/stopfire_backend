using System.ComponentModel.DataAnnotations;

namespace stopfire_backend.Dtos.admin;

public class AdminCrearEstacionDto
{
    [Required, MinLength(2)]
    public string Nombre { get; set; } = string.Empty;

    [Required]
    public string Latitud { get; set; } = string.Empty;

    [Required]
    public string Longitud { get; set; } = string.Empty;

    [Required, MinLength(3)]
    public string DescripcionDireccion { get; set; } = string.Empty;

    public string? Celular { get; set; }

    public bool? Estado { get; set; } = true;
}