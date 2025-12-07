using Microsoft.EntityFrameworkCore;
using StopFire.Api.Models;
using NetTopologySuite.Geometries;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Globalization;

namespace StopFire.Api.Data;

public class StopFireDbContext : DbContext
{
    public StopFireDbContext(DbContextOptions<StopFireDbContext> options) : base(options) { }

    public DbSet<Usuario> Usuarios => Set<Usuario>();
    public DbSet<Rol> Roles => Set<Rol>();
    public DbSet<Estacion> Estaciones => Set<Estacion>();
    public DbSet<Reporte> Reportes => Set<Reporte>();
    public DbSet<Asignacion> Asignaciones => Set<Asignacion>();
    public DbSet<Hidrante> Hidrantes => Set<Hidrante>();
    public DbSet<RegistroCapitanEstacion> RegistrosCapitanes { get; set; }
    public DbSet<ConfirmacionReporte> ConfirmacionesReporte => Set<ConfirmacionReporte>();

    private static double? ParseNullableDouble(string? v)
        => string.IsNullOrWhiteSpace(v) ? null :
           (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("public");

        var doubleStringConverter = new ValueConverter<double?, string>(
            v => v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture) : null,
            v => ParseNullableDouble(v)
        );

        // Asegurar UTC y tipo correcto para Reporte.FechaCreacion
        var utcConverter = new ValueConverter<DateTime, DateTime>(
            toDb => toDb.Kind == DateTimeKind.Utc ? toDb : toDb.ToUniversalTime(),
            fromDb => DateTime.SpecifyKind(fromDb, DateTimeKind.Utc)
        );

        // Converter nullable para columnas timestamp nullable en BD
        var utcNullableConverter = new ValueConverter<DateTime?, DateTime?>(
            toDb => toDb.HasValue ? (toDb.Value.Kind == DateTimeKind.Utc ? toDb.Value : toDb.Value.ToUniversalTime()) : (DateTime?)null,
            fromDb => fromDb.HasValue ? DateTime.SpecifyKind(fromDb.Value, DateTimeKind.Utc) : (DateTime?)null
        );

        // Converter para columna 'respuesta' que actualmente es varchar en la BD
        var dateStringConverter = new ValueConverter<DateTime?, string?>(
            v => v.HasValue ? v.Value.ToString("o", CultureInfo.InvariantCulture) : null,
            v => string.IsNullOrWhiteSpace(v) ? (DateTime?)null
                 : DateTime.SpecifyKind(DateTime.Parse(v, null, DateTimeStyles.RoundtripKind), DateTimeKind.Utc)
        );

        modelBuilder.Entity<Rol>(b =>
        {
            b.ToTable("rol");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            b.Property(x => x.Descripcion).HasColumnName("descripcion").IsRequired();
        });

        modelBuilder.Entity<Usuario>(b =>
        {
            b.ToTable("usuario");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            b.Property(x => x.Nombre).HasColumnName("nombre").IsRequired();
            b.Property(x => x.Apellido).HasColumnName("apellido").IsRequired();
            b.Property(x => x.Ci).HasColumnName("ci").IsRequired();
            b.Property(x => x.Correo).HasColumnName("correo").IsRequired();
            b.Property(x => x.Celular).HasColumnName("celular").IsRequired();
            b.Property(x => x.Contrasena).HasColumnName("contraseña").IsRequired();
            b.Property(x => x.RolId).HasColumnName("rol_id").HasDefaultValue(3);
            b.Property(x => x.Estado).HasColumnName("estado").HasDefaultValue(true);
            b.HasOne(x => x.Rol)
                .WithMany(r => r.Usuarios)
                .HasForeignKey(x => x.RolId);
        });

        modelBuilder.Entity<Estacion>(b =>
        {
            b.ToTable("estacion");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            b.Property(x => x.IdUsuario).HasColumnName("id_usuario").IsRequired();
            b.Property(x => x.Nombre).HasColumnName("nombre").IsRequired();
            b.Property(x => x.Latitud).HasColumnName("latitud").HasMaxLength(255).IsRequired();
            b.Property(x => x.Longitud).HasColumnName("longitud").HasMaxLength(255).IsRequired();
            b.Property(x => x.DescripcionDireccion).HasColumnName("descripcion_direccion").IsRequired();
            b.Property(x => x.Celular).HasColumnName("celular").IsRequired(false);
            b.Property(x => x.Estado).HasColumnName("estado").IsRequired();
            b.Property(x => x.Cobertura)
                .HasColumnName("cobertura")
                .HasColumnType("geometry(Polygon,4326)")
                .IsRequired(false);
        });

