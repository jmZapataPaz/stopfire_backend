using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;

namespace StopFire.Api.Dtos.Reportes;

public class CrearReporteDto
{
    [MinLength(2, ErrorMessage = "La descripci�n debe tener al menos 2 caracteres si se env�a.")]
    public string? Descripcion { get; set; }
    [Required]
    public string Latitud { get; set; } = string.Empty;
    [Required]
    public string Longitud { get; set; } = string.Empty;
    [Required(ErrorMessage = "La foto es obligatoria.")]
    public IFormFile Foto { get; set; } = default!;
    public string? ImagenUrl { get; internal set; }
    public string? Direccion{get;set;}
}