using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace LifeOS.UnitTests.App;

public class LaunchBrandingTests
{
    [Fact]
    public void Launcher_UsesDerivedPhotoAndNeutralBackground_InBothConfigurations()
    {
        var project = XDocument.Parse(Source("LifeOS.App.csproj"));
        var icon = Assert.Single(project.Descendants("MauiIcon"));
        Assert.Null(icon.Attribute("Condition"));
        Assert.Equal("Resources\\AppIcon\\appiconfg.png", (string?)icon.Attribute("ForegroundFile"));
        Assert.Equal("0.70", (string?)icon.Attribute("ForegroundScale"));
        Assert.Equal("#F6F7FB", (string?)icon.Attribute("Color"));
        Assert.DoesNotContain("#512BD4", Source("LifeOS.App.csproj"));
        var background = XDocument.Parse(Source("Resources/AppIcon/appicon.svg"));
        var rectangle = Assert.Single(background.Root!.Elements());
        Assert.Equal("rect", rectangle.Name.LocalName);
        Assert.Equal("#F6F7FB", (string?)rectangle.Attribute("fill"));

        var asset = File.ReadAllBytes(AppPath("Resources/AppIcon/appiconfg.png"));
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, asset[..8]);
        Assert.Equal(1024u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(asset.AsSpan(16, 4)));
        Assert.Equal(1024u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(asset.AsSpan(20, 4)));
        Assert.False(File.Exists(AppPath("Resources/AppIcon/appiconfg.svg")));

        // Personal source material must never be a project/runtime reference.
        Assert.DoesNotContain("LifeOS-Secrets", Source("LifeOS.App.csproj"));
        Assert.DoesNotContain("lifeos-icon-source", Source("LifeOS.App.csproj"));
        Assert.Equal(["appicon.svg", "appiconfg.png"], Directory.GetFiles(AppPath("Resources/AppIcon"))
            .Select(path => Path.GetFileName(path)!).OrderBy(name => name).ToArray());
    }

    [Fact]
    public void BootAndRestoring_ShowApprovedCopy_WithoutTimers()
    {
        var host = Source("wwwroot/index.html");
        var gate = Source("Components/Auth/AuthGate.razor");
        var restoring = gate.Split("case AuthState.Restoring:")[1].Split("break;")[0];

        foreach (var markup in new[] { host, restoring })
        {
            Assert.Contains("<h1>LifeOS</h1>", markup);
            Assert.Contains("<p>Your personal life, in one place.</p>", markup);
            Assert.Contains("class=\"lo-boot\"", markup);
        }

        foreach (var source in new[] { host, gate, Source("Services/Auth/AuthService.cs") })
        {
            Assert.DoesNotContain("Task.Delay", source);
            Assert.DoesNotContain("setTimeout", source);
            Assert.DoesNotContain("setInterval", source);
        }
        Assert.Contains("await Auth.RestoreSessionAsync();", gate);
        Assert.Contains("Auth.StateChanged += OnStateChanged;", gate);
        Assert.Contains("Auth.StateChanged -= OnStateChanged;", gate);
        Assert.Contains("case AuthState.SignedOut:", gate);
        Assert.Contains("<SignIn />", gate);
        Assert.Contains("case AuthState.Unreachable:", gate);
        Assert.Contains("role=\"alert\">@Auth.Message", gate);
        Assert.Contains("<OnboardingFlow User=\"Auth.CurrentUser\" />", gate);
        Assert.Contains("@ChildContent", gate);
    }

    [Fact]
    public void NativeSplash_IsPlainNeutral_AndHasNoMaskedTextOrPhoto()
    {
        var project = XDocument.Parse(Source("LifeOS.App.csproj"));
        var splash = Assert.Single(project.Descendants("MauiSplashScreen"));
        Assert.Equal("#F6F7FB", (string?)splash.Attribute("Color"));
        var svg = XDocument.Parse(Source("Resources/Splash/splash.svg"));
        var rectangle = Assert.Single(svg.Root!.Elements());
        Assert.Equal("rect", rectangle.Name.LocalName);
        Assert.Equal("#F6F7FB", (string?)rectangle.Attribute("fill"));
    }

    private static string Source(string relative, [CallerFilePath] string testFile = "") =>
        File.ReadAllText(AppPath(relative, testFile));

    private static string AppPath(string relative, [CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!,
            "..", "..", "..", "..", "src", "dotnet", "LifeOS.App", relative));
}
