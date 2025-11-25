using NetTopologySuite.Geometries;
using System.Text.Json.Serialization;

namespace StopFire.Api.Models
{
    public class Hidrante
    {
        public int Id { get; set; }
        public double? Latitud { get; set; }
        public double? Longitud { get; set; }
        public Point? Geom { get; set; }
    }
}