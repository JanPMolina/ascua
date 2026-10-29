using Ascua.Domain.Model;
using NetArchTest.Rules;

namespace Ascua.Architecture.Tests;

/// <summary>El dominio no depende de red, base de datos ni framework (requisito del enunciado).</summary>
public sealed class LayerTests
{
    [Fact]
    public void Domain_has_no_outward_dependencies()
    {
        var result = Types.InAssembly(typeof(Position).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Ascua.Application",
                "Ascua.Infrastructure",
                "Ascua.Api",
                "Microsoft.AspNetCore",
                "Microsoft.EntityFrameworkCore",
                "StackExchange.Redis",
                "Npgsql",
                "System.Net")
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Application_does_not_depend_on_infrastructure_or_api()
    {
        var result = Types.InAssembly(typeof(Ascua.Application.Abstractions.IClock).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Ascua.Infrastructure",
                "Ascua.Api",
                "Microsoft.AspNetCore",
                "Microsoft.EntityFrameworkCore",
                "Npgsql",
                "Konscious",
                "Microsoft.IdentityModel")
            .GetResult();

        Assert.True(result.IsSuccessful, string.Join(", ", result.FailingTypeNames ?? []));
    }
}