        modelBuilder.Entity<Reporte>(b =>
        {
            b.ToTable("reporte");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            b.Property(x => x.IdUsuario).HasColumnName("id_usuario").IsRequired();
            b.Property(x => x.Descripcion).HasColumnName("descripcion").IsRequired();
            b.Property(x => x.FotoUrl).HasColumnName("foto_url").IsRequired(false);
            b.Property(x => x.Estado).HasColumnName("estado").IsRequired().HasDefaultValue("PENDIENTE");
            b.Property(x => x.FechaCreacion).HasColumnName("fecha_creacion").HasDefaultValueSql("now()");
            // Conversión para columnas existentes tipo varchar
            b.Property(x => x.Latitud)
                .HasColumnName("latitud")
                .HasConversion(doubleStringConverter)
                .HasMaxLength(50)
                .IsRequired();
            b.Property(x => x.Longitud)
                .HasColumnName("longitud")
                .HasConversion(doubleStringConverter)
                .HasMaxLength(50)
                .IsRequired();

            b.Property(x => x.Confirmaciones)
                .HasColumnName("Confirmaciones")
                .IsRequired(false)
                .HasDefaultValue(0);

            b.Property(x => x.Direccion).HasColumnName("direccion").IsRequired(false); // NUEVO

            b.HasOne(x => x.Usuario)
                .WithMany()
                .HasForeignKey(x => x.IdUsuario)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Asignacion>(b =>
        {
            b.ToTable("asignacion");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            b.Property(x => x.IdReporte).HasColumnName("id_reporte").IsRequired();
            b.Property(x => x.IdEstacion).HasColumnName("id_estacion").IsRequired();
            // Respuesta ahora es timestamp with time zone en la BD -> mapear como fecha nullable con conversión a UTC
            b.Property(x => x.RespuestaUtc)
                .HasColumnName("respuesta")
                .HasConversion(utcNullableConverter)
                .HasColumnType("timestamp with time zone")
                .IsRequired(false);
            b.Property(x => x.CronometroMinutos).HasColumnName("cronometro").HasDefaultValue(2);
            b.HasOne(x => x.Reporte).WithMany().HasForeignKey(x => x.IdReporte);
            b.HasOne(x => x.Estacion).WithMany().HasForeignKey(x => x.IdEstacion);
        });

        modelBuilder.Entity<Hidrante>(b =>
        {
            b.ToTable("hidrantes");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            b.Property(x => x.Latitud)
                .HasColumnName("latitud")
                .HasConversion(doubleStringConverter)
                .HasMaxLength(50)
                .IsRequired(false);
            b.Property(x => x.Longitud)
                .HasColumnName("longitud")
                .HasConversion(doubleStringConverter)
                .HasMaxLength(50)
                .IsRequired(false);
            b.Property(x => x.Geom)
                .HasColumnName("geom")
                .HasColumnType("geometry(Point,4326)")
                .IsRequired(false);
            b.Property(x => x.Descripcion).HasColumnName("descripcion").IsRequired(false);

            // Asegurar NOT NULL + default true
            b.Property(x => x.Estado)
                .HasColumnName("estado")
                .IsRequired()
                .HasDefaultValue(true);
        });

        modelBuilder.Entity<Reporte>(e =>
        {
            e.Property(r => r.FechaCreacion)
             .HasConversion(utcConverter)
             .HasColumnType("timestamp with time zone"); 
        });

        modelBuilder.Entity<ConfirmacionReporte>(b =>
        {
            b.ToTable("confirmacion_reporte");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            b.Property(x => x.IdReporte).HasColumnName("id_reporte").IsRequired();
            b.Property(x => x.IdUsuario).HasColumnName("id_usuario").IsRequired();
            b.Property(x => x.FechaConfirmacion).HasColumnName("fecha_confirmacion").IsRequired();
            
            b.HasOne(x => x.Reporte)
                .WithMany()
                .HasForeignKey(x => x.IdReporte)
                .OnDelete(DeleteBehavior.Cascade);
            
            b.HasOne(x => x.Usuario)
                .WithMany()
                .HasForeignKey(x => x.IdUsuario)
                .OnDelete(DeleteBehavior.Cascade);
            
            // Índice único: un usuario solo puede confirmar un reporte una vez
            b.HasIndex(x => new { x.IdReporte, x.IdUsuario }).IsUnique();
        });
    }
}