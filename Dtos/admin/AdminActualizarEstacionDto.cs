using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace stopfire_backend.Dtos.admin;

public class AdminActualizarEstacionDto
{
    [Required, MinLength(2)] public string Nombre { get; set; } = string.Empty;

    [Required] public string Latitud { get; set; } = string.Empty;

    [Required] public string Longitud { get; set; } = string.Empty;

    [Required, MinLength(3)] public string DescripcionDireccion { get; set; } = string.Empty;

    public string? Celular { get; set; }

    [Required] public bool Estado { get; set; }

    public int? IdUsuario { get; set; }

    
    public JsonElement? CoberturaGeoJson { get; set; }
}