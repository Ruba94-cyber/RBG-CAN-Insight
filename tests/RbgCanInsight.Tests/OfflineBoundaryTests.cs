using Xunit;

namespace RbgCanInsight.Tests;

public sealed class OfflineBoundaryTests
{
    [Fact]
    public void ProductionSourceContainsNoNetworkSerialOrVehicleAdapterIntegration()
    {
        var root = FindRepositoryRoot();
        var forbidden = new[]
        {
            "System.Net.Http", "System.Net.Sockets", "System.IO.Ports", "HttpClient(", "TcpClient(",
            "UdpClient(", "new Socket(", "Vector.XL", "Peak.Can", "Kvaser", "J2534"
        };
        var sourceFiles = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

        var violations = sourceFiles
            .SelectMany(path => forbidden.Where(token => File.ReadAllText(path).Contains(token, StringComparison.Ordinal))
                .Select(token => $"{Path.GetRelativePath(root, path)}: {token}"))
            .ToArray();

        Assert.True(violations.Length == 0,
            "The offline-only production boundary was crossed:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void StoreManifestTemplateDeclaresNoNetworkOrDeviceCapabilities()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "tools", "make-msix.ps1"));
        var forbiddenCapabilities = new[]
        {
            "internetClient", "privateNetworkClientServer", "proximity", "serialcommunication",
            "bluetooth", "humaninterfacedevice", "removableStorage"
        };

        Assert.All(forbiddenCapabilities, capability =>
            Assert.DoesNotContain($"Name=\"{capability}\"", script, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("<rescap:Capability Name=\"runFullTrust\" />", script, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RbgCanInsight.sln"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate RbgCanInsight.sln from the test output directory.");
    }
}
