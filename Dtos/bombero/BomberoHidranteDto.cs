using NetTopologySuite.Geometries;
using System.Text.Json.Serialization;


namespace StopFire.Api.Dtos.bombero

{
    public sealed class BomberoHidranteDto
    {
        public int Id { get; set; }
        public double? Latitud { get; set; }
        public double? Longitud { get; set; }

        public string? GeomWkt { get; set; }          
        public object? GeomGeoJson { get; set; }     
        [JsonIgnore] public Point? GeomInternal { get; set; } 
    }
}