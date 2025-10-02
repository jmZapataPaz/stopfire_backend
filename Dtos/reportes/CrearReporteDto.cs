using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace StopFire.Api.Dtos.Reportes;

public class CrearReporteDto
{
    [MinLength(2, ErrorMessage = "La descripción debe tener al menos 2 caracteres si se envía.")]
    public string? Descripcion { get; set; }
    [Required]
    public string Latitud { get; set; } = string.Empty;
    [Required]
    public string Longitud { get; set; } = string.Empty;
    [Required(ErrorMessage = "La foto es obligatoria.")]
    public IFormFile Foto { get; set; } = default!;
    public string? ImagenUrl { get; internal set; }
}