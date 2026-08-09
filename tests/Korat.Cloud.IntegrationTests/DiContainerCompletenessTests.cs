using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Korat.Cloud.IntegrationTests;

/// <summary>
/// Р35: the DI container must contain everything the code actually asks it for.
///
/// <para><b>Why this exists.</b> On 2026-08-03 a refactor deleted nine service registrations from
/// Program.cs that belonged to surfaces we had decided to keep. Nothing caught it: the classes
/// still existed, so the compiler was happy and <c>dotnet build</c> reported zero errors. The
/// failure only appeared at host start — as 473 failing tests with one shared cause.</para>
///
/// <para><b>Why it is written this way.</b> The obvious test — "assert these 40 service types are
/// registered" — would not have caught it. Deleting a registration and its line from such a list
/// leaves the test green: a hand-maintained list detects <i>change</i>, never <i>incompleteness</i>.
/// So the required set is re-derived from the source on every run: every type argument of a
/// <c>GetRequiredService&lt;T&gt;()</c> call in the Cloud app is a service the code will demand at
/// runtime, and must therefore resolve. Add a new call and this test covers it with no edit here;
/// delete a registration and it fails whether or not anyone remembers this file.</para>
///
/// <para><b>What it does not cover</b> — stated because the check passing does not mean the
/// container is whole:</para>
/// <list type="bullet">
///   <item>Services obtained by non-generic <c>GetRequiredService(Type)</c> or by
///     <c>IServiceProvider.GetService</c> with a runtime type. The scan is syntactic.</item>
///   <item>Minimal-API handler parameters. An unregistered handler parameter is not an error at
///     all: RequestDelegateFactory silently reclassifies it as a body/route binding, so it fails
///     as a 400 at request time rather than as a resolution failure. Those are covered by the
///     contract/integration suites exercising the endpoints themselves.</item>
///   <item>Registrations that exist but are wired to the wrong lifetime or implementation.</item>
///   <item>Anything in Korat.Grains, the CLI, or the mobile clients — this looks at the Cloud
///     host only.</item>
/// </list>
/// </summary>
public sealed class DiContainerCompletenessTests(KoratIntegrationFixture fixture)
    : IClassFixture<KoratIntegrationFixture>
{
    /// <summary>
    /// Type arguments of GetRequiredService&lt;T&gt; / GetRequiredKeyedService&lt;T&gt;. Captures
    /// the inner text so both <c>Foo</c> and <c>Some.Namespace.Foo</c> are picked up; generic
    /// arguments containing a comma (e.g. IOptions&lt;A, B&gt;) are not expected and are skipped
    /// by the resolution step rather than by the pattern.
    /// </summary>
    private static readonly Regex GetRequiredServiceCall = new(
        @"GetRequiredService<\s*([A-Za-z0-9_.<>]+?)\s*>\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    /// Framework and BCL types are resolved by ASP.NET Core itself; a missing one would be a bug
    /// in the framework, not in our composition root. Skipping them keeps failures actionable.
    /// </summary>
    private static bool IsOurType(Type t) =>
        t.Assembly.GetName().Name?.StartsWith("Korat.", StringComparison.Ordinal) == true;

    [Fact]
    public void EveryServiceTheCodeAsksForIsRegistered()
    {
        var cloudRoot = Path.Combine(FindRepoRoot(), "apps", "Korat.Cloud");
        Assert.True(Directory.Exists(cloudRoot), $"Cloud source not found at {cloudRoot}");

        // ── Derive the required set from source, not from a list in this file ──
        var requestedNames = new HashSet<string>(StringComparer.Ordinal);
        var files = Directory.EnumerateFiles(cloudRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(files); // a silent zero-file scan would make this test vacuous

        foreach (var file in files)
            foreach (Match m in GetRequiredServiceCall.Matches(File.ReadAllText(file)))
                requestedNames.Add(m.Groups[1].Value);

        Assert.NotEmpty(requestedNames); // likewise: no matches ⇒ the pattern rotted, not "all clear"

        // ── Map the captured names onto real types in the loaded Korat assemblies ──
        var koratTypes = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("Korat.", StringComparison.Ordinal) == true)
            .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
            })
            .Where(t => t is not null)
            .ToList()!;

        var resolved = new List<Type>();
        foreach (var name in requestedNames)
        {
            if (name.Contains('<', StringComparison.Ordinal))
                continue; // open/closed generics (IOptions<T>, ILogger<T>) — framework-owned

            var simpleName = name.Contains('.', StringComparison.Ordinal)
                ? name[(name.LastIndexOf('.') + 1)..]
                : name;

            var matches = koratTypes.Where(t => t!.Name == simpleName).ToList();
            if (matches.Count == 1 && IsOurType(matches[0]!))
                resolved.Add(matches[0]!);
            // Ambiguous or framework-owned names are skipped: this test is a floor, not a ceiling,
            // and a false failure on a BCL name would train people to ignore it.
        }

        Assert.NotEmpty(resolved);

        // ── Assert every one of them actually resolves out of the running host ──
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        var missing = new List<string>();
        foreach (var type in resolved.DistinctBy(t => t.FullName))
        {
            try
            {
                if (sp.GetService(type) is null)
                    missing.Add(type.FullName!);
            }
            catch (Exception ex)
            {
                missing.Add($"{type.FullName} ({ex.GetType().Name}: {ex.Message})");
            }
        }

        Assert.True(
            missing.Count == 0,
            "Code calls GetRequiredService<T> for types the container cannot provide. The host will "
            + "throw on the first request (or at startup, if the call is on the boot path):"
            + Environment.NewLine + string.Join(Environment.NewLine, missing.Order()));
    }

    /// <summary>
    /// The composition root must be internally consistent: every registered implementation's own
    /// constructor dependencies must themselves be registered. This is the framework's own
    /// <c>ValidateOnBuild</c> check, run explicitly here so the guarantee is asserted rather than
    /// left to whether the host happens to run in the Development environment.
    ///
    /// Complements the scan above: that one covers what the code pulls out of the container by
    /// hand, this one covers what the container has to build for itself.
    /// </summary>
    [Fact]
    public void EveryRegisteredServiceCanBeConstructed()
    {
        // The descriptor list is captured from the real composition root rather than listed here,
        // for the same reason as above: a list would assert stability, not completeness.
        List<ServiceDescriptor>? descriptors = null;
        using var factory = fixture.Factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(services => descriptors = services.ToList()));

        // Force the host to build so ConfigureTestServices has run.
        using var scope = factory.Services.CreateScope();
        Assert.NotNull(descriptors);
        Assert.NotEmpty(descriptors!);

        var ourServiceTypes = descriptors!
            .Select(d => d.ServiceType)
            .Where(t => IsOurType(t) && !t.IsGenericTypeDefinition)
            .DistinctBy(t => t.FullName)
            .ToList();

        Assert.NotEmpty(ourServiceTypes);

        var failures = new List<string>();
        foreach (var serviceType in ourServiceTypes)
        {
            try
            {
                _ = scope.ServiceProvider.GetService(serviceType);
            }
            catch (Exception ex)
            {
                // Unwrap: DI wraps the real cause (usually "Unable to resolve service for type X").
                var root = ex;
                while (root.InnerException is not null) root = root.InnerException;
                failures.Add($"{serviceType.FullName} — {root.GetType().Name}: {root.Message}");
            }
        }

        Assert.True(
            failures.Count == 0,
            "Registered services that cannot be constructed (a dependency of theirs is missing):"
            + Environment.NewLine + string.Join(Environment.NewLine, failures.Order()));
    }

    /// <summary>
    /// The container-based checks above have a blind spot that a mutation test exposed: the test
    /// host substitutes some production services (<c>KoratTestHost</c> does
    /// <c>services.RemoveAll&lt;IOutboundHttpClientFactory&gt;()</c> and re-registers its own), so
    /// deleting the production registration of such a service leaves every container assertion
    /// green. Verified by mutation on 2026-08-03: removing the IOutboundHttpClientFactory
    /// registration from Program.cs was NOT caught until this test existed.
    ///
    /// This check therefore never touches the container. Both sides come from source: the demanded
    /// set from <c>GetRequiredService&lt;T&gt;</c> call sites, the supplied set from
    /// <c>Add{Singleton,Scoped,Transient}</c> mentions anywhere under apps/Korat.Cloud. It is
    /// deliberately textual — it answers "did anyone delete the line", which is exactly the
    /// failure mode a substituted service hides from the runtime.
    ///
    /// <b>What it does not prove:</b> a mention is not a registration. A type named in a comment,
    /// in dead code, or registered under a condition that is false in production would satisfy it.
    /// It is a floor under the container checks, not a replacement for them.
    /// </summary>
    [Fact]
    public void EveryServiceTheCodeAsksForIsRegisteredInSource()
    {
        var cloudRoot = Path.Combine(FindRepoRoot(), "apps", "Korat.Cloud");
        var sources = Directory.EnumerateFiles(cloudRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .ToList();

        Assert.NotEmpty(sources);

        var demanded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in sources)
            foreach (Match m in GetRequiredServiceCall.Matches(text))
            {
                var name = m.Groups[1].Value;
                if (name.Contains('<', StringComparison.Ordinal))
                    continue;
                demanded.Add(name.Contains('.', StringComparison.Ordinal)
                    ? name[(name.LastIndexOf('.') + 1)..]
                    : name);
            }

        Assert.NotEmpty(demanded);

        // Only our own types: framework services (ILoggerFactory, IConfiguration, IAntiforgery,
        // IOpenIddict*) are registered by the framework's own Add* extensions and would produce
        // noise, not findings.
        var ourTypeNames = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("Korat.", StringComparison.Ordinal) == true)
            .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
            })
            .Select(t => t!.Name)
            .ToHashSet(StringComparer.Ordinal);

        var registration = new Regex(
            @"Add(Singleton|Scoped|Transient|HostedService)\b[^;]*",
            RegexOptions.Compiled | RegexOptions.Singleline);

        var registrationText = string.Join('\n',
            sources.SelectMany(text => registration.Matches(text).Select(m => m.Value)));

        var unregistered = demanded
            .Where(ourTypeNames.Contains)
            .Where(name => !Regex.IsMatch(registrationText, $@"\b{Regex.Escape(name)}\b"))
            .Order()
            .ToList();

        Assert.True(
            unregistered.Count == 0,
            "Code calls GetRequiredService<T> for types nothing under apps/Korat.Cloud registers. "
            + "The container checks in this file can miss exactly this when the test host "
            + "substitutes its own implementation:"
            + Environment.NewLine + string.Join(Environment.NewLine, unregistered));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Korat.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Korat.slnx not found above test bin dir.");
    }
}
