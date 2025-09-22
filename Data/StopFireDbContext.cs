using Microsoft.EntityFrameworkCore;
using StopFire.Api.Models;

namespace StopFire.Api.Data;

public class StopFireDbContext : DbContext
{
    public StopFireDbContext(DbContextOptions<StopFireDbContext> options) : base(options) { }

    public DbSet<Usuario> Usuarios => Set<Usuario>();
    public DbSet<Rol> Roles => Set<Rol>();
    public DbSet<Estacion> Estaciones => Set<Estacion>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("public");

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
            b.Property(x => x.Celular).HasColumnName("celular").IsRequired();
            b.Property(x => x.Estado).HasColumnName("estado").IsRequired();
            b.Property(x => x.Cobertura)
                .HasColumnName("cobertura")
                .HasColumnType("geometry(Polygon,4326)")
                .IsRequired(false);
            
        });
    }
}