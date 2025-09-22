using System.Text.Json.Serialization;
using NetTopologySuite.Geometries;

namespace StopFire.Api.Models;

public class Estacion
{
    public int Id { get; set; }
    public int IdUsuario { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public string Latitud { get; set; } = string.Empty;
    public string Longitud { get; set; } = string.Empty;
    public string DescripcionDireccion { get; set; } = string.Empty;
    public string? Celular { get; set; }
    public bool Estado { get; set; } = true;

    [JsonIgnore] 
    public Polygon? Cobertura { get; set; }
}